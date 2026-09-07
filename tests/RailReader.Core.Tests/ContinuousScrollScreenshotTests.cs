using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Phase 4 coverage: <see cref="ScreenshotCompositor.RenderPageContinuous"/> composites every
/// visible page at the correct screen offset — the CLI/agent screenshot equivalent of the GUI's
/// per-page draw loop (docs/continuous-scroll-plan.md §5 Phase 4).
/// </summary>
public class ContinuousScrollScreenshotTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;
    private readonly ColourEffectShaders _shaders = new();

    public ContinuousScrollScreenshotTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig().ToCoreSettings() with { ContinuousScroll = true };
        var appConfig = new AppConfig();
        _controller = new DocumentController(config, appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose()
    {
        _controller.Dispose();
        _shaders.Dispose();
    }

    private DocumentModel Open()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        return doc;
    }

    [Fact]
    public void RenderPageContinuous_ProducesViewportSizedBitmap()
    {
        var doc = Open();
        var options = new ScreenshotOptions
        {
            Dpi = 72, RailOverlay = false, SearchHighlights = false, Annotations = false,
            DebugOverlay = false, SimulateViewport = true, ViewportWidth = 800, ViewportHeight = 600,
        };

        using var bmp = ScreenshotCompositor.RenderPageContinuous(doc, _controller, _shaders, options);
        Assert.Equal(800, bmp.Width);
        Assert.Equal(600, bmp.Height);
    }

    [Fact]
    public void RenderPageContinuous_AtPageBoundary_DrawsMoreThanOnePageIntoTheFrame()
    {
        var doc = Open();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        Assert.True(layout.Count >= 2);

        double z = vp.Camera.Zoom = Math.Min(0.5, vp.Camera.Zoom);
        vp.ClampCamera(vp.Width, vp.Height);
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.EnsureRenderWindow(vp.Width, vp.Height);

        Assert.True(vp.VisiblePages.Count >= 2, "the render window must cover the boundary");
        var secondPage = vp.VisiblePages.First(v => v.Page != vp.CurrentPage);

        var options = new ScreenshotOptions
        {
            Dpi = 72, RailOverlay = false, SearchHighlights = false, Annotations = false,
            DebugOverlay = false, SimulateViewport = true, ViewportWidth = 800, ViewportHeight = 600,
        };
        using var bmp = ScreenshotCompositor.RenderPageContinuous(doc, _controller, _shaders, options);
        Assert.Equal(800, bmp.Width);
        Assert.Equal(600, bmp.Height);

        // Actually verify the second page was drawn, not just that VisiblePages said it should be:
        // sample a point well inside its rect (a rendered page is white/light; the canvas is cleared
        // to black, so a still-black sample there means the compositor's loop skipped this entry).
        int sampleX = (int)(secondPage.OffsetX + secondPage.Width * z / 2);
        int sampleY = (int)(secondPage.OffsetY + secondPage.Height * z / 2);
        Assert.InRange(sampleX, 0, bmp.Width - 1);
        Assert.InRange(sampleY, 0, bmp.Height - 1);
        var sampled = bmp.GetPixel(sampleX, sampleY);
        Assert.False(sampled.Red == 0 && sampled.Green == 0 && sampled.Blue == 0,
            $"expected page {secondPage.Page}'s content at ({sampleX},{sampleY}), found the uncleared black background");
    }

    [Fact]
    public void RenderPageContinuous_NonAnchorPageWithLineFocusBlur_StaysBright_NotFlatDimmed()
    {
        // A non-anchor visible page has no seated line to keep sharp, so it gets a real Gaussian
        // blur of the WHOLE page (matching the anchor's own non-active-line treatment) rather than
        // the old v1 flat semi-transparent-black overlay. A blur of a uniform (blank) region is a
        // no-op, while the old dim overlay would have scaled a white pixel down to a fixed grey
        // (~145 at its alpha=110/255) — sampling a blank area of the neighbour page distinguishes
        // the two directly.
        var doc = Open();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        Assert.True(layout.Count >= 2);

        // Seat the rail on the anchor page directly (mirrors FocusBlockTests/ContinuousScrollRailTests
        // — no zoom threshold needed) so `railFocusable` (Rail.Active && NavigableCount > 0) holds.
        var block = new LayoutBlock
        {
            Role = BlockRole.Text, BBox = new BBox(72, 72, 468, 40), Confidence = 0.9f, Order = 0,
        };
        block.Lines.Add(new LineInfo(72, 16, 72, 468));
        var analysis = new PageAnalysis();
        analysis.Blocks.Add(block);
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, analysis);
        doc.ReapplyNavigableRoles(vp, _controller.Config.NavigableRoles);
        vp.Rail.Active = true;
        Assert.True(vp.Rail.NavigableCount > 0);

        double z = vp.Camera.Zoom = Math.Min(0.5, vp.Camera.Zoom);
        vp.ClampCamera(vp.Width, vp.Height);
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.EnsureRenderWindow(vp.Width, vp.Height);

        var secondPage = vp.VisiblePages.First(v => v.Page != vp.CurrentPage);

        var options = new ScreenshotOptions
        {
            Dpi = 72, RailOverlay = false, SearchHighlights = false, Annotations = false,
            DebugOverlay = false, SimulateViewport = true, ViewportWidth = 800, ViewportHeight = 600,
            LineFocusBlur = true, LineFocusBlurIntensity = 1.0f,
        };
        using var bmp = ScreenshotCompositor.RenderPageContinuous(doc, _controller, _shaders, options);

        // Page-local (300, 400) is well below TestFixtures' text (drawn at y ~= 72..140) and well
        // inside the page — a blank white region under a real blur, but a fixed grey under a flat dim.
        int sampleX = (int)(secondPage.OffsetX + 300 * z);
        int sampleY = (int)(secondPage.OffsetY + 400 * z);
        Assert.InRange(sampleX, 0, bmp.Width - 1);
        Assert.InRange(sampleY, 0, bmp.Height - 1);
        var sampled = bmp.GetPixel(sampleX, sampleY);
        Assert.True(sampled.Red > 200 && sampled.Green > 200 && sampled.Blue > 200,
            $"expected the neighbour page's blank region to stay bright under a real blur, got {sampled}");
    }
}
