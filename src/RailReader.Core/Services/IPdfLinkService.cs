using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Rendering-library-agnostic PDF link extraction. Returns clickable link
/// regions in page-point space (origin top-left, Y-down).
/// </summary>
public interface IPdfLinkService
{
    List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, string? password = null);

    /// <summary>
    /// Link rects (and destination page-point positions) in the view-rotated
    /// display frame — viewRotation is an extra clockwise quarter-turn count
    /// (0–3) composed on top of the page's /Rotate. Default ignores it.
    /// </summary>
    List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
        => ExtractPageLinks(pdfBytes, pageIndex, password);
}
