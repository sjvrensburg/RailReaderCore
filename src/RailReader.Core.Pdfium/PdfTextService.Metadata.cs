using System.Runtime.InteropServices;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Document metadata for the PDFium backend — the <see cref="IPdfMetadataService"/> capability.
/// Reads the Info dictionary only: PDFium doesn't parse XMP, so a document whose title exists only
/// in its XMP packet reports none.
/// </summary>
public sealed partial class PdfTextService : IPdfMetadataService
{
    public PdfMetadata GetMetadata(byte[] pdfBytes, string? password = null)
    {
        lock (PdfiumGate.Lock)
        {
            PdfiumResolver.EnsureLibraryInitialized();
            try
            {
                IntPtr doc = _docCache.GetOrLoad(pdfBytes, password);
                if (doc == IntPtr.Zero) return PdfMetadata.Empty;
                return new PdfMetadata(
                    MetaText(doc, "Title"),
                    MetaText(doc, "Author"),
                    MetaText(doc, "Subject"),
                    MetaText(doc, "Keywords"),
                    MetaText(doc, "Creator"),
                    MetaText(doc, "Producer"));
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Warn($"[PdfTextService] Failed to read metadata: {ex.Message}");
                return PdfMetadata.Empty;
            }
        }
    }

    // Caller holds PdfiumGate.Lock.
    private static string? MetaText(IntPtr doc, string tag)
    {
        uint len = FPDF_GetMetaText(doc, tag, IntPtr.Zero, 0);
        if (len <= 2) return null;   // empty: just the UTF-16 terminator
        IntPtr buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            FPDF_GetMetaText(doc, tag, buffer, len);
            string? text = Marshal.PtrToStringUni(buffer)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
