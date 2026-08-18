namespace RailReader.Core.Models;

/// <summary>
/// Immutable snapshot of runtime tuning values that Core's services and
/// controllers consume. UI-only fields (font scale, dark mode, minimap
/// dimensions, recent files) live in the consumer's persistent config and
/// are not part of this contract.
///
/// When settings change at the UI layer, the UI rebuilds a new
/// <see cref="CoreSettings"/> and pushes it via the controller's update path
/// (e.g. <c>RailNav.UpdateConfig</c>).
/// </summary>
public sealed record CoreSettings
{
    // Rail / zoom
    public double RailZoomThreshold { get; init; } = 3.0;
    public double SnapDurationMs { get; init; } = 450.0;
    public double LinePadding { get; init; } = 0.2;
    public double JumpPercentage { get; init; } = 25.0;

    // Hold-to-scroll
    public double ScrollSpeedStart { get; init; } = 14.0;
    public double ScrollSpeedMax { get; init; } = 42.0;
    public double ScrollRampTime { get; init; } = 1.5;
    public double DefaultAutoScrollSpeed => (ScrollSpeedStart + ScrollSpeedMax) / 2.0;

    // Auto-scroll (semi-automatic): flow through prose on AutoScrollLinePauseMs, park on
    // entry to a stop-role block / new chunk / new page until an explicit advance keypress.
    public double AutoScrollLinePauseMs { get; init; } = 400.0;
    public bool AutoScrollTriggerEnabled { get; init; }
    public double AutoScrollTriggerDelayMs { get; init; } = 2000.0;

    /// <summary>
    /// Block roles that, when entered by a line advance, park semi-auto scroll (it waits for
    /// an explicit advance keypress). Prose roles not in this set flow through, even across
    /// paragraph/block breaks within a column. Config-derived so the stop set is tunable
    /// without a code change.
    /// </summary>
    public IReadOnlySet<BlockRole> AutoScrollStopClasses { get; init; } = Services.DefaultRoleSets.AutoScrollStop;

    // Analysis
    public int AnalysisLookaheadPages { get; init; } = 2;

    /// <summary>
    /// How many pages on either side of the current page the background
    /// analysis sweep will cover. The sweep re-centres on the current page as
    /// the user navigates, so the whole document is still analysed page-by-page
    /// while reading — but opening a file no longer eagerly analyses every page
    /// up front (which pinned every core for the length of the document).
    /// <c>&lt;= 0</c> restores the legacy whole-document sweep.
    /// </summary>
    public int BackgroundAnalysisWindowPages { get; init; } = 12;

    /// <summary>
    /// Eviction radius for the per-page text and link caches: pages farther
    /// than this from the current page are dropped (they re-extract cheaply on
    /// revisit). Bounds the otherwise-monotonic memory growth of reading a
    /// large document end-to-end. <c>&lt;= 0</c> disables eviction. The small
    /// analysis-geometry cache is intentionally not evicted — it is cheap to
    /// hold and expensive (an ONNX inference) to recompute.
    /// </summary>
    public int PageCacheRadius { get; init; } = 24;

    /// <summary>
    /// Zoom-driven rasterisation tuning (DPI cap, tier step, floor, pixel-area
    /// ceiling, hysteresis). Resolved from a <see cref="RenderQuality"/> preset
    /// by the consumer's config layer. Defaults to the
    /// <see cref="RenderQuality.Quality"/> preset, which reproduces the
    /// pre-preset hardcoded behaviour (cap 600, tier step 75).
    /// </summary>
    public RenderDpiSettings RenderDpi { get; init; } = RenderDpiSettings.Default;
    public IReadOnlySet<BlockRole> NavigableRoles { get; init; } = Services.DefaultRoleSets.Navigable;
    public IReadOnlySet<BlockRole> CenteringRoles { get; init; } = Services.DefaultRoleSets.Centering;

    // Line detection
    /// <summary>
    /// Detect table rows: route <see cref="BlockRole.Table"/> blocks through per-row
    /// line detection (one line per table row) instead of collapsing the whole table
    /// to a single atomic line. This lets rail mode step a table row-by-row — essential
    /// for reading financial statements at high magnification. Set <c>false</c> to
    /// restore the legacy whole-table-as-one-line behaviour.
    /// </summary>
    public bool TableRowReading { get; init; } = true;

    /// <summary>
    /// Split each table row into navigable cells (requires <see cref="TableRowReading"/>;
    /// has no effect on non-table blocks). When on, a table row's
    /// <see cref="LineInfo.Cells"/> is populated so rail mode can step cell-to-cell
    /// horizontally — following "label …… value" across the whitespace gap at
    /// magnification. Off by default: row reading alone already lets the reader step
    /// table rows; cell stepping is the opt-in next level. Cell detection runs only when
    /// both flags are on, and only for <see cref="BlockRole.Table"/> blocks.
    /// </summary>
    public bool CellNavigation { get; init; }

    /// <summary>
    /// Correct page skew when grouping OCR'd text into lines. A page that went through a
    /// sheet feeder is routinely a fraction of a degree off square, and on tightly-set body
    /// text that is enough for mid-Y clustering to interleave adjacent lines and fuse a whole
    /// paragraph into one or two rail units.
    ///
    /// <para>
    /// When on, a page-global baseline angle is recovered from the OCR detector's own rotated
    /// quads — capped at ±5°, behind a confidence gate, and snapped to zero when near-upright
    /// — and applied as a shear <b>inside line grouping only</b>. No pixels are rotated and no
    /// rotated geometry leaves the pipeline, so glyph boxes, annotations, search and rail
    /// framing are unaffected.
    /// </para>
    /// <para>
    /// Applies only to pages that went through OCR. A page read from its own text layer was
    /// never skewed to begin with, and with <see cref="Services.OcrMode.Off"/> there is
    /// nothing to estimate an angle from — so a digital document pays nothing either way.
    /// </para>
    /// </summary>
    public bool DeskewOcrLines { get; init; } = true;

    // Visual effects (per-document defaults — UI may override per doc)
    public ColourEffect ColourEffect { get; init; } = ColourEffect.None;
    public double ColourEffectIntensity { get; init; } = 1.0;
    public bool MotionBlur { get; init; } = true;
    public double MotionBlurIntensity { get; init; } = 0.33;
    public bool PixelSnapping { get; init; } = true;
    public bool LineFocusBlur { get; init; }
    public double LineFocusBlurIntensity { get; init; } = 0.5;
    public bool LineHighlightEnabled { get; init; } = true;
    public LineHighlightTint LineHighlightTint { get; init; } = LineHighlightTint.Auto;
    public double LineHighlightOpacity { get; init; } = 0.25;
    public bool MarginCropping { get; init; }

    // VLM (vision-language model) — empty endpoint disables
    public string? VlmEndpoint { get; init; }
    public string? VlmModel { get; init; }
    public string? VlmApiKey { get; init; }
    public bool VlmStructuredOutput { get; init; }

    /// <summary>
    /// Per-tool selected index into <see cref="AnnotationInteractionHandler.AnnotationColors"/>
    /// (the fixed five-colour palette), for tools the user has switched off their built-in
    /// default. Empty by default — a tool absent here keeps
    /// <see cref="AnnotationInteractionHandler"/>'s own historical default, so an existing
    /// config file with no annotation-colour section round-trips unchanged. The UI shell reads
    /// the live selection back via <see cref="AnnotationInteractionHandler.ColorIndices"/> to
    /// persist it (Core itself never writes to disk).
    /// </summary>
    public IReadOnlyDictionary<AnnotationTool, int> AnnotationColorIndices { get; init; } =
        new Dictionary<AnnotationTool, int>();
}
