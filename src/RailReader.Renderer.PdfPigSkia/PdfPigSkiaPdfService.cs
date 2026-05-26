using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.PdfPig;
using RailReader.Core.Services;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;

namespace RailReader.Renderer.PdfPigSkia;

/// <summary>
/// Pure-managed implementation of <see cref="IPdfService"/> targeting
/// Lite (Avalonia.Browser), web, and mobile consumers that cannot ship
/// PDFium. Mirrors <c>RailReader.Renderer.Skia.SkiaPdfService</c>'s shape
/// so the same callers swap factories without touching call sites.
///
/// <para>
/// Holds a long-lived <c>PdfDocument</c> instance (opened once in the
/// constructor) so per-call render/text/outline operations don't pay
/// the parsing cost on every page navigation. Implements
/// <see cref="IDisposable"/> so the consumer can release that document
/// when the current PDF changes; if the consumer drops the reference
/// without disposing, the document is reclaimed by GC eventually.
/// </para>
/// <para>
/// All PdfPig touches go through <see cref="PdfPigGate.Lock"/> because
/// <c>PdfDocument</c> is not documented thread-safe and
/// <c>PdfPig.Rendering.Skia</c> may hold global state.
/// </para>
/// </summary>
public sealed class PdfPigSkiaPdfService : IPdfService, IDisposable
{
    private const float PointsPerInch = 72f;

    private readonly PdfDocument _doc;
    private bool _disposed;

    public byte[] PdfBytes { get; }
    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    /// <summary>
    /// Convenience overload that reads the file once and forwards to
    /// <see cref="PdfPigSkiaPdfService(byte[])"/>. Mirrors the desktop
    /// <c>SkiaPdfService(string)</c> shape.
    /// </summary>
    public PdfPigSkiaPdfService(string filePath)
        : this(File.ReadAllBytes(filePath)) { }

    /// <summary>
    /// Constructs the service from an in-memory PDF. Useful for web
    /// consumers that receive bytes from a file picker / network /
    /// embedded resource — avoids the temp-file hop the file-path
    /// constructor would otherwise need.
    /// </summary>
    public PdfPigSkiaPdfService(byte[] pdfBytes)
    {
        PdfBytes = pdfBytes;
        lock (PdfPigGate.Lock)
        {
            _doc = PdfDocument.Open(PdfBytes);
            // Registers the Skia page factories so subsequent calls to
            // GetPageAsSKBitmap on this document resolve. Idempotent.
            _doc.AddSkiaPageFactory();

            PageCount = _doc.NumberOfPages;
            // Reuse the cached document for outline extraction so we
            // don't re-parse the file just to read bookmarks.
            Outline = new PdfOutlineService().Extract(_doc);
        }
        if (Outline.Count > 0)
            RailReaderLogging.Logger.Debug($"[PdfPigSkia] Extracted {Outline.Count} outline entries");
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (PdfPigGate.Lock)
        {
            // PdfPig is 1-indexed; Core's IPdfService is 0-indexed.
            var page = _doc.GetPage(pageIndex + 1);
            return (page.Width, page.Height);
        }
    }

    public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
    {
        // SkiaSharp.GetPageAsSKBitmap renders at PDF native (72 DPI) ×
        // scale; convert the caller's DPI request into a scale factor.
        float scale = dpi / PointsPerInch;
        return new PdfPigSkiaRenderedPage(RenderAt(pageIndex, scale));
    }

    public IRenderedPage RenderThumbnail(int pageIndex)
    {
        var bmp = RenderAtPixelSize(pageIndex, 200, 200);
        return new PdfPigSkiaRenderedPage(bmp);
    }

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
    {
        var bitmap = RenderAtPixelSize(pageIndex, targetSize, targetSize);

        // BGRA→RGB conversion is pure CPU; run outside the gate so other
        // renders/text-extracts aren't blocked. Mirrors SkiaPdfService.
        try
        {
            var pixels = bitmap.GetPixelSpan();
            int pixelCount = bitmap.Width * bitmap.Height;
            var rgb = new byte[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                int src = i * 4;
                int dst = i * 3;
                rgb[dst]     = pixels[src + 2];
                rgb[dst + 1] = pixels[src + 1];
                rgb[dst + 2] = pixels[src];
            }
            return (rgb, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private SKBitmap RenderAt(int pageIndex, float scale)
    {
        lock (PdfPigGate.Lock)
        {
            return _doc.GetPageAsSKBitmap(pageIndex + 1, scale, SKColors.White);
        }
    }

    /// <summary>
    /// Renders to an exact pixel size. <c>GetPageAsSKBitmap</c> only
    /// accepts a uniform scale, so we compute the scale that fits the
    /// longest edge to the target. The resulting bitmap may be slightly
    /// smaller than the requested (W,H) on the shorter edge — same as
    /// <c>SkiaPdfService</c>'s behaviour.
    /// </summary>
    private SKBitmap RenderAtPixelSize(int pageIndex, int pixW, int pixH)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(pixW / pageW, pixH / pageH);
        return RenderAt(pageIndex, (float)scale);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (PdfPigGate.Lock)
        {
            _doc.Dispose();
        }
    }
}
