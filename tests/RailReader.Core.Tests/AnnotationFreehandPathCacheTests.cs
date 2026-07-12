using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the freehand SKPath cache: it used to be invalidated
/// only by point COUNT, so an in-place move/resize (which mutates
/// <c>Points[i]</c> on the same instance without changing the count — exactly
/// what <c>AnnotationInteractionHandler.MoveAnnotation</c>/<c>ResizeFreehand</c>
/// do) kept rendering the stale pre-drag path. The cache now keys on a hash of
/// the full point geometry.
/// </summary>
public class AnnotationFreehandPathCacheTests
{
    private static SKBitmap Draw(FreehandAnnotation ann, int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        AnnotationRenderer.DrawAnnotation(canvas, ann, isSelected: false);
        canvas.Flush();
        return bmp;
    }

    private static int NonWhitePixels(SKBitmap bmp, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250) n++;
            }
        return n;
    }

    private static FreehandAnnotation MakeStroke() => new()
    {
        Color = "#FF0000",
        Opacity = 1f,
        StrokeWidth = 4f,
        Points = [new(10, 10), new(40, 10), new(40, 40)],
    };

    [Fact]
    public void MoveInPlace_SameCount_RendersAtNewPosition()
    {
        var ann = MakeStroke();

        // Prime the cache: stroke draws in the old region.
        using (var first = Draw(ann, 100, 100))
            Assert.True(NonWhitePixels(first, 0, 0, 50, 50) > 0, "stroke must draw at its original position");

        // In-place move (same count, same instance) — what MoveAnnotation does.
        for (int i = 0; i < ann.Points.Count; i++)
            ann.Points[i] = new PointF(ann.Points[i].X + 45, ann.Points[i].Y + 45);

        using var second = Draw(ann, 100, 100);
        Assert.Equal(0, NonWhitePixels(second, 0, 0, 50, 50));
        Assert.True(NonWhitePixels(second, 50, 50, 100, 100) > 0,
            "a moved stroke must render at its new position, not the cached pre-drag one");
    }

    [Fact]
    public void ResizeInPlace_SameCount_RendersAtNewGeometry()
    {
        var ann = MakeStroke();
        using (Draw(ann, 100, 100)) { } // prime cache

        // In-place scale ×2 about the origin — what ResizeFreehand does.
        for (int i = 0; i < ann.Points.Count; i++)
            ann.Points[i] = new PointF(ann.Points[i].X * 2, ann.Points[i].Y * 2);

        using var resized = Draw(ann, 100, 100);
        // The scaled stroke reaches (80, 80); the stale one never leaves (10..40).
        Assert.True(NonWhitePixels(resized, 60, 60, 100, 100) > 0,
            "a resized stroke must render at its new geometry");
    }

    [Fact]
    public void UnchangedPoints_StillRenders()
    {
        var ann = MakeStroke();
        using (Draw(ann, 100, 100)) { } // prime cache
        using var again = Draw(ann, 100, 100); // cache hit path
        Assert.True(NonWhitePixels(again, 0, 0, 50, 50) > 0);
    }
}
