using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed partial class RailNav : ICameraClamp
{
    private CoreSettings _config;
    private PageAnalysis? _analysis;
    private readonly List<int> _navigableIndices = [];
    // The navigable role set from the last SetAnalysis, kept so ReapplyFocus can rebuild the index set
    // (collapse to / expand from a focus block) without the caller re-supplying it.
    private IReadOnlySet<BlockRole> _navigableRoles = new HashSet<BlockRole>();

    public int CurrentBlock { get; set; }

    private int _currentLine;
    /// <summary>
    /// Index of the active line within the current block. Assigning it seats the cursor at
    /// that row's first cell (<see cref="CurrentCell"/> resets to 0), so every line
    /// transition — internal advances and the controller's pause-restore alike — starts cell
    /// stepping from the left. The same-analysis preserve path in <see cref="SetAnalysis"/>
    /// only reassigns this when the line is out of range, so a pure config refresh keeps the
    /// seated cell.
    /// </summary>
    public int CurrentLine
    {
        get => _currentLine;
        set { _currentLine = value; CurrentCell = 0; }
    }
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
    /// Set-off display units (equations, algorithms) that are framed on their OWN bounds even when
    /// they sit inside a reading chunk — so they centre on themselves rather than left-align to the
    /// wide column run they belong to (regression introduced when chunk framing replaced per-block
    /// framing in 4d890b6). Prose centring roles (Text/Caption/Footnote) deliberately stay
    /// chunk-framed: framing each narrow column paragraph on its own bounds would re-introduce the
    /// per-block camera shift the chunk model exists to remove on multi-column pages.
    /// </summary>
    private static readonly HashSet<BlockRole> SelfFramedRoles =
        [BlockRole.DisplayMath, BlockRole.Algorithm];

    /// <summary>Whether the current block frames on its own bounds (a set-off display unit) rather than
    /// the chunk's. Gated on the role ALSO being a centering role, so the two role sets can't diverge:
    /// a consumer who removes DisplayMath from <see cref="CoreSettings.CenteringRoles"/> to left-align
    /// equations gets prose-style chunk framing too (not a self-framed-but-not-centred hybrid).</summary>
    private bool FrameOnOwnBlock() =>
        SelfFramedRoles.Contains(CurrentNavigableBlock.Role) && ShouldCenterBlock();

    /// <summary>
    /// Horizontal framing bounds for the current unit: the whole chunk for prose (so crossing block
    /// boundaries within a column doesn't shift the camera), but the current BLOCK's own bounds for a
    /// set-off display unit (<see cref="SelfFramedRoles"/>) so an equation/algorithm embedded in a
    /// prose chunk centres on itself instead of left-aligning to the chunk's left edge. Shared by the
    /// snap target, the per-frame clamp, the hard-edge test and the horizontal-fraction maths so they
    /// all agree on the framed unit.
    /// </summary>
    private (double Left, double Right, double WidthPx) GetFramingBounds(double zoom) =>
        FrameOnOwnBlock() ? GetBlockBounds(zoom) : GetChunkBounds(zoom);

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

    public void SetAnalysis(PageAnalysis analysis, IReadOnlySet<BlockRole> navigable,
        bool preservePosition = false, int? focusBlockIndex = null)
    {
        // Preserve the current navigation position when re-applying the same analysis (a config change
        // that didn't affect navigable roles) OR when the caller asks to (preservePosition) — the latter
        // is a same-page reseat under a new table/cell-nav variant, where top-level block indices are
        // invariant so the reader should stay put rather than jump to block 0 (CurrentLine is clamped
        // below in case a table un-collapsed into more/fewer rows).
        bool keepPosition = ReferenceEquals(_analysis, analysis) || preservePosition;

        _analysis = analysis;
        _navigableRoles = navigable;
        RebuildNavigableIndices(focusBlockIndex);
        BuildChunks();

        if (!keepPosition)
        {
            CurrentBlock = 0;
            CurrentLine = 0;
            VerticalBias = 0;
            _snap = null;
            _scroll.Stop();
            ScrollSpeed = 0.0;
            // A new page's analysis ends any forced ("start rail here") low-zoom session — otherwise
            // it would leak onto every subsequently-viewed page (rail re-activating at low zoom there).
            _forceActive = false;
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

    /// <summary>
    /// (Re)builds <see cref="_navigableIndices"/> from the seated analysis + <see cref="_navigableRoles"/>.
    /// When <paramref name="focusBlockIndex"/> is an in-range index the set collapses to just that block
    /// (portal confinement): block-advance, edge-hold, and role jumps all key off this set, so a single
    /// entry pins the rail to one block — force-included even if its role isn't navigable, so a focused
    /// figure/table/equation stays line-steppable. An out-of-range index is ignored (full set kept), so
    /// confinement is all-or-nothing in lockstep with <see cref="Viewport.CurrentFocusBlockIndex"/>.
    /// </summary>
    private void RebuildNavigableIndices(int? focusBlockIndex)
    {
        _navigableIndices.Clear();
        if (_analysis is null) return;
        if (focusBlockIndex is { } fi && fi >= 0 && fi < _analysis.Blocks.Count)
        {
            _navigableIndices.Add(fi);
            return;
        }
        for (int i = 0; i < _analysis.Blocks.Count; i++)
            if (_navigableRoles.Contains(_analysis.Blocks[i].Role))
                _navigableIndices.Add(i);
    }

    /// <summary>
    /// Re-collapse (or restore) the navigable set to <paramref name="focusBlockIndex"/> against the
    /// already-seated analysis, WITHOUT a fresh SetAnalysis. Lets a host confine the rail the moment it
    /// pins <see cref="Viewport.Focus"/> on an already-rail-seated page (and un-confine when it clears it)
    /// rather than waiting for the next reseat. No-op before any analysis is seated.
    /// <para>The cursor follows its CURRENT page-block across the rebuild (so un-pinning returns to where
    /// the focus block was, not block 0), and any in-flight snap/scroll/bias is cleared — a set-change
    /// while a snap toward the old block was animating would otherwise lurch into the new set.</para>
    /// </summary>
    public void ReapplyFocus(int? focusBlockIndex) => ReapplyFocus(focusBlockIndex, null);

    /// <summary>
    /// As <see cref="ReapplyFocus(int?)"/>, but first reseats onto <paramref name="analysis"/> when it is
    /// a different instance than the one currently seated (issue #81 item G). Used when a host pins
    /// <see cref="Viewport.Focus"/> while the rail holds a STALE same-page analysis instance — a different
    /// table/cell-nav variant, or a re-analysis that replaced the cache entry. Collapsing against the stale
    /// instance would rebuild the navigable set from the wrong blocks (and the plain overload keys off
    /// whatever the rail still holds, possibly even a previous page's analysis), so swap in the resident
    /// analysis FIRST, then collapse. The stored navigable role set is reused, so callers needn't re-supply
    /// it. Top-level block indices are param-invariant for a page, so the cursor still follows its page-block
    /// across the swap. No-op (delegates straight through) when <paramref name="analysis"/> is null or equal
    /// to the seated one. <paramref name="analysis"/> must be the current page's analysis.
    /// </summary>
    public void ReapplyFocus(int? focusBlockIndex, PageAnalysis? analysis)
    {
        if (_analysis is null && analysis is null) return;
        // Swap in the resident analysis before reading the cursor/rebuilding, so the collapse keys off the
        // right page's blocks. Reuses _navigableRoles (the full-set restore path), so confinement collapse
        // and un-confine expand both behave as for a same-instance ReapplyFocus.
        if (analysis is not null && !ReferenceEquals(_analysis, analysis))
            _analysis = analysis;
        if (_analysis is null) return;

        // Remember the page-block the cursor is on, so we can follow it into the rebuilt index set.
        int currentPageBlock = _navigableIndices.Count > 0 && CurrentBlock >= 0 && CurrentBlock < _navigableIndices.Count
            ? _navigableIndices[CurrentBlock]
            : -1;

        RebuildNavigableIndices(focusBlockIndex);
        BuildChunks();

        // Clear in-flight animation/bias — the set just changed under it; leaving it running lurches the
        // line toward an offset for a block that may no longer be in the set (mirrors SetAnalysis's reset).
        _snap = null;
        _scroll.Stop();
        ScrollSpeed = 0.0;
        VerticalBias = 0;
        // A Focus (re)pin/clear establishes its own rail state — end any forced ("start rail here") low-zoom
        // session so it can't leak across the confine/un-confine cycle (e.g. leaving rail stuck active below
        // threshold after un-pinning). Mirrors SetAnalysis's reset; rail Active is re-derived by the caller's
        // UpdateRailZoom against the (possibly raised) post-clamp zoom.
        _forceActive = false;

        if (_navigableIndices.Count == 0) { CurrentBlock = 0; CurrentLine = 0; return; }
        int pos = currentPageBlock >= 0 ? _navigableIndices.IndexOf(currentPageBlock) : -1;
        if (pos < 0 && currentPageBlock >= 0)
        {
            // The remembered page-block isn't in the rebuilt navigable set (e.g. un-pinning a focus that
            // was a NON-navigable block like a figure, so it drops out of the restored set). Land on the
            // navigable block nearest it in PAGE order — reusing the old subset ordinal (CurrentBlock)
            // here would index an unrelated block, since the two sets number their entries differently.
            pos = _navigableIndices.Count - 1;
            for (int i = 0; i < _navigableIndices.Count; i++)
                if (_navigableIndices[i] >= currentPageBlock) { pos = i; break; }
        }
        CurrentBlock = pos >= 0 ? pos : Math.Min(CurrentBlock, _navigableIndices.Count - 1);
        if (CurrentBlock < 0) CurrentBlock = 0;
        if (CurrentLine >= CurrentNavigableBlock.Lines.Count)
            CurrentLine = Math.Max(0, CurrentNavigableBlock.Lines.Count - 1);

        // When confining (collapsing to the focus block), pin the restored cursor so that if the camera
        // fit raises the zoom across the rail threshold — activating rail as a side effect of pinning —
        // UpdateZoom honours this block/line instead of falling to FindNearestBlock, which would reset
        // CurrentLine to 0 and discard the reading position we just preserved. (For an already-active or
        // never-activating rail the pin is harmless: it is consumed only on an inactive→active transition
        // and cleared on deactivate.)
        if (focusBlockIndex is { } fbi && fbi >= 0)
        {
            _pinnedActivationBlock = CurrentBlock;
            _pinnedActivationLine = CurrentLine;
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
        // Rail normally engages only above the zoom threshold. A forced activation
        // (ActivateRailAt — "start rail here" at any magnification) keeps it active
        // below the threshold so readers who don't want to magnify can still rail-read.
        bool shouldBeActive = (zoom >= _config.RailZoomThreshold || _forceActive) && HasAnalysis;

        // Once the user has zoomed to/above the threshold, normal rail rules take over: consume the
        // force flag so a later zoom-out below the threshold deactivates rail as usual (otherwise a
        // forced session would stay sticky-on at every zoom until Escape).
        if (zoom >= _config.RailZoomThreshold) _forceActive = false;

        if (shouldBeActive && !Active)
        {
            Active = true;
            if (_pinnedActivationBlock is { } pinned)
            {
                // An explicit framing (e.g. SmoothlyFrameBlock) pinned the target block;
                // honour it instead of geometric nearest-block selection, so overlapping
                // bboxes under the focus point can't redirect to a different block. Keep
                // the pinned line too (clamped to the block) so a framing aimed at a
                // specific line lands there rather than snapping back to line 0.
                CurrentBlock = Math.Min(pinned, Math.Max(0, _navigableIndices.Count - 1));
                CurrentLine = Math.Clamp(_pinnedActivationLine, 0, Math.Max(0, CurrentNavigableBlock.Lines.Count - 1));
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

    // Line within the pinned block to seat on activation. Captured alongside
    // _pinnedActivationBlock so a framing that targets a specific line (not just line 0)
    // keeps that line through the threshold crossing. Only read when the block pin is set.
    private int _pinnedActivationLine;

    // When set, rail stays active regardless of zoom (see UpdateZoom). Engaged by
    // ForceActivateAt so a reader can rail-read at any magnification; cleared by
    // ClearForceActive or any explicit Deactivate.
    private bool _forceActive;

    /// <summary>True when rail was forced active below the zoom threshold via
    /// <see cref="ForceActivateAt"/> (and hasn't been cleared). The shell uses this to
    /// reflect the "start rail here" toggle and to know an Escape should release it.</summary>
    public bool ForceActive => _forceActive;

    /// <summary>Pin the current block and line so the next rail activation keeps them
    /// seated instead of running geometric nearest-block selection.</summary>
    public void PinCurrentBlockForActivation()
    {
        _pinnedActivationBlock = CurrentBlock;
        _pinnedActivationLine = CurrentLine;
    }

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
        _forceActive = false;
    }

    /// <summary>
    /// Force rail active at <paramref name="pageX"/>/<paramref name="pageY"/> (page-point
    /// space) regardless of the current zoom, seating the nearest navigable block and the
    /// line under the point (via <see cref="FindBlockNearPoint"/>). Lets a reader start
    /// rail-reading at any magnification — the camera is left where it is; the caller may
    /// snap afterwards. No-op when no analysis is seated.
    /// </summary>
    public void ForceActivateAt(double pageX, double pageY)
    {
        if (_analysis is null || _navigableIndices.Count == 0) return;
        _forceActive = true;
        Active = true;
        _pinnedActivationBlock = null;
        FindBlockNearPoint(pageX, pageY); // seats CurrentBlock + CurrentLine
        VerticalBias = 0;
        _snap = null;
        _scroll.Stop();
        ScrollSpeed = 0.0;
    }

    /// <summary>Release a forced activation. Rail stays active only if the zoom is at or
    /// above the threshold the next time <see cref="UpdateZoom"/> runs; the caller should
    /// re-evaluate (e.g. via the controller's UpdateRailZoom) to deactivate immediately.</summary>
    public void ClearForceActive() => _forceActive = false;

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
    /// the next line would trigger. Backward (previous line) uses the framed-unit's left
    /// edge. Horizontal panning of wide content is unaffected — <see cref="ClampX"/> and this
    /// method share <see cref="GetFramingBounds"/> (the chunk for prose, the block's own bounds
    /// for a self-framed equation/algorithm) so over-scroll stays bounded by the framed unit.
    /// <c>internal</c> for direct boundary testing.
    /// </summary>
    internal bool IsAtHardEdge(double cameraX, double zoom, double windowWidth, ScrollDirection dir)
    {
        if (_navigableIndices.Count == 0) return false;
        var (blockLeft, _, blockWidthPx) = GetFramingBounds(zoom);

        // If the whole framed unit fits in the window it is centred and cannot scroll at all.
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

        var (blockLeft, blockRight, blockWidthPx) = GetFramingBounds(zoom);

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
    /// queries — landing on line <paramref name="line"/> (clamped to the block's line
    /// range; defaults to the first line) and clearing vertical bias. Returns false if
    /// that block is not in the navigable subset (e.g. a non-navigable role), leaving
    /// the cursor unchanged.
    /// </summary>
    public bool TrySetCurrentByPageIndex(int pageBlockIndex, int line = 0)
    {
        int navPos = _navigableIndices.IndexOf(pageBlockIndex);
        if (navPos < 0) return false;
        CurrentBlock = navPos;
        CurrentLine = Math.Clamp(line, 0, Math.Max(0, CurrentNavigableBlock.Lines.Count - 1));
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
