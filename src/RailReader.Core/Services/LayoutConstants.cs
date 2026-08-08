namespace RailReader.Core.Services;

/// <summary>
/// Model-agnostic tuning constants used by the post-processing pipeline. Per-model
/// values (input size, class table, role mappings) live on the analyzer via
/// <see cref="LayoutModelCapabilities"/>.
///
/// <para>
/// These are the <i>defaults</i>. To override any of them for a given analyzer or
/// pipeline run, pass a <see cref="LayoutTuning"/> instead of relying on these
/// constants — every <see cref="LayoutTuning"/> property defaults to the value here.
/// </para>
/// </summary>
public static class LayoutConstants
{
    public const float ConfidenceThreshold = 0.4f;
    public const float NmsIouThreshold = 0.5f;
    public const float DarkLuminanceThreshold = 160.0f;
    public const float DensityThresholdFraction = 0.15f;
    public const int MinLineHeightPx = 3;

    /// <summary>
    /// Minimum width/height (in pixel space) below which a detection is
    /// rejected as too small to be a meaningful block — filters out sliver
    /// detections that survive NMS.
    /// </summary>
    public const float MinDetectionSizePx = 5f;
}
