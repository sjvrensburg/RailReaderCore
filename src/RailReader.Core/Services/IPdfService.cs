using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Opaque handle to a rendered page. Platform implementations wrap their native
/// bitmap type (e.g. SKBitmap, CGImage).
/// </summary>
public interface IRenderedPage : IDisposable
{
    int Width { get; }
    int Height { get; }
}


/// <summary>
/// Rendering-library-agnostic PDF service.
/// </summary>
public interface IPdfService
{
    byte[] PdfBytes { get; }

    /// <summary>
    /// The password the document was opened with, or null for an unencrypted PDF.
    /// Carried so the stateless text/link services (which re-open the document on
    /// every call) can unlock the same encrypted document.
    /// </summary>
    string? Password => null;

    int PageCount { get; }
    List<OutlineEntry> Outline { get; }

    (double Width, double Height) GetPageSize(int pageIndex);
    IRenderedPage RenderPage(int pageIndex, int dpi = 200);
    IRenderedPage RenderThumbnail(int pageIndex);

    /// <summary>
    /// Renders a page to RGB bytes at the given target pixel size (for ONNX analysis).
    /// </summary>
    (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize);

    // --- View-rotation overloads ---------------------------------------------------
    // viewRotation is a user-requested extra rotation in clockwise quarter-turns
    // (0–3), composed on top of the page's own /Rotate attribute. Default
    // implementations ignore it so existing IPdfService implementations keep
    // compiling and behaving; rotation-aware backends override them.

    /// <summary>Displayed page size under an extra view rotation (axes swap on odd turns).</summary>
    (double Width, double Height) GetPageSize(int pageIndex, int viewRotation)
        => GetPageSize(pageIndex);

    /// <summary>Renders a page with an extra view rotation applied.</summary>
    IRenderedPage RenderPage(int pageIndex, int dpi, int viewRotation)
        => RenderPage(pageIndex, dpi);

    /// <summary>Renders a thumbnail with an extra view rotation applied.</summary>
    IRenderedPage RenderThumbnail(int pageIndex, int viewRotation)
        => RenderThumbnail(pageIndex);

    /// <summary>Renders the analysis pixmap with an extra view rotation applied.</summary>
    (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize, int viewRotation)
        => RenderPagePixmap(pageIndex, targetSize);
}
