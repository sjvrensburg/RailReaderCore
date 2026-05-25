using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class XYCutPlusPlusResolverTests
{
    private static LayoutBlock Block(float x, float y, float w, float h, BlockRole role = BlockRole.Text, string? tag = null)
    {
        var b = new LayoutBlock { BBox = new BBox(x, y, w, h), Role = role, Confidence = 0.9f };
        // Encode a probe value in ClassId so tests can identify blocks after re-ordering.
        b.ClassId = tag is null ? 0 : tag.GetHashCode();
        return b;
    }

    /// <summary>
    /// Snapshot the input list as a deterministic key per block so a test can
    /// describe expected ordering by reference, not by reading reorder-then-Order
    /// indices.
    /// </summary>
    private static int Id(LayoutBlock b) => b.ClassId;

    [Fact]
    public void Empty_NoOp()
    {
        var blocks = new List<LayoutBlock>();
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);
        Assert.Empty(blocks);
    }

    [Fact]
    public void SingleBlock_GetsOrderZero()
    {
        var only = Block(10, 20, 100, 30);
        var blocks = new List<LayoutBlock> { only };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);
        Assert.Single(blocks);
        Assert.Equal(0, blocks[0].Order);
    }

    [Fact]
    public void OutputOrderIsDense()
    {
        // Output invariant: blocks[i].Order == i after AssignOrder. Matches the
        // contract enforced by ModelOrderResolver / TopDownReadingOrderResolver.
        var blocks = new List<LayoutBlock>
        {
            Block(0,   0, 100, 20),
            Block(0,  30, 100, 20),
            Block(0,  60, 100, 20),
            Block(0,  90, 100, 20),
        };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);
        for (int i = 0; i < blocks.Count; i++)
            Assert.Equal(i, blocks[i].Order);
    }

    [Fact]
    public void SingleColumn_MatchesTopDownOrder()
    {
        var b0 = Block(40,  50, 400, 30, tag: "a");
        var b1 = Block(40, 100, 400, 30, tag: "b");
        var b2 = Block(40, 150, 400, 30, tag: "c");
        var b3 = Block(40, 200, 400, 30, tag: "d");

        // Input in arbitrary order — resolver should produce top-to-bottom.
        var blocks = new List<LayoutBlock> { b2, b0, b3, b1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800);

        Assert.Equal(Id(b0), Id(blocks[0]));
        Assert.Equal(Id(b1), Id(blocks[1]));
        Assert.Equal(Id(b2), Id(blocks[2]));
        Assert.Equal(Id(b3), Id(blocks[3]));
    }

    [Fact]
    public void TwoColumns_ColumnFirstReadingOrder()
    {
        // Left column spans x=[40, 280]; right column spans x=[320, 560].
        // Gutter at x=[280, 320] (40pt wide).
        var l0 = Block(40,  50, 240, 30, tag: "L0");
        var l1 = Block(40, 100, 240, 30, tag: "L1");
        var l2 = Block(40, 150, 240, 30, tag: "L2");
        var r0 = Block(320, 50, 240, 30, tag: "R0");
        var r1 = Block(320, 100, 240, 30, tag: "R1");
        var r2 = Block(320, 150, 240, 30, tag: "R2");

        // Interleaved input order — must NOT zig-zag in the output.
        var blocks = new List<LayoutBlock> { l0, r0, l1, r1, l2, r2 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        Assert.Equal(new[] { "L0", "L1", "L2", "R0", "R1", "R2" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void TwoColumns_WithFullWidthTitle_TitleReadsFirst()
    {
        var title = Block(40, 20, 520, 30, role: BlockRole.Title, tag: "title");
        var l0 = Block(40,  80, 240, 30, tag: "L0");
        var l1 = Block(40, 130, 240, 30, tag: "L1");
        var r0 = Block(320, 80, 240, 30, tag: "R0");
        var r1 = Block(320, 130, 240, 30, tag: "R1");

        var blocks = new List<LayoutBlock> { r1, l0, title, r0, l1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        Assert.Equal(new[] { "title", "L0", "L1", "R0", "R1" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void TwoColumns_WithMidPageFullWidthFigure_OrderRespectsSpanner()
    {
        // Top half: two columns. Mid: full-width figure. Bottom: two columns.
        var lTop = Block(40,  80, 240, 60, tag: "Lt");
        var rTop = Block(320, 80, 240, 60, tag: "Rt");
        var figure = Block(40, 160, 520, 100, role: BlockRole.Figure, tag: "Fig");
        var lBot = Block(40,  280, 240, 60, tag: "Lb");
        var rBot = Block(320, 280, 240, 60, tag: "Rb");

        var blocks = new List<LayoutBlock> { lBot, rTop, figure, lTop, rBot };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        Assert.Equal(new[] { "Lt", "Rt", "Fig", "Lb", "Rb" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void ThreeColumns_OrderedLeftToRight()
    {
        // Three 160pt-wide columns at x=40,240,440 separated by 40pt gutters.
        var a0 = Block(40,  50, 160, 30, tag: "a0");
        var a1 = Block(40, 100, 160, 30, tag: "a1");
        var b0 = Block(240, 50, 160, 30, tag: "b0");
        var b1 = Block(240, 100, 160, 30, tag: "b1");
        var c0 = Block(440, 50, 160, 30, tag: "c0");
        var c1 = Block(440, 100, 160, 30, tag: "c1");

        var blocks = new List<LayoutBlock> { c1, b0, a0, c0, a1, b1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 640, 800);

        Assert.Equal(new[] { "a0", "a1", "b0", "b1", "c0", "c1" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void FootnoteAtPageBottom_ReadsLast()
    {
        var l0 = Block(40,  80, 240, 60, tag: "L0");
        var l1 = Block(40, 160, 240, 60, tag: "L1");
        var r0 = Block(320, 80, 240, 60, tag: "R0");
        var r1 = Block(320, 160, 240, 60, tag: "R1");
        var footnote = Block(40, 720, 520, 30, role: BlockRole.Footnote, tag: "fn");

        var blocks = new List<LayoutBlock> { footnote, r0, l0, r1, l1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        Assert.Equal(new[] { "L0", "L1", "R0", "R1", "fn" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void TitleAndFootnoteWithColumnBody_TitleFirstFootnoteLast()
    {
        // Full pipeline shape: full-width title + 2-col body + full-width footnote.
        var title = Block(40,  20, 520, 30, role: BlockRole.Title, tag: "T");
        var l0 = Block(40,   80, 240, 200, tag: "L");
        var r0 = Block(320,  80, 240, 200, tag: "R");
        var fn = Block(40,  720, 520, 30, role: BlockRole.Footnote, tag: "FN");

        var blocks = new List<LayoutBlock> { r0, fn, l0, title };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        Assert.Equal(new[] { "T", "L", "R", "FN" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void NonLtrDirection_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            new XYCutPlusPlusResolver(ReadingDirection.RightToLeftTopToBottom));
        Assert.Throws<NotSupportedException>(() =>
            new XYCutPlusPlusResolver(ReadingDirection.TopToBottomRightToLeft));
    }

    [Fact]
    public void TinyVerticalGapBelowGutterThreshold_DoesNotSplitColumns()
    {
        // 5pt gap between two stacks of blocks — under MinColumnGutterPoints (12pt).
        // Should fall through to a horizontal cut and read top-to-bottom.
        var a = Block(40,  50, 200, 30, tag: "a");
        var b = Block(245, 50, 200, 30, tag: "b");
        var c = Block(40, 100, 200, 30, tag: "c");
        var d = Block(245, 100, 200, 30, tag: "d");

        var blocks = new List<LayoutBlock> { d, b, c, a };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800);

        // No column split → row-major (top-down, then left-to-right).
        Assert.Equal(new[] { "a", "b", "c", "d" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }
}
