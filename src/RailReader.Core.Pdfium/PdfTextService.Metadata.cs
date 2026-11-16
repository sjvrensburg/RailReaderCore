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
                // Reuse the text cache only for the document it already holds: a library host reads
                // other files' metadata through the open document's service, and loading them into
                // the one-entry cache would make that document's next page extraction re-parse it.
                IntPtr cached = _docCache.PeekCached(pdfBytes, password);
                if (cached != IntPtr.Zero) return Read(cached);

                var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
                IntPtr doc = IntPtr.Zero;
                try
                {
                    doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, password);
                    return doc == IntPtr.Zero ? PdfMetadata.Empty : Read(doc);
                }
                finally
                {
                    if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                    pinned.Free();
                }
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Warn($"[PdfTextService] Failed to read metadata: {ex.Message}");
                return PdfMetadata.Empty;
            }
        }
    }

    // Caller holds PdfiumGate.Lock.
    private static PdfMetadata Read(IntPtr doc) => new(
        MetaText(doc, "Title"),
        MetaText(doc, "Author"),
        MetaText(doc, "Subject"),
        MetaText(doc, "Keywords"),
        MetaText(doc, "Creator"),
        MetaText(doc, "Producer"));

    // Caller holds PdfiumGate.Lock.
    private static string? MetaText(IntPtr doc, string tag)
    {
        uint len = FPDF_GetMetaText(doc, tag, IntPtr.Zero, 0);
        if (len <= 2) return null;   // empty: just the UTF-16 terminator
        IntPtr buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            FPDF_GetMetaText(doc, tag, buffer, len);
            // Explicit length (like ReadAnnotString): a PDF text string may contain a NUL.
            string text = Marshal.PtrToStringUni(buffer, (int)(len / 2) - 1).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
