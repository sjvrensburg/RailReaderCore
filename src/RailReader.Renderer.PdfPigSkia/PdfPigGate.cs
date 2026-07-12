namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Serialisation gate around PdfPig calls. PdfPig's <c>PdfDocument</c> is
/// not documented as thread-safe and the rendering extensions in
/// <c>PdfPig.Rendering.Skia</c> may hold global state, so every code path
/// in this backend that touches a <c>PdfDocument</c> instance must do so
/// inside <c>lock (PdfPigGate.Lock)</c>. That covers the renderer's shared
/// long-lived document (<c>PdfPigSkiaPdfService</c>) and — via the
/// <c>GatedPdfPig*Service</c> wrappers handed out by
/// <c>PdfPigSkiaPdfServiceFactory</c> — the per-call documents opened by
/// <c>RailReader.Core.PdfPig</c>'s text/link services, which cannot take
/// this (assembly-internal) gate themselves. Mirrors the discipline of
/// <c>PdfiumGate.Lock</c> in <c>RailReader.Core.Pdfium</c>.
/// </summary>
internal static class PdfPigGate
{
    public static readonly object Lock = new();
}
