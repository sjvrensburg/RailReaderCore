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

    public SkiaPdfService(string filePath, string? password = null)
    {
        PdfBytes = File.ReadAllBytes(filePath);
        Password = password;
        lock (PdfiumGate.Lock)
        {
            // Open once up front to validate the password and read the page count.
            // PDFtoImage swallows load failures (it would just report 0 pages), so we
            // probe directly to translate an encrypted-without-password / wrong-password
            // open into a PdfPasswordRequiredException the caller can act on.
            PageCount = ProbePageCount(PdfBytes, password, filePath);
            Outline = new PdfOutlineService().Extract(PdfBytes, password);
        }
        if (Outline.Count > 0)
            RailReaderLogging.Logger.Debug($"[PDF] Extracted {Outline.Count} outline entries");
    }

    /// <summary>
    /// Loads the document under the PDFium gate to validate the password and return its
    /// page count. Throws <see cref="PdfPasswordRequiredException"/> for an encrypted
    /// document opened with a missing/incorrect password, or
    /// <see cref="InvalidOperationException"/> for any other load failure (corrupt file).
    /// Caller holds <see cref="PdfiumGate.Lock"/>.
    /// </summary>
    private static int ProbePageCount(byte[] pdfBytes, string? password, string filePath)
    {
        PdfiumResolver.EnsureLibraryInitialized();
        var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        IntPtr doc = IntPtr.Zero;
        try
        {
            doc = LoadDocumentChecked(pinned.AddrOfPinnedObject(), pdfBytes.Length, password, filePath);
            if (doc == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to open PDF '{filePath}' (not a valid PDF document).");
            return FPDF_GetPageCount(doc);
        }
        finally
        {
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            pinned.Free();
        }
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (PdfiumGate.Lock)
        {
            var size = Conversion.GetPageSize(PdfBytes, page: pageIndex, password: Password);
            return (size.Width, size.Height);
        }
    }

    public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
    {
        lock (PdfiumGate.Lock)
        {
            var bitmap = Conversion.ToImage(PdfBytes, password: Password, page: pageIndex,
                options: new RenderOptions(Dpi: dpi));
            return new SkiaRenderedPage(bitmap);
        }
    }

    public IRenderedPage RenderThumbnail(int pageIndex)
    {
        lock (PdfiumGate.Lock)
        {
            var (pixW, pixH) = FitPageToTarget(pageIndex, 200);
            var bitmap = Conversion.ToImage(PdfBytes, password: Password, page: pageIndex,
                options: new RenderOptions(Width: pixW, Height: pixH));
            return new SkiaRenderedPage(bitmap);
        }
    }

    public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
    {
        SKBitmap bitmap;
        lock (PdfiumGate.Lock)
        {
            var (pixW, pixH) = FitPageToTarget(pageIndex, targetSize);
            bitmap = Conversion.ToImage(PdfBytes, password: Password, page: pageIndex,
                options: new RenderOptions(Width: pixW, Height: pixH));
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

}
