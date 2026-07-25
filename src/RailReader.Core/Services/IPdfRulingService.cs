using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Optional capability: extraction of a page's axis-aligned ruling lines from its vector
/// content.
///
/// <para>
/// This is an <i>opt-in extra</i> rather than a required service, and is discovered by testing
/// whether the platform's <see cref="IPdfTextService"/> also implements it. That keeps the
/// wiring unchanged for every consumer — a backend that can read page paths (currently
/// <c>RailReader.Core.PdfPig</c>) gains exact table grids automatically, and one that cannot
/// simply keeps the raster-projection fallback, with no factory or constructor churn on
/// either side. The inputs are identical to text extraction's, so implementing both on one
/// type also means one document parse serves both.
/// </para>
/// </summary>
public interface IPdfRulingService
{
    /// <summary>
    /// Returns the page's ruling lines in page points, displayed frame (top-left origin,
    /// Y-down). Never null; an empty result means the page has no vector rules — which is the
    /// normal answer for a scan, and indistinguishable from a backend that found none.
    /// </summary>
    PageRulings ExtractRulings(byte[] pdfBytes, int pageIndex, string? password = null);

    /// <summary>Rulings in the view-rotated display frame (clockwise quarter-turns, 0–3).</summary>
    PageRulings ExtractRulings(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
        => ExtractRulings(pdfBytes, pageIndex, password);
}
