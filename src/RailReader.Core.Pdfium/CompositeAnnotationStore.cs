using System.Runtime.InteropServices;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>Why a save fell back to the JSON sidecar instead of writing into the PDF.</summary>
public enum SidecarFallbackReason
{
    /// <summary>The PDF file is read-only or cannot be opened for writing.</summary>
    ReadOnly,
    /// <summary>The PDF carries a digital signature; we don't modify signed documents.</summary>
    Signed,
}

/// <summary>
/// Makes the PDF's own /Annots the canonical annotation store, with the JSON sidecar
/// (<see cref="AnnotationService"/>) as a fallback for documents we must not / cannot
/// write to.
///
/// <para><b>Load</b> always reads native PDF annotations (via
/// <see cref="PdfAnnotationStore"/>) and merges the sidecar on top, deduped by /NM
/// (native wins); bookmarks are carried from the sidecar.</para>
///
/// <para><b>Save</b> routes by writability:
/// <list type="bullet">
/// <item><b>writable + unsigned</b> → annotations are reconciled into the PDF; bookmarks go
/// to a thin sidecar (until PR 4 stores them in the PDF).</item>
/// <item><b>read-only / signed</b> → RailReader-authored annotations + bookmarks go to the
/// sidecar, and <see cref="OnSidecarFallback"/> fires once so the UI can explain why.</item>
/// </list></para>
/// </summary>
public sealed class CompositeAnnotationStore : IAnnotationStore
{
    private readonly IAnnotationStore _sidecar;
    private readonly PdfAnnotationStore _pdfStore;
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _migrationWarned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _signedCache = new(StringComparer.OrdinalIgnoreCase);

    public CompositeAnnotationStore(IAnnotationStore sidecar, PdfAnnotationStore? pdfStore = null)
    {
        _sidecar = sidecar;
        _pdfStore = pdfStore ?? new PdfAnnotationStore();
    }

    /// <summary>Shared default wrapping <see cref="AnnotationService.Default"/>.</summary>
    public static readonly CompositeAnnotationStore Default = new(AnnotationService.Default);

    /// <summary>
    /// Raised (once per PDF path per session) when a save could not be written into the PDF
    /// and fell back to the sidecar. Lets the UI surface "annotations stored separately".
    /// </summary>
    public Action<string, SidecarFallbackReason>? OnSidecarFallback { get; set; }

    /// <summary>
    /// Raised (once per PDF path per session) when a writable PDF still has annotations in the
    /// sidecar that will be migrated <i>into</i> the PDF on the next save. Lets the UI give the
    /// user a heads-up before their notes are baked into a potentially-shared document.
    /// </summary>
    public Action<string>? OnSidecarMigration { get; set; }

    public AnnotationFile? Load(string pdfPath)
    {
        var sidecar = _sidecar.Load(pdfPath);
        var native = _pdfStore.Load(pdfPath);

        // Heads-up: a writable PDF whose sidecar still holds annotations will have them baked
        // into the PDF on the next save (lazy migration). Signal the consumer once.
        if (SidecarHasAnnotations(sidecar) && GetFallbackReason(pdfPath) is null
            && _migrationWarned.Add(Path.GetFullPath(pdfPath)))
            OnSidecarMigration?.Invoke(pdfPath);

        if (native is null || !HasAnnotations(native))
            return sidecar; // no native annotations → behave exactly like the sidecar store

        MergeSidecarInto(native, sidecar);
        native.SourcePdf = Path.GetFileName(pdfPath);
        native.SourcePdfPath = Path.GetFullPath(pdfPath);
        native.Bookmarks = sidecar?.Bookmarks ?? [];
        return native;
    }

    public bool Save(string pdfPath, AnnotationFile file)
    {
        var reason = GetFallbackReason(pdfPath);
        if (reason is null)
        {
            // PDF is canonical: reconcile annotations into the document.
            bool ok = _pdfStore.Save(pdfPath, file);
            PersistBookmarksToSidecar(pdfPath, file);
            return ok;
        }

        WarnOnce(pdfPath, reason.Value);
        return SaveAuthoredToSidecar(pdfPath, file);
    }

    public bool Delete(string pdfPath)
    {
        bool sidecarOk = _sidecar.Delete(pdfPath);
        if (GetFallbackReason(pdfPath) is null && File.Exists(pdfPath))
            return _pdfStore.Delete(pdfPath) || sidecarOk;
        return sidecarOk;
    }

    // --- routing ---

    private SidecarFallbackReason? GetFallbackReason(string pdfPath)
    {
        if (!CanWriteFile(pdfPath)) return SidecarFallbackReason.ReadOnly;
        if (IsSigned(pdfPath)) return SidecarFallbackReason.Signed;
        return null;
    }

    private static bool CanWriteFile(string pdfPath)
    {
        try
        {
            if (!File.Exists(pdfPath)) return false;
            if (File.GetAttributes(pdfPath).HasFlag(FileAttributes.ReadOnly)) return false;
            // Probe for write access without modifying the file.
            using var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsSigned(string pdfPath)
    {
        if (_signedCache.TryGetValue(pdfPath, out var cached)) return cached;

        bool signed = false;
        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            lock (PdfiumGate.Lock)
            {
                PdfiumResolver.EnsureLibraryInitialized();
                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    var doc = FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, null);
                    if (doc != IntPtr.Zero)
                    {
                        signed = FPDF_GetSignatureCount(doc) > 0;
                        FPDF_CloseDocument(doc);
                    }
                }
                finally
                {
                    pin.Free();
                }
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Debug($"[Annotations] Signature probe failed for {pdfPath}: {ex.Message}");
        }

        _signedCache[pdfPath] = signed;
        return signed;
    }

    private void WarnOnce(string pdfPath, SidecarFallbackReason reason)
    {
        if (_warned.Add(Path.GetFullPath(pdfPath)))
            OnSidecarFallback?.Invoke(pdfPath, reason);
    }

    // --- sidecar persistence ---

    /// <summary>Writable path: annotations live in the PDF; the sidecar carries only bookmarks.</summary>
    private void PersistBookmarksToSidecar(string pdfPath, AnnotationFile file)
    {
        if (file.Bookmarks.Count == 0)
        {
            _sidecar.Delete(pdfPath); // no bookmarks → drop any stale sidecar
            return;
        }

        var bookmarksOnly = new AnnotationFile
        {
            SourcePdf = Path.GetFileName(pdfPath),
            SourcePdfPath = Path.GetFullPath(pdfPath),
            Bookmarks = file.Bookmarks,
        };
        _sidecar.Save(pdfPath, bookmarksOnly);
    }

    /// <summary>Fallback path: persist RailReader-authored annotations + bookmarks to the sidecar.</summary>
    private bool SaveAuthoredToSidecar(string pdfPath, AnnotationFile file)
    {
        var authored = FilterAuthored(file);
        bool hasContent = authored.Pages.Values.Any(l => l.Count > 0) || authored.Bookmarks.Count > 0;
        if (!hasContent)
        {
            _sidecar.Delete(pdfPath);
            return true;
        }
        return _sidecar.Save(pdfPath, authored);
    }

    // --- helpers ---

    private static bool HasAnnotations(AnnotationFile f) => f.Pages.Values.Any(l => l.Count > 0);

    private static bool SidecarHasAnnotations(AnnotationFile? f) => f is not null && HasAnnotations(f);

    private static void MergeSidecarInto(AnnotationFile native, AnnotationFile? sidecar)
    {
        if (sidecar is null) return;

        foreach (var (page, sidecarList) in sidecar.Pages)
        {
            native.Pages.TryGetValue(page, out var target);
            target ??= [];

            foreach (var ann in sidecarList)
            {
                // Dedup against native by /NM or by value. Value-equivalence is what keeps
                // lazy migration idempotent: once a sidecar annotation has been baked into the
                // PDF (with a fresh /NM), its still-present sidecar copy — which has no /NM to
                // match on — is suppressed here, so a failed/late sidecar cleanup can't surface
                // duplicates.
                bool duplicate = target.Any(e =>
                    (ann.NativeId is not null && string.Equals(e.NativeId, ann.NativeId, StringComparison.Ordinal))
                    || AnnotationEquivalence.ContentEquivalent(e, ann));
                if (!duplicate) target.Add(ann);
            }

            if (target.Count > 0) native.Pages[page] = target;
        }
    }

    private static AnnotationFile FilterAuthored(AnnotationFile src)
    {
        var dst = new AnnotationFile
        {
            Version = src.Version,
            SourcePdf = src.SourcePdf,
            SourcePdfPath = src.SourcePdfPath,
            Bookmarks = src.Bookmarks,
        };

        foreach (var (page, list) in src.Pages)
        {
            var authored = list.Where(a => a.Source != AnnotationSource.InPdf).ToList();
            if (authored.Count > 0)
                dst.Pages[page] = authored;
        }

        return dst;
    }
}
