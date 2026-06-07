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
    public void PauseAutoScroll(double durationMs, bool resetDwell = false)
    {
        _autoScrollState.RequestDeferredPause(durationMs, resetDwell);
    }

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
        var (_, lineRight, _) = GetLineBounds(zoom);
        var block = CurrentNavigableBlock;

        var ctx = new AutoScrollContext
        {
            SnapInProgress = _snap is not null,
            LineRight = lineRight,
            RawBlockWidthPx = block.BBox.W * zoom,
            CurrentLine = CurrentLine,
            BlockLineCount = block.Lines.Count,
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
