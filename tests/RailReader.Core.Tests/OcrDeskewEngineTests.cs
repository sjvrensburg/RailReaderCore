using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Engine-level verification of deskew. Everything in <c>SkewEstimationTests</c> and
/// <c>DeskewLineGroupingTests</c> is our own geometry checked against itself; these tests are
/// the ones that prove the premise the whole feature rests on — that the <b>real</b> detector's
/// output carries the page's skew, and that recovering it fixes line grouping on a page a real
/// OCR pass produced.
///
/// <para>
/// Rendered at the tight ~1.05× line pitch <see cref="OcrLineSegmentationRegressionTests"/>
/// uses, because that is what makes skew bite: at generous spacing a line can drift a long way
/// before it reaches its neighbour's band, and the bug does not reproduce.
/// </para>
/// </summary>
public class OcrDeskewEngineTests
{
    private static readonly string[] Paragraph =
    [
        "This is the first line of a tightly set paragraph",
        "and this is the second line right below it here",
        "followed by a third line with similar content now",
        "then a fourth line continuing the same block of text",
        "a fifth line to give the estimator enough evidence",
        "a sixth line so the confidence gate is satisfied too",
        "a seventh line of ordinary prose for good measure",
        "and finally an eighth and last line to close it out",
    ];

    /// <summary>
    /// Renders <paramref name="lines"/> rotated clockwise by <paramref name="degrees"/> about
    /// the canvas centre — a synthetic scan placed crooked on the platen. The canvas is roomy
    /// so rotation cannot clip the text, which would cost detections and confound the counts.
    /// </summary>
    private static (byte[] Rgb, int W, int H) RenderSkewed(string[] lines, float degrees,
        float textSize = 44f, int width = 1100, int height = 800)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-width / 2f, -height / 2f);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, textSize);
        for (int i = 0; i < lines.Length; i++)
            canvas.DrawText(lines[i], 60f, 200f + i * (textSize * 1.05f), font, paint);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var rgb = new byte[width * height * 3];
        var pixels = bitmap.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = pixels[i].Red;
            rgb[i * 3 + 1] = pixels[i].Green;
            rgb[i * 3 + 2] = pixels[i].Blue;
        }
        return (rgb, width, height);
    }

    private static float Deg(float radians) => radians * 180f / MathF.PI;

    [OcrModelTheory]
    [InlineData(1.5f)]
    [InlineData(2.5f)]
    [InlineData(-2f)]
    public void Estimator_RecoversTheRenderedAngleFromRealDetections(float degrees)
    {
        // The load-bearing test: our math reads an angle out of quads the engine actually
        // produced, not out of quads we constructed to be readable.
        var (rgb, w, h) = RenderSkewed(Paragraph, degrees);
        using var ocr = new RapidOcrService();

        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);

        Assert.Equal(degrees, Deg(page.SkewAngle), 0.5f);
    }

    [OcrModelFact]
    public void Estimator_ReportsNothingForAnUprightPage()
    {
        var (rgb, w, h) = RenderSkewed(Paragraph, 0f);
        using var ocr = new RapidOcrService();

        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);

        // The dead band, against real detector noise rather than synthetic exactness.
        Assert.True(page.SkewAngle == 0f, $"expected an exact zero, got {Deg(page.SkewAngle)}°");
    }

    [OcrModelFact]
    public void DetectionQuadsCarryATrueHeightWellBelowTheirAxisAlignedOne()
    {
        var (rgb, w, h) = RenderSkewed(Paragraph, 2.5f);
        using var ocr = new RapidOcrService();

        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);
        var measured = page.Lines.Where(l => l.TrueHeight > 0f && l.Box.W > 200f).ToList();

        Assert.NotEmpty(measured);
        foreach (var line in measured)
            Assert.True(line.TrueHeight < line.Box.H,
                $"true height {line.TrueHeight} should undercut the inflated {line.Box.H}");
    }

    private static (int Corrected, int Uncorrected) GroupBothWays(float degrees)
    {
        var (rgb, w, h) = RenderSkewed(Paragraph, degrees);
        using var ocr = new RapidOcrService();
        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);
        var (pageText, _, skew) = OcrPageMapper.ToPageSpace(page, 1f, 1f);
        Assert.NotNull(pageText);

        var bbox = new BBox(0, 0, w, h);
        var chars = pageText!.DedupedCharBoxes;

        return (LineDetector.DetectLinesFromChars(bbox, chars,
                    skewTan: MathF.Tan(skew), pivotX: bbox.X + bbox.W / 2f).Count,
                LineDetector.DetectLinesFromChars(bbox, chars).Count);
    }

    [OcrModelTheory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(2.5f)]
    [InlineData(3.5f)]
    [InlineData(-3f)]
    public void SkewedPage_RecoversOneBandPerPrintedLine(float degrees)
    {
        // Real OCR is not pixel-perfect, so this is a range around the eight rendered lines
        // rather than an exact count. It has to hold at every angle, including the ones mild
        // enough that grouping would have coped anyway — the correction must never cost a line
        // it would otherwise have found.
        var (corrected, _) = GroupBothWays(degrees);

        Assert.InRange(corrected, Paragraph.Length - 1, Paragraph.Length + 1);
    }

    [OcrModelTheory]
    [InlineData(2.5f)]
    [InlineData(3.5f)]
    public void SkewedPage_LosesLinesWithoutTheCorrection(float degrees)
    {
        // The paired half: the same char boxes grouped without the shear merge into fewer
        // bands than there are printed lines. This is what makes the test above evidence of a
        // fix rather than of a lenient tolerance.
        //
        // Note the angles here start higher than the theory above. Merging is not a function
        // of skew alone — a line only reaches its neighbour's band once its drift across the
        // column, width × tan(θ), exceeds the median glyph height that sets the split
        // threshold. At this text size (~30 px glyphs over a ~900 px column) that crossover
        // sits near 2°, so at 1.5° the uncorrected path still finds all eight lines and there
        // is no regression to demonstrate. Tightly-set body text, which has a far smaller
        // glyph height relative to its column width, crosses over well below a degree — which
        // is why the real-world report reproduced at a skew this synthetic page shrugs off.
        var (corrected, uncorrected) = GroupBothWays(degrees);

        Assert.True(uncorrected < corrected,
            $"expected merging without deskew at {degrees}°: {uncorrected} vs {corrected}");
    }
}
