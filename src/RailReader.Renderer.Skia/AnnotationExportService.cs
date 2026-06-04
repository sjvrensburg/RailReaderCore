using System.Runtime.InteropServices;
using RailReader.Core.Models;
using RailReader.Core.Services;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Exports a PDF with annotations baked into a flattened <b>new</b> document:
/// copies the original pages verbatim (preserving vector content) and overlays the
/// annotations as native PDF annotation objects. Per-annotation writing is delegated
/// to <see cref="PdfAnnotationWriter"/> so geometry/metadata mapping has a single
/// source of truth.
///
/// <para>For editing annotations <i>in place</i> (preserving the source document's own
/// /Annots and AcroForm), use <see cref="PdfAnnotationWriter.AddAuthoredAnnotations"/>
/// instead — this export path intentionally produces a separate, self-contained copy.</para>
/// </summary>
public static class AnnotationExportService
{
    public static void Export(
        IPdfService pdf,
        AnnotationFile annotations,
        string outputPath,
        int dpi = 300,
        Action<int, int>? onProgress = null)
    {
        var pdfBytes = pdf.PdfBytes;
        lock (PdfiumGate.Lock)
        {
            var pinnedSrc = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            try
            {
                var srcDoc = FPDF_LoadMemDocument(pinnedSrc.AddrOfPinnedObject(), pdfBytes.Length, null);
                if (srcDoc == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to load source PDF via PDFium");

                var destDoc = FPDF_CreateNewDocument();
                if (destDoc == IntPtr.Zero)
                {
                    FPDF_CloseDocument(srcDoc);
                    throw new InvalidOperationException("Failed to create new PDF document");
                }

                try
                {
                    if (!FPDF_ImportPages(destDoc, srcDoc, null, 0))
                        throw new InvalidOperationException("Failed to import pages from source PDF");

                    for (int pageIdx = 0; pageIdx < pdf.PageCount; pageIdx++)
                    {
                        onProgress?.Invoke(pageIdx, pdf.PageCount);

                        if (!annotations.Pages.TryGetValue(pageIdx, out var pageAnns) || pageAnns.Count == 0)
                            continue;

                        var page = FPDF_LoadPage(destDoc, pageIdx);
                        if (page == IntPtr.Zero) continue;

                        try
                        {
                            var (cropLeft, cropBottom, visibleHeight) = GetCropBoxTransform(page);

                            foreach (var ann in pageAnns)
                                PdfAnnotationWriter.WriteAnnotationToPage(page, ann, cropLeft, cropBottom, visibleHeight);
                        }
                        finally
                        {
                            FPDF_ClosePage(page);
                        }
                    }

                    SaveDocument(destDoc, outputPath);
                }
                finally
                {
                    FPDF_CloseDocument(destDoc);
                    FPDF_CloseDocument(srcDoc);
                }
            }
            finally
            {
                pinnedSrc.Free();
            }
        }
    }

    private static unsafe void SaveDocument(IntPtr document, string outputPath)
    {
        using var stream = File.Create(outputPath);

        WriteBlockDelegate writeBlock = (IntPtr self, IntPtr data, uint size) =>
        {
            stream.Write(new ReadOnlySpan<byte>(data.ToPointer(), (int)size));
            return 1;
        };

        var writeBlockPtr = Marshal.GetFunctionPointerForDelegate(writeBlock);
        var fileWrite = new FpdfFileWrite
        {
            Version = 1,
            WriteBlock = writeBlockPtr,
        };

        if (!FPDF_SaveAsCopy(document, ref fileWrite, 0))
            throw new InvalidOperationException("Failed to save PDF document");

        // Prevent GC from collecting the delegate while PDFium holds the function pointer
        GC.KeepAlive(writeBlock);
    }
}
