namespace RailReader.Core.Services;

/// <summary>
/// Per-instance override of the model-agnostic tuning values in
/// <see cref="LayoutConstants"/>. Pass one to an <see cref="ILayoutAnalyzer"/>
/// implementation's constructor (detection thresholds) or to
/// <see cref="BlockPostProcessor.PostProcess"/> / <see cref="LineDetector.DetectLines"/>
/// (pixel-projection thresholds) to tune the pipeline for a corpus or a custom
/// model. Every value defaults to the corresponding <see cref="LayoutConstants"/>
/// constant, so <see cref="Default"/> reproduces the built-in behaviour exactly.
///
/// <para>
/// Immutable: derive a variant with <c>with</c>, e.g.
/// <c>LayoutTuning.Default with { ConfidenceThreshold = 0.25f }</c>.
/// </para>
/// </summary>
public sealed record LayoutTuning
{
    /// <summary>The built-in values — identical to <see cref="LayoutConstants"/>.</summary>
    public static readonly LayoutTuning Default = new();

    /// <summary>
    /// Minimum detection confidence. Detections below this are dropped before NMS.
    /// Lower it to keep faint detections (at the cost of false positives); raise it
    /// for a cleaner but sparser block set.
    /// </summary>
    public float ConfidenceThreshold { get; init; } = LayoutConstants.ConfidenceThreshold;

    /// <summary>
    /// IoU above which the lower-confidence of two overlapping detections is
    /// suppressed by non-maximum suppression.
    /// </summary>
    public float NmsIouThreshold { get; init; } = LayoutConstants.NmsIouThreshold;

    /// <summary>
    /// Luminance below which a rasterised pixel counts as ink, used by the
    /// pixel-projection line detector and the vertical-rule scan.
    /// </summary>
    public float DarkLuminanceThreshold { get; init; } = LayoutConstants.DarkLuminanceThreshold;

    /// <summary>
    /// Fraction of the mean row density a row must exceed to be considered part of
    /// a text line in the pixel-projection fallback.
    /// </summary>
    public float DensityThresholdFraction { get; init; } = LayoutConstants.DensityThresholdFraction;

    /// <summary>Shortest pixel run the projection fallback will accept as a line.</summary>
    public int MinLineHeightPx { get; init; } = LayoutConstants.MinLineHeightPx;

    /// <summary>
    /// Minimum width/height (in pixel space) below which a detection is rejected as
    /// too small to be a meaningful block.
    /// </summary>
    public float MinDetectionSizePx { get; init; } = LayoutConstants.MinDetectionSizePx;
}
