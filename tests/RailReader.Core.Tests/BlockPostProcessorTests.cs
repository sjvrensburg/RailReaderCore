using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class BlockPostProcessorTests
{
    private static LayoutBlock Block(float x, float y, float w, float h, BlockRole role = BlockRole.Text, int order = 0)
        => new() { BBox = new BBox(x, y, w, h), Role = role, Confidence = 0.9f, Order = order };

    [Fact]
    public void ResolveVerticalOverlaps_TrimsLaterBlock()
    {
        // Two stacked blocks sharing full X; b's top overlaps a's bottom by 10pt.
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 50, order: 0),
            Block(0, 40, 100, 50, order: 1),
        };

        BlockPostProcessor.ResolveVerticalOverlaps(blocks);

        Assert.Equal(50f, blocks[1].BBox.Y);
        Assert.Equal(40f, blocks[1].BBox.H);
    }

    [Fact]
    public void ResolveVerticalOverlaps_LeavesNonOverlappingAlone()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 50, order: 0),
            Block(0, 100, 100, 50, order: 1),
        };

        BlockPostProcessor.ResolveVerticalOverlaps(blocks);

        Assert.Equal(100f, blocks[1].BBox.Y);
        Assert.Equal(50f, blocks[1].BBox.H);
    }

    [Fact]
    public void ResolveVerticalOverlaps_SkipsHorizontallyDisjointBlocks()
    {
        // Side-by-side columns: overlap in Y but not in X — must leave both alone.
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 100, order: 0),
            Block(120, 50, 100, 100, order: 1),
        };

        BlockPostProcessor.ResolveVerticalOverlaps(blocks);

        Assert.Equal(50f, blocks[1].BBox.Y);
        Assert.Equal(100f, blocks[1].BBox.H);
    }

    [Fact]
    public void PostProcess_AtomicRoleBlockGetsSingleLine()
    {
        var blocks = new List<LayoutBlock>
        {
            Block(0, 0, 100, 60, role: BlockRole.Figure),
        };

        BlockPostProcessor.PostProcess(blocks, rgbBytes: [], imgW: 0, imgH: 0,
            scaleX: 1, scaleY: 1, charBoxes: null);

        Assert.Single(blocks[0].Lines);
    }

    [Fact]
    public void PostProcess_FallsBackToBlockCentreWhenNoLinesDetected()
    {
        // Text block, no chars, all-white pixmap (no dark pixels → no lines).
        // PostProcess must ensure at least one line covering the block.
        const int imgW = 100;
        const int imgH = 100;
        var white = new byte[imgW * imgH * 3];
        Array.Fill(white, (byte)255);

        var blocks = new List<LayoutBlock>
        {
            Block(10, 20, 80, 60, role: BlockRole.Text),
        };

        BlockPostProcessor.PostProcess(blocks, white, imgW, imgH,
            scaleX: 1, scaleY: 1, charBoxes: null);

        Assert.Single(blocks[0].Lines);
        Assert.Equal(60f, blocks[0].Lines[0].Height);
    }
}
