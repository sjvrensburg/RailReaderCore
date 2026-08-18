using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AppConfigTests
{
    [Fact]
    public void DefaultConfig_HasExpectedValues()
    {
        var config = new AppConfig();
        Assert.Equal(3.0, config.RailZoomThreshold);
        Assert.Equal(450.0, config.SnapDurationMs);
        Assert.True(config.PixelSnapping);
        Assert.NotEmpty(config.NavigableRoles);
        Assert.Equal(180, config.MinimapWidth);
        Assert.Equal(240, config.MinimapHeight);
        Assert.Equal(10, config.MinimapMarginRight);
        Assert.Equal(10, config.MinimapMarginBottom);
    }

    [Fact]
    public void DefaultConfig_HasAnalysisWindowAndCacheDefaults()
    {
        var config = new AppConfig();
        Assert.Equal(12, config.BackgroundAnalysisWindowPages);
        Assert.Equal(24, config.PageCacheRadius);
    }

    [Fact]
    public void ToCoreSettings_MapsAnalysisWindowAndCacheRadius()
    {
        var config = new AppConfig { BackgroundAnalysisWindowPages = 5, PageCacheRadius = 9 };
        var settings = config.ToCoreSettings();
        Assert.Equal(5, settings.BackgroundAnalysisWindowPages);
        Assert.Equal(9, settings.PageCacheRadius);
    }

    [Fact]
    public void PropertySetting_WorksCorrectly()
    {
        var config = new AppConfig { RailZoomThreshold = 5.0 };
        config.SnapDurationMs = 500.0;
        config.PixelSnapping = false;

        Assert.Equal(5.0, config.RailZoomThreshold);
        Assert.Equal(500.0, config.SnapDurationMs);
        Assert.False(config.PixelSnapping);
    }

    [Fact]
    public void ToCoreSettings_DefaultHasNoAnnotationColorOverrides()
    {
        var config = new AppConfig();
        Assert.Empty(config.ToCoreSettings().AnnotationColorIndices);
    }

    [Fact]
    public void SetAnnotationColorIndices_RoundTripsThroughToCoreSettings()
    {
        var config = new AppConfig();
        config.SetAnnotationColorIndices(new Dictionary<AnnotationTool, int>
        {
            [AnnotationTool.Highlight] = 3,
            [AnnotationTool.Pen] = 4,
        });

        var settings = config.ToCoreSettings();
        Assert.Equal(3, settings.AnnotationColorIndices[AnnotationTool.Highlight]);
        Assert.Equal(4, settings.AnnotationColorIndices[AnnotationTool.Pen]);
    }

    [Fact]
    public void ToCoreSettings_IgnoresUnknownAnnotationToolNames()
    {
        // Simulates loading a config written by a future build with a tool this build
        // doesn't know about — must not throw, must just drop the unrecognised entry.
        var config = new AppConfig();
        config.AnnotationColorIndices["NotARealTool"] = 2;
        config.AnnotationColorIndices[AnnotationTool.Rectangle.ToString()] = 1;

        var settings = config.ToCoreSettings();
        Assert.Single(settings.AnnotationColorIndices);
        Assert.Equal(1, settings.AnnotationColorIndices[AnnotationTool.Rectangle]);
    }

    [Fact]
    public void RecentFiles_AddAndRetrieve()
    {
        var config = new AppConfig();
        config.AddRecentFile("/tmp/test.pdf");

        var position = config.GetReadingPosition("/tmp/test.pdf");
        Assert.NotNull(position);
        Assert.Equal("/tmp/test.pdf", position.FilePath);
    }

    [Fact]
    public void SaveReadingPosition_UpdatesExisting()
    {
        var config = new AppConfig();
        config.AddRecentFile("/tmp/test.pdf");
        config.SaveReadingPosition("/tmp/test.pdf", page: 5, zoom: 2.0, offsetX: -100, offsetY: -50);

        var pos = config.GetReadingPosition("/tmp/test.pdf");
        Assert.NotNull(pos);
        Assert.Equal(5, pos.Page);
        Assert.Equal(2.0, pos.Zoom);
    }
}
