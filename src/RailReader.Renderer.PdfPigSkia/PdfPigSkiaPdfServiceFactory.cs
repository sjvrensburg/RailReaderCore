using RailReader.Core.PdfPig;
using RailReader.Core.Services;

namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Composition root for Lite/web/mobile consumers: hands out the
/// PdfPig-backed text and link services from <c>RailReader.Core.PdfPig</c>
/// alongside a <see cref="PdfPigSkiaPdfService"/> for rasterisation.
///
/// <para>
/// Mirrors <c>RailReader.Renderer.Skia.SkiaPdfServiceFactory</c>'s shape
/// so callers swap factories at startup without touching their wiring.
/// Drop one in via <see cref="IPdfServiceFactory"/> for the entire
/// platform-boundary surface.
/// </para>
/// </summary>
public sealed class PdfPigSkiaPdfServiceFactory : IPdfServiceFactory
{
    public IPdfService CreatePdfService(string filePath)
        => new PdfPigSkiaPdfService(filePath);

    public IPdfTextService CreatePdfTextService()
        => new PdfTextService();

    public IPdfLinkService CreatePdfLinkService()
        => new PdfLinkService();
}
