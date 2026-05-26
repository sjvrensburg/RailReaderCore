using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Wraps an <see cref="SKBitmap"/> as an <see cref="IRenderedPage"/>.
/// Identical shape to <c>RailReader.Renderer.Skia.SkiaRenderedPage</c>,
/// duplicated here so this package does not have to take a project
/// reference on the PDFium-bound renderer.
/// </summary>
public sealed class PdfPigSkiaRenderedPage : IRenderedPage
{
    public SKBitmap Bitmap { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public PdfPigSkiaRenderedPage(SKBitmap bitmap) => Bitmap = bitmap;

    public void Dispose() => Bitmap.Dispose();
}
