namespace RailReader.Core.Services;

/// <summary>
/// Model-agnostic tuning constants used by the post-processing pipeline. Per-model
/// values (input size, class table, role mappings) live on the analyzer via
/// <see cref="LayoutModelCapabilities"/>.
/// </summary>
public static class LayoutConstants
{
    public const float ConfidenceThreshold = 0.4f;
    public const float NmsIouThreshold = 0.5f;
    public const float DarkLuminanceThreshold = 160.0f;
    public const float DensityThresholdFraction = 0.15f;
    public const int MinLineHeightPx = 3;
}
