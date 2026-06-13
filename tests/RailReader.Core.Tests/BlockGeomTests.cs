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
    public void MarkColumnBlocks_FlagsOnlyTheSideBySidePair()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 200, 20),   // [0] full-width title (above both columns)
            Block(0, 40, 80, 100),  // [1] left column
            Block(120, 40, 80, 100),// [2] right column (beside [1])
        };

        var marks = BlockGeom.MarkColumnBlocks(blocks);

        Assert.False(marks[0]); // title has nothing beside it
        Assert.True(marks[1]);  // flanked by [2]
        Assert.True(marks[2]);  // flanked by [1]
    }

    [Fact]
    public void MarkColumnBlocks_EmptyInputIsEmpty()
    {
        Assert.Empty(BlockGeom.MarkColumnBlocks(new List<LayoutBlock>()));
    }
}
