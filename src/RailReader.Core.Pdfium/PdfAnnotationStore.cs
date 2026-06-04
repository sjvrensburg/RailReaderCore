using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// An <see cref="IAnnotationStore"/> backed by the PDF itself: annotations live in
/// the document's /Annots dictionaries. <see cref="Load"/> reads them via
/// <see cref="PdfAnnotationReader"/>; <see cref="Save"/> reconciles the document to
/// match the model via <see cref="PdfAnnotationWriter.WriteReconciled"/> (incremental
/// save, preserving AcroForm/signatures/unchanged annotations).
///
/// <para>Bookmarks are not yet stored in the PDF (PR 4) — this store ignores them.
/// Used for writable PDFs; non-writable ones fall back to the JSON sidecar (the
/// routing lives in <see cref="CompositeAnnotationStore"/>).</para>
/// </summary>
public sealed class PdfAnnotationStore : IAnnotationStore
{
    private readonly PdfAnnotationReader _reader = new();
    private readonly PdfAnnotationWriter _writer = new();

    public AnnotationFile? Load(string pdfPath)
    {
        if (!File.Exists(pdfPath)) return null;
        try
        {
            var file = _reader.Read(File.ReadAllBytes(pdfPath));
            file.SourcePdf = Path.GetFileName(pdfPath);
            file.SourcePdfPath = Path.GetFullPath(pdfPath);
            return file;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[Annotations] Failed to read PDF annotations from {pdfPath}", ex);
            return null;
        }
    }

    public bool Save(string pdfPath, AnnotationFile annotations) => Reconcile(pdfPath, annotations);

    /// <summary>Removes all managed annotations from the PDF (reconciles to empty).</summary>
    public bool Delete(string pdfPath) => Reconcile(pdfPath, new AnnotationFile());

    private bool Reconcile(string pdfPath, AnnotationFile annotations)
    {
        if (!File.Exists(pdfPath)) return false;
        try
        {
            var bytes = File.ReadAllBytes(pdfPath);
            var updated = _writer.WriteReconciled(bytes, annotations);
            WriteAtomically(pdfPath, updated);
            return true;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[Annotations] Failed to write PDF annotations to {pdfPath}", ex);
            return false;
        }
    }

    /// <summary>Writes to a sibling temp file then moves into place, so a crash mid-write
    /// cannot leave a truncated PDF.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var tmp = path + ".rrtmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
