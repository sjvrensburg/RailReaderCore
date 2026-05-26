using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.PdfPig;
using RailReader.Core.Services;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;

namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Pure-managed implementation of <see cref="IPdfService"/> targeting
/// Lite (Avalonia.Browser), web, and mobile consumers that cannot ship
/// PDFium. Mirrors <c>RailReader.Renderer.Skia.SkiaPdfService</c>'s shape
/// so the same callers swap factories without touching call sites.
///
/// <para>Outline is parsed via <see cref="PdfOutlineService"/> from
/// <c>RailReader.Core.PdfPig</c>. Page rendering is delegated to
/// <c>PdfPig.Rendering.Skia</c>'s <c>GetPageAsSKBitmap</c>. Every PdfPig
/// call is held under <c>PdfPigGate.Lock</c> since <c>PdfDocument</c> is
/// not documented thread-safe.</para>
/// </summary>
public sealed class PdfPigSkiaPdfService : IPdfService
{
    private const float PointsPerInch = 72f;

    public byte[] PdfBytes { get; }
    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    public PdfPigSkiaPdfService(string filePath)
    {
        PdfBytes = File.ReadAllBytes(filePath);
        lock (PdfPigGate.Lock)
        {
            using var doc = PdfDocument.Open(PdfBytes);
            PageCount = doc.NumberOfPages;
        }
        // Outline service opens its own PdfDocument; we keep the call
        // inside the gate to avoid two open documents on the same byte[]
        // racing through PdfPig's shared parsing state.
        lock (PdfPigGate.Lock)
        {
            Outline = new PdfOutlineService().Extract(PdfBytes);
        }
        if (Outline.Count > 0)
            RailReaderLogging.Logger.Debug($"[PdfPigSkia] Extracted {Outline.Count} outline entries");
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (PdfPigGate.Lock)
        {
            using var doc = PdfDocument.Open(PdfBytes);
            // PdfPig is 1-indexed; Core's IPdfService is 0-indexed. Using
            // base PdfPig's Page.Width/Height avoids requiring the Skia
            // page-size factory registration that GetPageSize-the-extension
            // would otherwise need.
            var page = doc.GetPage(pageIndex + 1);
            return (page.Width, page.Height);
        }
    }

    public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
    {
        // SkiaSharp.GetPageAsSKBitmap renders at PDF native (72 DPI) ×
        // scale; convert the caller's DPI request into a scale factor.
        float scale = dpi / PointsPerInch;
        return new PdfPigSkiaRenderedPage(RenderAt(pageIndex, scale));
    }

    public IRenderedPage RenderThumbnail(int pageIndex)
    {
        var (pixW, pixH) = FitPageToTarget(pageIndex, 200);
        var bmp = RenderAtPixelSize(pageIndex, pixW, pixH);
        return new PdfPigSkiaRenderedPage(bmp);
    }

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
    {
        SKBitmap bitmap;
        var (pixW, pixH) = FitPageToTarget(pageIndex, targetSize);
        bitmap = RenderAtPixelSize(pageIndex, pixW, pixH);

        // BGRA→RGB conversion is pure CPU; run outside the gate so other
        // renders/text-extracts aren't blocked. Mirrors SkiaPdfService.
        try
        {
            var pixels = bitmap.GetPixelSpan();
            int pixelCount = bitmap.Width * bitmap.Height;
            var rgb = new byte[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                int src = i * 4;
                int dst = i * 3;
                rgb[dst]     = pixels[src + 2];
                rgb[dst + 1] = pixels[src + 1];
                rgb[dst + 2] = pixels[src];
            }
            return (rgb, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private SKBitmap RenderAt(int pageIndex, float scale)
    {
        lock (PdfPigGate.Lock)
        {
            using var doc = PdfDocument.Open(PdfBytes);
            doc.AddSkiaPageFactory();
            return doc.GetPageAsSKBitmap(pageIndex + 1, scale, SKColors.White);
        }
    }

    /// <summary>
    /// Renders to an exact pixel size. PdfPig.Rendering.Skia's
    /// <c>GetPageAsSKBitmap</c> only accepts a uniform scale, so we
    /// compute the scale that fits the longest edge to the target. The
    /// resulting bitmap may be slightly smaller than the requested
    /// (W,H) on the shorter edge — same as <c>SkiaPdfService</c>'s
    /// behaviour.
    /// </summary>
    private SKBitmap RenderAtPixelSize(int pageIndex, int pixW, int pixH)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(pixW / pageW, pixH / pageH);
        return RenderAt(pageIndex, (float)scale);
    }

    private (int Width, int Height) FitPageToTarget(int pageIndex, int targetSize)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(targetSize / pageW, targetSize / pageH);
        return (Math.Max(1, (int)(pageW * scale)), Math.Max(1, (int)(pageH * scale)));
    }
}
