using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Per-view state extracted from <see cref="DocumentState"/> — Phase 0 of the
/// multi-viewport split (see <c>docs/multi-viewport-design.md</c>). Today each
/// <see cref="DocumentState"/> embeds exactly one "primary" viewport and delegates
/// its public camera / rendered-page surface to it, so this change ships invisibly.
/// Subsequent phases let a document own several of these (split-pane / detached-window
/// reading), each with an independent camera and rasterised-page cache.
///
/// <para>This cluster holds the camera and everything whose correctness depends on the
/// camera's zoom: the rasterised page (rendered at this view's DPI, since DPI ∝ zoom),
/// the render-DPI state machine, and the page-prefetch buffer. Rail navigation, current
/// page, history, and display preferences move here in later increments.</para>
/// </summary>
public sealed class Viewport
{
    internal Viewport(CoreSettings config)
    {
        // Rail needs config at construction, so build it here rather than leaving the
        // member null-initialised and assigned later by the owner (a NRE footgun once a
        // future phase constructs viewports outside DocumentState's ctor).
        Rail = new RailNav(config);
    }

    /// <summary>Camera (pan/zoom/offset) for this view.</summary>
    public Camera Camera { get; } = new();

    /// <summary>This view's viewport size in px. The controller keeps an ambient size and pushes it
    /// here. <b>Not read yet</b> — the controller's <c>GetViewportSize()</c> still feeds all animation
    /// from its ambient <c>_vpWidth/_vpHeight</c>; this becomes the source of truth when the per-view
    /// Tick reads it (later increment). In the single-window world it equals the controller's; per-view
    /// sizing diverges once detached surfaces manage their own dimensions (Phase 2).</summary>
    public double Width { get; private set; } = 1200;
    public double Height { get; private set; } = 900;

    /// <summary>Sets this view's size (ignores non-positive dimensions, matching the controller).</summary>
    public void SetSize(double w, double h)
    {
        if (w > 0) Width = w;
        if (h > 0) Height = h;
    }

    /// <summary>Rail navigation state for this view (built in the constructor).</summary>
    public RailNav Rail { get; }

    /// <summary>Smooth zoom/pan animation state for this view.</summary>
    internal ZoomAnimationController Zoom { get; } = new();

    /// <summary>Non-rail page-edge hold state (hold arrow at the page edge → page advance) for this view.</summary>
    internal EdgeHoldStateMachine PageEdgeHold { get; } = new();

    /// <summary>Active free-pan (Ctrl+drag) rail-pause snapshot for this view, or null when not paused.</summary>
    internal RailPauseState? RailPause { get; set; }

    // --- Page position + dimensions (this view's current page). Backing storage for
    //     DocumentState's delegated CurrentPage/PageWidth/PageHeight, which keep the
    //     SetField/StateChanged/eviction logic and write here via ref (Phase 0). ---
    internal int CurrentPageBacking;
    internal double PageWidthBacking;
    internal double PageHeightBacking;

    // --- Per-view display prefs + pending state. Backing storage for DocumentState's
    //     delegated SetField properties (written via ref). ---
    internal bool DebugOverlayBacking;
    internal bool PendingRailSetupBacking;
    internal ColourEffect ColourEffectBacking;
    internal bool LineFocusBlurBacking;
    internal bool LineHighlightEnabledBacking = true;
    internal bool MarginCroppingBacking;

    /// <summary>When set, this page was reached via rail navigation and should be skipped
    /// if analysis reveals no navigable blocks. Cleared on landing.</summary>
    public PendingPageSkip? PendingSkip { get; set; }

    /// <summary>Lookahead pages queued for analysis for this view.</summary>
    public Queue<int> PendingAnalysis { get; } = new();

    // Navigation history (back/forward) — per-view so each viewport navigates independently.
    internal readonly Stack<int> BackStack = new();
    internal readonly Stack<int> ForwardStack = new();

    // --- Rasterised page output (rendered at THIS view's DPI) ---

    /// <summary>The cached rendered page bitmap.</summary>
    public IRenderedPage? CachedPage { get; internal set; }

    /// <summary>The DPI <see cref="CachedPage"/> was rendered at.</summary>
    public int CachedDpi { get; internal set; }

    /// <summary>Small pre-scaled thumbnail used by the minimap (≤200×280 px).</summary>
    public IRenderedPage? MinimapPage { get; internal set; }

    /// <summary>
    /// Set to true when a DPI re-render completes. The next animation frame picks
    /// this up and invalidates the page layer atomically with the camera update,
    /// avoiding mid-frame bitmap swaps.
    /// </summary>
    public bool DpiRenderReady { get; internal set; }

    /// <summary>
    /// Called on the UI thread when a DPI re-render completes, so the view can
    /// request an animation frame to pick up the new bitmap.
    /// </summary>
    public Action? OnDpiRenderComplete { get; set; }

    // --- Render-DPI state machine (driven by DocumentState's render methods) ---

    /// <summary>Active render-quality tuning (DPI cap / tier step / floor / pixel-area
    /// ceiling / hysteresis). Updated at runtime via OnRenderQualityChanged.</summary>
    internal RenderDpiSettings RenderDpi { get; set; } = RenderDpiSettings.Default;

    /// <summary>Set when a render-quality change arrives while PDFium is busy, so the
    /// forced re-render is retried from the animation tick once it frees.</summary>
    internal bool RenderDpiDirty { get; set; }

    /// <summary>True while a DPI re-render task is in flight.</summary>
    internal bool DpiRenderPending { get; set; }

    // --- Page prefetch (seamless auto-scroll page transitions) ---

    /// <summary>The prefetched next-page bitmaps, consumed by the next matching LoadPageBitmap.</summary>
    internal PrefetchedPageData? Prefetched { get; set; }

    /// <summary>True while a prefetch task is in flight.</summary>
    internal bool PrefetchPending { get; set; }

    internal sealed record PrefetchedPageData(
        int PageIndex, int Dpi, IRenderedPage Page, IRenderedPage Minimap,
        double PageWidth, double PageHeight) : IDisposable
    {
        public void Dispose() { Page.Dispose(); Minimap.Dispose(); }
    }
}
