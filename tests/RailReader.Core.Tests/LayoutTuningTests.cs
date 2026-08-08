using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Covers the per-instance <see cref="LayoutTuning"/> overrides of the values that
/// used to be reachable only as <see cref="LayoutConstants"/> compile-time constants
/// (issue #89).
/// </summary>
public class LayoutTuningTests
{
    private static readonly IReadOnlyList<LayoutClassDescriptor> ClassTable =
    [
        new LayoutClassDescriptor(0, "text", BlockRole.Text),
    ];

    [Fact]
    public void Default_MatchesLayoutConstants()
    {
        var t = LayoutTuning.Default;
        Assert.Equal(LayoutConstants.ConfidenceThreshold, t.ConfidenceThreshold);
        Assert.Equal(LayoutConstants.NmsIouThreshold, t.NmsIouThreshold);
        Assert.Equal(LayoutConstants.DarkLuminanceThreshold, t.DarkLuminanceThreshold);
        Assert.Equal(LayoutConstants.DensityThresholdFraction, t.DensityThresholdFraction);
        Assert.Equal(LayoutConstants.MinLineHeightPx, t.MinLineHeightPx);
        Assert.Equal(LayoutConstants.MinDetectionSizePx, t.MinDetectionSizePx);
    }

    [Fact]
    public void TryBuildBlock_LowerConfidenceThreshold_KeepsFaintDetection()
    {
        const float confidence = 0.2f; // below the 0.4 default

        Assert.False(LayoutAnalyzer.TryBuildBlock(0, confidence, 0, 0, 100, 100,
            200, 200, 1f, 1f, ClassTable, order: 0, LayoutTuning.Default, out _));

        Assert.True(LayoutAnalyzer.TryBuildBlock(0, confidence, 0, 0, 100, 100,
            200, 200, 1f, 1f, ClassTable, order: 0,
            LayoutTuning.Default with { ConfidenceThreshold = 0.15f }, out var block));
        Assert.Equal(confidence, block.Confidence);
    }

    [Fact]
    public void TryBuildBlock_HigherConfidenceThreshold_RejectsDetection()
    {
        Assert.False(LayoutAnalyzer.TryBuildBlock(0, 0.5f, 0, 0, 100, 100,
            200, 200, 1f, 1f, ClassTable, order: 0,
            LayoutTuning.Default with { ConfidenceThreshold = 0.9f }, out _));
    }

    [Fact]
    public void TryBuildBlock_MinDetectionSize_IsHonoured()
    {
        // 8×8 detection: kept at the default 5px floor, rejected at a 10px floor.
        Assert.True(LayoutAnalyzer.TryBuildBlock(0, 0.9f, 0, 0, 8, 8,
            200, 200, 1f, 1f, ClassTable, order: 0, LayoutTuning.Default, out _));

        Assert.False(LayoutAnalyzer.TryBuildBlock(0, 0.9f, 0, 0, 8, 8,
            200, 200, 1f, 1f, ClassTable, order: 0,
            LayoutTuning.Default with { MinDetectionSizePx = 10f }, out _));
    }

    [Fact]
    public void FindLineRuns_MinLineHeight_IsHonoured()
    {
        // A single 4-row run: survives the default 3px floor, dropped at a 5px floor.
        var densities = new float[12];
        for (int i = 4; i < 8; i++) densities[i] = 1f;

        Assert.Single(LineDetector.FindLineRuns(densities));
        Assert.Empty(LineDetector.FindLineRuns(densities, minLineHeightPx: 5));
    }

    [Fact]
    public void ComputeRowDensities_DarkLuminanceThreshold_IsHonoured()
    {
        // Mid-grey (180) rows: above the default ink threshold (160), below a raised one.
        const int w = 4, h = 2;
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)180);

        Assert.All(LineDetector.ComputeRowDensities(rgb, w, 0, 0, w, h), d => Assert.Equal(0f, d));
        Assert.All(LineDetector.ComputeRowDensities(rgb, w, 0, 0, w, h, darkThreshold: 200f),
            d => Assert.Equal(1f, d));
    }

    [Fact]
    public void DetectLines_PixelFallback_UsesSuppliedTuning()
    {
        // 20×20 page, mid-grey band in the middle. With the default ink threshold nothing
        // is dark enough to project a line; raising it makes the band a detected line.
        const int w = 20, h = 20;
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)255);
        for (int y = 8; y < 14; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = 180;
            }

        var block = new LayoutBlock { BBox = new BBox(0, 0, w, h), Role = BlockRole.Text };

        var loose = LineDetector.DetectLines(block, charBoxes: null, rgb, w, h, 1f, 1f,
            tuning: LayoutTuning.Default with { DarkLuminanceThreshold = 200f });
        Assert.Single(loose);
        Assert.InRange(loose[0].Y, 8f, 14f);

        Assert.Empty(LineDetector.DetectLines(block, charBoxes: null, rgb, w, h, 1f, 1f));
    }
}
