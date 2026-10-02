using NetTopologySuite.Geometries;
using RailReader.Core.Analysis;
using RailReader.Core.Models;
using Xunit;
using PointF = RailReader.Core.Models.PointF;

namespace RailReader.Core.Tests;

public class MaskQuadExtractorTests
{
    private const int MaskSize = 200;
    private const float CanvasPerMask = 4f; // 800-px canvas, 200-px mask

    /// <summary>
    /// Rasterises a rectangle of <paramref name="w"/>×<paramref name="h"/> mask
    /// pixels centred on (<paramref name="cx"/>, <paramref name="cy"/>), rotated
    /// clockwise on screen by <paramref name="degrees"/>, into a binary mask.
    /// </summary>
    private static int[] RotatedRectMask(float cx, float cy, float w, float h, float degrees)
    {
        var mask = new int[MaskSize * MaskSize];
        float rad = degrees * MathF.PI / 180f, cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        for (int y = 0; y < MaskSize; y++)
        for (int x = 0; x < MaskSize; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            // Rotate the sample back into the rectangle's own frame.
            float u = dx * cos + dy * sin, v = -dx * sin + dy * cos;
            if (MathF.Abs(u) <= w / 2 && MathF.Abs(v) <= h / 2) mask[y * MaskSize + x] = 1;
        }
        return mask;
    }

    private static (float x0, float y0, float x1, float y1) CanvasBounds(int[] mask)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < MaskSize; y++)
        for (int x = 0; x < MaskSize; x++)
        {
            if (mask[y * MaskSize + x] == 0) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return (minX * CanvasPerMask, minY * CanvasPerMask, (maxX + 1) * CanvasPerMask, (maxY + 1) * CanvasPerMask);
    }

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    [Fact]
    public void UprightBlock_IsReportedAxisAligned()
    {
        var mask = new int[MaskSize * MaskSize];
        for (int y = 40; y < 90; y++)
        for (int x = 20; x < 150; x++)
            mask[y * MaskSize + x] = 1;

        Assert.True(MaskQuadExtractor.TryExtract(mask, MaskSize, MaskSize, 80, 160, 600, 360, CanvasPerMask,
            out var quad, out bool axisAligned));
        Assert.True(axisAligned);
        Assert.Equal(new PointF(80, 160), quad.TopLeft);
        Assert.Equal(new PointF(600, 360), quad.BottomRight);
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(-12f)]
    [InlineData(4f)]
    [InlineData(-30f)]
    public void RotatedBlock_RecoversAngleAndSize(float degrees)
    {
        var mask = RotatedRectMask(100, 100, 120, 50, degrees);
        var (x0, y0, x1, y1) = CanvasBounds(mask);

        Assert.True(MaskQuadExtractor.TryExtract(mask, MaskSize, MaskSize, x0, y0, x1, y1, CanvasPerMask,
            out var quad, out bool axisAligned));
        Assert.False(axisAligned);
        Assert.InRange(quad.AngleDegrees, degrees - 1.5f, degrees + 1.5f);

        // Side lengths in canvas px: 120×50 mask px, allowing one mask pixel of quantisation.
        Assert.InRange(Dist(quad.TopLeft, quad.TopRight), 120 * CanvasPerMask - 6, 120 * CanvasPerMask + 6);
        Assert.InRange(Dist(quad.TopRight, quad.BottomRight), 50 * CanvasPerMask - 6, 50 * CanvasPerMask + 6);

        // Centre preserved.
        float cx = (quad.TopLeft.X + quad.TopRight.X + quad.BottomRight.X + quad.BottomLeft.X) / 4;
        float cy = (quad.TopLeft.Y + quad.TopRight.Y + quad.BottomRight.Y + quad.BottomLeft.Y) / 4;
        Assert.InRange(cx, 100 * CanvasPerMask - 4, 100 * CanvasPerMask + 4);
        Assert.InRange(cy, 100 * CanvasPerMask - 4, 100 * CanvasPerMask + 4);
    }

    [Fact]
    public void TinyTilt_BelowMaskResolution_IsAxisAligned()
    {
        // 0.2° over a 60-px-long block drifts ~0.2 mask px end to end.
        var mask = RotatedRectMask(100, 100, 60, 20, 0.2f);
        var (x0, y0, x1, y1) = CanvasBounds(mask);
        Assert.True(MaskQuadExtractor.TryExtract(mask, MaskSize, MaskSize, x0, y0, x1, y1, CanvasPerMask,
            out _, out bool axisAligned));
        Assert.True(axisAligned);
    }

    [Fact]
    public void PixelsOutsideTheDetectionBox_AreIgnored()
    {
        var mask = new int[MaskSize * MaskSize];
        for (int y = 40; y < 90; y++)
        for (int x = 20; x < 150; x++)
            mask[y * MaskSize + x] = 1;
        mask[190 * MaskSize + 190] = 1; // stray pixel far from the box

        Assert.True(MaskQuadExtractor.TryExtract(mask, MaskSize, MaskSize, 80, 160, 600, 360, CanvasPerMask,
            out var quad, out _));
        Assert.Equal(new PointF(600, 360), quad.BottomRight);
    }

    [Fact]
    public void EmptyMaskInsideBox_ReturnsFalse()
    {
        var mask = new int[MaskSize * MaskSize];
        mask[190 * MaskSize + 190] = 1;
        Assert.False(MaskQuadExtractor.TryExtract(mask, MaskSize, MaskSize, 80, 160, 600, 360, CanvasPerMask,
            out _, out _));
    }

    [Fact]
    public void OrderCorners_IsClockwiseFromTopLeft()
    {
        // Mirrors squigl's order_quad test: a tilted rectangle given out of order.
        var ring = new[]
        {
            new Coordinate(6, 0), new Coordinate(0, 5), new Coordinate(14, 20), new Coordinate(20, 15),
            new Coordinate(6, 0),
        };
        var ordered = MaskQuadExtractor.OrderCorners(ring);
        Assert.Equal([new PointF(0, 5), new PointF(6, 0), new PointF(20, 15), new PointF(14, 20)], ordered);
    }

    /// <summary>A block whose quad is its bbox rotated about the bbox centre.</summary>
    private static LayoutBlock TiltedBlock(float x, float y, float degrees)
    {
        var bbox = new BBox(x, y, 200, 40);
        float cx = x + 100, cy = y + 20, rad = degrees * MathF.PI / 180f, cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        PointF Rot(PointF p) => new(cx + (p.X - cx) * cos - (p.Y - cy) * sin, cy + (p.X - cx) * sin + (p.Y - cy) * cos);
        var q = BlockQuad.FromBBox(bbox);
        return new LayoutBlock { BBox = bbox, Quad = new BlockQuad(Rot(q.TopLeft), Rot(q.TopRight), Rot(q.BottomRight), Rot(q.BottomLeft)) };
    }

    [Fact]
    public void LoneTilt_OnUprightPage_IsSnappedToBBox()
    {
        var blocks = new List<LayoutBlock>
        {
            TiltedBlock(0, 0, 0), TiltedBlock(0, 100, 0), TiltedBlock(0, 200, -0.6f), TiltedBlock(0, 300, 0),
        };
        MaskQuadExtractor.SnapUncorroboratedTilts(blocks);
        Assert.All(blocks, b => Assert.Equal(BlockQuad.FromBBox(b.BBox), b.Quad));
    }

    [Fact]
    public void DisagreeingTilts_AreAllSnapped()
    {
        // Two small leans in opposite directions plus a stray steep one: none corroborated.
        var blocks = new List<LayoutBlock> { TiltedBlock(0, 0, 0.5f), TiltedBlock(0, 100, -0.7f), TiltedBlock(0, 200, -26f) };
        MaskQuadExtractor.SnapUncorroboratedTilts(blocks);
        Assert.All(blocks, b => Assert.Equal(0f, b.Quad!.Value.AngleDegrees));
    }

    [Fact]
    public void TwoAgreeingTilts_AreNotEnough()
    {
        var blocks = new List<LayoutBlock> { TiltedBlock(0, 0, 3f), TiltedBlock(0, 100, 3.2f) };
        MaskQuadExtractor.SnapUncorroboratedTilts(blocks);
        Assert.All(blocks, b => Assert.Equal(0f, b.Quad!.Value.AngleDegrees));
    }

    [Fact]
    public void PageWideTilt_IsKept_AndAnOutlierIsSnapped()
    {
        var blocks = new List<LayoutBlock>
        {
            TiltedBlock(0, 0, 3f), TiltedBlock(0, 100, 2.2f), TiltedBlock(0, 200, 3.6f),
            TiltedBlock(0, 300, 0), // unresolvable block: stays upright, doesn't vote
            TiltedBlock(0, 400, -0.6f), // shape noise: disagrees with the page
        };
        MaskQuadExtractor.SnapUncorroboratedTilts(blocks);
        Assert.InRange(blocks[0].Quad!.Value.AngleDegrees, 2.9f, 3.1f);
        Assert.InRange(blocks[1].Quad!.Value.AngleDegrees, 2.1f, 2.3f);
        Assert.InRange(blocks[2].Quad!.Value.AngleDegrees, 3.5f, 3.7f);
        Assert.Equal(BlockQuad.FromBBox(blocks[3].BBox), blocks[3].Quad);
        Assert.Equal(BlockQuad.FromBBox(blocks[4].BBox), blocks[4].Quad);
    }

    [Fact]
    public void BlocksWithoutQuads_AreIgnored()
    {
        var blocks = new List<LayoutBlock> { new() { BBox = new BBox(0, 0, 10, 10) }, TiltedBlock(0, 100, 4f) };
        MaskQuadExtractor.SnapUncorroboratedTilts(blocks);
        Assert.Null(blocks[0].Quad);
        Assert.Equal(0f, blocks[1].Quad!.Value.AngleDegrees);
    }

    [Fact]
    public void BlockQuad_FromBBox_HasZeroAngle()
    {
        var q = BlockQuad.FromBBox(new BBox(10, 20, 30, 40));
        Assert.Equal(0f, q.AngleDegrees);
        Assert.Equal(new PointF(40, 60), q.BottomRight);
    }
}
