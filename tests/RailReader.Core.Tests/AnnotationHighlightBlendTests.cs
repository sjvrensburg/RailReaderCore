using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Highlights composite with Multiply so /CA 1 does not hide text (#131).</summary>
public class AnnotationHighlightBlendTests
{
    private static HighlightAnnotation Hl(float opacity) => new()
    {
        Color = "#FFFF00",
        Opacity = opacity,
        Rects = [new(0, 0, 20, 20)],
    };

    private static SKBitmap Draw(HighlightAnnotation h, SKColor bg, SKColor? ink, bool dark = false)
    {
        var bmp = new SKBitmap(20, 20);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(bg);
        if (ink is { } c)
        {
            using var p = new SKPaint { Color = c };
            canvas.DrawRect(SKRect.Create(5, 5, 10, 10), p);
        }
        AnnotationRenderer.DrawAnnotation(canvas, h, false, darkBackdrop: dark);
        canvas.Flush();
        return bmp;
    }

    [Fact]
    public void OpaqueHighlight_KeepsBlackTextDark()
    {
        using var bmp = Draw(Hl(1f), SKColors.White, SKColors.Black);
        var px = bmp.GetPixel(10, 10);
        Assert.True(px.Red < 20 && px.Green < 20 && px.Blue < 20);
        Assert.Equal(new SKColor(255, 255, 0), bmp.GetPixel(1, 1));
    }

    [Fact]
    public void TranslucentHighlight_OnWhite_MatchesSrcOver()
    {
        using var bmp = Draw(Hl(0.4f), SKColors.White, null);
        var px = bmp.GetPixel(1, 1);
        Assert.InRange(px.Red, 253, 255);
        Assert.InRange(px.Blue, 150, 156); // 255 * 0.6
    }

    [Fact]
    public void DarkBackdrop_HighlightStillVisible_AndAlphaCapped()
    {
        using var bmp = Draw(Hl(1f), SKColors.Black, null, dark: true);
        var px = bmp.GetPixel(1, 1);
        Assert.InRange(px.Red, 98, 106); // 255 * 0.4 over black
    }

    [Fact]
    public void Rendering_DoesNotMutateOpacity()
    {
        var h = Hl(1f);
        using var _ = Draw(h, SKColors.White, null, dark: true);
        Assert.Equal(1f, h.Opacity);
    }
}
