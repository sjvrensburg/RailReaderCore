using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Tick (animation frame logic) ---

    /// <summary>
    /// Advance one animation frame. Returns what needs repainting.
    /// </summary>
    public TickResult Tick(double dt)
    {
        dt = Math.Min(dt, 1.0 / 30.0);

        var doc = ActiveDocument;
        if (doc is null) return default;

        var (ww, wh) = GetViewportSize();
        bool cameraChanged = false;
        bool pageChanged = false;
        bool overlayChanged = false;
        bool animating = false;

        TickZoomAnimation(doc, ww, wh, ref cameraChanged, ref animating);

        if (!RailPaused)
            TickRailSnap(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Snap Y to integer pixel when rail mode is stable or nearly so.
        // Snapping during the snap animation tail (progress > 0.95) eliminates
        // the last few frames of sub-pixel text shimmer before full stop.
        if (_config.PixelSnapping && doc.Rail.Active
            && (!animating || doc.Rail.SnapProgress > 0.95))
        {
            double snapped = Math.Round(doc.Camera.OffsetY);
            if (snapped != doc.Camera.OffsetY)
            {
                doc.Camera.OffsetY = snapped;
                cameraChanged = true;
            }
        }

        if (!RailPaused)
            TickAutoScroll(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Decay zoom blur speed
        if (doc.Camera.ZoomSpeed > 0)
        {
            doc.Camera.DecayZoomSpeed(dt);
            animating = true;
            if (doc.Camera.ZoomSpeed > 0)
                cameraChanged = true;
        }

        // Poll analysis results
        var (gotResults, needsAnim, gotPageChange) = PollAnalysisResults();
        animating |= needsAnim;
        overlayChanged |= gotResults;
        pageChanged |= gotPageChange;

        if (!animating)
        {
            if (!doc.SubmitPendingLookahead(_worker)
                && !doc.Rail.Active
                && _worker is not null && _worker.IsIdle)
                TrySubmitBackgroundReadAhead();
        }

        // DPI bitmap swap
        if (doc.DpiRenderReady)
        {
            doc.DpiRenderReady = false;
            pageChanged = true;
        }

        // Retry any DPI re-render that was skipped because scroll was active.
        // UpdateRenderDpiIfNeeded gates on Rail.ScrollSpeed/AutoScrolling and
        // does nothing if the cached DPI is already within hysteresis, so this
        // poll is cheap. Also poll while a render-quality change is pending
        // (RenderDpiPending) even mid-animation — otherwise a preset change made
        // during auto-scroll (which keeps `animating` true) would never apply to
        // the current page until scrolling stopped entirely.
        if (!animating || doc.RenderDpiPending)
            doc.UpdateRenderDpiIfNeeded();

        return new TickResult(cameraChanged, pageChanged, overlayChanged, false, false, animating);
    }

    /// <summary>Smooth zoom animation step (delegated to ZoomAnimationController).</summary>
    private void TickZoomAnimation(DocumentState doc, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        _zoom.Tick(doc, ww, wh, ref cameraChanged, ref animating);
    }

    /// <summary>Rail snap animation and edge-hold line advance (skipped while zoom is animating).</summary>
    private void TickRailSnap(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (!_zoom.IsAnimating)
        {
            double cx = doc.Camera.OffsetX, cy = doc.Camera.OffsetY;
            bool railAnimating = doc.Rail.Tick(ref cx, ref cy, dt, doc.Camera.Zoom, ww);
            if (cx != doc.Camera.OffsetX || cy != doc.Camera.OffsetY)
            {
                doc.Camera.OffsetX = cx;
                doc.Camera.OffsetY = cy;
                cameraChanged = true;
            }
            animating |= railAnimating;

            if (doc.Rail.ConsumeAutoScrollTrigger())
            {
                _autoScroll.ActivateAutoScroll();
                StatusMessage?.Invoke("Auto-scroll activated");
            }

            HandleEdgeAdvance(doc, ww, wh, ref pageChanged, ref cameraChanged, ref overlayChanged);
        }
    }

    /// <summary>
    /// Handles edge-hold line advances: D/Right held at line end → NextLine;
    /// A/Left held at line start → PrevLine.
    /// </summary>
    private void HandleEdgeAdvance(DocumentState doc, double ww, double wh,
        ref bool pageChanged, ref bool cameraChanged, ref bool overlayChanged)
    {
        if (doc.Rail.AutoScrolling) return;
        if (doc.Rail.ConsumePendingEdgeAdvance() is not { } edgeDir) return;

        bool forward = edgeDir == ScrollDirection.Forward;
        var adv = AdvanceLine(doc, forward, ww, wh);
        if (adv is LineAdvanceResult.PageChanged or LineAdvanceResult.PageChangedRailLost)
        {
            FirePageChanged(ref pageChanged, doc.CurrentPage);
            if (!forward && adv == LineAdvanceResult.PageChanged)
                doc.StartSnapToEnd(ww, wh);
        }
        else if (adv == LineAdvanceResult.LineAdvanced)
        {
            if (forward) doc.StartSnap(ww, wh);
            else doc.StartSnapToEnd(ww, wh);
        }
        overlayChanged = true;
        cameraChanged = true;
        if (adv is LineAdvanceResult.LineAdvanced or LineAdvanceResult.PageChanged)
            FireReadingPositionChanged();
    }

    /// <summary>Auto-scroll tick: advance along the current line, then advance to the next line/page.</summary>
    private void TickAutoScroll(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (doc.Rail.AutoScrolling)
        {
            if (doc.Rail.NavigableCount > 0
                && doc.Rail.CurrentBlock >= doc.Rail.NavigableCount - 2
                && doc.CurrentPage + 1 < doc.PageCount)
            {
                doc.PrefetchPage(doc.CurrentPage + 1);
            }

            double cx = doc.Camera.OffsetX;
            bool reachedEnd = doc.Rail.TickAutoScroll(ref cx, dt, doc.Camera.Zoom, ww);
            if (cx != doc.Camera.OffsetX)
            {
                doc.Camera.OffsetX = cx;
                cameraChanged = true;
            }
            animating = true;

            if (reachedEnd)
            {
                int prevBlock = doc.Rail.CurrentBlock;
                int prevLine = doc.Rail.CurrentLine;
                int prevChunk = doc.Rail.CurrentChunk;
                var adv = AdvanceLine(doc, forward: true, ww, wh);
                switch (adv)
                {
                    case LineAdvanceResult.PageChanged:
                        FirePageChanged(ref pageChanged, doc.CurrentPage);
                        FireReadingPositionChanged();
                        doc.Rail.StartAutoScroll(_autoScroll.AutoScrollSpeed);
                        doc.Rail.PauseAutoScroll(GetBlockEntryPause(doc));
                        break;
                    case LineAdvanceResult.PageChangedRailLost:
                        FirePageChanged(ref pageChanged, doc.CurrentPage);
                        StopAutoScroll();
                        break;
                    case LineAdvanceResult.LineAdvanced:
                        if (doc.Rail.CurrentBlock == prevBlock && doc.Rail.CurrentLine == prevLine)
                        {
                            StopAutoScroll();
                            break;
                        }
                        doc.StartSnap(ww, wh);
                        // Heavier role-based entry pause only when crossing into a NEW
                        // chunk (new column/section); crossing block boundaries within a
                        // column reads continuously (no stutter). But always reset the
                        // per-block dwell on ANY block change so each fit-in-window block
                        // still gets its settling dwell.
                        bool enteredNewChunk = doc.Rail.CurrentChunk != prevChunk;
                        bool enteredNewBlock = doc.Rail.CurrentBlock != prevBlock;
                        doc.Rail.PauseAutoScroll(enteredNewChunk ? GetBlockEntryPause(doc) : 0,
                            resetDwell: enteredNewBlock);
                        FireReadingPositionChanged();
                        break;
                }
                overlayChanged = true;
            }
        }
    }

    /// <summary>
    /// Returns the auto-scroll pause duration for entering the current block,
    /// based on its class (equation, header, or default).
    /// </summary>
    private double GetBlockEntryPause(DocumentState doc) =>
        doc.Rail.CurrentNavigableBlock.Role switch
        {
            BlockRole.DisplayMath or BlockRole.Algorithm => _config.AutoScrollEquationPauseMs,
            BlockRole.Title or BlockRole.Heading => _config.AutoScrollHeaderPauseMs,
            _ => _config.AutoScrollLinePauseMs,
        };

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
                        ApplySkipLanding(doc, pendingSkip.Forward, pendingSkip.SavedVerticalBias);
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
                        if (TryResumeSkip(doc, ww, wh))
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
