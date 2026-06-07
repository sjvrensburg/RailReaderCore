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
        double blockRight = 1000,
        double rawBlockWidthPx = 800,
        int currentLine = 0,
        int blockLineCount = 10,
        double linePauseMs = 200,
        double windowWidth = 600,
        double zoom = 1.0,
        double maxSpeed = 5.0)
    {
        return new AutoScrollContext
        {
            SnapInProgress = snapInProgress,
            LineRight = blockRight, // helper param feeds the advance boundary (now the line's right edge)
            RawBlockWidthPx = rawBlockWidthPx,
            CurrentLine = currentLine,
            BlockLineCount = blockLineCount,
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
        sm.Start(1.0);
        double cameraX = 0;
        // blockRight far away so we don't reach the end
        var ctx = MakeContext(blockRight: 10000);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
        Assert.True(cameraX < 0, "Camera should move left (negative direction)");
    }

    [Fact]
    public void Tick_WhenScrolling_ReachedBlockEnd_ReturnsTrue()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        // Block right is within the visible area so we immediately reach the end.
        // visibleRight = (-cameraX + windowWidth) / zoom
        // With cameraX near 0, visibleRight = 600, blockRight = 100 => reached end.
        // currentLine = 9, blockLineCount = 10 => is block end.
        var ctx = MakeContext(blockRight: 100, currentLine: 9, blockLineCount: 10, linePauseMs: 0);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.True(result);
    }

    [Fact]
    public void Tick_MidBlockLineEnd_EntersPausedState()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        // visibleRight >= blockRight immediately, mid-block (not last line), linePauseMs > 0
        var ctx = MakeContext(blockRight: 100, currentLine: 3, blockLineCount: 10, linePauseMs: 200, rawBlockWidthPx: 2000);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPause_TransitionsToWaitingForSnap()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);

        sm.RequestDeferredPause(500);
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);
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
    public void WaitingForSnap_TransitionsToPausedWhenSnapCompletes()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(500);

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: false);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
    }

    [Fact]
    public void Paused_ResumesScrollingAfterTimeout()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(50); // short pause

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: false);

        // Snap completes -> enters pause
        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);

        // Wait for the pause to expire
        Thread.Sleep(55);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPause_WhenInactive_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.RequestDeferredPause(500);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void Dwell_ResetsOnBlockCrossing_SoNextFitBlockDwellsAgain()
    {
        // Regression: a within-chunk block crossing carries duration 0 but MUST
        // reset the per-block dwell, else the next fit-in-window block flashes
        // past without its settling dwell.
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        double t = 0; sm.GetScrollElapsedSeconds = () => t;
        sm.Start(5.0);
        double cam = 0;
        // fit-in-window block, one line, tiny dwell so the pause clears fast
        var ctx = MakeContext(rawBlockWidthPx: 100, blockRight: 50, blockLineCount: 1,
            linePauseMs: 1, windowWidth: 600, zoom: 1, maxSpeed: 5);

        t = 20; sm.Tick(ref cam, 0.016, ctx);                  // scroll to end → dwell
        Assert.Equal(AutoScrollState.Dwelling, sm.CurrentState);
        System.Threading.Thread.Sleep(3);
        Assert.True(sm.Tick(ref cam, 0.016, ctx));             // dwell done → advance, Scrolling

        sm.RequestDeferredPause(0, resetDwell: true);          // within-chunk crossing
        sm.Tick(ref cam, 0.016, ctx);                          // WaitingForSnap → Scrolling (reinit)

        t += 20; sm.Tick(ref cam, 0.016, ctx);                 // next fit block reaches end
        Assert.Equal(AutoScrollState.Dwelling, sm.CurrentState); // re-dwells thanks to reset
    }

    [Fact]
    public void Dwell_NotReset_OnMidBlockLineAdvance()
    {
        // The original guard must hold: a mid-block line advance (duration 0,
        // resetDwell false) does NOT reset dwell, so a narrow block doesn't
        // re-dwell on every line.
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        double t = 0; sm.GetScrollElapsedSeconds = () => t;
        sm.Start(5.0);
        double cam = 0;
        var ctx = MakeContext(rawBlockWidthPx: 100, blockRight: 50, blockLineCount: 1,
            linePauseMs: 1, windowWidth: 600, zoom: 1, maxSpeed: 5);

        t = 20; sm.Tick(ref cam, 0.016, ctx);
        Assert.Equal(AutoScrollState.Dwelling, sm.CurrentState);
        System.Threading.Thread.Sleep(3);
        Assert.True(sm.Tick(ref cam, 0.016, ctx));             // Scrolling

        sm.RequestDeferredPause(0);                            // mid-block line advance, no reset
        sm.Tick(ref cam, 0.016, ctx);                          // → Scrolling
        t += 20; sm.Tick(ref cam, 0.016, ctx);                 // reaches end again
        Assert.NotEqual(AutoScrollState.Dwelling, sm.CurrentState); // does NOT re-dwell
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
        var ctx = MakeContext(blockRight: 10000);
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
        var ctx = MakeContext(blockRight: 10000, maxSpeed: 5.0);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(0.5, sm.NormalizedSpeed, precision: 10);
    }
}
