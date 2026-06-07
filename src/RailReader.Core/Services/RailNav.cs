using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed partial class RailNav : ICameraClamp
{
    private CoreSettings _config;
    private PageAnalysis? _analysis;
    private readonly List<int> _navigableIndices = [];

    public int CurrentBlock { get; set; }
    public int CurrentLine { get; set; }
    public bool Active { get; set; }
    public double ScrollSpeed { get; private set; }

    /// <summary>Zoom level at or above which rail mode activates.</summary>
    public double ZoomThreshold => _config.RailZoomThreshold;

    /// <summary>
    /// Vertical offset from center (in pixels). Positive = line drawn above center.
    /// Set by user panning in rail mode; preserved across line navigation.
    /// </summary>
    public double VerticalBias { get; set; }

    private SnapAnimation? _snap;
    private readonly HorizontalScrollState _scroll = new();

    // Auto-scroll state machine: explicit states replace the previous implicit flag combinations
    private readonly AutoScrollStateMachine _autoScrollState;
    public bool AutoScrolling => _autoScrollState.IsActive;

    /// <summary>
    /// Set to true when sustained horizontal scroll triggers auto-scroll.
    /// The controller consumes this signal to transition state.
    /// </summary>
    private bool _autoScrollTriggered;
    public bool ConsumeAutoScrollTrigger()
    {
        if (!_autoScrollTriggered) return false;
        _autoScrollTriggered = false;
        return true;
    }

    // Edge-hold advance: when the user holds D/Right (or A/Left) against the line boundary,
    // the state machine fires a line advance after a hold threshold.
    private readonly EdgeHoldStateMachine _lineEdgeHold = new();



    /// <summary>
    /// Whether the current navigable block's role is in the centering role set.
    /// </summary>
    private bool ShouldCenterBlock() =>
        _config.CenteringRoles.Contains(CurrentNavigableBlock.Role);

    /// <summary>
    /// Single centering predicate shared by the snap target and the per-frame
    /// clamp, so they agree: a centering-role unit is centred only when it is
    /// narrower than <see cref="CoreTuning.CenterBlockThreshold"/> of the window.
    /// Using different thresholds in the two paths caused a snap-then-jump for
    /// content in the [threshold·window, window) band.
    /// </summary>
    private bool ShouldCenterUnit(double widthPx, double windowWidth) =>
        widthPx < windowWidth * CoreTuning.CenterBlockThreshold && ShouldCenterBlock();

    /// <summary>
    /// Returns true if input should be suppressed because an edge-hold advance
    /// snap animation is still in progress.
    /// </summary>
    private bool ShouldSuppressAfterEdgeAdvance()
    {
        if (!_lineEdgeHold.AdvanceJustFired) return false;
        if (_snap is not null) return true; // snap still running
        _lineEdgeHold.ClearAdvanceFlag();
        return false;
    }

    public RailNav(CoreSettings config)
    {
        _config = config;
        _autoScrollState = new AutoScrollStateMachine(this);
    }

    double ICameraClamp.ClampX(double cameraX, double zoom, double windowWidth)
        => ClampX(cameraX, zoom, windowWidth);

    public void SetAnalysis(PageAnalysis analysis, IReadOnlySet<BlockRole> navigable)
    {
        // If re-applying the same analysis (e.g. config change that didn't affect
        // navigable roles), preserve the current navigation position.
        bool sameAnalysis = ReferenceEquals(_analysis, analysis);

        _navigableIndices.Clear();
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (navigable.Contains(analysis.Blocks[i].Role))
                _navigableIndices.Add(i);
        }
        _analysis = analysis;
        BuildChunks();

        if (!sameAnalysis)
        {
            CurrentBlock = 0;
            CurrentLine = 0;
            VerticalBias = 0;
            _snap = null;
            _scroll.Stop();
            ScrollSpeed = 0.0;
        }
        else
        {
            // Clamp in case navigable set changed and current block is out of range
            if (CurrentBlock >= _navigableIndices.Count)
                CurrentBlock = Math.Max(0, _navigableIndices.Count - 1);
            if (_navigableIndices.Count > 0 && CurrentLine >= CurrentNavigableBlock.Lines.Count)
                CurrentLine = Math.Max(0, CurrentNavigableBlock.Lines.Count - 1);
        }
    }

    public bool HasAnalysis => _analysis is not null && _navigableIndices.Count > 0;
    public PageAnalysis? Analysis => _analysis;
    public int NavigableCount => _navigableIndices.Count;

    /// <summary>
    /// Progress of the current snap animation (0.0–1.0). Returns 1.0 if no snap is active.
    /// Used to enable early pixel snapping during the animation tail.
    /// </summary>
    public double SnapProgress => _snap is { } s
        ? Math.Min(s.Timer.Elapsed.TotalMilliseconds / s.DurationMs, 1.0)
        : 1.0;

    /// <summary>True when rail mode is active and has navigable blocks.</summary>
    private bool CanNavigate => Active && _navigableIndices.Count > 0;

    public int CurrentLineCount =>
        _navigableIndices.Count == 0 ? 0 : CurrentNavigableBlock.Lines.Count;

    public void UpdateZoom(double zoom, double cameraX, double cameraY, double windowWidth, double windowHeight,
        double? cursorPageX = null, double? cursorPageY = null)
    {
        bool shouldBeActive = zoom >= _config.RailZoomThreshold && HasAnalysis;

        if (shouldBeActive && !Active)
        {
            Active = true;
            if (_pinnedActivationBlock is { } pinned)
            {
                // An explicit framing (e.g. SmoothlyFrameBlock) pinned the target block;
                // honour it instead of geometric nearest-block selection, so overlapping
                // bboxes under the focus point can't redirect to a different block.
                CurrentBlock = Math.Min(pinned, Math.Max(0, _navigableIndices.Count - 1));
                CurrentLine = 0;
            }
            else if (cursorPageX.HasValue && cursorPageY.HasValue)
                FindBlockNearPoint(cursorPageX.Value, cursorPageY.Value);
            else
                FindNearestBlock(cameraX, cameraY, zoom, windowWidth, windowHeight);
            _pinnedActivationBlock = null;
        }
        else if (!shouldBeActive && Active)
        {
            Active = false;
            _snap = null;
            _scroll.Stop();
            ScrollSpeed = 0.0;
            _pinnedActivationBlock = null;
        }
    }

    // Navigable-subset index to honour on the next rail activation, set by an explicit
    // framing (SmoothlyFrameBlock) so the seated block survives the threshold crossing.
    // Consumed on the next activate; cleared on deactivate so it can't go stale.
    private int? _pinnedActivationBlock;

    /// <summary>Pin the current block so the next rail activation keeps it seated
    /// instead of running geometric nearest-block selection.</summary>
    public void PinCurrentBlockForActivation() => _pinnedActivationBlock = CurrentBlock;

    /// <summary>Force rail out of active mode. Mirrors the deactivate branch of
    /// <see cref="UpdateZoom"/>; used by geometric centred framing, which drives the camera
    /// directly (no rail) and must not leave a stale snap/scroll running.</summary>
    public void Deactivate()
    {
        Active = false;
        _snap = null;
        _scroll.Stop();
        ScrollSpeed = 0.0;
        _pinnedActivationBlock = null;
    }

    /// <summary>
    /// Scale VerticalBias proportionally so the active line stays at the
    /// same screen position when zoom changes incrementally.
    /// </summary>
    public void ScaleVerticalBias(double previousZoom, double newZoom)
    {
        if (Active && previousZoom > 0 && Math.Abs(previousZoom - newZoom) > 0.001)
            VerticalBias *= newZoom / previousZoom;
    }

    public void FindNearestBlock(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (_analysis is null) return;

        double centerX = (windowWidth / 2.0 - cameraX) / zoom;
        double centerY = (windowHeight / 2.0 - cameraY) / zoom;

        CurrentBlock = FindNearestNavigableIndex(centerX, centerY);
        CurrentLine = 0;
    }

    /// <summary>
    /// Finds the navigable block nearest to a point in page coordinates.
    /// Tries a direct bounding-box hit first; falls back to nearest-center distance.
    /// </summary>
    public void FindBlockNearPoint(double pageX, double pageY)
    {
        if (_analysis is null || _navigableIndices.Count == 0) return;

        int? hit = FindBlockAtPoint(pageX, pageY);
        CurrentBlock = hit ?? FindNearestNavigableIndex(pageX, pageY);
        CurrentLine = FindNearestLine(CurrentNavigableBlock, pageY);
    }

    private int FindNearestNavigableIndex(double pageX, double pageY)
    {
        double bestDist = double.MaxValue;
        int bestIdx = 0;
        for (int i = 0; i < _navigableIndices.Count; i++)
        {
            var block = _analysis!.Blocks[_navigableIndices[i]];
            double dx = block.BBox.X + block.BBox.W / 2.0 - pageX;
            double dy = block.BBox.Y + block.BBox.H / 2.0 - pageY;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    private static int FindNearestLine(LayoutBlock block, double pageY)
    {
        double bestDist = double.MaxValue;
        int bestLine = 0;
        for (int j = 0; j < block.Lines.Count; j++)
        {
            double lineMid = block.Lines[j].Y; // LineInfo.Y is the line centre
            double d = Math.Abs(lineMid - pageY);
            if (d < bestDist) { bestDist = d; bestLine = j; }
        }
        return bestLine;
    }

    public NavResult NextLine()
    {
        if (!CanNavigate) return NavResult.Ok;

        var block = CurrentNavigableBlock;
        if (CurrentLine + 1 < block.Lines.Count)
        {
            CurrentLine++;
            return NavResult.Ok;
        }
        if (CurrentBlock + 1 < _navigableIndices.Count)
        {
            CurrentBlock++;
            CurrentLine = 0;
            return NavResult.Ok;
        }
        return NavResult.PageBoundaryNext;
    }

    public NavResult PrevLine()
    {
        if (!CanNavigate) return NavResult.Ok;

        if (CurrentLine > 0)
        {
            CurrentLine--;
            return NavResult.Ok;
        }
        if (CurrentBlock > 0)
        {
            CurrentBlock--;
            CurrentLine = CurrentNavigableBlock.Lines.Count - 1;
            return NavResult.Ok;
        }
        return NavResult.PageBoundaryPrev;
    }

    public void StartScroll(ScrollDirection dir, double currentCameraX)
    {
        if (!CanNavigate) return;

        if (ShouldSuppressAfterEdgeAdvance()) return;

        if (_scroll.Direction != dir)
        {
            // If a snap is in progress, jump to its target and start scrolling from there.
            double startX = _snap is { } activeSnap ? activeSnap.TargetX : currentCameraX;
            _snap = null;
            _scroll.Start(dir, startX, _config.ScrollSpeedStart, _config.ScrollSpeedMax);
        }
    }

    public void StopScroll()
    {
        _scroll.Stop();
        ScrollSpeed = 0.0;
    }

    /// <summary>Clears both scroll and edge-hold state (e.g. on key release or mode change).</summary>
    public void StopScrollAndEdgeHold()
    {
        StopScroll();
        _lineEdgeHold.Reset();
    }

    /// <summary>
    /// Returns the direction of a pending edge-advance (triggered by holding D/Right or A/Left
    /// against the line boundary) and clears it. Returns null if none pending.
    /// </summary>
    public ScrollDirection? ConsumePendingEdgeAdvance()
        => _lineEdgeHold.ConsumePendingAdvance();

    /// <summary>
    /// Checks whether the camera is at the hard edge for the given direction and, if so,
    /// accumulates hold time via the edge-hold state machine. Returns true when the hold
    /// threshold is reached and an edge advance has been triggered.
    /// </summary>
    private bool CheckEdgeHoldAdvance(double cameraX, double zoom, double windowWidth, ScrollDirection dir)
    {
        if (IsAtHardEdge(cameraX, zoom, windowWidth, dir))
            return _lineEdgeHold.OnEdgeHit(forward: dir == ScrollDirection.Forward);

        _lineEdgeHold.OnMoved();
        return false;
    }

    /// <summary>
    /// Returns true when the camera is effectively at the hard scroll boundary for the
    /// given direction, i.e. the trigger point for an edge-hold line advance.
    ///
    /// Forward (next line) fires at the END OF THE CURRENT LINE, not the block/chunk
    /// right edge: a short line sits well left of the block's right margin, and gating
    /// on the block edge forced the user to scroll through trailing empty space before
    /// the next line would trigger. Backward (previous line) still uses the chunk's
    /// left edge. Horizontal panning of wide content is unaffected — <see cref="ClampX"/>
    /// keeps using the chunk bounds so over-scroll is still bounded by the column.
    /// <c>internal</c> for direct boundary testing.
    /// </summary>
    internal bool IsAtHardEdge(double cameraX, double zoom, double windowWidth, ScrollDirection dir)
    {
        if (_navigableIndices.Count == 0) return false;
        var (blockLeft, _, blockWidthPx) = GetChunkBounds(zoom);

        // If the whole chunk fits in the window it is centred and cannot scroll at all.
        if (blockWidthPx <= windowWidth) return true;

        const double epsilon = 2.0; // pixels of tolerance

        if (dir == ScrollDirection.Forward)
        {
            var (_, lineRight, _) = GetLineBounds(zoom);
            double lineMinX = windowWidth - lineRight * zoom; // camera X with the line's end at the right edge
            return cameraX <= lineMinX + epsilon;             // line end reached → can advance
        }

        double maxX = -blockLeft * zoom;     // left boundary (scrolled all the way left)
        return cameraX >= maxX - epsilon;    // can't scroll further left (content start)
    }

    /// <summary>
    /// Saccade-style jump: moves camera forward/backward by a percentage of visible width.
    /// When <paramref name="half"/> is true, the jump distance is halved (short jump).
    /// </summary>
    public void Jump(bool forward, double zoom, double windowWidth, double windowHeight,
                     double cameraX, double cameraY, bool half = false)
    {
        if (!CanNavigate) return;

        if (ShouldSuppressAfterEdgeAdvance()) return;

        double jumpPx = windowWidth * (_config.JumpPercentage / 100.0);
        if (half) jumpPx *= 0.5;
        double newX = forward ? cameraX - jumpPx : cameraX + jumpPx;
        newX = ClampX(newX, zoom, windowWidth);

        // Edge-hold advance: if the jump can't move the camera (at boundary),
        // accumulate hold time across repeated key-press events and trigger
        // a line advance when the threshold is reached.
        var dir = forward ? ScrollDirection.Forward : ScrollDirection.Backward;
        if (CheckEdgeHoldAdvance(newX, zoom, windowWidth, dir))
            return; // controller will advance line and snap

        var (_, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);

        _snap = new SnapAnimation
        {
            StartX = cameraX,
            StartY = cameraY,
            TargetX = newX,
            TargetY = targetY,
            Timer = Stopwatch.StartNew(),
            DurationMs = 120, // crisp, fast snap
        };
        StopScroll();
    }


    /// <summary>
    /// Captures the vertical bias from the current camera position relative to
    /// where the current line's center-aligned position would be.
    /// Call this when the user manually pans while in rail mode.
    /// </summary>
    public void CaptureVerticalBias(double cameraY, double zoom, double windowHeight)
    {
        if (!CanNavigate) return;
        var line = CurrentLineInfo;
        double centeredY = windowHeight / 2.0 - line.Y * zoom;
        VerticalBias = cameraY - centeredY;
    }

    public double? ComputeLineStartX(double zoom, double windowWidth)
        => ComputeLineEdgeX(zoom, windowWidth, start: true);

    public double? ComputeLineEndX(double zoom, double windowWidth)
        => ComputeLineEdgeX(zoom, windowWidth, start: false);

    private double? ComputeLineEdgeX(double zoom, double windowWidth, bool start)
    {
        if (!CanNavigate) return null;
        var block = CurrentNavigableBlock;
        double x = start
            ? windowWidth * 0.05 - block.BBox.X * zoom
            : windowWidth * 0.95 - (block.BBox.X + block.BBox.W) * zoom;
        _snap = null;
        return ClampX(x, zoom, windowWidth);
    }

    private double ClampX(double cameraX, double zoom, double windowWidth)
    {
        if (_navigableIndices.Count == 0) return cameraX;

        var (blockLeft, blockRight, blockWidthPx) = GetChunkBounds(zoom);

        double result;
        if (blockWidthPx <= windowWidth)
        {
            if (ShouldCenterUnit(blockWidthPx, windowWidth))
            {
                double center = (blockLeft + blockRight) / 2.0;
                result = windowWidth / 2.0 - center * zoom;
            }
            else
            {
                // Left-align with 5% margin (block fully visible, no scroll needed)
                result = windowWidth * 0.05 - blockLeft * zoom;
            }
        }
        else
        {
            double maxX = -blockLeft * zoom;
            double minX = windowWidth - blockRight * zoom;

            // Soft clamp: ease into the boundary using an asymptotic curve
            // instead of a hard stop, which eliminates visual judder.
            // SoftEase(over) = over * k / (k + over) — approaches k as over → ∞.
            const double k = 20.0; // pixels of easing zone
            if (cameraX > maxX)
                result = maxX + SoftEase(cameraX - maxX, k);
            else if (cameraX < minX)
                result = minX - SoftEase(minX - cameraX, k);
            else
                result = cameraX;
        }

        return SnapX(result, zoom);
    }

    private (double Left, double Right, double WidthPx) GetBlockBounds(double zoom)
    {
        var block = CurrentNavigableBlock;
        double margin = block.BBox.W * 0.05;
        double left = block.BBox.X - margin;
        double right = block.BBox.X + block.BBox.W + margin;
        return (left, right, (right - left) * zoom);
    }

    /// <summary>
    /// Horizontal bounds (page points, 5% margin) of the CURRENT LINE. Used to fire the
    /// forward line-advance at the line's actual right extent, which for a short line is
    /// well left of the block/chunk right edge.
    /// </summary>
    private (double Left, double Right, double WidthPx) GetLineBounds(double zoom)
    {
        var line = CurrentLineInfo;
        double margin = line.Width * 0.05;
        double left = line.X - margin;
        double right = line.X + line.Width + margin;
        return (left, right, (right - left) * zoom);
    }

    /// <summary>Asymptotic ease: approaches <paramref name="limit"/> as overshoot grows.</summary>
    private static double SoftEase(double overshoot, double limit)
        => overshoot * limit / (limit + overshoot);

    public bool Tick(ref double cameraX, ref double cameraY, double dtSecs, double zoom, double windowWidth)
    {
        bool animating = TickSnapAnimation(ref cameraX, ref cameraY);

        if (TickScrollHold(ref cameraX, zoom, windowWidth))
            animating = true;

        return animating;
    }


    private bool TickScrollHold(ref double cameraX, double zoom, double windowWidth)
    {
        if (_scroll.Direction is not { } dir)
        {
            ScrollSpeed = 0.0;
            return false;
        }

        double ramp = _config.ScrollRampTime;
        double sStart = _config.ScrollSpeedStart;
        double sMax = _config.ScrollSpeedMax;

        double totalDisplacement = _scroll.ComputeDisplacement(sStart, sMax, ramp);
        double holdSecs = _scroll.ElapsedSecs;

        double instantSpeed = holdSecs <= ramp
            ? sStart + (sMax - sStart) * (holdSecs / ramp) * (holdSecs / ramp)
            : sMax;
        ScrollSpeed = sMax > 0 ? instantSpeed / sMax : 0.0;

        double sign = dir == ScrollDirection.Forward ? -1.0 : 1.0;
        cameraX = SnapX(ClampX(_scroll.StartX + sign * totalDisplacement * zoom, zoom, windowWidth), zoom);

        // Auto-scroll trigger: if holding forward scroll for longer than the
        // configured delay, transition to auto-scroll mode.
        if (_config.AutoScrollTriggerEnabled
            && dir == ScrollDirection.Forward
            && _scroll.ElapsedMs >= _config.AutoScrollTriggerDelayMs)
        {
            StopScrollAndEdgeHold();
            StartAutoScroll(_config.DefaultAutoScrollSpeed);
            _autoScrollTriggered = true;
            return true;
        }

        // Edge-hold advance: if the camera is pinned against the line boundary,
        // accumulate hold time and trigger a line advance when the threshold is reached.
        if (CheckEdgeHoldAdvance(cameraX, zoom, windowWidth, dir))
            StopScroll(); // clear scroll only; output signals are consumed by the controller

        return true;
    }

    public void JumpToEnd()
    {
        if (_navigableIndices.Count == 0) return;
        CurrentBlock = _navigableIndices.Count - 1;
        CurrentLine = CurrentNavigableBlock.Lines.Count - 1;
    }

    public LayoutBlock CurrentNavigableBlock
    {
        get
        {
            int idx = Math.Min(CurrentBlock, _navigableIndices.Count - 1);
            return _analysis!.Blocks[_navigableIndices[idx]];
        }
    }

    /// <summary>
    /// Index of the current block within the page's full block list
    /// (<see cref="PageAnalysis.Blocks"/>), as opposed to <see cref="CurrentBlock"/>
    /// which indexes only the navigable subset. This is the value that lines up
    /// with the block indices reported by page-description queries.
    /// </summary>
    public int CurrentNavigableArrayIndex =>
        _navigableIndices.Count == 0
            ? 0
            : _navigableIndices[Math.Min(CurrentBlock, _navigableIndices.Count - 1)];

    /// <summary>
    /// Seats the rail cursor on the block whose page-level index (into
    /// <see cref="PageAnalysis.Blocks"/>) is <paramref name="pageBlockIndex"/> — the
    /// same index space as <see cref="CurrentNavigableArrayIndex"/> and page-description
    /// queries — landing on its first line and clearing vertical bias. Returns false if
    /// that block is not in the navigable subset (e.g. a non-navigable role), leaving
    /// the cursor unchanged.
    /// </summary>
    public bool TrySetCurrentByPageIndex(int pageBlockIndex)
    {
        int navPos = _navigableIndices.IndexOf(pageBlockIndex);
        if (navPos < 0) return false;
        CurrentBlock = navPos;
        CurrentLine = 0;
        VerticalBias = 0;
        return true;
    }

    /// <summary>
    /// Moves the cursor to the next (forward) or previous block whose role equals
    /// <paramref name="role"/>, starting from the current block, and lands on its
    /// first line. Walks the navigable subset directly (which is already in reading
    /// order), so navigation is exact — it does not geometrically hit-test, which
    /// could otherwise land on an overlapping block. Returns true if a matching
    /// block exists in the given direction on this page; false otherwise (and the
    /// cursor is left unchanged).
    /// </summary>
    public bool TryNavigateToRole(BlockRole role, bool forward)
    {
        if (_analysis is null || _navigableIndices.Count == 0) return false;
        int step = forward ? 1 : -1;
        for (int i = CurrentBlock + step; i >= 0 && i < _navigableIndices.Count; i += step)
        {
            if (_analysis.Blocks[_navigableIndices[i]].Role != role) continue;
            CurrentBlock = i;
            CurrentLine = 0;
            return true;
        }
        return false;
    }

    public LineInfo CurrentLineInfo
    {
        get
        {
            var block = CurrentNavigableBlock;
            return block.Lines[Math.Min(CurrentLine, block.Lines.Count - 1)];
        }
    }

    public int? FindBlockAtPoint(double pageX, double pageY)
    {
        if (_analysis is null) return null;
        for (int i = 0; i < _navigableIndices.Count; i++)
        {
            var b = _analysis.Blocks[_navigableIndices[i]].BBox;
            if (pageX >= b.X && pageX <= b.X + b.W && pageY >= b.Y && pageY <= b.Y + b.H)
                return i;
        }
        return null;
    }


    public void UpdateConfig(CoreSettings config)
    {
        _config = config;
        // If autoscroll is running, apply the updated speed immediately so
        // [ / ] key adjustments take effect without stopping and restarting.
        _autoScrollState.UpdateSpeed(config.DefaultAutoScrollSpeed);
    }

}
