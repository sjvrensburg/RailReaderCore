using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// The controller side of steering the rail (railreader2 docs/reading-guide-design.md §11):
/// <see cref="DocumentController.SteerRail"/> / <see cref="DocumentController.StepRailLine"/> and the
/// park-on-stop-unit decision in the edge-advance path. Page 0: a 2-line text block followed by a display
/// equation (navigable, and a stop role — figures are not navigable by default).
/// </summary>
public class RailSteerControllerTests : IDisposable
{
    private readonly DocumentController _controller;
    private readonly AppConfig _appConfig = new();
    private double _nowMs;

    public RailSteerControllerTests()
    {
        _controller = new DocumentController(_appConfig.ToCoreSettings(), _appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    private static PageAnalysis TextThenEquation()
    {
        var text = new LayoutBlock { Role = BlockRole.Text, BBox = new BBox(72, 72, 468, 40), Confidence = 0.9f, Order = 0 };
        text.Lines.Add(new LineInfo(72, 16, 72, 468));
        text.Lines.Add(new LineInfo(92, 16, 72, 468));
        var equation = new LayoutBlock { Role = BlockRole.DisplayMath, BBox = new BBox(72, 140, 468, 200), Confidence = 0.9f, Order = 1 };
        equation.Lines.Add(new LineInfo(140, 200, 72, 468));
        var analysis = new PageAnalysis();
        analysis.Blocks.Add(text);
        analysis.Blocks.Add(equation);
        return analysis;
    }

    private Viewport Setup()
    {
        var doc = _controller.CreateDocument(TestFixtures.GetTestPdfPath());
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        var vp = doc.Primary;
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, TextThenEquation());
        doc.ReapplyNavigableRoles(vp, _controller.Config.NavigableRoles);
        vp.Camera.Zoom = _controller.Config.RailZoomThreshold + 1;
        vp.ClampCamera(vp.Width, vp.Height);
        vp.UpdateRailZoom(vp.Width, vp.Height);
        _controller.FocusedViewport = vp;
        vp.Rail.EdgeHoldNowMsOverride = () => _nowMs;
        Assert.True(vp.Rail.Active);
        return vp;
    }

    private void Settle(Viewport vp)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp.Rail.SnapProgress < 1.0 && sw.ElapsedMilliseconds < 3000)
            Tick(vp);
    }

    private void Tick(Viewport vp)
    {
        _nowMs += 16;
        _controller.TickViewport(vp, 0.016);
    }

    [Fact]
    public void StepRailLine_AdvancesAndSnaps_AndAnnounces()
    {
        var vp = Setup();
        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 0;
        int announced = 0;
        vp.ReadingPositionChanged += _ => announced++;

        Assert.True(_controller.StepRailLine(vp, forward: true));
        Assert.Equal((0, 1), (vp.Rail.CurrentBlock, vp.Rail.CurrentLine));
        Assert.True(vp.Rail.SnapProgress < 1.0);
        Assert.Equal(1, announced);

        Settle(vp);
        Assert.True(_controller.StepRailLine(vp, forward: false));
        Assert.Equal((0, 0), (vp.Rail.CurrentBlock, vp.Rail.CurrentLine));
    }

    [Fact]
    public void SteerRail_StopsAutoScroll_OnEngage_AndReleasesOnZero()
    {
        var vp = Setup();
        _controller.ToggleAutoScroll();
        Assert.True(_controller.AutoScrollActive);

        _controller.SteerRail(vp, 0.7);
        Assert.False(_controller.AutoScrollActive);
        Assert.True(vp.Rail.Steering);

        _controller.SteerRail(vp, 0);
        Assert.False(vp.Rail.Steering);
    }

    [Fact]
    public void SteeredFlow_IntoAStopBlock_ParksTheStick_UntilRepushed()
    {
        var vp = Setup();
        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 1;                       // last text line; the next unit is the equation
        vp.StartSnap(vp.Width, vp.Height);
        Settle(vp);

        _controller.SteerRail(vp, 1);
        for (int i = 0; i < 4000 && vp.Rail.CurrentBlock == 0; i++)
            Tick(vp);
        Assert.Equal(1, vp.Rail.CurrentBlock);         // steered off the line end into the equation
        Assert.True(vp.Rail.SteerParked);

        Settle(vp);
        double parkedX = vp.Camera.OffsetX;
        for (int i = 0; i < 30; i++) Tick(vp);         // still deflected: nothing moves
        Assert.Equal(parkedX, vp.Camera.OffsetX);

        _controller.SteerRail(vp, 0);                   // dead zone …
        _controller.SteerRail(vp, 1);                   // … re-push
        Assert.False(vp.Rail.SteerParked);
        for (int i = 0; i < 10; i++) Tick(vp);
        Assert.NotEqual(parkedX, vp.Camera.OffsetX);
    }

    [Fact]
    public void SteeredFlow_WithinProse_DoesNotPark()
    {
        var vp = Setup();
        vp.Rail.CurrentBlock = 0;
        vp.Rail.CurrentLine = 0;
        vp.StartSnap(vp.Width, vp.Height);
        Settle(vp);

        _controller.SteerRail(vp, 1);
        for (int i = 0; i < 4000 && vp.Rail.CurrentLine == 0; i++)
            Tick(vp);
        Assert.Equal((0, 1), (vp.Rail.CurrentBlock, vp.Rail.CurrentLine));
        Assert.False(vp.Rail.SteerParked);
    }
}
