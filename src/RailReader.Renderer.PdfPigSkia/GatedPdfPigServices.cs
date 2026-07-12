using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Serialises <c>RailReader.Core.PdfPig.PdfTextService</c> behind
/// <see cref="PdfPigGate.Lock"/>. The Core.PdfPig services cannot take the
/// gate themselves (it is internal to this assembly, which Core.PdfPig does
/// not reference), so the factory wraps them here — keeping every PdfPig call
/// made through this backend inside the single gate that also serialises
/// <see cref="PdfPigSkiaPdfService"/> rendering.
/// </summary>
internal sealed class GatedPdfPigTextService(IPdfTextService inner) : IPdfTextService
{
    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex, string? password = null)
    {
        lock (PdfPigGate.Lock) return inner.ExtractPageText(pdfBytes, pageIndex, password);
    }

    public PageText ExtractPageText(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
    {
        lock (PdfPigGate.Lock) return inner.ExtractPageText(pdfBytes, pageIndex, viewRotation, password);
    }

    public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges, string? password = null)
    {
        lock (PdfPigGate.Lock) return inner.GetTextRangeRects(pdfBytes, pageIndex, ranges, password);
    }

    public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex,
        List<(int CharStart, int CharLength)> ranges, int viewRotation, string? password = null)
    {
        lock (PdfPigGate.Lock)
            return inner.GetTextRangeRects(pdfBytes, pageIndex, ranges, viewRotation, password);
    }
}

/// <summary>Serialises <c>RailReader.Core.PdfPig.PdfLinkService</c> behind
/// <see cref="PdfPigGate.Lock"/> — see <see cref="GatedPdfPigTextService"/>.</summary>
internal sealed class GatedPdfPigLinkService(IPdfLinkService inner) : IPdfLinkService
{
    public List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, string? password = null)
    {
        lock (PdfPigGate.Lock) return inner.ExtractPageLinks(pdfBytes, pageIndex, password);
    }

    public List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
    {
        lock (PdfPigGate.Lock) return inner.ExtractPageLinks(pdfBytes, pageIndex, viewRotation, password);
    }
}
