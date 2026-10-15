using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Scroll speeds are tuned in page units/s, so the text's on-screen velocity used to be <c>speed × zoom</c>
/// (too fast when zoomed in, too slow when zoomed out). <see cref="CoreSettings.ScrollSpeedZoomFactor"/>
/// normalises hold-to-scroll, auto-scroll and the steered scroll to the screen velocity at a reference zoom.
/// </summary>
public class ScrollSpeedZoomTests
{
    // ── The factor ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(7.0, 1.0)]     // at the reference: unchanged
    [InlineData(14.0, 0.5)]    // twice the zoom: half the page speed → same screen speed
    [InlineData(3.5, 2.0)]
    [InlineData(3.0, 7.0 / 3.0)]
    public void Factor_KeepsScreenVelocityConstant(double zoom, double expected)
    {
        var s = new CoreSettings();
        Assert.Equal(expected, s.ScrollSpeedZoomFactor(zoom), 9);
        Assert.Equal(s.ScrollSpeedZoomReference, s.ScrollSpeedZoomFactor(zoom) * zoom, 9);   // factor × zoom = const
    }

    [Fact]
    public void Factor_IsClamped()
    {
        var s = new CoreSettings();
        Assert.Equal(CoreSettings.ScrollSpeedZoomFactorMax, s.ScrollSpeedZoomFactor(0.5));
        Assert.Equal(CoreSettings.ScrollSpeedZoomFactorMin, s.ScrollSpeedZoomFactor(200));
    }

    [Fact]
    public void Factor_ExponentTradesScreenSpeedForWordsPerSecond()
    {
        var half = new CoreSettings { ScrollSpeedZoomExponent = 0.5 };
        Assert.Equal(Math.Sqrt(0.5), half.ScrollSpeedZoomFactor(14.0), 9);
    }

    [Theory]
    [InlineData(0.0, 1.0)]       // reference disabled
    [InlineData(-3.0, 1.0)]
    public void Factor_DisabledByReference_IsOne(double reference, double expected)
        => Assert.Equal(expected, new CoreSettings { ScrollSpeedZoomReference = reference }.ScrollSpeedZoomFactor(14));

    [Fact]
    public void Factor_DisabledByZeroExponent_IsOne()
        => Assert.Equal(1.0, new CoreSettings { ScrollSpeedZoomExponent = 0 }.ScrollSpeedZoomFactor(14));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Factor_NonsenseZoom_IsOne(double zoom)
        => Assert.Equal(1.0, new CoreSettings().ScrollSpeedZoomFactor(zoom));

    [Fact]
    public void AppConfig_CarriesTheSettingsThrough()
    {
        var s = new AppConfig { ScrollSpeedZoomReference = 5, ScrollSpeedZoomExponent = 0.7 }.ToCoreSettings();
        Assert.Equal(5, s.ScrollSpeedZoomReference);
        Assert.Equal(0.7, s.ScrollSpeedZoomExponent);
        Assert.Equal(7.0, new AppConfig().ToCoreSettings().ScrollSpeedZoomReference);   // default: on, 7×
    }

    // ── Auto-scroll ───────────────────────────────────────────────────────────

    private sealed class NoOpClamp : ICameraClamp
    {
        public double ClampX(double cameraX, double zoom, double windowWidth) => cameraX;
    }

    private static double AutoScrollCameraAfter(double zoom, double factor)
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(28.0);
        double cameraX = 0;
        var ctx = new AutoScrollContext
        {
            SnapInProgress = false, LineRight = 1e9, LinePauseMs = 0, WindowWidth = 800, Zoom = zoom,
            MaxSpeed = 42, SpeedZoomFactor = factor,
        };
        for (int i = 0; i < 30; i++) sm.Tick(ref cameraX, 1.0 / 60.0, in ctx);
        return cameraX;
    }

    [Fact]
    public void AutoScroll_FactorScalesTheScreenStep()
    {
        var s = new CoreSettings();
        double at7 = AutoScrollCameraAfter(7, s.ScrollSpeedZoomFactor(7));
        double at14 = AutoScrollCameraAfter(14, s.ScrollSpeedZoomFactor(14));
        double at35 = AutoScrollCameraAfter(3.5, s.ScrollSpeedZoomFactor(3.5));
        Assert.True(at7 < 0);
        Assert.Equal(at7, at14, 9);
        Assert.Equal(at7, at35, 9);
        // Unnormalised, the same session moves twice as far at twice the zoom.
        Assert.Equal(2 * AutoScrollCameraAfter(7, 1.0), AutoScrollCameraAfter(14, 1.0), 9);
    }

    // ── Hold-to-scroll and the steer, through RailNav ─────────────────────────

    private const double WindowWidth = 800, WindowHeight = 600;
    private const double LineLeft = 72, LineRight = 540;

    private static PageAnalysis OneLine() => new()
    {
        Blocks =
        [
            new LayoutBlock
            {
                BBox = new BBox((float)LineLeft, 72, (float)(LineRight - LineLeft), 16), Role = BlockRole.Text,
                Confidence = 0.9f, Order = 0,
                Lines = [new LineInfo(72, 16, (float)LineLeft, (float)(LineRight - LineLeft))],
            },
        ],
        PageWidth = 612, PageHeight = 792,
    };

    private static (RailNav nav, double cx, double cy) Seated(double zoom)
    {
        var cfg = new AppConfig { SnapDurationMs = 1, PixelSnapping = false }.ToCoreSettings();
        var nav = new RailNav(cfg);
        nav.SetAnalysis(OneLine(), new HashSet<BlockRole> { BlockRole.Text });
        nav.Active = true;
        double cx = 0, cy = 0;
        nav.StartSnapToCurrent(cx, cy, zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        nav.Tick(ref cx, ref cy, 0.016, zoom, WindowWidth);
        return (nav, cx, cy);
    }

    /// <summary>Screen px the reading point travels in half a second at full throttle.</summary>
    private static double SteerScreenTravel(double zoom)
    {
        var (nav, cx, cy) = Seated(zoom);
        nav.SetSteerThrottle(1.0);
        nav.Tick(ref cx, ref cy, 1.0 / 60, zoom, WindowWidth);
        double start = nav.SteerReadingPointPageX!.Value;
        for (int i = 0; i < 30; i++) nav.Tick(ref cx, ref cy, 1.0 / 60, zoom, WindowWidth);
        return (nav.SteerReadingPointPageX!.Value - start) * zoom;
    }

    [Fact]
    public void Steer_ScreenTravelIsTheSameAtEveryZoom()
    {
        double at7 = SteerScreenTravel(7);
        Assert.True(at7 > 10);
        Assert.Equal(at7, SteerScreenTravel(14), 6);
        Assert.Equal(at7, SteerScreenTravel(3.5), 6);
    }

    [Fact]
    public void Steer_NormalisedSpeedStaysTheFractionOfTheRange()
    {
        // ScrollSpeed (0…1) feeds the motion blur and the deferred-DPI gate: it must not pick up the zoom factor.
        var (nav, cx, cy) = Seated(14);
        nav.SetSteerThrottle(1.0);
        nav.Tick(ref cx, ref cy, 1.0 / 60, 14, WindowWidth);
        Assert.Equal(1.0, nav.ScrollSpeed, 6);
    }

    /// <summary>Camera travel (px) of a forward hold of ~120 ms. Real-time stopwatch inside: compare loosely.</summary>
    private static double HoldTravel(double zoom)
    {
        var (nav, cx, cy) = Seated(zoom);
        double startX = cx;
        nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(120);
        nav.Tick(ref cx, ref cy, 0.016, zoom, WindowWidth);
        return Math.Abs(cx - startX);
    }

    [Fact]
    public void HoldToScroll_ScreenTravelIsTheSameAtEveryZoom()
    {
        double at7 = HoldTravel(7);
        Assert.True(at7 > 1);
        Assert.InRange(HoldTravel(14) / at7, 0.9, 1.1);
        Assert.InRange(HoldTravel(3.5) / at7, 0.9, 1.1);
    }
}
