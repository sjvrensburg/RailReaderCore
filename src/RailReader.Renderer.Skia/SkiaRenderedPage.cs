using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Wraps an SKBitmap as an IRenderedPage. Exposes the bitmap for callers
/// that need the underlying Skia type (e.g. GPU image upload).
/// </summary>
/// <remarks>
/// The bitmap is marked <b>immutable</b> on construction. A rasterised page is never drawn into
/// again — every consumer (viewport image wrap, screenshot compositor, block crops, freeze panes,
/// CLI render) only reads it — and immutability is what lets <c>SKImage.FromBitmap</c> share the
/// pixels instead of copying them: on a mutable bitmap Skia copies the whole buffer (measured
/// 90 ms at 300 DPI, 209 ms at 450 DPI, 370 ms at 600 DPI for a Letter page), and the desktop
/// host does that wrap on the UI thread every time a DPI tier or a continuous-scroll neighbour
/// lands. With the flag set the wrap is ~0.01 ms.
/// </remarks>
public sealed class SkiaRenderedPage : IRenderedPage
{
    public SKBitmap Bitmap { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public SkiaRenderedPage(SKBitmap bitmap)
    {
        bitmap.SetImmutable();
        Bitmap = bitmap;
    }

    public void Dispose() => Bitmap.Dispose();
}
