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
/// <b>Skew misgroups in two opposite directions</b>, and these tests are written not to care
/// which. Once a line's drift across the column, <c>width × tan(θ)</c>, passes the median glyph
/// height, that single line splits into several bands; once it passes the line <i>pitch</i>,
/// neighbouring lines interleave and merge instead. Long lines reach the first threshold first
/// and produce <i>too many</i> bands; short tightly-set lines reach the second and produce too
/// few — which is the form the reported scan took. Assertions are therefore stated as distance
/// from the detector's own line count, never as a direction.
/// </para>
/// <para>
/// <b>Long lines at ordinary spacing</b>, deliberately not the ~1.05× pitch
/// <see cref="OcrLineSegmentationRegressionTests"/> renders at. Skew bites when a line's drift
/// across the column, <c>width × tan(θ)</c>, exceeds the line pitch — so the way to make it
/// bite without also starving the split threshold is to make the lines <i>long</i>, not to
/// crowd them. Crowding puts glyph ink height and line pitch within a few pixels of each other,
/// where whether two lines separate depends on the host's font metrics; that compounds the
/// tightening regression with this one and makes the result differ between a CI image and a
/// developer's machine. At ~1600&#160;px lines and 1.4× pitch, drift passes pitch by 2.5° while
/// the pitch stays comfortably above any plausible ink height.
/// </para>
/// </summary>
public class OcrDeskewEngineTests
{
    private static readonly string[] Paragraph =
    [
        "This is the first line of a paragraph set wide enough that skew has room to bite",
        "and this is the second line running right below it at the very same generous width",
        "followed by a third line carrying similar content across the full column once more",
        "then a fourth line continuing the same block of ordinary prose without interruption",
        "a fifth line included so that the page level estimator has ample evidence to work on",
        "a sixth line so that the confidence gate sees far more than its minimum sample count",
        "a seventh line of unremarkable text present purely to lengthen the rendered paragraph",
        "and finally an eighth and last line to close out the block and end the page cleanly",
    ];

    /// <summary>
    /// Renders <paramref name="lines"/> rotated clockwise by <paramref name="degrees"/> about
    /// the canvas centre — a synthetic scan placed crooked on the platen. The canvas is roomy
    /// so rotation cannot clip the text, which would cost detections and confound the counts.
    /// </summary>
    private static (byte[] Rgb, int W, int H) RenderSkewed(string[] lines, float degrees,
        float textSize = 40f, int width = 1900, int height = 900)
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
            canvas.DrawText(lines[i], 60f, 200f + i * (textSize * 1.4f), font, paint);

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

    /// <summary>
    /// Groups the rendered page's OCR char boxes with and without the correction, alongside the
    /// detector's own count of the text lines it segmented.
    ///
    /// <para>
    /// That detector count — not the number of strings we asked Skia to draw — is the reference
    /// these tests measure against, because the two can legitimately differ: the font backing
    /// <see cref="SKTypeface.Default"/> is whatever the host provides, so a CI image and a
    /// developer's machine render the same strings at different metrics and OCR finds a
    /// different number of lines in the result. Asserting against the rendered count makes the
    /// test a claim about the host's fonts; asserting against the detector's makes it a claim
    /// about grouping, which is what is under test. The two are independent measurements —
    /// detection segments line <i>regions</i> from pixels, grouping reconstructs lines from
    /// <i>character</i> boxes by clustering — so their agreement is meaningful.
    /// </para>
    /// </summary>
    private static (int Detected, int Corrected, int Uncorrected) GroupBothWays(float degrees)
    {
        var (rgb, w, h) = RenderSkewed(Paragraph, degrees);
        using var ocr = new RapidOcrService();
        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);
        var (pageText, _, skew) = OcrPageMapper.ToPageSpace(page, 1f, 1f);
        Assert.NotNull(pageText);

        // Guard against a vacuous pass: if the host rendered something OCR could barely read,
        // every count below would agree at some uselessly small number. Fail loudly instead.
        int detected = page.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Text));
        Assert.InRange(detected, 5, Paragraph.Length);

        var bbox = new BBox(0, 0, w, h);
        var chars = pageText!.DedupedCharBoxes;

        return (detected,
                LineDetector.DetectLinesFromChars(bbox, chars,
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
        // Grouping should land on the same lines the detector segmented, give or take one for
        // OCR not being pixel-perfect. This has to hold at every angle, including the ones mild
        // enough that grouping would have coped anyway — the correction must never cost a line
        // it would otherwise have found.
        var (detected, corrected, _) = GroupBothWays(degrees);

        Assert.InRange(corrected, detected - 1, detected + 1);
    }

    [OcrModelTheory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(2.5f)]
    [InlineData(3.5f)]
    [InlineData(-3f)]
    public void Correction_IsNeverFurtherFromTheTruthThanNoCorrection(float degrees)
    {
        // Stated as distance from the detector's count rather than as a direction, because
        // skew misgroups BOTH ways and which one shows up depends on the page's proportions
        // (see the class remarks). A directional assertion passes on one geometry and fails on
        // another for reasons that have nothing to do with the correction being right.
        var (detected, corrected, uncorrected) = GroupBothWays(degrees);

        Assert.True(Math.Abs(corrected - detected) <= Math.Abs(uncorrected - detected),
            $"at {degrees}°: corrected {corrected}, uncorrected {uncorrected}, detected {detected}");
    }

    [OcrModelTheory]
    [InlineData(2.5f)]
    [InlineData(3.5f)]
    [InlineData(-4f)]
    public void SkewedPage_IsMisgroupedWithoutTheCorrection(float degrees)
    {
        // The paired half: without the shear the same char boxes land on a materially wrong
        // number of lines. This is what makes the recovery test evidence of a fix rather than
        // of a lenient tolerance.
        //
        // These angles are steeper than the recovery theory uses because misgrouping has a
        // threshold: nothing goes wrong until the drift across a line, width × tan(θ), passes
        // either the glyph height (fragmenting one line into several) or the line pitch
        // (merging neighbours). On this page the first crossover sits near 2°. Real book text,
        // whose glyphs are far smaller relative to the column, crosses below 1° — which is why
        // the reported scan reproduced at a skew a synthetic page at this text size shrugs off.
        var (detected, corrected, uncorrected) = GroupBothWays(degrees);

        Assert.True(Math.Abs(uncorrected - detected) > Math.Abs(corrected - detected),
            $"expected misgrouping without deskew at {degrees}°: " +
            $"uncorrected {uncorrected}, corrected {corrected}, detected {detected}");
    }
}
