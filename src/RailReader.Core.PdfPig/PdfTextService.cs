using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace RailReader.Core.PdfPig;

/// <summary>
/// Pure-managed text extraction via UglyToad.PdfPig. Returns coordinates
/// in page-point space (origin top-left, Y-down) matching the Pdfium
/// implementation, so consumers can swap backends without coordinate
/// fix-ups downstream.
/// </summary>
public sealed class PdfTextService : IPdfTextService
{
    private static readonly PageText s_empty = new("", []);

    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return s_empty;

            // PdfPig pages are 1-indexed; Core's IPdfTextService is 0-indexed.
            var page = doc.GetPage(pageIndex + 1);
            return BuildPageText(page);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[PdfPig.Text] Failed to extract text for page {pageIndex}", ex);
            return s_empty;
        }
    }

    public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges)
    {
        var result = new List<List<RectF>>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
            result.Add([]);

        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return result;

            var page = doc.GetPage(pageIndex + 1);
            var pageText = BuildPageText(page);
            var boxes = pageText.CharBoxes;
            int textLen = pageText.Text.Length;

            for (int i = 0; i < ranges.Count; i++)
            {
                var (start, length) = ranges[i];
                int end = start + length;
                if (start < 0 || length <= 0 || start >= textLen) continue;
                end = Math.Min(end, textLen);

                // Group contiguous boxes on the same visual line into single rects.
                // Same heuristic as PDFium's FPDFText_GetRect would produce: split
                // when the Y-band changes significantly.
                RectF? current = null;
                float currentMidY = 0f;
                float currentLineHeight = 1f;

                foreach (var box in boxes)
                {
                    if (box.Index < start || box.Index >= end) continue;

                    float midY = (box.Top + box.Bottom) / 2f;
                    float lineHeight = Math.Max(1f, box.Bottom - box.Top);

                    if (current is null)
                    {
                        current = new RectF(box.Left, box.Top, box.Right, box.Bottom);
                        currentMidY = midY;
                        currentLineHeight = lineHeight;
                    }
                    else if (Math.Abs(midY - currentMidY) > currentLineHeight * 0.5f)
                    {
                        result[i].Add(current.Value);
                        current = new RectF(box.Left, box.Top, box.Right, box.Bottom);
                        currentMidY = midY;
                        currentLineHeight = lineHeight;
                    }
                    else
                    {
                        var c = current.Value;
                        current = new RectF(
                            Math.Min(c.Left, box.Left),
                            Math.Min(c.Top, box.Top),
                            Math.Max(c.Right, box.Right),
                            Math.Max(c.Bottom, box.Bottom));
                    }
                }
                if (current is not null) result[i].Add(current.Value);
            }

            return result;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[PdfPig.Text] Failed range-rects for page {pageIndex}", ex);
            return result;
        }
    }

    /// <summary>
    /// Walks <see cref="Page.Letters"/>, flipping Y-up → Y-down using the
    /// page height, and emits one <see cref="CharBox"/> per character in
    /// the concatenated text. Ligature glyphs whose <c>Value</c> is
    /// multi-char get one box per char, all pointing at the same glyph
    /// rect — matches PDFium's per-codepoint behaviour so downstream
    /// indexing is identical.
    /// </summary>
    private static PageText BuildPageText(Page page)
    {
        var letters = page.Letters;
        if (letters.Count == 0) return s_empty;

        double pageH = page.Height;
        var sb = new StringBuilder(letters.Count);
        var boxes = new List<CharBox>(letters.Count);

        foreach (var letter in letters)
        {
            var rect = letter.BoundingBox;
            float left   = (float)rect.Left;
            float right  = (float)rect.Right;
            float top    = (float)(pageH - rect.Top);
            float bottom = (float)(pageH - rect.Bottom);

            string value = letter.Value ?? "";
            if (value.Length == 0)
            {
                int index = sb.Length;
                sb.Append('�');
                boxes.Add(new CharBox(index, left, top, right, bottom));
                continue;
            }

            foreach (var ch in value)
            {
                int index = sb.Length;
                sb.Append(ch);
                boxes.Add(new CharBox(index, left, top, right, bottom));
            }
        }

        return new PageText(sb.ToString(), boxes);
    }
}
