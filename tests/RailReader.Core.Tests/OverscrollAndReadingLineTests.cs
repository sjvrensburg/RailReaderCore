using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// #199 (vertical over-scroll margin in <see cref="Viewport.ClampCamera(double, double)"/>) and #200
/// (configurable rail reading-line height, <see cref="CoreSettings.RailReadingLineFraction"/>).
/// </summary>
public class OverscrollAndReadingLineTests : IDisposable
{
    private const double W = 800, H = 600;
    private readonly List<DocumentController> _controllers = [];
    private double _nowMs;

    public void Dispose()
    {
        foreach (var c in _controllers) c.Dispose();
    }

    private (DocumentController Controller, DocumentModel Doc, Viewport Vp) Setup(
        double overscroll = 1.0 / 3.0, double readingLine = 0.5)
    {
        var appConfig = new AppConfig
        {
            ScrollSpeedZoomReference = 0,
            PixelSnapping = false,
            VerticalOverscrollFraction = overscroll,
            RailReadingLineFraction = readingLine,
        };
        var controller = new DocumentController(appConfig.ToCoreSettings(), appConfig, AnnotationService.Default,
            new QueuedThreadMarshaller(), TestFixtures.CreatePdfFactory());
        _controllers.Add(controller);
        var doc = controller.CreateDocument(TestFixtures.GetTestPdfPath()); // 3 pages
        doc.LoadPageBitmap();
        controller.AddDocument(doc);
        controller.SetViewportSize(W, H);
        var vp = doc.Primary;
        controller.FocusedViewport = vp;
        return (controller, doc, vp);
    }

    /// <summary>Zoom so the page is taller than the window (browse mode, below the rail threshold).</summary>
    private static void ZoomTall(Viewport vp, double zoom = 2.0)
    {
        vp.Camera.Zoom = zoom;
        Assert.True(vp.PageHeight * zoom > H);
        Assert.True(zoom < vp.Rail.ZoomThreshold);
    }

    // ===================== #199: over-scroll margin =====================

    [Theory]
    [InlineData(-1e6, 1000, 600)]
    [InlineData(-200, 1000, 600)]
    [InlineData(50, 1000, 600)]
    [InlineData(1e6, 1000, 600)]
    [InlineData(-1e6, 300, 600)]
    [InlineData(123, 300, 600)]
    [InlineData(1e6, 300, 600)]
    public void ClampVerticalOffset_ZeroMargin_IsTheStrictClamp(double offset, double scaledH, double wh)
    {
        double strict = scaledH <= wh ? (wh - scaledH) / 2.0 : Math.Clamp(offset, wh - scaledH, 0);
        Assert.Equal(strict, Viewport.ClampVerticalOffset(offset, scaledH, wh, 0));
    }

    [Fact]
    public void ClampVerticalOffset_RangesMeetAtWindowHeight()
    {
        // No jump when a zoom takes the page across the window height.
        const double m = 200;
        Assert.Equal(m, Viewport.ClampVerticalOffset(1e6, H, H, m), precision: 9);
        Assert.Equal(m, Viewport.ClampVerticalOffset(1e6, H + 1e-9, H, m), precision: 6);
        Assert.Equal(-m, Viewport.ClampVerticalOffset(-1e6, H, H, m), precision: 9);
        Assert.Equal(-m, Viewport.ClampVerticalOffset(-1e6, H + 1e-9, H, m), precision: 6);
    }

    [Fact]
    public void ZeroFraction_ClampMatchesStrictClamp()
    {
        var (_, _, vp) = Setup(overscroll: 0);
        foreach (double zoom in new[] { 0.5, 2.0 })
            foreach (double oy in new[] { -1e6, -300, 0, 40, 1e6 })
            {
                vp.Camera.Zoom = zoom;
                vp.Camera.OffsetY = oy;
                vp.ClampCamera(W, H);
                double withMargin = vp.Camera.OffsetY;
                vp.Camera.OffsetY = oy;
                vp.ClampCamera(W, H, overscroll: false);
                Assert.Equal(vp.Camera.OffsetY, withMargin);
            }
    }

    [Fact]
    public void TallPage_EdgesComeInwardByTheMarginOnly()
    {
        var (_, _, vp) = Setup();
        ZoomTall(vp);
        double m = H / 3.0;
        double scaledH = vp.PageHeight * vp.Camera.Zoom;

        vp.Camera.OffsetY = 1e6;
        vp.ClampCamera(W, H);
        Assert.Equal(m, vp.Camera.OffsetY, precision: 6);

        vp.Camera.OffsetY = -1e6;
        vp.ClampCamera(W, H);
        Assert.Equal(H - scaledH - m, vp.Camera.OffsetY, precision: 6);

        // Inside the margin: untouched.
        vp.Camera.OffsetY = m / 2;
        vp.ClampCamera(W, H);
        Assert.Equal(m / 2, vp.Camera.OffsetY, precision: 9);
    }

    [Fact]
    public void ShortPage_FitPageLandsCentred_PanMovesWithinMargin()
    {
        var (controller, _, vp) = Setup();
        controller.FitPage();
        double scaledH = vp.PageHeight * vp.Camera.Zoom;
        Assert.True(scaledH <= H + 1e-6);
        double centred = (H - scaledH) / 2.0;
        Assert.Equal(centred, vp.Camera.OffsetY, precision: 6);

        controller.HandlePan(0, 1e6);
        Assert.Equal(centred + H / 3.0, vp.Camera.OffsetY, precision: 6);
        controller.HandlePan(0, -1e6);
        Assert.Equal(centred - H / 3.0, vp.Camera.OffsetY, precision: 6);
    }

    [Fact]
    public void MaxFraction_PageNeverLeavesTheView()
    {
        var (_, _, vp) = Setup(overscroll: 5.0); // clamped to MaxVerticalOverscrollFraction
        foreach (double zoom in new[] { 0.3, 0.75, 2.0 })
            foreach (double oy in new[] { -1e7, 1e7 })
            {
                vp.Camera.Zoom = zoom;
                vp.Camera.OffsetY = oy;
                vp.ClampCamera(W, H);
                double top = vp.Camera.OffsetY, bottom = top + vp.PageHeight * zoom;
                double visible = Math.Min(bottom, H) - Math.Max(top, 0);
                Assert.True(visible > 0, $"zoom {zoom}, offset {oy}: page left the view");
                if (vp.PageHeight * zoom > H)
                    Assert.True(visible >= H * (1 - CoreSettings.MaxVerticalOverscrollFraction) - 1e-6);
            }
    }

    [Fact]
    public void NonFiniteFraction_MeansNoMargin()
    {
        Assert.Equal(0.0, new CoreSettings { VerticalOverscrollFraction = double.NaN }.EffectiveVerticalOverscrollFraction);
        Assert.Equal(0.0, new CoreSettings { VerticalOverscrollFraction = -1 }.EffectiveVerticalOverscrollFraction);
        Assert.Equal(0.0, new CoreSettings().EffectiveVerticalOverscrollFraction); // Core default: old behaviour
    }

    [Fact]
    public void PageEdgeHold_TriggersAtExtendedEdge_AndTurnedPageLandsFlush()
    {
        var (controller, _, vp) = Setup();
        ZoomTall(vp);
        vp.Camera.OffsetY = 0;
        double scaledH = vp.PageHeight * vp.Camera.Zoom;

        for (int i = 0; i < 500 && vp.CurrentPage == 0; i++)
            controller.HandleArrowDown();
        Assert.Equal(0, vp.CurrentPage); // the hold needs time, not just presses
        Assert.Equal(H - scaledH - H / 3.0, vp.Camera.OffsetY, precision: 6); // stopped at the extended edge

        Thread.Sleep(450);
        controller.HandleArrowDown();
        Assert.Equal(1, vp.CurrentPage);
        Assert.Equal(0.0, vp.Camera.OffsetY, precision: 6); // flush top, not in the margin
    }

    [Fact]
    public void PageEdgeHold_OnShortPage_TurnedPageLandsCentred()
    {
        var (controller, _, vp) = Setup();
        controller.FitPage();
        double centred = vp.Camera.OffsetY;

        for (int i = 0; i < 500; i++) controller.HandleArrowDown();
        Assert.Equal(0, vp.CurrentPage);
        Thread.Sleep(450);
        controller.HandleArrowDown();
        Assert.Equal(1, vp.CurrentPage);
        Assert.Equal(centred, vp.Camera.OffsetY, precision: 6);
    }

    [Fact]
    public void GoToPage_FromTheMargin_LandsFlush()
    {
        var (controller, _, vp) = Setup();
        ZoomTall(vp);
        controller.HandlePan(0, 1e6);
        Assert.Equal(H / 3.0, vp.Camera.OffsetY, precision: 6);

        controller.GoToPage(1);
        Assert.Equal(1, vp.CurrentPage);
        Assert.Equal(0.0, vp.Camera.OffsetY, precision: 6);
    }

    [Fact]
    public void NarrowingTheMargin_ReclampsABrowseCamera()
    {
        var (controller, _, vp) = Setup();
        ZoomTall(vp);
        controller.HandlePan(0, 1e6);
        Assert.Equal(H / 3.0, vp.Camera.OffsetY, precision: 6);

        controller.OnConfigChanged(controller.Config with { VerticalOverscrollFraction = 0.1 });
        Assert.Equal(H * 0.1, vp.Camera.OffsetY, precision: 6);
    }

    // ===================== rail, with both settings =====================

    /// <summary>Page 0 and page 1: one text block of 30 lines, 16pt apart, filling most of the page.</summary>
    private static PageAnalysis TallTextBlock()
    {
        var block = new LayoutBlock { Role = BlockRole.Text, BBox = new BBox(72, 72, 300, 480), Confidence = 0.9f, Order = 0 };
        for (int i = 0; i < 30; i++)
            block.Lines.Add(new LineInfo(80 + i * 16, 16, 72, 300));
        var analysis = new PageAnalysis();
        analysis.Blocks.Add(block);
        return analysis;
    }

    private (DocumentController Controller, Viewport Vp) SetupRail(double overscroll = 1.0 / 3.0, double readingLine = 0.5)
    {
        var (controller, doc, vp) = Setup(overscroll, readingLine);
        for (int p = 0; p < doc.PageCount; p++)
            doc.SetAnalysis(p, doc.DefaultAnalysisParams, TallTextBlock());
        doc.ReapplyNavigableRoles(vp, controller.Config.NavigableRoles);
        vp.Camera.Zoom = controller.Config.RailZoomThreshold + 1;
        vp.ClampCamera(W, H);
        vp.UpdateRailZoom(W, H);
        vp.Rail.EdgeHoldNowMsOverride = () => _nowMs;
        Assert.True(vp.Rail.Active);
        return (controller, vp);
    }

    private void Settle(DocumentController controller, Viewport vp)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            _nowMs += 16;
            controller.TickViewport(vp, 0.016);
        } while ((vp.Rail.SnapProgress < 1.0 || vp.Zoom.IsAnimating) && sw.ElapsedMilliseconds < 3000);
    }

    private static double LineScreenY(Viewport vp) => vp.Rail.CurrentLineInfo.Y * vp.Camera.Zoom + vp.Camera.OffsetY;

    [Fact]
    public void RailDrag_IntoTheMargin_RecordsThatBias()
    {
        // #199 part 2: near the page bottom the reader can drag the line above the middle.
        var (controller, vp) = SetupRail();
        vp.Rail.CurrentLine = vp.Rail.CurrentNavigableBlock.Lines.Count - 1; // last line
        vp.StartSnap(W, H);
        Settle(controller, vp);

        controller.HandlePan(0, -1e6); // drag the page up as far as it goes
        double scaledH = vp.PageHeight * vp.Camera.Zoom;
        Assert.Equal(H - scaledH - H / 3.0, vp.Camera.OffsetY, precision: 6);
        double expectedBias = vp.Camera.OffsetY - (H / 2.0 - vp.Rail.CurrentLineInfo.Y * vp.Camera.Zoom);
        Assert.Equal(expectedBias, vp.Rail.VerticalBias, precision: 6);
    }

    [Fact]
    public void ReadingLine_Default_SeatsAtTheCentre()
    {
        var (controller, vp) = SetupRail();
        vp.StartSnap(W, H);
        Settle(controller, vp);
        Assert.Equal(H / 2.0, LineScreenY(vp), precision: 6);
    }

    [Fact]
    public void ReadingLine_SeatsAtTheConfiguredHeight_AcrossLinesPagesAndZoom()
    {
        var (controller, vp) = SetupRail(readingLine: 0.35);
        vp.Rail.CurrentLine = 10;
        vp.StartSnap(W, H);
        Settle(controller, vp);
        Assert.Equal(0.35 * H, LineScreenY(vp), precision: 6);

        Assert.True(controller.StepRailLine(vp, forward: true));
        Settle(controller, vp);
        Assert.Equal(0.35 * H, LineScreenY(vp), precision: 6);

        controller.HandleZoomKey(zoomIn: true);
        Settle(controller, vp);
        Assert.True(vp.Rail.Active);
        Assert.Equal(0.35 * H, LineScreenY(vp), precision: 3);

        controller.GoToPage(1);
        Assert.Equal(1, vp.CurrentPage);
        controller.PumpAnalysis();
        vp.StartSnap(W, H);
        Settle(controller, vp);
        Assert.Equal(0, vp.Rail.VerticalBias);
        Assert.Equal(0.35 * H, LineScreenY(vp), precision: 6);
    }

    [Fact]
    public void ReadingLine_BiasIsRelativeToTheConfiguredLine()
    {
        var (_, vp) = SetupRail(readingLine: 0.35);
        vp.Rail.CurrentLine = 10;
        double z = vp.Camera.Zoom;
        double cameraY = 0.35 * H - vp.Rail.CurrentLineInfo.Y * z + 42;
        vp.Rail.CaptureVerticalBias(cameraY, z, H);
        Assert.Equal(42, vp.Rail.VerticalBias, precision: 9);

        var (_, targetY) = vp.Rail.ComputeSnapTarget(z, W, H);
        Assert.Equal(cameraY, targetY, precision: 9);
    }

    [Fact]
    public void ReadingLine_ConfigChange_ResnapsTheCurrentLine()
    {
        var (controller, vp) = SetupRail();
        vp.Rail.CurrentLine = 10;
        vp.StartSnap(W, H);
        Settle(controller, vp);
        Assert.Equal(H / 2.0, LineScreenY(vp), precision: 6);

        controller.OnConfigChanged(controller.Config with { RailReadingLineFraction = 0.3 });
        Settle(controller, vp);
        Assert.Equal(0.3 * H, LineScreenY(vp), precision: 6);
    }

    [Theory]
    [InlineData(0.05, 0.2)]
    [InlineData(0.95, 0.8)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    [InlineData(0.35, 0.35)]
    public void ReadingLine_FractionIsClamped(double configured, double effective)
    {
        Assert.Equal(effective, new CoreSettings { RailReadingLineFraction = configured }.EffectiveRailReadingLineFraction);
        Assert.Equal(effective * H, new RailNav(new CoreSettings { RailReadingLineFraction = configured }).ReadingLineScreenY(H));
    }

    [Fact]
    public void AppConfig_RoundTripsBothSettings()
    {
        var cfg = new AppConfig { RailReadingLineFraction = 0.4, VerticalOverscrollFraction = 0.25 };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg, RailReaderJsonContext.Default.AppConfig);
        Assert.Contains("\"rail_reading_line_fraction\"", json);
        Assert.Contains("\"vertical_overscroll_fraction\"", json);
        var core = System.Text.Json.JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AppConfig)!.ToCoreSettings();
        Assert.Equal(0.4, core.RailReadingLineFraction);
        Assert.Equal(0.25, core.VerticalOverscrollFraction);
    }
}
