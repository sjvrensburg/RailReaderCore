namespace RailReader.Core;

/// <summary>
/// Internal tuning constants not exposed to users via AppConfig.
/// User-tunable values live in AppConfig; ONNX-specific values live in LayoutConstants.
/// This file collects internal thresholds that control UX feel but aren't meant to be
/// end-user configurable.
/// </summary>
internal static class CoreTuning
{
    // Navigation
    public const double PanStep = 50.0;
    public const double DestMarginTop = 0.1;
    public const double DestMarginLeft = 0.05;

    // Rail centering
    public const double CenterBlockThreshold = 0.75;

    // Edge-hold state machine
    public const double EdgeHoldMs = 400.0;
    public const double EdgeCooldownMs = 300.0;

    // Short-line reading budget. The forward line-advance fires at the line's right
    // extent, so a line narrower than the viewport puts the camera at the hard edge the
    // instant it is framed — it scrolls little or not at all and earns almost no reading
    // time before advancing (worst at a paragraph's short FINAL line, which then flashes
    // straight into the next chunk). These bound a minimum reading beat for such lines.
    // The beat is computed in PAGE space (zoom-independent) and scaled by the reading
    // pace; see RailNav.LineReadBudgetMs — the same text reads for the same time at any
    // magnification. Whether a line is "short" is judged in SCREEN pixels (lineWidthPx <=
    // windowWidth, i.e. width <= W/zoom): deliberately zoom-dependent. As zoom RISES the
    // threshold shrinks, so FEWER lines qualify — at high magnification even a moderate
    // line must scroll (and earns its reading time). The shortchanged set is largest just
    // above the rail-mode zoom threshold, which is why the bug bites there most.
    public const double MinLineReadMs = 350.0;
    public const double MaxLineReadMs = 1200.0;

    // Zoom animation
    public const double ZoomStep = 1.25;
    public const double ZoomScrollSensitivity = 0.003;
    public const double ZoomAnimationDurationMs = 180.0;
}
