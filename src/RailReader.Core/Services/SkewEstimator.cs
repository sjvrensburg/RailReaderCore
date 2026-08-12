using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Estimates a scanned page's skew from the per-line baseline angles an OCR detector already
/// measured, so that line grouping can cluster along the text's true direction instead of
/// along page-Y.
///
/// <para>
/// <b>Why this is worth doing at all.</b> Every grouping step downstream is an orthogonal
/// projection onto Y — glyphs are sorted and clustered by mid-Y, lines are merged when their
/// vertical bands overlap, column rules are found by walking straight down a pixel column.
/// Skew is precisely the transformation those steps are blind to, so a page that is a degree
/// or two off square degrades from "one band per printed line" to bands that split mid-line
/// and fuse with their neighbours. Correcting the *coordinate* the grouping reasons about is
/// far cheaper, and far less destructive, than rotating pixels and mapping everything back.
/// </para>
/// <para>
/// <b>Why the detector's own quads.</b> A DB-style text detector emits a minimum-area
/// rectangle per line, which is a direct measurement of that line's baseline direction made by
/// a model trained to find tilted text. It costs nothing extra to read, and it is a stronger
/// signal than any projection-profile or nearest-neighbour estimate we could compute
/// afterwards. The corollary is that this only works when OCR ran: a page analysed with
/// <see cref="OcrMode.Off"/> has no estimate and keeps its uncorrected behaviour.
/// </para>
/// </summary>
public static class SkewEstimator
{
    /// <summary>
    /// Largest skew we will correct, in radians (5°). Beyond this the input is not a skewed
    /// scan but a rotated one, which is the quarter-turn <c>ViewRotation</c> machinery's job;
    /// widening the range mostly buys confident wrong answers on pages that are not skewed.
    /// </summary>
    public const float MaxSkewRadians = 5f * MathF.PI / 180f;

    /// <summary>
    /// Smallest skew worth acting on, in radians (0.15°) — below this the estimate is snapped
    /// to exactly 0.
    ///
    /// <para>
    /// This dead band is what makes the feature provably free on ordinary pages. Detection
    /// quads carry integer pixel corners, so a single 400&#160;px-wide line can only resolve an
    /// angle to about <c>atan(1/400) ≈ 0.14°</c>; a perfectly square page therefore estimates
    /// some small non-zero noise value rather than a clean 0. Snapping that to 0 means the
    /// shear term downstream is exactly zero and not one comparison in line grouping changes,
    /// instead of every band shifting by a hundredth of a point.
    /// </para>
    /// </summary>
    private const float MinSkewRadians = 0.15f * MathF.PI / 180f;

    /// <summary>
    /// Fewest measured lines that can carry an estimate — roughly one real paragraph. A
    /// handful of detections on a title page or a figure page is not evidence of a page-wide
    /// rotation, and there a wrong global angle costs more than no correction at all.
    /// </summary>
    private const int MinLines = 8;

    /// <summary>
    /// Largest tolerated weighted interquartile spread of the per-line angles (1.5°). A genuine
    /// scan rotation is rigid, so the lines should agree closely; a wide spread means we are
    /// looking at something else — a figure, a rotated caption, curved text — and the median
    /// would be meaningless.
    /// </summary>
    private const float MaxDispersionRadians = 1.5f * MathF.PI / 180f;

    /// <summary>
    /// Detections shorter than this contribute nothing. A short line's angle is mostly corner
    /// quantisation noise, and dropping it outright (rather than letting the length weight
    /// shrink it) also keeps it from padding the <see cref="MinLines"/> count into false
    /// confidence.
    /// </summary>
    private const float MinLineWidth = 40f;

    /// <summary>
    /// Aggregates per-line angles into one page-global estimate, in radians clockwise from
    /// horizontal, or <b>0 when not confident</b>.
    ///
    /// <para>
    /// Returning 0 rather than a nullable is deliberate: 0 is the identity for the shear
    /// correction downstream, so an unconfident page follows exactly the code path it followed
    /// before deskew existed, with no branch for a caller to forget.
    /// </para>
    /// <para>
    /// A <b>length-weighted median</b>, not a mean: a long body line is better evidence of the
    /// sheet's rotation than a short one, while the median keeps a few wildly-off detections
    /// (a vertical label, a figure caption the detector boxed diagonally) from dragging the
    /// result the way an average would.
    /// </para>
    /// </summary>
    /// <param name="lines">
    /// Detected lines in pixmap pixel space. Only lines with a measured quad participate —
    /// <see cref="OcrLine.TrueHeight"/> greater than zero is that marker, which is why it is
    /// used here in preference to <see cref="OcrLine.Angle"/>: an angle of 0 is ambiguous
    /// between "upright" and "unmeasurable", whereas a height of 0 is not.
    /// </param>
    public static float Estimate(IReadOnlyList<OcrLine>? lines)
    {
        if (lines is null || lines.Count < MinLines) return 0f;

        var samples = new List<(float Angle, float Weight)>(lines.Count);
        foreach (var l in lines)
        {
            if (l.TrueHeight <= 0f) continue;
            if (l.Box.W < MinLineWidth) continue;
            samples.Add((l.Angle, l.Box.W));
        }

        // Note there is deliberately NO per-sample filter at MaxSkewRadians here. Discarding
        // samples at the same threshold the page is later judged by would be circular: on a
        // genuinely 6° page it would throw away most of the evidence, leave only the tail that
        // happens to fall under 5°, and hand back a confident-looking 5° that no line actually
        // measured. Per-sample sanity is the detector's job (it rejects quads past a much
        // looser tilt); deciding whether the page as a whole is in range is this method's, and
        // it happens once, at the end, on the aggregate.

        if (samples.Count < MinLines) return 0f;

        samples.Sort(static (a, b) => a.Angle.CompareTo(b.Angle));

        float total = 0f;
        foreach (var s in samples) total += s.Weight;
        if (total <= 0f) return 0f;

        float median = WeightedQuantile(samples, total, 0.5f);
        float spread = WeightedQuantile(samples, total, 0.75f) - WeightedQuantile(samples, total, 0.25f);

        if (spread > MaxDispersionRadians) return 0f;
        // Reject rather than clamp. Clamping a 20° page to 5° would apply a large, wrong shear
        // to every band on it — worse than leaving it alone. The ±5° limit is a statement about
        // what we are confident correcting, not a range to squeeze other answers into.
        if (MathF.Abs(median) > MaxSkewRadians) return 0f;
        if (MathF.Abs(median) < MinSkewRadians) return 0f;
        return median;
    }

    /// <summary>
    /// The angle at which the cumulative weight first reaches <paramref name="q"/> of the
    /// total. <paramref name="samples"/> must already be sorted by angle.
    /// </summary>
    private static float WeightedQuantile(List<(float Angle, float Weight)> samples, float total, float q)
    {
        float target = total * q;
        float acc = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            acc += samples[i].Weight;
            if (acc >= target) return samples[i].Angle;
        }
        return samples[^1].Angle;
    }
}
