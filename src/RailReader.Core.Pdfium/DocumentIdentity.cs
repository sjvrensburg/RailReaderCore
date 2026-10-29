using System.Security.Cryptography;
using System.Text.Json;

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
        string full = Path.GetFullPath(pdfPath);
        var info = new FileInfo(full);
        long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;

        lock (s_lock)
        {
            var map = Map();
            if (map.Files.TryGetValue(full, out var known) && known.Size == size && known.MtimeTicks == mtime)
                return known.Id;
        }

        string hash = HashFile(full);   // outside the lock: this is the slow part

        lock (s_lock)
        {
            s_owner = true;
            var map = Map();
            map.Files.TryGetValue(full, out var known);
            string id = map.Aliases.TryGetValue(hash, out var aliased) ? aliased : known?.Id ?? hash;
            map.Aliases[hash] = id;
            map.Files[full] = new FileEntry { Size = size, MtimeTicks = mtime, Id = id };
            if (!map.Paths.TryGetValue(id, out var paths)) map.Paths[id] = paths = [];
            paths.Remove(full);
            paths.Insert(0, full);
            if (paths.Count > MaxPathsPerId) paths.RemoveRange(MaxPathsPerId, paths.Count - MaxPathsPerId);
            JsonSidecar.Save(MapPath, map, RailReaderJsonContext.Default.IdentityMap);
            return id;
        }
    }

    /// <summary>The id last resolved for this path, with no file access (null when never resolved).
    /// Safe after the file has changed or gone.</summary>
    public static string? Peek(string pdfPath)
    {
        string full = Path.GetFullPath(pdfPath);
        lock (s_lock)
            return Map().Files.TryGetValue(full, out var e) ? e.Id : null;
    }

    /// <summary>Read-only <see cref="Resolve"/>: the id last resolved for this path, else the id an
    /// earlier open recorded for these exact bytes (a moved, renamed or copied file), else null. Hashes
    /// the file only when the path is unknown, and never writes the map — for a host that may run while
    /// the app holds the map in memory.</summary>
    public static string? Lookup(string pdfPath)
    {
        if (Peek(pdfPath) is { } id) return id;
        string full = Path.GetFullPath(pdfPath);
        if (!File.Exists(full)) return null;
        string hash = HashFile(full);
        lock (s_lock)
            return Map().Aliases.TryGetValue(hash, out var aliased) ? aliased : null;
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

        string path = MapPath;
        IdentityMap? fresh;
        try
        {
            fresh = File.Exists(path)
                ? JsonSerializer.Deserialize(JsonSidecar.ReadShared(path), RailReaderJsonContext.Default.IdentityMap)
                : new IdentityMap();
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to refresh {Path.GetFileName(path)}; keeping the loaded map", ex);
            return false;
        }
        if (fresh is null) return false;

        lock (s_lock)
        {
            if (s_owner) return false;   // Resolve ran while we were reading
            s_map = fresh;
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
        }
    }

    private static IdentityMap Map()
        => s_map ??= JsonSidecar.Load(MapPath, RailReaderJsonContext.Default.IdentityMap, static () => new IdentityMap());

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
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
