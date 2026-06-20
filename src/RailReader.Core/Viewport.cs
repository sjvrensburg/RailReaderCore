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
    /// <summary>Camera (pan/zoom/offset) for this view.</summary>
    public Camera Camera { get; } = new();

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
