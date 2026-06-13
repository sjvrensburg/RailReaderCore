using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Locks the shared side-by-side / overlap contract that both
/// <see cref="XYCutPlusPlusResolver"/> (density guard) and <see cref="RailNav"/>
/// (chunking) consume. A change here is meant to move both consumers together —
/// these tests pin the geometry so a tweak is a deliberate, visible decision.
/// </summary>
public class BlockGeomTests
{
    private static LayoutBlock Block(float x, float y, float w, float h)
        => new() { BBox = new BBox(x, y, w, h), Role = BlockRole.Text };

    [Fact]
    public void XOverlap_IsSymmetricAndHalfOpen()
    {
        var a = new BBox(0, 0, 10, 10);
        var b = new BBox(5, 0, 10, 10);
        Assert.True(BlockGeom.XOverlap(a, b));
        Assert.True(BlockGeom.XOverlap(b, a));

        // Touching edges (a ends where b starts) do not overlap.
        var touching = new BBox(10, 0, 10, 10);
        Assert.False(BlockGeom.XOverlap(a, touching));
    }

    [Fact]
    public void YOverlap_IsSymmetricAndHalfOpen()
    {
        var a = new BBox(0, 0, 10, 10);
        var b = new BBox(0, 5, 10, 10);
        Assert.True(BlockGeom.YOverlap(a, b));
        Assert.True(BlockGeom.YOverlap(b, a));

        var touching = new BBox(0, 10, 10, 10);
        Assert.False(BlockGeom.YOverlap(a, touching));
    }

    [Fact]
    public void IsSideBySide_TrueWhenYOverlapsButXDisjoint()
    {
        // Two columns at the same height.
        var left = new BBox(0, 0, 40, 100);
        var right = new BBox(60, 0, 40, 100);
        Assert.True(BlockGeom.IsSideBySide(left, right));
        Assert.True(BlockGeom.IsSideBySide(right, left));
    }

    [Fact]
    public void IsSideBySide_FalseWhenStacked()
    {
        // One above the other in the same column: x-overlap, no side-by-side.
        var top = new BBox(0, 0, 100, 40);
        var bottom = new BBox(0, 60, 100, 40);
        Assert.False(BlockGeom.IsSideBySide(top, bottom));
    }

    [Fact]
    public void IsSideBySide_FalseWhenFullWidthSpannerContainsColumn()
    {
        // A full-width spanner x-overlaps the narrow column beneath it, so it is
        // not "beside" it — this is why the chunk spanner barrier exists.
        var spanner = new BBox(0, 0, 200, 20);
        var column = new BBox(0, 30, 80, 100);
        Assert.False(BlockGeom.IsSideBySide(spanner, column));
    }

    [Fact]
    public void AnySideBySide_DetectsTwoColumns()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 40, 100),   // left column
            Block(60, 0, 40, 100),  // right column, same height
        };
        Assert.True(BlockGeom.AnySideBySide(blocks));
    }

    [Fact]
    public void AnySideBySide_FalseForSingleColumnStack()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 40),
            Block(0, 50, 100, 40),
            Block(0, 100, 100, 40),
        };
        Assert.False(BlockGeom.AnySideBySide(blocks));
    }

    [Fact]
    public void MarkColumnBlocks_FlagsColumnsNotTheFullWidthSpanner()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 200, 20),   // [0] full-width title (above both columns)
            Block(0, 40, 80, 100),  // [1] left column
            Block(120, 40, 80, 100),// [2] right column (beside [1])
        };

        var marks = BlockGeom.MarkColumnBlocks(blocks, pageWidth: 200);

        Assert.False(marks[0]); // full-width spanner is excluded, never a column
        Assert.True(marks[1]);  // genuine left column
        Assert.True(marks[2]);  // genuine right column
    }

    [Fact]
    public void MarkColumnBlocks_StaggeredColumns_FlagsBothWithoutYOverlap()
    {
        // Edge #1: the right column's body sits LOWER than the left's and so y-overlaps
        // no left block. A pairwise "is anything beside me?" test misses it; band
        // detection recognises it by its X-band, with a full-height gutter between them.
        var blocks = new List<LayoutBlock>
        {
            Block(40, 70, 240, 60),   // [0] left column (top)
            Block(320, 200, 240, 60), // [1] right column (staggered low — no y-overlap)
        };

        var marks = BlockGeom.MarkColumnBlocks(blocks, pageWidth: 600);

        Assert.True(marks[0]);
        Assert.True(marks[1]); // flagged despite never vertically overlapping [0]
    }

    [Fact]
    public void MarkColumnBlocks_IncidentalSideFloat_StaysFlagged_DocumentedLimitation()
    {
        // Edge #2 (intentionally NOT fixed — see MarkColumnBlocks remarks): a
        // single-column body with a narrow caption/footnote beside it. The float
        // y-overlaps the body and is x-disjoint, so the pairwise floor flags BOTH.
        // This over-arms the chunk barrier (a title above is split from the body), a
        // benign over-segmentation — both still frame at single-column width, nothing
        // is framed across a gutter. The flag is left set on purpose: clearing it
        // (to merge title+body) is the same "remove a barrier" move that would let a
        // genuine column be framed across the gutter, so the floor is never cleared.
        var blocks = new List<LayoutBlock>
        {
            Block(40, 70, 260, 200), // [0] body (single column)
            Block(320, 70, 100, 80), // [1] narrow side-float beside the body
        };

        var marks = BlockGeom.MarkColumnBlocks(blocks, pageWidth: 600);

        Assert.True(marks[0]); // pairwise floor flags it (documented over-arm)
        Assert.True(marks[1]);
    }

    [Fact]
    public void MarkColumnBlocks_TitleAboveTwoColumns_DetectsColumnsBelow()
    {
        // The common case: a full-width title straddles the gutter. It is excluded from
        // gutter detection (else it would hide the gutter), so the two columns below it
        // are still found.
        var blocks = new List<LayoutBlock>
        {
            Block(40, 40, 520, 24),  // [0] full-width title (straddles the gutter)
            Block(40, 80, 240, 200), // [1] left column
            Block(320, 80, 240, 200),// [2] right column
        };

        var marks = BlockGeom.MarkColumnBlocks(blocks, pageWidth: 600);

        Assert.False(marks[0]);
        Assert.True(marks[1]);
        Assert.True(marks[2]);
    }

    [Fact]
    public void MarkColumnBlocks_SingleColumnStack_FlagsNothing()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(40, 50, 240, 40),
            Block(40, 96, 240, 40),
            Block(40, 142, 240, 40),
        };

        var marks = BlockGeom.MarkColumnBlocks(blocks, pageWidth: 600);

        Assert.All(marks, m => Assert.False(m));
    }

    [Fact]
    public void MarkColumnBlocks_EmptyInputIsEmpty()
    {
        Assert.Empty(BlockGeom.MarkColumnBlocks(new List<LayoutBlock>(), pageWidth: 600));
    }
}
