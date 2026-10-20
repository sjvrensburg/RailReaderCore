using System.Runtime.InteropServices;
using PDFtoImage;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Renderer.Skia;

/// <summary>
/// PDFium/SkiaSharp implementation of IPdfService.
/// </summary>
public sealed class SkiaPdfService : IPdfService, IPdfTiledRenderService
{

    public byte[] PdfBytes { get; }

    /// <summary>The password the document was opened with, or null when unencrypted.</summary>
    public string? Password { get; }

    public int PageCount { get; }
    public List<OutlineEntry> Outline { get; }

    /// <summary>The source file path, kept only for diagnostic messages (e.g. a load failure in
    /// <see cref="GetPageSizes"/>) — everything else already operates on <see cref="PdfBytes"/>.</summary>
    private readonly string _filePath;

    /// <summary>Unrotated (page /Rotate applied, view rotation not) page sizes, populated once on
    /// first access and reused for every subsequent <see cref="GetPageSize(int, int)"/> or
    /// <see cref="GetPageSizes"/> call — sizes are immutable for an open document, so there is no
    /// reason to re-open it (and re-parse the whole file) on every call (issue #114).
    /// Built under <see cref="PdfiumGate.Lock"/> itself (rather than a dedicated lock) so callers
    /// that already hold the gate — <see cref="RenderThumbnail(int, int)"/>,
    /// <see cref="RenderPagePixmap(int, int, int)"/> — can re-enter it without a second lock whose
    /// acquisition order could invert against theirs; <c>volatile</c> guards the unlocked
    /// fast-path read below against a torn/reordered view of a just-published array.</summary>
    private volatile (double Width, double Height)[]? _pageSizesCache;

    public SkiaPdfService(string filePath, string? password = null)
    {
        PdfBytes = File.ReadAllBytes(filePath);
        Password = password;
        _filePath = filePath;
        (PageCount, Outline) = OpenAndRead(PdfBytes, password, filePath);
        if (Outline.Count > 0)
            RailReaderLogging.Logger.Debug($"[PDF] Extracted {Outline.Count} outline entries");
    }

    /// <summary>
    /// Opens the document once under the PDFium gate to validate the password and read both
    /// the page count and the outline in a single load. PDFtoImage swallows load failures
    /// (it would just report 0 pages), so we probe directly to translate an
    /// encrypted-without-password / wrong-password open into a
    /// <see cref="PdfPasswordRequiredException"/>; any other load failure (corrupt file)
    /// becomes an <see cref="InvalidOperationException"/>.
    /// </summary>
    private static (int PageCount, List<OutlineEntry> Outline) OpenAndRead(
        byte[] pdfBytes, string? password, string filePath)
    {
        lock (PdfiumGate.Lock)
        {
            PdfiumResolver.EnsureLibraryInitialized();
            var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero;
            try
            {
                doc = LoadDocumentChecked(pinned.AddrOfPinnedObject(), pdfBytes.Length, password, filePath);
                if (doc == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to open PDF '{filePath}' (not a valid PDF document).");
                return (FPDF_GetPageCount(doc), PdfOutlineService.ExtractFromDocument(doc));
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                pinned.Free();
            }
        }
    }

    public (double Width, double Height) GetPageSize(int pageIndex) => GetPageSize(pageIndex, 0);

    public (double Width, double Height) GetPageSize(int pageIndex, int viewRotation)
    {
        var size = EnsurePageSizesCache()[pageIndex];
        // The cache already honours the page /Rotate; an extra view rotation just
        // swaps the displayed axes on odd quarter-turns.
        return (viewRotation & 1) == 0 ? size : (size.Height, size.Width);
    }

    /// <summary>
    /// Reads every page's displayed size, from the cache built on first access — a single
    /// document open, all pages, rather than the per-page re-parse the old
    /// <see cref="GetPageSize(int, int)"/> (via PDFtoImage) did. Used by
    /// <see cref="DocumentModel.EnsurePageLayout"/> when building continuous-scroll layout.
    /// </summary>
    public IReadOnlyList<(double Width, double Height)> GetPageSizes(int viewRotation)
    {
        var cache = EnsurePageSizesCache();
        var sizes = new List<(double, double)>(cache.Length);
        foreach (var size in cache)
            sizes.Add((viewRotation & 1) == 0 ? size : (size.Height, size.Width));
        return sizes;
    }

    /// <summary>
    /// Populates <see cref="_pageSizesCache"/> from a single document open on first access.
    /// Page sizes are immutable for an already-open document, so every later caller reads the
    /// same array instead of re-opening (and re-parsing the whole file) each time.
    /// </summary>
    private (double Width, double Height)[] EnsurePageSizesCache()
    {
        if (_pageSizesCache is { } cached)
            return cached;

        lock (PdfiumGate.Lock)
        {
            if (_pageSizesCache is { } cachedInner)
                return cachedInner;

            var sizes = new (double Width, double Height)[PageCount];
            PdfiumResolver.EnsureLibraryInitialized();
            var pinned = GCHandle.Alloc(PdfBytes, GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero;
            try
            {
                doc = LoadDocumentChecked(pinned.AddrOfPinnedObject(), PdfBytes.Length, Password, _filePath);
                for (int i = 0; i < PageCount; i++)
                {
                    IntPtr page = FPDF_LoadPage(doc, i);
                    if (page == IntPtr.Zero) { sizes[i] = (0, 0); continue; }
                    try
                    {
                        sizes[i] = (FPDF_GetPageWidth(page), FPDF_GetPageHeight(page));
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }
                }
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                pinned.Free();
            }

            _pageSizesCache = sizes;
            return sizes;
        }
    }

    public IRenderedPage RenderPage(int pageIndex, int dpi = 200) => RenderPage(pageIndex, dpi, 0);

    public IRenderedPage RenderPage(int pageIndex, int dpi, int viewRotation)
    {
        lock (PdfiumGate.Lock)
        {
            var bitmap = Conversion.ToImage(PdfBytes, password: Password, page: pageIndex,
                options: new RenderOptions(Dpi: dpi, Rotation: ToPdfRotation(viewRotation)));
            return new SkiaRenderedPage(bitmap);
        }
    }

    /// <summary>Pixels per band of <see cref="RenderPageTiled"/> (~2 MP, so a band of a 5× US Letter
    /// page holds the gate for a tenth of the page's full render), with a floor on the band height so
    /// a very wide page still renders in a sensible number of bands.</summary>
    private const int TilePixelBudget = 2_000_000;
    private const int MinTileRows = 64;

    /// <summary>
    /// Renders the page band by band straight into one bitmap, taking <see cref="PdfiumGate.Lock"/>
    /// per band rather than for the whole page (issue #147). The document and page are loaded once and
    /// kept open across bands; each band is a PDFium bitmap wrapping that band's rows of the full
    /// bitmap, rendered with the whole page's transform shifted up by the band's offset, so bands join
    /// without a seam. Size, rotation and flags mirror PDFtoImage's own path in
    /// <see cref="RenderPage(int, int, int)"/> (float page size × dpi/72, truncated; no annotations).
    /// </summary>
    public IRenderedPage RenderPageTiled(int pageIndex, int dpi, int viewRotation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (pw, ph) = GetPageSize(pageIndex, viewRotation);
        float sx = (float)pw * (dpi / 72f), sy = (float)ph * (dpi / 72f);
        int width = (int)sx, height = (int)sy;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Page {pageIndex + 1} has no renderable area.");
        int sizeX = (int)Math.Ceiling(sx), sizeY = (int)Math.Ceiling(sy);
        int rotate = (int)ToPdfRotation(viewRotation);
        int bandRows = Math.Max(MinTileRows, TilePixelBudget / width);

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pinned = GCHandle.Alloc(PdfBytes, GCHandleType.Pinned);
        IntPtr doc = IntPtr.Zero, page = IntPtr.Zero;
        try
        {
            bitmap.Erase(SKColors.White);
            IntPtr pixels = bitmap.GetPixels();
            int stride = bitmap.RowBytes;

            lock (PdfiumGate.Lock)
            {
                PdfiumResolver.EnsureLibraryInitialized();
                doc = LoadDocumentChecked(pinned.AddrOfPinnedObject(), PdfBytes.Length, Password, _filePath);
                if (doc == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to open PDF '{_filePath}' for rendering.");
                page = FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to load page {pageIndex + 1} for rendering.");
            }

            for (int y = 0; y < height; y += bandRows)
            {
                ct.ThrowIfCancellationRequested();
                int rows = Math.Min(bandRows, height - y);
                lock (PdfiumGate.Lock)
                {
                    IntPtr band = FPDFBitmap_CreateEx(width, rows, FPDFBitmap_BGRA, pixels + (nint)y * stride, stride);
                    if (band == IntPtr.Zero)
                        throw new InvalidOperationException($"Failed to allocate a render band for page {pageIndex + 1}.");
                    try { FPDF_RenderPageBitmap(band, page, 0, -y, sizeX, sizeY, rotate, 0); }
                    finally { FPDFBitmap_Destroy(band); }
                }
            }
            return new SkiaRenderedPage(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (doc != IntPtr.Zero)
            {
                lock (PdfiumGate.Lock)
                {
                    if (page != IntPtr.Zero) FPDF_ClosePage(page);
                    FPDF_CloseDocument(doc);
                }
            }
            pinned.Free();
        }
    }

    public IRenderedPage RenderThumbnail(int pageIndex) => RenderThumbnail(pageIndex, 0);

    public IRenderedPage RenderThumbnail(int pageIndex, int viewRotation)
    {
        lock (PdfiumGate.Lock)
        {
            // PDFtoImage applies explicit Width/Height to the PRE-rotation raster and
            // rotates the output afterwards (verified via tools/rotation-probe), so fit
            // the unrotated page; odd turns swap the output dimensions on their own.
            var (pixW, pixH) = FitPageToTarget(pageIndex, 200);
            var bitmap = Conversion.ToImage(PdfBytes, password: Password, page: pageIndex,
                options: new RenderOptions(Width: pixW, Height: pixH, Rotation: ToPdfRotation(viewRotation)));
            return new SkiaRenderedPage(bitmap);
        }
    }

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
        => RenderPagePixmap(pageIndex, targetSize, 0);

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize, int viewRotation)
    {
        SKBitmap bitmap;
        lock (PdfiumGate.Lock)
        {
            // Fit the PRE-rotation page: PDFtoImage rasterises at Width/Height and then
            // rotates the output, swapping the dimensions on odd turns (see RenderThumbnail).
            var (pixW, pixH) = FitPageToTarget(pageIndex, targetSize);
            bitmap = Conversion.ToImage(PdfBytes, password: Password, page: pageIndex,
                options: new RenderOptions(Width: pixW, Height: pixH, Rotation: ToPdfRotation(viewRotation)));
        }

        // BGRA->RGB conversion is pure CPU; run outside the PDFium gate so
        // other tabs' render/text-extract calls aren't blocked by this loop.
        try
        {
            var pixels = bitmap.GetPixelSpan();
            int pixelCount = bitmap.Width * bitmap.Height;
            var rgb = new byte[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                int src = i * 4;
                int dst = i * 3;
                rgb[dst] = pixels[src + 2];
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

    private (int Width, int Height) FitPageToTarget(int pageIndex, int targetSize)
    {
        var (pageW, pageH) = GetPageSize(pageIndex);
        double scale = Math.Min(targetSize / pageW, targetSize / pageH);
        return (Math.Max(1, (int)(pageW * scale)), Math.Max(1, (int)(pageH * scale)));
    }

    private static PdfRotation ToPdfRotation(int viewRotation) => (((viewRotation % 4) + 4) % 4) switch
    {
        1 => PdfRotation.Rotate90,
        2 => PdfRotation.Rotate180,
        3 => PdfRotation.Rotate270,
        _ => PdfRotation.Rotate0,
    };

}
