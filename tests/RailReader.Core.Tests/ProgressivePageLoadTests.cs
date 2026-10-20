using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #147: a fresh single-page load at high zoom renders a low-DPI preview synchronously and
/// flags a forced re-render, so the async DPI path brings it to full quality instead of the UI
/// thread blocking on a full-resolution render.
/// </summary>
public class ProgressivePageLoadTests : IDisposable
{
    private readonly DocumentController _controller;
    private readonly DocumentModel _doc;

    public ProgressivePageLoadTests()
    {
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        _doc = _controller.CreateDocument(TestFixtures.GetTestPdfPath());
        _doc.LoadPageBitmap();
        _controller.AddDocument(_doc);
        _controller.SetViewportSize(800, 600);
    }

    public void Dispose() => _controller.Dispose();

    private int FullDpi(Viewport vp) =>
        DocumentModel.CalculateRenderDpi(vp.Camera.Zoom, vp.PageWidth, vp.PageHeight, vp.RenderDpi);

    private static void WaitForDpi(Viewport vp, int dpi)
    {
        long deadline = Environment.TickCount64 + 5000;
        while ((vp.CachedDpi != dpi || vp.DpiRenderPending) && Environment.TickCount64 < deadline)
        {
            vp.UpdateRenderDpiIfNeeded();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void FreshLoadAtHighZoom_ShowsPreviewThenUpgradesToFullDpi()
    {
        var vp = _doc.Primary;
        vp.Camera.Zoom = 5.0;
        Assert.True(vp.LoadPageBitmap());

        int full = FullDpi(vp);
        Assert.True(full > vp.RenderDpi.PreviewDpi * vp.RenderDpi.PreviewMinRatio); // precondition
        Assert.Equal(vp.RenderDpi.PreviewDpi, vp.CachedDpi);
        Assert.True(vp.RenderDpiDirty, "a preview must flag a forced re-render");

        // Geometry comes from the page size, not the bitmap: the preview must not change it.
        var (w, h) = _doc.Pdf.GetPageSize(vp.CurrentPage, _doc.ViewRotation);
        Assert.Equal(w, vp.PageWidth);
        Assert.Equal(h, vp.PageHeight);

        Assert.True(vp.UpdateRenderDpiIfNeeded());
        WaitForDpi(vp, full);
        Assert.Equal(full, vp.CachedDpi);
        Assert.False(vp.RenderDpiDirty);
    }

    [Fact]
    public void FreshLoadAtLowZoom_RendersFullDpiDirectly()
    {
        // Full DPI within PreviewMinRatio of the preview: the render is cheap, load as before.
        var vp = _doc.Primary;
        vp.Camera.Zoom = 1.2;
        Assert.True(vp.LoadPageBitmap());

        Assert.Equal(FullDpi(vp), vp.CachedDpi);
        Assert.False(vp.RenderDpiDirty);
    }

    [Fact]
    public void PreviewDisabled_RendersFullDpiDirectly()
    {
        var vp = _doc.Primary;
        _doc.OnRenderQualityChanged(vp.RenderDpi with { PreviewDpi = 0 });
        vp.RenderDpiDirty = false; // the quality change itself arms a re-render
        vp.Camera.Zoom = 5.0;
        Assert.True(vp.LoadPageBitmap());

        Assert.Equal(FullDpi(vp), vp.CachedDpi);
        Assert.False(vp.RenderDpiDirty);
    }

    [Fact]
    public void FreshLoadWhileAutoScrolling_RendersFullDpiDirectly()
    {
        // Re-renders are deferred while scrolling, so a preview would stay soft for the whole
        // scroll — the load must render at full DPI instead.
        TestFixtures.SetupRailMode(_doc, _controller.Config, 800, 600);
        _doc.Rail.StartAutoScroll(100.0);
        Assert.True(_doc.Rail.AutoScrolling);

        var vp = _doc.Primary;
        vp.Camera.Zoom = 5.0;
        Assert.True(vp.LoadPageBitmap());

        Assert.Equal(FullDpi(vp), vp.CachedDpi);
        Assert.False(vp.RenderDpiDirty);
    }

    [Fact]
    public void GoToPageAtRailZoom_UsesPreview_AndTickUpgradesIt()
    {
        if (_doc.PageCount < 2) return;
        var vp = _doc.Primary;
        vp.Camera.Zoom = 5.0;

        _controller.GoToPage(1);
        Assert.Equal(1, vp.CurrentPage);
        Assert.Equal(vp.RenderDpi.PreviewDpi, vp.CachedDpi);

        int full = FullDpi(vp);
        long deadline = Environment.TickCount64 + 5000;
        while ((vp.CachedDpi != full || vp.DpiRenderPending) && Environment.TickCount64 < deadline)
        {
            _controller.TickViewport(vp, 1.0 / 60);
            Thread.Sleep(5);
        }
        Assert.Equal(full, vp.CachedDpi);
    }
}
