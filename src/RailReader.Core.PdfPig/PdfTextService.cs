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
    /// Word-gap threshold as a fraction of the local font size (PdfPig's
    /// <c>Letter.PointSize</c>). Glyph widths vary per character — 'i'
    /// and 'r' are ~3pt wide, 'm' and 'w' are ~9pt at the same font
    /// size — so a width-based threshold mis-fires on kerning before
    /// narrow letters. The font size is stable across chars in the
    /// same run, giving a much more reliable reference. ~25% of the
    /// font size corresponds to roughly the natural inter-word gap a
    /// PDF renderer emits.
    /// </summary>
    private const float WordGapThreshold = 0.25f;

    /// <summary>
    /// Walks <see cref="Page.Letters"/>, flipping Y-up → Y-down using the
    /// page height, and emits one <see cref="CharBox"/> per character in
    /// the concatenated text. Ligature glyphs whose <c>Value</c> is
    /// multi-char get one box per char, all pointing at the same glyph
    /// rect — matches PDFium's per-codepoint behaviour so downstream
    /// indexing is identical.
    ///
    /// <para>
    /// PdfPig's <c>Letters</c> collection contains only the visible
    /// glyphs from the PDF content stream — no whitespace tokens. To
    /// keep the extracted text usable for word-boundary search and
    /// drag-to-copy, this method inserts synthetic <c>' '</c> and
    /// <c>'\n'</c> characters where the geometry implies them. The
    /// synthetic chars get <see cref="CharBox"/>es positioned in the
    /// physical gap so <see cref="PageText.ExtractTextInRect"/> picks
    /// them up when the drag rect spans the surrounding glyphs.
    /// </para>
    /// </summary>
    private static PageText BuildPageText(Page page)
    {
        var letters = page.Letters;
        if (letters.Count == 0) return s_empty;

        double pageH = page.Height;
        var sb = new StringBuilder(letters.Count);
        var boxes = new List<CharBox>(letters.Count);

        float prevRight = float.NaN;
        float prevTop = float.NaN;
        float prevBottom = float.NaN;
        float prevPointSize = 0f;

        foreach (var letter in letters)
        {
            var rect = letter.BoundingBox;
            float left   = (float)rect.Left;
            float right  = (float)rect.Right;
            float top    = (float)(pageH - rect.Top);
            float bottom = (float)(pageH - rect.Bottom);
            float height = bottom - top;
            float pointSize = (float)letter.PointSize;

            if (!float.IsNaN(prevRight))
            {
                float midY     = (top + bottom) / 2f;
                float prevMidY = (prevTop + prevBottom) / 2f;
                float refLineH = Math.Max(1f, Math.Max(height, prevBottom - prevTop));
                bool sameLine = Math.Abs(midY - prevMidY) <= refLineH * 0.5f;

                if (!sameLine)
                {
                    // Line break — synthetic '\n' positioned at the
                    // right edge of the previous letter so a drag that
                    // catches the tail of line N captures the newline.
                    int idx = sb.Length;
                    sb.Append('\n');
                    boxes.Add(new CharBox(idx, prevRight, prevTop, prevRight + 1f, prevBottom));
                }
                else
                {
                    float gap = left - prevRight;
                    // Use the larger of the two surrounding letters' font
                    // sizes as the reference. Stable across the line, so
                    // narrow chars like 'i'/'r' don't trip the threshold
                    // inside words.
                    float refSize = Math.Max(1f, Math.Max(pointSize, prevPointSize));
                    if (gap > refSize * WordGapThreshold)
                    {
                        // Word break — synthetic ' ' positioned in the
                        // actual horizontal gap. ExtractTextInRect's
                        // midpoint test picks it up whenever the drag
                        // rect spans the surrounding glyphs.
                        int idx = sb.Length;
                        sb.Append(' ');
                        float spaceTop    = Math.Min(prevTop, top);
                        float spaceBottom = Math.Max(prevBottom, bottom);
                        boxes.Add(new CharBox(idx, prevRight, spaceTop, left, spaceBottom));
                    }
                }
            }

            string value = letter.Value ?? "";
            if (value.Length == 0)
            {
                int index = sb.Length;
                sb.Append('�');
                boxes.Add(new CharBox(index, left, top, right, bottom));
            }
            else
            {
                foreach (var ch in value)
                {
                    int index = sb.Length;
                    sb.Append(ch);
                    boxes.Add(new CharBox(index, left, top, right, bottom));
                }
            }

            prevRight     = right;
            prevTop       = top;
            prevBottom    = bottom;
            prevPointSize = pointSize;
        }

        return new PageText(sb.ToString(), boxes);
    }
}
