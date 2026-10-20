namespace RailReader.Core.Services;

/// <summary>
/// Optional <see cref="IPdfService"/> capability: renders a page in horizontal bands, releasing the
/// backend's render lock between bands and checking <c>ct</c> before each one. Core discovers it by
/// casting the service it was handed (a decorator must forward it) and uses it for the background
/// DPI upgrade, so a UI-thread render — the next page change's preview — waits for one band rather
/// than a whole full-resolution page (issue #147). Backends without it render the page in one piece.
/// </summary>
public interface IPdfTiledRenderService
{
    /// <summary>
    /// Renders the whole page at <paramref name="dpi"/> under an extra view rotation, as
    /// <see cref="IPdfService.RenderPage(int, int, int)"/> would, but band by band. Throws
    /// <see cref="OperationCanceledException"/> between bands once <paramref name="ct"/> is cancelled.
    /// </summary>
    IRenderedPage RenderPageTiled(int pageIndex, int dpi, int viewRotation, CancellationToken ct);
}
