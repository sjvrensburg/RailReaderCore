using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

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

    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex, string? password = null)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes, PdfPigOpen.Options(password));
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
        List<(int CharStart, int CharLength)> ranges, string? password = null)
    {
        var result = new List<List<RectF>>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
            result.Add([]);

        try
        {
            using var doc = PdfDocument.Open(pdfBytes, PdfPigOpen.Options(password));
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
    /// Walks the words returned by PdfPig's
    /// <see cref="NearestNeighbourWordExtractor"/>, flipping each
    /// letter's Y-up → Y-down using the page height, and emits one
    /// <see cref="CharBox"/> per character in the concatenated text.
    /// Ligature glyphs whose <c>Value</c> is multi-char get one box
    /// per char, all pointing at the same glyph rect — matches
    /// PDFium's per-codepoint behaviour so downstream indexing is
    /// identical.
    ///
    /// <para>
    /// PdfPig's raw <c>Letters</c> collection contains only the
    /// visible glyphs from the PDF content stream — no whitespace
    /// tokens — and naive geometry-based gap detection is unreliable
    /// for justified typesetting where the inter-word gap can be
    /// comparable to intra-word kerning. <c>NearestNeighbourWordExtractor</c>
    /// is purpose-built for this problem and handles tight academic
    /// typesetting correctly. Between consecutive extracted words we
    /// insert a synthetic <c>' '</c> if they share a baseline, or
    /// <c>'\n'</c> if they don't. The synthetic chars get
    /// <see cref="CharBox"/>es positioned in the physical gap so
    /// <see cref="PageText.ExtractTextInRect"/> picks them up when the
    /// drag rect spans the surrounding glyphs.
    /// </para>
    /// </summary>
    private static PageText BuildPageText(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
        if (words.Count == 0) return s_empty;

        double pageH = page.Height;
        var sb = new StringBuilder(words.Sum(w => w.Letters.Count));
        var boxes = new List<CharBox>(sb.Capacity);

        float prevWordRight = float.NaN;
        float prevWordTop = float.NaN;
        float prevWordBottom = float.NaN;

        foreach (var word in words)
        {
            // Compute the word's flipped bbox once for break-detection
            // against the previous word.
            var (wLeft, wTop, wRight, wBottom) = FlippedAabb(word.BoundingBox, pageH);

            if (!float.IsNaN(prevWordRight))
            {
                // Same visual line ⇔ the two words' vertical extents overlap. Use range
                // overlap rather than a midpoint-distance heuristic: some producers
                // (e.g. SkiaSharp) emit explicit space glyphs whose box is zero-height
                // and anchored at the baseline. Such a box's midpoint sits ~half a
                // glyph-height below adjacent glyph centres, so a midpoint test
                // mis-reads the space boundary as a line break and fragments words
                // ("Page\n \n1\n of 3" instead of "Page 1 of 3"). A small tolerance
                // absorbs the baseline touch and sub-pixel jitter.
                const float lineTol = 2f;
                float overlapTop    = Math.Max(wTop, prevWordTop);
                float overlapBottom = Math.Min(wBottom, prevWordBottom);
                bool sameLine = overlapTop <= overlapBottom + lineTol;

                // PDFs that encode spacing as explicit ' ' chars in the
                // content stream produce Words whose Letters already
                // contain that whitespace — sometimes as a trailing char
                // of one word, sometimes as a whitespace-only word in
                // its own right. Only inject our synthetic separator if
                // there isn't whitespace already on either side.
                char lastChar  = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                char firstChar = word.Text.Length > 0 ? word.Text[0] : '\0';
                bool boundaryAlreadyWhitespace = char.IsWhiteSpace(lastChar) || char.IsWhiteSpace(firstChar);

                if (sameLine)
                {
                    if (!boundaryAlreadyWhitespace)
                    {
                        int idx = sb.Length;
                        sb.Append(' ');
                        float spaceTop    = Math.Min(prevWordTop, wTop);
                        float spaceBottom = Math.Max(prevWordBottom, wBottom);
                        boxes.Add(new CharBox(idx, prevWordRight, spaceTop, wLeft, spaceBottom));
                    }
                }
                else if (lastChar != '\n')
                {
                    int idx = sb.Length;
                    sb.Append('\n');
                    boxes.Add(new CharBox(idx, prevWordRight, prevWordTop,
                                          prevWordRight + 1f, prevWordBottom));
                }
            }

            foreach (var letter in word.Letters)
            {
                var (left, top, right, bottom) = FlippedAabb(letter.BoundingBox, pageH);

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
            }

            prevWordRight  = wRight;
            prevWordTop    = wTop;
            prevWordBottom = wBottom;
        }

        return new PageText(sb.ToString(), boxes);
    }

    /// <summary>
    /// Converts a PdfPig rectangle to a Y-flipped axis-aligned box
    /// (Left, Top, Right, Bottom in page-point space, Y-down).
    /// PdfPig rectangles are <b>oriented</b>: for glyphs rotated by the page
    /// /Rotate attribute or by an in-content rotation, Left/Right and
    /// Top/Bottom follow the glyph's own axes and can come back inverted
    /// (Left &gt; Right), which used to produce degenerate boxes on rotated
    /// text. Taking the min/max over the four corners yields the correct
    /// axis-aligned bounds regardless of glyph orientation. Verified
    /// empirically (tools/rotation-probe-pdfpig): ink coverage 1.000 on all
    /// four /Rotate values and on 90°-rotated in-content text.
    /// </summary>
    private static (float Left, float Top, float Right, float Bottom) FlippedAabb(
        UglyToad.PdfPig.Core.PdfRectangle rect, double pageH)
    {
        double minX = Math.Min(Math.Min(rect.TopLeft.X, rect.TopRight.X),
                               Math.Min(rect.BottomLeft.X, rect.BottomRight.X));
        double maxX = Math.Max(Math.Max(rect.TopLeft.X, rect.TopRight.X),
                               Math.Max(rect.BottomLeft.X, rect.BottomRight.X));
        double minY = Math.Min(Math.Min(rect.TopLeft.Y, rect.TopRight.Y),
                               Math.Min(rect.BottomLeft.Y, rect.BottomRight.Y));
        double maxY = Math.Max(Math.Max(rect.TopLeft.Y, rect.TopRight.Y),
                               Math.Max(rect.BottomLeft.Y, rect.BottomRight.Y));
        return ((float)minX, (float)(pageH - maxY), (float)maxX, (float)(pageH - minY));
    }
}
