namespace RailReader.Core.Analysis;

/// <summary>
/// Shared input-normalisation constants used by the ONNX preprocessing
/// pipelines. Kept in one place so a fourth analyzer reusing the same
/// statistics doesn't redefine them.
/// </summary>
internal static class NormalizationConstants
{
    /// <summary>ImageNet per-channel mean (RGB), used by PP-DocLayout-S and most
    /// detectors trained on ImageNet-pretrained backbones.</summary>
    public static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];

    /// <summary>ImageNet per-channel std (RGB), paired with <see cref="ImageNetMean"/>.</summary>
    public static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];
}
