using RailReader.Core.Commands;
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
public sealed class Viewport : IDisposable
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

    /// <summary>Fires when a per-view property changes (parameter is the property name). Per-view so a
    /// multi-viewport host can tell "which view changed". <see cref="DocumentState.StateChanged"/>
    /// forwards the primary view's events, so existing single-viewport subscribers are unaffected.</summary>
    public Action<string>? StateChanged;

    /// <summary>Fires when this view's page changes (parameter = new page index). Per-view so a detached
    /// pane updates its own chrome; the controller-level <c>PageChanged</c> mirrors the focused view.</summary>
    public Action<int>? PageChanged;

    /// <summary>Fires when this view's rail reading position (block/line) changes. Per-view, mirrored to
    /// the controller-level <c>ReadingPositionChanged</c> for the focused view.</summary>
    public Action<ReadingPosition>? ReadingPositionChanged;

    /// <summary>Sets a backing field and fires <see cref="StateChanged"/> if the value changed.
    /// Mirrors <c>DocumentState.SetField</c> — UI-thread-only, same change-detection.</summary>
    private bool SetField<T>(ref T field, T value, string propertyName)
    {
        Owner.Marshaller.AssertUIThread();
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        StateChanged?.Invoke(propertyName);
        return true;
    }

    /// <summary>Whether this view is "live" — currently visible/focused and worth feeding analysis,
    /// read-ahead, and forced animation frames. The analysis fan-out (§5) and a host's render loop
    /// consult it. Defaults to true so a single-viewport document behaves exactly as before; a
    /// multi-viewport host sets it as views show/hide.</summary>
    public bool IsLive { get; set; } = true;

    /// <summary>Host hook to request an animation frame for THIS view (e.g. after a DPI re-render or
    /// an external camera change). A multi-viewport host wires each surface's invalidate here so a
    /// background tick can wake the right window. Null when no host is attached.</summary>
    public Action? RequestAnimation { get; set; }

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

    // --- Page position + dimensions (this view's current page). Per-view properties:
    //     each viewport sits on its own page rendered at its own size. DocumentState's
    //     CurrentPage/PageWidth/PageHeight delegate here. ---
    internal int CurrentPageBacking;
    internal double PageWidthBacking;
    internal double PageHeightBacking;

    /// <summary>This view's current page index. Setting it evicts now-distant text/link caches
    /// (doc-level, union of all views) and fires <see cref="StateChanged"/>.</summary>
    public int CurrentPage
    {
        get => CurrentPageBacking;
        set
        {
            if (SetField(ref CurrentPageBacking, value, nameof(CurrentPage)))
                Owner.EvictDistantPageCaches();
        }
    }

    /// <summary>This view's current page width in PDF points (set when the page bitmap loads).</summary>
    public double PageWidth
    {
        get => PageWidthBacking;
        set => SetField(ref PageWidthBacking, value, nameof(PageWidth));
    }

    /// <summary>This view's current page height in PDF points (set when the page bitmap loads).</summary>
    public double PageHeight
    {
        get => PageHeightBacking;
        set => SetField(ref PageHeightBacking, value, nameof(PageHeight));
    }

    // --- Per-view display prefs + pending state. Backing storage for DocumentState's
    //     delegated SetField properties (written via ref). ---
    internal bool DebugOverlayBacking;
    internal bool PendingRailSetupBacking;
    internal ColourEffect ColourEffectBacking;
    internal bool LineFocusBlurBacking;
    internal bool LineHighlightEnabledBacking = true;
    internal bool MarginCroppingBacking;

    /// <summary>True while this view is waiting on analysis for its current page before its rail can
    /// be seated. Per-view so each viewport tracks its own pending state; fires <see cref="StateChanged"/>.</summary>
    public bool PendingRailSetup
    {
        get => PendingRailSetupBacking;
        set => SetField(ref PendingRailSetupBacking, value, nameof(PendingRailSetup));
    }

    /// <summary>When set, this page was reached via rail navigation and should be skipped
    /// if analysis reveals no navigable blocks. Cleared on landing.</summary>
    public PendingPageSkip? PendingSkip { get; set; }

    /// <summary>Lookahead pages queued for analysis for this view.</summary>
    public Queue<int> PendingAnalysis { get; } = new();

    // Navigation history (back/forward) — per-view so each viewport navigates independently.
    internal readonly Stack<int> BackStack = new();
    internal readonly Stack<int> ForwardStack = new();

    // --- Rasterised page output (rendered at THIS view's DPI) ---

    /// <summary>Cancels this view's in-flight render/prefetch/DPI-rerender tasks. Per-view (not the
    /// document's) so removing one viewport (future <c>RemoveViewport</c>) cancels only its own
    /// renders; the document's own CTS still guards the shared analysis-submission tasks. Cancelled
    /// and disposed by <see cref="DocumentState.Dispose"/> before the cached bitmaps are freed
    /// (§6 disposal ordering).</summary>
    internal CancellationTokenSource Cts { get; } = new();

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
    //  Render path (capstone slice 3). These rasterise THIS view's page at THIS view's DPI, writing
    //  the per-view CachedPage/DPI/prefetch state and reaching doc-level data (Pdf/marshaller/logger
    //  /caches) through Owner. Background tasks use this view's own Cts. DocumentState keeps thin
    //  delegating wrappers so call sites are untouched.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Renders the current page bitmap. Safe to call from a background thread.
    /// Does NOT submit analysis (which requires UI-thread access to the worker).
    /// Returns false if the page could not be rendered.
    /// Uses prefetched bitmap if available for the current page (seamless auto-scroll transitions).
    /// </summary>
    public bool LoadPageBitmap()
    {
        var oldPage = CachedPage;
        var oldMinimap = MinimapPage;

        try
        {
            // Use prefetched page if available (e.g. from auto-scroll lookahead).
            if (Prefetched is { } pf && pf.PageIndex == CurrentPageBacking)
            {
                CachedPage = pf.Page;
                CachedDpi = pf.Dpi;
                MinimapPage = pf.Minimap;
                PageWidth = pf.PageWidth;
                PageHeight = pf.PageHeight;
                Prefetched = null; // consumed — don't dispose, we're using the bitmaps
                oldPage?.Dispose();
                oldMinimap?.Dispose();
                return true;
            }

            var (w, h) = Owner.Pdf.GetPageSize(CurrentPageBacking);
            int dpi = DocumentState.CalculateRenderDpi(Camera.Zoom, w, h, RenderDpi);
            var newPage = Owner.Pdf.RenderPage(CurrentPageBacking, dpi);
            var newMinimap = Owner.Pdf.RenderThumbnail(CurrentPageBacking);

            // Commit: swap fields and dispose old bitmaps only after full success
            CachedPage = newPage;
            CachedDpi = dpi;
            MinimapPage = newMinimap;
            PageWidth = w;
            PageHeight = h;
            oldPage?.Dispose();
            oldMinimap?.Dispose();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Owner.Logger.Error($"Failed to render page {CurrentPageBacking + 1}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Schedules background rendering of the specified page for seamless auto-scroll
    /// page transitions. The prefetched bitmap is consumed by the next LoadPageBitmap()
    /// call if it targets the same page. No-op if a prefetch is already pending or
    /// the page is out of range.
    /// </summary>
    internal void PrefetchPage(int pageIndex)
    {
        // Serialize with DPI re-render to avoid concurrent PDFium access.
        if (PrefetchPending || DpiRenderPending) return;
        if (pageIndex < 0 || pageIndex >= Owner.PageCount || Owner.IsDisposed) return;
        if (Prefetched?.PageIndex == pageIndex) return;

        PrefetchPending = true;
        // Capture UI-thread state; the page's own dimensions (needed for the
        // pixel-area ceiling) are fetched inside the task to keep PDFium off the
        // UI thread, so DPI is computed there too.
        double zoom = Camera.Zoom;
        var dpiSettings = RenderDpi;
        var ct = Cts.Token;

        Task.Run(() =>
        {
            PrefetchedPageData? prepared = null;
            Exception? error = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                var (w, h) = Owner.Pdf.GetPageSize(pageIndex);
                int dpi = DocumentState.CalculateRenderDpi(zoom, w, h, dpiSettings);
                Owner.Logger.Debug($"[PDFium] prefetch pg {pageIndex} @ {dpi}dpi tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(Owner.FilePath)}");
                var page = Owner.Pdf.RenderPage(pageIndex, dpi);
                var minimap = Owner.Pdf.RenderThumbnail(pageIndex);
                prepared = new(pageIndex, dpi, page, minimap, w, h);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex; }

            Owner.Marshaller.Post(() =>
            {
                try
                {
                    if (error is not null)
                        Owner.Logger.Error($"Failed to prefetch page {pageIndex + 1}: {error.Message}", error);
                    // Bail if the document OR just this view (RemoveViewport) was disposed while the
                    // render was in flight — otherwise we'd resurrect Prefetched on a freed view whose
                    // Dispose already ran, leaking the bitmaps it will never get to dispose.
                    if (Owner.IsDisposed || _disposed || prepared is null)
                    {
                        prepared?.Dispose();
                        return;
                    }
                    Prefetched?.Dispose();
                    Prefetched = prepared;
                }
                finally { PrefetchPending = false; }
            });
        }, ct);
    }

    /// <summary>
    /// Checks if the current zoom demands a different DPI and schedules an
    /// async re-render on a background thread. A render-quality change sets a
    /// pending "dirty" flag (see <see cref="OnRenderQualityChanged"/>) that is
    /// treated as a forced re-render: it bypasses the hysteresis band so the new
    /// DPI takes effect on any change, not just large ones. It still respects the
    /// scroll-skip guard below — a forced re-render is deferred (stays dirty)
    /// while scrolling and retried from the animation tick the moment scrolling stops.
    /// </summary>
    public bool UpdateRenderDpiIfNeeded()
    {
        // Serialize with prefetch to avoid concurrent PDFium access. If a
        // render-quality change is pending it stays dirty and retries once the
        // gate frees (from the animation tick or the in-flight render's completion).
        if (DpiRenderPending || PrefetchPending) return false;

        bool force = RenderDpiDirty;

        // Skip DPI re-renders while the user is actively scrolling. PDFium runs
        // under a process-wide gate; a 100-200ms re-render at high zoom blocks
        // any subsequent text/link extraction the scroll path may need and the
        // bitmap-swap defers a frame. This applies to forced (preset-change)
        // re-renders too: jumping the gate mid-scroll would stutter the scroll,
        // so the change stays dirty and the animation tick retries it the moment
        // scroll velocity drops to zero.
        if (Rail.ScrollSpeed > 0.1 || Rail.AutoScrolling) return false;

        int neededDpi = DocumentState.CalculateRenderDpi(Camera.Zoom, PageWidthBacking, PageHeightBacking, RenderDpi);
        bool trigger = force
            ? neededDpi != CachedDpi
            : neededDpi > CachedDpi * RenderDpi.UpscaleHysteresis
              || (neededDpi < CachedDpi * RenderDpi.DownscaleHysteresis && CachedDpi > RenderDpi.MinDpi);

        // Optimistically mark a forced (preset-change) pass as satisfied now that
        // it's about to render (or needs no render). If the scheduled re-render
        // later FAILS, the completion handler re-arms the flag so the tick retries
        // — without this, a thrown RenderPage would silently strand the page at
        // the old DPI (the flag was already cleared).
        if (force) RenderDpiDirty = false;

        if (trigger)
        {
            DpiRenderPending = true;
            int page = CurrentPageBacking;
            var ct = Cts.Token;
            Task.Run(() =>
            {
                IRenderedPage? newPage = null;
                Exception? error = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    Owner.Logger.Debug($"[PDFium] dpi-rerender pg {page} @ {neededDpi}dpi tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(Owner.FilePath)}");
                    newPage = Owner.Pdf.RenderPage(page, neededDpi);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }

                Owner.Marshaller.Post(() =>
                {
                    try
                    {
                        if (error is not null)
                            Owner.Logger.Error($"Failed to re-render page at {neededDpi} DPI: {error.Message}", error);
                        // Bail if the document OR just this view (RemoveViewport) was disposed while the
                        // render was in flight — otherwise we'd swap a fresh bitmap onto a freed view
                        // whose Dispose already ran, leaking it.
                        if (Owner.IsDisposed || _disposed || CurrentPageBacking != page || newPage is null)
                        {
                            newPage?.Dispose();
                            // Re-arm a forced re-render that FAILED (RenderPage threw →
                            // error set) so the pending preset change is retried, not
                            // lost. A page-navigation abort or cancellation leaves error
                            // null, and GoToPage's LoadPageBitmap already rendered the new
                            // page at the new DPI, so neither needs a re-arm. A disposed
                            // view is gone — never re-arm it.
                            if (force && !Owner.IsDisposed && !_disposed && error is not null)
                                RenderDpiDirty = true;
                            return;
                        }
                        var oldPage = CachedPage;
                        CachedPage = newPage;
                        CachedDpi = neededDpi;
                        DpiRenderReady = true;
                        oldPage?.Dispose();
                        OnDpiRenderComplete?.Invoke();
                    }
                    finally { DpiRenderPending = false; }
                });
            }, ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applies a new render-quality preset at runtime and invalidates the page
    /// cache so already-rasterised pages re-render at the new DPI — no restart.
    /// Called from the controller's config-changed path for every open document.
    /// No-op unless the resolved DPI tuning actually changed, so unrelated config
    /// changes (dark mode, scroll speed, …) don't needlessly drop the prefetch
    /// buffer or schedule a render. When it does change, the prefetched page
    /// (rasterised at the old DPI) is dropped and the current page is forced to
    /// re-render — deferred via the dirty flag while PDFium is busy or scrolling.
    /// </summary>
    internal void OnRenderQualityChanged(in RenderDpiSettings settings)
    {
        Owner.Marshaller.AssertUIThread();

        // Gate on the actual DPI tuning, not "some setting changed" — OnConfigChanged
        // funnels every settings change here. RenderDpiSettings is a record struct
        // with value equality, so this is a cheap, correct comparison.
        if (settings == RenderDpi) return;

        RenderDpi = settings;

        // Drop the prefetch buffer — it was rasterised at the previous DPI.
        Prefetched?.Dispose();
        Prefetched = null;

        // Force the current page to re-render at the new DPI band; if PDFium is
        // busy or the user is scrolling, mark dirty so the tick retries.
        RenderDpiDirty = true;
        UpdateRenderDpiIfNeeded();
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

    /// <summary>
    /// Releases this view's resources: cancels its in-flight render/prefetch/DPI tasks (so a late
    /// one can't touch a freed bitmap — §6 disposal ordering), frees the cached page/minimap/prefetch
    /// bitmaps, and drops its callbacks. Called by <see cref="DocumentState.RemoveViewport"/> for a
    /// detached view and by <see cref="DocumentState.Dispose"/> for every view of a closing document.
    /// Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Cts.Cancel();
        Cts.Dispose();

        CachedPage?.Dispose();
        CachedPage = null;
        MinimapPage?.Dispose();
        MinimapPage = null;
        Prefetched?.Dispose();
        Prefetched = null;

        StateChanged = null;
        PageChanged = null;
        ReadingPositionChanged = null;
        OnDpiRenderComplete = null;
        RequestAnimation = null;
        AutoScroll.StateChanged = null;
    }

    private bool _disposed;

    /// <summary>True once this view has been removed/disposed. Document-level background tasks that
    /// captured this view check it before seating results or writing per-view state.</summary>
    internal bool IsDisposed => _disposed;
}
