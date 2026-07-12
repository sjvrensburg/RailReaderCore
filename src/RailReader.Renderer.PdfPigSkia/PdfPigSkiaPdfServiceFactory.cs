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
    public IPdfService CreatePdfService(string filePath, string? password = null)
        => new PdfPigSkiaPdfService(filePath, password);

    // The Core.PdfPig services open their own short-lived PdfDocument per call;
    // they are wrapped in the gate here so every PdfPig touch made through this
    // backend (UI-thread renders, thread-pool text extraction) is serialised by
    // the same PdfPigGate.Lock — Core.PdfPig itself cannot reference the gate.

    public IPdfTextService CreatePdfTextService()
        => new GatedPdfPigTextService(new PdfTextService());

    public IPdfLinkService CreatePdfLinkService()
        => new GatedPdfPigLinkService(new PdfLinkService());
}
