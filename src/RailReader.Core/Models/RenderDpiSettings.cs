namespace RailReader.Core.Models;

/// <summary>
/// Immutable tuning parameters for zoom-driven page rasterisation. Consumed by
/// <see cref="DocumentModel.CalculateRenderDpi"/> (DPI selection + pixel-area
/// ceiling) and the hysteresis gate in
/// <see cref="DocumentModel.UpdateRenderDpiIfNeeded"/>. Built from a
/// <see cref="RenderQuality"/> preset by the consumer's config layer
/// (<c>AppConfig.ToCoreSettings</c>) and carried inside <see cref="CoreSettings"/>.
///
/// Before this type existed, the cap (600), tier step (75), floor (150),
/// hysteresis factors (1.5 / 0.5) and down-guard (150) were hardcoded literals
/// in <c>DocumentModel</c>; the defaults here reproduce that behaviour exactly,
/// which is the <see cref="RenderQuality.Quality"/> preset.
/// </summary>
public readonly record struct RenderDpiSettings
{
    /// <summary>Upper clamp on render DPI (the "DPI cap"). Varies by preset.</summary>
    public int MaxDpi { get; init; }

    /// <summary>DPI quantum the renderer snaps to as zoom changes. Varies by preset.</summary>
    public int TierStep { get; init; }

    /// <summary>Lower clamp on render DPI, and the floor below which downscale re-renders are suppressed.</summary>
    public int MinDpi { get; init; }

    /// <summary>
    /// Pixel-area ceiling (in megapixels) for a full-page bitmap. Caps the
    /// effective DPI on large-format pages so high presets cannot allocate a
    /// runaway bitmap (e.g. Ultra @ 800 DPI on an A0 sheet). <c>&lt;= 0</c> disables.
    /// </summary>
    public double MaxMegapixels { get; init; }

    /// <summary>A re-render up is triggered when the needed DPI exceeds the cached DPI by this factor.</summary>
    public double UpscaleHysteresis { get; init; }

    /// <summary>A re-render down is triggered when the needed DPI drops below the cached DPI by this factor.</summary>
    public double DownscaleHysteresis { get; init; }

    // Defaults shared by every built-in preset (the preset schema only varies
    // MaxDpi and TierStep). Custom presets may override MaxDpi/TierStep but
    // inherit these.
    internal const int DefaultMinDpi = 150;
    internal const double DefaultMaxMegapixels = 64.0;
    internal const double DefaultUpscaleHysteresis = 1.5;
    internal const double DefaultDownscaleHysteresis = 0.5;

    /// <summary>The settings used when no preset is specified — equivalent to <see cref="RenderQuality.Quality"/>.</summary>
    public static readonly RenderDpiSettings Default = ForPreset(RenderQuality.Quality);

    /// <summary>
    /// Resolve a preset to concrete DPI parameters. <paramref name="customMaxDpi"/>
    /// and <paramref name="customTierStep"/> are used only for
    /// <see cref="RenderQuality.Custom"/>.
    /// </summary>
    public static RenderDpiSettings ForPreset(
        RenderQuality quality, int customMaxDpi = 600, int customTierStep = 75) =>
        quality switch
        {
            RenderQuality.Ultra       => Build(800, 50),
            RenderQuality.Quality     => Build(600, 75),
            RenderQuality.High        => Build(525, 85),
            RenderQuality.Balanced    => Build(450, 100),
            RenderQuality.Medium      => Build(400, 125),
            RenderQuality.Performance => Build(350, 150),
            RenderQuality.Custom      => Build(customMaxDpi, customTierStep),
            _                         => Build(600, 75),
        };

    private static RenderDpiSettings Build(int maxDpi, int tierStep) => new()
    {
        // Guard Custom inputs: tier step must be positive, cap must not fall
        // below the floor (an inverted clamp would throw).
        MaxDpi = Math.Max(DefaultMinDpi, maxDpi),
        TierStep = Math.Max(1, tierStep),
        MinDpi = DefaultMinDpi,
        MaxMegapixels = DefaultMaxMegapixels,
        UpscaleHysteresis = DefaultUpscaleHysteresis,
        DownscaleHysteresis = DefaultDownscaleHysteresis,
    };
}
