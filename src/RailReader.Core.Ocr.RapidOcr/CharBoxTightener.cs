using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// Snaps each OCR <see cref="CharBox"/> horizontally to the actual ink it contains, instead of
/// trusting RapidOcrNet's own estimated width.
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
/// <b>Approach.</b> Column-wise dark-pixel projection — the same luminance-threshold idiom
/// <see cref="LineDetector.DetectColumnGrid"/> already uses for vertical-rule detection —
/// scanning strictly <i>within</i> each box's own estimated extent. The result can only shrink
/// a box, never grow one: adjacent estimated boxes already tile the line without overlapping
/// (RapidOcrNet's own overlap-adjustment), so confining the search to a box's own bounds is
/// sufficient to guarantee a tightened box never encroaches on a neighbour, with no separate
/// neighbour-midpoint bookkeeping needed. Any window with no usable ink (a mis-attributed blank
/// box, very light print, or an implausibly small result) is left untouched — the feature is
/// strictly non-regressive: worst case a character keeps today's behaviour.
/// </para>
/// </summary>
internal static class CharBoxTightener
{
    /// <summary>
    /// A column counts as "ink" once at least this fraction of its rows (within the box's own
    /// vertical span) are dark — filters lone-pixel scan noise while still catching a thin
    /// vertical stroke (the stem of "l" or "i", which spans nearly the whole line height).
    /// </summary>
    private const float MinInkRowFraction = 0.20f;

    /// <summary>Padding kept around the tightest ink run so anti-aliased glyph edges are not clipped.</summary>
    private const float PaddingPx = 1f;

    /// <summary>Below this width a tightened result is treated as noise, not a real glyph, and discarded.</summary>
    private const float MinResultWidthPx = 2f;

    /// <summary>
    /// Returns a new list with every box's <see cref="CharBox.Left"/>/<see cref="CharBox.Right"/>
    /// tightened to the ink found within its own original extent (top/bottom unchanged — those
    /// already span the recogniser's full line height, which line detection already gets right).
    /// Boxes with degenerate extents, or with no ink found, pass through unchanged.
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
        int left = (int)MathF.Floor(box.Left);
        int right = (int)MathF.Ceiling(box.Right);
        int top = Math.Max(0, (int)MathF.Floor(box.Top));
        int bottom = Math.Min(imgH, (int)MathF.Ceiling(box.Bottom));
        left = Math.Max(0, left);
        right = Math.Min(imgW, right);
        if (right - left < 1 || bottom - top < 1) return box;

        int rowCount = bottom - top;
        int minDarkRows = Math.Max(1, (int)(rowCount * MinInkRowFraction));

        int inkLeft = -1, inkRight = -1;
        for (int x = left; x < right; x++)
        {
            int dark = 0;
            for (int y = top; y < bottom; y++)
            {
                int idx = (y * imgW + x) * 3;
                if (idx + 2 >= rgbBytes.Length) continue;
                float luminance = rgbBytes[idx] * 0.299f + rgbBytes[idx + 1] * 0.587f + rgbBytes[idx + 2] * 0.114f;
                if (luminance < LayoutConstants.DarkLuminanceThreshold) dark++;
            }
            if (dark < minDarkRows) continue;
            if (inkLeft < 0) inkLeft = x;
            inkRight = x;
        }

        if (inkLeft < 0) return box; // no ink found in this box's window — leave it as estimated

        float newLeft = Math.Max(box.Left, inkLeft - PaddingPx);
        float newRight = Math.Min(box.Right, inkRight + 1 + PaddingPx);
        if (newRight - newLeft < MinResultWidthPx) return box;

        return box with { Left = newLeft, Right = newRight };
    }
}
