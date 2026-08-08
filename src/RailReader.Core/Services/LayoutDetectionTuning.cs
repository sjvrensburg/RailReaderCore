namespace RailReader.Core.Services;

/// <summary>
/// Per-instance override of the thresholds an <see cref="ILayoutAnalyzer"/> applies to
/// its own detections. Pass one to an analyzer's constructor to tune it for a corpus or
/// a custom model. Every value defaults to the corresponding <see cref="LayoutConstants"/>
/// constant, so <see cref="Default"/> reproduces the built-in behaviour exactly.
///
/// <para>
/// Immutable: derive a variant with <c>with</c>, e.g.
/// <c>LayoutDetectionTuning.Default with { ConfidenceThreshold = 0.25f }</c>.
/// </para>
///
/// <para>
/// The thresholds that shape <i>line</i> detection downstream of the model live in
/// <see cref="LineDetectionTuning"/> — they belong to the post-processing pipeline, which
/// runs the same way regardless of which analyzer produced the blocks.
/// </para>
/// </summary>
public sealed record LayoutDetectionTuning
{
    /// <summary>The built-in values — identical to <see cref="LayoutConstants"/>.</summary>
    public static readonly LayoutDetectionTuning Default = new();

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
    /// Minimum width/height (in pixel space) below which a detection is rejected as
    /// too small to be a meaningful block.
    /// </summary>
    public float MinDetectionSizePx { get; init; } = LayoutConstants.MinDetectionSizePx;
}
