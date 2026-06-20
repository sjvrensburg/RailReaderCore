using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Per-document state: PDF, camera, rail nav, analysis cache, annotations.
/// UI-free — no Avalonia dependency.
/// </summary>
public sealed class DocumentState : IDisposable
{
    private readonly IPdfService _pdf;
    private readonly IPdfTextService _pdfText;
    private readonly IPdfLinkService _pdfLink;
    private readonly IThreadMarshaller _marshaller;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    internal bool IsDisposed { get; private set; }

    private string _title;
    private bool _tableRowReading = true;
    private bool _cellNavigation;
    /// <summary>Fires when a property changes. Parameter is the property name. Doc-level changes
    /// (e.g. Title) fire directly; per-view page changes are forwarded from the primary
    /// <see cref="Viewport.StateChanged"/> (wired in the constructor) so single-viewport
    /// subscribers see the same notifications as before.</summary>
    public Action<string>? StateChanged;

    /// <summary>Sets a backing field and fires StateChanged if the value changed.</summary>
    private bool SetField<T>(ref T field, T value, string propertyName)
    {
        _marshaller.AssertUIThread();
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        StateChanged?.Invoke(propertyName);
        return true;
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value, nameof(Title));
    }

    // Page position + dimensions are per-view (on Viewport); these delegate to the primary view.
    public int CurrentPage
    {
        get => Primary.CurrentPage;
        set => Primary.CurrentPage = value;
    }

    public double PageWidth
    {
        get => Primary.PageWidth;
        set => Primary.PageWidth = value;
    }

    public double PageHeight
    {
        get => Primary.PageHeight;
        set => Primary.PageHeight = value;
    }

    public bool DebugOverlay
    {
        get => Primary.DebugOverlayBacking;
        set => SetField(ref Primary.DebugOverlayBacking, value, nameof(DebugOverlay));
    }

    public bool PendingRailSetup
    {
        get => Primary.PendingRailSetupBacking;
        set => SetField(ref Primary.PendingRailSetupBacking, value, nameof(PendingRailSetup));
    }

    public ColourEffect ColourEffect
    {
        get => Primary.ColourEffectBacking;
        set => SetField(ref Primary.ColourEffectBacking, value, nameof(ColourEffect));
    }

    public bool LineFocusBlur
    {
        get => Primary.LineFocusBlurBacking;
        set => SetField(ref Primary.LineFocusBlurBacking, value, nameof(LineFocusBlur));
    }

    public bool LineHighlightEnabled
    {
        get => Primary.LineHighlightEnabledBacking;
        set => SetField(ref Primary.LineHighlightEnabledBacking, value, nameof(LineHighlightEnabled));
    }

    public bool MarginCropping
    {
        get => Primary.MarginCroppingBacking;
        set => SetField(ref Primary.MarginCroppingBacking, value, nameof(MarginCropping));
    }

    public string FilePath { get; }
    public int PageCount { get; }
    public IPdfService Pdf => _pdf;
    public IPdfTextService PdfText => _pdfText;
    // Doc-level dependencies the per-view render path (moved onto Viewport) reaches via Owner.
    internal IThreadMarshaller Marshaller => _marshaller;
    internal ILogger Logger => _logger;

    /// <summary>
    /// The single embedded view (Phase 0 of the multi-viewport split — see
    /// <c>docs/multi-viewport-design.md</c>). Holds the camera and the rasterised-page
    /// cache; <see cref="DocumentState"/>'s camera/render-surface members delegate here.
    /// </summary>
    public Viewport Primary { get; }

    private readonly List<Viewport> _viewports;
    /// <summary>All views of this document — always ≥1, with <c>Viewports[0] == <see cref="Primary"/></c>.
    /// A document starts with just <see cref="Primary"/>; <see cref="AddViewport"/> appends detached
    /// views (split-pane / tear-off windows), each with its own camera and rasterised-page cache.</summary>
    public IReadOnlyList<Viewport> Viewports => _viewports;

    public Camera Camera => Primary.Camera;
    public RailNav Rail => Primary.Rail;
    private readonly Dictionary<int, PageAnalysis> _analysisCache = [];
    private readonly Dictionary<int, PageText> _textCache = [];
    private readonly Dictionary<int, List<PdfLink>> _linkCache = [];
    public IReadOnlyDictionary<int, PageAnalysis> AnalysisCache => _analysisCache;
    public IReadOnlyDictionary<int, PageText> TextCache => _textCache;
    public IReadOnlyDictionary<int, List<PdfLink>> LinkCache => _linkCache;
    // Pages farther than this from any view's current page are dropped from the
    // text/link caches; <= 0 disables eviction. See CoreSettings.PageCacheRadius.
    private int _pageCacheRadius;
    // Latest settings snapshot — kept so AddViewport can build a new view from current config.
    private CoreSettings _config;

    public Queue<int> PendingAnalysis => Primary.PendingAnalysis;
    internal BackgroundAnalysisQueue BackgroundQueue { get; private set; } = null!;

    /// <summary>Number of pages with cached analysis results.</summary>
    public int AnalysedPageCount => _analysisCache.Count;

    /// <summary>Whether this document has unanalysed pages remaining.</summary>
    public bool HasPendingBackgroundWork => !BackgroundQueue.IsExhausted;

    /// <summary>
    /// True when a user-initiated PDFium render (DPI re-render or page prefetch)
    /// is in flight. Background work should defer while this is true to keep
    /// the PDFium gate free for the in-flight task and any follow-up scroll-path
    /// calls (text/link extraction) the user is about to need.
    /// </summary>
    public bool IsPdfiumBusy => Primary.DpiRenderPending || Primary.PrefetchPending;

    /// <summary>Fires on the UI thread when a new page analysis result is cached.</summary>
    public event Action? AnalysisCacheUpdated;

    /// <summary>
    /// When set, this page was reached via rail navigation and should be
    /// skipped if analysis reveals no navigable blocks. Cleared on landing.
    /// </summary>
    public PendingPageSkip? PendingSkip { get => Primary.PendingSkip; set => Primary.PendingSkip = value; }
    public List<OutlineEntry> Outline { get; }

    // Navigation history (back/forward) — per-view so each viewport navigates independently
    public int BackStackCount => Primary.BackStack.Count;
    public int ForwardStackCount => Primary.ForwardStack.Count;
    public int PeekBack() => Primary.BackStack.Peek();
    public int PeekForward() => Primary.ForwardStack.Peek();

    // Annotations (shared via AnnotationFileManager when set)
    public AnnotationFile Annotations { get; set; } = new();
    public Stack<IUndoAction> UndoStack { get; } = new();
    public Stack<IUndoAction> RedoStack { get; } = new();
    private AnnotationFileManager? _annotationManager;

    // Cached rendered page and the DPI it was rendered at (delegated to the view).
    public IRenderedPage? CachedPage { get => Primary.CachedPage; private set => Primary.CachedPage = value; }
    public int CachedDpi { get => Primary.CachedDpi; private set => Primary.CachedDpi = value; }

    /// <summary>
    /// Document-wide content fraction: the smallest page-fractional rectangle that
    /// contains the union of analysed blocks across all pages seen so far.
    /// Only grows outward as more analyses land. Null until any analysis arrives.
    /// </summary>
    public ContentFraction? DocumentContentFraction { get; private set; }

    // Small pre-scaled thumbnail used by the minimap (≤200×280 px).
    public IRenderedPage? MinimapPage { get => Primary.MinimapPage; private set => Primary.MinimapPage = value; }

    public DocumentState(string filePath, IPdfService pdf, IPdfTextService pdfText, IPdfLinkService pdfLink,
        CoreSettings config, IThreadMarshaller marshaller, ILogger? logger = null)
    {
        _config = config;
        Primary = new Viewport(config, this);
        _viewports = [Primary];
        // Forward the primary view's per-view property changes to the doc-level StateChanged facade
        // so existing single-viewport subscribers keep seeing CurrentPage/PageWidth/PageHeight events.
        Primary.StateChanged += name => StateChanged?.Invoke(name);
        _marshaller = marshaller;
        _logger = logger ?? NullLogger.Instance;
        FilePath = filePath;
        _pdf = pdf;
        _pdfText = pdfText;
        _pdfLink = pdfLink;
        PageCount = _pdf.PageCount;
        _title = Path.GetFileName(filePath);
        Primary.ColourEffectBacking = config.ColourEffect;
        Primary.LineFocusBlurBacking = config.LineFocusBlur;
        Primary.LineHighlightEnabledBacking = config.LineHighlightEnabled;
        Primary.MarginCroppingBacking = config.MarginCropping;
        _tableRowReading = config.TableRowReading;
        _cellNavigation = config.CellNavigation;
        Outline = _pdf.Outline;
        _pageCacheRadius = config.PageCacheRadius;
        Primary.RenderDpi = config.RenderDpi;
        BackgroundQueue = new BackgroundAnalysisQueue(PageCount, config.BackgroundAnalysisWindowPages);
    }

    /// <summary>
    /// Applies background-analysis / cache tuning from an updated settings
    /// snapshot. Mirrors how <see cref="RailNav.UpdateConfig"/> propagates rail
    /// settings; called from the controller's config-changed path.
    /// </summary>
    internal void UpdateBackgroundSettings(CoreSettings config)
    {
        _marshaller.AssertUIThread();
        _config = config;
        BackgroundQueue.WindowPages = config.BackgroundAnalysisWindowPages;
        _pageCacheRadius = config.PageCacheRadius;
        _tableRowReading = config.TableRowReading;
        _cellNavigation = config.CellNavigation;
        EvictDistantPageCaches();
    }

    /// <summary>
    /// Drops text/link cache entries for pages no view needs, called when a viewport's page changes
    /// or cache tuning updates. A page is kept if it is within ±<see cref="_pageCacheRadius"/> of ANY
    /// view's current page (union over all viewports), so one view advancing can't drop a page another
    /// view still sits on (§5). The analysis-geometry cache is left intact (cheap to hold, expensive to
    /// recompute). No-op when eviction is disabled (<see cref="_pageCacheRadius"/> &lt;= 0).
    /// </summary>
    internal void EvictDistantPageCaches()
    {
        if (_pageCacheRadius <= 0) return;
        EvictUnneeded(_textCache);
        EvictUnneeded(_linkCache);
    }

    private void EvictUnneeded<TValue>(Dictionary<int, TValue> cache)
    {
        List<int>? stale = null;
        foreach (var page in cache.Keys)
            if (!AnyViewportNeeds(page))
                (stale ??= []).Add(page);
        if (stale is null) return;
        foreach (var page in stale)
            cache.Remove(page);
    }

    private bool AnyViewportNeeds(int page)
    {
        foreach (var vp in _viewports)
            if (Math.Abs(page - vp.CurrentPage) <= _pageCacheRadius)
                return true;
        return false;
    }

    /// <summary>
    /// Adds a detached view of this document (split-pane / tear-off window): a fresh
    /// <see cref="Viewport"/> seeded from the current settings and the primary view's size, starting
    /// on page 0 with a default camera. The caller positions it (page / centre / zoom). Its render
    /// cache and camera are independent of the primary's. UI-thread only.
    /// <para><b>Scope (Phase 1):</b> a non-primary view's camera, zoom, rail-snap, auto-scroll and
    /// rasterisation are fully independent, but a <em>cross-page</em> transition reached from its
    /// tick (rail page-advance / edge-hold) still routes through the document-level
    /// <see cref="GoToPage"/>, which moves the <see cref="Primary"/> view. Per-view page navigation
    /// and analysis fan-out land with the analysis-fan-out phase (see <c>docs/multi-viewport-design.md</c> §5).</para>
    /// </summary>
    public Viewport AddViewport()
    {
        _marshaller.AssertUIThread();
        var vp = new Viewport(_config, this)
        {
            ColourEffectBacking = _config.ColourEffect,
            LineFocusBlurBacking = _config.LineFocusBlur,
            LineHighlightEnabledBacking = _config.LineHighlightEnabled,
            MarginCroppingBacking = _config.MarginCropping,
            RenderDpi = _config.RenderDpi,
        };
        vp.SetSize(Primary.Width, Primary.Height);
        _viewports.Add(vp);
        return vp;
    }

    /// <summary>
    /// Removes and disposes a detached view (cancelling its in-flight renders and freeing its
    /// bitmaps). The <see cref="Primary"/> view cannot be removed — it lives for the document's
    /// lifetime. No-op if the view isn't ours. UI-thread only.
    /// </summary>
    public void RemoveViewport(Viewport vp)
    {
        _marshaller.AssertUIThread();
        if (ReferenceEquals(vp, Primary))
            throw new InvalidOperationException("Cannot remove the primary viewport.");
        if (_viewports.Remove(vp))
            vp.Dispose();
    }

    /// <summary>
    /// Renders the current page bitmap. Safe to call from a background thread.
    /// Does NOT submit analysis (which requires UI-thread access to the worker).
    /// Returns false if the page could not be rendered.
    /// Uses prefetched bitmap if available for the current page (seamless auto-scroll transitions).
    /// </summary>
    public bool LoadPageBitmap() => Primary.LoadPageBitmap();

    /// <summary>
    /// Schedules background rendering of the specified page for seamless auto-scroll
    /// page transitions. The prefetched bitmap is consumed by the next LoadPageBitmap()
    /// call if it targets the same page. No-op if a prefetch is already pending or
    /// the page is out of range.
    /// </summary>
    internal void PrefetchPage(int pageIndex) => Primary.PrefetchPage(pageIndex);

    /// <summary>
    /// Set to true when a DPI re-render completes. The next animation frame
    /// picks this up and invalidates the page layer atomically with the
    /// camera update, avoiding mid-frame bitmap swaps. (Delegated to the view.)
    /// </summary>
    public bool DpiRenderReady { get => Primary.DpiRenderReady; internal set => Primary.DpiRenderReady = value; }

    /// <summary>
    /// Called on the UI thread when a DPI re-render completes, so the view can
    /// request an animation frame to pick up the new bitmap. (Delegated to the view.)
    /// </summary>
    public Action? OnDpiRenderComplete { get => Primary.OnDpiRenderComplete; set => Primary.OnDpiRenderComplete = value; }

    /// <summary>
    /// Checks if the current zoom demands a different DPI and schedules an
    /// async re-render on a background thread. A render-quality change sets a
    /// pending "dirty" flag (see <see cref="OnRenderQualityChanged"/>) that is
    /// treated as a forced re-render: it bypasses the hysteresis band so the new
    /// DPI takes effect on any change, not just large ones. It still respects the
    /// scroll-skip guard below — a forced re-render is deferred (stays dirty)
    /// while scrolling and retried from the animation tick (which polls
    /// <see cref="RenderDpiPending"/>) the moment scrolling stops.
    /// </summary>
    public bool UpdateRenderDpiIfNeeded() => Primary.UpdateRenderDpiIfNeeded();

    /// <summary>True while a render-quality change is queued but not yet applied
    /// (PDFium was busy, or the user is scrolling). Polled every frame by the
    /// animation tick so the deferred re-render fires as soon as the gate and
    /// scroll allow, even while another animation is still running.</summary>
    internal bool RenderDpiPending => Primary.RenderDpiDirty;

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
    internal void OnRenderQualityChanged(in RenderDpiSettings settings) => Primary.OnRenderQualityChanged(in settings);

    public void SubmitAnalysis(AnalysisWorker? worker, IReadOnlySet<BlockRole> navigableRoles)
    {
        if (_analysisCache.TryGetValue(CurrentPage, out var cached))
        {
            _logger.Debug($"[SubmitAnalysis] Page {CurrentPage}: cache hit, {cached.Blocks.Count} blocks");
            ApplyAnalysis(cached, navigableRoles);
            return;
        }

        if (worker is null) return;

        if (worker.IsInFlight(FilePath, CurrentPage))
        {
            _logger.Debug($"[SubmitAnalysis] Page {CurrentPage}: already in flight");
            PendingRailSetup = true;
            return;
        }

        int page = CurrentPage;
        double pageW = PageWidth, pageH = PageHeight;
        string filePath = FilePath;
        PendingRailSetup = true;

        _logger.Debug($"[SubmitAnalysis] Page {page}: scheduling pixmap on background thread...");
        var ct = _cts.Token;
        Task.Run(() =>
        {
            _logger.Debug($"[PDFium] analysis-pixmap pg {page} tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(FilePath)}");
            try
            {
                ct.ThrowIfCancellationRequested();
                var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, worker.InputSize);
                var pageText = _pdfText.ExtractPageText(_pdf.PdfBytes, page, _pdf.Password);
                _logger.Debug($"[SubmitAnalysis] Page {page}: pixmap ready {pxW}x{pxH}, {pageText.CharBoxes.Count} chars, submitting...");
                _marshaller.Post(() =>
                {
                    if (IsDisposed || CurrentPage != page) return;
                    _textCache[page] = pageText;
                    worker.Submit(new AnalysisRequest(filePath, page, rgb, pxW, pxH, pageW, pageH, pageText.CharBoxes, _tableRowReading, _cellNavigation));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"Failed to prepare analysis input: {ex.Message}", ex);
                _marshaller.Post(() => { if (!IsDisposed) PendingRailSetup = false; });
            }
        }, ct);
    }

    public void ReapplyNavigableRoles(IReadOnlySet<BlockRole> navigableRoles)
    {
        if (_analysisCache.TryGetValue(CurrentPage, out var cached))
            Rail.SetAnalysis(cached, navigableRoles);
    }

    private void ApplyAnalysis(PageAnalysis analysis, IReadOnlySet<BlockRole> navigableRoles)
    {
        Rail.SetAnalysis(analysis, navigableRoles);
        PendingRailSetup = false;
    }

    public void QueueLookahead(int count)
    {
        PendingAnalysis.Clear();
        if (_analysisCache.Count < PageCount)
            BackgroundQueue.Reset(CurrentPage);
        for (int i = 1; i <= count; i++)
        {
            int page = CurrentPage + i;
            if (page < PageCount && !_analysisCache.ContainsKey(page))
                PendingAnalysis.Enqueue(page);
        }
    }

    public bool SubmitPendingLookahead(AnalysisWorker? worker)
    {
        if (worker is null || !worker.IsIdle) return false;
        if (PendingRailSetup) return false;

        while (PendingAnalysis.Count > 0)
        {
            int page = PendingAnalysis.Dequeue();
            if (_analysisCache.ContainsKey(page) || worker.IsInFlight(FilePath, page)) continue;

            string filePath = FilePath;
            double pageW = PageWidth, pageH = PageHeight;
            var ct = _cts.Token;
            Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, worker.InputSize);
                    var pageText = _pdfText.ExtractPageText(_pdf.PdfBytes, page, _pdf.Password);
                    _marshaller.Post(() =>
                    {
                        if (!IsDisposed)
                        {
                            _textCache[page] = pageText;
                            worker.Submit(new AnalysisRequest(filePath, page, rgb, pxW, pxH, pageW, pageH, pageText.CharBoxes, _tableRowReading, _cellNavigation));
                        }
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Error($"Lookahead prepare failed for page {page + 1}: {ex.Message}", ex);
                }
            }, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Submits the next background analysis page (outside the lookahead window).
    /// Returns true if a page was submitted, false if exhausted or worker busy.
    /// Renders the pixmap synchronously on the UI thread to avoid concurrent
    /// PDFium access — the pixmap is only 800x800 so this takes ~5ms.
    /// </summary>
    public bool SubmitBackgroundAnalysis(AnalysisWorker worker)
    {
        _marshaller.AssertUIThread();
        if (!worker.IsIdle) return false;
        if (PendingRailSetup) return false;
        if (IsPdfiumBusy) return false;
        if (BackgroundQueue.IsExhausted) return false;

        int? nextPage = BackgroundQueue.TryGetNext(
            _analysisCache, page => worker.IsInFlight(FilePath, page));
        if (nextPage is not { } page) return false;

        try
        {
            var (pageW, pageH) = _pdf.GetPageSize(page);
            var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, worker.InputSize);
            var pageText = GetOrExtractText(page);
            worker.Submit(new AnalysisRequest(FilePath, page, rgb, pxW, pxH, pageW, pageH, pageText.CharBoxes, _tableRowReading, _cellNavigation));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Background analysis prepare failed for page {page + 1}: {ex.Message}", ex);
            return false;
        }
    }

    public bool GoToPage(int page, AnalysisWorker? worker, IReadOnlySet<BlockRole> navigableRoles, double windowWidth, double windowHeight)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == CurrentPage) return true;

        int oldPage = CurrentPage;
        double oldZoom = Camera.Zoom;

        // Clear stale state from the previous page — the background task
        // for the old page will check CurrentPage != page and discard its
        // result, so these flags must not linger.
        ClearPendingState();

        CurrentPage = page;
        if (!LoadPageBitmap())
        {
            CurrentPage = oldPage;
            return false;
        }
        SubmitAnalysis(worker, navigableRoles);
        Camera.Zoom = oldZoom;
        ClampCamera(windowWidth, windowHeight);
        return true;
    }

    /// <summary>
    /// Clears transient state tied to the current page. Call before
    /// navigating away so that stale flags don't leak across pages.
    /// </summary>
    internal void ClearPendingState()
    {
        PendingRailSetup = false;
        PendingSkip = null;
        Primary.Prefetched?.Dispose();
        Primary.Prefetched = null;
    }

    // --- Camera geometry: delegated to the view (capstone slice 2). The bodies live on
    //     Viewport (reading its own Camera/Rail/page dims + Owner.DocumentContentFraction);
    //     these wrappers keep DocumentState's public surface and call sites untouched. ---

    /// <summary>
    /// Returns the page-space rectangle used by fit/centre operations.
    /// With margin cropping off (or no analysis yet), this is the full page.
    /// With margin cropping on, it's the content region of the page.
    /// </summary>
    public (double X, double Y, double W, double H) GetFitRect() => Primary.GetFitRect();

    /// <summary>
    /// Zoom that fits <paramref name="box"/> within the viewport with a uniform margin,
    /// clamped to the camera range. The limiting dimension wins so the whole block shows.
    /// </summary>
    public double ComputeBlockFitZoom(BBox box, double viewportW, double viewportH,
        double marginFraction = 0.08) => Primary.ComputeBlockFitZoom(box, viewportW, viewportH, marginFraction);

    /// <summary>
    /// Camera target (zoom + offsets) that centres <paramref name="box"/> in the viewport,
    /// fitting it via <see cref="ComputeBlockFitZoom"/> unless <paramref name="targetZoom"/> is
    /// given. Unlike rail framing this does NOT floor at the rail threshold — a large figure can
    /// be shown whole below 3×. Used by geometric centred framing for non-navigable blocks
    /// (figures/tables/charts) that the rail index can't seat.
    /// </summary>
    public (double Zoom, double OffsetX, double OffsetY) ComputeCenteredFrame(
        BBox box, double viewportW, double viewportH, double? targetZoom = null)
        => Primary.ComputeCenteredFrame(box, viewportW, viewportH, targetZoom);

    public void CenterPage(double windowWidth, double windowHeight) => Primary.CenterPage(windowWidth, windowHeight);

    public void FitWidth(double windowWidth, double windowHeight) => Primary.FitWidth(windowWidth, windowHeight);

    /// <summary>
    /// Applies the fit-width zoom while keeping the page-space y currently at
    /// the viewport top edge in place. Used when toggling margin cropping at
    /// fit-width zoom so the reading position stays anchored to the top.
    /// Horizontally, content is centred (same as <see cref="FitWidth"/>).
    /// </summary>
    public void FitWidthPreservingTop(double windowWidth, double windowHeight)
        => Primary.FitWidthPreservingTop(windowWidth, windowHeight);

    public void ClampCamera(double windowWidth, double windowHeight) => Primary.ClampCamera(windowWidth, windowHeight);

    public void ApplyZoom(double newZoom, double windowWidth, double windowHeight)
        => Primary.ApplyZoom(newZoom, windowWidth, windowHeight);

    public void UpdateRailZoom(double windowWidth, double windowHeight,
        double? cursorPageX = null, double? cursorPageY = null)
        => Primary.UpdateRailZoom(windowWidth, windowHeight, cursorPageX, cursorPageY);

    public void StartSnap(double windowWidth, double windowHeight) => Primary.StartSnap(windowWidth, windowHeight);

    public void StartSnapPreservingPosition(double windowWidth, double windowHeight,
        double horizontalFraction, double lineScreenY)
        => Primary.StartSnapPreservingPosition(windowWidth, windowHeight, horizontalFraction, lineScreenY);

    public void StartSnapToEnd(double windowWidth, double windowHeight) => Primary.StartSnapToEnd(windowWidth, windowHeight);

    public void LoadAnnotations(AnnotationFileManager manager)
    {
        _annotationManager = manager;
        Annotations = manager.Checkout(FilePath, _pdf.Password);
    }

    /// <summary>
    /// Generation counter incremented on every annotation mutation.
    /// Used by the UI to detect changes without deep comparison.
    /// </summary>
    public int AnnotationGeneration { get; private set; }

    public void MarkAnnotationsDirty()
    {
        AnnotationGeneration++;
        _annotationManager?.MarkDirty(FilePath);
    }

    // --- Bookmarks ---

    public void AddBookmark(string name, int page)
    {
        Annotations.Bookmarks.Add(new BookmarkEntry { Name = name, Page = page });
        MarkAnnotationsDirty();
    }

    public void RemoveBookmark(int index)
    {
        if (index < 0 || index >= Annotations.Bookmarks.Count) return;
        Annotations.Bookmarks.RemoveAt(index);
        MarkAnnotationsDirty();
    }

    public void RenameBookmark(int index, string newName)
    {
        if (index < 0 || index >= Annotations.Bookmarks.Count) return;
        Annotations.Bookmarks[index].Name = newName;
        MarkAnnotationsDirty();
    }

    public void AddAnnotation(int page, Annotation annotation)
    {
        if (!Annotations.Pages.TryGetValue(page, out var list))
        {
            list = [];
            Annotations.Pages[page] = list;
        }
        list.Add(annotation);

        var action = new AddAnnotationAction(page, annotation);
        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void UpdateAnnotationText(int page, TextNoteAnnotation note, string newText)
    {
        note.Text = newText;
        MarkAnnotationsDirty();
    }

    public void PushUndoAction(IUndoAction action)
    {
        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void RemoveAnnotation(int page, Annotation annotation)
    {
        var action = new RemoveAnnotationAction(page, annotation);
        action.Redo(Annotations);

        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void Undo()
    {
        if (UndoStack.Count == 0) return;
        var action = UndoStack.Pop();
        action.Undo(Annotations);
        RedoStack.Push(action);
        MarkAnnotationsDirty();
    }

    public void Redo()
    {
        if (RedoStack.Count == 0) return;
        var action = RedoStack.Pop();
        action.Redo(Annotations);
        UndoStack.Push(action);
        MarkAnnotationsDirty();
    }

    /// <summary>
    /// Returns cached text for a page, extracting it on first access.
    /// Must be called on the UI thread (PDFium is not thread-safe).
    /// </summary>
    public PageText GetOrExtractText(int pageIndex)
    {
        _marshaller.AssertUIThread();
        if (_textCache.TryGetValue(pageIndex, out var cached))
            return cached;
        var text = _pdfText.ExtractPageText(_pdf.PdfBytes, pageIndex, _pdf.Password);
        _textCache[pageIndex] = text;
        return text;
    }

    /// <summary>
    /// Returns cached links for a page, extracting them on first access.
    /// Must be called on the UI thread (PDFium is not thread-safe).
    /// </summary>
    public List<PdfLink> GetOrExtractLinks(int pageIndex)
    {
        _marshaller.AssertUIThread();
        if (_linkCache.TryGetValue(pageIndex, out var cached))
            return cached;
        var links = _pdfLink.ExtractPageLinks(_pdf.PdfBytes, pageIndex, _pdf.Password);
        _linkCache[pageIndex] = links;
        return links;
    }

    /// <summary>
    /// Hit-tests a point against PDF links on the current page.
    /// Uses the cached link list for fast in-memory lookup.
    /// </summary>
    public PdfLink? HitTestLink(double pageX, double pageY)
    {
        var links = GetOrExtractLinks(CurrentPage);
        foreach (var link in links)
        {
            if (link.Rect.Contains((float)pageX, (float)pageY))
                return link;
        }
        return null;
    }

    // --- Cache mutation methods ---

    internal void SetAnalysis(int page, PageAnalysis analysis)
    {
        _marshaller.AssertUIThread();
        _analysisCache[page] = analysis;

        var frac = PageCropUtil.ComputeFraction(analysis);
        var next = DocumentContentFraction is { } existing ? existing.Union(frac) : frac;
        if (!next.Equals(DocumentContentFraction))
            DocumentContentFraction = next;

        AnalysisCacheUpdated?.Invoke();
    }

    internal void SetText(int page, PageText text)
    {
        _marshaller.AssertUIThread();
        _textCache[page] = text;
    }

    internal void SetLinks(int page, List<PdfLink> links)
    {
        _marshaller.AssertUIThread();
        _linkCache[page] = links;
    }

    // --- Navigation history mutation ---

    internal void PushHistory(int currentPage)
    {
        _marshaller.AssertUIThread();
        Primary.BackStack.Push(currentPage);
        Primary.ForwardStack.Clear();
    }

    internal int PopBack(int currentPage)
    {
        _marshaller.AssertUIThread();
        Primary.ForwardStack.Push(currentPage);
        return Primary.BackStack.Pop();
    }

    internal int PopForward(int currentPage)
    {
        _marshaller.AssertUIThread();
        Primary.BackStack.Push(currentPage);
        return Primary.ForwardStack.Pop();
    }

    /// <summary>
    /// Base render DPI at zoom 1.0 (before the tier-step quantisation and
    /// clamping). A PDF point is 1/72", so 150 is ~2.08× oversampling — headroom
    /// that keeps the rasterised page sharp through the compositor's display-scale
    /// upscale on HiDPI screens without the renderer needing to know the scale.
    /// </summary>
    private const double BaseDpiPerZoom = 150.0;

    /// <summary>
    /// Calculates the appropriate render DPI for a zoom level, quantised to the
    /// preset's tier step, clamped to the preset's [MinDpi, MaxDpi] band, and
    /// finally lowered if a full-page bitmap at that DPI would exceed the preset's
    /// pixel-area ceiling. Pure math — no rendering-library dependency.
    /// </summary>
    /// <param name="zoom">Camera zoom factor.</param>
    /// <param name="pageWidthPts">Page width in PDF points (1/72"). <c>&lt;= 0</c> skips the area ceiling.</param>
    /// <param name="pageHeightPts">Page height in PDF points. <c>&lt;= 0</c> skips the area ceiling.</param>
    /// <param name="settings">Resolved render-quality tuning (see <see cref="RenderDpiSettings"/>).</param>
    public static int CalculateRenderDpi(double zoom, double pageWidthPts, double pageHeightPts, in RenderDpiSettings settings)
    {
        // Defensively normalise the bounds: ForPreset always yields valid values,
        // but this is a public entry point and `in` settings could be a default
        // (all-zero) or hand-built struct. Guarantee step >= 1, a positive floor
        // (never return 0 DPI to RenderPage), and MaxDpi >= MinDpi (Math.Clamp
        // throws on an inverted range). Valid presets are unaffected.
        int step = Math.Max(1, settings.TierStep);
        int minDpi = Math.Max(1, settings.MinDpi);
        int maxDpi = Math.Max(minDpi, settings.MaxDpi);
        int raw = (int)(zoom * BaseDpiPerZoom);
        int rounded = ((raw + step / 2) / step) * step;
        int dpi = Math.Clamp(rounded, minDpi, maxDpi);

        // Pixel-area ceiling: a full page rendered at `dpi` is
        // (w/72 · dpi) × (h/72 · dpi) px. If that exceeds the megapixel budget,
        // drop to the largest DPI that fits — but never below the readability
        // floor, even for a pathologically large page.
        if (settings.MaxMegapixels > 0 && pageWidthPts > 0 && pageHeightPts > 0)
        {
            double pageAreaSqInches = (pageWidthPts / 72.0) * (pageHeightPts / 72.0);
            double maxDpiByArea = Math.Sqrt(settings.MaxMegapixels * 1_000_000.0 / pageAreaSqInches);
            if (maxDpiByArea < dpi)
                dpi = Math.Max(minDpi, (int)maxDpiByArea);
        }
        return dpi;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        // §6 disposal ordering: cancel the doc-level analysis tasks, then dispose every view. Each
        // Viewport.Dispose cancels its own in-flight render/prefetch/DPI tasks before freeing the
        // bitmaps they touch, and clears its callbacks (incl. the auto-scroll StateChanged hook wired
        // in DocumentController.AddDocument). Each task's Post callback re-checks IsDisposed and
        // disposes its own result, so a late one is harmless.
        _cts.Cancel();
        _annotationManager?.Release(FilePath);
        foreach (var vp in _viewports)
            vp.Dispose();
        _cts.Dispose();

        StateChanged = null;
        AnalysisCacheUpdated = null;
    }
}
