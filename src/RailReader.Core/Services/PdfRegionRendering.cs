using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Region rendering for any <see cref="IPdfService"/> (issue #222): uses the backend's
/// <see cref="IPdfRegionRenderService"/> when it has one, and otherwise renders the whole page at
/// the requested scale and crops it — the same pixels, at the whole-page cost the capability exists
/// to avoid.
/// </summary>
public static class PdfRegionRendering
{
    /// <summary>
    /// Renders <paramref name="regionInPagePoints"/> of a page at <paramref name="scale"/> pixels per
    /// point. See <see cref="IPdfRegionRenderService.RenderRegionPixmap"/> for the coordinate space,
    /// clamping and pixel snapping, which the fallback follows as closely as the backend's
    /// whole-page render allows: <see cref="IPdfService.RenderPagePixmap(int, int, int)"/> takes a
    /// longest-edge pixel count rather than a scale, so its raster can come out up to a pixel smaller
    /// than the page at exactly <paramref name="scale"/>, and the crop is taken at the scale it
    /// actually used.
    /// </summary>
    public static (byte[] RgbBytes, int Width, int Height) RenderRegionPixmap(this IPdfService pdf,
        int pageIndex, BBox regionInPagePoints, double scale, int viewRotation = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf is IPdfRegionRenderService region)
            return region.RenderRegionPixmap(pageIndex, regionInPagePoints, scale, viewRotation, ct);

        ct.ThrowIfCancellationRequested();
        var (pageW, pageH) = pdf.GetPageSize(pageIndex, viewRotation);
        // Validates scale and the region before paying for a page render.
        PixelBounds(regionInPagePoints, pageW, pageH, scale);

        int targetSize = (int)Math.Ceiling(Math.Max(pageW, pageH) * scale);
        var (rgb, w, h) = pdf.RenderPagePixmap(pageIndex, targetSize, viewRotation);
        double actualScale = Math.Min(w / pageW, h / pageH);
        var (x0, y0, x1, y1) = PixelBounds(regionInPagePoints, pageW, pageH, actualScale);
        // The raster can be a pixel short of pageW x actualScale (truncation): keep the crop inside it.
        x0 = Math.Min(x0, w - 1);
        y0 = Math.Min(y0, h - 1);
        x1 = Math.Min(x1, w);
        y1 = Math.Min(y1, h);
        return Crop(rgb, w, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    /// <summary>
    /// The page-raster pixel rectangle [x0, x1) × [y0, y1) covering <paramref name="region"/> at
    /// <paramref name="scale"/>, clamped to the page and snapped outward to whole pixels.
    /// </summary>
    internal static (int X0, int Y0, int X1, int Y1) PixelBounds(BBox region, double pageW, double pageH, double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive and finite.");

        double left = Math.Max(0, region.X), top = Math.Max(0, region.Y);
        double right = Math.Min(pageW, (double)region.X + region.W);
        double bottom = Math.Min(pageH, (double)region.Y + region.H);
        if (!(right > left) || !(bottom > top))
            throw new ArgumentOutOfRangeException(nameof(region), region,
                $"Region does not intersect the {pageW:0.#} x {pageH:0.#} pt page.");

        int x0 = (int)Math.Floor(left * scale), y0 = (int)Math.Floor(top * scale);
        int x1 = Math.Max(x0 + 1, (int)Math.Ceiling(right * scale));
        int y1 = Math.Max(y0 + 1, (int)Math.Ceiling(bottom * scale));
        return (x0, y0, x1, y1);
    }

    private static (byte[] RgbBytes, int Width, int Height) Crop(byte[] rgb, int stridePixels,
        int x0, int y0, int width, int height)
    {
        var output = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(rgb, ((y0 + row) * stridePixels + x0) * 3, output, row * width * 3, width * 3);
        return (output, width, height);
    }
}
