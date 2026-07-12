using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// EdgeHoldStateMachine tests driven by an injected fake clock
/// (<see cref="EdgeHoldStateMachine.GetNowMs"/>), so the timing assertions are
/// deterministic — no Thread.Sleep, no dependence on runner load.
/// </summary>
public class EdgeHoldStateMachineTests
{
    private double _nowMs;

    private EdgeHoldStateMachine NewSm()
    {
        var sm = new EdgeHoldStateMachine { GetNowMs = () => _nowMs };
        return sm;
    }

    [Fact]
    public void OnEdgeHit_FromIdle_StartsHolding()
    {
        var sm = NewSm();
        bool result = sm.OnEdgeHit(forward: true);

        Assert.False(result);
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);
    }

    [Fact]
    public void OnEdgeHit_AfterHoldThreshold_ReturnsTrue()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);

        _nowMs += 405;
        bool result = sm.OnEdgeHit(forward: true);

        Assert.True(result);
        Assert.Equal(EdgeHoldState.Cooldown, sm.CurrentState);
    }

    [Fact]
    public void OnEdgeHit_JustUnderHoldThreshold_DoesNotFire()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);

        _nowMs += 399;
        Assert.False(sm.OnEdgeHit(forward: true));
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);
    }

    [Fact]
    public void OnEdgeHit_DuringCooldown_DoesNotRetrigger()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true); // fires advance → Cooldown

        _nowMs += 299; // still inside the 300ms cooldown
        Assert.False(sm.OnEdgeHit(forward: true));
        Assert.Equal(EdgeHoldState.Cooldown, sm.CurrentState);

        _nowMs += 2; // cooldown expired → a new hold starts (no immediate fire)
        Assert.False(sm.OnEdgeHit(forward: true));
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);
    }

    [Fact]
    public void OnMoved_ResetsToIdle()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);

        sm.OnMoved();
        Assert.Equal(EdgeHoldState.Idle, sm.CurrentState);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true); // fires advance

        sm.Reset();

        Assert.Equal(EdgeHoldState.Idle, sm.CurrentState);
        Assert.False(sm.AdvanceJustFired);
        Assert.Null(sm.ConsumePendingAdvance());
    }

    [Fact]
    public void ShouldSuppressInput_TrueAfterAdvance()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true); // fires advance

        Assert.True(sm.ShouldSuppressInput);
    }

    [Fact]
    public void ShouldSuppressInput_RemainsTrueUntilReset()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true); // fires advance

        _nowMs += 10_000; // long past any plausible cooldown
        Assert.True(sm.ShouldSuppressInput);

        sm.Reset();
        Assert.False(sm.ShouldSuppressInput);
    }

    [Fact]
    public void DirectionChange_ResetsHold()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 200;

        // Change direction mid-hold — should restart the timer
        bool result = sm.OnEdgeHit(forward: false);
        Assert.False(result);
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);

        // Original 400ms has not passed since the direction change
        _nowMs += 205;
        result = sm.OnEdgeHit(forward: false);
        Assert.False(result);

        // Now wait enough for the restarted timer
        _nowMs += 205;
        result = sm.OnEdgeHit(forward: false);
        Assert.True(result);
    }

    [Fact]
    public void AdvanceJustFired_SetAfterAdvance()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true);

        Assert.True(sm.AdvanceJustFired);
    }

    [Fact]
    public void ClearAdvanceFlag_ClearsFlag()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true);
        Assert.True(sm.AdvanceJustFired);

        sm.ClearAdvanceFlag();
        Assert.False(sm.AdvanceJustFired);
    }

    [Fact]
    public void ConsumePendingAdvance_ReturnsDirection()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: true);
        _nowMs += 405;
        sm.OnEdgeHit(forward: true);

        var direction = sm.ConsumePendingAdvance();
        Assert.Equal(Models.ScrollDirection.Forward, direction);

        // Second call should return null (consumed)
        Assert.Null(sm.ConsumePendingAdvance());
    }

    [Fact]
    public void ConsumePendingAdvance_BackwardDirection()
    {
        var sm = NewSm();
        sm.OnEdgeHit(forward: false);
        _nowMs += 405;
        sm.OnEdgeHit(forward: false);

        var direction = sm.ConsumePendingAdvance();
        Assert.Equal(Models.ScrollDirection.Backward, direction);
    }
}
