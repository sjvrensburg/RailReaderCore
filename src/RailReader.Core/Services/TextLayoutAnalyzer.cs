using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// A layout analyzer with no model behind it: blocks are recovered from the page's own text
/// layer by bottom-up grouping, so it runs anywhere Core runs — no ONNX Runtime, no weights to
/// ship, no native dependency.
///
/// <para>
/// That is the point of it. Every other <see cref="ILayoutAnalyzer"/> needs a model file
/// (4.7 MB for the smallest, 164 MB for the largest) and a runtime that can execute it, which
/// is exactly what a web or low-end mobile target struggles to provide. This one gives such a
/// build a working rail pipeline out of the box, and gives every build a fallback when the
/// model is missing or fails to load.
/// </para>
/// <para>
/// The method is Docstrum's central idea — take the thresholds from the page rather than from a
/// constant. Character spacing varies by an order of magnitude between a dense two-column
/// journal page and a large-print book, so a fixed "same paragraph" gap is wrong on most
/// documents; the nearest-neighbour spacing distribution of the page's own glyphs is right on
/// all of them. Adapted from the Docstrum implementation in PdfPig (and CalyPdf's optimised
/// port of it), reduced to what a rail reader needs: this produces prose blocks to navigate,
/// not a full physical-layout tree.
/// </para>
/// <para>
/// <b>Limits, stated plainly.</b> Without a model there is no class signal, so every block is
/// <see cref="BlockRole.Text"/> — no figures, tables, captions or headings are distinguished,
/// and features keyed to those roles (table-row reading, cell navigation, figure framing,
/// auto-scroll stop classes) do nothing. It needs a text layer: on a scan it returns nothing
/// unless OCR supplied one (see <see cref="IOcrService"/>). Reading order comes from the
/// pipeline's <see cref="XYCutPlusPlusResolver"/>, as for any model without an order signal.
/// </para>
/// </summary>
public sealed class TextLayoutAnalyzer : ILayoutAnalyzer
{
    /// <summary>
    /// Rasterisation size requested of the caller. Nothing here reads pixels, but the
    /// surrounding pipeline still rasterises for line detection's fallback path and for the
    /// debug overlay, so this asks for the same modest page the light models use.
    /// </summary>
    public const int DefaultInputSize = 800;

    /// <summary>
    /// Two glyphs belong to the same line when their vertical extents overlap and their
    /// horizontal gap is at most this multiple of the page's estimated within-line spacing.
    /// Generous, because within-line spacing estimated across a whole page understates the gap
    /// at a word boundary in a wide-tracked font.
    /// </summary>
    private const float WithinLineMultiplier = 3.0f;

    /// <summary>
    /// Two lines belong to the same block when the vertical gap between them is at most this
    /// multiple of the page's estimated line pitch. Just above 1 keeps a paragraph together
    /// while separating it from the next one, which is set off by extra leading or an indent.
    /// </summary>
    private const float BetweenLineMultiplier = 1.6f;

    /// <summary>
    /// Minimum horizontal overlap between a line and the block it would join, as a fraction of
    /// the narrower of the two. Keeps a second column's lines out of the first column's block
    /// even where their vertical spacing would allow the join.
    /// </summary>
    private const float BlockOverlapFraction = 0.25f;

    public LayoutModelCapabilities Capabilities { get; } =
        new(DefaultInputSize, [], ProvidesReadingOrder: false);

    /// <inheritdoc/>
    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
    {
        var analysis = new PageAnalysis { PageWidth = pageW, PageHeight = pageH };
        if (charBoxes is not { Count: > 0 }) return analysis;

        ct.ThrowIfCancellationRequested();

        var glyphs = new List<CharBox>(charBoxes.Count);
        foreach (var c in charBoxes)
            if (c.Right > c.Left && c.Bottom > c.Top) glyphs.Add(c);   // skip whitespace boxes
        if (glyphs.Count == 0) return analysis;

        float withinLine = EstimateWithinLineSpacing(glyphs);
        var lines = BuildLines(glyphs, withinLine);
        if (lines.Count == 0) return analysis;

        ct.ThrowIfCancellationRequested();

        float linePitch = EstimateLinePitch(lines);
        foreach (var bbox in BuildBlocks(lines, linePitch))
            analysis.Blocks.Add(new LayoutBlock { BBox = bbox, Role = BlockRole.Text, Confidence = 1f });

        return analysis;
    }

    /// <summary>
    /// Estimates the page's typical inter-glyph gap along a line: for each glyph, the distance
    /// to the nearest glyph to its right that shares its vertical band, taken as a median so
    /// that column gutters and paragraph indents (the long tail) do not shift it.
    /// Falls back to a fraction of the median glyph height when no pairs are found.
    /// </summary>
    internal static float EstimateWithinLineSpacing(List<CharBox> glyphs)
    {
        var byY = new List<CharBox>(glyphs);
        byY.Sort((a, b) =>
        {
            float ay = (a.Top + a.Bottom) * 0.5f, by = (b.Top + b.Bottom) * 0.5f;
            int cmp = ay.CompareTo(by);
            return cmp != 0 ? cmp : a.Left.CompareTo(b.Left);
        });

        var gaps = new List<float>();
        for (int i = 0; i < byY.Count; i++)
        {
            var g = byY[i];
            float best = float.PositiveInfinity;
            // Neighbours are adjacent in Y-sorted order, so a short forward scan finds the
            // same-line successor without an O(n²) sweep over the page.
            for (int j = i + 1; j < byY.Count && j <= i + 24; j++)
            {
                var h = byY[j];
                if (h.Bottom <= g.Top || h.Top >= g.Bottom) continue;   // different line
                float gap = h.Left - g.Right;
                if (gap >= 0 && gap < best) best = gap;
            }
            if (!float.IsPositiveInfinity(best)) gaps.Add(best);
        }

        if (gaps.Count > 0)
        {
            gaps.Sort();
            float median = gaps[gaps.Count / 2];
            if (median > 0) return median;
        }

        return MedianHeight(glyphs) * 0.25f;
    }

    private static float MedianHeight(List<CharBox> glyphs)
    {
        var heights = new List<float>(glyphs.Count);
        foreach (var g in glyphs) heights.Add(g.Bottom - g.Top);
        heights.Sort();
        return Math.Max(1f, heights[heights.Count / 2]);
    }

    /// <summary>
    /// Groups glyphs into text lines: sort by vertical centre, start a new line where a glyph
    /// no longer overlaps the current line's band, then split each line wherever a horizontal
    /// gap exceeds the page's within-line spacing — which is what separates a two-column page's
    /// side-by-side lines into one run each.
    /// </summary>
    internal static List<BBox> BuildLines(List<CharBox> glyphs, float withinLineSpacing)
    {
        var sorted = new List<CharBox>(glyphs);
        sorted.Sort((a, b) => ((a.Top + a.Bottom) * 0.5f).CompareTo((b.Top + b.Bottom) * 0.5f));

        float maxGap = withinLineSpacing * WithinLineMultiplier;
        var lines = new List<BBox>();
        var current = new List<CharBox>();
        float bandTop = 0f, bandBottom = 0f;

        foreach (var g in sorted)
        {
            if (current.Count == 0)
            {
                current.Add(g);
                bandTop = g.Top; bandBottom = g.Bottom;
                continue;
            }

            // Overlap with the band, not distance to its centre: superscripts and inline math
            // sit off-centre but still belong to the line they annotate.
            if (g.Top < bandBottom && g.Bottom > bandTop)
            {
                current.Add(g);
                bandTop = Math.Min(bandTop, g.Top);
                bandBottom = Math.Max(bandBottom, g.Bottom);
            }
            else
            {
                EmitRuns(current, maxGap, lines);
                current.Clear();
                current.Add(g);
                bandTop = g.Top; bandBottom = g.Bottom;
            }
        }
        EmitRuns(current, maxGap, lines);

        return lines;
    }

    /// <summary>
    /// Splits one vertical band's glyphs into horizontal runs at gaps wider than
    /// <paramref name="maxGap"/> and appends each run's bounds. A band spanning two columns
    /// yields one run per column.
    /// </summary>
    private static void EmitRuns(List<CharBox> band, float maxGap, List<BBox> lines)
    {
        if (band.Count == 0) return;
        band.Sort((a, b) => a.Left.CompareTo(b.Left));

        float left = band[0].Left, right = band[0].Right;
        float top = band[0].Top, bottom = band[0].Bottom;

        for (int i = 1; i < band.Count; i++)
        {
            var g = band[i];
            if (g.Left - right > maxGap)
            {
                lines.Add(new BBox(left, top, right - left, bottom - top));
                left = g.Left; right = g.Right; top = g.Top; bottom = g.Bottom;
            }
            else
            {
                right = Math.Max(right, g.Right);
                top = Math.Min(top, g.Top);
                bottom = Math.Max(bottom, g.Bottom);
            }
        }
        lines.Add(new BBox(left, top, right - left, bottom - top));
    }

    /// <summary>
    /// Estimates the page's line pitch — the median vertical distance from one line's top to
    /// the next's, over lines that actually follow one another. Falls back to the median line
    /// height when there are too few lines to measure.
    /// </summary>
    internal static float EstimateLinePitch(List<BBox> lines)
    {
        if (lines.Count < 2)
            return lines.Count == 1 ? Math.Max(1f, lines[0].H) : 1f;

        var ordered = new List<BBox>(lines);
        ordered.Sort((a, b) => a.Y.CompareTo(b.Y));

        var pitches = new List<float>();
        for (int i = 1; i < ordered.Count; i++)
        {
            float d = ordered[i].Y - ordered[i - 1].Y;
            if (d > 0) pitches.Add(d);
        }

        if (pitches.Count == 0)
        {
            var heights = new List<float>(lines.Count);
            foreach (var l in lines) heights.Add(l.H);
            heights.Sort();
            return Math.Max(1f, heights[heights.Count / 2]);
        }

        pitches.Sort();
        return Math.Max(1f, pitches[pitches.Count / 2]);
    }

    /// <summary>
    /// Merges lines into blocks: a line joins an open block when it follows closely enough in
    /// Y (relative to the page's own line pitch) and shares enough of its width with it. Both
    /// conditions are needed — the vertical test alone would run a two-column page's blocks
    /// together, and the horizontal test alone would merge a heading with the paragraph three
    /// inches below it.
    /// </summary>
    internal static List<BBox> BuildBlocks(List<BBox> lines, float linePitch)
    {
        var ordered = new List<BBox>(lines);
        ordered.Sort((a, b) =>
        {
            int cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.X.CompareTo(b.X);
        });

        float maxGap = linePitch * BetweenLineMultiplier;
        var blocks = new List<BBox>();
        // Blocks stay open so a line can join the column it belongs to rather than only the
        // most recent line — interleaved columns arrive interleaved in Y order.
        var open = new List<BBox>();

        foreach (var line in ordered)
        {
            int best = -1;
            float bestGap = float.PositiveInfinity;

            for (int i = 0; i < open.Count; i++)
            {
                var b = open[i];
                float gap = line.Y - (b.Y + b.H);
                if (gap > maxGap) continue;                    // too far below to continue it
                if (line.Y + line.H < b.Y) continue;           // entirely above

                float overlap = Math.Min(b.X + b.W, line.X + line.W) - Math.Max(b.X, line.X);
                if (overlap < Math.Min(b.W, line.W) * BlockOverlapFraction) continue;

                if (gap < bestGap) { bestGap = gap; best = i; }
            }

            if (best < 0)
            {
                open.Add(line);
                continue;
            }

            var target = open[best];
            float x = Math.Min(target.X, line.X);
            float y = Math.Min(target.Y, line.Y);
            float right = Math.Max(target.X + target.W, line.X + line.W);
            float bottom = Math.Max(target.Y + target.H, line.Y + line.H);
            open[best] = new BBox(x, y, right - x, bottom - y);
        }

        blocks.AddRange(open);
        blocks.Sort((a, b) =>
        {
            int cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.X.CompareTo(b.X);
        });
        return blocks;
    }

    public void Dispose() { }
}
