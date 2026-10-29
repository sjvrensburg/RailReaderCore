using System.Security.Cryptography;

namespace RailReader.Core.Services;

/// <summary>
/// Content-based document identity. A PDF's id is the SHA-256 of its bytes the first time it is seen,
/// so a moved, renamed or copied file keeps the id its sidecar data was saved under. Hashing a large PDF
/// is not free, so each path's (size, mtime) → id is cached in <c>ConfigDir/document_ids.json</c> and an
/// unchanged file is never re-read.
///
/// A file whose bytes change in place (annotations written back into the PDF) hashes to something new;
/// it keeps its id because the path was already known, and the new hash is recorded as an alias of that
/// id, so a later copy of the rewritten file is still recognised. Two unrelated files never share an id:
/// an unknown hash at an unknown path always starts a fresh id.
///
/// Paths are keyed by their canonical spelling (casing, 8.3 names, junctions, <c>subst</c> and mapped
/// drives on Windows; symlinks elsewhere), so a file reached another way still finds its entry — even
/// after its bytes changed outside the app, when the content alias can't recognise it.
///
/// Every host sharing the config directory must key <see cref="AnnotationService.DocumentKeyResolver"/>
/// the same way to find the same sidecar. Only the app that owns the map calls <see cref="Resolve"/>
/// (it mutates and saves the map, UI-thread); any other host uses <see cref="ReadOnlyResolver"/>, and
/// if it outlives a single command, calls <see cref="Refresh"/> to see documents the app opened since.
/// </summary>
public static class DocumentIdentity
{
    private const int MaxPathsPerId = 8;

    public static string MapPath => s_mapPathOverride ?? Path.Combine(AppConfig.ConfigDir, "document_ids.json");

    private static string? s_mapPathOverride;
    private static IdentityMap? s_map;
    private static bool s_owner;   // this process has called Resolve, so its map may hold unsaved entries
    private static bool s_loadFailed;   // the map file exists but could not be read: keep it before overwriting
    // Lookup's last hash of each path the map doesn't know (one entry per path, so a file rewritten many
    // times doesn't grow it), so a read-only host's resolver (called on every sidecar Load/Save/Delete)
    // doesn't re-read a large PDF each time.
    private static readonly Dictionary<string, (long Size, long Mtime, string Hash)> s_lookupHashes = [];
    // Ids Lookup found by content for an unknown path. Kept for the process even if the bytes then change
    // (e.g. annotations written into the PDF between a Load and a Save), so one session's calls all key
    // the same sidecar rather than falling back to the path hash half-way through.
    private static readonly Dictionary<string, string> s_lookupIds = [];
    private static readonly Lock s_lock = new();

    /// <summary>
    /// <see cref="Lookup"/> as an <see cref="AnnotationService.DocumentKeyResolver"/>: the resolver for
    /// any host other than the one that owns the map, since it never writes it.
    /// </summary>
    public static readonly Func<string, string?> ReadOnlyResolver = Lookup;

    /// <summary>The document's id, hashing the file only when its size or modification time changed.
    /// Records the result in the map. Blocking file IO — call from a background thread for a large PDF.
    /// Only for the host that owns the map; others use <see cref="Lookup"/>.</summary>
    public static string Resolve(string pdfPath)
    {
        var (key, full) = Keys(pdfPath);
        var info = new FileInfo(key);
        long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;

        lock (s_lock)
        {
            var map = Map();
            if (Find(map, key, full, out bool canonical) is { } known && known.Size == size && known.MtimeTicks == mtime)
            {
                if (canonical) return known.Id;
                s_owner = true;   // entry written under another spelling: move it to the canonical one
                Record(map, key, full, known.Id, size, mtime);
                SaveMap(map);
                return known.Id;
            }
        }

        string hash = HashFile(key);   // outside the lock: this is the slow part

        lock (s_lock)
        {
            s_owner = true;
            var map = Map();
            var known = Find(map, key, full, out _);
            string id = map.Aliases.TryGetValue(hash, out var aliased) ? aliased : known?.Id ?? hash;
            if (known is not null && known.Id != id && map.Paths.TryGetValue(known.Id, out var oldPaths))
            {
                oldPaths.Remove(key);   // this path now holds a different document
                oldPaths.Remove(full);
            }
            map.Aliases[hash] = id;
            Record(map, key, full, id, size, mtime);
            SaveMap(map);
            return id;
        }
    }

    /// <summary>The id last resolved for this path, without hashing it (null when never resolved).
    /// Safe after the file has changed or gone. Touches the file system only to find the path's
    /// canonical spelling.</summary>
    public static string? Peek(string pdfPath)
    {
        var (key, full) = Keys(pdfPath);
        lock (s_lock)
            return Find(Map(), key, full, out _)?.Id;
    }

    /// <summary>Read-only <see cref="Resolve"/>: the id last resolved for this path, else the id an
    /// earlier open recorded for these exact bytes (a moved, renamed or copied file), else null. Hashes
    /// the file only when the path is unknown, and never writes the map — for a host that may run while
    /// the app holds the map in memory.</summary>
    public static string? Lookup(string pdfPath)
    {
        var (key, full) = Keys(pdfPath);
        lock (s_lock)
            if (Find(Map(), key, full, out _) is { } known)
                return known.Id;

        if (!File.Exists(key)) return null;
        var info = new FileInfo(key);
        long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;
        string? hash = null;
        lock (s_lock)
        {
            if (s_lookupIds.TryGetValue(key, out var seen)) return seen;
            if (s_lookupHashes.TryGetValue(key, out var cached) && cached.Size == size && cached.Mtime == mtime)
                hash = cached.Hash;
        }
        if (hash is null)
        {
            hash = HashFile(key);
            lock (s_lock)
                s_lookupHashes[key] = (size, mtime, hash);
        }
        lock (s_lock)
        {
            if (!Map().Aliases.TryGetValue(hash, out var aliased)) return null;
            s_lookupIds[key] = aliased;
            return aliased;
        }
    }

    /// <summary>Other paths this document has been opened from, most recent first.</summary>
    public static IReadOnlyList<string> KnownPaths(string id)
    {
        lock (s_lock)
            return Map().Paths.TryGetValue(id, out var p) ? [.. p] : [];
    }

    /// <summary>
    /// Re-reads the map from disk, for a long-lived read-only host: the map is otherwise read once per
    /// process, so documents the app opens later stay unknown to <see cref="Lookup"/>. Returns false and
    /// keeps the current map when the file can't be read — an empty map would send
    /// <see cref="AnnotationService"/> back to the path-hash sidecar. A missing file reads as empty.
    /// Throws in a process that has called <see cref="Resolve"/>: its map is the newer copy (a failed
    /// save leaves entries only in memory), and reloading would lose them.
    /// </summary>
    public static bool Refresh()
    {
        lock (s_lock)
            if (s_owner)
                throw new InvalidOperationException("DocumentIdentity.Refresh is for read-only hosts; this process owns the map (it called Resolve).");

        if (!JsonSidecar.TryLoad(MapPath, RailReaderJsonContext.Default.IdentityMap, static () => new IdentityMap(), out var fresh))
        {
            RailReaderLogging.Logger.Warn("DocumentIdentity.Refresh: keeping the loaded map");
            return false;
        }

        lock (s_lock)
        {
            if (s_owner) return false;   // Resolve ran while we were reading
            s_map = fresh;
            s_loadFailed = false;
        }
        return true;
    }

    /// <summary>Test hook: redirects <see cref="MapPath"/> (null restores the default) and drops the
    /// in-memory map, so the next call reads it from disk — calling it again with the same path
    /// simulates a restart. Also forgets that this process called <see cref="Resolve"/>.</summary>
    public static void OverrideMapPathForTesting(string? path)
    {
        lock (s_lock)
        {
            s_mapPathOverride = path;
            s_map = null;
            s_owner = false;
            s_loadFailed = false;
            s_lookupHashes.Clear();
            s_lookupIds.Clear();
        }
    }

    // The map key for a path is its canonical spelling (CanonicalPath), so one file has one entry however
    // it is reached; a file that is gone or can't be resolved falls back to its full path. Full is the
    // spelling entries were keyed by before canonicalisation, still consulted so they keep their ids.
    private static (string Key, string Full) Keys(string pdfPath)
    {
        string full = Path.GetFullPath(pdfPath);
        return (CanonicalPath.Of(full) ?? full, full);
    }

    // Callers hold s_lock.
    private static FileEntry? Find(IdentityMap map, string key, string full, out bool canonical)
    {
        canonical = map.Files.TryGetValue(key, out var entry);
        if (canonical || key == full) return entry;
        return map.Files.TryGetValue(full, out entry) ? entry : null;
    }

    // Callers hold s_lock. Records the path under its canonical key, dropping the old spelling's entry.
    private static void Record(IdentityMap map, string key, string full, string id, long size, long mtime)
    {
        if (key != full) map.Files.Remove(full);
        map.Files[key] = new FileEntry { Size = size, MtimeTicks = mtime, Id = id };
        if (!map.Paths.TryGetValue(id, out var paths)) map.Paths[id] = paths = [];
        paths.Remove(key);
        paths.Remove(full);
        paths.Insert(0, key);
        if (paths.Count > MaxPathsPerId) paths.RemoveRange(MaxPathsPerId, paths.Count - MaxPathsPerId);
    }

    // Callers hold s_lock.
    private static void SaveMap(IdentityMap map)
    {
        if (!PreserveUnreadableMap()) return;   // retried on the next save; never overwrite the only copy
        JsonSidecar.Save(MapPath, map, RailReaderJsonContext.Default.IdentityMap);
    }

    // Callers hold s_lock.
    private static IdentityMap Map()
    {
        if (s_map is null)
        {
            s_loadFailed = !JsonSidecar.TryLoad(MapPath, RailReaderJsonContext.Default.IdentityMap,
                static () => new IdentityMap(), out var loaded);
            s_map = loaded;
        }
        return s_map;
    }

    // Callers hold s_lock. The first save after an unreadable map would replace every recorded id with
    // this process's (empty) view of it, orphaning every id-keyed sidecar; keep the old file for recovery.
    // Returns false when the copy failed, so the caller must not overwrite the original.
    private static bool PreserveUnreadableMap()
    {
        if (!s_loadFailed) return true;
        try
        {
            if (File.Exists(MapPath)) File.Copy(MapPath, MapPath + ".unreadable", overwrite: true);
            s_loadFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to preserve the unreadable document_ids.json; not overwriting it", ex);
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>A path's last-seen (size, mtime) and the id it resolved to.</summary>
public sealed class FileEntry
{
    public long Size { get; set; }
    public long MtimeTicks { get; set; }
    public string Id { get; set; } = "";
}

/// <summary>On-disk shape of <c>document_ids.json</c> (snake_case, version 1).</summary>
public sealed class IdentityMap
{
    public int Version { get; set; } = 1;
    public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Paths { get; set; } = new(StringComparer.Ordinal);
}
