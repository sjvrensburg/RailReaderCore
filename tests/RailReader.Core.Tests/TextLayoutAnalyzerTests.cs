using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for <see cref="TextLayoutAnalyzer"/>, the model-free layout analyzer.
///
/// <para>
/// It recovers blocks from the text layer alone, so a build with no ONNX runtime and no model
/// file still has a rail pipeline. The behaviour that matters is that paragraphs separated by
/// extra leading come apart, that columns do not merge across the gutter, and that the
/// thresholds follow the page's own spacing rather than a constant — the Docstrum idea.
/// </para>
/// </summary>
public class TextLayoutAnalyzerTests
{
    /// <summary>
    /// Lays out lines of glyphs. Each entry is (left, top, charCount); glyph advance and size
    /// scale with <paramref name="fontSize"/> so a test can shrink or enlarge the whole page.
    /// </summary>
    private static List<CharBox> Glyphs(IEnumerable<(float Left, float Top, int Count)> lines,
        float fontSize = 10f)
    {
        var boxes = new List<CharBox>();
        int idx = 0;
        float advance = fontSize * 0.8f, glyphW = fontSize * 0.7f;
        foreach (var (left, top, count) in lines)
            for (int i = 0; i < count; i++)
            {
                float x = left + i * advance;
                boxes.Add(new CharBox(idx++, x, top, x + glyphW, top + fontSize));
            }
        return boxes;
    }

    private static PageAnalysis Analyse(List<CharBox> glyphs, double pageW = 612, double pageH = 792)
    {
        using var analyzer = new TextLayoutAnalyzer();
        return analyzer.RunAnalysis([], 0, 0, pageW, pageH, glyphs);
    }

    [Fact]
    public void NeedsNoModelAndDeclaresNoReadingOrder()
    {
        using var analyzer = new TextLayoutAnalyzer();

        Assert.False(analyzer.Capabilities.ProvidesReadingOrder);
        Assert.Empty(analyzer.Capabilities.Classes);
        Assert.Equal(TextLayoutAnalyzer.DefaultInputSize, analyzer.Capabilities.InputSize);
    }

    [Fact]
    public void ParagraphsSeparatedByLeading_BecomeSeparateBlocks()
    {
        // Two five-line paragraphs, 14 pt line pitch, separated by a 40 pt gap.
        var lines = new List<(float, float, int)>();
        for (int i = 0; i < 5; i++) lines.Add((100f, 100f + i * 14f, 40));
        for (int i = 0; i < 5; i++) lines.Add((100f, 210f + i * 14f, 40));

        var blocks = Analyse(Glyphs(lines)).Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(BlockRole.Text, b.Role));
        Assert.True(blocks[0].BBox.Y < blocks[1].BBox.Y);
    }

    [Fact]
    public void ConsecutiveLinesOfOneParagraph_StayInOneBlock()
    {
        var lines = new List<(float, float, int)>();
        for (int i = 0; i < 8; i++) lines.Add((100f, 100f + i * 14f, 40));

        var blocks = Analyse(Glyphs(lines)).Blocks;

        Assert.Single(blocks);
        Assert.Equal(8 * 14f - 4f, blocks[0].BBox.H, 2f);   // spans first top to last bottom
    }

    [Fact]
    public void TwoColumns_DoNotMergeAcrossTheGutter()
    {
        // Side-by-side columns at the same vertical positions, 80 pt of gutter between them.
        var lines = new List<(float, float, int)>();
        for (int i = 0; i < 6; i++)
        {
            lines.Add((60f, 100f + i * 14f, 25));    // left column, ends near x=260
            lines.Add((340f, 100f + i * 14f, 25));   // right column
        }

        var blocks = Analyse(Glyphs(lines)).Blocks;

        Assert.Equal(2, blocks.Count);
        var left = blocks.OrderBy(b => b.BBox.X).First();
        var right = blocks.OrderBy(b => b.BBox.X).Last();
        Assert.True(left.BBox.X + left.BBox.W < right.BBox.X,
            "columns must not overlap after grouping");
    }

    [Fact]
    public void ThresholdsFollowThePagesOwnSpacing()
    {
        // The same layout at two font sizes must give the same block structure: a fixed
        // pixel threshold would split the large-print page or merge the dense one.
        static List<(float, float, int)> Layout(float pitch, float gap)
        {
            var lines = new List<(float, float, int)>();
            for (int i = 0; i < 4; i++) lines.Add((100f, 100f + i * pitch, 30));
            float second = 100f + 4 * pitch + gap;
            for (int i = 0; i < 4; i++) lines.Add((100f, second + i * pitch, 30));
            return lines;
        }

        var dense = Analyse(Glyphs(Layout(pitch: 9f, gap: 20f), fontSize: 6f)).Blocks;
        var large = Analyse(Glyphs(Layout(pitch: 30f, gap: 66f), fontSize: 22f)).Blocks;

        Assert.Equal(2, dense.Count);
        Assert.Equal(2, large.Count);
    }

    [Fact]
    public void NoTextLayer_YieldsNoBlocks()
    {
        // A scan reaches this analyzer with no char boxes; it must say so rather than invent
        // a page-sized block. OCR in Full mode is what turns such a page into a usable one.
        using var analyzer = new TextLayoutAnalyzer();

        Assert.Empty(analyzer.RunAnalysis([], 0, 0, 612, 792, null).Blocks);
        Assert.Empty(analyzer.RunAnalysis([], 0, 0, 612, 792, []).Blocks);
    }

    [Fact]
    public void WhitespaceOnlyBoxes_AreIgnored()
    {
        // Zero-area boxes (explicit space glyphs) carry no geometry and must not create blocks.
        List<CharBox> spaces = [new(0, 100f, 100f, 100f, 100f), new(1, 110f, 100f, 110f, 100f)];

        Assert.Empty(Analyse(spaces).Blocks);
    }

    [Fact]
    public void BlocksCoverTheGlyphsTheyWereBuiltFrom()
    {
        var lines = new List<(float, float, int)>();
        for (int i = 0; i < 5; i++) lines.Add((100f, 100f + i * 14f, 30));
        var glyphs = Glyphs(lines);

        var blocks = Analyse(glyphs).Blocks;

        // Every glyph must fall inside some block, or rail would have nothing to stop on there.
        foreach (var g in glyphs)
        {
            bool covered = blocks.Any(b =>
                g.Left >= b.BBox.X - 0.5f && g.Right <= b.BBox.X + b.BBox.W + 0.5f &&
                g.Top >= b.BBox.Y - 0.5f && g.Bottom <= b.BBox.Y + b.BBox.H + 0.5f);
            Assert.True(covered, $"glyph {g.Index} at ({g.Left},{g.Top}) is in no block");
        }
    }

    [Fact]
    public void OutputFeedsTheStandardPipeline()
    {
        // The pipeline that runs after any analyzer — reading order, then post-processing and
        // line detection — must accept these blocks unchanged.
        var lines = new List<(float, float, int)>();
        for (int i = 0; i < 4; i++) lines.Add((100f, 100f + i * 14f, 30));
        for (int i = 0; i < 4; i++) lines.Add((100f, 220f + i * 14f, 30));
        var glyphs = Glyphs(lines);

        var analysis = Analyse(glyphs);
        new XYCutPlusPlusResolver().AssignOrder(
            analysis.Blocks, analysis.PageWidth, analysis.PageHeight, glyphs);

        var pixmap = new byte[10 * 10 * 3];
        Array.Fill(pixmap, (byte)255);
        BlockPostProcessor.PostProcess(analysis.Blocks, pixmap, 10, 10, 1f, 1f, glyphs);

        Assert.Equal(2, analysis.Blocks.Count);
        Assert.Equal([0, 1], analysis.Blocks.Select(b => b.Order).OrderBy(o => o));
        // Line detection found the four lines of each paragraph.
        Assert.All(analysis.Blocks, b => Assert.Equal(4, b.Lines.Count));
    }

    [Fact]
    public void TwoColumnsWithUnevenTops_StillGroupIntoParagraphs()
    {
        // A real two-column page's left and right lines never share an exact top — each line's
        // box starts at its own tallest ascender. Sorted by Y they interleave, so pairing lines
        // by Y order alone makes half of all pitch samples the sub-point difference between two
        // columns of the same visual row. The median then collapses, the between-line gap
        // shrinks below the real leading, and no two lines ever join: one block per line.
        var rnd = new Random(7);
        var lines = new List<(float, float, int)>();
        foreach (float x in new[] { 60f, 340f })
            for (int i = 0; i < 30; i++)
                lines.Add((x, 100f + i * 14f + (float)(rnd.NextDouble() * 2.0 - 1.0), 25));

        var blocks = Analyse(Glyphs(lines)).Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.True(b.BBox.H > 300f,
            $"block height {b.BBox.H} should span a whole column, not one line"));
    }

    [Fact]
    public void LinePitchIsMeasuredDownAColumn_NotAcrossTheGutter()
    {
        // Same geometry, straight at the estimator: the answer must be the 14 pt leading, not
        // the 1 pt stagger between the columns.
        List<BBox> lines = [];
        foreach (float x in new[] { 60f, 340f })
            for (int i = 0; i < 20; i++)
                lines.Add(new BBox(x, 100f + i * 14f + (x > 100f ? 1f : 0f), 200f, 10f));

        Assert.Equal(14f, TextLayoutAnalyzer.EstimateLinePitch(lines), 1f);
    }

    [Fact]
    public void SpacingEstimateIgnoresTheLongTail()
    {
        // Ordinary inter-glyph gaps of 2, plus one huge gutter jump: the median must report
        // the ordinary gap, not be dragged up by the outlier.
        List<CharBox> glyphs =
        [
            new(0, 0f, 0f, 8f, 10f), new(1, 10f, 0f, 18f, 10f), new(2, 20f, 0f, 28f, 10f),
            new(3, 300f, 0f, 308f, 10f),
        ];

        Assert.Equal(2f, TextLayoutAnalyzer.EstimateWithinLineSpacing(glyphs), 1f);
    }
}
