using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class RenderQualityTests
{
    // --- Preset → DPI-parameter mapping (the 7-preset schema) ---

    [Theory]
    [InlineData(RenderQuality.Ultra, 800, 50)]
    [InlineData(RenderQuality.Quality, 600, 75)]
    [InlineData(RenderQuality.High, 525, 85)]
    [InlineData(RenderQuality.Balanced, 450, 100)]
    [InlineData(RenderQuality.Medium, 400, 125)]
    [InlineData(RenderQuality.Performance, 350, 150)]
    public void ForPreset_MapsBuiltInPresetsToSchema(RenderQuality quality, int expectedMaxDpi, int expectedStep)
    {
        var s = RenderDpiSettings.ForPreset(quality);
        Assert.Equal(expectedMaxDpi, s.MaxDpi);
        Assert.Equal(expectedStep, s.TierStep);
    }

    [Fact]
    public void ForPreset_Custom_UsesSuppliedValues()
    {
        var s = RenderDpiSettings.ForPreset(RenderQuality.Custom, customMaxDpi: 700, customTierStep: 60);
        Assert.Equal(700, s.MaxDpi);
        Assert.Equal(60, s.TierStep);
    }

    [Fact]
    public void ForPreset_Custom_GuardsAgainstInvalidInputs()
    {
        // Tier step must be >= 1 and the cap must not fall below the floor,
        // otherwise the clamp/round in CalculateRenderDpi would throw or misbehave.
        var s = RenderDpiSettings.ForPreset(RenderQuality.Custom, customMaxDpi: 10, customTierStep: 0);
        Assert.Equal(RenderDpiSettings.DefaultMinDpi, s.MaxDpi);
        Assert.Equal(1, s.TierStep);
    }

    [Fact]
    public void Default_EqualsQualityPreset()
    {
        Assert.Equal(RenderDpiSettings.ForPreset(RenderQuality.Quality), RenderDpiSettings.Default);
    }

    // --- CalculateRenderDpi: quantisation + clamping ---

    // A US-Letter page (612×792 pt). At <= 600 DPI a full-page bitmap is ~34 MP,
    // well under the 64 MP default ceiling, so the ceiling never interferes here.
    private const double LetterW = 612, LetterH = 792;

    [Theory]
    [InlineData(0.1, 150)]   // below floor → clamped up to MinDpi
    [InlineData(1.0, 150)]   // raw 150 → snaps to 150
    [InlineData(2.0, 300)]   // raw 300 → snaps to 300
    [InlineData(4.0, 600)]   // raw 600 → snaps to 600 (== cap)
    [InlineData(10.0, 600)]  // raw 1500 → clamped down to MaxDpi
    public void CalculateRenderDpi_Quality_ReproducesLegacyBehaviour(double zoom, int expected)
    {
        int dpi = DocumentModel.CalculateRenderDpi(zoom, LetterW, LetterH, RenderDpiSettings.Default);
        Assert.Equal(expected, dpi);
    }

    [Theory]
    [InlineData(5.0, 750)]  // raw 750 → snaps to 750 on the 50-step grid
    [InlineData(6.0, 800)]  // raw 900 → snaps to 900 → clamped to the 800 cap
    public void CalculateRenderDpi_Ultra_UsesPresetStepAndCap(double zoom, int expected)
    {
        int dpi = DocumentModel.CalculateRenderDpi(zoom, LetterW, LetterH, RenderDpiSettings.ForPreset(RenderQuality.Ultra));
        Assert.Equal(expected, dpi);
    }

    // --- CalculateRenderDpi: pixel-area (megapixel) ceiling ---
    //
    // A 720×720 pt page is exactly 10"×10" = 100 sq in, so the area-limited DPI
    // is 100·√(MaxMegapixels): MP=4 → 200 DPI, MP=1 → 100 DPI.
    private const double TenInchPt = 720;

    private static RenderDpiSettings Settings(int maxDpi, int step, double maxMp) => new()
    {
        MaxDpi = maxDpi,
        TierStep = step,
        MinDpi = RenderDpiSettings.DefaultMinDpi,
        MaxMegapixels = maxMp,
        UpscaleHysteresis = RenderDpiSettings.DefaultUpscaleHysteresis,
        DownscaleHysteresis = RenderDpiSettings.DefaultDownscaleHysteresis,
    };

    [Fact]
    public void CalculateRenderDpi_MegapixelCeiling_LowersDpiOnLargePage()
    {
        // Zoom alone would clamp to the 800 cap, but the 4 MP ceiling on a
        // 10"×10" page caps the effective DPI at 200.
        int dpi = DocumentModel.CalculateRenderDpi(zoom: 6.0, TenInchPt, TenInchPt, Settings(800, 50, maxMp: 4.0));
        Assert.Equal(200, dpi);
    }

    [Fact]
    public void CalculateRenderDpi_MegapixelCeiling_NeverDropsBelowFloor()
    {
        // A 1 MP ceiling implies 100 DPI on this page, but the readability floor
        // (150) wins — memory yields to legibility for a pathological page.
        int dpi = DocumentModel.CalculateRenderDpi(zoom: 6.0, TenInchPt, TenInchPt, Settings(800, 50, maxMp: 1.0));
        Assert.Equal(RenderDpiSettings.DefaultMinDpi, dpi);
    }

    [Fact]
    public void CalculateRenderDpi_MegapixelCeiling_DisabledWhenNonPositive()
    {
        int dpi = DocumentModel.CalculateRenderDpi(zoom: 6.0, TenInchPt, TenInchPt, Settings(800, 50, maxMp: 0));
        Assert.Equal(800, dpi); // unconstrained by area → hits the cap
    }

    [Fact]
    public void CalculateRenderDpi_MegapixelCeiling_SkippedWhenPageSizeUnknown()
    {
        int dpi = DocumentModel.CalculateRenderDpi(zoom: 6.0, pageWidthPts: 0, pageHeightPts: 0, Settings(800, 50, maxMp: 4.0));
        Assert.Equal(800, dpi); // no dims → ceiling can't be computed → cap applies
    }

    [Fact]
    public void CalculateRenderDpi_DefaultSettings_ReturnsPositiveDpiNeverZero()
    {
        // A default(RenderDpiSettings) has all-zero fields. The public entry point
        // must not return 0 (which would be passed to RenderPage) — it clamps to a
        // positive floor instead.
        int dpi = DocumentModel.CalculateRenderDpi(zoom: 2.0, LetterW, LetterH, default);
        Assert.True(dpi >= 1, $"expected positive DPI, got {dpi}");
    }

    [Fact]
    public void CalculateRenderDpi_InvertedBounds_DoesNotThrow()
    {
        // MinDpi > MaxDpi would make Math.Clamp throw; the method must normalise.
        var inverted = new RenderDpiSettings
        {
            MinDpi = 200, MaxDpi = 100, TierStep = 75, MaxMegapixels = 0,
            UpscaleHysteresis = 1.5, DownscaleHysteresis = 0.5,
        };
        var ex = Record.Exception(() => DocumentModel.CalculateRenderDpi(2.0, LetterW, LetterH, inverted));
        Assert.Null(ex);
    }

    // --- AppConfig → CoreSettings bridge ---

    [Fact]
    public void AppConfig_Default_MapsToQualityPreset()
    {
        var settings = new AppConfig().ToCoreSettings();
        Assert.Equal(RenderDpiSettings.ForPreset(RenderQuality.Quality), settings.RenderDpi);
    }

    [Fact]
    public void AppConfig_ToCoreSettings_MapsPreset()
    {
        var settings = new AppConfig { RenderQuality = RenderQuality.Ultra }.ToCoreSettings();
        Assert.Equal(800, settings.RenderDpi.MaxDpi);
        Assert.Equal(50, settings.RenderDpi.TierStep);
    }

    [Fact]
    public void AppConfig_ToCoreSettings_MapsCustomPresetValues()
    {
        var settings = new AppConfig
        {
            RenderQuality = RenderQuality.Custom,
            CustomMaxRenderDpi = 720,
            CustomRenderTierStep = 60,
        }.ToCoreSettings();
        Assert.Equal(720, settings.RenderDpi.MaxDpi);
        Assert.Equal(60, settings.RenderDpi.TierStep);
    }

    [Fact]
    public void AppConfig_WithoutRenderQuality_DeserializesToQualityDefault()
    {
        // A config saved before the preset existed: the absent key must fall back
        // to the property initializer (Quality), not a zero-valued enum surprise.
        const string json = """{ "schema_version": 2 }""";
        var config = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AppConfig);
        Assert.NotNull(config);
        Assert.Equal(RenderQuality.Quality, config!.RenderQuality);
    }
}
