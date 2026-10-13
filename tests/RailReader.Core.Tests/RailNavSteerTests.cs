using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Steered scroll (railreader2 docs/reading-guide-design.md §11): the reading guide's stick carries a
/// virtual reading point along the line at the steered speed and the camera eases to keep it centred
/// where the line's framing allows. One 3-line text block, x 72…540 pt, at 4× (1872 px) in an 800 px
/// window: the block's start snaps to 5 % (40 px), the reading point is framed at the centre (400 px),
/// so a line has a 90 pt "head" the eye reads with the camera still, a scrolled middle, and a tail read
/// with the camera pinned at the line's end.
/// </summary>
public class RailNavSteerTests
{
    private const double WindowWidth = 800, WindowHeight = 600, Zoom = 4.0;
    private const double LineLeft = 72, LineRight = 540, LineY = 72, LineH = 16;
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
        float y = (float)LineY;
        for (int b = 0; b < blocks; b++)
        {
            var li = new List<LineInfo>();
            for (int l = 0; l < lines; l++) li.Add(new LineInfo(y + l * (float)LineH, (float)LineH, (float)LineLeft, (float)(LineRight - LineLeft)));
            list.Add(new LayoutBlock { BBox = new BBox((float)LineLeft, y, (float)(LineRight - LineLeft), lines * (float)LineH), Role = role, Confidence = 0.9f, Order = b, Lines = li });
            y += lines * (float)LineH + 20;
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

    private void Run(ref double cx, ref double cy, double secs)
    {
        for (double t = 0; t < secs; t += 1.0 / 60) Tick(ref cx, ref cy);
    }

    /// <summary>Screen x of the reading point for the given camera.</summary>
    private double ReadScreenX(double cx) => _nav.SteerReadingPointPageX!.Value * Zoom + cx;

    [Fact]
    public void SteerSpeed_IsHoldToScrollsRange_WithQuadraticRamp()
    {
        Assert.Equal(30, RailNav.SteerSpeed(0, 30, 130));
        Assert.Equal(130, RailNav.SteerSpeed(1, 30, 130));
        Assert.Equal(30 + 100 * 0.25, RailNav.SteerSpeed(0.5, 30, 130));
        Assert.Equal(130, RailNav.SteerSpeed(7, 30, 130));   // clamped
    }

    [Fact]
    public void FreshLine_ReadsTheHeadWithTheCameraStill_ThenKeepsTheWordCentred()
    {
        var (cx, cy) = Seat();
        double start = cx;
        _nav.SetSteerThrottle(1);
        Assert.True(_nav.Steering);

        // Head: the reading point starts on the first word and advances at top speed; the camera stays put.
        Assert.True(Tick(ref cx, ref cy, 0.5));
        Assert.Equal(start, cx);
        Assert.Equal(LineLeft + _config.ScrollSpeedMax * 0.5, _nav.SteerReadingPointPageX!.Value, 3);
        Assert.Equal(1.0, _nav.ScrollSpeed, 6);

        // Middle: once the point passes the centre the camera follows; after the ease settles the point
        // is held near the centre (a small constant lag from the exponential follower).
        Run(ref cx, ref cy, 4.0);
        Assert.True(cx < start - 100);
        Assert.InRange(ReadScreenX(cx), WindowWidth / 2 - 5, WindowWidth / 2 + 40);
        double before = cx;
        Run(ref cx, ref cy, 0.5);
        double expected = before - _config.ScrollSpeedMax * 0.5 * Zoom;      // steady state: camera speed ≈ steer speed
        Assert.InRange(cx, expected - 5, expected + 5);
    }

    [Fact]
    public void BackwardFromMidLine_ReversesTheCamera()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Run(ref cx, ref cy, 4.0);
        double mid = cx;
        _nav.SetSteerThrottle(-1);
        Run(ref cx, ref cy, 1.0);
        Assert.True(cx > mid + 50);
        Assert.Equal(1.0, _nav.ScrollSpeed, 6);
        _nav.SetSteerThrottle(-1e-3);
        Tick(ref cx, ref cy, 0.1);
        Assert.Equal(_config.ScrollSpeedStart / _config.ScrollSpeedMax, _nav.ScrollSpeed, 3);
    }

    [Fact]
    public void ZeroThrottle_StopsWhereItIs_AndKeepsTheReadingPoint()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Run(ref cx, ref cy, 3.0);
        double held = cx;
        double read = _nav.SteerReadingPointPageX!.Value;
        _nav.SetSteerThrottle(0);
        Assert.False(_nav.Steering);
        Assert.False(Tick(ref cx, ref cy, 0.1));
        Assert.Equal(held, cx);
        Assert.Equal(0, _nav.ScrollSpeed);
        Assert.Equal(read, _nav.SteerReadingPointPageX);   // a paused reader's marker stays put
    }

    [Fact]
    public void TheTail_IsReadWithTheCameraPinnedAtTheLineEnd_ThenTheEdgeAdvanceFires()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        double lineMinX = WindowWidth - (LineRight + (LineRight - LineLeft) * 0.05) * Zoom;   // line end (+5 % margin) at the right edge
        bool sawPinnedTail = false;
        for (int i = 0; i < 60 * 20; i++)
        {
            Tick(ref cx, ref cy);
            Assert.True(cx >= lineMinX - 0.01, "camera scrolled past the line end");
            if (Math.Abs(cx - lineMinX) < 1 && _nav.SteerReadingPointPageX < LineRight - 5) sawPinnedTail = true;
            if (_nav.ConsumePendingEdgeAdvance() is { } d)
            {
                Assert.Equal(ScrollDirection.Forward, d);
                Assert.True(sawPinnedTail, "the tail should be read with the camera pinned before the advance");
                Assert.Equal(LineRight, _nav.SteerReadingPointPageX!.Value, 6);
                return;
            }
        }
        Assert.Fail("no edge advance fired while steering to the line end");
    }

    [Fact]
    public void NewLineUnderTheStick_RestartsTheReadingPointAtItsNearEnd()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Run(ref cx, ref cy, 3.0);
        Assert.True(_nav.SteerReadingPointPageX > LineLeft + 100);
        _nav.NextLine();                                        // what the controller does on the advance
        Tick(ref cx, ref cy, 0.01);
        Assert.Equal(LineLeft + _config.ScrollSpeedMax * 0.01, _nav.SteerReadingPointPageX!.Value, 3);

        _nav.SetSteerThrottle(-1);
        _nav.PrevLine();
        Tick(ref cx, ref cy, 0.01);
        Assert.Equal(LineRight - _config.ScrollSpeedMax * 0.01, _nav.SteerReadingPointPageX!.Value, 3);
    }

    [Fact]
    public void SeatSteerReadingPoint_AppliesOnTheNextTick_AndTheCameraCentresIt()
    {
        var (cx, cy) = Seat();
        _nav.SeatSteerReadingPoint(300);           // "clicked" a word mid-line, before engaging
        _nav.SetSteerThrottle(1e-3);
        Tick(ref cx, ref cy, 0.01);
        Assert.Equal(300 + _config.ScrollSpeedStart * 0.01, _nav.SteerReadingPointPageX!.Value, 3);
        Run(ref cx, ref cy, 1.5);                  // the ease brings the word to the centre
        Assert.InRange(ReadScreenX(cx), WindowWidth / 2 - 5, WindowWidth / 2 + 20);
    }

    [Fact]
    public void TrySeatAt_OnlyOnTheCurrentLine()
    {
        var (cx, cy) = Seat();
        Assert.False(_nav.TrySeatSteerReadingPointAt(300, LineY + 3 * LineH));   // another line: continue from the rail
        Assert.True(_nav.TrySeatSteerReadingPointAt(300, LineY + LineH / 2));
        _nav.SetSteerThrottle(1);
        Tick(ref cx, ref cy, 0.01);
        Assert.Equal(300 + _config.ScrollSpeedMax * 0.01, _nav.SteerReadingPointPageX!.Value, 3);
    }

    [Fact]
    public void Parked_IgnoresThrottle_UntilItReturnsToZeroAndIsReapplied()
    {
        var (cx, cy) = Seat();
        _nav.SetSteerThrottle(1);
        Run(ref cx, ref cy, 3.0);
        _nav.ParkSteer();
        Assert.True(_nav.SteerParked);
        double parked = cx;
        _nav.SetSteerThrottle(1);                 // still deflected
        Assert.False(Tick(ref cx, ref cy, 0.2));
        Assert.Equal(parked, cx);
        Assert.Equal(0, _nav.ScrollSpeed);

        _nav.SetSteerThrottle(0);                 // back to the dead zone …
        Assert.False(_nav.SteerParked);
        _nav.SetSteerThrottle(1);                 // … and re-push: the flow restarts at the (new) unit's start
        Assert.True(Tick(ref cx, ref cy, 0.1));
        Assert.Equal(LineLeft + _config.ScrollSpeedMax * 0.1, _nav.SteerReadingPointPageX!.Value, 3);
    }

    [Fact]
    public void Steer_WaitsForARunningSnap()
    {
        var slow = new RailNav(new AppConfig { SnapDurationMs = 500, PixelSnapping = false }.ToCoreSettings());
        slow.SetAnalysis(Analysis(3), new HashSet<BlockRole> { BlockRole.Text });
        slow.Active = true;
        double cx = 0, cy = 0;
        slow.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(600);
        slow.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        slow.SetSteerThrottle(1);
        slow.Tick(ref cx, ref cy, 0.1, Zoom, WindowWidth);
        double read = slow.SteerReadingPointPageX!.Value;

        slow.NextLine();
        slow.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);   // 500 ms snap in flight
        slow.Tick(ref cx, ref cy, 0.1, Zoom, WindowWidth);
        Assert.Equal(LineLeft, slow.SteerReadingPointPageX!.Value);          // new line noted, nothing read yet
        Assert.Equal(0, slow.ScrollSpeed);
        Thread.Sleep(600);
        slow.Tick(ref cx, ref cy, 0.1, Zoom, WindowWidth);                   // snap landed; now it reads
        Assert.Equal(LineLeft + 42.0 * 0.1, slow.SteerReadingPointPageX!.Value, 3);
        Assert.True(read > LineLeft);
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
        Tick(ref cx, ref cy, 0.1);
        _nav.ParkSteer();
        _nav.Deactivate();
        Assert.False(_nav.Steering);
        Assert.False(_nav.SteerParked);
        Assert.Null(_nav.SteerReadingPointPageX);
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
    public void EngagedMidLine_StartsFromTheWordAtTheCentre()
    {
        var (cx, cy) = Seat();
        cx -= 600;                                 // the reader had scrolled along the line by hand
        _nav.SetSteerThrottle(1);
        Tick(ref cx, ref cy, 0.01);
        Assert.Equal((WindowWidth / 2 - (cx + 0)) / Zoom, _nav.SteerReadingPointPageX!.Value, 0);
        Run(ref cx, ref cy, 1.0);
        Assert.True(cx < -600 - 100);              // and the camera follows
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
