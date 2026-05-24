using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class ReadingOrderResolverTests
{
    private static LayoutBlock Make(float x, float y, int order = 0)
        => new() { BBox = new BBox(x, y, 10, 10), Role = BlockRole.Text, Order = order };

    [Fact]
    public void ModelOrderResolver_HonoursExistingOrder()
    {
        // Input deliberately out of order; resolver should restore it via Order.
        var blocks = new List<LayoutBlock>
        {
            Make(0,   0, order: 2),
            Make(100, 0, order: 0),
            Make(50,  0, order: 1),
        };

        new ModelOrderResolver().AssignOrder(blocks, 200, 200);

        Assert.Equal(100f, blocks[0].BBox.X);
        Assert.Equal(50f,  blocks[1].BBox.X);
        Assert.Equal(0f,   blocks[2].BBox.X);
    }

    [Fact]
    public void ModelOrderResolver_RenumbersFromZero()
    {
        var blocks = new List<LayoutBlock>
        {
            Make(0, 0, order: 50),
            Make(0, 1, order: 51),
            Make(0, 2, order: 52),
        };

        new ModelOrderResolver().AssignOrder(blocks, 100, 100);

        Assert.Equal(0, blocks[0].Order);
        Assert.Equal(1, blocks[1].Order);
        Assert.Equal(2, blocks[2].Order);
    }

    [Fact]
    public void TopDownReadingOrderResolver_SortsByYThenX()
    {
        var blocks = new List<LayoutBlock>
        {
            Make(50, 100), // bottom
            Make(0,   0),  // top-left
            Make(50,  0),  // top-right
            Make(0,  50),  // middle-left
        };

        new TopDownReadingOrderResolver().AssignOrder(blocks, 200, 200);

        // (0,0), (50,0), (0,50), (50,100)
        Assert.Equal(0f,   blocks[0].BBox.X);
        Assert.Equal(0f,   blocks[0].BBox.Y);
        Assert.Equal(50f,  blocks[1].BBox.X);
        Assert.Equal(0f,   blocks[1].BBox.Y);
        Assert.Equal(0f,   blocks[2].BBox.X);
        Assert.Equal(50f,  blocks[2].BBox.Y);
        Assert.Equal(50f,  blocks[3].BBox.X);
        Assert.Equal(100f, blocks[3].BBox.Y);
    }

    [Fact]
    public void TopDownReadingOrderResolver_IgnoresExistingOrder()
    {
        // Order hints are nonsense; resolver should overwrite them.
        var blocks = new List<LayoutBlock>
        {
            Make(0, 0,  order: 99),
            Make(0, 10, order: 0),
        };

        new TopDownReadingOrderResolver().AssignOrder(blocks, 100, 100);

        Assert.Equal(0, blocks[0].Order);
        Assert.Equal(0f, blocks[0].BBox.Y);
        Assert.Equal(1, blocks[1].Order);
        Assert.Equal(10f, blocks[1].BBox.Y);
    }

    [Fact]
    public void Resolvers_EmptyListIsNoop()
    {
        var empty = new List<LayoutBlock>();
        new ModelOrderResolver().AssignOrder(empty, 100, 100);
        Assert.Empty(empty);

        new TopDownReadingOrderResolver().AssignOrder(empty, 100, 100);
        Assert.Empty(empty);
    }
}
