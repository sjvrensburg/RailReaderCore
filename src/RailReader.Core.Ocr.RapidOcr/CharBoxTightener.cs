using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// Snaps each OCR <see cref="CharBox"/> to the actual ink it contains — both horizontally and
/// vertically — instead of trusting RapidOcrNet's own estimated width and line-spanning height.
///
/// <para>
/// <b>Why this exists (railreader2#209).</b> RapidOcrNet's per-character boxes (from
/// <c>CalRecBoxes.BuildCellsForWord</c>, decompiled — RapidOcrNet ships no source for this) get
/// a genuine per-character X-<i>centre</i> from the recogniser's own CTC column decode, but a
/// shared line-level <i>average</i> character width for every box's size, only locally nudged
/// where two estimated cells would otherwise overlap. On clean rendered text that average is a
/// fair approximation; on a real scan (skew, noise, uneven ink) it visibly misaligns — verified
/// by overlaying boxes on a reporter-submitted scanned page, where body-text boxes bled across
/// word-space gaps and sat offset from the glyphs beneath them. Selection/highlight rects are
/// built as the union of the char boxes in range
/// (<see cref="RailReader.Core.AnnotationInteractionHandler.BuildHighlightRects"/>), so an
/// oversized char box reads directly as an oversized, misaligned highlight.
/// </para>
/// <para>
/// <b>Why vertical matters too (found investigating a follow-up report against the same
/// <c>test.pdf</c>).</b> Every character in a word inherits the <i>word's</i> full Top/Bottom
/// (<see cref="RapidOcrService"/>'s <c>CharBoxesFor</c> — RapidOcrNet reports no intra-word
/// glyph vertical extent), which is close to the full line height. Two downstream consumers
/// cluster characters into lines by vertical position on the assumption that a character's own
/// height is small relative to the gap between lines — true for real PDFium glyph boxes, false
/// here: <see cref="LineDetector.DetectLinesFromChars"/>'s median-height split threshold and
/// <see cref="RailReader.Core.AnnotationInteractionHandler"/>'s vertical-overlap line grouping
/// both merge adjacent real printed lines into one whenever a scan's line spacing is tighter
/// than its line height (common in single-spaced book text) — respectively under-segmenting
/// rail navigation lines and collapsing a multi-line drag selection into one oversized rect.
/// Tightening Top/Bottom to each glyph's own ink extent (ascender-to-descender for <i>that</i>
/// character, exactly like a real PDFium char box) fixes both for free — neither consumer needs
/// to change, since both were already built and tuned against real per-glyph vertical extents.
/// </para>
/// <para>
/// <b>Approach.</b> Dark-pixel projection — the same luminance-threshold idiom
/// <see cref="LineDetector.DetectColumnGrid"/> already uses for vertical-rule detection —
/// scanning strictly <i>within</i> each box's own estimated extent (vertical tightening scans
/// rows using the already horizontally-tightened column window, so a neighbour's ink can't leak
/// in). The result can only shrink a box, never grow one: adjacent estimated boxes already tile
/// the line without overlapping (RapidOcrNet's own overlap-adjustment), so confining the search
/// to a box's own bounds is sufficient to guarantee a tightened box never encroaches on a
/// neighbour, with no separate neighbour-midpoint bookkeeping needed. Any window with no usable
/// ink (a mis-attributed blank box, very light print, or an implausibly small result) is left
/// untouched on that axis — the feature is strictly non-regressive: worst case a character keeps
/// today's behaviour.
/// </para>
/// </summary>
internal static class CharBoxTightener
{
    /// <summary>
    /// A column (resp. row) counts as "ink" once at least this fraction of the rows (resp.
    /// columns) in the box's own opposite-axis span are dark — filters lone-pixel scan noise
    /// while still catching a thin vertical stroke (the stem of "l" or "i", which spans nearly
    /// the whole line height) or a thin horizontal one (the crossbar of a "t").
    /// </summary>
    private const float MinInkRowFraction = 0.20f;

    /// <summary>Padding kept around the tightest ink run so anti-aliased glyph edges are not clipped.</summary>
    private const float PaddingPx = 1f;

    /// <summary>Below this extent a tightened result is treated as noise, not a real glyph, and discarded.</summary>
    private const float MinResultExtentPx = 2f;

    /// <summary>
    /// Returns a new list with every box tightened to the ink found within its own original
    /// extent, both horizontally (<see cref="CharBox.Left"/>/<see cref="CharBox.Right"/>) and
    /// vertically (<see cref="CharBox.Top"/>/<see cref="CharBox.Bottom"/>). Boxes with degenerate
    /// extents, or with no ink found on an axis, pass through unchanged on that axis.
    /// </summary>
    public static List<CharBox> Tighten(List<CharBox> boxes, byte[] rgbBytes, int imgW, int imgH)
    {
        if (boxes.Count == 0 || rgbBytes is null || rgbBytes.Length == 0 || imgW <= 0 || imgH <= 0)
            return boxes;

        var result = new List<CharBox>(boxes.Count);
        foreach (var box in boxes)
            result.Add(TightenOne(box, rgbBytes, imgW, imgH));
        return result;
    }

    private static CharBox TightenOne(CharBox box, byte[] rgbBytes, int imgW, int imgH)
    {
        int left = Math.Max(0, (int)MathF.Floor(box.Left));
        int right = Math.Min(imgW, (int)MathF.Ceiling(box.Right));
        int top = Math.Max(0, (int)MathF.Floor(box.Top));
        int bottom = Math.Min(imgH, (int)MathF.Ceiling(box.Bottom));
        if (right - left < 1 || bottom - top < 1) return box;

        float newLeft = box.Left, newRight = box.Right;
        int hInkLeft = FindInkRun(rgbBytes, imgW, left, right, top, bottom, scanX: true, out int hInkRight);
        if (hInkLeft >= 0)
        {
            float candLeft = Math.Max(box.Left, hInkLeft - PaddingPx);
            float candRight = Math.Min(box.Right, hInkRight + 1 + PaddingPx);
            if (candRight - candLeft >= MinResultExtentPx) { newLeft = candLeft; newRight = candRight; }
        }

        // Vertical scan uses the (possibly just-tightened) horizontal window, so a neighbouring
        // glyph's ink — still inside the original, wider column range — can't be picked up as
        // this character's own vertical extent.
        int vLeft = Math.Max(left, (int)MathF.Floor(newLeft));
        int vRight = Math.Min(right, (int)MathF.Ceiling(newRight));
        float newTop = box.Top, newBottom = box.Bottom;
        if (vRight - vLeft >= 1)
        {
            int vInkTop = FindInkRun(rgbBytes, imgW, vLeft, vRight, top, bottom, scanX: false, out int vInkBottom);
            if (vInkTop >= 0)
            {
                float candTop = Math.Max(box.Top, vInkTop - PaddingPx);
                float candBottom = Math.Min(box.Bottom, vInkBottom + 1 + PaddingPx);
                if (candBottom - candTop >= MinResultExtentPx) { newTop = candTop; newBottom = candBottom; }
            }
        }

        return box with { Left = newLeft, Right = newRight, Top = newTop, Bottom = newBottom };
    }

    /// <summary>
    /// Scans the box window for the first and last "inked" line along one axis — columns
    /// (<paramref name="scanX"/> true) or rows (false) — each requiring at least
    /// <see cref="MinInkRowFraction"/> of the opposite axis to be dark. Returns -1 (with
    /// <paramref name="inkEnd"/> also -1) when no line qualifies.
    /// </summary>
    private static int FindInkRun(
        byte[] rgbBytes, int imgW, int left, int right, int top, int bottom, bool scanX, out int inkEnd)
    {
        int outerCount = scanX ? right - left : bottom - top;
        int innerCount = scanX ? bottom - top : right - left;
        int minDark = Math.Max(1, (int)(innerCount * MinInkRowFraction));

        int inkStart = -1;
        inkEnd = -1;
        for (int o = 0; o < outerCount; o++)
        {
            int dark = 0;
            for (int i = 0; i < innerCount; i++)
            {
                int x = scanX ? left + o : left + i;
                int y = scanX ? top + i : top + o;
                int idx = (y * imgW + x) * 3;
                if (idx + 2 >= rgbBytes.Length) continue;
                float luminance = rgbBytes[idx] * 0.299f + rgbBytes[idx + 1] * 0.587f + rgbBytes[idx + 2] * 0.114f;
                if (luminance < LayoutConstants.DarkLuminanceThreshold) dark++;
            }
            if (dark < minDark) continue;
            if (inkStart < 0) inkStart = scanX ? left + o : top + o;
            inkEnd = scanX ? left + o : top + o;
        }
        return inkStart;
    }
}
