using System.Diagnostics;

namespace RailReader.Core.Services;

internal enum AutoScrollState
{
    Inactive,
    Scrolling,
    WaitingForSnap,
    Paused,
    /// <summary>Indefinite park (no timer): semi-auto scroll has stopped on entry to a
    /// non-prose unit / chunk / page and waits for an explicit advance keypress.</summary>
    WaitingForAdvance,
}

/// <summary>
/// Provides camera clamping to the auto-scroll state machine.
/// Implemented by <see cref="RailNav"/> to avoid per-frame delegate allocation.
/// </summary>
internal interface ICameraClamp { double ClampX(double cameraX, double zoom, double windowWidth); }

internal readonly struct AutoScrollContext
{
    public required bool SnapInProgress { get; init; }
    /// <summary>Right edge of the current line (with margin) — the auto-scroll line-end.</summary>
    public required double LineRight { get; init; }
    /// <summary>Flat per-line beat (ms) held after every line reaches its right extent, before
    /// advancing — the sole intra-flow cadence knob. Applied to all lines (wide lines included)
    /// so each carriage-return is preceded by a rest rather than flipping straight to the next
    /// line, which read as abrupt. Set to 0 to advance immediately.</summary>
    public required double LinePauseMs { get; init; }
    public required double WindowWidth { get; init; }
    public required double Zoom { get; init; }
    public required double MaxSpeed { get; init; }
}

/// <summary>
/// Manages semi-automatic auto-scroll as an explicit state machine. Flow advances
/// line-by-line through prose on a single speed knob; the orchestrator decides where to
/// <see cref="RequestDeferredPark"/> (a non-prose unit, a new chunk, a new page) and the
/// reader resumes with an explicit advance keypress (<see cref="ResumeFromPark"/>).
///
/// State transitions:
///   Inactive ──Start()──────────────────────────→ Scrolling
///   Scrolling ──reached line end (LinePauseMs>0)→ Paused (brief per-line beat), then advances
///   Scrolling ──reached line end (LinePauseMs=0)→ returns reachedEnd immediately
///   Any ──RequestDeferredPause()────────────────→ WaitingForSnap (resume flow after snap)
///   Any ──RequestDeferredPark()─────────────────→ WaitingForSnap (park after snap)
///   WaitingForSnap ──snap completes, pause──────→ Paused (non-advancing)
///   WaitingForSnap ──snap completes, park───────→ WaitingForAdvance (indefinite)
///   WaitingForSnap ──snap completes, neither────→ Scrolling
///   Paused ──timer expires──────────────────────→ Scrolling (+ returns reachedEnd if advancing)
///   WaitingForAdvance ──ResumeFromPark()────────→ Scrolling
///   Any ──Stop()────────────────────────────────→ Inactive
/// </summary>
internal sealed class AutoScrollStateMachine
{
    private readonly ICameraClamp _clamp;

    public AutoScrollState CurrentState { get; private set; } = AutoScrollState.Inactive;
    public bool IsActive => CurrentState != AutoScrollState.Inactive;
    /// <summary>True while parked indefinitely, waiting for an explicit advance keypress.</summary>
    public bool Parked => CurrentState == AutoScrollState.WaitingForAdvance;

    // Speed
    private double _speed;
    private bool _boost;

    // WaitingForSnap: deferred action that fires once the snap completes.
    private double _pendingPauseMs;
    private bool _pendingPark;

    // Paused: countdown timer
    private Stopwatch? _pauseTimer;
    private double _pauseDurationMs;
    private bool _pauseAdvances; // true = pause triggers line advance on completion

    // Wall-clock scroll positioning: camera position is computed as an absolute
    // function of elapsed time rather than accumulated frame deltas. This means
    // each frame shows exactly where the content should be at that moment.
    // A dropped frame (33ms instead of 16ms) produces a clean 2-frame jump
    // instead of a sustained lag-then-catchup, which is perceived as jitter.
    // _scrollInitialized = false signals that the next TickScrolling call must
    // capture the current cameraX as the reference start position.
    private bool _scrollInitialized;
    private Stopwatch? _scrollClock;
    private double _scrollStartX;

    /// <summary>
    /// Inject a controlled elapsed-seconds source for unit tests.
    /// When set, the real Stopwatch is not used.
    /// </summary>
    internal Func<double>? GetScrollElapsedSeconds;

    private double ScrollElapsed => GetScrollElapsedSeconds?.Invoke()
        ?? _scrollClock?.Elapsed.TotalSeconds
        ?? 0.0;

    /// <summary>Normalized scroll speed (0-1) for UI display.</summary>
    public double NormalizedSpeed { get; private set; }

    public AutoScrollStateMachine(ICameraClamp clamp)
    {
        _clamp = clamp;
    }

    public void Start(double speed)
    {
        Reset();
        CurrentState = AutoScrollState.Scrolling;
        _speed = speed;
    }

    public void Stop()
    {
        Reset();
        CurrentState = AutoScrollState.Inactive;
    }

    private void Reset()
    {
        _speed = 0;
        _boost = false;
        _pauseTimer = null;
        _pendingPauseMs = 0;
        _pendingPark = false;
        NormalizedSpeed = 0;
        _scrollInitialized = false;
        _scrollClock = null;
        _scrollStartX = 0;
    }

    /// <summary>Set/clear the boost flag (user holding D/Right during auto-scroll).</summary>
    public void SetBoost(bool boost)
    {
        if (_boost == boost) return;
        _boost = boost;
        // Re-capture current position as new reference so the speed change
        // takes effect from the current camera position without a jump.
        _scrollInitialized = false;
    }

    /// <summary>
    /// Request a pause that starts after the current snap animation completes.
    /// Used after a mid-block line advance to resume flow without fighting the snap
    /// (pass durationMs=0 to wait for snap completion with no display pause).
    /// Transition: current state -> WaitingForSnap.
    /// </summary>
    public void RequestDeferredPause(double durationMs)
    {
        // A settle-pause request must never silently un-park: while parked the only exit is
        // ResumeFromPark (so a stray manual nudge — Home/End, a non-intercepted advance — that
        // calls PauseAutoScroll keeps the reader on the parked unit).
        if (!IsActive || Parked) return;
        _pendingPauseMs = durationMs;
        _pendingPark = false;
        CurrentState = AutoScrollState.WaitingForSnap;
    }

    /// <summary>
    /// Request an indefinite park that engages after the current snap animation completes,
    /// so the parked frame is the settled, centred target. Used when a line advance enters a
    /// stop unit (non-prose block, new chunk, new page). Exited only by <see cref="ResumeFromPark"/>.
    /// Transition: current state -> WaitingForSnap -> WaitingForAdvance.
    /// </summary>
    public void RequestDeferredPark()
    {
        // No-op when inactive or already parked (idempotent re-entry).
        if (!IsActive || Parked) return;
        _pendingPauseMs = 0;
        _pendingPark = true;
        CurrentState = AutoScrollState.WaitingForSnap;
    }

    /// <summary>
    /// Resume flow from an indefinite park (the reader pressed the advance key).
    /// Transition: WaitingForAdvance -> Scrolling.
    /// </summary>
    public void ResumeFromPark()
    {
        if (CurrentState != AutoScrollState.WaitingForAdvance) return;
        CurrentState = AutoScrollState.Scrolling;
        _scrollInitialized = false; // capture new start position from the parked camera
    }

    /// <summary>
    /// Update the scroll speed without resetting state (e.g. config change via [ / ] keys).
    /// </summary>
    public void UpdateSpeed(double speed)
    {
        if (!IsActive || _speed == speed) return;
        _speed = speed;
        // Re-capture current position so the new speed starts from here.
        _scrollInitialized = false;
    }

    /// <summary>
    /// Advance the state machine by one frame. Modifies camera position when scrolling.
    /// Returns true when the line end has been reached and the caller should advance.
    /// </summary>
    public bool Tick(ref double cameraX, double dtSecs, in AutoScrollContext ctx)
    {
        return CurrentState switch
        {
            AutoScrollState.WaitingForSnap => TickWaitingForSnap(in ctx),
            AutoScrollState.Paused => TickPause(),
            AutoScrollState.Scrolling => TickScrolling(ref cameraX, dtSecs, in ctx),
            _ => false, // Inactive / WaitingForAdvance: no-op
        };
    }

    private bool TickWaitingForSnap(in AutoScrollContext ctx)
    {
        if (ctx.SnapInProgress) return false; // still snapping

        // Snap completed -> engage the deferred park, the deferred pause, or resume flow.
        if (_pendingPark)
        {
            _pendingPark = false;
            CurrentState = AutoScrollState.WaitingForAdvance;
            NormalizedSpeed = 0;
            return false;
        }

        double pauseMs = _pendingPauseMs;
        _pendingPauseMs = 0;
        if (pauseMs > 0)
            BeginPause(pauseMs, advances: false);
        else
        {
            CurrentState = AutoScrollState.Scrolling;
            _scrollInitialized = false; // capture new start position from snap target
        }
        return false;
    }

    private bool TickPause()
    {
        if (_pauseTimer is null) return false;

        if (_pauseTimer.Elapsed.TotalMilliseconds >= _pauseDurationMs)
        {
            bool advance = _pauseAdvances;
            _pauseTimer = null;
            CurrentState = AutoScrollState.Scrolling;
            _scrollInitialized = false; // capture new start position from post-pause camera
            return advance;
        }
        return false; // still pausing
    }

    private bool TickScrolling(ref double cameraX, double dtSecs, in AutoScrollContext ctx)
    {
        // Wall-clock positioning: compute cameraX as an absolute function of elapsed
        // time since scrolling started. This means every frame shows exactly where
        // the content should be at that moment — dropped frames produce a clean jump
        // to the correct position rather than sustained lag followed by catchup jitter.
        if (!_scrollInitialized)
        {
            _scrollStartX = cameraX;
            _scrollClock = GetScrollElapsedSeconds is null ? Stopwatch.StartNew() : null;
            _scrollInitialized = true;
        }

        double speed = _boost ? _speed * 2.0 : _speed;
        cameraX = _scrollStartX - speed * ctx.Zoom * ScrollElapsed;
        NormalizedSpeed = ctx.MaxSpeed > 0 ? Math.Clamp(speed / ctx.MaxSpeed, 0.0, 1.0) : 0.0;
        cameraX = _clamp.ClampX(cameraX, ctx.Zoom, ctx.WindowWidth);

        // Check if we've reached the right edge of the line
        double visibleRight = (-cameraX + ctx.WindowWidth) / ctx.Zoom;
        if (visibleRight < ctx.LineRight)
            return false; // still scrolling

        // Reached the line's right edge. Hold a flat per-line beat before advancing — on EVERY
        // line, wide ones included. A wide line reaching its end then snapping straight back to
        // the next line's start (a full-width carriage-return) with no rest read as abrupt; the
        // beat puts a brief pause between "done reading this line" and the carriage-return. The
        // "is this a stop unit?" decision belongs to the orchestrator (it needs role / chunk /
        // page knowledge the state machine doesn't have); this just reports reachedEnd.
        if (ctx.LinePauseMs > 0)
        {
            BeginPause(ctx.LinePauseMs, advances: true);
            return false;
        }
        return true;
    }

    private void BeginPause(double durationMs, bool advances)
    {
        _pauseTimer = Stopwatch.StartNew();
        _pauseDurationMs = durationMs;
        _pauseAdvances = advances;
        NormalizedSpeed = 0;
        CurrentState = AutoScrollState.Paused;
    }
}
