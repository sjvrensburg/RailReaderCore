using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// The point of deskew, tested without an OCR engine: synthetic glyph boxes laid out as a
/// skewed page, clustered with and without the correction.
///
/// <para>
/// The geometry here is the real-world failure from railreader2#209 in miniature — tightly
/// spaced body text (line pitch only 1.4× the glyph height, as in single-spaced book text) on a
/// page a couple of degrees off square. At that spacing a line's own vertical drift across the
/// column exceeds the gap to its neighbour, so sorting by raw mid-Y interleaves glyphs from
/// different printed lines and the greedy split can no longer find the boundaries between them.
/// </para>
/// </summary>
public class DeskewLineGroupingTests
{
    private const int LineCount = 12;
    private const int GlyphsPerLine = 40;
    private const float GlyphWidth = 8f, GlyphAdvance = 10f, GlyphHeight = 10f;
    private const float LinePitch = 14f, FirstBaseline = 100f;
    private const float BlockX = 0f, BlockW = GlyphsPerLine * GlyphAdvance;   // 400
    private const float PivotX = BlockX + BlockW / 2f;

    private static readonly BBox Block = new(BlockX, 80f, BlockW, 200f);

    /// <summary>Glyph boxes for a paragraph rotated by <paramref name="degrees"/> about the block centre.</summary>
    private static List<CharBox> SkewedParagraph(float degrees)
    {
        float tan = MathF.Tan(degrees * MathF.PI / 180f);
        var boxes = new List<CharBox>(LineCount * GlyphsPerLine);
        int index = 0;

        for (int line = 0; line < LineCount; line++)
        {
            float baseline = FirstBaseline + LinePitch * line;
            for (int g = 0; g < GlyphsPerLine; g++)
            {
                float left = BlockX + g * GlyphAdvance;
                float centreX = left + GlyphWidth / 2f;
                float centreY = baseline + (centreX - PivotX) * tan;
                boxes.Add(new CharBox(index++, left, centreY - GlyphHeight / 2f,
                    left + GlyphWidth, centreY + GlyphHeight / 2f));
            }
        }
        return boxes;
    }

    private static float TanOf(float degrees) => MathF.Tan(degrees * MathF.PI / 180f);

    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(-2f)]
    public void SkewedParagraph_ClustersIntoOneBandPerPrintedLine(float degrees)
    {
        var chars = SkewedParagraph(degrees);

        var corrected = LineDetector.DetectLinesFromChars(
            Block, chars, skewTan: TanOf(degrees), pivotX: PivotX);

        Assert.Equal(LineCount, corrected.Count);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(3f)]
    public void SkewedParagraph_CollapsesWithoutTheCorrection(float degrees)
    {
        // The paired half of the test above: without the shear the same glyphs recover
        // materially fewer bands than there are printed lines. Asserting the failure is what
        // makes the passing case evidence of a fix rather than of a lenient tolerance.
        var chars = SkewedParagraph(degrees);

        var uncorrected = LineDetector.DetectLinesFromChars(Block, chars);

        Assert.True(uncorrected.Count < LineCount,
            $"expected fewer than {LineCount} bands without deskew at {degrees}°, got {uncorrected.Count}");
    }

    [Fact]
    public void UnskewedParagraph_IsUnaffectedByTheCorrectionPath()
    {
        // A square page must produce identical output whether or not the feature is compiled
        // in: at tan == 0 the shear is the exact identity, not an approximation of it.
        var chars = SkewedParagraph(0f);

        var without = LineDetector.DetectLinesFromChars(Block, chars);
        var with = LineDetector.DetectLinesFromChars(Block, chars, skewTan: 0f, pivotX: PivotX);

        Assert.Equal(LineCount, without.Count);
        Assert.Equal(without, with);
    }

    [Fact]
    public void EndToEnd_SkewedBlockKeepsItsLinesThroughNormalisation()
    {
        // DetectLinesFromChars alone is only half the path: NormalizeLines' 50%-overlap merge
        // would otherwise re-fuse the very bands the clustering just separated, because two
        // adjacent lines on a skewed page genuinely do overlap in raw page-Y.
        var chars = SkewedParagraph(2f);
        var block = new LayoutBlock
        {
            BBox = Block,
            Role = BlockRole.Text,
        };

        var lines = LineDetector.DetectLines(block, chars, rgbBytes: [], imgW: 0, imgH: 0,
            scaleX: 1f, scaleY: 1f, skewTan: TanOf(2f));

        Assert.Equal(LineCount, lines.Count);
        // Bands must come back sorted and non-overlapping in the ordinary page space every
        // downstream consumer reads them in.
        for (int i = 1; i < lines.Count; i++)
            Assert.True(lines[i].Y > lines[i - 1].Y, "bands should be ordered top-to-bottom");
    }

    [Fact]
    public void RotatedTextBlock_IgnoresSkewEntirely()
    {
        // Deskew and the quarter-turn rotation machinery must never fight: a sideways block
        // collapses to its single atomic line before any sheared code can run.
        var chars = SkewedParagraph(2f);
        var block = new LayoutBlock { BBox = Block, Role = BlockRole.Text, UprightTurns = 1 };

        var straight = LineDetector.DetectLines(block, chars, [], 0, 0, 1f, 1f);
        var sheared = LineDetector.DetectLines(block, chars, [], 0, 0, 1f, 1f, skewTan: TanOf(2f));

        Assert.Single(sheared);
        Assert.Equal(straight, sheared);
    }

    [Fact]
    public void DeskewY_IsExactlyTheIdentityAtZeroSkew()
    {
        foreach (float x in new[] { -1e4f, -13.7f, 0f, 0.5f, 913f, 1e5f })
            foreach (float y in new[] { -8081f, -0.25f, 0f, 42f, 1e6f })
            {
                Assert.True(LineDetector.DeskewY(x, y, 200f, 0f) == y);
                Assert.True(LineDetector.ReskewY(x, y, 200f, 0f) == y);
            }
    }

    [Fact]
    public void DeskewY_AndReskewY_RoundTrip()
    {
        float tan = TanOf(3f);
        float deskewed = LineDetector.DeskewY(x: 380f, y: 210f, pivotX: PivotX, skewTan: tan);

        Assert.NotEqual(210f, deskewed);
        Assert.Equal(210f, LineDetector.ReskewY(380f, deskewed, PivotX, tan), 0.0001f);
    }
}
