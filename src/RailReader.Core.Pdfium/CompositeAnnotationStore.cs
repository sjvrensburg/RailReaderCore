using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// An <see cref="IAnnotationStore"/> that treats the PDF's own /Annots as the
/// canonical source of annotations and uses the JSON sidecar
/// (<see cref="AnnotationService"/>) for RailReader-authored annotations and
/// bookmarks.
///
/// <para><b>Load</b> reads native PDF annotations via <see cref="PdfAnnotationReader"/>
/// and merges the sidecar on top, deduped by /NM (native wins). Bookmarks are
/// carried from the sidecar.</para>
///
/// <para><b>Save</b> (PR 1 scope) persists only RailReader-authored annotations
/// (<see cref="AnnotationSource.RailReader"/>) and bookmarks to the sidecar —
/// native, in-PDF annotations are read-only here and are re-read from the PDF on
/// each load, never copied into the sidecar. Writing edits back into the PDF is
/// PR 2.</para>
/// </summary>
public sealed class CompositeAnnotationStore : IAnnotationStore
{
    private readonly IAnnotationStore _sidecar;
    private readonly PdfAnnotationReader _reader;

    public CompositeAnnotationStore(IAnnotationStore sidecar, PdfAnnotationReader? reader = null)
    {
        _sidecar = sidecar;
        _reader = reader ?? new PdfAnnotationReader();
    }

    /// <summary>Shared default wrapping <see cref="AnnotationService.Default"/>.</summary>
    public static readonly CompositeAnnotationStore Default = new(AnnotationService.Default);

    public AnnotationFile? Load(string pdfPath)
    {
        var sidecar = _sidecar.Load(pdfPath);
        var native = ReadNative(pdfPath);

        if (native is null || !HasAnnotations(native))
            return sidecar; // no native annotations → behave exactly like the sidecar store

        MergeSidecarInto(native, sidecar);
        native.SourcePdf = Path.GetFileName(pdfPath);
        native.SourcePdfPath = Path.GetFullPath(pdfPath);
        native.Bookmarks = sidecar?.Bookmarks ?? [];
        return native;
    }

    public bool Save(string pdfPath, AnnotationFile annotations)
    {
        // PR 1: in-PDF annotations are read-only — persist only RailReader-authored
        // annotations and bookmarks to the sidecar.
        var authored = FilterAuthored(annotations);
        bool hasContent = authored.Pages.Values.Any(l => l.Count > 0)
            || authored.Bookmarks.Count > 0;

        if (!hasContent)
        {
            // Nothing of our own to persist; clean up any stale sidecar but report
            // success so the manager doesn't surface a spurious save-failure.
            _sidecar.Delete(pdfPath);
            return true;
        }

        return _sidecar.Save(pdfPath, authored);
    }

    public bool Delete(string pdfPath) => _sidecar.Delete(pdfPath);

    private AnnotationFile? ReadNative(string pdfPath)
    {
        try
        {
            if (!File.Exists(pdfPath)) return null;
            return _reader.Read(File.ReadAllBytes(pdfPath));
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[Annotations] Failed to read native annotations from {pdfPath}", ex);
            return null;
        }
    }

    private static bool HasAnnotations(AnnotationFile f) => f.Pages.Values.Any(l => l.Count > 0);

    /// <summary>
    /// Adds sidecar annotations into <paramref name="native"/>, skipping any that
    /// duplicate a native annotation by /NM (native is authoritative).
    /// </summary>
    private static void MergeSidecarInto(AnnotationFile native, AnnotationFile? sidecar)
    {
        if (sidecar is null) return;

        foreach (var (page, sidecarList) in sidecar.Pages)
        {
            var nativeIds = native.Pages.TryGetValue(page, out var existing)
                ? existing.Where(a => a.NativeId is not null).Select(a => a.NativeId!).ToHashSet(StringComparer.Ordinal)
                : [];

            foreach (var ann in sidecarList)
            {
                if (ann.NativeId is not null && nativeIds.Contains(ann.NativeId))
                    continue; // native copy wins

                if (!native.Pages.TryGetValue(page, out var target))
                {
                    target = [];
                    native.Pages[page] = target;
                }
                target.Add(ann);
            }
        }
    }

    /// <summary>Projects out only the RailReader-authored annotations (+ bookmarks).</summary>
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
