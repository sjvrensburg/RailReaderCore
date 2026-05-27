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
    public void ModelOrderResolver_EmptyListIsNoop()
    {
        var empty = new List<LayoutBlock>();
        new ModelOrderResolver().AssignOrder(empty, 100, 100);
        Assert.Empty(empty);
    }
}
