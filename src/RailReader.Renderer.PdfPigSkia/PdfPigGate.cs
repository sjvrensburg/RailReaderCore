namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Serialisation gate around PdfPig calls. PdfPig's <c>PdfDocument</c> is
/// not documented as thread-safe and the rendering extensions in
/// <c>PdfPig.Rendering.Skia</c> may hold global state, so every code
/// path that touches a <c>PdfDocument</c> instance must do so inside
/// <c>lock (PdfPigGate.Lock)</c>. Mirrors the discipline of
/// <c>PdfiumGate.Lock</c> in <c>RailReader.Core.Pdfium</c>.
/// </summary>
internal static class PdfPigGate
{
    public static readonly object Lock = new();
}
