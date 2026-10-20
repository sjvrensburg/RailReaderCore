using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #147: <see cref="SkiaPdfService.RenderPageTiled"/> renders a page band by band, releasing
/// the PDFium gate between bands, and must produce the same bitmap as a whole-page render.
/// </summary>
public class TiledRenderTests : IDisposable
{
    private readonly string _pdfPath = Path.Combine(Path.GetTempPath(), $"tiled-render-{Guid.NewGuid():N}.pdf");
    private readonly SkiaPdfService _pdf;

    public TiledRenderTests()
    {
        // Content across the whole page (a diagonal, a disc and text near the bottom), so every band
        // boundary cuts through something and a misplaced band would show.
        using (var stream = File.Create(_pdfPath))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = SKColors.DarkBlue, IsAntialias = true };
            // Page 1 uses the platform default face, page 2 DejaVu Sans: its em dash ends on a
            // half-pixel at 400 DPI under rotation 3, which is what first exposed the rounding below.
            foreach (var typeface in new[] { SKTypeface.Default, SKTypeface.FromFamilyName("DejaVu Sans") })
            {
                using var font = new SKFont(typeface, 14);
                var canvas = doc.BeginPage(612, 792);
                canvas.DrawLine(20, 20, 592, 772, paint);
                canvas.DrawCircle(400, 300, 120, fill);
                for (int y = 60; y < 780; y += 37)
                    canvas.DrawText($"Line at {y} — the quick brown fox", 72, y, font, fill);
                doc.EndPage();
            }
            doc.Close();
        }
        _pdf = new SkiaPdfService(_pdfPath);
    }

    public void Dispose()
    {
        try { File.Delete(_pdfPath); } catch { }
    }

    public static TheoryData<int, int> PagesAndRotations()
    {
        var data = new TheoryData<int, int>();
        for (int page = 0; page < 2; page++)
            for (int rotation = 0; rotation < 4; rotation++)
                data.Add(page, rotation);
        return data;
    }

    [Theory]
    [MemberData(nameof(PagesAndRotations))]
    public void TiledRender_MatchesWholePageRender(int page, int viewRotation)
    {
        // 400 DPI: 3400 px wide → ~588-row bands, so the page renders in several bands.
        using var whole = (SkiaRenderedPage)_pdf.RenderPage(page, 400, viewRotation);
        using var tiled = (SkiaRenderedPage)_pdf.RenderPageTiled(page, 400, viewRotation, CancellationToken.None);

        Assert.Equal(whole.Width, tiled.Width);
        Assert.Equal(whole.Height, tiled.Height);

        var a = whole.Bitmap.GetPixelSpan();
        var b = tiled.Bitmap.GetPixelSpan();
        Assert.Equal(a.Length, b.Length);

        // Bands render the whole page's transform shifted by an integer offset, but PDFium's rounding
        // isn't strictly offset-independent: anti-aliased edges come out a few levels apart, and a
        // glyph edge that lands exactly on a half-pixel (an em dash's ends, depending on the font) can
        // round one pixel the other way — anywhere in the page, not at band boundaries. So every
        // strongly differing pixel must be matched by the other render within one pixel; a misplaced
        // or missing band is off by far more than that and fails.
        int w = whole.Width, h = whole.Height, rowBytes = whole.Bitmap.RowBytes;
        const int Tol = 32;
        bool Near(ReadOnlySpan<byte> src, int x, int y, ReadOnlySpan<byte> other)
        {
            int o = y * rowBytes + x * 4;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = ny * rowBytes + nx * 4;
                    if (Math.Abs(src[o] - other[n]) <= Tol && Math.Abs(src[o + 1] - other[n + 1]) <= Tol
                        && Math.Abs(src[o + 2] - other[n + 2]) <= Tol)
                        return true;
                }
            return false;
        }

        long differing = 0, unmatched = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = y * rowBytes + x * 4;
                int d = Math.Max(Math.Abs(a[o] - b[o]), Math.Max(Math.Abs(a[o + 1] - b[o + 1]), Math.Abs(a[o + 2] - b[o + 2])));
                if (d == 0) continue;
                differing++;
                if (d > Tol && !(Near(a, x, y, b) && Near(b, x, y, a))) unmatched++;
            }
        Assert.True(differing <= (long)w * h / 500, $"{differing} of {w * h} pixels differ");
        Assert.True(unmatched == 0, $"{unmatched} pixels differ beyond a one-pixel shift");
    }

    [Fact]
    public void TiledRender_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _pdf.RenderPageTiled(0, 400, 0, cts.Token));
    }

    [Fact]
    public void PageChangeDuringUpgrade_CancelsIt_AndTheNewPageStillUpgrades()
    {
        var config = new AppConfig();
        using var controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        var doc = controller.CreateDocument(TestFixtures.GetTestPdfPath());
        doc.LoadPageBitmap();
        controller.AddDocument(doc);
        controller.SetViewportSize(800, 600);
        if (doc.PageCount < 3) return;

        var vp = doc.Primary;
        vp.Camera.Zoom = 5.0;
        controller.GoToPage(1);
        Assert.True(vp.RenderDpiDirty);
        Assert.True(vp.UpdateRenderDpiIfNeeded()); // page 1's upgrade is now in flight

        controller.GoToPage(2);                     // supersedes it
        Assert.Equal(2, vp.CurrentPage);
        Assert.Equal(vp.RenderDpi.PreviewDpi, vp.CachedDpi);

        int full = DocumentModel.CalculateRenderDpi(vp.Camera.Zoom, vp.PageWidth, vp.PageHeight, vp.RenderDpi);
        long deadline = Environment.TickCount64 + 5000;
        while ((vp.CachedDpi != full || vp.DpiRenderPending) && Environment.TickCount64 < deadline)
        {
            controller.TickViewport(vp, 1.0 / 60);
            Thread.Sleep(5);
        }
        Assert.Equal(2, vp.CurrentPage);
        Assert.Equal(full, vp.CachedDpi);
        Assert.False(vp.DpiRenderPending);
    }
}
