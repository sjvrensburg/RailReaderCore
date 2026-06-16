namespace RailReader.Core.Services;

public sealed partial class RailNav
{
    /// <summary>
    /// Starts auto-scroll at the given speed (page-coordinate pixels/sec).
    /// </summary>
    public void StartAutoScroll(double speed)
    {
        if (!CanNavigate) return;
        _autoScrollState.Start(speed);
        StopScrollAndEdgeHold();
    }

    public void StopAutoScroll()
    {
        _autoScrollState.Stop();
        ScrollSpeed = 0.0;
    }

    /// <summary>Inject a settling pause into auto-scroll (e.g. after advancing to a new block).
    /// The pause is deferred until any snap animation completes, so the full duration
    /// is perceived as stillness after the camera reaches its target.</summary>
    public void PauseAutoScroll(double durationMs)
    {
        _autoScrollState.RequestDeferredPause(durationMs);
    }

    /// <summary>Park auto-scroll on entry to a stop unit (non-prose block, new chunk, new
    /// page). The park is deferred until any snap animation completes, then holds indefinitely
    /// until <see cref="ResumeAutoScrollFromPark"/> — the reader's explicit advance keypress.</summary>
    public void ParkAutoScroll() => _autoScrollState.RequestDeferredPark();

    /// <summary>Resume flow from an indefinite park (the reader pressed the advance key).</summary>
    public void ResumeAutoScrollFromPark() => _autoScrollState.ResumeFromPark();

    /// <summary>True while semi-auto scroll is parked, waiting for an explicit advance keypress.</summary>
    public bool AutoScrollParked => _autoScrollState.Parked;

    /// <summary>Set/clear the boost flag (user holding D/Right during auto-scroll).</summary>
    public void SetAutoScrollBoost(bool boost) => _autoScrollState.SetBoost(boost);

    /// <summary>
    /// Inject a controlled elapsed-seconds source for unit tests.
    /// Forwarded to the underlying <see cref="AutoScrollStateMachine"/>.
    /// </summary>
    internal Func<double>? AutoScrollElapsedSecondsOverride
    {
        set => _autoScrollState.GetScrollElapsedSeconds = value;
    }

    /// <summary>
    /// Returns true if auto-scroll has reached the right edge and should advance.
    /// Called from Tick; the caller is responsible for calling NextLine and snapping.
    /// </summary>
    public bool TickAutoScroll(ref double cameraX, double dtSecs, double zoom, double windowWidth)
    {
        if (!_autoScrollState.IsActive || _navigableIndices.Count == 0) return false;

        // Advance at the current LINE's right extent (not the block/chunk right edge) so a
        // short line in a wide block doesn't auto-scroll through trailing empty space —
        // matching the manual edge-hold trigger (IsAtHardEdge). The dwell decision below
        // still uses the raw BLOCK width (whether the block needs horizontal scrolling).
        var (_, lineRight, lineWidthPx) = GetLineBounds(zoom);

        var ctx = new AutoScrollContext
        {
            SnapInProgress = _snap is not null,
            LineRight = lineRight,
            // "Fits the viewport" is judged in screen pixels (zoom-dependent) — that is what
            // determines whether the line scrolls at all. The flat per-line beat is applied
            // only to fit-in-window lines; the set of beat-eligible lines SHRINKS as zoom
            // rises (W/zoom falls), so at high zoom even moderate lines scroll (and earn
            // their reading time by scrolling rather than the beat).
            LineFitsWindow = lineWidthPx <= windowWidth,
            LinePauseMs = _config.AutoScrollLinePauseMs,
            WindowWidth = windowWidth,
            Zoom = zoom,
            MaxSpeed = _config.ScrollSpeedMax,
        };

        bool reachedEnd = _autoScrollState.Tick(ref cameraX, dtSecs, in ctx);
        cameraX = SnapX(cameraX, zoom);
        ScrollSpeed = _autoScrollState.NormalizedSpeed;
        return reachedEnd;
    }
}
