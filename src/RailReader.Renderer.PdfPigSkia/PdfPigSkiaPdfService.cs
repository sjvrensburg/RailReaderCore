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

    /// <summary>
    /// Pages to keep as recorded display lists. A page's <see cref="SKPicture"/> is
    /// resolution-independent, so one recording serves every zoom level, the analysis pixmap
    /// and the thumbnail — but it retains that page's decoded images, so the cache is bounded.
    /// Sized for the page in view plus its neighbours under prefetch, in each of a couple of
    /// split panes.
    /// </summary>
    private const int PictureCacheSize = 8;

    // Guarded by PdfPigGate.Lock, like every other _doc touch. _pictureOrder is least-recently-
    // used first.
    private readonly Dictionary<int, SKPicture> _pictures = [];
    private readonly List<int> _pictureOrder = [];

    public byte[] PdfBytes { get; }

    /// <summary>The password the document was opened with, or null when unencrypted.</summary>
    public string? Password { get; }

    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    /// <summary>
    /// Convenience overload that reads the file once and forwards to
    /// <see cref="PdfPigSkiaPdfService(byte[], string?)"/>. Mirrors the desktop
    /// <c>SkiaPdfService(string, string?)</c> shape.
    /// </summary>
    public PdfPigSkiaPdfService(string filePath, string? password = null)
        : this(File.ReadAllBytes(filePath), password) { }

    /// <summary>
    /// Constructs the service from an in-memory PDF. Useful for web
    /// consumers that receive bytes from a file picker / network /
    /// embedded resource — avoids the temp-file hop the file-path
    /// constructor would otherwise need. For an encrypted document pass the
    /// <paramref name="password"/>; an encrypted-without-password / wrong-password
    /// open surfaces as <see cref="PdfPasswordRequiredException"/>.
    /// </summary>
    public PdfPigSkiaPdfService(byte[] pdfBytes, string? password = null)
    {
        PdfBytes = pdfBytes;
        Password = password;
        lock (PdfPigGate.Lock)
        {
            try
            {
                _doc = PdfDocument.Open(PdfBytes, PdfPigOpen.Options(password));
            }
            catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
            {
                throw new PdfPasswordRequiredException(!string.IsNullOrEmpty(password), null);
            }
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

    public (double Width, double Height) GetPageSize(int pageIndex) => GetPageSize(pageIndex, 0);

    public (double Width, double Height) GetPageSize(int pageIndex, int viewRotation)
    {
        lock (PdfPigGate.Lock)
        {
            // PdfPig is 1-indexed; Core's IPdfService is 0-indexed. page.Width/Height
            // already honour the page /Rotate; the view rotation swaps axes on odd turns.
            var page = _doc.GetPage(pageIndex + 1);
            return ViewRotationMath.RotateSize(page.Width, page.Height, viewRotation);
        }
    }

    public IRenderedPage RenderPage(int pageIndex, int dpi = 200) => RenderPage(pageIndex, dpi, 0);

    public IRenderedPage RenderPage(int pageIndex, int dpi, int viewRotation)
    {
        // SkiaSharp.GetPageAsSKBitmap renders at PDF native (72 DPI) ×
        // scale; convert the caller's DPI request into a scale factor.
        float scale = dpi / PointsPerInch;
        return new PdfPigSkiaRenderedPage(RotateBitmap(RenderAt(pageIndex, scale), viewRotation));
    }

    public IRenderedPage RenderThumbnail(int pageIndex) => RenderThumbnail(pageIndex, 0);

    public IRenderedPage RenderThumbnail(int pageIndex, int viewRotation)
    {
        var bmp = RotateBitmap(RenderAtPixelSize(pageIndex, 200, 200), viewRotation);
        return new PdfPigSkiaRenderedPage(bmp);
    }

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
        => RenderPagePixmap(pageIndex, targetSize, 0);

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize, int viewRotation)
    {
        var bitmap = RotateBitmap(RenderAtPixelSize(pageIndex, targetSize, targetSize), viewRotation);

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

    /// <summary>
    /// Rotates a rendered bitmap by the view rotation (clockwise quarter-turns).
    /// PdfPig's Skia page factory has no rotation input, so the rotation is
    /// applied to the rasterised output; the source bitmap is disposed.
    /// </summary>
    private static SKBitmap RotateBitmap(SKBitmap source, int viewRotation)
    {
        int q = ViewRotationMath.Normalize(viewRotation);
        if (q == 0) return source;

        try
        {
            var rotated = (q & 1) == 0
                ? new SKBitmap(source.Width, source.Height)
                : new SKBitmap(source.Height, source.Width);
            using var canvas = new SKCanvas(rotated);
            switch (q)
            {
                case 1: canvas.Translate(rotated.Width, 0); break;
                case 2: canvas.Translate(rotated.Width, rotated.Height); break;
                case 3: canvas.Translate(0, rotated.Height); break;
            }
            canvas.RotateDegrees(q * 90);
            canvas.DrawBitmap(source, 0, 0);
            canvas.Flush();
            return rotated;
        }
        finally
        {
            source.Dispose();
        }
    }

    private SKBitmap RenderAt(int pageIndex, float scale)
    {
        lock (PdfPigGate.Lock)
        {
            var page = _doc.GetPage(pageIndex + 1);
            return DrawPicture(pageIndex, page.Width, page.Height, scale);
        }
    }

    /// <summary>
    /// Renders to an exact pixel size. Rendering is uniformly scaled, so we compute the scale
    /// that fits the longest edge to the target. The resulting bitmap may be slightly smaller
    /// than the requested (W,H) on the shorter edge — same as <c>SkiaPdfService</c>'s behaviour.
    /// </summary>
    private SKBitmap RenderAtPixelSize(int pageIndex, int pixW, int pixH)
    {
        // Take the gate once: GetPageSize + RenderAt would otherwise acquire it
        // twice and resolve the page twice for a single render.
        lock (PdfPigGate.Lock)
        {
            var page = _doc.GetPage(pageIndex + 1);
            double scale = Math.Min(pixW / page.Width, pixH / page.Height);
            return DrawPicture(pageIndex, page.Width, page.Height, (float)scale);
        }
    }

    /// <summary>
    /// Rasterises a page from its cached display list at an arbitrary scale.
    ///
    /// <para>
    /// This is the point of recording pictures: parsing and interpreting a page's content
    /// stream is the expensive half of rendering, and it does not depend on scale. A rail
    /// reader re-renders the same page constantly — zoom steps, the render-DPI state machine,
    /// thumbnails, the analysis pixmap — and each of those used to re-parse the page from
    /// scratch. Replaying a recorded picture skips straight to rasterisation.
    /// </para>
    /// <para>Caller must hold <see cref="PdfPigGate.Lock"/>.</para>
    /// </summary>
    private SKBitmap DrawPicture(int pageIndex, double pageWidth, double pageHeight, float scale)
    {
        var picture = GetOrRecordPicture(pageIndex);

        // Match GetPageAsSKBitmap's sizing exactly — it rounds the scaled page box *up*, so
        // truncating here would silently shave a pixel off every render relative to the path
        // this replaces.
        int width = Math.Max(1, (int)Math.Ceiling(pageWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(pageHeight * scale));

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Flush();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the page's recorded display list, recording it on first use. Caller must hold
    /// <see cref="PdfPigGate.Lock"/>; the cache and its LRU order are guarded by it.
    /// </summary>
    private SKPicture GetOrRecordPicture(int pageIndex)
    {
        if (_pictures.TryGetValue(pageIndex, out var cached))
        {
            _pictureOrder.Remove(pageIndex);
            _pictureOrder.Add(pageIndex);
            return cached;
        }

        var picture = _doc.GetPageAsSKPicture(pageIndex + 1);

        while (_pictureOrder.Count >= PictureCacheSize)
        {
            int evict = _pictureOrder[0];
            _pictureOrder.RemoveAt(0);
            if (_pictures.Remove(evict, out var stale)) stale.Dispose();
        }

        _pictures[pageIndex] = picture;
        _pictureOrder.Add(pageIndex);
        return picture;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (PdfPigGate.Lock)
        {
            foreach (var picture in _pictures.Values) picture.Dispose();
            _pictures.Clear();
            _pictureOrder.Clear();
            _doc.Dispose();
        }
    }
}
