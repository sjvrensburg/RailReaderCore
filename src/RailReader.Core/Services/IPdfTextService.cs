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
}
