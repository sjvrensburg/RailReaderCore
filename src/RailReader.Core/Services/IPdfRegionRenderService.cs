using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Optional <see cref="IPdfService"/> capability: renders one rectangle of a page at a chosen scale,
/// rasterising only that rectangle (issue #222). A host that wants a small region at high
/// resolution — a crop of an equation for a vision model, say — would otherwise render the whole
/// page at that resolution and crop it: for an 80 pt-wide equation scaled to a fixed pixel width
/// that is a page raster of ~140 MB, held under the backend's render lock throughout.
/// Discovered by casting the service, like <see cref="IPdfTiledRenderService"/> (a decorator must
/// forward it). Most callers want <see cref="PdfRegionRendering.RenderRegionPixmap"/>, which uses
/// this when present and falls back to render-and-crop otherwise.
/// </summary>
public interface IPdfRegionRenderService
{
    /// <summary>
    /// Renders <paramref name="regionInPagePoints"/> of page <paramref name="pageIndex"/> at
    /// <paramref name="scale"/> pixels per point, as packed RGB (3 bytes per pixel, row-major, no
    /// padding) like <see cref="IPdfService.RenderPagePixmap(int, int)"/>.
    /// </summary>
    /// <param name="regionInPagePoints">
    /// The rectangle in displayed page points: the space of
    /// <see cref="IPdfService.GetPageSize(int, int)"/> under the same <paramref name="viewRotation"/>,
    /// which is the space layout blocks' boxes are in. Clamped to the page.
    /// </param>
    /// <param name="scale">Output pixels per page point (72 DPI = 1). Must be positive and finite.</param>
    /// <param name="viewRotation">Extra clockwise quarter-turns, as elsewhere on <see cref="IPdfService"/>.</param>
    /// <param name="ct">Checked before rendering; the render itself is one call.</param>
    /// <returns>
    /// The pixels covering the clamped region, snapped outward to whole pixels of the page raster
    /// at <paramref name="scale"/>: pixel (0, 0) is page point
    /// (⌊X·scale⌋ / scale, ⌊Y·scale⌋ / scale).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scale"/> is not positive and finite, or the region does not intersect the page.
    /// </exception>
    (byte[] RgbBytes, int Width, int Height) RenderRegionPixmap(int pageIndex, BBox regionInPagePoints,
        double scale, int viewRotation = 0, CancellationToken ct = default);
}
