using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core;

internal enum EdgeHoldState
{
    Idle,
    Holding,
    Cooldown,
}

/// <summary>
/// Manages edge-hold advance for both non-rail vertical navigation and
/// rail-mode horizontal navigation. When the user holds a key at the
/// content edge for long enough, fires an advance signal, then enters
/// a cooldown to suppress key-repeat from immediately retriggering.
/// </summary>
internal sealed class EdgeHoldStateMachine
{
    public EdgeHoldState CurrentState { get; private set; } = EdgeHoldState.Idle;

    // Monotonic clock; process-wide is fine because only elapsed differences are used.
    private static readonly Stopwatch SharedClock = Stopwatch.StartNew();

    /// <summary>
    /// Inject a controlled milliseconds source for unit tests (mirrors
    /// <c>AutoScrollStateMachine.GetScrollElapsedSeconds</c>). When set, the
    /// real clock is not used.
    /// </summary>
    internal Func<double>? GetNowMs;

    private double NowMs => GetNowMs?.Invoke() ?? SharedClock.Elapsed.TotalMilliseconds;

    // Timestamp (in NowMs units) when the current hold/cooldown began; null when idle.
    private double? _markMs;
    private bool _forward;

    // Output signals: set when OnEdgeHit fires, consumed by the caller.
    private ScrollDirection? _pendingAdvance;
    private bool _advanceJustFired;
    // True after an advance fires until the caller resets on key release.
    // Stops a continued key-hold from panning on the just-flipped page.
    private bool _suppressUntilRelease;

    /// <summary>
    /// Called when the camera is at the content edge after a nav attempt.
    /// Returns true when the hold threshold has been reached and the caller
    /// should advance. Also sets a pending advance direction and the
    /// advance-suppression flag.
    /// </summary>
    public bool OnEdgeHit(bool forward)
    {
        switch (CurrentState)
        {
            case EdgeHoldState.Cooldown:
                if (NowMs - _markMs!.Value < CoreTuning.EdgeCooldownMs) return false;
                // Cooldown expired, fall through to start a new hold
                goto case EdgeHoldState.Idle;

            case EdgeHoldState.Idle:
                _markMs = NowMs;
                _forward = forward;
                CurrentState = EdgeHoldState.Holding;
                return false;

            case EdgeHoldState.Holding:
                if (_forward != forward)
                {
                    // Direction changed, restart
                    _markMs = NowMs;
                    _forward = forward;
                    return false;
                }
                if (NowMs - _markMs!.Value >= CoreTuning.EdgeHoldMs)
                {
                    _markMs = NowMs;
                    CurrentState = EdgeHoldState.Cooldown;
                    _pendingAdvance = forward ? ScrollDirection.Forward : ScrollDirection.Backward;
                    _advanceJustFired = true;
                    _suppressUntilRelease = true;
                    return true; // fire advance
                }
                return false;

            default:
                return false;
        }
    }

    public void OnMoved()
    {
        if (CurrentState != EdgeHoldState.Idle)
        {
            CurrentState = EdgeHoldState.Idle;
            _markMs = null;
        }
    }

    /// <summary>
    /// True from when an advance fires until the caller resets on key release.
    /// Used by non-rail navigation to stop auto-repeat from panning past the
    /// just-flipped page top/bottom while the user still holds the key.
    /// </summary>
    public bool ShouldSuppressInput => _suppressUntilRelease;

    /// <summary>
    /// Returns the direction of a pending edge advance and clears it.
    /// Used by rail-mode navigation where the advance is consumed
    /// asynchronously in the tick loop.
    /// </summary>
    public ScrollDirection? ConsumePendingAdvance()
    {
        var result = _pendingAdvance;
        _pendingAdvance = null;
        return result;
    }

    /// <summary>
    /// True after an advance fires, until the caller clears it.
    /// Used by rail-mode navigation to suppress input while a
    /// post-advance snap animation completes.
    /// </summary>
    public bool AdvanceJustFired => _advanceJustFired;

    public void ClearAdvanceFlag() => _advanceJustFired = false;

    /// <summary>Resets all state including output signals.</summary>
    public void Reset()
    {
        CurrentState = EdgeHoldState.Idle;
        _markMs = null;
        _pendingAdvance = null;
        _advanceJustFired = false;
        _suppressUntilRelease = false;
    }
}
