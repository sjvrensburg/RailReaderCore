using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Renders a layout block region from a PDF page as PNG bytes.
/// </summary>
public static class BlockCropRenderer
{
    /// <summary>
    /// Renders the page at the given DPI, crops to the block's BBox with padding,
    /// and returns the result as PNG bytes. Single-block convenience; for multiple
    /// blocks on the same page use <see cref="RenderBlocksAsPng"/> to avoid
    /// re-rasterising the page.
    /// </summary>
    public static byte[]? RenderBlockAsPng(IPdfService pdf, int pageIndex,
        BBox blockBBox, double pageWidth, double pageHeight, int dpi = 300, int uprightTurns = 0)
    {
        using var rendered = pdf.RenderPage(pageIndex, dpi);
        if (rendered is not SkiaRenderedPage skiaPage) return null;
        return CropFromBitmap(skiaPage.Bitmap, blockBBox, pageWidth, pageHeight, uprightTurns);
    }

    /// <summary>
    /// Renders the page once and extracts PNG crops for every block. Order of
    /// the returned list matches the input order. Entries are null for blocks
    /// whose crop was empty or failed. <paramref name="uprightTurns"/> optionally
    /// gives each block's <see cref="LayoutBlock.UprightTurns"/> so rotated-text
    /// crops (sideways tables) are turned upright before the VLM sees them.
    /// </summary>
    public static List<byte[]?> RenderBlocksAsPng(IPdfService pdf, int pageIndex,
        IReadOnlyList<BBox> blockBBoxes, double pageWidth, double pageHeight, int dpi = 300,
        IReadOnlyList<int>? uprightTurns = null)
    {
        var results = new List<byte[]?>(blockBBoxes.Count);
        using var rendered = pdf.RenderPage(pageIndex, dpi);
        if (rendered is not SkiaRenderedPage skiaPage)
        {
            for (int i = 0; i < blockBBoxes.Count; i++) results.Add(null);
            return results;
        }
        for (int i = 0; i < blockBBoxes.Count; i++)
        {
            int turns = uprightTurns is not null && i < uprightTurns.Count ? uprightTurns[i] : 0;
            results.Add(CropFromBitmap(skiaPage.Bitmap, blockBBoxes[i], pageWidth, pageHeight, turns));
        }
        return results;
    }

    private static byte[]? CropFromBitmap(SKBitmap bitmap, BBox blockBBox,
        double pageWidth, double pageHeight, int uprightTurns = 0)
    {
        float scaleX = bitmap.Width / (float)pageWidth;
        float scaleY = bitmap.Height / (float)pageHeight;

        // Add ~5% padding around the block for context
        float padX = blockBBox.W * 0.05f;
        float padY = blockBBox.H * 0.05f;

        var cropRect = new SKRectI(
            Math.Max(0, (int)((blockBBox.X - padX) * scaleX)),
            Math.Max(0, (int)((blockBBox.Y - padY) * scaleY)),
            Math.Min(bitmap.Width, (int)((blockBBox.X + blockBBox.W + padX) * scaleX)),
            Math.Min(bitmap.Height, (int)((blockBBox.Y + blockBBox.H + padY) * scaleY)));

        if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

        using var cropped = new SKBitmap();
        if (!bitmap.ExtractSubset(cropped, cropRect)) return null;

        // Rotated-text block: turn the crop upright before encoding so the VLM
        // (and any exported figure) sees readable text instead of sideways glyphs.
        int q = ViewRotationMath.Normalize(uprightTurns);
        if (q != 0)
        {
            using var upright = RotateBitmap(cropped, q);
            using var rotatedData = upright.Encode(SKEncodedImageFormat.Png, 100);
            return rotatedData?.ToArray();
        }

        using var data = cropped.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
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
