using System.Runtime.InteropServices;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Renders a layout block region from a PDF page as PNG bytes (or a bitmap).
///
/// Each crop is rendered as a page region (<see cref="PdfRegionRendering.RenderRegionPixmap"/>,
/// issue #228) rather than cut from a whole-page raster, so an equation crop at 300 DPI costs a
/// few hundred pixels of rendering instead of a ~2550 × 3300 px page held under the PDFium gate.
/// Block boxes are in the displayed page's points under <c>viewRotation</c> — the frame layout
/// analysis of a rotated view produces — and the crop is taken from the page rendered the same way.
/// </summary>
public static class BlockCropRenderer
{
    /// <summary>Padding added on each side of a block, as a fraction of its width / height.</summary>
    internal const float PaddingFraction = 0.05f;

    /// <summary>
    /// Above this share of the page area (summed padded crops), <see cref="RenderBlocksAsPng"/>
    /// renders the page once and crops from it instead of rendering each block as a region.
    /// </summary>
    internal const double WholePageAreaFraction = 0.5;

    /// <summary>
    /// Renders the block's box (plus ~5% padding per side) at <paramref name="dpi"/> and returns it
    /// as PNG bytes, or null when the box misses the page or rendering fails.
    /// <paramref name="pageWidth"/>/<paramref name="pageHeight"/> are the size of the page the box is
    /// in (under <paramref name="viewRotation"/>). <paramref name="uprightTurns"/> turns a
    /// rotated-text crop upright (see <see cref="LayoutBlock.UprightTurns"/>).
    /// </summary>
    public static byte[]? RenderBlockAsPng(IPdfService pdf, int pageIndex,
        BBox blockBBox, double pageWidth, double pageHeight, int dpi = 300, int uprightTurns = 0,
        int viewRotation = 0)
    {
        using var bitmap = RenderBlockAsBitmap(pdf, pageIndex, blockBBox, pageWidth, pageHeight, dpi,
            uprightTurns, viewRotation);
        return bitmap is null ? null : EncodePng(bitmap);
    }

    /// <summary>
    /// As <see cref="RenderBlockAsPng"/>, but returns the crop as a bitmap (already turned upright)
    /// for hosts that only display it and would otherwise decode the PNG straight back. The caller
    /// owns the bitmap.
    /// </summary>
    public static SKBitmap? RenderBlockAsBitmap(IPdfService pdf, int pageIndex,
        BBox blockBBox, double pageWidth, double pageHeight, int dpi = 300, int uprightTurns = 0,
        int viewRotation = 0)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var region = PaddedRegion(blockBBox);
        if (ClampedArea(region, pageWidth, pageHeight) <= 0) return null;
        try
        {
            var pixmap = pdf.RenderRegionPixmap(pageIndex, region, dpi / 72.0, viewRotation);
            return ToUprightBitmap(pixmap, uprightTurns);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The region missed the backend's page (caller's page size disagreed with it).
            return null;
        }
    }

    /// <summary>
    /// PNG crops for every block of one page, in input order; an entry is null where the block's
    /// crop was empty or failed. <paramref name="uprightTurns"/> optionally gives each block's
    /// <see cref="LayoutBlock.UprightTurns"/> so rotated-text crops (sideways tables) are turned
    /// upright before the VLM sees them. Blocks are rendered as separate regions while they are a
    /// small part of the page — the usual case; when their padded areas add up to more than half the
    /// page, the page is rendered once and every crop is cut from it.
    /// </summary>
    public static List<byte[]?> RenderBlocksAsPng(IPdfService pdf, int pageIndex,
        IReadOnlyList<BBox> blockBBoxes, double pageWidth, double pageHeight, int dpi = 300,
        IReadOnlyList<int>? uprightTurns = null, int viewRotation = 0)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var results = new List<byte[]?>(blockBBoxes.Count);
        if (blockBBoxes.Count == 0) return results;

        var regions = blockBBoxes.Select(PaddedRegion).ToList();
        double covered = regions.Sum(r => ClampedArea(r, pageWidth, pageHeight));
        bool wholePage = covered > WholePageAreaFraction * pageWidth * pageHeight;
        double scale = dpi / 72.0;

        (byte[] RgbBytes, int Width, int Height)? page = null;
        if (wholePage)
            page = PdfRegionRendering.RenderPageForCropping(pdf, pageIndex, scale, viewRotation);

        for (int i = 0; i < regions.Count; i++)
        {
            int turns = uprightTurns is not null && i < uprightTurns.Count ? uprightTurns[i] : 0;
            if (ClampedArea(regions[i], pageWidth, pageHeight) <= 0)
            {
                results.Add(null);
                continue;
            }
            try
            {
                var pixmap = page is { } p
                    ? PdfRegionRendering.CropPagePixmap(p, regions[i], pageWidth, pageHeight)
                    : pdf.RenderRegionPixmap(pageIndex, regions[i], scale, viewRotation);
                using var bitmap = ToUprightBitmap(pixmap, turns);
                results.Add(EncodePng(bitmap));
            }
            catch (ArgumentOutOfRangeException)
            {
                results.Add(null);
            }
        }
        return results;
    }

    /// <summary>The block's box grown by <see cref="PaddingFraction"/> per side, for context.</summary>
    internal static BBox PaddedRegion(BBox b)
    {
        float padX = b.W * PaddingFraction, padY = b.H * PaddingFraction;
        return new BBox(b.X - padX, b.Y - padY, b.W + 2 * padX, b.H + 2 * padY);
    }

    private static double ClampedArea(BBox r, double pageW, double pageH)
    {
        double w = Math.Min(pageW, (double)r.X + r.W) - Math.Max(0, r.X);
        double h = Math.Min(pageH, (double)r.Y + r.H) - Math.Max(0, r.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static byte[]? EncodePng(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>Packed RGB as a bitmap, turned <paramref name="uprightTurns"/> quarter-turns clockwise.</summary>
    private static SKBitmap ToUprightBitmap((byte[] RgbBytes, int Width, int Height) pixmap, int uprightTurns)
    {
        var bitmap = FromPackedRgb(pixmap.RgbBytes, pixmap.Width, pixmap.Height);
        // Rotated-text block: turn the crop upright so the VLM (and any exported figure) sees
        // readable text instead of sideways glyphs.
        int q = ViewRotationMath.Normalize(uprightTurns);
        if (q == 0) return bitmap;
        using (bitmap)
            return RotateBitmap(bitmap, q);
    }

    private static SKBitmap FromPackedRgb(byte[] rgb, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        int rowBytes = bitmap.RowBytes;
        var row = new byte[rowBytes];
        IntPtr pixels = bitmap.GetPixels();
        for (int y = 0; y < height; y++)
        {
            int src = y * width * 3;
            for (int x = 0, dst = 0; x < width; x++, src += 3, dst += 4)
            {
                row[dst] = rgb[src];
                row[dst + 1] = rgb[src + 1];
                row[dst + 2] = rgb[src + 2];
                row[dst + 3] = 255;
            }
            Marshal.Copy(row, 0, pixels + y * rowBytes, rowBytes);
        }
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    private static SKBitmap RotateBitmap(SKBitmap source, int quarterTurnsCw)
    {
        var rotated = (quarterTurnsCw & 1) == 0
            ? new SKBitmap(source.Width, source.Height)
            : new SKBitmap(source.Height, source.Width);
        using var canvas = new SKCanvas(rotated);
        switch (quarterTurnsCw)
        {
            case 1: canvas.Translate(rotated.Width, 0); break;
            case 2: canvas.Translate(rotated.Width, rotated.Height); break;
            case 3: canvas.Translate(0, rotated.Height); break;
        }
        canvas.RotateDegrees(quarterTurnsCw * 90);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return rotated;
    }
}
