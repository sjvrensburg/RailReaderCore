using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed partial class RailNav
{
    // Steered scroll (railreader2 docs/reading-guide-design.md §11): the reading guide's invisible
    // joystick drives the rail above the zoom threshold. The host sets a signed throttle every frame
    // (0 inside the stick's dead zone); TickSteer turns it into a camera velocity along the line with
    // hold-to-scroll's speed range and quadratic shape — deflection replaces hold time — and feeds the
    // same edge-hold state machine, so line ends, snaps and page turns reach the controller through the
    // existing pending-edge-advance channel. Mutually exclusive with hold-to-scroll and auto-scroll.
    private double _steerThrottle;
    private bool _steerParked;

    /// <summary>The host's current stick throttle, −1…1 (0 = released / inside the dead zone).</summary>
    public double SteerThrottle => _steerThrottle;

    /// <summary>True while a non-zero throttle is applied (the stick is deflected).</summary>
    public bool Steering => _steerThrottle != 0;

    /// <summary>
    /// True after a steered advance landed on a stop unit (a new chunk, a non-prose block, a new
    /// page — see <see cref="ParkSteer"/>): the throttle is ignored until it returns to 0 and is
    /// re-applied. That re-push is the steering reader's equivalent of semi-auto scroll's advance key.
    /// </summary>
    public bool SteerParked => _steerParked;

    /// <summary>
    /// Apply the stick's throttle for this frame. A non-zero value engages steering (stopping any
    /// held arrow-key scroll — the caller stops auto-scroll, which is controller-level state); 0
    /// releases it, leaves the camera where it is, and clears a park so the next deflection resumes.
    /// </summary>
    public void SetSteerThrottle(double throttle)
    {
        if (!CanNavigate) { ClearSteer(); return; }
        double t = Math.Clamp(throttle, -1, 1);
        if (Math.Abs(t) < 1e-6)
        {
            if (_steerThrottle != 0)
            {
                _steerThrottle = 0;
                ScrollSpeed = 0.0;
                _lineEdgeHold.OnMoved();   // letting go is not a hold against the edge
            }
            _steerParked = false;
            return;
        }
        if (_steerThrottle == 0)
        {
            // Engaging: the stick supersedes a held arrow key.
            _scroll.Stop();
            _lineEdgeHold.OnMoved();
        }
        _steerThrottle = t;
    }

    /// <summary>Park the stick (controller: a steered advance entered a stop unit).</summary>
    public void ParkSteer()
    {
        if (!Steering) return;
        _steerParked = true;
        ScrollSpeed = 0.0;
    }

    private void ClearSteer()
    {
        _steerThrottle = 0;
        _steerParked = false;
    }

    /// <summary>
    /// Steered speed in page px/s for a deflection <paramref name="t"/> (0 at the dead-zone edge, 1 at
    /// full deflection): hold-to-scroll's start speed rising quadratically to its top speed, the same
    /// curve <see cref="TickScrollHold"/> runs over hold time. <c>internal static</c> for direct testing.
    /// </summary>
    internal static double SteerSpeed(double t, double speedStart, double speedMax)
    {
        t = Math.Clamp(t, 0, 1);
        return speedStart + (speedMax - speedStart) * t * t;
    }

    private bool TickSteer(ref double cameraX, double dtSecs, double zoom, double windowWidth)
    {
        if (_steerThrottle == 0 || dtSecs <= 0) return false;
        // Parked, or a snap (an edge advance's, or a line step's) is still landing: hold the velocity so a
        // held deflection doesn't pan the just-flipped line. Not animating — the snap reports its own.
        if (_steerParked || _snap is not null || ShouldSuppressAfterEdgeAdvance())
        {
            ScrollSpeed = 0.0;
            return false;
        }

        double t = Math.Abs(_steerThrottle);
        double speed = SteerSpeed(t, _config.ScrollSpeedStart, _config.ScrollSpeedMax);
        ScrollSpeed = _config.ScrollSpeedMax > 0 ? speed / _config.ScrollSpeedMax : 0.0;

        var dir = _steerThrottle > 0 ? ScrollDirection.Forward : ScrollDirection.Backward;
        double sign = dir == ScrollDirection.Forward ? -1.0 : 1.0;
        cameraX = SnapX(ClampX(cameraX + sign * speed * dtSecs * zoom, zoom, windowWidth), zoom);

        // Pinned against the line end (forward) / the framed unit's left edge (backward): the edge-hold
        // machine accumulates the hold and raises the pending advance the controller consumes.
        CheckEdgeHoldAdvance(cameraX, zoom, windowWidth, dir);
        return true;
    }

    /// <summary>Inject a controlled clock (ms) into the line edge-hold machine for unit tests.</summary>
    internal Func<double>? EdgeHoldNowMsOverride
    {
        set => _lineEdgeHold.GetNowMs = value;
    }
}
