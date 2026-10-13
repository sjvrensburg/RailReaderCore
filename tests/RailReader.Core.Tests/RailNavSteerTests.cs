using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Steered scroll (railreader2 docs/reading-guide-design.md §11): the reading guide's stick drives the
/// rail's camera along the line. One 3-line text block, 468 pt wide at 4× (1872 px) in an 800 px window,
/// so there is room to scroll along every line.
/// </summary>
public class RailNavSteerTests
{
    private const double WindowWidth = 800, WindowHeight = 600, Zoom = 4.0;
    private readonly AppConfig _config = new() { SnapDurationMs = 1, PixelSnapping = false };
    private readonly RailNav _nav;
    private double _nowMs;

    public RailNavSteerTests()
    {
        _nav = new RailNav(_config.ToCoreSettings());
        _nav.EdgeHoldNowMsOverride = () => _nowMs;
    }

    private static PageAnalysis Analysis(int lines, BlockRole role = BlockRole.Text, int blocks = 1)
    {
        var list = new List<LayoutBlock>();
        float y = 72;
        for (int b = 0; b < blocks; b++)
        {
            var li = new List<LineInfo>();
            for (int l = 0; l < lines; l++) li.Add(new LineInfo(y + l * 16, 16, 72, 468));
            list.Add(new LayoutBlock { BBox = new BBox(72, y, 468, lines * 16), Role = role, Confidence = 0.9f, Order = b, Lines = li });
            y += lines * 16 + 20;
        }
        return new PageAnalysis { Blocks = list, PageWidth = 612, PageHeight = 792 };
    }

    /// <summary>Seats the rail on line 0 and runs the snap to its start; returns the camera.</summary>
    private (double cx, double cy) Seat(int lines = 3)
    {
        _nav.SetAnalysis(Analysis(lines), new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;
        double cx = 0, cy = 0;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        return (cx, cy);
    }

    private bool Tick(ref double cx, ref double cy, double dt = 1.0 / 60)
    {
        _nowMs += dt * 1000;
        return _nav.Tick(ref cx, ref cy, dt, Zoom, WindowWidth);
    }

    [Fact]
    public void SteerSpeed_IsHoldToScrollsRange_WithQuadraticRamp()
    {
        Assert.Equal(30, RailNav.SteerSpeed(0, 30, 130));
        Assert.Equal(130, RailNav.SteerSpeed(1, 30, 130));
        Assert.Equal(30 + 100 * 0.25, RailNav.SteerSpeed(0.5, 30, 130));
        Assert.Equal(130, RailNav.SteerSpeed(7, 30, 130));   // clamped
    }

    [Fact]
    public void FullForwardThrottle_MovesCameraAtTopSpeed_AndReportsScrollSpeed()
    {
        var (cx, cy) = Seat();
        double start = cx;
        _nav.SetSteerThrottle(1);
        Assert.True(_nav.Steering);
        bool animating = Tick(ref cx, ref cy, 0.1);
        Assert.True(animating);
        // Forward = content moves left = camera X decreases by speed × dt × zoom (page px → screen px).
        Assert.Equal(start - _config.ScrollSpeedMax * 0.1 * Zoom, cx, 3);
        Assert.Equal(1.0, _nav.ScrollSpeed, 6);
    }

    [Fact]
    public void SmallThrottle_MovesAtStartSpeed_AndBackwardReverses()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Tick(ref cx, ref cy, 0.2);              // scroll in a bit so there is room to go back
        double mid = cx;
        _nav.SetSteerThrottle(-1e-3);
        Tick(ref cx, ref cy, 0.1);
        Assert.Equal(mid + _config.ScrollSpeedStart * 0.1 * Zoom, cx, 1);
        Assert.Equal(_config.ScrollSpeedStart / _config.ScrollSpeedMax, _nav.ScrollSpeed, 3);
    }

    [Fact]
    public void ZeroThrottle_StopsWhereItIs()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Tick(ref cx, ref cy, 0.1);
        double held = cx;
        _nav.SetSteerThrottle(0);
        Assert.False(_nav.Steering);
        Assert.False(Tick(ref cx, ref cy, 0.1));
        Assert.Equal(held, cx);
        Assert.Equal(0, _nav.ScrollSpeed);
    }

    [Fact]
    public void HeldAtLineEnd_FiresEdgeAdvance_AfterTheHoldThreshold()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        // Drive to the line end (1872 px of content in 800 px: ~1072 px at 130 px/s × 4 ≈ 2.1 s).
        for (int i = 0; i < 60 * 12; i++)
        {
            Tick(ref cx, ref cy);
            if (_nav.ConsumePendingEdgeAdvance() is { } d)
            {
                Assert.Equal(ScrollDirection.Forward, d);
                Assert.True(_nav.IsAtHardEdge(cx, Zoom, WindowWidth, ScrollDirection.Forward));
                return;
            }
        }
        Assert.Fail("no edge advance fired while steering against the line end");
    }

    [Fact]
    public void Parked_IgnoresThrottle_UntilItReturnsToZeroAndIsReapplied()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Tick(ref cx, ref cy, 0.05);
        _nav.ParkSteer();
        Assert.True(_nav.SteerParked);
        double parked = cx;
        _nav.SetSteerThrottle(1);                 // still deflected
        Assert.False(Tick(ref cx, ref cy, 0.2));
        Assert.Equal(parked, cx);
        Assert.Equal(0, _nav.ScrollSpeed);

        _nav.SetSteerThrottle(0);                 // back to the dead zone …
        Assert.False(_nav.SteerParked);
        _nav.SetSteerThrottle(1);                 // … and re-push
        Assert.True(Tick(ref cx, ref cy, 0.1));
        Assert.True(cx < parked);
    }

    [Fact]
    public void Steer_WaitsForARunningSnap()
    {
        var (cx, cy) = Seat();
        _nav.NextLine();
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);   // snap in flight (1 ms)
        _nav.SetSteerThrottle(1);
        double before = cx;
        _nav.Tick(ref cx, ref cy, 0.0, Zoom, WindowWidth);                   // dt 0: snap may still be running
        Thread.Sleep(5);
        Tick(ref cx, ref cy, 0.1);                                           // snap lands this tick; steer holds
        double landed = cx;
        Tick(ref cx, ref cy, 0.1);                                           // now it moves
        Assert.True(cx < landed, $"expected steer to move after the snap landed ({before} → {landed} → {cx})");
    }

    [Fact]
    public void ArrowScroll_AutoScroll_AndDeactivate_ClearTheStick()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        _nav.StartScroll(ScrollDirection.Forward, cx);
        Assert.False(_nav.Steering);

        _nav.StopScrollAndEdgeHold();
        _nav.SetSteerThrottle(1);
        _nav.StartAutoScroll(60);
        Assert.False(_nav.Steering);
        _nav.StopAutoScroll();

        _nav.SetSteerThrottle(1);
        _nav.ParkSteer();
        _nav.Deactivate();
        Assert.False(_nav.Steering);
        Assert.False(_nav.SteerParked);
    }

    [Fact]
    public void EngagingTheStick_StopsAHeldArrowScroll()
    {
        var (cx, cy) = Seat();
        _nav.StartScroll(ScrollDirection.Forward, cx);
        Tick(ref cx, ref cy, 0.05);
        _nav.SetSteerThrottle(0.5);
        double at = cx;
        _nav.SetSteerThrottle(0);
        Assert.False(Tick(ref cx, ref cy, 0.1));   // nothing (neither hold nor stick) moves the camera
        Assert.Equal(at, cx);
    }

    [Fact]
    public void InactiveRail_IgnoresThrottle()
    {
        _nav.SetSteerThrottle(1);
        Assert.False(_nav.Steering);
        double cx = 0, cy = 0;
        Assert.False(_nav.Tick(ref cx, ref cy, 0.1, Zoom, WindowWidth));
    }
}
