using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Runtime behaviour of render-quality preset changes: the cache-invalidation
/// path (OnRenderQualityChanged / UpdateRenderDpiIfNeeded). These exercise the
/// synchronous decision logic — the equality guard, deferral while scrolling,
/// and the retry once scrolling stops — without depending on the async
/// background re-render completing.
/// </summary>
public class RenderQualityRuntimeTests : IDisposable
{
    private readonly DocumentController _controller;
    private readonly DocumentModel _doc;

    public RenderQualityRuntimeTests()
    {
        var pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        _doc = _controller.CreateDocument(pdfPath);
        _doc.LoadPageBitmap();
        _controller.AddDocument(_doc);
        _controller.SetViewportSize(800, 600);
        TestFixtures.SetupRailMode(_doc, _controller.Config, 800, 600);
        _doc.Rail.StartAutoScroll(100.0);
        Assert.True(_doc.Rail.AutoScrolling); // precondition for the deferral tests
    }

    public void Dispose() => _controller.Dispose();

    [Fact]
    public void OnRenderQualityChanged_SameSettings_IsNoOp()
    {
        // #1: the default is the Quality preset; re-applying it must short-circuit
        // (no dirty flag set) even while scrolling, so unrelated config changes
        // that funnel through here don't invalidate anything.
        _doc.OnRenderQualityChanged(RenderDpiSettings.ForPreset(RenderQuality.Quality));
        Assert.False(_doc.RenderDpiPending);
    }

    [Fact]
    public void OnRenderQualityChanged_RealChangeWhileScrolling_IsDeferred()
    {
        // #3: a genuine preset change while auto-scrolling must NOT jump the PDFium
        // gate mid-scroll — it stays pending instead of re-rendering immediately.
        _doc.OnRenderQualityChanged(RenderDpiSettings.ForPreset(RenderQuality.Ultra));
        Assert.True(_doc.RenderDpiPending);
    }

    [Fact]
    public void PendingChange_IsHonouredOnceScrollStops()
    {
        // #2: the deferred change must be applied (dirty cleared) on the next
        // UpdateRenderDpiIfNeeded once scrolling has stopped — it must not be
        // starved by auto-scroll keeping the animation loop "animating".
        _doc.OnRenderQualityChanged(RenderDpiSettings.ForPreset(RenderQuality.Ultra));
        Assert.True(_doc.RenderDpiPending);

        _doc.Rail.StopAutoScroll();
        Assert.False(_doc.Rail.AutoScrolling);

        _doc.UpdateRenderDpiIfNeeded();
        Assert.False(_doc.RenderDpiPending);
    }

    [Fact]
    public void SameSettingsChange_DoesNotBecomePending_WhileScrolling()
    {
        // Contrast with the real-change case: a no-op change leaves nothing
        // pending even mid-scroll (guards against re-introducing the unconditional
        // invalidation that dropped the prefetch on every config change).
        _doc.OnRenderQualityChanged(RenderDpiSettings.Default); // == current (Quality)
        Assert.False(_doc.RenderDpiPending);

        _doc.OnRenderQualityChanged(RenderDpiSettings.ForPreset(RenderQuality.Performance));
        Assert.True(_doc.RenderDpiPending);
    }

    [Fact]
    public void OnSliderChanged_PropagatesRenderDpiToDocuments()
    {
        // #6: OnSliderChanged must keep each document's render-DPI in sync with
        // _config (not just _config itself). Pushing a config whose RenderDpi
        // differs, while scrolling, must leave the change pending on the document
        // — proving it propagated through OnRenderQualityChanged rather than
        // updating _config alone.
        var newConfig = _controller.Config with { RenderDpi = RenderDpiSettings.ForPreset(RenderQuality.Ultra) };
        _controller.OnSliderChanged(newConfig);
        Assert.True(_doc.RenderDpiPending);
    }
}
