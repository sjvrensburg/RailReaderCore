using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Detects text lines inside layout blocks.
///
/// Three strategies are applied in order of preference:
///   1. <b>Atomic classes</b> — equation, figure, table blocks collapse to a single
///      line spanning the full block. Multi-line equations and figures should
///      advance in rail mode as one unit, not be fragmented row-by-row.
///   2. <b>Char-box clustering</b> — when PDFium per-character bounding boxes are
///      available, cluster them by vertical position. Robust to subscripts,
///      superscripts, and inline math; gives true baselines rather than the
///      smoothed peaks of pixel projection.
///   3. <b>Pixel projection</b> — fall back to row-density analysis of the
///      rasterized page. Used for scanned PDFs and any block where char
///      clustering produced nothing.
/// </summary>
public static class LineDetector
{
    /// <summary>
    /// Block roles treated as a single atomic line in rail mode. Only purely
    /// visual blocks belong here — they have no meaningful per-line structure
    /// and should advance as one unit. Math roles (<see cref="BlockRole.DisplayMath"/>,
    /// <see cref="BlockRole.InlineMath"/>, <see cref="BlockRole.Algorithm"/>)
    /// deliberately stay line-detectable because stepwise derivations and
    /// algorithm pseudocode read line-by-line; char-box clustering handles
    /// those without fragmenting sub/superscripts.
    /// </summary>
    internal static readonly HashSet<BlockRole> AtomicLineRoles =
    [
        BlockRole.Figure,
        BlockRole.Chart,
        BlockRole.Table,
    ];

    /// <summary>
    /// Returns line runs (in block-relative pixel coordinates) for a block,
    /// using char clustering when available and pixel projection as fallback.
    /// </summary>
    /// <summary>
    /// Cluster split threshold as a multiple of median char height. Math blocks
    /// use a more generous multiple so a single display equation's stacked parts
    /// (fraction bars, sub/superscripts, matrix rows) cluster into one line
    /// rather than fragmenting — while genuinely separated derivation rows, whose
    /// inter-row gaps are larger, still split. Tuned against the Surya line
    /// oracle (see tools/line-eval): char-clustering was over-segmenting
    /// DisplayMath ~3-4×.
    /// </summary>
    internal const float DefaultSplitMultiplier = 1.0f;
    internal const float MathSplitMultiplier = 1.3f;

    /// <summary>
    /// A char taller than this multiple of the block's median char height is
    /// treated as an oversize glyph — typically a drop cap — and lifted out of
    /// line clustering so it can't dominate a line's vertical band. Normal
    /// glyph-height variation (caps and ascenders/descenders vs x-height) stays
    /// well under this; a drop cap spanning 2–3 text lines is several times the
    /// median, far above it. See <see cref="DetectLinesFromChars"/>.
    /// </summary>
    internal const float OversizeGlyphFactor = 1.8f;

    private static readonly HashSet<BlockRole> MathRoles =
        [BlockRole.DisplayMath, BlockRole.InlineMath, BlockRole.Algorithm];

    public static List<LineInfo> DetectLines(
        LayoutBlock block,
        IReadOnlyList<CharBox>? charBoxes,
        byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        if (AtomicLineRoles.Contains(block.Role))
            return [new LineInfo(block.BBox.Y + block.BBox.H / 2f, block.BBox.H, block.BBox.X, block.BBox.W)];

        if (charBoxes is { Count: > 0 })
        {
            bool isMath = MathRoles.Contains(block.Role);
            float mult = isMath ? MathSplitMultiplier : DefaultSplitMultiplier;
            // Lift oversize spanning glyphs (drop caps) out of clustering for prose,
            // but not for math — there a tall bracket legitimately spans, and should
            // merge, stacked rows (matrices, large fractions).
            var charLines = DetectLinesFromChars(block.BBox, charBoxes, mult, excludeOversizeSpanners: !isMath);
            if (charLines.Count > 0)
                return NormalizeLines(charLines, block.BBox);
        }

        return NormalizeLines(DetectLinesFromPixels(block, rgbBytes, imgW, imgH, scaleX, scaleY), block.BBox);
    }

    /// <summary>
    /// Enforces the invariants every line consumer (rail stepping, snap, line
    /// focus/highlight, and chunk concatenation) silently assumes: positive
    /// height, geometry clamped inside the block, sorted top-to-bottom, and no
    /// two lines overlapping by more than half the smaller. Idempotent.
    /// </summary>
    internal static List<LineInfo> NormalizeLines(List<LineInfo> lines, BBox block)
    {
        float top = block.Y, bottom = block.Y + block.H;
        float left = block.X, right = block.X + block.W;

        var clamped = new List<LineInfo>(lines.Count);
        foreach (var l in lines)
        {
            float h = l.Height;
            if (h <= 0) continue;
            // Clamp the [top,bottom] band into the block, recompute centre/height.
            float lt = Math.Max(top, l.Y - h / 2f);
            float lb = Math.Min(bottom, l.Y + h / 2f);
            if (lb - lt <= 0) continue;
            float lx = Math.Max(left, l.X);
            float lr = Math.Min(right, l.X + l.Width);
            float lw;
            if (lr - lx > 0) { lw = lr - lx; }
            else { lx = left; lw = block.W; }   // extent doesn't overlap → span the block, anchored at its left
            if (lw <= 0) continue;              // degenerate block width
            clamped.Add(new LineInfo((lt + lb) / 2f, lb - lt, lx, lw));
        }

        clamped.Sort((a, b) => a.Y.CompareTo(b.Y));

        // Merge lines whose vertical bands overlap by > 50% of the smaller.
        var merged = new List<LineInfo>(clamped.Count);
        foreach (var l in clamped)
        {
            if (merged.Count > 0)
            {
                var p = merged[^1];
                float pt = p.Y - p.Height / 2f, pb = p.Y + p.Height / 2f;
                float lt = l.Y - l.Height / 2f, lb = l.Y + l.Height / 2f;
                float ov = Math.Min(pb, lb) - Math.Max(pt, lt);
                if (ov > 0.5f * Math.Min(p.Height, l.Height))
                {
                    float nt = Math.Min(pt, lt), nb = Math.Max(pb, lb);
                    float nx = Math.Min(p.X, l.X), nr = Math.Max(p.X + p.Width, l.X + l.Width);
                    merged[^1] = new LineInfo((nt + nb) / 2f, nb - nt, nx, nr - nx);
                    continue;
                }
            }
            merged.Add(l);
        }
        return merged;
    }

    private readonly record struct GlyphBox(float MidY, float Top, float Bottom, float Height, float Left, float Right);

    // Vertical+horizontal extent of one detected line, accumulated before being
    // converted to a LineInfo. Mutable-by-replacement (it's a value tuple in a list).
    private record struct Band(float Top, float Bottom, float Left, float Right)
    {
        public readonly float CenterY => (Top + Bottom) * 0.5f;
        public readonly bool OverlapsY(in GlyphBox g) => g.Bottom > Top && g.Top < Bottom;
        public readonly LineInfo ToLineInfo() => new(CenterY, Bottom - Top, Left, Right - Left);
    }

    /// <summary>
    /// Clusters character bounding boxes by vertical position into text lines.
    /// Returns lines in page-point space (Y measured from page top).
    /// </summary>
    /// <remarks>
    /// Algorithm: filter chars whose midpoint falls inside the block, sort by
    /// mid-Y, then greedily split clusters when the gap between a char's mid-Y
    /// and the current cluster's median mid-Y exceeds a fraction of the median
    /// char height. Each cluster becomes one LineInfo whose height spans from
    /// the cluster's min-top to max-bottom — this includes ascenders, descenders,
    /// and sub/superscripts within the line.
    ///
    /// <para>
    /// When <paramref name="excludeOversizeSpanners"/> is set, glyphs taller than
    /// <see cref="OversizeGlyphFactor"/>× the median are lifted out before clustering
    /// and re-attached afterwards, so a drop cap (one glyph 2–3 lines tall) cannot
    /// be pulled into a line by its mid-Y and then inflate that line's band to its
    /// full height — which would make the downstream <see cref="NormalizeLines"/>
    /// overlap merge swallow every line the glyph spans. A lifted glyph that spans
    /// ≥2 detected lines is decoration: it widens only the topmost line's horizontal
    /// extent, never any line's vertical band. One that covers a single line (a tall
    /// operator, a raised cap) is folded back in fully so its line still bounds it.
    /// Left off for math, where a tall bracket spanning stacked rows should merge them.
    /// </para>
    /// </remarks>
    internal static List<LineInfo> DetectLinesFromChars(
        BBox bbox, IReadOnlyList<CharBox> charBoxes, float splitMultiplier = DefaultSplitMultiplier,
        bool excludeOversizeSpanners = false)
    {
        float left = bbox.X;
        float right = bbox.X + bbox.W;
        float top = bbox.Y;
        float bottom = bbox.Y + bbox.H;

        var chars = new List<GlyphBox>(charBoxes.Count);
        foreach (var c in charBoxes)
        {
            float h = c.Bottom - c.Top;
            if (h <= 0) continue; // skip whitespace / degenerate boxes

            float midX = (c.Left + c.Right) * 0.5f;
            float midY = (c.Top + c.Bottom) * 0.5f;
            if (midX < left || midX > right) continue;
            if (midY < top || midY > bottom) continue;

            chars.Add(new GlyphBox(midY, c.Top, c.Bottom, h, c.Left, c.Right));
        }

        if (chars.Count == 0) return [];

        var heightsSorted = chars.Select(c => c.Height).OrderBy(h => h).ToArray();
        float refHeight = heightsSorted[heightsSorted.Length / 2];
        if (refHeight <= 0) return [];

        // Lift out oversize spanning glyphs (drop caps) before clustering. Only when
        // some normal-height chars remain to define the real lines — an all-large run
        // (e.g. a heading) has no spanner to lift and clusters as-is.
        List<GlyphBox>? oversize = null;
        if (excludeOversizeSpanners)
        {
            float oversizeThreshold = refHeight * OversizeGlyphFactor;
            var normal = new List<GlyphBox>(chars.Count);
            foreach (var c in chars)
            {
                if (c.Height > oversizeThreshold) (oversize ??= []).Add(c);
                else normal.Add(c);
            }
            if (oversize is not null && normal.Count > 0) chars = normal;
            else oversize = null;
        }

        chars.Sort((a, b) => a.MidY.CompareTo(b.MidY));

        // Greedy clustering. Threshold generous (>= 1.0 × refHeight) so sub/
        // superscripts and inline math don't fragment a single visual line; math
        // blocks pass a larger multiplier to avoid splitting a stacked equation.
        float splitThreshold = refHeight * splitMultiplier;
        var bands = new List<Band>();
        int clusterStart = 0;
        float clusterSumMidY = chars[0].MidY;

        for (int i = 1; i < chars.Count; i++)
        {
            int clusterCount = i - clusterStart;
            float clusterAvgMidY = clusterSumMidY / clusterCount;
            if (chars[i].MidY - clusterAvgMidY > splitThreshold)
            {
                bands.Add(BandOf(chars, clusterStart, i));
                clusterStart = i;
                clusterSumMidY = chars[i].MidY;
            }
            else
            {
                clusterSumMidY += chars[i].MidY;
            }
        }
        bands.Add(BandOf(chars, clusterStart, chars.Count));

        // Re-attach the lifted oversize glyphs.
        if (oversize is not null)
        {
            foreach (var g in oversize)
            {
                int first = -1, span = 0;
                for (int k = 0; k < bands.Count; k++)
                    if (bands[k].OverlapsY(g))
                    {
                        if (first < 0) first = k;
                        span++;
                    }

                if (span >= 2)
                {
                    // Spans multiple lines → decoration (drop cap): grow only the
                    // topmost line's horizontal extent so its highlight reaches the
                    // glyph; never grow a line's vertical band.
                    var b = bands[first];
                    bands[first] = b with { Left = Math.Min(b.Left, g.Left), Right = Math.Max(b.Right, g.Right) };
                }
                else if (span == 1)
                {
                    // Belongs to a single line (tall operator / raised cap): fold it
                    // in fully so the line's band still bounds it.
                    var b = bands[first];
                    bands[first] = new Band(
                        Math.Min(b.Top, g.Top), Math.Max(b.Bottom, g.Bottom),
                        Math.Min(b.Left, g.Left), Math.Max(b.Right, g.Right));
                }
                else
                {
                    // Overlaps no detected line → a stand-alone glyph: its own line.
                    bands.Add(new Band(g.Top, g.Bottom, g.Left, g.Right));
                }
            }
            bands.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));
        }

        var lines = new List<LineInfo>(bands.Count);
        foreach (var b in bands)
            lines.Add(b.ToLineInfo());
        return lines;

        static Band BandOf(List<GlyphBox> sorted, int start, int endExclusive)
        {
            float minTop = float.PositiveInfinity, maxBottom = float.NegativeInfinity;
            float minLeft = float.PositiveInfinity, maxRight = float.NegativeInfinity;
            for (int i = start; i < endExclusive; i++)
            {
                if (sorted[i].Top < minTop) minTop = sorted[i].Top;
                if (sorted[i].Bottom > maxBottom) maxBottom = sorted[i].Bottom;
                if (sorted[i].Left < minLeft) minLeft = sorted[i].Left;
                if (sorted[i].Right > maxRight) maxRight = sorted[i].Right;
            }
            return new Band(minTop, maxBottom, minLeft, maxRight);
        }
    }

    /// <summary>
    /// Pixel-projection line detection: crops the block region of the page bitmap,
    /// computes per-row dark-pixel density, and finds runs above an adaptive
    /// threshold. Returns lines in page-point space.
    /// </summary>
    internal static List<LineInfo> DetectLinesFromPixels(
        LayoutBlock block, byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        int pxX = Math.Min((int)Math.Round(block.BBox.X / scaleX), imgW - 1);
        int pxY = Math.Min((int)Math.Round(block.BBox.Y / scaleY), imgH - 1);
        int pxW = Math.Min((int)Math.Round(block.BBox.W / scaleX), imgW - pxX);
        int pxH = Math.Min((int)Math.Round(block.BBox.H / scaleY), imgH - pxY);

        if (pxW == 0 || pxH == 0)
            return [];

        var densities = ComputeRowDensities(rgbBytes, imgW, pxX, pxY, pxW, pxH);
        var runs = FindLineRuns(densities);

        var lines = new List<LineInfo>(runs.Count);
        foreach (var run in runs)
        {
            float centerYPx = run.Start + run.Height / 2.0f;
            lines.Add(new LineInfo(block.BBox.Y + centerYPx * scaleY, run.Height * scaleY,
                block.BBox.X, block.BBox.W));
        }
        return lines;
    }

    internal static float[] ComputeRowDensities(byte[] rgbBytes, int imgW, int cropX, int cropY, int cropW, int cropH)
    {
        var profile = new float[cropH];
        for (int row = 0; row < cropH; row++)
        {
            int darkCount = 0;
            for (int col = 0; col < cropW; col++)
            {
                int pixelIdx = ((cropY + row) * imgW + (cropX + col)) * 3;
                if (pixelIdx + 2 < rgbBytes.Length)
                {
                    float r = rgbBytes[pixelIdx];
                    float g = rgbBytes[pixelIdx + 1];
                    float b = rgbBytes[pixelIdx + 2];
                    float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                    if (lum < LayoutConstants.DarkLuminanceThreshold)
                        darkCount++;
                }
            }
            profile[row] = (float)darkCount / cropW;
        }

        // Smooth with radius-1 moving average
        var smoothed = new float[cropH];
        for (int r = 0; r < cropH; r++)
        {
            int start = Math.Max(r - 1, 0);
            int end = Math.Min(r + 2, cropH);
            float sum = 0;
            for (int k = start; k < end; k++) sum += profile[k];
            smoothed[r] = sum / (end - start);
        }
        return smoothed;
    }

    /// <summary>
    /// Detects line runs using adaptive density thresholding with recovery for short lines.
    /// The primary pass uses a density-fraction threshold (15% of average) which reliably
    /// segments dense text. A second recovery pass scans any uncovered regions at the top
    /// and bottom of the block with a low absolute threshold to catch short lines (e.g. the
    /// last few words of a paragraph) that fall below the density-fraction threshold.
    /// </summary>
    internal static List<(int Start, int Height)> FindLineRuns(float[] densities)
    {
        // Primary pass: density-fraction threshold — works well for dense text
        float nonZeroSum = 0;
        int nonZeroCount = 0;
        foreach (var v in densities)
        {
            if (v > 0.005f) { nonZeroSum += v; nonZeroCount++; }
        }
        float threshold = nonZeroCount == 0
            ? 0.005f
            : Math.Max(nonZeroSum / nonZeroCount * LayoutConstants.DensityThresholdFraction, 0.005f);

        var runs = FindRunsAboveThreshold(densities, threshold);

        if (runs.Count == 0) return runs;

        // Recovery pass: check for uncovered regions at top and bottom of the block.
        // If the first/last detected line is far from the block edge, re-scan that
        // region with a low absolute threshold to catch short lines.
        int medianHeight = runs.Select(r => r.Height).OrderBy(h => h).ElementAt(runs.Count / 2);
        float recoveryThreshold = 0.005f;

        // Top recovery: region before the first detected line
        int firstLineStart = runs[0].Start;
        if (firstLineStart > medianHeight / 2)
        {
            var topRuns = FindRunsAboveThreshold(densities[..firstLineStart], recoveryThreshold);
            runs.InsertRange(0, topRuns);
        }

        // Bottom recovery: region after the last detected line
        var lastRun = runs[^1];
        int lastLineEnd = lastRun.Start + lastRun.Height;
        int remaining = densities.Length - lastLineEnd;
        if (remaining > medianHeight / 2)
        {
            var bottomRuns = FindRunsAboveThreshold(densities[lastLineEnd..], recoveryThreshold);
            runs.AddRange(bottomRuns.Select(r => (r.Start + lastLineEnd, r.Height)));
        }

        return runs;
    }

    private static List<(int Start, int Height)> FindRunsAboveThreshold(float[] densities, float threshold)
    {
        var runs = new List<(int Start, int Height)>();
        int? runStart = null;

        for (int r = 0; r < densities.Length; r++)
        {
            if (densities[r] > threshold)
            {
                runStart ??= r;
            }
            else if (runStart is not null)
            {
                int runH = r - runStart.Value;
                if (runH >= LayoutConstants.MinLineHeightPx)
                    runs.Add((runStart.Value, runH));
                runStart = null;
            }
        }
        if (runStart is not null)
        {
            int runH = densities.Length - runStart.Value;
            if (runH >= LayoutConstants.MinLineHeightPx)
                runs.Add((runStart.Value, runH));
        }
        return runs;
    }
}
