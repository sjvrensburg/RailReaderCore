using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #228: <see cref="BlockCropRenderer"/> renders each crop as a page region instead of
/// cropping a whole-page raster, and takes a view rotation so a rotated document's block boxes
/// (which are in the rotated frame) crop the right part of the page.
/// </summary>
public class BlockCropRendererTests : IDisposable
{
    private const float PageW = 612, PageH = 792;
    private readonly string _pdfPath = Path.Combine(Path.GetTempPath(), $"block-crop-{Guid.NewGuid():N}.pdf");
    private readonly SkiaPdfService _pdf;

    public BlockCropRendererTests()
    {
        using (var stream = File.Create(_pdfPath))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            using var text = new SKPaint { Color = SKColors.DarkBlue, IsAntialias = true };
            using var red = new SKPaint { Color = SKColors.Red };
            using var font = new SKFont(SKTypeface.Default, 14);
            var canvas = doc.BeginPage(PageW, PageH);
            canvas.DrawLine(20, 20, 592, 772, stroke);
            for (int y = 160; y < 780; y += 37)
                canvas.DrawText($"Line at {y}: x^2 + y_i = z", 72, y, font, text);
            // A solid marker in the top-left corner, for telling rotated frames apart.
            canvas.DrawRect(50, 50, 60, 60, red);
            doc.EndPage();
            doc.Close();
        }
        _pdf = new SkiaPdfService(_pdfPath);
    }

    public void Dispose()
    {
        try { File.Delete(_pdfPath); } catch { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Region_crop_matches_the_old_whole_page_crop(int viewRotation)
    {
        // 288 DPI = scale 4, where the page's pixel size is exact: at 300 DPI PDFtoImage truncates
        // 3300 px to 3299, so the old raster sits at a slightly different scale and drifts a
        // fraction of a pixel down the page (the region is the one at the requested scale).
        const int Dpi = 288;
        var (pw, ph) = _pdf.GetPageSize(0, viewRotation);
        var block = new BBox(80.4f, 200.7f, 160f, 40.3f);

        using var crop = BlockCropRenderer.RenderBlockAsBitmap(_pdf, 0, block, pw, ph, Dpi, viewRotation: viewRotation);
        Assert.NotNull(crop);

        // What the renderer did before #228: rasterise the page, cut the padded box with (int) casts.
        using var page = (SkiaRenderedPage)_pdf.RenderPage(0, Dpi, viewRotation);
        var padded = BlockCropRenderer.PaddedRegion(block);
        float sx = page.Bitmap.Width / (float)pw, sy = page.Bitmap.Height / (float)ph;
        var rect = new SKRectI(
            Math.Max(0, (int)(padded.X * sx)), Math.Max(0, (int)(padded.Y * sy)),
            Math.Min(page.Bitmap.Width, (int)((padded.X + padded.W) * sx)),
            Math.Min(page.Bitmap.Height, (int)((padded.Y + padded.H) * sy)));
        using var old = new SKBitmap();
        Assert.True(page.Bitmap.ExtractSubset(old, rect));

        // Each edge may move by a pixel (the region snaps outward where the old crop truncated).
        Assert.InRange(crop!.Width - old.Width, -2, 2);
        Assert.InRange(crop.Height - old.Height, -2, 2);
        AssertSamePixels(crop, old);
    }

    [Fact]
    public void Rotated_view_crops_the_region_in_the_rotated_frame()
    {
        // A quarter-turn clockwise carries the top-left marker (x 50..110, y 50..110) to the
        // top-right of the 792 x 612 displayed page: x 682..742, y 50..110.
        var (pw, ph) = _pdf.GetPageSize(0, 1);
        Assert.Equal(PageH, pw, 0.5);
        var marker = new BBox(682, 50, 60, 60);

        using var crop = BlockCropRenderer.RenderBlockAsBitmap(_pdf, 0, marker, pw, ph, 150, viewRotation: 1);
        Assert.NotNull(crop);
        Assert.True(RedFraction(crop!) > 0.75, $"red fraction {RedFraction(crop!):0.00}");

        // Unrotated, the same box lies off the 612 pt wide page — the wrong region #228 reported.
        Assert.Null(BlockCropRenderer.RenderBlockAsPng(_pdf, 0, marker, PageW, PageH, 150));
    }

    [Fact]
    public void Many_large_blocks_render_the_page_once_with_the_same_crops()
    {
        // Two blocks covering most of the page take the whole-page path…
        var blocks = new List<BBox> { new(10, 10, 590, 380), new(10, 400, 590, 380) };
        var batch = BlockCropRenderer.RenderBlocksAsPng(_pdf, 0, blocks, PageW, PageH, 144);
        Assert.Equal(2, batch.Count);

        // …and come out like the region path renders them one at a time.
        for (int i = 0; i < blocks.Count; i++)
        {
            using var single = BlockCropRenderer.RenderBlockAsBitmap(_pdf, 0, blocks[i], PageW, PageH, 144);
            using var fromBatch = SKBitmap.Decode(batch[i]);
            Assert.InRange(fromBatch.Width - single!.Width, -1, 1);
            Assert.InRange(fromBatch.Height - single.Height, -1, 1);
            AssertSamePixels(fromBatch, single);
        }
    }

    [Fact]
    public void Small_blocks_keep_input_order_and_null_for_off_page_boxes()
    {
        var blocks = new List<BBox> { new(50, 50, 60, 60), new(900, 900, 10, 10), new(70, 140, 100, 30) };
        var pngs = BlockCropRenderer.RenderBlocksAsPng(_pdf, 0, blocks, PageW, PageH, 150,
            uprightTurns: [0, 0, 1]);

        Assert.Equal(3, pngs.Count);
        Assert.NotNull(pngs[0]);
        Assert.Null(pngs[1]);
        using var marker = SKBitmap.Decode(pngs[0]);
        Assert.True(RedFraction(marker) > 0.75);
        using var turned = SKBitmap.Decode(pngs[2]);
        Assert.True(turned.Height > turned.Width, "upright turn swaps a landscape crop's axes");
    }


    private static double RedFraction(SKBitmap bmp)
    {
        int red = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 60 && c.Blue < 60) red++;
            }
        return red / (double)(bmp.Width * bmp.Height);
    }

    /// <summary>
    /// The overlapping area matches, as in <see cref="RegionRenderTests"/>: each pixel is within a
    /// small tolerance of the same pixel, or of a neighbour (antialiased edges can land a fraction
    /// of a pixel apart between a whole-page and a region raster).
    /// </summary>
    private static void AssertSamePixels(SKBitmap a, SKBitmap b)
    {
        const int Tol = 32;
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        static bool Close(SKColor p, SKColor q) =>
            Math.Abs(p.Red - q.Red) <= Tol && Math.Abs(p.Green - q.Green) <= Tol && Math.Abs(p.Blue - q.Blue) <= Tol;

        int unmatched = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = a.GetPixel(x, y);
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1 && !near; dx++)
                    {
                        int qx = x + dx, qy = y + dy;
                        near = qx >= 0 && qy >= 0 && qx < b.Width && qy < b.Height && Close(p, b.GetPixel(qx, qy));
                    }
                if (!near) unmatched++;
            }
        Assert.True(unmatched == 0, $"{unmatched} of {w * h} pixels match nothing nearby");
    }
}
