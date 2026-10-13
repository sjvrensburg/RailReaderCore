namespace RailReader.Core.Services;

/// <summary>Which axis a steered guide is currently moving along.</summary>
public enum SteerAxis { None, Horizontal, Vertical }

/// <summary>
/// What one <see cref="ReadingGuideSteer.Tick"/> asks the host to apply: a signed word step (±1 or 0,
/// along reading order) or a signed line step (±1 or 0), never both, plus the current throttle
/// (signed, −1…1 on the active axis) for diagnostics / accessibility.
/// </summary>
public readonly record struct SteerOutput(int WordSteps, int LineSteps, SteerAxis Axis, double Throttle)
{
    public static readonly SteerOutput None = new(0, 0, SteerAxis.None, 0);
    public bool Any => WordSteps != 0 || LineSteps != 0;
}

/// <summary>
/// The "invisible virtual joystick" of the reading guide's steer mode. The host plants the stick's
/// origin where the pointer is when the follow key goes down (<see cref="Engage"/>), feeds every
/// pointer position (<see cref="SetPointer"/>) and ticks it from its animation frame; the pointer's
/// displacement from the origin is a throttle, not a position:
/// <list type="bullet">
///   <item>inside <see cref="DeadZonePx"/> nothing moves;</item>
///   <item>beyond it, the larger axis wins (<see cref="SteerAxis"/>) — horizontal displacement advances
///   (right) or reverses (left) word by word along reading order, vertical steps lines;</item>
///   <item>the rate ramps quadratically from the minimum at the dead-zone edge to the maximum at
///   <see cref="FullDeflectionPx"/>, like hold-to-scroll;</item>
///   <item>the horizontal pace is in <b>characters per second</b>, so a long word dwells longer than a
///   short one and the rhythm feels even; a word that ends a sentence dwells
///   <see cref="SentencePauseChars"/> extra characters' worth.</item>
/// </list>
/// The host maps the pace from its reading-pace settings (e.g. <c>CoreSettings.ScrollSpeedMax</c>).
/// Pure state, no timers: a frame's <paramref name="dt"/> drives it, so a host with no frame running
/// (stick released) pays nothing.
/// </summary>
public sealed class ReadingGuideSteer
{
    /// <summary>Radius around the origin, in host (screen) pixels, inside which the stick is centred.</summary>
    public double DeadZonePx { get; set; } = 16;

    /// <summary>Displacement, in host pixels, at which the pace reaches its maximum.</summary>
    public double FullDeflectionPx { get; set; } = 160;

    /// <summary>Horizontal pace at the dead-zone edge, characters per second.</summary>
    public double MinCharsPerSecond { get; set; } = 5;

    /// <summary>Horizontal pace at full deflection, characters per second.</summary>
    public double MaxCharsPerSecond { get; set; } = 17;

    /// <summary>Vertical pace at the dead-zone edge, lines per second.</summary>
    public double MinLinesPerSecond { get; set; } = 0.8;

    /// <summary>Vertical pace at full deflection, lines per second.</summary>
    public double MaxLinesPerSecond { get; set; } = 3;

    /// <summary>Extra dwell, in characters' worth, on a word that ends a sentence.</summary>
    public double SentencePauseChars { get; set; } = 6;

    /// <summary>A word shorter than this dwells as if it were this long (a lone "a" shouldn't flash by).</summary>
    public int MinWordChars { get; set; } = 2;

    /// <summary>Dwell, in characters' worth, for a line-only step (no text layer) when steering horizontally.</summary>
    public double LineOnlyStepChars { get; set; } = 12;

    public bool Engaged { get; private set; }
    public double OriginX { get; private set; }
    public double OriginY { get; private set; }
    public SteerAxis Axis { get; private set; }
    /// <summary>Signed throttle on <see cref="Axis"/>: −1…1 (0 inside the dead zone).</summary>
    public double Throttle { get; private set; }
    /// <summary>True while engaged and deflected past the dead zone — the host should keep ticking.</summary>
    public bool IsMoving => Engaged && Axis != SteerAxis.None;

    private double _pointerX, _pointerY;
    private double _progress;   // characters (horizontal) or lines (vertical) accumulated toward the next step
    private int _lastSign;

    /// <summary>Plant the origin (the follow key went down with the pointer here) and start steering.</summary>
    public void Engage(double originX, double originY)
    {
        Engaged = true;
        OriginX = _pointerX = originX;
        OriginY = _pointerY = originY;
        Axis = SteerAxis.None;
        Throttle = 0;
        _progress = 0;
        _lastSign = 0;
    }

    /// <summary>Stop steering (the follow key went up); the guide stays where it is.</summary>
    public void Release()
    {
        Engaged = false;
        Axis = SteerAxis.None;
        Throttle = 0;
        _progress = 0;
        _lastSign = 0;
    }

    /// <summary>The pointer's current host-pixel position.</summary>
    public void SetPointer(double x, double y)
    {
        _pointerX = x;
        _pointerY = y;
    }

    /// <summary>
    /// Advance by <paramref name="dt"/> seconds. <paramref name="currentWordChars"/> is the length of the
    /// word the guide is on (0 for a line-only position), <paramref name="sentenceEnd"/> whether that word
    /// ends a sentence. At most one step per tick, so the next tick dwells on the NEW word's length.
    /// </summary>
    public SteerOutput Tick(double dt, int currentWordChars, bool sentenceEnd)
    {
        if (!Engaged || dt <= 0) return SteerOutput.None;

        double dx = _pointerX - OriginX, dy = _pointerY - OriginY;
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        double mag = Math.Max(ax, ay);
        if (mag <= DeadZonePx)
        {
            Axis = SteerAxis.None;
            Throttle = 0;
            _progress = 0;
            _lastSign = 0;
            return SteerOutput.None;
        }

        var axis = ax >= ay ? SteerAxis.Horizontal : SteerAxis.Vertical;
        int sign = Math.Sign(axis == SteerAxis.Horizontal ? dx : dy);
        double span = Math.Max(1e-6, FullDeflectionPx - DeadZonePx);
        double t = Math.Clamp((mag - DeadZonePx) / span, 0, 1);
        double ramp = t * t;
        if (axis != Axis || sign != _lastSign)
        {
            _progress = 0;
            Axis = axis;
            _lastSign = sign;
        }
        Throttle = sign * t;

        if (axis == SteerAxis.Horizontal)
        {
            double rate = MinCharsPerSecond + (MaxCharsPerSecond - MinCharsPerSecond) * ramp;
            double cost = currentWordChars > 0
                ? Math.Max(MinWordChars, currentWordChars) + (sentenceEnd ? SentencePauseChars : 0)
                : LineOnlyStepChars;
            _progress += rate * dt;
            if (_progress >= cost)
            {
                _progress -= cost;
                return new SteerOutput(sign, 0, axis, Throttle);
            }
            return new SteerOutput(0, 0, axis, Throttle);
        }
        else
        {
            double rate = MinLinesPerSecond + (MaxLinesPerSecond - MinLinesPerSecond) * ramp;
            _progress += rate * dt;
            if (_progress >= 1)
            {
                _progress -= 1;
                return new SteerOutput(0, sign, axis, Throttle);
            }
            return new SteerOutput(0, 0, axis, Throttle);
        }
    }
}
