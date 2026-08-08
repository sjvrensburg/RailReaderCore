namespace RailReader.Core.Services;

/// <summary>
/// Per-instance override of the raster thresholds <see cref="LineDetector"/> uses when it
/// falls back to pixel projection (a page with no text layer and no OCR) and when it scans
/// for a table's vertical rules. Pass one to <see cref="LineDetector.DetectLines"/>,
/// <see cref="BlockPostProcessor.PostProcess"/>, or the analysis worker. Every value
/// defaults to the corresponding <see cref="LayoutConstants"/> constant, so
/// <see cref="Default"/> reproduces the built-in behaviour exactly.
///
/// <para>
/// Immutable: derive a variant with <c>with</c>, e.g.
/// <c>LineDetectionTuning.Default with { MinLineHeightPx = 5 }</c>.
/// </para>
///
/// <para>
/// The thresholds the layout model's own detections are filtered by live in
/// <see cref="LayoutDetectionTuning"/>, which is the analyzer's business — these two are
/// deliberately separate types so a value cannot be handed to the half that ignores it.
/// </para>
/// </summary>
public sealed record LineDetectionTuning
{
    /// <summary>The built-in values — identical to <see cref="LayoutConstants"/>.</summary>
    public static readonly LineDetectionTuning Default = new();

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
}
