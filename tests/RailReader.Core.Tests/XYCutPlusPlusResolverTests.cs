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
        // contract enforced by ModelOrderResolver.
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
    public void TwoColumns_WithTallFigureAndCaptionAtop_ColumnsDoNotInterleave()
    {
        // Regression (TimeGMM.pdf p.2): a TALL full-width figure plus its
        // full-width caption stacked above two columns. The combined height of
        // that top matter spanning the gutter pushes the gutter's vertical
        // coverage above the projection-split threshold, so no column gutter is
        // found for the whole-page region. Before the IsDenseSingleColumn fix,
        // the dense-column guard then suppressed the horizontal cut that would
        // peel the top matter, and the page fell through to row-banding — which
        // interleaved the columns (left-pair, right-pair, left, right, …).
        // The columns must each read contiguously, top-to-bottom.
        var figure = Block(40, 30, 520, 210, role: BlockRole.Figure, tag: "FIG");
        var caption = Block(40, 250, 520, 36, role: BlockRole.Caption, tag: "CAP");
        // Body rows are 12pt apart — above the 6pt super-block merge threshold,
        // so each block stays separate (as in the real page, where paragraph
        // gaps, headings and equations break a column into several blocks). The
        // caption→body gap (24pt) stays the widest horizontal gap so the fix
        // peels the top matter first, then column-splits the body.
        var l0 = Block(40, 310, 240, 40, tag: "L0");
        var l1 = Block(40, 362, 240, 40, tag: "L1");
        var l2 = Block(40, 414, 240, 40, tag: "L2");
        var l3 = Block(40, 466, 240, 40, tag: "L3");
        var r0 = Block(320, 310, 240, 40, tag: "R0");
        var r1 = Block(320, 362, 240, 40, tag: "R1");
        var r2 = Block(320, 414, 240, 40, tag: "R2");
        var r3 = Block(320, 466, 240, 40, tag: "R3");

        // Interleaved input order.
        var blocks = new List<LayoutBlock> { figure, l0, r0, l1, r1, caption, l2, r2, l3, r3 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        var order = blocks.Select(Id).ToList();
        int Pos(string tag) => order.IndexOf(tag.GetHashCode());

        // Full-width top matter reads first, in top-to-bottom order: the figure
        // then its caption (the divider peel keeps the caption with the figure
        // rather than stranding it at the column boundary).
        Assert.Equal(0, Pos("FIG"));
        Assert.Equal(1, Pos("CAP"));
        // Each column reads strictly top-to-bottom (no zig-zag within a column).
        Assert.True(Pos("L0") < Pos("L1") && Pos("L1") < Pos("L2") && Pos("L2") < Pos("L3"));
        Assert.True(Pos("R0") < Pos("R1") && Pos("R1") < Pos("R2") && Pos("R2") < Pos("R3"));
        // The columns do not interleave: the whole left column precedes the
        // whole right column.
        int leftLast = new[] { "L0", "L1", "L2", "L3" }.Max(Pos);
        int rightFirst = new[] { "R0", "R1", "R2", "R3" }.Min(Pos);
        Assert.True(leftLast < rightFirst,
            $"columns interleave: last left block at {leftLast}, first right block at {rightFirst}");
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

    // -----------------------------------------------------------------
    // Many headings + short paragraphs: phantom-gutter rejection,
    // Y-primary leaf ordering, and heading attachment.
    // -----------------------------------------------------------------

    [Fact]
    public void RealPage_ContributionsThenSectionHeading_HeadingDoesNotJumpAheadOfBullet()
    {
        // Exact geometry from "Foundation Models for Time Series Analysis"
        // (KDD'24), page 2 right column. The "2 Background" heading (y=357) must
        // NOT be ordered ahead of the "Future research" bullet (y=311) above it.
        // Regression for the density-guard + text-stream-sort interaction.
        var bComp = Block(317.1f, 212.7f, 242.8f, 40.9f, tag: "comprehensive");
        var bNovel = Block(316.8f, 257.4f, 242.6f, 51.2f, tag: "novel");
        var bFuture = Block(317.0f, 311.7f, 242.0f, 30.7f, tag: "future");
        var hBackground = Block(317.0f, 357.1f, 77.9f, 10.6f, role: BlockRole.Heading, tag: "2-background");
        var bFoundation = Block(316.9f, 372.3f, 242.9f, 150.1f, tag: "foundation-models");

        var blocks = new List<LayoutBlock> { hBackground, bFuture, bComp, bFoundation, bNovel };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 612, 792);

        var order = blocks.Select(Id).ToList();
        Assert.Equal(
            new[] { "comprehensive", "novel", "future", "2-background", "foundation-models" }
                .Select(t => t.GetHashCode()),
            order);
    }

    [Fact]
    public void RaggedShortParagraphs_NoPhantomColumnSplit()
    {
        // A single column of short, ragged-right one-sentence paragraphs. Their
        // right edges vary, manufacturing a >12pt non-straddled gap on the right —
        // but it does not span the column height, so it must be rejected.
        var p0 = Block(40,  50, 380, 24, tag: "p0"); // right edge 420
        var p1 = Block(40,  84, 250, 24, tag: "p1"); // right edge 290 (ragged)
        var p2 = Block(40, 118, 300, 24, tag: "p2"); // right edge 340
        var p3 = Block(40, 152, 210, 24, tag: "p3"); // right edge 250

        var blocks = new List<LayoutBlock> { p2, p0, p3, p1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800);

        Assert.Equal(new[] { "p0", "p1", "p2", "p3" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void HeadingAttachment_HeadingPulledDownToItsBody()
    {
        // A heading whose body sits just below it must read immediately before
        // that body even if another block shares the region.
        var prevPara = Block(40,  40, 400, 60, tag: "prev");
        var heading = Block(40, 120, 120, 14, role: BlockRole.Heading, tag: "H");
        var body = Block(40, 140, 400, 80, tag: "body");

        var blocks = new List<LayoutBlock> { prevPara, heading, body };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800);

        var order = blocks.Select(Id).ToList();
        int iH = order.IndexOf(Id(heading)), iBody = order.IndexOf(Id(body));
        Assert.Equal(iH + 1, iBody); // heading immediately precedes its body
        Assert.True(order.IndexOf(Id(prevPara)) < iH);
    }

    [Fact]
    public void NarrowSidebar_BelowMinColumnWidth_NotTreatedAsColumn()
    {
        // A 40pt-wide sliver beside a wide body. The gap between them exceeds the
        // gutter threshold, but the sliver is under MinColumnWidthFraction of the
        // region — it must not become a phantom first column read entirely first.
        var sliver = Block(20, 60, 40, 200, tag: "sliver");
        var body0 = Block(120, 60, 400, 60, tag: "b0");
        var body1 = Block(120, 140, 400, 60, tag: "b1");

        var blocks = new List<LayoutBlock> { body1, sliver, body0 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        // The sliver is not pulled ahead of the whole body as a column.
        Assert.NotEqual(Id(sliver), blocks.Select(Id).First());
    }

    [Fact]
    public void TwoColumns_NarrowGutterWithCrossingPageNumber_StillColumnFirst()
    {
        // Real-corpus failure: ~9pt gutter (below the old 12pt floor) AND a page
        // number straddling the column boundary at the bottom. Either alone
        // defeated the column split and interleaved the columns. The page number
        // must be masked (furniture) and the 9pt gutter accepted.
        var l0 = Block(40,  50, 110, 30, tag: "L0");
        var l1 = Block(40, 100, 110, 30, tag: "L1");
        var r0 = Block(159, 50, 111, 30, tag: "R0"); // gutter 150->159 = 9pt
        var r1 = Block(159, 100, 111, 30, tag: "R1");
        var pageNo = Block(145, 780, 20, 10, role: BlockRole.PageNumber, tag: "PG"); // crosses gutter

        var blocks = new List<LayoutBlock> { r1, pageNo, l0, r0, l1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 300, 820);

        var order = blocks.Select(Id).ToList();
        // Left column fully before right column (not interleaved) — the page
        // number crossing the gutter no longer defeats the split.
        Assert.True(order.IndexOf(Id(l0)) < order.IndexOf(Id(l1)));
        Assert.True(order.IndexOf(Id(l1)) < order.IndexOf(Id(r0)));
        Assert.True(order.IndexOf(Id(r0)) < order.IndexOf(Id(r1)));
        // Page number is furniture → not ordered ahead of the body.
        Assert.NotEqual(Id(pageNo), order[0]);
    }

    [Fact]
    public void RunningHeaderAndFooter_GoToExtremes()
    {
        var header = Block(40, 20, 400, 12, role: BlockRole.Header, tag: "HDR");
        var body0 = Block(40, 60, 400, 80, tag: "b0");
        var body1 = Block(40, 150, 400, 80, tag: "b1");
        var footer = Block(40, 760, 400, 12, role: BlockRole.Footer, tag: "FTR");

        var blocks = new List<LayoutBlock> { body1, footer, header, body0 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800);

        var order = blocks.Select(Id).ToList();
        Assert.Equal(Id(header), order[0]);
        Assert.Equal(Id(footer), order[^1]);
        Assert.True(order.IndexOf(Id(body0)) < order.IndexOf(Id(body1)));
    }

    [Fact]
    public void TwoColumns_WithContentBlockCrossingGutter_ProjectionSplitRecoversColumns()
    {
        // A wide figure pokes across the gutter mid-page while both columns
        // continue above and below it. The straddler sweep finds no clean gutter
        // (the figure bridges left↔right), but the projection profile sees a
        // low-coverage valley at the gutter and still splits column-first.
        var lTop = Block(40,  60, 240, 80, tag: "Lt");
        var lMid = Block(40, 380, 240, 80, tag: "Lm");
        var lBot = Block(40, 560, 240, 80, tag: "Lb");
        var rTop = Block(310, 60, 240, 80, tag: "Rt");
        var rMid = Block(310, 380, 240, 80, tag: "Rm");
        var rBot = Block(310, 560, 240, 80, tag: "Rb");
        // Figure crossing the gutter (x 250→520) over a short y-band only.
        var fig = Block(250, 250, 270, 60, role: BlockRole.Figure, tag: "Fig");

        var blocks = new List<LayoutBlock> { rBot, lTop, fig, rMid, lBot, rTop, lMid };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 600, 800);

        var order = blocks.Select(Id).ToList();
        // All left-column blocks precede all right-column blocks (not interleaved).
        int lastLeft = new[] { lTop, lMid, lBot }.Max(b => order.IndexOf(Id(b)));
        int firstRight = new[] { rTop, rMid, rBot }.Min(b => order.IndexOf(Id(b)));
        Assert.True(lastLeft < firstRight, $"columns interleaved: lastLeft={lastLeft} firstRight={firstRight}");
    }

    [Fact]
    public void SingleColumn_ProjectionDoesNotInventAColumn()
    {
        // One dense column of full-width paragraphs: no interior coverage valley,
        // so the projection fallback must NOT split it.
        var blocks = new List<LayoutBlock>
        {
            Block(40,  50, 480, 60, tag: "p0"),
            Block(40, 120, 480, 60, tag: "p1"),
            Block(40, 190, 480, 60, tag: "p2"),
            Block(40, 260, 480, 60, tag: "p3"),
        };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 560, 800);
        Assert.Equal(new[] { "p0", "p1", "p2", "p3" }.Select(t => t.GetHashCode()), blocks.Select(Id));
    }

    // -----------------------------------------------------------------
    // Super-block merge pre-pass (merge tight-spaced runs, order coarse,
    // expand top-down). gap=6pt, classes body / notes / math.
    // -----------------------------------------------------------------

    [Fact]
    public void TightlySpacedColumns_MergeReadsColumnFirst()
    {
        // Two columns of paragraphs spaced ~4pt apart (tight enough to merge into
        // two tall super-blocks). Reads column-first via the coarse super-block
        // ordering, expanded top-down.
        var l0 = Block(40,  50, 210, 34, tag: "L0");
        var l1 = Block(40,  88, 210, 34, tag: "L1");
        var l2 = Block(40, 126, 210, 34, tag: "L2");
        var r0 = Block(260, 50, 210, 34, tag: "R0");
        var r1 = Block(260, 88, 210, 34, tag: "R1");
        var r2 = Block(260, 126, 210, 34, tag: "R2");

        var blocks = new List<LayoutBlock> { r1, l0, r0, l2, r2, l1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 520, 800);

        Assert.Equal(new[] { "L0", "L1", "L2", "R0", "R1", "R2" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }

    [Fact]
    public void FullWidthTitle_NotAbsorbedByColumnRun()
    {
        // A full-width title above a tight column run must stay its own
        // super-block (merge barrier), read first, not swallowed by the column.
        var title = Block(40, 10, 460, 20, role: BlockRole.Title, tag: "T");
        var l0 = Block(40,  60, 210, 30, tag: "L0");
        var l1 = Block(40,  94, 210, 30, tag: "L1");
        var r0 = Block(260, 60, 210, 30, tag: "R0");
        var r1 = Block(260, 94, 210, 30, tag: "R1");

        var blocks = new List<LayoutBlock> { r1, l0, title, r0, l1 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 520, 800);

        var order = blocks.Select(Id).ToList();
        Assert.Equal(Id(title), order[0]);
        Assert.True(order.IndexOf(Id(l0)) < order.IndexOf(Id(l1)));
        Assert.True(order.IndexOf(Id(l1)) < order.IndexOf(Id(r0)));
        Assert.True(order.IndexOf(Id(r0)) < order.IndexOf(Id(r1)));
    }

    [Fact]
    public void MathBlock_IsolatedClass_StillReadsInColumnFlow()
    {
        // A display equation tightly nested between two paragraphs. Math is a
        // separate merge class (so it never gets absorbed/reordered into the text
        // run), yet the super-block ordering still reads text→equation→text.
        var t0 = Block(40,  50, 210, 30, tag: "t0");
        var eq = Block(40,  84, 210, 30, role: BlockRole.DisplayMath, tag: "eq");
        var t1 = Block(40, 118, 210, 30, tag: "t1");

        var blocks = new List<LayoutBlock> { t1, eq, t0 };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 520, 800);

        Assert.Equal(new[] { "t0", "eq", "t1" }.Select(t => t.GetHashCode()), blocks.Select(Id));
    }

    [Fact]
    public void MergeDisabled_ProducesDenseValidOrder()
    {
        // The merge pre-pass can be turned off via the constructor; the core
        // resolver still yields a dense, column-first order.
        var l0 = Block(40,  50, 210, 34, tag: "L0");
        var l1 = Block(40,  88, 210, 34, tag: "L1");
        var r0 = Block(260, 50, 210, 34, tag: "R0");
        var r1 = Block(260, 88, 210, 34, tag: "R1");

        var blocks = new List<LayoutBlock> { r1, l0, r0, l1 };
        new XYCutPlusPlusResolver(mergeAdjacent: false).AssignOrder(blocks, 520, 800);

        for (int i = 0; i < blocks.Count; i++) Assert.Equal(i, blocks[i].Order);
        var order = blocks.Select(Id).ToList();
        Assert.True(order.IndexOf(Id(l0)) < order.IndexOf(Id(r0)));
    }

    [Fact]
    public void TightGutterTwoStacks_ProjectionReadsColumnFirst()
    {
        // Two clean vertical stacks separated by only a 5pt gutter — below the
        // straddler floor, so the straddler sweep won't split. But the projection
        // profile sees a full-height zero-coverage valley flanked by two stacks
        // (≥2 blocks each) and correctly reads it column-first.
        var a = Block(40,  50, 200, 30, tag: "a");
        var b = Block(245, 50, 200, 30, tag: "b");
        var c = Block(40, 100, 200, 30, tag: "c");
        var d = Block(245, 100, 200, 30, tag: "d");

        var blocks = new List<LayoutBlock> { d, b, c, a };
        new XYCutPlusPlusResolver().AssignOrder(blocks, 480, 800);

        // Column-first: left stack (a,c) before right stack (b,d).
        Assert.Equal(new[] { "a", "c", "b", "d" }.Select(t => t.GetHashCode()),
            blocks.Select(Id));
    }
}
