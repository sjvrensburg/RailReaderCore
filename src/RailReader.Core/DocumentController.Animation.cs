using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Tick (animation frame logic) ---

    /// <summary>
    /// Advance one animation frame for the focused viewport. Returns what needs repainting.
    /// Facade over <see cref="TickViewport"/> — retained as the single-viewport entry point
    /// through Phase 2 (see <c>docs/multi-viewport-design.md</c> §7). A multi-viewport host
    /// drives each on-screen viewport's <see cref="TickViewport"/> from its own frame callback.
    /// </summary>
    public TickResult Tick(double dt)
    {
        if (FocusedViewport is not { } vp) return default;
        return TickViewport(vp, dt);
    }

    /// <summary>
    /// Advance one animation frame for a specific <paramref name="vp"/>: its camera/rail/zoom/
    /// auto-scroll animation and DPI bitmap swap, plus the global analysis pump. Operates on the
    /// passed viewport throughout, so a second viewport animates independently of the first. A
    /// multi-viewport host drives each visible viewport's tick from its own frame callback.
    /// <para>Note: this still runs <see cref="PumpAnalysis"/> internally (preserving the single-
    /// viewport <see cref="Tick(double)"/> behaviour exactly). A host ticking several viewports per
    /// frame therefore pumps once per view — harmless (the worker drains on the first), but the
    /// pump-once-globally split is a Phase 2 frame-loop concern.</para>
    /// </summary>
    public TickResult TickViewport(Viewport vp, double dt)
    {
        dt = Math.Min(dt, 1.0 / 30.0);

        var (ww, wh) = GetViewportSize();
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

        // Drain the analysis worker and schedule read-ahead (global, once per frame).
        // `quiescent: !animating` preserves the prior gate: read-ahead only when neither
        // the camera nor a just-arrived result is animating.
        var (gotResults, needsAnim, gotPageChange) = PumpAnalysis(quiescent: !animating);
        animating |= needsAnim;
        overlayChanged |= gotResults;
        pageChanged |= gotPageChange;

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
            FirePageChanged(ref pageChanged, vp.Owner.CurrentPage);
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
            FireReadingPositionChanged();
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
                && vp.Owner.CurrentPage + 1 < vp.Owner.PageCount)
            {
                vp.PrefetchPage(vp.Owner.CurrentPage + 1);
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
                        FirePageChanged(ref pageChanged, vp.Owner.CurrentPage);
                        FireReadingPositionChanged();
                        // A page boundary is always a stop unit: restart auto-scroll on the
                        // new page, then park on entry (after the skip-landing snap settles).
                        vp.Rail.StartAutoScroll(vp.AutoScroll.AutoScrollSpeed);
                        vp.Rail.ParkAutoScroll();
                        break;
                    case LineAdvanceResult.PageChangedRailLost:
                        FirePageChanged(ref pageChanged, vp.Owner.CurrentPage);
                        StopAutoScroll();
                        break;
                    case LineAdvanceResult.LineAdvanced:
                        if (vp.Rail.CurrentBlock == prevBlock && vp.Rail.CurrentLine == prevLine)
                        {
                            StopAutoScroll();
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
                        FireReadingPositionChanged();
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

        if (quiescent && !needsAnim && ActiveDocument is { } doc)
        {
            if (!doc.SubmitPendingLookahead(_worker)
                && !doc.Rail.Active
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
        var (ww, wh) = GetViewportSize();
        while (_worker.Poll() is { } result)
        {
            got = true;
            _logger.Debug($"[Analysis] Got result for {Path.GetFileName(result.FilePath)} page {result.Page}: {result.Analysis.Blocks.Count} blocks");
            bool matchedLiveDoc = false;
            foreach (var doc in Documents)
            {
                if (doc.IsDisposed || doc.FilePath != result.FilePath) continue;
                matchedLiveDoc = true;

                doc.SetAnalysis(result.Page, result.Analysis);

                if (doc.CurrentPage != result.Page)
                    continue;

                if (!doc.PendingRailSetup)
                    continue;

                // Visual side effects (event fires, page-change / needs-animation
                // flags) must only reflect the ACTIVE document — a background tab's
                // analysis completing must not fire events for, or repaint, the tab
                // the user is actually looking at.
                bool isActive = doc == ActiveDocument;
                int prevPage = doc.CurrentPage;

                doc.Rail.SetAnalysis(result.Analysis, _config.NavigableRoles);
                doc.PendingRailSetup = false;
                doc.UpdateRailZoom(ww, wh);
                _logger.Debug($"[Analysis] Rail has {doc.Rail.NavigableCount} navigable blocks, Active={doc.Rail.Active}");
                if (doc.Rail.Active)
                {
                    if (doc.PendingSkip is { } pendingSkip)
                        ApplySkipLanding(doc.Primary, pendingSkip.Forward, pendingSkip.SavedVerticalBias);
                    doc.PendingSkip = null;
                    doc.StartSnap(ww, wh);
                    if (isActive)
                    {
                        needsAnim = true;
                        FireReadingPositionChanged();
                    }
                }
                else if (doc.PendingSkip is not null)
                {
                    if (isActive)
                    {
                        if (TryResumeSkip(doc.Primary, ww, wh))
                        {
                            needsAnim = true;
                            FireReadingPositionChanged();
                        }
                    }
                    else
                        doc.PendingSkip = null;
                }

                // Single chokepoint for the PageChanged event: announce a transition
                // exactly once, and only if the active document's page actually moved
                // during this resolution (a deferred skip advancing to a navigable
                // page — including via re-deferral inside TryResumeSkip, which calls
                // doc.GoToPage without firing the event). ApplySkipLanding does not
                // change the page, so a same-page completion (already announced when
                // the skip first deferred) does not fire again.
                if (isActive && doc.CurrentPage != prevPage)
                    FirePageChanged(ref pageChanged, doc.CurrentPage);
            }

            // Fire once per result, but only when a live document actually owns it
            // (a result for a closed/disposed document must not notify subscribers).
            if (matchedLiveDoc)
                AnalysisPageReady?.Invoke(result.Page);
        }
        return (got, needsAnim, pageChanged);
    }

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
