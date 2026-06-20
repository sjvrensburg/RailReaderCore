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
    internal Viewport(CoreSettings config, DocumentState owner)
    {
        // Back-reference to the owning document (set at construction so the camera-geometry /
        // render-path methods that move onto Viewport in later increments can reach doc-level data
        // — Pdf, caches, DocumentContentFraction, marshaller/logger). Always non-null.
        Owner = owner;
        // Rail and AutoScroll need config at construction, so build them here rather than leaving the
        // members null-initialised and assigned later by the owner (a NRE footgun once a future phase
        // constructs viewports outside DocumentState's ctor).
        Rail = new RailNav(config);
        AutoScroll = new AutoScrollController(config);
    }

    /// <summary>The document this view belongs to. Gives the per-view camera/render methods
    /// (moved onto <see cref="Viewport"/> in the capstone) read access to doc-level data:
    /// <see cref="DocumentState.Pdf"/>, the page caches, <see cref="DocumentState.DocumentContentFraction"/>,
    /// and the marshaller/logger. Set once in the constructor.</summary>
    internal DocumentState Owner { get; }

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

    /// <summary>Auto-scroll + jump-mode state for this view (built in the constructor).</summary>
    internal AutoScrollController AutoScroll { get; }

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

    // ---------------------------------------------------------------------------------------------
    //  Camera geometry (capstone slice 2). These read this view's own Camera/Rail/page dimensions
    //  and the document-wide content fraction (via Owner); DocumentState keeps thin public
    //  delegating wrappers so call sites are untouched.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the page-space rectangle used by fit/centre operations.
    /// With margin cropping off (or no analysis yet), this is the full page.
    /// With margin cropping on, it's the content region of the page.
    /// </summary>
    public (double X, double Y, double W, double H) GetFitRect()
    {
        if (MarginCroppingBacking && Owner.DocumentContentFraction is { } f)
        {
            return (f.X * PageWidthBacking, f.Y * PageHeightBacking,
                    f.W * PageWidthBacking, f.H * PageHeightBacking);
        }
        return (0, 0, PageWidthBacking, PageHeightBacking);
    }

    /// <summary>
    /// Sub-rail-threshold epsilon: keeps margin-cropping fit zoom strictly
    /// below the rail trigger so cropping never accidentally enters rail mode.
    /// </summary>
    private const double RailThresholdEpsilon = 0.001;

    /// <summary>
    /// Zoom that fits <paramref name="box"/> within the viewport with a uniform margin,
    /// clamped to the camera range. The limiting dimension wins so the whole block shows.
    /// </summary>
    public double ComputeBlockFitZoom(BBox box, double viewportW, double viewportH,
        double marginFraction = 0.08)
    {
        double padW = box.W * (1.0 + 2.0 * marginFraction);
        double padH = box.H * (1.0 + 2.0 * marginFraction);
        if (padW <= 0 || padH <= 0 || viewportW <= 0 || viewportH <= 0) return Camera.Zoom;
        double z = Math.Min(viewportW / padW, viewportH / padH);
        return Math.Clamp(z, Camera.ZoomMin, Camera.ZoomMax);
    }

    /// <summary>
    /// Camera target (zoom + offsets) that centres <paramref name="box"/> in the viewport,
    /// fitting it via <see cref="ComputeBlockFitZoom"/> unless <paramref name="targetZoom"/> is
    /// given. Unlike rail framing this does NOT floor at the rail threshold — a large figure can
    /// be shown whole below 3×. Used by geometric centred framing for non-navigable blocks
    /// (figures/tables/charts) that the rail index can't seat.
    /// </summary>
    public (double Zoom, double OffsetX, double OffsetY) ComputeCenteredFrame(
        BBox box, double viewportW, double viewportH, double? targetZoom = null)
    {
        double z = Math.Clamp(targetZoom ?? ComputeBlockFitZoom(box, viewportW, viewportH),
            Camera.ZoomMin, Camera.ZoomMax);
        double ox = (viewportW - box.W * z) / 2.0 - box.X * z;
        double oy = (viewportH - box.H * z) / 2.0 - box.Y * z;
        return (z, ox, oy);
    }

    public void CenterPage(double windowWidth, double windowHeight)
    {
        if (PageWidthBacking <= 0 || PageHeightBacking <= 0 || windowWidth <= 0 || windowHeight <= 0) return;
        var (rx, ry, rw, rh) = GetFitRect();
        if (rw <= 0 || rh <= 0) return;
        Camera.Zoom = Math.Min(windowWidth / rw, windowHeight / rh);
        Camera.OffsetX = CenteredOffsetX(windowWidth, rx, rw, Camera.Zoom);
        Camera.OffsetY = (windowHeight - rh * Camera.Zoom) / 2.0 - ry * Camera.Zoom;
    }

    public void FitWidth(double windowWidth, double windowHeight)
    {
        if (PageWidthBacking <= 0 || windowWidth <= 0) return;
        var (rx, ry, rw, rh) = GetFitRect();
        if (rw <= 0) return;

        Camera.Zoom = ComputeFitWidthZoom(windowWidth, rw);
        double scaledRectH = rh * Camera.Zoom;
        Camera.OffsetX = CenteredOffsetX(windowWidth, rx, rw, Camera.Zoom);
        Camera.OffsetY = scaledRectH <= windowHeight
            ? (windowHeight - scaledRectH) / 2.0 - ry * Camera.Zoom
            : -ry * Camera.Zoom;
    }

    /// <summary>
    /// Applies the fit-width zoom while keeping the page-space y currently at
    /// the viewport top edge in place. Used when toggling margin cropping at
    /// fit-width zoom so the reading position stays anchored to the top.
    /// Horizontally, content is centred (same as <see cref="FitWidth"/>).
    /// </summary>
    public void FitWidthPreservingTop(double windowWidth, double windowHeight)
    {
        if (PageWidthBacking <= 0 || windowWidth <= 0 || Camera.Zoom <= 0) return;
        double pageTopY = -Camera.OffsetY / Camera.Zoom;

        var (rx, _, rw, _) = GetFitRect();
        double newZoom = ComputeFitWidthZoom(windowWidth, rw);
        if (newZoom <= 0) return;

        Camera.Zoom = newZoom;
        Camera.OffsetX = CenteredOffsetX(windowWidth, rx, rw, newZoom);
        Camera.OffsetY = -pageTopY * newZoom;
        ClampCamera(windowWidth, windowHeight);
    }

    private static double CenteredOffsetX(double windowWidth, double rectX, double rectW, double zoom)
        => (windowWidth - rectW * zoom) / 2.0 - rectX * zoom;

    private double ComputeFitWidthZoom(double windowWidth, double rectW)
    {
        if (rectW <= 0) return Camera.Zoom;

        double maxZoom = Camera.ZoomMax;
        // Keep margin cropping from pushing the user into rail mode on large
        // screens. Only caps when the uncropped fit was itself below the rail
        // threshold — if the user would already be in rail without cropping,
        // cropping shouldn't un-rail them.
        if (MarginCroppingBacking && Rail.ZoomThreshold > 0)
        {
            double uncroppedFit = windowWidth / PageWidthBacking;
            if (uncroppedFit < Rail.ZoomThreshold)
                maxZoom = Math.Min(maxZoom, Rail.ZoomThreshold - RailThresholdEpsilon);
        }
        return Math.Clamp(windowWidth / rectW, Camera.ZoomMin, maxZoom);
    }

    public void ClampCamera(double windowWidth, double windowHeight)
    {
        double scaledW = PageWidthBacking * Camera.Zoom;
        double scaledH = PageHeightBacking * Camera.Zoom;

        if (scaledW <= windowWidth)
            Camera.OffsetX = (windowWidth - scaledW) / 2.0;
        else
            Camera.OffsetX = Math.Clamp(Camera.OffsetX, windowWidth - scaledW, 0);

        if (scaledH <= windowHeight)
            Camera.OffsetY = (windowHeight - scaledH) / 2.0;
        else
            Camera.OffsetY = Math.Clamp(Camera.OffsetY, windowHeight - scaledH, 0);
    }

    public void ApplyZoom(double newZoom, double windowWidth, double windowHeight)
    {
        Camera.Zoom = Math.Clamp(newZoom, Camera.ZoomMin, Camera.ZoomMax);
        UpdateRailZoom(windowWidth, windowHeight);
        if (Rail.Active)
            StartSnap(windowWidth, windowHeight);
        ClampCamera(windowWidth, windowHeight);
    }

    public void UpdateRailZoom(double windowWidth, double windowHeight,
        double? cursorPageX = null, double? cursorPageY = null)
    {
        Rail.UpdateZoom(Camera.Zoom, Camera.OffsetX, Camera.OffsetY, windowWidth, windowHeight,
            cursorPageX, cursorPageY);
    }

    public void StartSnap(double windowWidth, double windowHeight)
    {
        Rail.StartSnapToCurrent(Camera.OffsetX, Camera.OffsetY, Camera.Zoom, windowWidth, windowHeight);
    }

    public void StartSnapPreservingPosition(double windowWidth, double windowHeight,
        double horizontalFraction, double lineScreenY)
    {
        Rail.StartSnapPreservingPosition(Camera.OffsetX, Camera.OffsetY, Camera.Zoom,
            windowWidth, windowHeight, horizontalFraction, lineScreenY);
    }

    public void StartSnapToEnd(double windowWidth, double windowHeight)
    {
        Rail.StartSnapToCurrentEnd(Camera.OffsetX, Camera.OffsetY, Camera.Zoom, windowWidth, windowHeight);
    }
}
