using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AutoScrollStateMachineTests
{
    private sealed class NoOpClamp : ICameraClamp
    {
        public double ClampX(double cameraX, double zoom, double windowWidth) => cameraX;
    }

    private static AutoScrollContext MakeContext(
        bool snapInProgress = false,
        double lineRight = 1000,
        double linePauseMs = 200,
        double windowWidth = 600,
        double zoom = 1.0,
        double maxSpeed = 5.0)
    {
        return new AutoScrollContext
        {
            SnapInProgress = snapInProgress,
            LineRight = lineRight,
            LinePauseMs = linePauseMs,
            WindowWidth = windowWidth,
            Zoom = zoom,
            MaxSpeed = maxSpeed,
        };
    }

    [Fact]
    public void Start_FromInactive_Activates()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        Assert.False(sm.IsActive);

        sm.Start(1.0);
        Assert.True(sm.IsActive);
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    [Fact]
    public void Stop_WhenActive_Deactivates()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        Assert.True(sm.IsActive);

        sm.Stop();
        Assert.False(sm.IsActive);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void Stop_WhenInactive_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Stop();
        Assert.False(sm.IsActive);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void Tick_WhenInactive_ReturnsFalse()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        double cameraX = 0;
        var ctx = MakeContext();

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
    }

    [Fact]
    public void Tick_WhenScrolling_MovesCameraLeft()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        // Inject a fixed elapsed-time source. Without it the first Tick starts a real
        // Stopwatch and reads it immediately on the same line; the elapsed can round to
        // 0, leaving cameraX at 0 and failing the "< 0" assertion intermittently (the
        // timing-dependent flake that also runs in the tag-publish job).
        sm.GetScrollElapsedSeconds = () => 1.0;
        sm.Start(1.0);
        double cameraX = 0;
        // lineRight far away so we don't reach the end
        var ctx = MakeContext(lineRight: 10000);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
        Assert.True(cameraX < 0, "Camera should move left (negative direction)");
    }

    // ===== Intra-flow per-line beat (held on EVERY line end, width-independent) =====

    [Fact]
    public void TickScrolling_ReachesLineEnd_HoldsFlatBeat()
    {
        // On reaching the line's right extent, a configured beat is held (brief Paused) before
        // advancing — on every line, regardless of width, so each carriage-return is preceded
        // by a rest rather than flipping straight to the next line.
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        var ctx = MakeContext(lineRight: 100, linePauseMs: 200);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result); // holding the beat
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
        Assert.Equal(0.0, sm.NormalizedSpeed);
    }

    [Fact]
    public void TickScrolling_ReachesLineEnd_NoPauseConfigured_AdvancesImmediately()
    {
        // With LinePauseMs = 0 the line advances at once on reaching its end (no beat).
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        var ctx = MakeContext(lineRight: 100, linePauseMs: 0);

        Assert.True(sm.Tick(ref cameraX, 0.016, in ctx));
    }

    [Fact]
    public void Paused_ResumesAndAdvancesAfterBeatElapses()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        // Tiny beat so the test stays fast.
        var ctx = MakeContext(lineRight: 100, linePauseMs: 1);

        Assert.False(sm.Tick(ref cameraX, 0.016, in ctx)); // enters beat pause
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
        Thread.Sleep(5);
        Assert.True(sm.Tick(ref cameraX, 0.016, in ctx)); // beat done → advance, back to Scrolling
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    // ===== Deferred pause (resume flow after a snap) =====

    [Fact]
    public void RequestDeferredPause_TransitionsToWaitingForSnap()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);

        sm.RequestDeferredPause(500);
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPause_WhenInactive_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.RequestDeferredPause(500);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void WaitingForSnap_StaysWhileSnapping()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(500);

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: true);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);
    }

    [Fact]
    public void WaitingForSnap_ZeroPause_ResumesScrollingWhenSnapCompletes()
    {
        // PauseAutoScroll(0): wait for the snap, then resume flow (no display pause).
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(0);

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: false);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    [Fact]
    public void WaitingForSnap_NonZeroPause_TransitionsToPausedWhenSnapCompletes()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(500);

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: false);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
    }

    // ===== Park / resume (semi-auto stop units) =====

    [Fact]
    public void RequestDeferredPark_TransitionsToWaitingForSnap()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);

        sm.RequestDeferredPark();
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPark_WhenInactive_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.RequestDeferredPark();
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void Park_EngagesAfterSnapCompletes_ThenWaitsIndefinitely()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        double t = 0; sm.GetScrollElapsedSeconds = () => t;
        sm.Start(5.0);
        double cameraX = 0;

        sm.RequestDeferredPark();
        // Still snapping → stays in WaitingForSnap, not yet parked.
        sm.Tick(ref cameraX, 0.016, MakeContext(snapInProgress: true));
        Assert.False(sm.Parked);
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);

        // Snap completes → park engages.
        sm.Tick(ref cameraX, 0.016, MakeContext(snapInProgress: false));
        Assert.Equal(AutoScrollState.WaitingForAdvance, sm.CurrentState);
        Assert.True(sm.Parked);
        Assert.Equal(0.0, sm.NormalizedSpeed);

        // Park is indefinite: ticking (even with large elapsed time) neither scrolls nor advances.
        double parkedCam = cameraX;
        t = 10_000;
        Assert.False(sm.Tick(ref cameraX, 0.016, MakeContext(lineRight: 100)));
        Assert.Equal(parkedCam, cameraX);
        Assert.True(sm.Parked);
    }

    [Fact]
    public void ResumeFromPark_ReturnsToScrolling()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;

        sm.RequestDeferredPark();
        sm.Tick(ref cameraX, 0.016, MakeContext(snapInProgress: false)); // engage park
        Assert.True(sm.Parked);

        sm.ResumeFromPark();
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
        Assert.False(sm.Parked);
    }

    [Fact]
    public void ResumeFromPark_WhenNotParked_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.ResumeFromPark(); // Scrolling, not parked
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPause_WhileParked_DoesNotUnpark()
    {
        // A settle-pause request (e.g. Home/End line-edge snap, or a non-intercepted manual
        // advance) must not silently un-park the reader — only ResumeFromPark exits a park.
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        sm.RequestDeferredPark();
        sm.Tick(ref cameraX, 0.016, MakeContext(snapInProgress: false)); // engage park
        Assert.True(sm.Parked);

        sm.RequestDeferredPause(400);
        Assert.True(sm.Parked); // stayed parked, not WaitingForSnap
        Assert.Equal(AutoScrollState.WaitingForAdvance, sm.CurrentState);
    }

    [Fact]
    public void SetBoost_DoublesScrollSpeed()
    {
        // Inject a controlled elapsed-time source so the wall-clock positioning
        // produces deterministic results independent of real execution time.
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.GetScrollElapsedSeconds = () => 1.0;
        sm.Start(1.0);

        // Tick without boost: elapsed = 1.0s, speed = 1.0, zoom = 1.0 → displacement = -1.0
        double cameraX1 = 0;
        var ctx = MakeContext(lineRight: 10000);
        sm.Tick(ref cameraX1, 0, in ctx);

        // Reset and tick with boost: elapsed = 1.0s, speed = 2.0, zoom = 1.0 → displacement = -2.0
        sm.Stop();
        sm.Start(1.0);
        sm.SetBoost(true);
        double cameraX2 = 0;
        sm.Tick(ref cameraX2, 0, in ctx);

        Assert.Equal(cameraX1 * 2, cameraX2, precision: 10);
    }

    [Fact]
    public void NormalizedSpeed_UpdatesDuringScrolling()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(2.5);
        double cameraX = 0;
        var ctx = MakeContext(lineRight: 10000, maxSpeed: 5.0);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(0.5, sm.NormalizedSpeed, precision: 10);
    }
}
