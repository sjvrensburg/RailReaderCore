using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Rail navigation ---

    // ConfinedHold is distinct from NoChange so the auto-scroll handler keys off an explicit
    // "held at a boundary it cannot cross" signal, not the generic no-op — see TickAutoScroll.
    // It covers both a block-confined (portal) viewport held at its focus-block edge AND a rail
    // boundary that produced no page change (document edge, or a failed page render): in all of
    // these nothing moved, so no PageChanged fires, auto-scroll stops, and edge-advance skips
    // its repaint flags.
    private enum LineAdvanceResult { NoChange, LineAdvanced, PageChanged, PageChangedRailLost, ConfinedHold }

    private LineAdvanceResult AdvanceLine(Viewport vp, bool forward, double ww, double wh)
    {
        var result = forward ? vp.Rail.NextLine() : vp.Rail.PrevLine();
        var boundary = forward ? NavResult.PageBoundaryNext : NavResult.PageBoundaryPrev;
        if (result == boundary)
        {
            // A block-confined (portal) viewport must not cross the page on a rail boundary — advancing
            // would leave the focus block's page, where the confinement (rail-set collapse + camera
            // clamp) no longer applies, letting the reader escape the block. Hold at the edge instead.
            // Gated on CurrentFocusBlockIndex (Focus set AND on the focus page), matching the camera
            // clamp / rail-set collapse / GetFitRect — so a stale off-page focus doesn't trap paging
            // where no confinement is actually in effect. Also drop any deferred skip so a late
            // TryResumeSkip can't page off the block after the guard already held it here.
            if (vp.CurrentFocusBlockIndex is not null)
            {
                vp.PendingSkip = null;
                return LineAdvanceResult.ConfinedHold;
            }

            return SkipToNavigablePage(vp, forward, 0, ww, wh) switch
            {
                SkipResult.FoundNavigable => LineAdvanceResult.PageChanged,
                SkipResult.Deferred => LineAdvanceResult.PageChangedRailLost,
                // Exhausted means NO page change happened (document edge, confined view, or a
                // failed page render — GoToPage restores the old page on failure). Reporting a
                // page change here made every keypress at the document edge re-fire PageChanged
                // to hosts (page indicators, AT announcements) for a page that never moved.
                // ConfinedHold is the existing "held at a boundary, nothing changed" signal:
                // no PageChanged, auto-scroll stops cleanly, no edge-advance repaint churn.
                _ => LineAdvanceResult.ConfinedHold,
            };
        }
        return result == NavResult.Ok ? LineAdvanceResult.LineAdvanced : LineAdvanceResult.NoChange;
    }

    private enum SkipResult { FoundNavigable, Deferred, Exhausted }

    /// <summary>
    /// Advance through pages in the given direction, skipping pages with no
    /// navigable blocks. Cached analysis is checked without rasterizing.
    /// If analysis is pending (async), stores skip state on the document for
    /// deferred continuation via <see cref="TryResumeSkip"/>.
    /// </summary>
    private SkipResult SkipToNavigablePage(Viewport vp, bool forward, int skipped, double ww, double wh)
    {
        // A confined (portal) viewport must never page off its focus block's page. AdvanceLine already
        // short-circuits before calling here, but the deferred-skip resume path (TryResumeSkip) reaches
        // SkipToNavigablePage directly — bail (and drop the skip) so it can't GoToPage off the block.
        if (vp.CurrentFocusBlockIndex is not null) { vp.PendingSkip = null; return SkipResult.Exhausted; }

        var doc = vp.Owner;
        int step = forward ? 1 : -1;
        // Walk from THIS view's page — doc.CurrentPage is the Primary facade, so a non-primary
        // viewport must skip relative to its own page, not the primary's.
        int targetPage = vp.CurrentPage + step;

        // Preserve vertical bias across page transitions so the line stays
        // at the same screen position instead of snapping to center.
        double savedBias = vp.Rail.VerticalBias;

        while (targetPage >= 0 && targetPage < doc.PageCount)
        {
            // Fast path: skip cached pages with no navigable blocks without rasterizing
            if (doc.TryGetAnalysis(targetPage, vp.AnalysisParams, out var cached)
                && !HasNavigableBlocks(cached))
            {
                skipped++;
                targetPage += step;
                continue;
            }

            // Either has navigable blocks (land on it) or needs async analysis
            if (!doc.GoToPage(vp, targetPage, _worker, _config.NavigableRoles, ww, wh))
            {
                NotifyRenderFailed(targetPage);
                vp.PendingSkip = null;
                return SkipResult.Exhausted;
            }
            vp.UpdateRailZoom(ww, wh);

            // Check the pending flag BEFORE the rail: a cache-miss GoToPage leaves the rail
            // still seated on the PREVIOUS page's analysis (nothing clears it until the worker
            // result arrives), so Rail.Active alone conflates "seated on the new page" with a
            // stale still-active rail — which made this deferred branch unreachable and landed
            // rail navigation on the old page's block geometry. Drop the stale rail so it can't
            // navigate or render the old geometry over the new page in the interim, and pin
            // block 0 / line 0 (ClearAnalysis reset the cursor) so the async seat activates at
            // the top of the page instead of geometric nearest-block selection; a backward skip
            // is corrected to the page end by the deferred ApplySkipLanding (JumpToEnd).
            if (vp.PendingRailSetup)
            {
                vp.Rail.ClearAnalysis();
                vp.Rail.PinCurrentBlockForActivation();
                vp.PendingSkip = new(forward, skipped, savedBias);
                doc.QueueLookahead(vp, _config.AnalysisLookaheadPages);
                return SkipResult.Deferred;
            }

            // Not pending → this page is the landing. Either the rail engaged (cached analysis
            // with navigable blocks — the loop's fast path guarantees cached pages without them
            // never reach GoToPage), or it stayed inactive because the zoom is below the rail
            // threshold (e.g. a deferred-skip resume after the user zoomed out, or a forced-rail
            // session ending at the page boundary) — in which case walking on would runaway-page
            // through every remaining readable page, so stop here too. The snap no-ops on an
            // inactive rail.
            vp.PendingSkip = null;
            doc.QueueLookahead(vp, _config.AnalysisLookaheadPages);
            ApplySkipLanding(vp, forward, savedBias);
            vp.StartSnap(ww, wh);
            if (skipped > 0) NotifyPagesSkipped(skipped);
            return SkipResult.FoundNavigable;
        }

        vp.PendingSkip = null;
        return SkipResult.Exhausted;
    }

    private bool HasNavigableBlocks(PageAnalysis analysis)
    {
        foreach (var block in analysis.Blocks)
            if (_config.NavigableRoles.Contains(block.Role))
                return true;
        return false;
    }

    private void NotifyPagesSkipped(int count)
    {
        StatusMessage?.Invoke(count == 1
            ? "Skipped 1 page (no text blocks)"
            : $"Skipped {count} pages (no text blocks)");
    }

    private static void ApplySkipLanding(Viewport vp, bool forward, double savedBias)
    {
        if (!forward) vp.Rail.JumpToEnd();
        vp.Rail.VerticalBias = savedBias;
    }

    /// <summary>
    /// Resume a deferred skip after analysis arrived with no navigable blocks.
    /// Called from <see cref="PollAnalysisResults"/>.
    /// </summary>
    private bool TryResumeSkip(Viewport vp, double ww, double wh)
    {
        var skip = vp.PendingSkip!;
        vp.Rail.VerticalBias = skip.SavedVerticalBias;
        // The seat that triggered this resume may have left the rail INACTIVE not because the
        // landed page has nothing to read (the walk-on case this resume exists for) but because
        // the zoom dropped below the rail threshold while the skip was in flight. If the seated
        // analysis has navigable blocks, the current page IS the landing — apply it and stop
        // rather than walking past every readable page to the document edge.
        if (vp.Rail.HasAnalysis)
        {
            vp.PendingSkip = null;
            ApplySkipLanding(vp, skip.Forward, skip.SavedVerticalBias);
            return true;
        }
        return SkipToNavigablePage(vp, skip.Forward, skip.Skipped + 1, ww, wh) == SkipResult.FoundNavigable;
    }

    public void HandleArrowDown() => HandleVerticalNav(forward: true);
    public void HandleArrowUp() => HandleVerticalNav(forward: false);

    private void HandleVerticalNav(bool forward)
    {
        if (!forward && AutoScrollActive) StopAutoScroll();
        if (FocusedViewport is not { } vp) return;
        var doc = vp.Owner;
        var (ww, wh) = (vp.Width, vp.Height);

        if (vp.Rail.Active)
        {
            var adv = AdvanceLine(vp, forward, ww, wh);
            if (adv == LineAdvanceResult.LineAdvanced)
            {
                vp.StartSnap(ww, wh);
                // During autoscroll: pause until snap completes + line pause, then resume
                if (AutoScrollActive)
                    vp.Rail.PauseAutoScroll(_config.AutoScrollLinePauseMs);
            }
            else if (adv is LineAdvanceResult.PageChanged or LineAdvanceResult.PageChangedRailLost)
            {
                RaisePageChanged(vp);
            }

            if (adv is LineAdvanceResult.LineAdvanced or LineAdvanceResult.PageChanged)
                FireReadingPositionChanged();
        }
        else
        {
            // Confined (portal) view below the rail threshold: pan within the block (the block clamp keeps
            // it bounded) but DON'T run the page-edge hold. The clamp pins OffsetY, so every arrow reads
            // as "at edge"; running OnEdgeHit would churn the hold counter and trip ShouldSuppressInput on
            // a view that can never page. The page change itself is already refused downstream.
            if (vp.CurrentFocusBlockIndex is not null)
            {
                vp.PageEdgeHold.Reset();
                // Pan only when the focus block is actually taller than the viewport (the reader zoomed
                // in past the block-fit floor). When the whole block already fits there is nothing to
                // scroll — and a confined view must not page off its block — so the arrows are
                // intentionally inert here rather than churning OffsetY through a clamp that would just
                // re-centre it. Mirrors ClampCameraToBlock's f.Bounds-based confinement.
                if (vp.Focus is { } fb && fb.Bounds.H * vp.Camera.Zoom > wh)
                {
                    vp.Camera.OffsetY += forward ? -CoreTuning.PanStep : CoreTuning.PanStep;
                    vp.ClampCamera(ww, wh);
                }
                return;
            }

            if (vp.PageEdgeHold.ShouldSuppressInput) return;

            double prevY = vp.Camera.OffsetY;
            vp.Camera.OffsetY += forward ? -CoreTuning.PanStep : CoreTuning.PanStep;
            vp.ClampCamera(ww, wh);

            bool atEdge = Math.Abs(vp.Camera.OffsetY - prevY) < 1.0;
            if (atEdge)
            {
                if (vp.PageEdgeHold.OnEdgeHit(forward))
                {
                    int targetPage = vp.CurrentPage + (forward ? 1 : -1);
                    if (targetPage >= 0 && targetPage < doc.PageCount)
                    {
                        GoToPage(targetPage);
                        var (_, ry, _, rh) = vp.GetFitRect();
                        double topTarget = -ry * vp.Camera.Zoom;
                        vp.Camera.OffsetY = forward
                            ? topTarget
                            : Math.Min(wh - (ry + rh) * vp.Camera.Zoom, topTarget);
                        vp.ClampCamera(ww, wh);
                    }
                }
            }
            else
            {
                vp.PageEdgeHold.OnMoved();
            }
        }
    }

    /// <summary>Clear non-rail edge-hold state (call on key release).</summary>
    public void ClearPageEdgeHold() => FocusedViewport?.PageEdgeHold.Reset();

    /// <summary>Step the rail to the next cell in the current table row (rolling to the next row /
    /// page-end like <see cref="HandleArrowDown"/>), centring it. Returns true if the move applied —
    /// false when the current line has no cells, so the caller can fall back to horizontal block nav.
    /// Cell stepping requires rail active on a cell-bearing row (see <see cref="RailNav.HasCells"/>).</summary>
    public bool HandleCellRight() => HandleCellNav(forward: true);

    /// <summary>Step the rail to the previous cell (rolling to the previous row's last cell at a row
    /// start). See <see cref="HandleCellRight"/>.</summary>
    public bool HandleCellLeft() => HandleCellNav(forward: false);

    private bool HandleCellNav(bool forward)
    {
        if (FocusedViewport is not { } vp) return false;
        if (!vp.Rail.Active) return false;

        var result = forward ? vp.Rail.NextCell() : vp.Rail.PrevCell();
        if (result == NavResult.NotApplicable) return false;

        var (ww, wh) = (vp.Width, vp.Height);
        vp.Rail.StartSnapToCell(vp.Camera.OffsetX, vp.Camera.OffsetY, vp.Camera.Zoom, ww, wh);
        // PageBoundary at the table's far edge: stay put (no cross-page cell flow in v1) — the snap
        // simply re-centres the current cell. A within-page roll to the next/prev row is a position
        // change, so announce either way.
        FireReadingPositionChanged();
        return true;
    }

    public void HandleArrowRight(bool shortJump = false)
    {
        if (AutoScrollActive && FocusedViewport is { } d && d.Rail.Active && d.Rail.AutoScrolling)
        {
            d.Rail.SetAutoScrollBoost(true);
            return;
        }
        if (TryJump(forward: true, half: shortJump)) return;
        HandleHorizontalArrow(ScrollDirection.Forward, -CoreTuning.PanStep);
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (TryJump(forward: false, half: shortJump)) return;
        HandleHorizontalArrow(ScrollDirection.Backward, CoreTuning.PanStep);
    }

    private bool TryJump(bool forward, bool half = false)
    {
        if (!JumpMode || FocusedViewport is not { } vp || !vp.Rail.Active) return false;
        var (ww, wh) = (vp.Width, vp.Height);
        vp.Rail.Jump(forward, vp.Camera.Zoom, ww, wh, vp.Camera.OffsetX, vp.Camera.OffsetY, half);
        return true;
    }

    private void HandleHorizontalArrow(ScrollDirection direction, double panDelta)
    {
        if (FocusedViewport is not { } vp) return;
        if (vp.Rail.Active)
            vp.Rail.StartScroll(direction, vp.Camera.OffsetX);
        else
        {
            var (ww, wh) = (vp.Width, vp.Height);
            vp.Camera.OffsetX += panDelta;
            vp.ClampCamera(ww, wh);
        }
    }

    public void HandleLineHome() => SnapToLineEdge(start: true);
    public void HandleLineEnd() => SnapToLineEdge(start: false);

    private void SnapToLineEdge(bool start)
    {
        if (FocusedViewport is not { } vp || !vp.Rail.Active) return;
        var (ww, _) = (vp.Width, vp.Height);
        var x = start
            ? vp.Rail.ComputeLineStartX(vp.Camera.Zoom, ww)
            : vp.Rail.ComputeLineEndX(vp.Camera.Zoom, ww);
        if (x is { } val)
        {
            // Cancel any in-flight snap BEFORE driving the camera directly — a live snap would
            // overwrite OffsetX on the next tick. (This cancellation used to hide inside
            // ComputeLineStartX/EndX; it now lives here, at the mutation site.)
            vp.Rail.CancelSnap();
            vp.Camera.OffsetX = val;
            // During autoscroll: brief settle pause, then resume from new position
            if (AutoScrollActive)
                vp.Rail.PauseAutoScroll(_config.AutoScrollLinePauseMs);
        }
    }

    public void HandleArrowRelease(bool isHorizontal)
    {
        if (isHorizontal)
        {
            FocusedViewport?.Rail.StopScrollAndEdgeHold();
            if (AutoScrollActive)
                FocusedViewport?.Rail.SetAutoScrollBoost(false);
        }
    }

    /// <summary>
    /// Handles a click on the viewport. Returns link destination if a link was clicked,
    /// otherwise falls through to rail-mode block snapping.
    /// </summary>
    public (bool Handled, PdfLinkDestination? Link) HandleClick(double canvasX, double canvasY)
    {
        if (FocusedViewport is not { } vp) return (false, null);
        var doc = vp.Owner;

        double pageX = (canvasX - vp.Camera.OffsetX) / vp.Camera.Zoom;
        double pageY = (canvasY - vp.Camera.OffsetY) / vp.Camera.Zoom;

        // Check for PDF links first (takes priority over rail-mode snap). Hit-test the FOCUSED view's
        // own page so a detached pane sitting on a different page than the primary clicks its own links.
        var link = doc.HitTestLink(vp.CurrentPage, pageX, pageY);
        if (link is not null)
        {
            if (link.Destination is PageDestination pageDest && vp.CurrentFocusBlockIndex is null)
            {
                // Confined (portal) view: suppress internal page-link navigation entirely — pushing
                // history before the GoToPage no-op would corrupt the back stack and leave the block.
                PushHistory();
                GoToPage(pageDest.PageIndex);
                ScrollToDestination(pageDest);
            }
            return (true, link.Destination);
        }

        // Fall through to rail-mode block snapping on the focused view.
        if (!vp.Rail.Active || !vp.Rail.HasAnalysis) return (false, null);

        vp.Rail.FindBlockNearPoint(pageX, pageY);
        var (ww2, wh2) = (vp.Width, vp.Height);
        vp.StartSnap(ww2, wh2);
        // The click-snap must not fight the wall-clock scroll trajectory: without a pause the
        // Scrolling state recomputes cameraX from the pre-click origin every frame, discarding
        // the snap's X (only its Y survived) and potentially firing an instant line advance
        // against the newly-clicked line. Defer-resume after the snap settles, matching the
        // manual line-advance and Home/End paths. (A no-op while parked — a click never un-parks.)
        if (AutoScrollActive)
            vp.Rail.PauseAutoScroll(_config.AutoScrollLinePauseMs);
        FireReadingPositionChanged();
        return (true, null);
    }

    /// <summary>
    /// Hit-tests a point (in page-point space) against PDF links on the focused view's page.
    /// </summary>
    public PdfLink? HitTestLink(double pageX, double pageY)
        => FocusedViewport is { } vp ? vp.Owner.HitTestLink(vp.CurrentPage, pageX, pageY) : null;

    /// <summary>
    /// Force rail mode active at the clicked point regardless of zoom ("start rail here"),
    /// seating the nearest navigable block and the line under the click, then snapping it to
    /// the reading position. Unlike <see cref="SmoothlyFrameBlock"/> this does NOT magnify —
    /// the camera zoom is preserved — so a reader can rail-read at any magnification.
    /// Returns false when there is no document or the current page has no analysis yet.
    /// </summary>
    public bool ActivateRailAt(double canvasX, double canvasY)
    {
        if (FocusedViewport is not { } vp) return false;
        var doc = vp.Owner;
        if (!doc.TryGetAnalysis(vp.CurrentPage, vp.AnalysisParams, out var analysis)) return false;

        // Sync RailNav to this page's analysis so the navigable index space + line seating
        // refer to the current page (mirrors SmoothlyFrameBlock). Skip when already current.
        if (!ReferenceEquals(vp.Rail.Analysis, analysis))
            doc.ReapplyNavigableRoles(vp, _config.NavigableRoles);
        if (!vp.Rail.HasAnalysis) return false;

        double pageX = (canvasX - vp.Camera.OffsetX) / vp.Camera.Zoom;
        double pageY = (canvasY - vp.Camera.OffsetY) / vp.Camera.Zoom;

        if (AutoScrollActive) StopAutoScroll();
        vp.Rail.ForceActivateAt(pageX, pageY);
        var (ww, wh) = (vp.Width, vp.Height);
        // Below the rail threshold (the usual "start rail here" case) the snap is intentionally
        // suppressed (RailNav.SnapSuppressed) so the page doesn't lurch — the seated line is simply
        // highlighted where it is. Above the threshold this frames the line as normal rail does.
        vp.StartSnap(ww, wh);
        FireReadingPositionChanged();
        return true;
    }

    /// <summary>True when rail is currently held active below the zoom threshold by a forced
    /// <see cref="ActivateRailAt"/> activation on the focused view.</summary>
    public bool ForcedRailActive => FocusedViewport?.Rail is { Active: true, ForceActive: true };

    /// <summary>
    /// Release a forced ("start rail here") activation and re-evaluate the zoom gate, so rail
    /// deactivates immediately if the current zoom is below the threshold. No-op when rail is
    /// not forced.
    /// </summary>
    public void ExitForcedRail()
    {
        if (FocusedViewport is not { } vp) return;
        if (!vp.Rail.ForceActive) return;
        vp.Rail.ClearForceActive();
        var (ww, wh) = (vp.Width, vp.Height);
        vp.UpdateRailZoom(ww, wh);
        FireReadingPositionChanged();
    }
}
