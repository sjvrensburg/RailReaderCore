using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #222: <see cref="SkiaPdfService.RenderRegionPixmap"/> rasterises only a page rectangle, and
/// must produce the pixels a whole-page render at the same scale holds there.
/// <see cref="PdfRegionRendering.RenderRegionPixmap"/> falls back to render-and-crop for a backend
/// without the capability.
/// </summary>
public class RegionRenderTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(Path.GetTempPath(), $"region-render-{Guid.NewGuid():N}.pdf");
    private readonly SkiaPdfService _pdf;

    public RegionRenderTests()
    {
        // Content across the whole page, so a misplaced region shows.
        using (var stream = File.Create(_pdfPath))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = SKColors.DarkBlue, IsAntialias = true };
            using var font = new SKFont(SKTypeface.Default, 14);
            var canvas = doc.BeginPage(612, 792);
            canvas.DrawLine(20, 20, 592, 772, stroke);
            canvas.DrawCircle(400, 300, 120, fill);
            for (int y = 60; y < 780; y += 37)
                canvas.DrawText($"Line at {y}: x^2 + y_i = z", 72, y, font, fill);
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
    public void Region_MatchesCropOfWholePageRender(int viewRotation)
    {
        // Scale 4 = 288 DPI. A region with fractional edges, inside the page under every rotation.
        const double Scale = 4;
        var region = new BBox(100.3f, 150.7f, 180f, 95.2f);
        var (rgb, w, h) = _pdf.RenderRegionPixmap(0, region, Scale, viewRotation);

        var (x0, y0, x1, y1) = PdfRegionRendering.PixelBounds(region, 612, 792, Scale);
        Assert.Equal(x1 - x0, w);
        Assert.Equal(y1 - y0, h);
        Assert.Equal(w * h * 3, rgb.Length);

        using var whole = (SkiaRenderedPage)_pdf.RenderPageTiled(0, 288, viewRotation, CancellationToken.None);
        AssertMatchesCrop(rgb, w, h, whole.Bitmap, x0, y0);
    }

    [Fact]
    public void Region_SnapsOutwardToWholePixels()
    {
        // At scale 2, x 10.25..30.25 covers raster columns 20.5..60.5 -> [20, 61).
        var (_, w, h) = _pdf.RenderRegionPixmap(0, new BBox(10.25f, 40f, 20f, 10f), 2);
        Assert.Equal(41, w);
        Assert.Equal(20, h);
    }

    [Fact]
    public void Region_IsClampedToThePage()
    {
        // Half of the region lies left of and above the page.
        var (_, w, h) = _pdf.RenderRegionPixmap(0, new BBox(-50f, -20f, 100f, 40f), 2);
        Assert.Equal(100, w);
        Assert.Equal(40, h);

        // Odd view rotations swap the displayed axes: the page is 792 pt wide.
        var (_, wr, _) = _pdf.RenderRegionPixmap(0, new BBox(700f, 0f, 200f, 10f), 1, viewRotation: 1);
        Assert.Equal(92, wr);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScale_Throws(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _pdf.RenderRegionPixmap(0, new BBox(0, 0, 10, 10), scale));
    }

    [Fact]
    public void RegionOutsideThePage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _pdf.RenderRegionPixmap(0, new BBox(700, 0, 50, 50), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => _pdf.RenderRegionPixmap(0, new BBox(10, 10, 0, 50), 2));
    }

    [Fact]
    public void CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => _pdf.RenderRegionPixmap(0, new BBox(0, 0, 10, 10), 2, ct: cts.Token));
    }

    [Fact]
    public void Extension_UsesTheCapability_WhenPresent()
    {
        var region = new BBox(300f, 200f, 80f, 20f);
        var direct = _pdf.RenderRegionPixmap(0, region, 6);
        var viaExtension = PdfRegionRendering.RenderRegionPixmap(_pdf, 0, region, 6);
        Assert.Equal(direct.Width, viaExtension.Width);
        Assert.Equal(direct.Height, viaExtension.Height);
        Assert.Equal(direct.RgbBytes, viaExtension.RgbBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Extension_FallsBackToRenderAndCrop_WithoutTheCapability(int viewRotation)
    {
        const double Scale = 3;
        var region = new BBox(280f, 180f, 240f, 60f);
        IPdfService plain = new PlainPdfService(_pdf);
        Assert.False(plain is IPdfRegionRenderService);

        var fallback = plain.RenderRegionPixmap(0, region, Scale, viewRotation);
        var direct = _pdf.RenderRegionPixmap(0, region, Scale, viewRotation);

        // The fallback crops a whole-page raster sized by longest edge, so its scale can differ from
        // Scale by under a pixel per page: allow a pixel of size difference and compare the overlap.
        Assert.InRange(fallback.Width, direct.Width - 1, direct.Width + 1);
        Assert.InRange(fallback.Height, direct.Height - 1, direct.Height + 1);
        int w = Math.Min(fallback.Width, direct.Width), h = Math.Min(fallback.Height, direct.Height);
        long total = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w * 3; x++)
                total += Math.Abs(fallback.RgbBytes[y * fallback.Width * 3 + x] - direct.RgbBytes[y * direct.Width * 3 + x]);
        double meanDiff = (double)total / (w * h * 3);
        Assert.True(meanDiff < 6, $"fallback differs from the region render by {meanDiff:F2} levels on average");
    }

    /// <summary>
    /// Every strongly differing pixel must be matched by the other image within one pixel: integer
    /// offsets change PDFium's anti-aliasing a little (see <see cref="TiledRenderTests"/>), a misplaced
    /// region by far more.
    /// </summary>
    private static void AssertMatchesCrop(byte[] rgb, int w, int h, SKBitmap whole, int x0, int y0)
    {
        var page = whole.GetPixelSpan();
        int rowBytes = whole.RowBytes, pageW = whole.Width, pageH = whole.Height;
        const int Tol = 32;

        (int R, int G, int B) Region(int x, int y) { int o = (y * w + x) * 3; return (rgb[o], rgb[o + 1], rgb[o + 2]); }
        (int R, int G, int B) Page(ReadOnlySpan<byte> p, int x, int y) { int o = y * rowBytes + x * 4; return (p[o + 2], p[o + 1], p[o]); }
        static bool Close((int R, int G, int B) a, (int R, int G, int B) b) =>
            Math.Abs(a.R - b.R) <= Tol && Math.Abs(a.G - b.G) <= Tol && Math.Abs(a.B - b.B) <= Tol;

        long differing = 0, unmatched = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int px = x0 + x, py = y0 + y;
                Assert.True(px < pageW && py < pageH, "region extends past the whole-page raster");
                var r = Region(x, y);
                var p = Page(page, px, py);
                int d = Math.Max(Math.Abs(r.R - p.R), Math.Max(Math.Abs(r.G - p.G), Math.Abs(r.B - p.B)));
                if (d == 0) continue;
                differing++;
                if (d <= Tol) continue;
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1 && !near; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx >= 0 && ny >= 0 && nx < pageW && ny < pageH && Close(r, Page(page, nx, ny))) near = true;
                    }
                if (!near) unmatched++;
            }
        Assert.True(differing <= (long)w * h / 100, $"{differing} of {w * h} pixels differ");
        Assert.True(unmatched == 0, $"{unmatched} pixels differ beyond a one-pixel shift");
    }

    /// <summary>Forwards the plain <see cref="IPdfService"/> surface only, hiding the optional capabilities.</summary>
    private sealed class PlainPdfService(IPdfService inner) : IPdfService
    {
        public byte[] PdfBytes => inner.PdfBytes;
        public string? Password => inner.Password;
        public int PageCount => inner.PageCount;
        public List<OutlineEntry> Outline => inner.Outline;
        public (double Width, double Height) GetPageSize(int pageIndex) => inner.GetPageSize(pageIndex);
        public (double Width, double Height) GetPageSize(int pageIndex, int viewRotation) => inner.GetPageSize(pageIndex, viewRotation);
        public IRenderedPage RenderPage(int pageIndex, int dpi = 200) => inner.RenderPage(pageIndex, dpi);
        public IRenderedPage RenderThumbnail(int pageIndex) => inner.RenderThumbnail(pageIndex);
        public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
            => inner.RenderPagePixmap(pageIndex, targetSize);
        public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize, int viewRotation)
            => inner.RenderPagePixmap(pageIndex, targetSize, viewRotation);
    }
}
