using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Phase 3 coverage: continuous rail (docs/continuous-scroll-plan.md §5 Phase 3) — rail page
/// advance re-anchors without moving the screen instead of cutting, in both directions, and the
/// deferred (analysis-not-cached) path still defers correctly.
/// </summary>
public class ContinuousScrollRailTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;
    private readonly AppConfig _appConfig;

    public ContinuousScrollRailTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath(); // 3-page synthetic PDF
        _appConfig = new AppConfig();
        var config = _appConfig.ToCoreSettings() with { ContinuousScroll = true };
        _controller = new DocumentController(config, _appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    private DocumentModel SetupDoc()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        return doc;
    }

    private static PageAnalysis TwoLineAnalysis()
    {
        var block = new LayoutBlock
        {
            Role = BlockRole.Text, BBox = new BBox(72, 72, 468, 40),
            Confidence = 0.9f, Order = 0,
        };
        block.Lines.Add(new LineInfo(72, 16, 72, 468));
        block.Lines.Add(new LineInfo(92, 16, 72, 468));
        var analysis = new PageAnalysis();
        analysis.Blocks.Add(block);
        return analysis;
    }

    /// <summary>Seats rail on page 0 with cached analysis for both pages 0 and 1, so the forward
    /// skip lands synchronously (cache hit) rather than deferring.</summary>
    private Viewport SetupCrossPageRail(DocumentModel doc)
    {
        var vp = doc.Primary;
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, TwoLineAnalysis());
        doc.SetAnalysis(1, doc.DefaultAnalysisParams, TwoLineAnalysis());
        doc.SetAnalysis(2, doc.DefaultAnalysisParams, TwoLineAnalysis());
        doc.ReapplyNavigableRoles(vp, _controller.Config.NavigableRoles);
        vp.Camera.Zoom = _controller.Config.RailZoomThreshold + 1;
        vp.ClampCamera(vp.Width, vp.Height); // the stale fit-zoom offset is invalid at rail zoom
        vp.UpdateRailZoom(vp.Width, vp.Height);
        _controller.FocusedViewport = vp;
        return vp;
    }

    [Fact]
    public void RailAdvance_ForwardAcrossPageBoundary_ReanchorsWithoutCuttingThenSnaps()
    {
        var doc = SetupDoc();
        var vp = SetupCrossPageRail(doc);
        Assert.True(vp.Rail.Active);

        // Capture page 0's on-screen offset (the line the reader is leaving) before crossing.
        var beforePage0Offset = vp.PageOffset(0);

        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 1; // last line of the 2-line block on page 0

        _controller.HandleArrowDown(); // advances past the last line → page boundary

        Assert.Equal(1, vp.CurrentPage);
        // PreserveScreen: page 0's own on-screen position is unchanged at the instant of the
        // transition (the reader hasn't scrolled — only the anchor changed under them).
        var afterPage0Offset = vp.PageOffset(0);
        Assert.Equal(beforePage0Offset.OffsetX, afterPage0Offset.OffsetX, precision: 3);
        Assert.Equal(beforePage0Offset.OffsetY, afterPage0Offset.OffsetY, precision: 3);

        // A snap to page 1's first line is in flight.
        Assert.True(vp.Rail.SnapProgress < 1.0);
        var (targetX, targetY) = vp.Rail.ComputeSnapTarget(vp.Camera.Zoom, vp.Width, vp.Height);

        // Run ticks until the snap settles; the camera lands at the computed target.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp.Rail.SnapProgress < 1.0 && sw.ElapsedMilliseconds < 3000)
            _controller.TickViewport(vp, 0.016);

        Assert.Equal(targetX, vp.Camera.OffsetX, 1.0);
        Assert.Equal(targetY, vp.Camera.OffsetY, 1.0);
        Assert.Equal(0, vp.Rail.CurrentBlock);
        Assert.Equal(0, vp.Rail.CurrentLine);
    }

    [Fact]
    public void RailAdvance_BackwardAcrossPageBoundary_ReanchorsAndLandsAtPageEnd()
    {
        var doc = SetupDoc();
        var vp = SetupCrossPageRail(doc);

        // Start on page 1, at its first line.
        doc.GoToPage(vp, 1, null, _controller.Config.NavigableRoles, vp.Width, vp.Height);
        doc.ReapplyNavigableRoles(vp, _controller.Config.NavigableRoles);
        vp.UpdateRailZoom(vp.Width, vp.Height);
        Assert.True(vp.Rail.Active);
        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 0;

        _controller.HandleArrowUp(); // at block 0 / line 0 → page boundary backward

        Assert.Equal(0, vp.CurrentPage);
        Assert.True(vp.Rail.SnapProgress < 1.0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp.Rail.SnapProgress < 1.0 && sw.ElapsedMilliseconds < 3000)
            _controller.TickViewport(vp, 0.016);

        // Backward skip lands at the END of the landed page (last block/line).
        Assert.Equal(0, vp.Rail.CurrentBlock);
        Assert.Equal(1, vp.Rail.CurrentLine); // the 2-line block's last line
    }

    [Fact]
    public void RailAdvance_Deferred_AnalysisNotCached_CameraWaitsThenSnapsOnLanding()
    {
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(TwoLineAnalysis));
        var doc = SetupDoc();
        var vp = doc.Primary;
        _controller.FocusedViewport = vp;

        // Seat rail on page 0 only — page 1 is NOT pre-analysed, forcing the deferred path.
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, TwoLineAnalysis());
        doc.ReapplyNavigableRoles(vp, _controller.Config.NavigableRoles);
        vp.Camera.Zoom = _controller.Config.RailZoomThreshold + 1;
        vp.UpdateRailZoom(vp.Width, vp.Height);
        Assert.True(vp.Rail.Active);

        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 1;

        _controller.HandleArrowDown();

        // Page 1 was never analysed → the skip defers; the camera waits at the boundary rather
        // than jumping ahead of the (not yet known) landing.
        Assert.Equal(1, vp.CurrentPage);
        Assert.True(vp.PendingRailSetup);
        Assert.NotNull(vp.PendingSkip);
        Assert.False(vp.Rail.Active);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp.PendingRailSetup && sw.ElapsedMilliseconds < 5000)
        {
            _controller.PollAnalysisResults();
            Thread.Sleep(10);
        }

        Assert.False(vp.PendingRailSetup);
        Assert.True(vp.Rail.Active); // deferred landing engaged the rail on the new page
        Assert.Null(vp.PendingSkip);
        Assert.Equal(0, vp.Rail.CurrentBlock);
        Assert.Equal(0, vp.Rail.CurrentLine); // forward skip lands at the TOP of the page
    }

    [Fact]
    public void AutoScroll_PageAdvance_ReanchorsWithContinuity_ThenParks()
    {
        var doc = SetupDoc();
        var vp = SetupCrossPageRail(doc);

        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);

        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 1;
        // Force the auto-scroll elapsed clock far enough forward that the next tick advances
        // past the last line of page 0's block.
        vp.Rail.AutoScrollElapsedSecondsOverride = () => 999.0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp.CurrentPage == 0 && sw.ElapsedMilliseconds < 3000)
            _controller.TickViewport(vp, 0.016);

        Assert.Equal(1, vp.CurrentPage);
        // Park is requested on entry but takes effect once the skip-landing snap settles — keep
        // ticking (the snap crosses the page-gap distance under continuous camera continuity).
        while (!vp.Rail.AutoScrollParked && sw.ElapsedMilliseconds < 3000)
            _controller.TickViewport(vp, 0.016);

        // Auto-scroll parks on the page boundary (v1 behaviour is unchanged by continuous mode —
        // only the camera continuity of the transition changes, per §2/§5 Phase 3).
        Assert.True(vp.Rail.AutoScrollParked);
    }
}
