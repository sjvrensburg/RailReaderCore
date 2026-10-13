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
    // The virtual reading point (page x on the current line) the stick carries along at the steered
    // speed. The camera FOLLOWS it — keeping it at SteerReadingPointFraction of the window where it can,
    // staying pinned at the line's ends where it can't — so every word passes the eye: the first third of
    // a line is read with the camera still at the line start, the last with it pinned at the line end,
    // and the edge-hold only begins once the point reaches the line's edge. Null until the first tick.
    private double? _steerReadPage;
    private (int Block, int Line) _steerLineKey = (-1, -1);

    /// <summary>Where on screen the eye rests while the rail scrolls under the stick, as a fraction of the
    /// window width; the host's word marker reads <see cref="SteerReadingPointPageX"/> back.</summary>
    public double SteerReadingPointFraction { get; set; } = 1.0 / 3.0;

    /// <summary>The steered reading point on the current line, in page units (null when nothing has been
    /// steered since the stick was cleared). Valid while the throttle is 0 too, so a paused reader's marker
    /// stays where the sweep stopped.</summary>
    public double? SteerReadingPointPageX => _steerReadPage;

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
        _steerLineKey = (-2, -2);   // a park means a new unit: the resumed flow restarts at its near end
    }

    private void ClearSteer()
    {
        _steerThrottle = 0;
        _steerParked = false;
        _steerReadPage = null;
        _steerLineKey = (-1, -1);
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
        bool forward = _steerThrottle > 0;
        var line = CurrentLineInfo;
        double lineLeft = line.X, lineRight = line.X + line.Width;

        // A new line under the stick (edge advance, line step, click, page turn): the eye starts at its
        // near end, not at whatever the window's reading point happens to cover after the snap.
        var key = (CurrentBlock, CurrentLine);
        if (key != _steerLineKey)
        {
            bool first = _steerLineKey == (-1, -1);   // first tick since the stick was cleared: see ??= below
            _steerLineKey = key;
            if (!first) _steerReadPage = forward ? lineLeft : lineRight;
        }

        // Parked, or a snap (an edge advance's, or a line step's) is still landing: hold the velocity so a
        // held deflection doesn't pan the just-flipped line. Not animating — the snap reports its own.
        if (_steerParked || _snap is not null || ShouldSuppressAfterEdgeAdvance())
        {
            ScrollSpeed = 0.0;
            return false;
        }

        double readX = windowWidth * SteerReadingPointFraction;
        // Engaged mid-line: start from the word under the reading point — unless the camera still sits at
        // the line's start (a fresh snap), where the eye is at the first word.
        _steerReadPage ??= cameraX >= ComputeSnapTarget(zoom, windowWidth, 0).X - 0.5
            ? lineLeft
            : Math.Clamp((readX - cameraX) / zoom, lineLeft, lineRight);
        _steerReadPage = Math.Clamp(_steerReadPage.Value, lineLeft, lineRight);   // the line may have been re-seated under it

        double t = Math.Abs(_steerThrottle);
        double speed = SteerSpeed(t, _config.ScrollSpeedStart, _config.ScrollSpeedMax);
        ScrollSpeed = _config.ScrollSpeedMax > 0 ? speed / _config.ScrollSpeedMax : 0.0;

        double read = _steerReadPage.Value + (forward ? 1 : -1) * speed * dtSecs;
        bool atEdge = forward ? read >= lineRight : read <= lineLeft;
        if (atEdge) read = forward ? lineRight : lineLeft;
        _steerReadPage = read;

        // The camera follows the reading point to keep it at readX — only in the direction of travel (it
        // never backs up to meet it), and forward never past the line's end at the window's right edge,
        // the same stop the edge-hold trigger and auto-scroll use. ClampX bounds it to the framed unit.
        double followX = readX - read * zoom;
        double newX = forward ? Math.Min(cameraX, followX) : Math.Max(cameraX, followX);
        if (forward)
        {
            var (_, lr, _) = GetLineBounds(zoom);
            newX = Math.Max(newX, Math.Min(cameraX, windowWidth - lr * zoom));
        }
        cameraX = SnapX(ClampX(newX, zoom, windowWidth), zoom);

        // The reading point reached the line's edge: hold there (the edge-hold machine's beat), then the
        // controller consumes the pending advance. Any earlier movement keeps the machine idle.
        if (atEdge) _lineEdgeHold.OnEdgeHit(forward);
        else _lineEdgeHold.OnMoved();
        return true;
    }

    /// <summary>Inject a controlled clock (ms) into the line edge-hold machine for unit tests.</summary>
    internal Func<double>? EdgeHoldNowMsOverride
    {
        set => _lineEdgeHold.GetNowMs = value;
    }
}
