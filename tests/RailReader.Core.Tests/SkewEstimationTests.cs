using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Model-free tests for skew measurement: <see cref="RapidOcrService.QuadMetrics"/> reading a
/// baseline out of a detection quad, and <see cref="SkewEstimator"/> aggregating those into a
/// page angle. Both are pure geometry, so the expected values are exact rather than eyeballed
/// and none of this needs the ONNX models.
/// </summary>
public class SkewEstimationTests
{
    private static float Rad(float degrees) => degrees * MathF.PI / 180f;
    private static float Deg(float radians) => radians * 180f / MathF.PI;

    /// <summary>
    /// Corners of a <paramref name="w"/>×<paramref name="h"/> rectangle rotated clockwise by
    /// <paramref name="degrees"/>, in clockwise cyclic order — the shape the detector's
    /// minimum-area rectangle produces. Rounded to integers because <see cref="SKPointI"/> is
    /// what the engine actually hands back, so the tests carry the same quantisation error the
    /// real code does.
    /// </summary>
    private static SKPointI[] Quad(float cx, float cy, float w, float h, float degrees)
    {
        float t = Rad(degrees);
        float ca = MathF.Cos(t), sa = MathF.Sin(t);
        (float X, float Y)[] local = [(-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2)];

        var pts = new SKPointI[4];
        for (int i = 0; i < 4; i++)
        {
            float x = local[i].X * ca - local[i].Y * sa + cx;
            float y = local[i].X * sa + local[i].Y * ca + cy;
            pts[i] = new SKPointI((int)MathF.Round(x), (int)MathF.Round(y));
        }
        return pts;
    }

    // Integer corners on an 800 px quad bound the recoverable angle to a fraction of a degree.
    private const float AngleToleranceDeg = 0.2f;

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(5f)]
    [InlineData(-3f)]
    [InlineData(-5f)]
    public void QuadMetrics_RecoversTheRotationAngle(float degrees)
    {
        var (angle, height) = RapidOcrService.QuadMetrics(Quad(500, 500, 800, 40, degrees));

        Assert.Equal(degrees, Deg(angle), AngleToleranceDeg);
        Assert.Equal(40f, height, 1.5f);
    }

    [Fact]
    public void QuadMetrics_HeightIsTheTrueHeightNotTheInflatedAxisAlignedOne()
    {
        // This is the whole point of measuring the quad: at 5 degrees an 800-wide line's
        // axis-aligned box is ~110 px tall, nearly three times its real 40 px height, and that
        // inflation is what makes neighbouring lines' bands overlap and fuse.
        var quad = Quad(500, 500, 800, 40, 5f);
        var (_, height) = RapidOcrService.QuadMetrics(quad);

        int minY = quad.Min(p => p.Y), maxY = quad.Max(p => p.Y);
        float axisAlignedHeight = maxY - minY;

        Assert.True(axisAlignedHeight > 100f, $"expected an inflated AABB, got {axisAlignedHeight}");
        Assert.Equal(40f, height, 1.5f);
    }

    [Fact]
    public void QuadMetrics_IsIndependentOfWhichCornerComesFirst()
    {
        // The engine guarantees cyclic hull order but not a starting corner, so all four
        // rotations of the same quad must agree.
        var baseQuad = Quad(500, 500, 800, 40, 3f);
        var (expectedAngle, expectedHeight) = RapidOcrService.QuadMetrics(baseQuad);

        for (int shift = 1; shift < 4; shift++)
        {
            var rotated = new SKPointI[4];
            for (int i = 0; i < 4; i++) rotated[i] = baseQuad[(i + shift) % 4];

            var (angle, height) = RapidOcrService.QuadMetrics(rotated);
            Assert.Equal(Deg(expectedAngle), Deg(angle), 0.001f);
            Assert.Equal(expectedHeight, height, 0.001f);
        }
    }

    [Fact]
    public void QuadMetrics_IsIndependentOfWindingDirection()
    {
        var cw = Quad(500, 500, 800, 40, 3f);
        var ccw = cw.Reverse().ToArray();

        var (cwAngle, cwHeight) = RapidOcrService.QuadMetrics(cw);
        var (ccwAngle, ccwHeight) = RapidOcrService.QuadMetrics(ccw);

        Assert.Equal(Deg(cwAngle), Deg(ccwAngle), 0.001f);
        Assert.Equal(cwHeight, ccwHeight, 0.001f);
    }

    [Fact]
    public void QuadMetrics_ReportsNothingForDegenerateInput()
    {
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(null!));
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics([]));
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics([new(0, 0), new(1, 0), new(1, 1)]));
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(Quad(100, 100, 0, 0, 0f)));
    }

    [Fact]
    public void QuadMetrics_ReportsNothingForATooSquareQuad()
    {
        // A lone glyph or a single-character CJK line: there is no baseline to read, and
        // guessing one would let noise vote in the page estimate.
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(Quad(500, 500, 40, 40, 3f)));
    }

    [Fact]
    public void QuadMetrics_ReportsNothingForSidewaysText()
    {
        // Past the per-quad tilt cap this is a rotated line, which the quarter-turn
        // block-orientation machinery owns. Reporting nothing keeps deskew and rotation apart.
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(Quad(500, 500, 800, 40, 90f)));
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(Quad(500, 500, 800, 40, -70f)));
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(Quad(500, 500, 800, 40, 20f)));
    }

    [Fact]
    public void QuadMetrics_StillReportsBeyondThePageLevelRange()
    {
        // The per-quad guard is deliberately looser than the ±5° page gate: a genuinely
        // 8-degree page's lines must reach the estimator, so it can see they agree and then
        // reject the page for being out of range — rather than silently discarding them and
        // reporting confidence in whatever tail survived.
        var (angle, height) = RapidOcrService.QuadMetrics(Quad(500, 500, 800, 40, 8f));

        Assert.Equal(8f, Deg(angle), AngleToleranceDeg);
        Assert.True(height > 0f);
    }

    [Fact]
    public void QuadMetrics_ReportsNothingForAVeryShortQuad()
    {
        Assert.Equal((0f, 0f), RapidOcrService.QuadMetrics(Quad(500, 500, 8, 3, 3f)));
    }

    // ---- aggregation ----

    private static OcrLine Line(float angleDeg, float width = 400f, float height = 20f)
        => new(new BBox(0, 0, width, height), Angle: Rad(angleDeg), TrueHeight: height);

    [Fact]
    public void Estimate_AgreesWithConsistentLines()
    {
        var lines = Enumerable.Range(0, 10).Select(_ => Line(2f)).ToList();
        Assert.Equal(2f, Deg(SkewEstimator.Estimate(lines)), 0.001f);
    }

    [Fact]
    public void Estimate_ReturnsZeroBelowTheLineMinimum()
    {
        var lines = Enumerable.Range(0, 7).Select(_ => Line(2f)).ToList();
        Assert.Equal(0f, SkewEstimator.Estimate(lines));
        Assert.Equal(0f, SkewEstimator.Estimate(null));
        Assert.Equal(0f, SkewEstimator.Estimate([]));
    }

    [Fact]
    public void Estimate_ReturnsZeroWhenLinesDisagree()
    {
        // Not a rigid page rotation — something else is being measured, so the median would be
        // meaningless and we must not act on it.
        float[] spread = [-4f, -3f, -2f, 0f, 1f, 2f, 3f, 4f, -1f, 3.5f];
        Assert.Equal(0f, SkewEstimator.Estimate(spread.Select(a => Line(a)).ToList()));
    }

    [Fact]
    public void Estimate_IgnoresLinesWithNoMeasuredQuad()
    {
        // TrueHeight == 0 marks "could not measure". Those lines report Angle 0, and counting
        // them would be a silent vote for uprightness.
        var lines = new List<OcrLine>();
        for (int i = 0; i < 12; i++) lines.Add(new OcrLine(new BBox(0, 0, 400, 20)));  // unmeasured
        for (int i = 0; i < 8; i++) lines.Add(Line(2f));

        Assert.Equal(2f, Deg(SkewEstimator.Estimate(lines)), 0.001f);
    }

    [Fact]
    public void Estimate_IgnoresLinesTooShortToMeasure()
    {
        var lines = Enumerable.Range(0, 10).Select(_ => Line(2f, width: 12f)).ToList();
        Assert.Equal(0f, SkewEstimator.Estimate(lines));
    }

    [Fact]
    public void Estimate_WeightsLongLinesMoreHeavily()
    {
        // Five short fragments say 0.5, five full body lines say 2.5. An unweighted median
        // would land between them; the body lines are the better evidence of how the sheet
        // actually sat on the platen, so the answer should be theirs.
        var lines = new List<OcrLine>();
        for (int i = 0; i < 5; i++) lines.Add(Line(0.5f, width: 50f));
        for (int i = 0; i < 5; i++) lines.Add(Line(2.5f, width: 1000f));

        Assert.Equal(2.5f, Deg(SkewEstimator.Estimate(lines)), 0.001f);
    }

    [Fact]
    public void Estimate_ReturnsZeroBeyondTheSupportedRange()
    {
        // A consistent 8-degree page: the lines agree, so this is not rejected for noise — it
        // is rejected because correcting it is outside what we claim to be confident about.
        // Rejecting beats clamping, which would apply a large wrong shear to every band.
        var lines = Enumerable.Range(0, 10).Select(_ => Line(8f)).ToList();
        Assert.Equal(0f, SkewEstimator.Estimate(lines));
    }

    [Fact]
    public void Estimate_SnapsNearUprightPagesToExactlyZero()
    {
        // The dead band. Integer quad corners mean a square page measures as a small non-zero
        // angle; returning it would shift every band by a rounding artefact. Bitwise zero is
        // the invariant the whole "no change at angle 0" guarantee rests on.
        Assert.True(SkewEstimator.Estimate(Enumerable.Range(0, 10).Select(_ => Line(0f)).ToList()) == 0f);
        Assert.True(SkewEstimator.Estimate(Enumerable.Range(0, 10).Select(_ => Line(0.1f)).ToList()) == 0f);
        Assert.True(SkewEstimator.Estimate(Enumerable.Range(0, 10).Select(_ => Line(-0.1f)).ToList()) == 0f);
    }
}
