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
    private int _currentPage;
    private double _pageWidth;
    private double _pageHeight;
    private bool _debugOverlay;
    private bool _pendingRailSetup;
    private ColourEffect _colourEffect;
    private bool _lineFocusBlur;
    private bool _lineHighlightEnabled = true;
    private bool _marginCropping;
    /// <summary>Fires when a property changes. Parameter is the property name.</summary>
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

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetField(ref _currentPage, value, nameof(CurrentPage)))
                EvictDistantPageCaches(value);
        }
    }

    public double PageWidth
    {
        get => _pageWidth;
        set => SetField(ref _pageWidth, value, nameof(PageWidth));
    }

    public double PageHeight
    {
        get => _pageHeight;
        set => SetField(ref _pageHeight, value, nameof(PageHeight));
    }

    public bool DebugOverlay
    {
        get => _debugOverlay;
        set => SetField(ref _debugOverlay, value, nameof(DebugOverlay));
    }

    public bool PendingRailSetup
    {
        get => _pendingRailSetup;
        set => SetField(ref _pendingRailSetup, value, nameof(PendingRailSetup));
    }

    public ColourEffect ColourEffect
    {
        get => _colourEffect;
        set => SetField(ref _colourEffect, value, nameof(ColourEffect));
    }

    public bool LineFocusBlur
    {
        get => _lineFocusBlur;
        set => SetField(ref _lineFocusBlur, value, nameof(LineFocusBlur));
    }

    public bool LineHighlightEnabled
    {
        get => _lineHighlightEnabled;
        set => SetField(ref _lineHighlightEnabled, value, nameof(LineHighlightEnabled));
    }

    public bool MarginCropping
    {
        get => _marginCropping;
        set => SetField(ref _marginCropping, value, nameof(MarginCropping));
    }

    public string FilePath { get; }
    public int PageCount { get; }
    public IPdfService Pdf => _pdf;
    public IPdfTextService PdfText => _pdfText;
    public Camera Camera { get; } = new();
    public RailNav Rail { get; }
    private readonly Dictionary<int, PageAnalysis> _analysisCache = [];
    private readonly Dictionary<int, PageText> _textCache = [];
    private readonly Dictionary<int, List<PdfLink>> _linkCache = [];
    public IReadOnlyDictionary<int, PageAnalysis> AnalysisCache => _analysisCache;
    public IReadOnlyDictionary<int, PageText> TextCache => _textCache;
    public IReadOnlyDictionary<int, List<PdfLink>> LinkCache => _linkCache;
    // Pages farther than this from CurrentPage are dropped from the text/link
    // caches; <= 0 disables eviction. See CoreSettings.PageCacheRadius.
    private int _pageCacheRadius;

    // Active render-quality tuning (DPI cap / tier step / floor / pixel-area
    // ceiling / hysteresis). Updated at runtime via OnRenderQualityChanged.
    private RenderDpiSettings _renderDpi = RenderDpiSettings.Default;

    // Set when a render-quality change arrives while PDFium is busy, so the
    // forced re-render is retried from the animation tick once it frees.
    private bool _renderDpiDirty;
    public Queue<int> PendingAnalysis { get; } = new();
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
    public bool IsPdfiumBusy => _dpiRenderPending || _prefetchPending;

    /// <summary>Fires on the UI thread when a new page analysis result is cached.</summary>
    public event Action? AnalysisCacheUpdated;

    /// <summary>
    /// When set, this page was reached via rail navigation and should be
    /// skipped if analysis reveals no navigable blocks. Cleared on landing.
    /// </summary>
    public PendingPageSkip? PendingSkip { get; set; }
    public List<OutlineEntry> Outline { get; }

    // Navigation history (back/forward) — per-document so tab switching doesn't cross-pollinate
    private readonly Stack<int> _backStack = new();
    private readonly Stack<int> _forwardStack = new();
    public int BackStackCount => _backStack.Count;
    public int ForwardStackCount => _forwardStack.Count;
    public int PeekBack() => _backStack.Peek();
    public int PeekForward() => _forwardStack.Peek();

    // Annotations (shared via AnnotationFileManager when set)
    public AnnotationFile Annotations { get; set; } = new();
    public Stack<IUndoAction> UndoStack { get; } = new();
    public Stack<IUndoAction> RedoStack { get; } = new();
    private AnnotationFileManager? _annotationManager;

    // Cached rendered page and the DPI it was rendered at
    public IRenderedPage? CachedPage { get; private set; }
    public int CachedDpi { get; private set; }

    /// <summary>
    /// Document-wide content fraction: the smallest page-fractional rectangle that
    /// contains the union of analysed blocks across all pages seen so far.
    /// Only grows outward as more analyses land. Null until any analysis arrives.
    /// </summary>
    public ContentFraction? DocumentContentFraction { get; private set; }

    // Small pre-scaled thumbnail used by the minimap (≤200×280 px).
    public IRenderedPage? MinimapPage { get; private set; }

    public DocumentState(string filePath, IPdfService pdf, IPdfTextService pdfText, IPdfLinkService pdfLink,
        CoreSettings config, IThreadMarshaller marshaller, ILogger? logger = null)
    {
        _marshaller = marshaller;
        _logger = logger ?? NullLogger.Instance;
        FilePath = filePath;
        _pdf = pdf;
        _pdfText = pdfText;
        _pdfLink = pdfLink;
        PageCount = _pdf.PageCount;
        _title = Path.GetFileName(filePath);
        _colourEffect = config.ColourEffect;
        _lineFocusBlur = config.LineFocusBlur;
        _lineHighlightEnabled = config.LineHighlightEnabled;
        _marginCropping = config.MarginCropping;
        Rail = new RailNav(config);
        Outline = _pdf.Outline;
        _pageCacheRadius = config.PageCacheRadius;
        _renderDpi = config.RenderDpi;
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
        BackgroundQueue.WindowPages = config.BackgroundAnalysisWindowPages;
        _pageCacheRadius = config.PageCacheRadius;
        EvictDistantPageCaches(CurrentPage);
    }

    /// <summary>
    /// Drops text/link cache entries for pages outside ±<see cref="_pageCacheRadius"/>
    /// of <paramref name="center"/>. The analysis-geometry cache is left intact
    /// (cheap to hold, expensive to recompute).
    /// </summary>
    private void EvictDistantPageCaches(int center)
    {
        if (_pageCacheRadius <= 0) return;
        int lo = center - _pageCacheRadius, hi = center + _pageCacheRadius;
        EvictOutside(_textCache, lo, hi);
        EvictOutside(_linkCache, lo, hi);
    }

    private static void EvictOutside<TValue>(Dictionary<int, TValue> cache, int lo, int hi)
    {
        List<int>? stale = null;
        foreach (var page in cache.Keys)
            if (page < lo || page > hi)
                (stale ??= []).Add(page);
        if (stale is null) return;
        foreach (var page in stale)
            cache.Remove(page);
    }

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
            if (_prefetched is { } pf && pf.PageIndex == CurrentPage)
            {
                CachedPage = pf.Page;
                CachedDpi = pf.Dpi;
                MinimapPage = pf.Minimap;
                PageWidth = pf.PageWidth;
                PageHeight = pf.PageHeight;
                _prefetched = null; // consumed — don't dispose, we're using the bitmaps
                oldPage?.Dispose();
                oldMinimap?.Dispose();
                return true;
            }

            var (w, h) = _pdf.GetPageSize(CurrentPage);
            int dpi = CalculateRenderDpi(Camera.Zoom, w, h, _renderDpi);
            var newPage = _pdf.RenderPage(CurrentPage, dpi);
            var newMinimap = _pdf.RenderThumbnail(CurrentPage);

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
            _logger.Error($"Failed to render page {CurrentPage + 1}: {ex.Message}", ex);
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
        if (_prefetchPending || _dpiRenderPending) return;
        if (pageIndex < 0 || pageIndex >= PageCount || IsDisposed) return;
        if (_prefetched?.PageIndex == pageIndex) return;

        _prefetchPending = true;
        // Capture UI-thread state; the page's own dimensions (needed for the
        // pixel-area ceiling) are fetched inside the task to keep PDFium off the
        // UI thread, so DPI is computed there too.
        double zoom = Camera.Zoom;
        var dpiSettings = _renderDpi;
        var ct = _cts.Token;

        Task.Run(() =>
        {
            PrefetchedPageData? prepared = null;
            Exception? error = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                var (w, h) = _pdf.GetPageSize(pageIndex);
                int dpi = CalculateRenderDpi(zoom, w, h, dpiSettings);
                _logger.Debug($"[PDFium] prefetch pg {pageIndex} @ {dpi}dpi tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(FilePath)}");
                var page = _pdf.RenderPage(pageIndex, dpi);
                var minimap = _pdf.RenderThumbnail(pageIndex);
                prepared = new(pageIndex, dpi, page, minimap, w, h);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex; }

            _marshaller.Post(() =>
            {
                try
                {
                    if (error is not null)
                        _logger.Error($"Failed to prefetch page {pageIndex + 1}: {error.Message}", error);
                    if (IsDisposed || prepared is null)
                    {
                        prepared?.Dispose();
                        return;
                    }
                    _prefetched?.Dispose();
                    _prefetched = prepared;
                }
                finally { _prefetchPending = false; }
            });
        }, ct);
    }

    private bool _dpiRenderPending;

    // Page prefetch for seamless auto-scroll page transitions.
    private sealed record PrefetchedPageData(
        int PageIndex, int Dpi, IRenderedPage Page, IRenderedPage Minimap,
        double PageWidth, double PageHeight) : IDisposable
    {
        public void Dispose() { Page.Dispose(); Minimap.Dispose(); }
    }

    private PrefetchedPageData? _prefetched;
    private bool _prefetchPending;

    /// <summary>
    /// Set to true when a DPI re-render completes. The next animation frame
    /// picks this up and invalidates the page layer atomically with the
    /// camera update, avoiding mid-frame bitmap swaps.
    /// </summary>
    public bool DpiRenderReady { get; internal set; }

    /// <summary>
    /// Called on the UI thread when a DPI re-render completes, so the view can
    /// request an animation frame to pick up the new bitmap.
    /// </summary>
    public Action? OnDpiRenderComplete { get; set; }

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
    public bool UpdateRenderDpiIfNeeded()
    {
        // Serialize with prefetch to avoid concurrent PDFium access. If a
        // render-quality change is pending it stays dirty and retries once the
        // gate frees (from the animation tick or the in-flight render's completion).
        if (_dpiRenderPending || _prefetchPending) return false;

        bool force = _renderDpiDirty;

        // Skip DPI re-renders while the user is actively scrolling. PDFium runs
        // under a process-wide gate; a 100-200ms re-render at high zoom blocks
        // any subsequent text/link extraction the scroll path may need and the
        // bitmap-swap defers a frame. This applies to forced (preset-change)
        // re-renders too: jumping the gate mid-scroll would stutter the scroll,
        // so the change stays dirty and the animation tick retries it the moment
        // scroll velocity drops to zero.
        if (Rail.ScrollSpeed > 0.1 || Rail.AutoScrolling) return false;

        int neededDpi = CalculateRenderDpi(Camera.Zoom, PageWidth, PageHeight, _renderDpi);
        bool trigger = force
            ? neededDpi != CachedDpi
            : neededDpi > CachedDpi * _renderDpi.UpscaleHysteresis
              || (neededDpi < CachedDpi * _renderDpi.DownscaleHysteresis && CachedDpi > _renderDpi.MinDpi);

        // Optimistically mark a forced (preset-change) pass as satisfied now that
        // it's about to render (or needs no render). If the scheduled re-render
        // later FAILS, the completion handler re-arms the flag so the tick retries
        // — without this, a thrown RenderPage would silently strand the page at
        // the old DPI (the flag was already cleared).
        if (force) _renderDpiDirty = false;

        if (trigger)
        {
            _dpiRenderPending = true;
            int page = CurrentPage;
            bool wasForced = force;
            var ct = _cts.Token;
            Task.Run(() =>
            {
                IRenderedPage? newPage = null;
                Exception? error = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.Debug($"[PDFium] dpi-rerender pg {page} @ {neededDpi}dpi tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(FilePath)}");
                    newPage = _pdf.RenderPage(page, neededDpi);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex; }

                _marshaller.Post(() =>
                {
                    try
                    {
                        if (error is not null)
                            _logger.Error($"Failed to re-render page at {neededDpi} DPI: {error.Message}", error);
                        if (IsDisposed || CurrentPage != page || newPage is null)
                        {
                            newPage?.Dispose();
                            // Re-arm a forced re-render that FAILED (RenderPage threw)
                            // so the pending preset change is retried, not lost. A
                            // page-navigation abort needs no re-arm — GoToPage's
                            // LoadPageBitmap already rendered the new page at the new DPI.
                            if (wasForced && !IsDisposed && newPage is null && error is not null)
                                _renderDpiDirty = true;
                            return;
                        }
                        var oldPage = CachedPage;
                        CachedPage = newPage;
                        CachedDpi = neededDpi;
                        DpiRenderReady = true;
                        oldPage?.Dispose();
                        OnDpiRenderComplete?.Invoke();
                    }
                    finally { _dpiRenderPending = false; }
                });
            }, ct);
            return true;
        }

        return false;
    }

    /// <summary>True while a render-quality change is queued but not yet applied
    /// (PDFium was busy, or the user is scrolling). Polled every frame by the
    /// animation tick so the deferred re-render fires as soon as the gate and
    /// scroll allow, even while another animation is still running.</summary>
    internal bool RenderDpiPending => _renderDpiDirty;

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
        _marshaller.AssertUIThread();

        // Gate on the actual DPI tuning, not "some setting changed" — OnConfigChanged
        // funnels every settings change here. RenderDpiSettings is a record struct
        // with value equality, so this is a cheap, correct comparison.
        if (settings == _renderDpi) return;

        _renderDpi = settings;

        // Drop the prefetch buffer — it was rasterised at the previous DPI.
        _prefetched?.Dispose();
        _prefetched = null;

        // Force the current page to re-render at the new DPI band; if PDFium is
        // busy or the user is scrolling, mark dirty so the tick retries.
        _renderDpiDirty = true;
        UpdateRenderDpiIfNeeded();
    }

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
                var pageText = _pdfText.ExtractPageText(_pdf.PdfBytes, page);
                _logger.Debug($"[SubmitAnalysis] Page {page}: pixmap ready {pxW}x{pxH}, {pageText.CharBoxes.Count} chars, submitting...");
                _marshaller.Post(() =>
                {
                    if (IsDisposed || CurrentPage != page) return;
                    _textCache[page] = pageText;
                    worker.Submit(new AnalysisRequest(filePath, page, rgb, pxW, pxH, pageW, pageH, pageText.CharBoxes));
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
                    var pageText = _pdfText.ExtractPageText(_pdf.PdfBytes, page);
                    _marshaller.Post(() =>
                    {
                        if (!IsDisposed)
                        {
                            _textCache[page] = pageText;
                            worker.Submit(new AnalysisRequest(filePath, page, rgb, pxW, pxH, pageW, pageH, pageText.CharBoxes));
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
            worker.Submit(new AnalysisRequest(FilePath, page, rgb, pxW, pxH, pageW, pageH, pageText.CharBoxes));
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
        _prefetched?.Dispose();
        _prefetched = null;
    }

    /// <summary>
    /// Returns the page-space rectangle used by fit/centre operations.
    /// With margin cropping off (or no analysis yet), this is the full page.
    /// With margin cropping on, it's the content region of the page.
    /// </summary>
    public (double X, double Y, double W, double H) GetFitRect()
    {
        if (MarginCropping && DocumentContentFraction is { } f)
        {
            return (f.X * PageWidth, f.Y * PageHeight,
                    f.W * PageWidth, f.H * PageHeight);
        }
        return (0, 0, PageWidth, PageHeight);
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
        if (PageWidth <= 0 || PageHeight <= 0 || windowWidth <= 0 || windowHeight <= 0) return;
        var (rx, ry, rw, rh) = GetFitRect();
        if (rw <= 0 || rh <= 0) return;
        Camera.Zoom = Math.Min(windowWidth / rw, windowHeight / rh);
        Camera.OffsetX = CenteredOffsetX(windowWidth, rx, rw, Camera.Zoom);
        Camera.OffsetY = (windowHeight - rh * Camera.Zoom) / 2.0 - ry * Camera.Zoom;
    }

    public void FitWidth(double windowWidth, double windowHeight)
    {
        if (PageWidth <= 0 || windowWidth <= 0) return;
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
        if (PageWidth <= 0 || windowWidth <= 0 || Camera.Zoom <= 0) return;
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
        if (MarginCropping && Rail.ZoomThreshold > 0)
        {
            double uncroppedFit = windowWidth / PageWidth;
            if (uncroppedFit < Rail.ZoomThreshold)
                maxZoom = Math.Min(maxZoom, Rail.ZoomThreshold - RailThresholdEpsilon);
        }
        return Math.Clamp(windowWidth / rectW, Camera.ZoomMin, maxZoom);
    }

    public void ClampCamera(double windowWidth, double windowHeight)
    {
        double scaledW = PageWidth * Camera.Zoom;
        double scaledH = PageHeight * Camera.Zoom;

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

    public void LoadAnnotations(AnnotationFileManager manager)
    {
        _annotationManager = manager;
        Annotations = manager.Checkout(FilePath);
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
        var text = _pdfText.ExtractPageText(_pdf.PdfBytes, pageIndex);
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
        var links = _pdfLink.ExtractPageLinks(_pdf.PdfBytes, pageIndex);
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
        _backStack.Push(currentPage);
        _forwardStack.Clear();
    }

    internal int PopBack(int currentPage)
    {
        _marshaller.AssertUIThread();
        _forwardStack.Push(currentPage);
        return _backStack.Pop();
    }

    internal int PopForward(int currentPage)
    {
        _marshaller.AssertUIThread();
        _backStack.Push(currentPage);
        return _forwardStack.Pop();
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
        _cts.Cancel();
        _annotationManager?.Release(FilePath);
        var page = CachedPage;
        CachedPage = null;
        page?.Dispose();
        var mm = MinimapPage;
        MinimapPage = null;
        mm?.Dispose();
        _prefetched?.Dispose();
        _prefetched = null;
        _cts.Dispose();

        StateChanged = null;
        AnalysisCacheUpdated = null;
        OnDpiRenderComplete = null;
    }
}
