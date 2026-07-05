using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Detects rotated-text blocks (sideways tables, rotated figures' captions,
/// whole-page sideways scans) so downstream consumers can collapse them to
/// atomic rail units, rotate VLM crops upright, or offer rotate-to-read.
///
/// Primary signal: the majority displayed glyph angle of the char boxes inside
/// the block (<see cref="CharBox.Angle"/>, quarter-turn-normalised clockwise
/// degrees). Fallback for pages with no text layer: a pixel-projection
/// heuristic comparing the row- vs column-density profile structure of the
/// block's raster — upright text lines produce a strongly banded ROW profile,
/// 90°-rotated text a banded COLUMN profile.
/// </summary>
public static class OrientationDetector
{
    /// <summary>Minimum glyphs inside a block before the angle vote is trusted.</summary>
    private const int MinChars = 4;

    /// <summary>Fraction of in-block glyphs the winning angle must reach.</summary>
    private const float MajorityFraction = 0.6f;

    /// <summary>
    /// Row/column profile-variance dominance required before the pixel fallback
    /// flags a block as sideways. Conservative on purpose: a false "sideways"
    /// collapses real lines into one atomic unit.
    /// </summary>
    private const double PixelVarianceDominance = 4.0;

    /// <summary>
    /// Sets <see cref="LayoutBlock.UprightTurns"/> for every block. Glyph-angle
    /// evidence overrides any model-seeded value (e.g. PP-DocLayoutV3's
    /// vertical_text class); blocks without enough glyphs keep their seed or
    /// fall back to the pixel heuristic. Pure-visual roles (figures, charts)
    /// are skipped — rotation is meaningless for them.
    /// </summary>
    public static void DetectBlockOrientations(
        List<LayoutBlock> blocks,
        IReadOnlyList<CharBox>? charBoxes,
        byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        foreach (var block in blocks)
        {
            if (LineDetector.AtomicLineRoles.Contains(block.Role)) continue;

            if (DetectFromChars(block.BBox, charBoxes) is { } turns)
                block.UprightTurns = turns;
            else if (block.UprightTurns == 0 &&
                     DetectSidewaysFromPixels(block.BBox, rgbBytes, imgW, imgH, scaleX, scaleY))
                block.UprightTurns = 1; // projection can't tell 90° from 270°; 1 (CCW-rotated
                                        // content, the common sideways-table case) is the default
        }
    }

    /// <summary>
    /// Majority vote over the displayed glyph angles inside the block. Returns
    /// the clockwise quarter-turns needed to make the dominant angle upright,
    /// or null when there aren't enough glyphs / no angle dominates.
    /// </summary>
    internal static int? DetectFromChars(BBox bbox, IReadOnlyList<CharBox>? charBoxes)
    {
        if (charBoxes is not { Count: > 0 }) return null;

        Span<int> counts = stackalloc int[4];
        int total = 0;
        foreach (var c in charBoxes)
        {
            if (c.Right <= c.Left || c.Bottom <= c.Top) continue;
            float midX = (c.Left + c.Right) / 2f;
            float midY = (c.Top + c.Bottom) / 2f;
            if (midX < bbox.X || midX > bbox.X + bbox.W || midY < bbox.Y || midY > bbox.Y + bbox.H)
                continue;

            int bucket = ((int)Math.Round(c.Angle / 90f) % 4 + 4) % 4;
            counts[bucket]++;
            total++;
        }

        if (total < MinChars) return null;

        int best = 0;
        for (int i = 1; i < 4; i++)
            if (counts[i] > counts[best]) best = i;
        if (counts[best] < total * MajorityFraction) return null;

        // A glyph displayed at (90*best)° clockwise reads upright after the
        // remaining clockwise turns to complete the circle.
        return (4 - best) % 4;
    }

    /// <summary>
    /// Text-layer-free fallback: within the block's raster region, compare the
    /// structural variance of the per-row vs per-column dark-pixel densities.
    /// Horizontal text lines alternate dark bands and gaps down the page (high
    /// row variance, smooth columns); 90°-rotated text is the transpose. Only
    /// flags sideways when column variance dominates by
    /// <see cref="PixelVarianceDominance"/>.
    /// </summary>
    internal static bool DetectSidewaysFromPixels(
        BBox bbox, byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        if (imgW <= 0 || imgH <= 0 || rgbBytes.Length < imgW * imgH * 3 ||
            scaleX <= 0 || scaleY <= 0) return false;

        int x0 = Math.Clamp((int)(bbox.X / scaleX), 0, imgW - 1);
        int x1 = Math.Clamp((int)((bbox.X + bbox.W) / scaleX), 0, imgW - 1);
        int y0 = Math.Clamp((int)(bbox.Y / scaleY), 0, imgH - 1);
        int y1 = Math.Clamp((int)((bbox.Y + bbox.H) / scaleY), 0, imgH - 1);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        if (w < 16 || h < 16) return false;

        var rowDensity = new double[h];
        var colDensity = new double[w];
        for (int y = y0; y <= y1; y++)
        {
            int rowBase = y * imgW;
            for (int x = x0; x <= x1; x++)
            {
                int p = (rowBase + x) * 3;
                if (rgbBytes[p] + rgbBytes[p + 1] + rgbBytes[p + 2] < 384)
                {
                    rowDensity[y - y0] += 1;
                    colDensity[x - x0] += 1;
                }
            }
        }
        for (int i = 0; i < h; i++) rowDensity[i] /= w;
        for (int i = 0; i < w; i++) colDensity[i] /= h;

        double rowVar = Variance(rowDensity);
        double colVar = Variance(colDensity);
        // Require real ink and decisive dominance before overriding the default.
        return colVar > 1e-4 && colVar > rowVar * PixelVarianceDominance;
    }

    private static double Variance(double[] values)
    {
        double mean = 0;
        foreach (var v in values) mean += v;
        mean /= values.Length;
        double var = 0;
        foreach (var v in values) var += (v - mean) * (v - mean);
        return var / values.Length;
    }
}
