using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Rail navigation ---

    private enum LineAdvanceResult { NoChange, LineAdvanced, PageChanged, PageChangedRailLost }

    private LineAdvanceResult AdvanceLine(Viewport vp, bool forward, double ww, double wh)
    {
        var result = forward ? vp.Rail.NextLine() : vp.Rail.PrevLine();
        var boundary = forward ? NavResult.PageBoundaryNext : NavResult.PageBoundaryPrev;
        if (result == boundary)
        {
            return SkipToNavigablePage(vp, forward, 0, ww, wh) switch
            {
                SkipResult.FoundNavigable => LineAdvanceResult.PageChanged,
                _ => LineAdvanceResult.PageChangedRailLost,
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

            if (vp.Rail.Active)
            {
                vp.PendingSkip = null;
                doc.QueueLookahead(vp, _config.AnalysisLookaheadPages);
                ApplySkipLanding(vp, forward, savedBias);
                vp.StartSnap(ww, wh);
                if (skipped > 0) NotifyPagesSkipped(skipped);
                return SkipResult.FoundNavigable;
            }

            if (vp.PendingRailSetup)
            {
                vp.PendingSkip = new(forward, skipped, savedBias);
                doc.QueueLookahead(vp, _config.AnalysisLookaheadPages);
                return SkipResult.Deferred;
            }

            skipped++;
            targetPage += step;
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

        // Check for PDF links first (takes priority over rail-mode snap). Link hit-testing is
        // document-level (it uses the document's current page); precise per-view link clicking on a
        // detached pane sitting on a different page is a later increment.
        var link = doc.HitTestLink(pageX, pageY);
        if (link is not null)
        {
            if (link.Destination is PageDestination pageDest)
            {
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
        FireReadingPositionChanged();
        return (true, null);
    }

    /// <summary>
    /// Hit-tests a point (in page-point space) against PDF links on the active document.
    /// </summary>
    public PdfLink? HitTestLink(double pageX, double pageY)
        => ActiveDocument?.HitTestLink(pageX, pageY);

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
