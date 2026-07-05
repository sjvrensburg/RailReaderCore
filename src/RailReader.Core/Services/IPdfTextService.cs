using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Rendering-library-agnostic PDF text extraction.
/// </summary>
public interface IPdfTextService
{
    PageText ExtractPageText(byte[] pdfBytes, int pageIndex, string? password = null);
    List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges, string? password = null);

    // --- View-rotation overloads ---------------------------------------------------
    // viewRotation is an extra clockwise quarter-turn count (0–3) composed on top
    // of the page's /Rotate, so returned geometry matches a pixmap rendered with
    // the same view rotation. Defaults ignore it (backwards compatible).

    /// <summary>Extracts text with char boxes in the view-rotated display frame.</summary>
    PageText ExtractPageText(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
        => ExtractPageText(pdfBytes, pageIndex, password);

    /// <summary>Range rects in the view-rotated display frame.</summary>
    List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges, int viewRotation, string? password = null)
        => GetTextRangeRects(pdfBytes, pageIndex, ranges, password);
}
