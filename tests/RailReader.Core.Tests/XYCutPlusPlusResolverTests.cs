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

    // -----------------------------------------------------------------
    // Hard cases: margin/side notes + footnotes embedded in complex
    // multi-column layouts. These are where reading order breaks in
    // practice (the clean column tests above are sanity checks).
    // -----------------------------------------------------------------

    [Fact]
    public void LeftMarginNote_BesideColumn_ReadsAdjacentToItsNeighbour()
    {
        // Single body column at x=[120, 520]; narrow aside in the left margin
        // at x=[20, 90], vertically beside the second paragraph. A naive XY-cut
        // would treat the aside as a first column and read it entirely first.
        var p0 = Block(120,  60, 400, 40, tag: "P0");
        var note = Block(20, 120, 70, 50, role: BlockRole.Aside, tag: "NOTE");
        var p1 = Block(120, 120, 400, 40, tag: "P1");
        var p2 = Block(120, 180, 400, 40, tag: "P2");

        var blocks = new List<LayoutBlock> { p2, note, p0, p1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        // The aside is re-inserted next to its nearest body block (P1), not
        // pulled to the very front as a phantom column.
        var order = blocks.Select(Id).ToList();
        int iP0 = order.IndexOf(Id(p0)), iNote = order.IndexOf(Id(note)), iP2 = order.IndexOf(Id(p2));
        Assert.True(iP0 < iNote, "body should not start with the margin note");
        Assert.True(iNote <= iP2, "margin note should read before the lower paragraph");
    }

    [Fact]
    public void TwoColumns_WithMarginNote_ColumnsStillOrderedFirst()
    {
        // Two body columns at x=[120,290] and [330,560], plus a left-margin
        // aside. The aside must not be mistaken for a third (leftmost) column.
        var note = Block(20, 100, 70, 60, role: BlockRole.Aside, tag: "NOTE");
        var l0 = Block(120, 60, 170, 40, tag: "L0");
        var l1 = Block(120, 120, 170, 40, tag: "L1");
        var r0 = Block(330, 60, 230, 40, tag: "R0");
        var r1 = Block(330, 120, 230, 40, tag: "R1");

        var blocks = new List<LayoutBlock> { r1, note, l0, r0, l1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        var order = blocks.Select(Id).ToList();
        // Column body keeps its column-major order regardless of the aside.
        Assert.True(order.IndexOf(Id(l0)) < order.IndexOf(Id(l1)));
        Assert.True(order.IndexOf(Id(l1)) < order.IndexOf(Id(r0)));
        Assert.True(order.IndexOf(Id(r0)) < order.IndexOf(Id(r1)));
        // The note is not the first block read.
        Assert.NotEqual(Id(note), order[0]);
    }

    [Fact]
    public void TwoColumns_WithPerColumnFootnotes_FootnotesReadAfterColumnBody()
    {
        // Each column has its own footnote band at the bottom (not full-width).
        var l0 = Block(40,  80, 240, 120, tag: "L0");
        var lfn = Block(40, 700, 240, 30, role: BlockRole.Footnote, tag: "LFN");
        var r0 = Block(320, 80, 240, 120, tag: "R0");
        var rfn = Block(320, 700, 240, 30, role: BlockRole.Footnote, tag: "RFN");

        var blocks = new List<LayoutBlock> { rfn, l0, lfn, r0 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        var order = blocks.Select(Id).ToList();
        // Within each column, body precedes its footnote.
        Assert.True(order.IndexOf(Id(l0)) < order.IndexOf(Id(lfn)));
        Assert.True(order.IndexOf(Id(r0)) < order.IndexOf(Id(rfn)));
        // Left column (body+footnote) is fully read before the right column.
        Assert.True(order.IndexOf(Id(lfn)) < order.IndexOf(Id(r0)));
    }

    [Fact]
    public void Compound_TitleColumnsFigureInsetFootnote_OrderedCorrectly()
    {
        // Full-width title, two columns, a figure inset that clips into the
        // right column (2D-overlaps it), and a full-width footnote band.
        var title = Block(40,  20, 520, 30, role: BlockRole.Title, tag: "T");
        var l0 = Block(40,  80, 240, 200, tag: "L0");
        var r0 = Block(320, 80, 240, 80, tag: "R0");
        var figure = Block(300, 170, 260, 90, role: BlockRole.Figure, tag: "FIG"); // clips right col
        var r1 = Block(320, 270, 240, 80, tag: "R1");
        var fn = Block(40, 720, 520, 30, role: BlockRole.Footnote, tag: "FN");

        var blocks = new List<LayoutBlock> { fn, r1, figure, l0, r0, title };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        var order = blocks.Select(Id).ToList();
        Assert.Equal(Id(title), order[0]);                              // title first
        Assert.Equal(Id(fn), order[^1]);                                // footnote last
        Assert.True(order.IndexOf(Id(r0)) < order.IndexOf(Id(r1)));     // right column in order
        // Clipping figure re-inserted within the body (not first, not last).
        int iFig = order.IndexOf(Id(figure));
        Assert.True(iFig > 0 && iFig < order.Count - 1);
    }

    [Fact]
    public void TextLayerTieBreak_OrdersLeafByContentStreamIndex()
    {
        // Two blocks the geometry cannot separate (same row, sub-gutter gap):
        // a 5pt gap is below the column threshold, so they land in one leaf.
        // The text layer says the right block's text comes first in the stream.
        var left = Block(40,  50, 200, 30, tag: "LEFT");
        var right = Block(245, 50, 200, 30, tag: "RIGHT");

        var charBoxes = new List<CharBox>
        {
            // RIGHT block characters appear earlier in the content stream.
            new(0, 250, 55, 260, 75),
            new(1, 260, 55, 270, 75),
            // LEFT block characters appear later.
            new(2, 50, 55, 60, 75),
            new(3, 60, 55, 70, 75),
        };

        var blocks = new List<LayoutBlock> { left, right };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800, charBoxes);

        // Text-stream order (RIGHT then LEFT) overrides the geometric L→R sort.
        Assert.Equal(new[] { "RIGHT", "LEFT" }.Select(t => t.GetHashCode()), blocks.Select(Id));
    }

    [Fact]
    public void NullTextLayer_LeafFallsBackToGeometricOrder()
    {
        var left = Block(40,  50, 200, 30, tag: "LEFT");
        var right = Block(245, 50, 200, 30, tag: "RIGHT");

        var blocks = new List<LayoutBlock> { right, left };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800, charBoxes: null);

        // No text layer → geometric top-down/left-to-right.
        Assert.Equal(new[] { "LEFT", "RIGHT" }.Select(t => t.GetHashCode()), blocks.Select(Id));
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
