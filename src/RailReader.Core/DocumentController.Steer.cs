namespace RailReader.Core;

public sealed partial class DocumentController
{
    /// <summary>
    /// Steer <paramref name="vp"/>'s rail with the reading guide's stick (railreader2
    /// docs/reading-guide-design.md §11). <paramref name="throttle"/> is the stick's signed horizontal
    /// deflection, −1…1, with 0 inside the dead zone; call it every frame while the stick is engaged
    /// and once with 0 on release. Engaging stops a running (semi-)auto-scroll, as A/Left does — the
    /// stick supersedes it. Per-viewport, unlike the arrow handlers, because the stick acts on the view
    /// under the pointer rather than <see cref="FocusedViewport"/>. No-op on an inactive rail.
    /// </summary>
    public void SteerRail(Viewport vp, double throttle)
    {
        if (!vp.Rail.Active || !vp.Rail.HasAnalysis)
        {
            vp.Rail.SetSteerThrottle(0);
            return;
        }
        if (throttle != 0 && !vp.Rail.Steering && vp.AutoScroll.AutoScrollActive)
            vp.AutoScroll.StopAutoScroll();
        vp.Rail.SetSteerThrottle(throttle);
    }

    /// <summary>
    /// Step <paramref name="vp"/>'s rail one line forward or back — the stick's vertical axis — exactly
    /// as ↑/↓ do for the focused view (advance + snap; page turn at the boundary, snapping to the end of
    /// the last line when stepping back onto a page). A deliberate step never parks the stick.
    /// Returns true when the reading position changed.
    /// </summary>
    public bool StepRailLine(Viewport vp, bool forward)
    {
        if (!vp.Rail.Active || !vp.Rail.HasAnalysis) return false;
        if (vp.AutoScroll.AutoScrollActive) vp.AutoScroll.StopAutoScroll();
        var (ww, wh) = (vp.Width, vp.Height);
        var adv = AdvanceLine(vp, forward, ww, wh);
        switch (adv)
        {
            case LineAdvanceResult.LineAdvanced:
                vp.StartSnap(ww, wh);
                FireReadingPositionChanged(vp);
                return true;
            case LineAdvanceResult.PageChanged:
                RaisePageChanged(vp);
                if (!forward) vp.StartSnapToEnd(ww, wh);
                FireReadingPositionChanged(vp);
                return true;
            case LineAdvanceResult.PageChangedRailLost:
                RaisePageChanged(vp);
                return true;
            default:
                return false;
        }
    }
}
