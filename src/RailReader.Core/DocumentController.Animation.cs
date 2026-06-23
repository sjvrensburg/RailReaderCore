using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Tick (animation frame logic) ---

    /// <summary>
    /// Advance one animation frame for a specific <paramref name="vp"/>: its camera/rail/zoom/
    /// auto-scroll animation and DPI bitmap swap, plus the global analysis pump. Operates on the
    /// passed viewport throughout, so a second viewport animates independently of the first.
    /// <para>Pumps analysis. A multi-viewport host that ticks several views per frame should instead
    /// pump once via the <see cref="TickViewport(Viewport,double,bool)"/> overload — see §5.5.</para>
    /// </summary>
    public TickResult TickViewport(Viewport vp, double dt) => TickViewport(vp, dt, pumpAnalysis: true);

    /// <summary>
    /// Advance one animation frame for <paramref name="vp"/>, with explicit control over the global
    /// analysis pump. The pump (worker drain + read-ahead scheduling) is document-global, not
    /// per-view, so a host driving N viewports per frame ticks one view with
    /// <paramref name="pumpAnalysis"/>=<c>true</c> (or calls <see cref="PumpAnalysis"/> once itself)
    /// and the rest with <c>false</c> — the worker would otherwise be drained redundantly on each
    /// view (harmless, but wasteful).
    /// </summary>
    public TickResult TickViewport(Viewport vp, double dt, bool pumpAnalysis)
    {
        dt = Math.Min(dt, 1.0 / 30.0);

        // Per-view geometry: animate/clamp THIS view against its OWN size. Every viewport (primary or
        // detached) is sized by the host via Viewport.SetSize; there is no controller-level ambient
        // size any more (the single-viewport facade was removed in Phase 3).
        var (ww, wh) = (vp.Width, vp.Height);
        // Free-pan pause is this view's own state and can't change within a tick; read it once.
        bool railPaused = vp.RailPause is not null;
        bool cameraChanged = false;
        bool pageChanged = false;
        bool overlayChanged = false;
        bool animating = false;

        TickZoomAnimation(vp, ww, wh, ref cameraChanged, ref animating);

        if (!railPaused)
            TickRailSnap(vp, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Snap Y to integer pixel when rail mode is stable or nearly so.
        // Snapping during the snap animation tail (progress > 0.95) eliminates
        // the last few frames of sub-pixel text shimmer before full stop.
        if (_config.PixelSnapping && vp.Rail.Active
            && (!animating || vp.Rail.SnapProgress > 0.95))
        {
            double snapped = Math.Round(vp.Camera.OffsetY);
            if (snapped != vp.Camera.OffsetY)
            {
                vp.Camera.OffsetY = snapped;
                cameraChanged = true;
            }
        }

        if (!railPaused)
            TickAutoScroll(vp, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Decay zoom blur speed
        if (vp.Camera.ZoomSpeed > 0)
        {
            vp.Camera.DecayZoomSpeed(dt);
            animating = true;
            if (vp.Camera.ZoomSpeed > 0)
                cameraChanged = true;
        }

        // Drain the analysis worker and schedule read-ahead (document-global, once per frame).
        // `quiescent: !animating` preserves the prior gate: read-ahead only when neither
        // the camera nor a just-arrived result is animating. A multi-viewport host pumps
        // once per frame (not once per view) by passing pumpAnalysis:false on the others.
        if (pumpAnalysis)
        {
            var (gotResults, needsAnim, gotPageChange) = PumpAnalysis(quiescent: !animating);
            animating |= needsAnim;
            overlayChanged |= gotResults;
            pageChanged |= gotPageChange;
        }

        // DPI bitmap swap
        if (vp.DpiRenderReady)
        {
            vp.DpiRenderReady = false;
            pageChanged = true;
        }

        // Retry any DPI re-render that was skipped because scroll was active.
        // UpdateRenderDpiIfNeeded gates on Rail.ScrollSpeed/AutoScrolling and
        // does nothing if the cached DPI is already within hysteresis, so this
        // poll is cheap. Also poll while a render-quality change is pending
        // (RenderDpiDirty) even mid-animation — otherwise a preset change made
        // during auto-scroll (which keeps `animating` true) would never apply to
        // the current page until scrolling stopped entirely.
        if (!animating || vp.RenderDpiDirty)
            vp.UpdateRenderDpiIfNeeded();

        return new TickResult(cameraChanged, pageChanged, overlayChanged, false, false, animating);
    }

    /// <summary>Smooth zoom animation step (delegated to ZoomAnimationController).</summary>
    private void TickZoomAnimation(Viewport vp, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        vp.Zoom.Tick(vp, ww, wh, ref cameraChanged, ref animating);
    }

    /// <summary>Rail snap animation and edge-hold line advance (skipped while zoom is animating).</summary>
    private void TickRailSnap(Viewport vp, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (!vp.Zoom.IsAnimating)
        {
            double cx = vp.Camera.OffsetX, cy = vp.Camera.OffsetY;
            bool railAnimating = vp.Rail.Tick(ref cx, ref cy, dt, vp.Camera.Zoom, ww);
            if (cx != vp.Camera.OffsetX || cy != vp.Camera.OffsetY)
            {
                vp.Camera.OffsetX = cx;
                vp.Camera.OffsetY = cy;
                cameraChanged = true;
            }
            animating |= railAnimating;

            if (vp.Rail.ConsumeAutoScrollTrigger())
            {
                vp.AutoScroll.ActivateAutoScroll();
                StatusMessage?.Invoke("Auto-scroll activated");
            }

            HandleEdgeAdvance(vp, ww, wh, ref pageChanged, ref cameraChanged, ref overlayChanged);
        }
    }

    /// <summary>
    /// Handles edge-hold line advances: D/Right held at line end → NextLine;
    /// A/Left held at line start → PrevLine.
    /// </summary>
    private void HandleEdgeAdvance(Viewport vp, double ww, double wh,
        ref bool pageChanged, ref bool cameraChanged, ref bool overlayChanged)
    {
        if (vp.Rail.AutoScrolling) return;
        if (vp.Rail.ConsumePendingEdgeAdvance() is not { } edgeDir) return;

        bool forward = edgeDir == ScrollDirection.Forward;
        var adv = AdvanceLine(vp, forward, ww, wh);
        if (adv is LineAdvanceResult.PageChanged or LineAdvanceResult.PageChangedRailLost)
        {
            FirePageChanged(ref pageChanged, vp);
            if (!forward && adv == LineAdvanceResult.PageChanged)
                vp.StartSnapToEnd(ww, wh);
        }
        else if (adv == LineAdvanceResult.LineAdvanced)
        {
            if (forward) vp.StartSnap(ww, wh);
            else vp.StartSnapToEnd(ww, wh);
        }
        overlayChanged = true;
        cameraChanged = true;
        if (adv is LineAdvanceResult.LineAdvanced or LineAdvanceResult.PageChanged)
            FireReadingPositionChanged(vp);
    }

    /// <summary>Auto-scroll tick: advance along the current line, then advance to the next line/page.</summary>
    private void TickAutoScroll(Viewport vp, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (vp.Rail.AutoScrolling)
        {
            // Parked (semi-auto): indefinite wait for an explicit advance keypress. No camera
            // motion and no forced animation — let the render loop idle until the desktop
            // calls ResumeAutoScrollFromPark. Pan/zoom/inspect compose via the other paths.
            if (vp.Rail.AutoScrollParked)
                return;

            if (vp.Rail.NavigableCount > 0
                && vp.Rail.CurrentBlock >= vp.Rail.NavigableCount - 2
                && vp.CurrentPage + 1 < vp.Owner.PageCount)
            {
                // Prefetch THIS view's next page into its own buffer — vp.Owner.CurrentPage is the
                // Primary facade and would prefetch the primary's next page for a secondary view.
                vp.PrefetchPage(vp.CurrentPage + 1);
            }

            double cx = vp.Camera.OffsetX;
            bool reachedEnd = vp.Rail.TickAutoScroll(ref cx, dt, vp.Camera.Zoom, ww);
            if (cx != vp.Camera.OffsetX)
            {
                vp.Camera.OffsetX = cx;
                cameraChanged = true;
            }
            animating = true;

            if (reachedEnd)
            {
                int prevBlock = vp.Rail.CurrentBlock;
                int prevLine = vp.Rail.CurrentLine;
                int prevChunk = vp.Rail.CurrentChunk;
                var adv = AdvanceLine(vp, forward: true, ww, wh);
                switch (adv)
                {
                    case LineAdvanceResult.PageChanged:
                        FirePageChanged(ref pageChanged, vp);
                        FireReadingPositionChanged(vp);
                        // A page boundary is always a stop unit: restart auto-scroll on the
                        // new page, then park on entry (after the skip-landing snap settles).
                        vp.Rail.StartAutoScroll(vp.AutoScroll.AutoScrollSpeed);
                        vp.Rail.ParkAutoScroll();
                        break;
                    case LineAdvanceResult.PageChangedRailLost:
                        FirePageChanged(ref pageChanged, vp);
                        vp.AutoScroll.StopAutoScroll(vp);
                        break;
                    case LineAdvanceResult.LineAdvanced:
                        if (vp.Rail.CurrentBlock == prevBlock && vp.Rail.CurrentLine == prevLine)
                        {
                            vp.AutoScroll.StopAutoScroll(vp);
                            break;
                        }
                        vp.StartSnap(ww, wh);
                        // Park on ENTRY to a stop unit: a new chunk (column/section break), or a
                        // newly-entered stop-role block (non-prose). One stop per unit — the
                        // stop-role check is gated on a block change so the remaining lines of a
                        // multi-line stop block flow after the reader resumes (and leaving a stop
                        // block into prose does not park). Otherwise prose flows on: defer-resume
                        // after the snap completes (PauseAutoScroll(0) waits for the snap, then
                        // recaptures the scroll origin from the settled target) so auto-scroll
                        // doesn't fight the line snap.
                        bool shouldPark = ShouldParkOnLineAdvance(
                            enteredNewChunk: vp.Rail.CurrentChunk != prevChunk,
                            enteredNewBlock: vp.Rail.CurrentBlock != prevBlock,
                            newRole: vp.Rail.CurrentNavigableBlock.Role,
                            _config.AutoScrollStopClasses);
                        if (shouldPark)
                            vp.Rail.ParkAutoScroll();
                        else
                            vp.Rail.PauseAutoScroll(0);
                        FireReadingPositionChanged(vp);
                        break;
                }
                overlayChanged = true;
            }
        }
    }

    /// <summary>
    /// Semi-auto park decision on a line advance (page changes always park separately). Parks
    /// on entry to a stop unit: a new chunk (column/section break), or a newly-entered
    /// stop-role block (a non-prose role in <see cref="CoreSettings.AutoScrollStopClasses"/>).
    /// The stop-role check is gated on a block change so the remaining lines of a multi-line
    /// stop block flow after the reader resumes (one stop per unit, on entry) and leaving a
    /// stop block into prose does not park. <c>internal static</c> for direct testing.
    /// </summary>
    internal static bool ShouldParkOnLineAdvance(
        bool enteredNewChunk, bool enteredNewBlock, BlockRole newRole, IReadOnlySet<BlockRole> stopClasses)
        => enteredNewChunk || (enteredNewBlock && stopClasses.Contains(newRole));

    /// <summary>
    /// The global analysis pump: drain the worker (fanning results out to the matching
    /// documents) and, when the frame is otherwise idle, schedule lookahead / background
    /// read-ahead. Designed to be called <em>once per frame</em> regardless of how many
    /// viewports tick (the worker and its read-ahead are global). <paramref name="quiescent"/>
    /// is whether the caller's frame has no in-progress camera animation; read-ahead is
    /// suppressed unless quiescent and no freshly-arrived result needs animating (so a
    /// PDFium re-render can't jump the gate mid-scroll). Returns the same flags as
    /// <see cref="PollAnalysisResults"/> so a facade <see cref="Tick"/> can fold them in.
    /// </summary>
    public (bool GotResults, bool NeedsAnimation, bool PageChanged) PumpAnalysis(bool quiescent = true)
    {
        var (gotResults, needsAnim, gotPageChange) = PollAnalysisResults();

        if (quiescent && !needsAnim && ActiveDocument is { } doc && FocusedViewport is { } focus)
        {
            // Eager lookahead is per-view (§5.5): each viewport owns its PendingAnalysis queue, filled
            // when it rail-navigates across a page. Drain the focused view first, then the doc's other
            // views; the single worker takes one page per pump, so stop as soon as one bites. (The old
            // code drained only Primary's queue, so a non-primary view's lookahead never fired.)
            bool submitted = doc.SubmitPendingLookahead(focus, _worker);
            if (!submitted)
                foreach (var vp in doc.Viewports)
                {
                    if (ReferenceEquals(vp, focus)) continue;
                    if (doc.SubmitPendingLookahead(vp, _worker)) { submitted = true; break; }
                }

            // Otherwise spend idle worker cycles pre-analysing background pages — but only when the
            // focused view is neither actively rail-reading nor still waiting on its own page (else
            // background work would contend with the analysis the user is actually waiting for).
            if (!submitted && !focus.Rail.Active && !focus.PendingRailSetup
                && _worker is not null && _worker.IsIdle)
                TrySubmitBackgroundReadAhead();
        }

        return (gotResults, needsAnim, gotPageChange);
    }

    /// <summary>
    /// Poll the analysis worker for completed results. Can also be called
    /// from a low-frequency timer when not animating.
    /// </summary>
    public (bool GotResults, bool NeedsAnimation, bool PageChanged) PollAnalysisResults()
    {
        bool got = false;
        bool needsAnim = false;
        bool pageChanged = false;
        if (_worker is null) return (false, false, false);
        while (_worker.Poll() is { } result)
        {
            got = true;
            _logger.Debug($"[Analysis] Got result for {Path.GetFileName(result.FilePath)} page {result.Page}: {result.Analysis.Blocks.Count} blocks");
            bool matchedLiveDoc = false;
            foreach (var doc in Documents)
            {
                if (doc.IsDisposed || doc.FilePath != result.FilePath) continue;
                matchedLiveDoc = true;

                // Cache at the model level under the params it was produced with (railreader2#180 #3).
                doc.SetAnalysis(result.Page, result.Params, result.Analysis);

                // Fan out to every view of this document sitting on the analysed page, waiting for its
                // rail, AND whose post-processing params match this result (§5.4). Two views on the
                // same page each get seated independently; a view with different params keeps waiting
                // for its own variant.
                foreach (var vp in doc.Viewports)
                {
                    if (vp.CurrentPage != result.Page || !vp.PendingRailSetup
                        || vp.AnalysisParams != result.Params) continue;
                    ApplyAnalysisToViewport(vp, result.Analysis, ref needsAnim, ref pageChanged);
                }
            }

            // Fire once per result, but only when a live document actually owns it
            // (a result for a closed/disposed document must not notify subscribers).
            if (matchedLiveDoc)
                AnalysisPageReady?.Invoke(result.Page);
        }
        return (got, needsAnim, pageChanged);
    }

    /// <summary>
    /// Seats a freshly-arrived page analysis on one viewport — the §5.4 fan-out body. Sets the view's
    /// rail, clears its pending flag, applies any deferred skip-landing, and starts its snap. Visual
    /// side effects fire for a <see cref="IsViewportLive">live</see> view (the focused view, an active-
    /// document pane, or a host-shown detached pane): its own reading-position / page events fire, it
    /// resumes a deferred skip, and it is woken (focused → the controller tick's needs-animation flag;
    /// non-focused → its own <see cref="Viewport.RequestAnimation"/>). A background view is seated
    /// silently so it is ready when shown. The controller-level event facades and the tick's repaint
    /// flags reflect only the FOCUSED view.
    /// </summary>
    private void ApplyAnalysisToViewport(Viewport vp, PageAnalysis analysis,
        ref bool needsAnim, ref bool pageChanged)
    {
        bool focused = vp == FocusedViewport;
        bool live = IsViewportLive(vp);
        int prevPage = vp.CurrentPage;

        // Seat THIS view's rail against its OWN size (Finding 2): a cache-miss result arriving
        // asynchronously must centre/zoom/snap using the view's geometry, not whatever ambient size
        // the controller last ticked — otherwise a pane of a different width opens at the wrong rail
        // zoom / line position until a later resize re-clamps. (Cache-hit seating already runs through
        // the synchronous GoToPage/SubmitAnalysis path the host drives per-view.)
        var (ww, wh) = (vp.Width, vp.Height);

        vp.Rail.SetAnalysis(analysis, _config.NavigableRoles);
        vp.PendingRailSetup = false;
        vp.UpdateRailZoom(ww, wh);
        _logger.Debug($"[Analysis] Rail has {vp.Rail.NavigableCount} navigable blocks, Active={vp.Rail.Active}");

        if (vp.Rail.Active)
        {
            if (vp.PendingSkip is { } pendingSkip)
                ApplySkipLanding(vp, pendingSkip.Forward, pendingSkip.SavedVerticalBias);
            vp.PendingSkip = null;
            vp.StartSnap(ww, wh);
            if (live)
                WakeSeatedView(vp, focused, ref needsAnim);
        }
        else if (vp.PendingSkip is not null)
        {
            if (live)
            {
                if (TryResumeSkip(vp, ww, wh))
                    WakeSeatedView(vp, focused, ref needsAnim);
            }
            else
                vp.PendingSkip = null;
        }

        if (vp.CurrentPage != prevPage)
        {
            RaisePageChanged(vp);            // the view's own PageChanged; controller facade if focused
            if (focused) pageChanged = true; // only the focused view drives the controller tick repaint
        }
    }

    /// <summary>Announces a freshly-seated reading position and wakes the view: the focused view via the
    /// controller tick's <paramref name="needsAnim"/> flag, a live non-focused pane via its own
    /// <see cref="Viewport.RequestAnimation"/> hook.</summary>
    private void WakeSeatedView(Viewport vp, bool focused, ref bool needsAnim)
    {
        FireReadingPositionChanged(vp);
        if (focused) needsAnim = true;
        else vp.RequestAnimation?.Invoke();
    }

    /// <summary>
    /// Whether a view is "live" — worth firing events for and resuming its own deferred skip. True for
    /// the focused view; for any other view, true when the host has marked it visible
    /// (<see cref="Viewport.IsLive"/>) AND it is either a view of the active document (so an active-tab
    /// split pane stays live) or a non-primary detached pane. A background tab's PRIMARY is therefore
    /// never live on its own — you focus it to make it active — preserving the single-window rule that
    /// only the active document announces. The host refines this by toggling IsLive on its panes.
    /// </summary>
    private bool IsViewportLive(Viewport vp)
        => vp == FocusedViewport
           || (vp.IsLive && (vp.Owner == ActiveDocument || !ReferenceEquals(vp, vp.Owner.Primary)));

    /// <summary>
    /// Returns true if any document has unanalysed pages remaining.
    /// </summary>
    public bool HasBackgroundAnalysisWork
    {
        get
        {
            for (int i = 0; i < Documents.Count; i++)
            {
                var doc = Documents[i];
                if (!doc.IsDisposed && doc.HasPendingBackgroundWork)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Submits background analysis work when the lookahead queue is empty.
    /// Active document gets priority; other tabs are served round-robin.
    /// Can be called from the poll timer when the animation loop isn't running.
    /// </summary>
    public bool TrySubmitBackgroundReadAhead()
    {
        if (_worker is null) return false;

        var active = ActiveDocument;

        if (active is not null && !active.IsDisposed
            && active.SubmitBackgroundAnalysis(_worker))
            return true;

        for (int i = 0; i < Documents.Count; i++)
        {
            var doc = Documents[i];
            if (doc == active || doc.IsDisposed) continue;
            if (doc.SubmitBackgroundAnalysis(_worker))
                return true;
        }
        return false;
    }
}
