using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Covers the per-instance <see cref="LayoutDetectionTuning"/> / <see cref="LineDetectionTuning"/>
/// overrides of the values that used to be reachable only as <see cref="LayoutConstants"/>
/// compile-time constants (issue #89).
/// </summary>
public class TuningOverrideTests
{
    private static readonly IReadOnlyList<LayoutClassDescriptor> ClassTable =
    [
        new LayoutClassDescriptor(0, "text", BlockRole.Text),
    ];

    [Fact]
    public void Defaults_MatchLayoutConstants()
    {
        var detection = LayoutDetectionTuning.Default;
        Assert.Equal(LayoutConstants.ConfidenceThreshold, detection.ConfidenceThreshold);
        Assert.Equal(LayoutConstants.NmsIouThreshold, detection.NmsIouThreshold);
        Assert.Equal(LayoutConstants.MinDetectionSizePx, detection.MinDetectionSizePx);

        var lines = LineDetectionTuning.Default;
        Assert.Equal(LayoutConstants.DarkLuminanceThreshold, lines.DarkLuminanceThreshold);
        Assert.Equal(LayoutConstants.DensityThresholdFraction, lines.DensityThresholdFraction);
        Assert.Equal(LayoutConstants.MinLineHeightPx, lines.MinLineHeightPx);
    }

    // The default threshold is 0.4: a 0.2 detection is dropped by it, kept once the
    // threshold is lowered past it, and a 0.5 detection is dropped once it is raised.
    [Theory]
    [InlineData(0.2f, LayoutConstants.ConfidenceThreshold, false)]
    [InlineData(0.2f, 0.15f, true)]
    [InlineData(0.5f, 0.9f, false)]
    public void TryBuildBlock_ConfidenceThreshold_IsReadFromTuning(
        float confidence, float threshold, bool kept)
    {
        bool built = LayoutAnalyzer.TryBuildBlock(0, confidence, 0, 0, 100, 100,
            200, 200, 1f, 1f, ClassTable, order: 0,
            LayoutDetectionTuning.Default with { ConfidenceThreshold = threshold }, out var block);

        Assert.Equal(kept, built);
        if (kept) Assert.Equal(confidence, block.Confidence);
    }

    [Fact]
    public void TryBuildBlock_MinDetectionSize_IsHonoured()
    {
        // 8×8 detection: kept at the default 5px floor, rejected at a 10px floor.
        Assert.True(LayoutAnalyzer.TryBuildBlock(0, 0.9f, 0, 0, 8, 8,
            200, 200, 1f, 1f, ClassTable, order: 0, LayoutDetectionTuning.Default, out _));

        Assert.False(LayoutAnalyzer.TryBuildBlock(0, 0.9f, 0, 0, 8, 8,
            200, 200, 1f, 1f, ClassTable, order: 0,
            LayoutDetectionTuning.Default with { MinDetectionSizePx = 10f }, out _));
    }

    [Fact]
    public void FindLineRuns_MinLineHeight_IsReadFromTuning()
    {
        // A single 4-row run: survives the default 3px floor, dropped at a 5px floor.
        var densities = new float[12];
        for (int i = 4; i < 8; i++) densities[i] = 1f;

        Assert.Single(LineDetector.FindLineRuns(densities, LineDetectionTuning.Default));
        Assert.Empty(LineDetector.FindLineRuns(densities,
            LineDetectionTuning.Default with { MinLineHeightPx = 5 }));
    }

    [Fact]
    public void FindLineRuns_DensityThresholdFraction_IsReadFromTuning()
    {
        // Two dense bands separated by three faint rows (30% density). A permissive fraction
        // keeps the faint gap above threshold, so the whole block reads as one run; raising
        // the fraction past the gap splits it into the two real lines. The faint rows sit in
        // the middle deliberately — the top/bottom recovery pass would resurrect them at an
        // edge regardless of the fraction.
        var densities = new float[11];
        for (int i = 0; i < 11; i++) densities[i] = i is >= 4 and < 7 ? 0.3f : 1f;

        Assert.Single(LineDetector.FindLineRuns(densities,
            LineDetectionTuning.Default with { DensityThresholdFraction = 0.01f }));

        var strict = LineDetector.FindLineRuns(densities,
            LineDetectionTuning.Default with { DensityThresholdFraction = 0.9f });
        Assert.Equal(2, strict.Count);
        Assert.All(strict, r => Assert.Equal(4, r.Height));
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
            tuning: LineDetectionTuning.Default with { DarkLuminanceThreshold = 200f });
        Assert.Single(loose);
        Assert.InRange(loose[0].Y, 8f, 14f);

        Assert.Empty(LineDetector.DetectLines(block, charBoxes: null, rgb, w, h, 1f, 1f));
    }
}
