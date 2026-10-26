using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Manages annotation persistence. Annotations are stored internally in the
/// app config directory (ConfigDir/annotations/), keyed by a hash of the PDF
/// path. Legacy sidecar files (alongside the PDF) are loaded as a migration
/// fallback but never written to.
/// </summary>
public sealed class AnnotationService : IAnnotationStore
{
    /// <summary>
    /// Shared default instance. Most callers can use this; consumers that
    /// need a different store (e.g. tests, or future web implementations)
    /// inject their own <see cref="IAnnotationStore"/>.
    /// </summary>
    public static readonly AnnotationService Default = new();


    private static string? _annotationDir;

    /// <summary>Internal storage directory for annotation files.</summary>
    public static string AnnotationDir => _annotationDir ??= InitAnnotationDir();

    private static string InitAnnotationDir()
    {
        var dir = Path.Combine(AppConfig.ConfigDir, "annotations");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Optional host-supplied stable document identity (pdfPath → key; null = fall back to path
    /// hashing). When it returns a key, the sidecar is <c>annotations/&lt;key&gt;.json</c>, so it
    /// survives the PDF being moved, renamed or copied. A key that is not a plain file name
    /// (separators, invalid characters, dots-only) is ignored. When the keyed file does not exist
    /// yet, the legacy path-hash file is read instead and the keyed file is written on the next
    /// save; the legacy file is kept.
    /// </summary>
    public Func<string, string?>? DocumentKeyResolver { get; set; }

    /// <summary>
    /// Fires (pdfPath, exception) when <see cref="DocumentKeyResolver"/> throws during
    /// <c>Load</c>/<c>Save</c>/<c>Delete</c>, so the host can tell a failed lookup from "no
    /// annotations" and warn the user. Invoked on the calling thread; exceptions from it are swallowed.
    /// </summary>
    public Action<string, Exception>? OnResolverFailure { get; set; }

    /// <summary>Get the path-hash storage path for a PDF's annotations (ignores any document key).</summary>
    public static string GetInternalPath(string pdfPath)
    {
        var hash = GetPathHash(pdfPath);
        return Path.Combine(AnnotationDir, $"{hash}.json");
    }

    /// <summary>
    /// Storage path for a PDF's annotations: keyed by <see cref="DocumentKeyResolver"/> when it
    /// yields a usable key, else the path-hash file. Propagates a resolver failure.
    /// </summary>
    public string GetStoragePath(string pdfPath)
    {
        var key = ResolveKey(pdfPath);
        return key is null ? GetInternalPath(pdfPath) : Path.Combine(AnnotationDir, $"{key}.json");
    }

    private string? ResolveKey(string pdfPath)
    {
        // Deliberately not caught: falling back to the path hash would put this call's sidecar
        // somewhere other than the keyed file a successful call uses, so edits would look lost.
        var key = DocumentKeyResolver?.Invoke(pdfPath);
        if (string.IsNullOrWhiteSpace(key) || key.Trim('.').Length == 0
            || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return key;
    }

    /// <summary>Get the legacy sidecar path (for migration only).</summary>
    public static string GetSidecarPath(string pdfPath)
    {
        var dir = Path.GetDirectoryName(pdfPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        return Path.Combine(dir, $"{name}.railreader2.json");
    }

    /// <summary>
    /// Load annotations for a PDF. Checks internal storage first,
    /// falls back to legacy sidecar file (and migrates it to internal).
    /// </summary>
    // password is unused: the sidecar persists annotations in a separate JSON file
    // and never opens the (possibly encrypted) PDF itself.
    public AnnotationFile? Load(string pdfPath, string? password = null)
    {
        string storagePath;
        try { storagePath = GetStoragePath(pdfPath); }
        catch (Exception ex) { return ResolverFailed<AnnotationFile?>(pdfPath, ex, null); }

        // Try internal storage first
        if (File.Exists(storagePath))
            return LoadFromFile(storagePath);

        // No keyed file yet: the document may predate the key, so read the path-hash file.
        // It is left in place; the next save writes the keyed file.
        var internalPath = GetInternalPath(pdfPath);
        if (internalPath != storagePath && File.Exists(internalPath))
            return LoadFromFile(internalPath);

        // Fall back to legacy sidecar (migration)
        var sidecarPath = GetSidecarPath(pdfPath);
        if (File.Exists(sidecarPath))
        {
            var annotations = LoadFromFile(sidecarPath);
            if (annotations is not null)
            {
                // Set the full path for orphan detection
                annotations.SourcePdfPath = Path.GetFullPath(pdfPath);
                // Migrate to internal storage
                SaveToFile(storagePath, annotations);
                RailReaderLogging.Logger.Debug($"[Annotations] Migrated sidecar to internal storage: {Path.GetFileName(pdfPath)}");
            }
            return annotations;
        }

        return null;
    }

    /// <summary>Save annotations to internal storage. Returns false if the write fails.</summary>
    public bool Save(string pdfPath, AnnotationFile annotations, string? password = null)
    {
        string storagePath;
        try { storagePath = GetStoragePath(pdfPath); }
        catch (Exception ex) { return ResolverFailed(pdfPath, ex, false); }

        annotations.SourcePdfPath = Path.GetFullPath(pdfPath);
        return SaveToFile(storagePath, annotations);
    }

    /// <summary>Export annotations to a JSON file at a user-chosen path. Throws on failure.</summary>
    public static void ExportJson(AnnotationFile annotations, string outputPath)
    {
        var json = JsonSerializer.Serialize(annotations, RailReaderJsonContext.Default.AnnotationFile);
        File.WriteAllText(outputPath, json);
    }

    /// <summary>Import annotations from a JSON file. Returns null if deserialization fails.</summary>
    public static AnnotationFile? ImportJson(string inputPath)
        => LoadFromFile(inputPath);

    /// <summary>
    /// Merges imported annotations into an existing annotation file.
    /// Appends annotations per page and adds bookmarks that don't already exist.
    /// An imported annotation whose <c>/NM</c> already exists in the target (or earlier in the
    /// import) has its <see cref="Annotation.NativeId"/> cleared: the PDF writer and
    /// <c>CompositeAnnotationStore</c> dedupe by <c>/NM</c>, so importing an export back into
    /// the same PDF would otherwise silently lose the copies on save.
    /// </summary>
    public static int MergeInto(AnnotationFile target, AnnotationFile imported)
    {
        int added = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in target.Pages.Values)
            foreach (var a in list)
                if (!string.IsNullOrEmpty(a.NativeId)) seenIds.Add(a.NativeId);

        foreach (var (page, annotations) in imported.Pages)
        {
            foreach (var a in annotations)
            {
                if (string.IsNullOrEmpty(a.NativeId) || seenIds.Add(a.NativeId)) continue;
                // Source must go with the id: the writer skips InPdf annotations that have no /NM,
                // so a cleared id alone would still drop the copy on save.
                a.NativeId = null;
                a.Source = AnnotationSource.RailReader;
            }

            if (!target.Pages.TryGetValue(page, out var existing))
            {
                existing = [];
                target.Pages[page] = existing;
            }
            existing.AddRange(annotations);
            added += annotations.Count;
        }

        // Add bookmarks that don't already exist (by name + page)
        var existingBookmarks = new HashSet<(string, int)>(
            target.Bookmarks.Select(b => (b.Name, b.Page)));
        foreach (var bm in imported.Bookmarks)
        {
            if (existingBookmarks.Add((bm.Name, bm.Page)))
            {
                target.Bookmarks.Add(bm);
                added++;
            }
        }

        return added;
    }

    /// <summary>Delete internal annotation file for a PDF.</summary>
    public bool Delete(string pdfPath, string? password = null)
    {
        // Both files: a surviving legacy file would be read back by Load once the keyed one is gone.
        string storagePath;
        try { storagePath = GetStoragePath(pdfPath); }
        catch (Exception ex) { return ResolverFailed(pdfPath, ex, false); }

        var deleted = false;
        foreach (var path in new[] { storagePath, GetInternalPath(pdfPath) }.Distinct())
        {
            if (!File.Exists(path)) continue;
            try { File.Delete(path); deleted = true; }
            catch (Exception ex) { RailReaderLogging.Logger.Debug($"[Annotations] Failed to delete {path}: {ex.Message}"); }
        }
        return deleted;
    }

    /// <summary>List all internally stored annotation files with metadata.</summary>
    public static List<AnnotationStorageInfo> ListStored()
    {
        var result = new List<AnnotationStorageInfo>();
        if (!Directory.Exists(AnnotationDir)) return result;

        foreach (var file in Directory.EnumerateFiles(AnnotationDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var af = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AnnotationFile);
                if (af is null) continue;

                var fi = new FileInfo(file);
                result.Add(new AnnotationStorageInfo(
                    file, af.SourcePdf, af.SourcePdfPath,
                    fi.Length, fi.LastWriteTime,
                    af.Pages.Values.Sum(p => p.Count), af.Bookmarks.Count,
                    !string.IsNullOrEmpty(af.SourcePdfPath) && File.Exists(af.SourcePdfPath)));
            }
            catch (Exception ex) { RailReaderLogging.Logger.Debug($"[Annotations] Skip corrupt file {file}: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>Remove annotation files whose source PDFs no longer exist.</summary>
    public static (int Removed, long BytesFreed) CleanOrphaned()
    {
        int removed = 0;
        long bytesFreed = 0;

        foreach (var info in ListStored())
        {
            if (info.SourcePdfExists || string.IsNullOrEmpty(info.SourcePdfPath))
                continue;

            // A document-keyed sidecar is meant to outlive the PDF moving or being renamed, so
            // its recorded path going stale does not make it an orphan. Only path-hash files do.
            if (!IsPathHashName(Path.GetFileNameWithoutExtension(info.InternalPath)))
                continue;

            try
            {
                bytesFreed += info.Size;
                File.Delete(info.InternalPath);
                removed++;
            }
            catch (Exception ex) { RailReaderLogging.Logger.Debug($"[Annotations] Failed to clean orphan: {ex.Message}"); }
        }

        return (removed, bytesFreed);
    }

    private static AnnotationFile? LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AnnotationFile);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[Annotations] Failed to load {path}", ex);
            return null;
        }
    }

    private static bool SaveToFile(string path, AnnotationFile annotations)
    {
        try
        {
            var json = JsonSerializer.Serialize(annotations, RailReaderJsonContext.Default.AnnotationFile);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[Annotations] Failed to save {path}", ex);
            return false;
        }
    }

    /// <summary>
    /// A failed resolver means the keyed location is unknown, so the call fails (Save/Delete
    /// return false, Load returns null) rather than touching the path-hash file; logged as an error.
    /// </summary>
    private T ResolverFailed<T>(string pdfPath, Exception ex, T result)
    {
        RailReaderLogging.Logger.Error("[Annotations] Document key resolver failed; sidecar left untouched", ex);
        try { OnResolverFailure?.Invoke(pdfPath, ex); }
        catch (Exception cbEx) { RailReaderLogging.Logger.Debug($"[Annotations] OnResolverFailure threw: {cbEx.Message}"); }
        return result;
    }

    private static bool IsPathHashName(string name) =>
        name.Length == 16 && name.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));

    private static string GetPathHash(string pdfPath)
    {
        var fullPath = Path.GetFullPath(pdfPath);
        var bytes = Encoding.UTF8.GetBytes(fullPath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>Metadata about a stored annotation file, for display and cleanup.</summary>
public sealed record AnnotationStorageInfo(
    string InternalPath, string SourcePdf, string SourcePdfPath,
    long Size, DateTime LastModified,
    int AnnotationCount, int BookmarkCount, bool SourcePdfExists);
