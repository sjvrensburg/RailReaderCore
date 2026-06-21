using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Result of a single animation tick. Tells the UI what to repaint.
/// </summary>
public record struct TickResult(
    bool CameraChanged,
    bool PageChanged,
    bool OverlayChanged,
    bool SearchChanged,
    bool AnnotationsChanged,
    bool StillAnimating);

/// <summary>
/// Headless controller that owns all document business logic.
/// No Avalonia dependency — can be driven by AI agent, tests, or UI.
/// </summary>
public sealed partial class DocumentController : IDisposable
{
    private CoreSettings _config;
    private readonly IRecentFilesStore _recentFiles;
    private readonly IAnnotationStore _annotationStore;
    private readonly IThreadMarshaller _marshaller;
    private readonly IPdfServiceFactory _pdfFactory;
    private readonly ILogger _logger;
    private readonly AnnotationFileManager _annotationManager;
    private AnalysisWorker? _worker;
    public bool HasWorker => _worker is not null;

    private double _vpWidth = 1200;
    private double _vpHeight = 900;

    public List<DocumentState> Documents { get; } = [];

    private Viewport? _focusedViewport;

    /// <summary>
    /// The view that receives input / search / annotation — the single source of truth for "where
    /// the user is". A host sets this on pane focus (split-pane / detached window); the active
    /// document derives from it, so the two cannot diverge. Null when no document is open; setting a
    /// view whose document isn't open is ignored.
    /// </summary>
    public Viewport? FocusedViewport
    {
        get => _focusedViewport;
        set
        {
            // Reject a view whose document isn't open, or one already removed from its document
            // (a disposed/detached view must never become the focus the tick dereferences).
            if (value is not null
                && (!Documents.Contains(value.Owner) || !value.Owner.Viewports.Contains(value)))
                return;
            _focusedViewport = value;
        }
    }

    /// <summary>Re-points focus to the document's primary view when the currently-focused view is
    /// removed (<see cref="DocumentState.RemoveViewport"/>), so <see cref="FocusedViewport"/> /
    /// <see cref="ActiveDocument"/> never reference a disposed view. The owning document is still open
    /// (only one of its non-primary views was removed), so its primary is a safe fallback.</summary>
    private void OnViewportRemoved(Viewport removed)
    {
        if (ReferenceEquals(_focusedViewport, removed))
            _focusedViewport = removed.Owner.Primary;
    }

    public CoreSettings Config => _config;
    public ColourEffect ActiveColourEffect => ActiveDocument?.ColourEffect ?? _config.ColourEffect;
    public float ActiveColourIntensity => (float)_config.ColourEffectIntensity;
    public AnalysisWorker? Worker => _worker;
    public AnnotationFileManager AnnotationManager => _annotationManager;

    /// <summary>The document the focused view belongs to — the input/search/annotation target.
    /// Derived from <see cref="FocusedViewport"/>, so setting focus moves it.</summary>
    public DocumentState? ActiveDocument => _focusedViewport?.Owner;

    /// <summary>Index of <see cref="ActiveDocument"/> in <see cref="Documents"/> (-1 if none). Setting
    /// it focuses that document's primary view — the tab-switch entry point.</summary>
    public int ActiveDocumentIndex
    {
        get => _focusedViewport?.Owner is { } d ? Documents.IndexOf(d) : -1;
        set => _focusedViewport = (uint)value < (uint)Documents.Count ? Documents[value].Primary : null;
    }

    // Annotation and search subsystems
    public AnnotationInteractionHandler Annotations { get; }
    public SearchService Search { get; }

    // Auto-scroll state — per-view, lives on the active document's Viewport.
    public bool AutoScrollActive => FocusedViewport?.AutoScroll.AutoScrollActive ?? false;
    public bool JumpMode
    {
        get => FocusedViewport?.AutoScroll.JumpMode ?? false;
        set { if (FocusedViewport is { } vp) vp.AutoScroll.JumpMode = value; }
    }

    // Rail pause (Ctrl+drag free pan) state — per-view, lives on Viewport.
    public bool RailPaused => FocusedViewport?.RailPause is not null;

    /// <summary>
    /// Fired when a property changes. UI can subscribe to update bindings.
    /// </summary>
    public Action<string>? StateChanged;

    /// <summary>
    /// Fired when a transient status message should be shown to the user.
    /// </summary>
    public Action<string>? StatusMessage;

    /// <summary>Fired when the active document's page changes. Parameter = new page index.</summary>
    public Action<int>? PageChanged;

    /// <summary>Fired when the reading position (block/line) changes in rail mode.</summary>
    public Action<ReadingPosition>? ReadingPositionChanged;

    /// <summary>Fired when analysis completes for a page. Parameter = page index.</summary>
    public Action<int>? AnalysisPageReady;

    public DocumentController(CoreSettings config, IRecentFilesStore recentFiles,
        IAnnotationStore annotationStore, IThreadMarshaller marshaller,
        IPdfServiceFactory pdfFactory, ILogger? logger = null)
    {
        _config = config;
        _recentFiles = recentFiles;
        _annotationStore = annotationStore;
        _marshaller = marshaller;
        _pdfFactory = pdfFactory;
        _logger = logger ?? NullLogger.Instance;
        _annotationManager = new AnnotationFileManager(annotationStore, marshaller);
        _annotationManager.OnSaveFailure = msg => StatusMessage?.Invoke(msg);
        Annotations = new AnnotationInteractionHandler();
        Search = new SearchService(
            () => ActiveDocument,
            () => GetViewportSize(),
            GoToPage);
    }

    /// <summary>
    /// Initialize the analysis worker with the platform's layout analyzer.
    /// Capabilities are passed eagerly so consumers can read InputSize without
    /// waiting for the model to load. An optional reading-order resolver may
    /// be supplied; otherwise a sensible default is chosen from the
    /// capabilities (model order if available, top-down otherwise).
    /// Must be called before opening documents.
    /// </summary>
    public void InitializeWorker(
        LayoutModelCapabilities capabilities,
        Func<ILayoutAnalyzer> analyzerFactory,
        IReadingOrderResolver? readingOrderResolver = null)
    {
        _worker = new AnalysisWorker(capabilities, analyzerFactory, _marshaller, readingOrderResolver, _logger);
        _logger.Debug("[Analysis] Worker started (analyzer loading in background)");
    }

    public void SetViewportSize(double w, double h)
    {
        if (w > 0) _vpWidth = w;
        if (h > 0) _vpHeight = h;
        // Mirror the ambient size into every view's Width/Height. Not yet consumed (GetViewportSize
        // still returns the ambient size below); this primes the per-view size the relocated Tick
        // will read in a later increment. Single-window today → all equal.
        foreach (var doc in Documents)
            doc.Primary.SetSize(_vpWidth, _vpHeight);
    }

    public (double Width, double Height) GetViewportSize() => (_vpWidth, _vpHeight);

    // --- Document management ---

    /// <summary>
    /// Creates a DocumentState for the given path (synchronous). Call LoadDocumentAsync for bitmap loading.
    /// For an encrypted PDF, pass the
    /// <paramref name="password"/>; this throws <see cref="Services.PdfPasswordRequiredException"/>
    /// when the document is encrypted and the password is missing or wrong, so the
    /// caller can prompt the user and retry.
    /// </summary>
    public DocumentState CreateDocument(string path, string? password = null)
        => new(path, _pdfFactory.CreatePdfService(path, password), _pdfFactory.CreatePdfTextService(),
            _pdfFactory.CreatePdfLinkService(), _config, _marshaller, _logger);

    /// <summary>
    /// Adds a document to the tab list, restores reading position, and submits analysis.
    /// Call after bitmap is loaded.
    /// </summary>
    public void AddDocument(DocumentState state)
    {
        var (ww, wh) = GetViewportSize();
        state.Primary.SetSize(ww, wh); // seed this view's size from the ambient size
        // This view's auto-scroll state changes surface through the controller's StateChanged
        // (UI re-reads AutoScrollActive/JumpMode, which delegate to the active view).
        state.Primary.AutoScroll.StateChanged = name => StateChanged?.Invoke(name);
        // If the focused view is removed, fall back to its document's primary so FocusedViewport is
        // never left pointing at a disposed view (it backs ActiveDocument and the per-frame tick).
        state.ViewportRemoved = OnViewportRemoved;

        var saved = _recentFiles.GetReadingPosition(state.FilePath);
        bool restoredPage = saved is not null && saved.Page > 0;
        if (restoredPage)
            state.GoToPage(Math.Clamp(saved!.Page, 0, state.PageCount - 1), _worker, _config.NavigableRoles, ww, wh);
        if (saved?.ColourEffect is { } savedEffect)
            state.ColourEffect = savedEffect;

        state.CenterPage(ww, wh);
        state.UpdateRailZoom(ww, wh);

        Documents.Add(state);
        _recentFiles.AddRecentFile(state.FilePath);
        ActiveDocumentIndex = Documents.Count - 1;

        // GoToPage already submitted analysis for the restored page;
        // only submit here for new documents starting at page 0.
        if (!restoredPage)
            state.SubmitAnalysis(_worker, _config.NavigableRoles);
        state.QueueLookahead(_config.AnalysisLookaheadPages);
    }

    public void CloseDocument(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        var doc = Documents[index];
        _recentFiles.SaveReadingPosition(doc.FilePath, doc.CurrentPage,
            doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, doc.ColourEffect);

        // Re-point focus only when the closed document held it; a surviving focused view (another
        // tab, or a detached pane) keeps focus, and ActiveDocumentIndex follows it automatically.
        bool closingFocused = ReferenceEquals(_focusedViewport?.Owner, doc);
        Documents.RemoveAt(index);
        doc.Dispose();
        if (closingFocused)
            _focusedViewport = Documents.Count > 0 ? Documents[Math.Min(index, Documents.Count - 1)].Primary : null;
        // Free-pan pause is per-view now: the closed doc's pause died with its disposal, and any
        // other tab is already cleared on switch-away (SelectDocument). The surviving active doc
        // keeps its own pause — so nothing to clear here. (The old global `_railPause = null` was a
        // single shared slot; reaching into the *new* active view to null it would clobber a
        // legitimate in-progress free-pan on the tab the user is keeping.)
        Search.CloseSearch();
    }

    public void SelectDocument(int index)
    {
        // Re-selecting the already-active tab is a no-op: it is not "leaving" the tab, so it must
        // not quiesce it. Without this guard, a redundant SelectDocument(ActiveDocumentIndex) (which
        // tab-strip click handlers commonly fire) would stop the active view's auto-scroll and drop
        // its free-pan pause via the leaving-tab teardown below.
        if (index < 0 || index >= Documents.Count || index == ActiveDocumentIndex) return;

        // Leaving this tab: drop its free-pan pause and end its auto-scroll session, so a tab
        // switch quiesces the tab you're leaving. With per-view auto-scroll flags there is no
        // shared flag to go stale, so the old post-switch sync is no longer needed.
        if (ActiveDocument is { } leaving && _focusedViewport is { } leavingView)
        {
            // Quiesce the FOCUSED view of the tab you're leaving — it may be a secondary/detached
            // pane, not the primary (auto-scroll/free-pan state is per-view).
            leavingView.RailPause = null;
            leavingView.AutoScroll.StopAutoScroll(leaving);
        }
        ActiveDocumentIndex = index;
    }

    public void MoveDocument(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex
            || fromIndex < 0 || fromIndex >= Documents.Count
            || toIndex < 0 || toIndex >= Documents.Count)
            return;

        var doc = Documents[fromIndex];
        Documents.RemoveAt(fromIndex);
        Documents.Insert(toIndex, doc);
        // Focus is a view reference, not an index, so reordering the tab list leaves it intact —
        // ActiveDocument / ActiveDocumentIndex follow the moved document automatically.
    }

    public void SaveAllReadingPositions()
    {
        foreach (var doc in Documents)
            _recentFiles.SaveReadingPosition(doc.FilePath, doc.CurrentPage,
                doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, doc.ColourEffect);
        _annotationManager.FlushAll();
    }

    public void Dispose()
    {
        SaveAllReadingPositions();
        foreach (var doc in Documents)
            doc.Dispose();
        Documents.Clear();
        _worker?.Dispose();
        _worker = null;
        _annotationManager.Dispose();
    }

    // --- Bookmarks ---

    /// <summary>
    /// Adds a bookmark for the current page. If a bookmark already exists
    /// for this page, its name is updated instead of creating a duplicate.
    /// Returns true if a new bookmark was added, false if an existing one was renamed.
    /// </summary>
    public bool AddBookmark(string name)
    {
        if (ActiveDocument is not { Annotations: { } annotations } doc) return false;

        var existing = annotations.Bookmarks.FindIndex(b => b.Page == doc.CurrentPage);
        if (existing >= 0)
        {
            doc.RenameBookmark(existing, name);
            return false;
        }

        doc.AddBookmark(name, doc.CurrentPage);
        return true;
    }

    public void RemoveBookmark(int index)
    {
        ActiveDocument?.RemoveBookmark(index);
    }

    public void RenameBookmark(int index, string newName)
    {
        ActiveDocument?.RenameBookmark(index, newName);
    }

    // --- Navigation history (back/forward) ---
    // Stacks live on DocumentState so each tab has independent history.

    public bool CanGoBack => ActiveDocument is { } d && d.BackStackCount > 0;
    public bool CanGoForward => ActiveDocument is { } d && d.ForwardStackCount > 0;

    /// <summary>Pushes the current page onto the back stack and clears forward history.</summary>
    private void PushHistory()
    {
        if (ActiveDocument is not { } doc) return;
        doc.PushHistory(doc.CurrentPage);
    }

    public void NavigateToBookmark(int index)
    {
        if (ActiveDocument is not { Annotations: { } annotations } doc) return;
        if ((uint)index >= (uint)annotations.Bookmarks.Count) return;
        PushHistory();
        GoToPage(annotations.Bookmarks[index].Page);
        FitPage();
    }

    public void NavigateBack()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.BackStackCount == 0) return;
        GoToPage(doc.PopBack(doc.CurrentPage));
    }

    public void NavigateForward()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.ForwardStackCount == 0) return;
        GoToPage(doc.PopForward(doc.CurrentPage));
    }

    /// <summary>
    /// Scrolls the camera so the destination position is visible.
    /// Coordinates are in PDF user space; converted using the target page dimensions.
    /// </summary>
    private void ScrollToDestination(PageDestination dest)
    {
        if (ActiveDocument is not { } doc) return;
        if (dest.PdfX is null && dest.PdfY is null) return;

        var (ww, wh) = GetViewportSize();

        if (dest.PdfY is { } pdfY)
        {
            double pageY = doc.PageHeight - pdfY;
            doc.Camera.OffsetY = -pageY * doc.Camera.Zoom + wh * CoreTuning.DestMarginTop;
        }

        if (dest.PdfX is { } pdfX)
        {
            doc.Camera.OffsetX = -pdfX * doc.Camera.Zoom + ww * CoreTuning.DestMarginLeft;
        }

        doc.ClampCamera(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    // --- Navigation ---

    public void GoToPage(int page)
    {
        FocusedViewport?.Zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        int prevPage = doc.CurrentPage;
        if (!doc.GoToPage(page, _worker, _config.NavigableRoles, ww, wh))
        {
            NotifyRenderFailed(page);
            return;
        }
        doc.QueueLookahead(_config.AnalysisLookaheadPages);
        Search.UpdateCurrentPageMatches();
        // DocumentState.GoToPage returns true without changing anything when the
        // target clamps to the current page; only announce a real transition.
        if (doc.CurrentPage != prevPage)
            RaisePageChanged(doc.Primary);
    }

    private void NotifyRenderFailed(int page)
        => StatusMessage?.Invoke($"Page {page + 1} could not be rendered (corrupted?)");

    public void FitPage()
    {
        FocusedViewport?.Zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.CenterPage(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    public void FitWidth()
    {
        FocusedViewport?.Zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.FitWidth(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    // --- Camera ---

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        if (ctrlHeld && doc.Rail.Active && !RailPaused)
        {
            double step = scrollDelta * 2.0 * doc.Camera.Zoom;
            doc.Camera.OffsetX += step;
            doc.ClampCamera(ww, wh);
        }
        else
        {
            double factor = 1.0 + scrollDelta * CoreTuning.ZoomScrollSensitivity;
            double baseZoom = doc.Primary.Zoom.PendingTargetZoom ?? doc.Camera.Zoom;
            double newZoom = Math.Clamp(baseZoom * factor, Camera.ZoomMin, Camera.ZoomMax);
            doc.Primary.Zoom.Start(doc.Primary, newZoom, cursorX, cursorY, _vpWidth);
        }
    }

    public void HandlePan(double dx, double dy, bool ctrlHeld = false)
    {
        FocusedViewport?.Zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        if (AutoScrollActive) StopAutoScroll();
        var (ww, wh) = GetViewportSize();

        if (ctrlHeld && doc.Rail.Active && !RailPaused)
            StartRailPause(doc);

        doc.Camera.OffsetX += dx;
        doc.Camera.OffsetY += dy;
        doc.ClampCamera(ww, wh);
        if (doc.Rail.Active && !RailPaused)
            doc.Rail.CaptureVerticalBias(doc.Camera.OffsetY, doc.Camera.Zoom, wh);
    }

    private void StartRailPause(DocumentState doc)
    {
        doc.Primary.RailPause = new(doc.Rail.CurrentBlock, doc.Rail.CurrentLine, doc.Rail.VerticalBias, doc.Camera.Zoom);
        StatusMessage?.Invoke("Free pan — release Ctrl to return");
    }

    /// <summary>
    /// End rail pause: restore block/line/bias/zoom from before the free pan and snap back.
    /// </summary>
    public void ResumeRailFromPause()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.Primary.RailPause is not { } pause) return;
        doc.Primary.RailPause = null;

        var (ww, wh) = GetViewportSize();

        // Restore zoom if it changed during free pan (may re-enter rail mode)
        if (Math.Abs(doc.Camera.Zoom - pause.Zoom) > 0.001)
        {
            doc.Camera.Zoom = pause.Zoom;
            doc.Camera.NotifyZoomChange();
            doc.UpdateRailZoom(ww, wh);
            doc.UpdateRenderDpiIfNeeded();
        }

        if (!doc.Rail.Active) return;

        // Clamp indices in case analysis changed while paused
        doc.Rail.CurrentBlock = Math.Clamp(pause.Block, 0, Math.Max(doc.Rail.NavigableCount - 1, 0));
        doc.Rail.CurrentLine = Math.Clamp(pause.Line, 0, Math.Max(doc.Rail.CurrentLineCount - 1, 0));
        doc.Rail.VerticalBias = pause.VerticalBias;

        doc.StartSnap(ww, wh);
        FireReadingPositionChanged();
        StatusMessage?.Invoke("");
    }

    public void HandleZoomKey(bool zoomIn)
    {
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        double baseZoom = doc.Primary.Zoom.PendingTargetZoom ?? doc.Camera.Zoom;
        double newZoom = Math.Clamp(
            zoomIn ? baseZoom * CoreTuning.ZoomStep : baseZoom / CoreTuning.ZoomStep,
            Camera.ZoomMin, Camera.ZoomMax);

        doc.Primary.Zoom.Start(doc.Primary, newZoom, ww / 2.0, wh / 2.0, _vpWidth);
        if (!doc.Rail.Active && AutoScrollActive) StopAutoScroll();
    }

    /// <summary>
    /// Smoothly animate zoom AND pan to an explicit camera target (native 180 ms cubic
    /// ease-out). Works in browse or rail mode.
    /// </summary>
    public void AnimateCameraTo(double targetZoom, double targetOffsetX, double targetOffsetY)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        double z = Math.Clamp(targetZoom, Camera.ZoomMin, Camera.ZoomMax);
        double cpx = (ww / 2.0 - targetOffsetX) / z; // target viewport-centre in page space
        double cpy = (wh / 2.0 - targetOffsetY) / z;
        doc.Primary.Zoom.StartTo(doc.Primary, z, targetOffsetX, targetOffsetY, cpx, cpy);
    }

    /// <summary>
    /// Smoothly frame the page-level block <paramref name="pageBlockIndex"/> on the
    /// current page (auto-fit when <paramref name="targetZoom"/> is null). Navigable blocks
    /// use rail's exact framing: the zoom is floored at the rail threshold so rail engages and
    /// the completion snap frames the block. <paramref name="line"/> (clamped to the block's
    /// line range; defaults to the first line) seats the rail on a specific line and is honoured
    /// through both the framing and the rail-activation reset, so callers can land on an arbitrary
    /// line in one smooth motion. Non-navigable blocks (figures/tables/charts) — which the rail
    /// index can't seat — fall back to a geometric centred frame instead of failing (and ignore
    /// <paramref name="line"/>), so callers can frame any detected block. Returns false only if
    /// there's no document / no current-page analysis / the index is out of range.
    /// </summary>
    public bool SmoothlyFrameBlock(int pageBlockIndex, double? targetZoom = null, double? durationMs = null, int line = 0)
    {
        if (ActiveDocument is not { } doc) return false;
        if (!doc.AnalysisCache.TryGetValue(doc.CurrentPage, out var analysis)) return false;
        if (pageBlockIndex < 0 || pageBlockIndex >= analysis.Blocks.Count) return false;

        // Sync RailNav to THIS page's analysis so the index space + chunk framing below
        // refer to the current page. Skip the rebuild when it already holds this exact
        // analysis (the common case) — ReapplyNavigableRoles itself always rebuilds.
        if (!ReferenceEquals(doc.Rail.Analysis, analysis))
            doc.ReapplyNavigableRoles(_config.NavigableRoles);

        var box = analysis.Blocks[pageBlockIndex].BBox;

        // Non-navigable role (figure/table/chart): centre it geometrically instead of failing.
        if (!doc.Rail.TrySetCurrentByPageIndex(pageBlockIndex, line))
            return CenterBlockGeometric(doc, box, targetZoom, durationMs);

        // Keep THIS block (and seated line) when the zoom crosses the rail threshold
        // mid-flight, regardless of overlapping block geometry under the focus point.
        doc.Rail.PinCurrentBlockForActivation();

        var (ww, wh) = GetViewportSize();
        // Floor at the rail threshold (not ZoomMin) so rail framing actually applies.
        double z = Math.Clamp(targetZoom ?? doc.ComputeBlockFitZoom(box, ww, wh),
            _config.RailZoomThreshold, Camera.ZoomMax);

        var (ox, oy) = doc.Rail.ComputeSnapTarget(z, ww, wh);
        var lineInfo = doc.Rail.CurrentLineInfo; // seated block's target line
        if (AutoScrollActive) StopAutoScroll();
        doc.Primary.Zoom.StartTo(doc.Primary, z, ox, oy, lineInfo.X + lineInfo.Width / 2.0, lineInfo.Y, durationMs);
        FireReadingPositionChanged();
        return true;
    }

    /// <summary>
    /// Frame the <paramref name="occurrence"/>-th (0-based, reading order) block of
    /// <paramref name="role"/> on the current page. Non-navigable roles are centred geometrically
    /// (see <see cref="SmoothlyFrameBlock"/>).
    /// </summary>
    public bool SmoothlyFrameRole(BlockRole role, int occurrence = 0, double? targetZoom = null)
    {
        if (ActiveDocument is not { } doc) return false;
        if (!doc.AnalysisCache.TryGetValue(doc.CurrentPage, out var analysis)) return false;
        int idx = FindRoleOccurrence(analysis, role, occurrence);
        return idx >= 0 && SmoothlyFrameBlock(idx, targetZoom);
    }

    /// <summary>Page-block index of the <paramref name="occurrence"/>-th block of
    /// <paramref name="role"/> in reading order on <paramref name="analysis"/>, or -1.</summary>
    private static int FindRoleOccurrence(PageAnalysis analysis, BlockRole role, int occurrence)
    {
        if (occurrence < 0) return -1;
        var matches = new List<int>();
        for (int i = 0; i < analysis.Blocks.Count; i++)
            if (analysis.Blocks[i].Role == role) matches.Add(i);
        if (occurrence >= matches.Count) return -1;
        matches.Sort((a, b) => analysis.Blocks[a].Order.CompareTo(analysis.Blocks[b].Order));
        return matches[occurrence];
    }

    /// <summary>Shared geometric centred-frame: ease the camera to fit + centre <paramref name="box"/>
    /// without engaging rail. Always returns true.</summary>
    private bool CenterBlockGeometric(DocumentState doc, BBox box, double? targetZoom, double? durationMs = null)
    {
        var (ww, wh) = GetViewportSize();
        var (z, ox, oy) = doc.ComputeCenteredFrame(box, ww, wh, targetZoom);
        if (AutoScrollActive) StopAutoScroll();
        doc.Rail.Deactivate(); // drive the camera directly; no rail seat/snap
        doc.Primary.Zoom.StartCameraOnly(doc.Primary, z, ox, oy, durationMs);
        FireReadingPositionChanged(); // rail now inactive → reading position cleared
        return true;
    }

    /// <summary>
    /// True while any camera animation (zoom, rail snap) or auto-scroll is running — the
    /// D-Bus control layer derives its "Settled" signal from the true→false transition
    /// of this across <see cref="Tick"/>.
    /// A <em>parked</em> semi-auto-scroll is excluded: a park is an indefinite idle wait
    /// for an explicit advance keypress (no camera motion — <see cref="Tick"/> returns
    /// early with <c>StillAnimating == false</c>), so it must let the render loop quiesce
    /// rather than pin it true forever (issue #62).
    /// </summary>
    public bool IsAnimating
    {
        get
        {
            if (ActiveDocument is not { } d)
                return false; // no active document → nothing (zoom/rail/auto-scroll is all per-view) can animate
            return d.Primary.Zoom.IsAnimating
                || d.Rail.SnapProgress < 1.0
                || (d.Primary.AutoScroll.AutoScrollActive && !d.Rail.AutoScrollParked);
        }
    }

    // --- Auto-scroll (delegated to AutoScrollController) ---

    public void ToggleAutoScroll() => FocusedViewport?.AutoScroll.ToggleAutoScroll(ActiveDocument);

    public void StopAutoScroll() => FocusedViewport?.AutoScroll.StopAutoScroll(ActiveDocument);

    public void ToggleAutoScrollExclusive() => FocusedViewport?.AutoScroll.ToggleAutoScrollExclusive(ActiveDocument);

    public void ToggleJumpModeExclusive() => FocusedViewport?.AutoScroll.ToggleJumpModeExclusive(ActiveDocument);

    /// <summary>
    /// True when semi-automatic auto-scroll is parked on a stop unit (non-prose block, new
    /// chunk, or new page) waiting for an explicit advance keypress. The consumer routes the
    /// forward/advance keys to <see cref="ResumeAutoScrollFromPark"/> while this is set and
    /// surfaces a "parked — press D to continue" affordance.
    /// </summary>
    public bool AutoScrollParked =>
        AutoScrollActive && (ActiveDocument?.Rail.AutoScrollParked ?? false);

    /// <summary>Resume flow from a semi-auto park (the reader pressed the advance key).</summary>
    public void ResumeAutoScrollFromPark() => ActiveDocument?.Rail.ResumeAutoScrollFromPark();

    // --- Colour effects ---

    public void SetColourEffect(ColourEffect effect)
    {
        if (ActiveDocument is { } doc)
            doc.ColourEffect = effect;
    }

    public ColourEffect CycleColourEffect()
    {
        var values = Enum.GetValues<ColourEffect>();
        var current = ActiveDocument?.ColourEffect ?? ActiveColourEffect;
        int idx = (Array.IndexOf(values, current) + 1) % values.Length;
        var next = values[idx];
        SetColourEffect(next);
        return next;
    }

    // --- Config ---

    /// <summary>
    /// Apply a new settings snapshot. The caller (UI shell) is responsible for
    /// persisting the underlying config file — Core has no filesystem awareness.
    /// </summary>
    public void OnConfigChanged(CoreSettings newConfig)
    {
        _config = newConfig;
        foreach (var doc in Documents)
        {
            // Per-view settings reach EVERY view (rail, auto-scroll, render-DPI); a detached pane
            // must respond to a live settings change too (§8).
            foreach (var vp in doc.Viewports)
            {
                vp.AutoScroll.UpdateConfig(newConfig);
                vp.Rail.UpdateConfig(_config);
                doc.ReapplyNavigableRoles(vp, _config.NavigableRoles);
                vp.OnRenderQualityChanged(_config.RenderDpi);
            }
            doc.UpdateBackgroundSettings(_config); // doc-level caches/queue — once per document
        }
    }

    /// <summary>
    /// Apply a new settings snapshot for incremental slider drag events.
    /// Like <see cref="OnConfigChanged"/> but skips reapplying analysis-derived
    /// state (navigable classes) because slider drags don't change them.
    /// </summary>
    public void OnSliderChanged(CoreSettings newConfig)
    {
        _config = newConfig;
        foreach (var doc in Documents)
            foreach (var vp in doc.Viewports)
            {
                vp.AutoScroll.UpdateConfig(newConfig);
                vp.Rail.UpdateConfig(_config);
                // Keep per-view render-DPI in sync with _config on this path too, so the two can't
                // diverge. OnRenderQualityChanged no-ops unless the resolved tuning actually changed,
                // so a normal slider drag is free.
                vp.OnRenderQualityChanged(_config.RenderDpi);
            }
    }


    // --- Query methods (for agent / headless use) ---

    private DocumentState? ResolveDocument(int? index)
    {
        if (!index.HasValue) return ActiveDocument;
        if (index.Value < 0 || index.Value >= Documents.Count) return null;
        return Documents[index.Value];
    }

    public DocumentList ListDocuments()
    {
        var summaries = Documents.Select((d, i) => new DocumentSummary(
            i, d.FilePath, d.Title, d.PageCount, d.CurrentPage)).ToList();
        return new DocumentList(ActiveDocumentIndex, summaries);
    }

    public DocumentInfo? GetDocumentInfo(int? index = null)
    {
        var doc = ResolveDocument(index);
        if (doc is null) return null;

        return new DocumentInfo(
            doc.FilePath, doc.Title, doc.PageCount, doc.CurrentPage,
            doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY,
            doc.Rail.Active, doc.Rail.HasAnalysis, doc.Rail.NavigableCount,
            AutoScrollActive, JumpMode);
    }

    public SearchResult GetSearchState() => Search.GetSearchState();

    /// <summary>
    /// Returns the current reading position (page, block, line, text, geometry)
    /// for the active or specified document. Returns null if no document or rail
    /// mode is not active.
    /// </summary>
    public ReadingPosition? GetReadingPosition(int? index = null)
    {
        var doc = ResolveDocument(index);
        return doc is null ? null : BuildReadingPosition(doc, withText: true);
    }

    /// <summary>
    /// Builds a <see cref="ReadingPosition"/> for the given document's current rail
    /// position, or null if rail mode is inactive / has no navigable block / the
    /// block has no lines. When <paramref name="withText"/> is false the text
    /// fields are left empty (the push path skips text extraction for performance);
    /// callers that need text use <see cref="GetReadingPosition"/>. This is the
    /// single source of truth shared by the pull query and the push event so the
    /// two cannot drift.
    /// </summary>
    private ReadingPosition? BuildReadingPosition(DocumentState doc, bool withText)
        => BuildReadingPosition(doc.Primary, withText);

    private ReadingPosition? BuildReadingPosition(Viewport vp, bool withText)
    {
        if (!vp.Rail.Active || !vp.Rail.HasAnalysis) return null;

        var block = vp.Rail.CurrentNavigableBlock;
        if (block.Lines.Count == 0) return null;

        // Report the line index actually described (CurrentLine clamped to the
        // block's lines), matching the line CurrentLineInfo extracts text from.
        int lineIndex = Math.Min(vp.Rail.CurrentLine, block.Lines.Count - 1);
        string blockText = "";
        string lineText = "";
        if (withText && vp.Owner.TextCache.TryGetValue(vp.CurrentPage, out var pageText))
        {
            blockText = pageText.ExtractBlockText(block);
            var line = vp.Rail.CurrentLineInfo;
            float top = line.Y - line.Height / 2f;
            lineText = pageText.ExtractTextInRect(
                line.X, top, line.X + line.Width, top + line.Height) ?? "";
        }
        // BlockIndex is the page-level block index (matches BlockSummary.Index from
        // GetPageDescription), NOT the navigable-subset index, so agents can
        // correlate the two even when the page has non-navigable blocks.
        var (vpW, _) = GetViewportSize();
        double hFraction = vp.Rail.ComputeHorizontalFraction(vp.Camera.OffsetX, vp.Camera.Zoom, vpW);
        return new ReadingPosition(
            vp.CurrentPage, vp.Rail.CurrentNavigableArrayIndex, lineIndex,
            block.Role, blockText, lineText, block.BBox,
            block.Lines.Count, hFraction);
    }

    /// <summary>
    /// Returns an accessible description of a page's layout: all blocks with
    /// semantic roles, text previews, and reading order. Targets the active
    /// document unless <paramref name="index"/> specifies a different tab.
    /// Uses the current page unless <paramref name="page"/> specifies a page.
    /// </summary>
    public PageDescription? GetPageDescription(int? index = null, int? page = null)
    {
        var doc = ResolveDocument(index);
        if (doc is null) return null;
        int targetPage = page ?? doc.CurrentPage;
        if (targetPage < 0 || targetPage >= doc.PageCount) return null;
        if (!doc.AnalysisCache.TryGetValue(targetPage, out var analysis)) return null;

        PageText? pageText = doc.TextCache.TryGetValue(targetPage, out var pt) ? pt : null;
        var blocks = new List<BlockSummary>(analysis.Blocks.Count);
        const int maxChars = 200;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var block = analysis.Blocks[i];
            string preview = "";
            if (pageText is not null)
            {
                preview = pageText.ExtractBlockText(block, maxChars, out bool more);
                if (more) preview += "…";
            }
            blocks.Add(new BlockSummary(i, block.Role, preview, block.BBox, block.Order));
        }
        return new PageDescription(targetPage, analysis.Blocks.Count, blocks);
    }

    /// <summary>
    /// Navigates to the next (or previous) navigable block with the given
    /// <paramref name="target"/> role on the current page. Returns true if found.
    /// </summary>
    public bool NavigateToRole(BlockRole target, bool forward = true)
    {
        if (ActiveDocument is not { } doc) return false;
        if (!doc.Rail.Active || !doc.Rail.HasAnalysis) return false;
        if (!_config.NavigableRoles.Contains(target)) return false;
        if (!doc.Rail.TryNavigateToRole(target, forward)) return false;

        var (ww, wh) = GetViewportSize();
        doc.StartSnap(ww, wh);
        FireReadingPositionChanged();
        return true;
    }

    /// <summary>
    /// Sets the pageChanged flag and announces the page change for <paramref name="vp"/> in one call,
    /// so a code path can't forget one or the other. Fires the view's own <see cref="Viewport.PageChanged"/>
    /// and — when <paramref name="vp"/> is the focused view — the controller-level <c>PageChanged</c> facade.
    /// </summary>
    private void FirePageChanged(ref bool pageChanged, Viewport vp)
    {
        pageChanged = true;
        RaisePageChanged(vp);
    }

    /// <summary>Announces a page change for <paramref name="vp"/> (no TickResult flag): the view's own
    /// <see cref="Viewport.PageChanged"/>, plus the controller-level facade when it is the focused view.</summary>
    private void RaisePageChanged(Viewport vp)
    {
        vp.PageChanged?.Invoke(vp.CurrentPage);
        if (vp == FocusedViewport)
            PageChanged?.Invoke(vp.CurrentPage);
    }

    private void FireReadingPositionChanged()
    {
        if (FocusedViewport is { } vp) FireReadingPositionChanged(vp);
    }

    /// <summary>Announces a rail reading-position change for <paramref name="vp"/>: the view's own
    /// <see cref="Viewport.ReadingPositionChanged"/>, plus the controller-level facade when it is the
    /// focused view. Builds the position lazily and only when someone is listening.</summary>
    private void FireReadingPositionChanged(Viewport vp)
    {
        bool focused = vp == FocusedViewport;
        if (vp.ReadingPositionChanged is null && (!focused || ReadingPositionChanged is null)) return;
        if (BuildReadingPosition(vp, withText: false) is not { } pos) return;
        vp.ReadingPositionChanged?.Invoke(pos);
        if (focused)
            ReadingPositionChanged?.Invoke(pos);
    }
}
