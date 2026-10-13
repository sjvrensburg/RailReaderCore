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
    // speed. The camera FOLLOWS it — easing to keep it centred (SteerReadingPointFraction) where the
    // line's framing allows, staying pinned at the line's start (where the snap landed) and end (the
    // line's end at the window's right edge) where it doesn't — so every word passes the eye: a line's
    // head is read with the camera still, the tail with it pinned, and the edge-hold only begins once the
    // point reaches the line's edge. Null until the first tick.
    private double? _steerReadPage;
    private double? _steerSeatPending;   // SeatSteerReadingPoint: applied to the then-current line on the next tick
    private (int Block, int Line) _steerLineKey = (-1, -1);

    /// <summary>Where on screen the camera keeps the reading point while the line scrolls under the stick,
    /// as a fraction of the window width (centred by default); the host's word marker reads
    /// <see cref="SteerReadingPointPageX"/> back.</summary>
    public double SteerReadingPointFraction { get; set; } = 0.5;

    /// <summary>Time constant (s) of the camera's ease toward the reading point's framing.</summary>
    public double SteerFollowTauSecs { get; set; } = 0.12;

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

    /// <summary>
    /// Start reading from <paramref name="pageX"/> on whatever line is current at the next tick — the host
    /// calls this with the click point just before the click seats the rail's block / line, so the steered
    /// reading point (and the host's word marker) lands on the clicked word rather than the line's start.
    /// Clamped to the line. Takes effect when the stick next ticks (it may not be engaged yet).
    /// </summary>
    public void SeatSteerReadingPoint(double pageX) => _steerSeatPending = pageX;

    /// <summary>
    /// If <paramref name="pageX"/>/<paramref name="pageY"/> lies on the current line, start reading from
    /// there (the follow key went down with the pointer resting on a word) and return true; otherwise leave
    /// the reading point alone — the stick continues from where the rail is — and return false.
    /// </summary>
    public bool TrySeatSteerReadingPointAt(double pageX, double pageY)
    {
        if (!CanNavigate) return false;
        var line = CurrentLineInfo;
        double pad = line.Height * 0.5;
        if (pageY < line.Y - pad || pageY > line.Y + line.Height + pad
            || pageX < line.X || pageX > line.X + line.Width)
            return false;
        _steerSeatPending = pageX;
        return true;
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
        _steerSeatPending = null;
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
        // A seat (click / follow-key-down on a word) wins over both.
        if (_steerSeatPending is { } seat)
        {
            _steerSeatPending = null;
            _steerReadPage = Math.Clamp(seat, lineLeft, lineRight);
        }

        // Parked, or a snap (an edge advance's, or a line step's) is still landing: hold the velocity so a
        // held deflection doesn't pan the just-flipped line. Not animating — the snap reports its own.
        if (_steerParked || _snap is not null || ShouldSuppressAfterEdgeAdvance())
        {
            ScrollSpeed = 0.0;
            return false;
        }

        double readX = windowWidth * SteerReadingPointFraction;
        // Engaged with nothing seated: start from the word under the window's reading point — unless the
        // camera still sits at the line's start (a fresh snap), where the eye is at the first word.
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

        // The camera eases toward framing the reading point at readX, within the line's own range: no
        // further right than the line-start framing the snap lands on (so the head is read with the
        // camera still, and nothing fights the snap), no further left than the line's end at the window's
        // right edge (the stop the edge-hold trigger and auto-scroll use). A line that fits sits still.
        double target = readX - read * zoom;
        double maxX = ComputeSnapTarget(zoom, windowWidth, 0).X;
        var (_, lr, _) = GetLineBounds(zoom);
        double minX = Math.Min(maxX, windowWidth - lr * zoom);
        target = Math.Clamp(target, minX, maxX);
        double k = 1 - Math.Exp(-dtSecs / Math.Max(1e-3, SteerFollowTauSecs));
        double gap = target - cameraX;
        double newX = Math.Abs(gap) < 0.5 ? target : cameraX + gap * k;
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
