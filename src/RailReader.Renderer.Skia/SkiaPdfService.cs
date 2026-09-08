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
public sealed class SkiaPdfService : IPdfService
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
    /// reason to re-open it (and re-parse the whole file) on every call (issue #114).</summary>
    private (double Width, double Height)[]? _pageSizesCache;
    private readonly object _pageSizesCacheLock = new();

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

        lock (_pageSizesCacheLock)
        {
            if (_pageSizesCache is { } cachedInner)
                return cachedInner;

            var sizes = new (double Width, double Height)[PageCount];
            lock (PdfiumGate.Lock)
            {
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
