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
            using var font = new SKFont(SKTypeface.Default, 14);
            var canvas = doc.BeginPage(612, 792);
            canvas.DrawLine(20, 20, 592, 772, paint);
            canvas.DrawCircle(400, 300, 120, fill);
            for (int y = 60; y < 780; y += 37)
                canvas.DrawText($"Line at {y} — the quick brown fox", 72, y, font, fill);
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
    public void TiledRender_MatchesWholePageRender(int viewRotation)
    {
        // 400 DPI: 3400 px wide → ~588-row bands, so the page renders in several bands.
        using var whole = (SkiaRenderedPage)_pdf.RenderPage(0, 400, viewRotation);
        using var tiled = (SkiaRenderedPage)_pdf.RenderPageTiled(0, 400, viewRotation, CancellationToken.None);

        Assert.Equal(whole.Width, tiled.Width);
        Assert.Equal(whole.Height, tiled.Height);

        var a = whole.Bitmap.GetPixelSpan();
        var b = tiled.Bitmap.GetPixelSpan();
        Assert.Equal(a.Length, b.Length);
        // Bands render the whole page's transform shifted by an integer offset, but PDFium's
        // anti-aliasing isn't strictly independent of the target bitmap's extent: a handful of edge
        // pixels (glyph and curve edges) come out a few levels apart, anywhere in the page — not at
        // band boundaries, and up to ~1% of a row that runs along an edge. Bound that noise. A
        // misplaced band would instead show full-contrast deltas along the diagonal and text, which
        // the max-delta bound rules out.
        int rowBytes = whole.Bitmap.RowBytes;
        long differing = 0;
        int maxDelta = 0, worstRow = 0;
        for (int row = 0; row < whole.Height; row++)
        {
            int rowDiffs = 0;
            for (int i = row * rowBytes, end = i + rowBytes; i < end; i++)
            {
                int d = Math.Abs(a[i] - b[i]);
                if (d > 0) { rowDiffs++; maxDelta = Math.Max(maxDelta, d); }
            }
            differing += rowDiffs;
            worstRow = Math.Max(worstRow, rowDiffs);
        }
        Assert.True(differing <= a.Length / 1_000, $"{differing} of {a.Length} bytes differ");
        Assert.True(maxDelta <= 32, $"max channel delta {maxDelta}");
        Assert.True(worstRow <= rowBytes / 50, $"a row has {worstRow} of {rowBytes} bytes differing");
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
