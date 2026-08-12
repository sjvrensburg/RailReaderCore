namespace RailReader.Core.Models;

/// <summary>
/// One page's OCR output, in the <b>pixel space of the pixmap that was submitted</b>
/// (top-left origin, Y-down). Mapping into page-point space is the caller's job — the
/// analysis worker does it with the same scale factors it uses for layout blocks.
/// </summary>
/// <param name="Lines">
/// Detected text lines, in the engine's own order. Line geometry is always present;
/// <see cref="OcrLine.Text"/> and <see cref="OcrLine.Chars"/> are only populated when the
/// engine ran in <see cref="Services.OcrMode.Full"/>.
/// </param>
/// <param name="SkewAngle">
/// The page's estimated skew, in radians, measured clockwise from horizontal (Y is down, so
/// a positive angle means text runs downhill to the right). Aggregated from the detector's
/// own line quads by <see cref="Services.SkewEstimator"/>.
///
/// <para>
/// <b>Zero means "no correction"</b>, not "provably upright": the estimator returns 0 whenever
/// it is not confident (too few lines, too much disagreement, or a result outside the
/// supported range). Consumers therefore need no separate confidence flag — they simply
/// apply the angle, and an unconfident page behaves exactly as it did before deskew existed.
/// </para>
/// </param>
public sealed record OcrPage(List<OcrLine> Lines, float SkewAngle = 0f)
{
    /// <summary>No text found (or OCR disabled).</summary>
    public static readonly OcrPage Empty = new([]);

    /// <summary>True when at least one line carries transcribed text.</summary>
    public bool HasText
    {
        get
        {
            foreach (var l in Lines)
                if (!string.IsNullOrEmpty(l.Text)) return true;
            return false;
        }
    }
}

/// <summary>
/// One detected text line. <paramref name="Box"/> is the line's axis-aligned bounds in
/// pixmap pixel space.
/// </summary>
/// <param name="Text">
/// The transcribed line, or null when only detection ran.
/// </param>
/// <param name="Chars">
/// Per-character boxes whose <see cref="CharBox.Index"/> is an offset into
/// <paramref name="Text"/> (not into any page-level string). Null when only detection
/// ran, or when the engine could not localise individual characters.
/// </param>
/// <param name="Confidence">Engine confidence in [0,1]; 0 when not reported.</param>
/// <param name="Angle">
/// The line's baseline direction in radians, clockwise from horizontal (Y is down). 0 when the
/// detector reported no usable quad — see <see cref="OcrPage.SkewAngle"/> for why 0 is a safe
/// "no information" value rather than a claim of uprightness.
/// </param>
/// <param name="TrueHeight">
/// The line's height measured perpendicular to its own baseline — the short side of the
/// detector's minimum-area rectangle — or 0 when unmeasured.
///
/// <para>
/// This exists because <paramref name="Box"/> is the <i>axis-aligned</i> bound of a rotated
/// quad, so its height is inflated by <c>width × sin(angle)</c>: on a 400&#160;pt line a mere
/// 1° of skew adds ~7&#160;pt of phantom height. That inflation is what makes neighbouring
/// lines' bands overlap enough for <c>LineDetector.NormalizeLines</c> to fuse them, which is
/// the visible "several printed lines became one rail line" bug. Prefer
/// <see cref="EffectiveHeight"/> over <c>Box.H</c> anywhere the value is a line's height.
/// </para>
/// <para>
/// <b>It is not the ink height.</b> Detectors of this family dilate each contour outwards
/// before fitting the rectangle, so this value is systematically larger than the glyphs it
/// encloses. It is still strictly better than the axis-aligned height — which is dilated
/// <i>and</i> skew-inflated — and its consumers compare line heights to one another rather
/// than to absolute ink, so the common factor cancels. Do not try to undo the dilation.
/// </para>
/// </param>
public sealed record OcrLine(
    BBox Box,
    string? Text = null,
    List<CharBox>? Chars = null,
    float Confidence = 0f,
    float Angle = 0f,
    float TrueHeight = 0f)
{
    /// <summary>
    /// The line's height, preferring the skew-independent <see cref="TrueHeight"/> and falling
    /// back to the axis-aligned <see cref="BBox.H"/> when the detector gave no usable quad.
    /// </summary>
    public float EffectiveHeight => TrueHeight > 0f ? TrueHeight : Box.H;
}
