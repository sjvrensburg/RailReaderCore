using System.Runtime.InteropServices;
using RailReader.Core;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Extracts per-page text and character bounding boxes from PDFs via PDFium P/Invoke.
/// Relies on PDFtoImage having already loaded the native pdfium library.
/// </summary>
public sealed class PdfTextService : IPdfTextService
{

    private static readonly PageText s_empty = new("", []);

    /// <summary>
    /// Extracts all text and per-character bounding boxes for a given page.
    /// PDFium returns coordinates in PDF user space (origin bottom-left, Y-up).
    /// This method converts them to page-point space (origin top-left, Y-down)
    /// matching BBox and the overlay layers.
    /// </summary>
    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex, string? password = null)
        => ExtractPageText(pdfBytes, pageIndex, 0, password);

    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
    {
        return WithTextPage(pdfBytes, pageIndex, viewRotation, password, s_empty, "extract text",
            (textPage, tx) =>
            {
                int charCount = FPDFText_CountChars(textPage);
                if (charCount <= 0)
                    return s_empty;

                // Build text character-by-character using FPDFText_GetUnicode to ensure
                // 1:1 index correspondence with FPDFText_GetCharBox.
                var textChars = new char[charCount];
                var charBoxes = new List<CharBox>(charCount);
                for (int i = 0; i < charCount; i++)
                {
                    uint unicode = FPDFText_GetUnicode(textPage, i);
                    textChars[i] = unicode <= 0xFFFF ? (char)unicode : '\uFFFD';

                    double left = 0, right = 0, bottom = 0, top = 0;
                    if (FPDFText_GetCharBox(textPage, i, ref left, ref right, ref bottom, ref top))
                    {
                        var (l, t, r, b) = tx.PdfRectToPage(left, bottom, right, top);
                        charBoxes.Add(new CharBox(i, l, t, r, b));
                    }
                    else
                    {
                        charBoxes.Add(new CharBox(i, 0, 0, 0, 0));
                    }
                }
                string text = new string(textChars);

                return new PageText(text, charBoxes);
            });
    }

    /// <summary>
    /// Uses PDFium's FPDFText_CountRects/GetRect to get visual bounding rectangles
    /// for character ranges on a page. Returns rects in page-point space (origin top-left, Y-down),
    /// adjusted for CropBox offset so highlights align with the rendered page.
    /// </summary>
    public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges, string? password = null)
        => GetTextRangeRects(pdfBytes, pageIndex, ranges, 0, password);

    public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges, int viewRotation, string? password = null)
    {
        var result = new List<List<RectF>>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
            result.Add([]);

        return WithTextPage(pdfBytes, pageIndex, viewRotation, password, result, "get text range rects",
            (textPage, tx) =>
            {
                for (int i = 0; i < ranges.Count; i++)
                {
                    var (charStart, charLength) = ranges[i];
                    int rectCount = FPDFText_CountRects(textPage, charStart, charLength);
                    for (int r = 0; r < rectCount; r++)
                    {
                        double left = 0, top = 0, right = 0, bottom = 0;
                        if (FPDFText_GetRect(textPage, r, ref left, ref top, ref right, ref bottom))
                        {
                            var (l, t, rr, b) = tx.PdfRectToPage(left, bottom, right, top);
                            result[i].Add(new RectF(l, t, rr, b));
                        }
                    }
                }

                return result;
            });
    }

    /// <summary>
    /// Loads a PDFium document and text page from in-memory PDF bytes, invokes
    /// <paramref name="action"/> with the text page handle and CropBox transform,
    /// then tears everything down in a finally block.
    /// Returns <paramref name="defaultValue"/> if the document, page, or text page
    /// fails to load, or if an exception is thrown.
    /// </summary>
    private static T WithTextPage<T>(byte[] pdfBytes, int pageIndex, int viewRotation, string? password, T defaultValue,
        string operationName, Func<IntPtr, PageTransform, T> action)
    {
        lock (PdfiumGate.Lock)
        {
            IntPtr doc = IntPtr.Zero;
            IntPtr page = IntPtr.Zero;
            IntPtr textPage = IntPtr.Zero;
            GCHandle pinned = default;

            try
            {
                pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
                // Read path is fail-soft: a wrong/missing password yields IntPtr.Zero and the
                // default (empty) result rather than throwing — this runs on the render hot
                // path, and the open boundary already validated the password. The write/open
                // paths use LoadDocumentChecked to throw instead.
                doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, password);
                if (doc == IntPtr.Zero)
                    return defaultValue;

                page = FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero)
                    return defaultValue;

                var tx = GetPageTransform(page, viewRotation);

                textPage = FPDFText_LoadPage(page);
                if (textPage == IntPtr.Zero)
                    return defaultValue;

                return action(textPage, tx);
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Error($"[PdfText] Failed to {operationName} for page {pageIndex}", ex);
                return defaultValue;
            }
            finally
            {
                if (textPage != IntPtr.Zero) FPDFText_ClosePage(textPage);
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                if (pinned.IsAllocated) pinned.Free();
            }
        }
    }
}
