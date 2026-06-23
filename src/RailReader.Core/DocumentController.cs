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

    public List<DocumentModel> Documents { get; } = [];

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
            SetFocus(value);
        }
    }

    /// <summary>The single focus-assignment seam. Routes the focused view's auto-scroll/jump state
    /// changes to the controller's <see cref="StateChanged"/> (which the UI reads via the focus-
    /// delegating <see cref="AutoScrollActive"/>/<see cref="JumpMode"/>), unwiring the previously-
    /// focused view first. This is why a detached pane (created via <see cref="DocumentModel.AddViewport"/>,
    /// which can't reach the controller) still drives the UI once focused — the forwarder is wired
    /// here, not at view creation.</summary>
    private void SetFocus(Viewport? value)
    {
        if (ReferenceEquals(_focusedViewport, value)) return;
        if (_focusedViewport is { } prev) prev.AutoScroll.StateChanged = null;
        _focusedViewport = value;
        if (value is not null) value.AutoScroll.StateChanged = name => StateChanged?.Invoke(name);
    }

    /// <summary>Re-points focus to the document's primary view when the currently-focused view is
    /// removed (<see cref="DocumentModel.RemoveViewport"/>), so <see cref="FocusedViewport"/> /
    /// <see cref="ActiveDocument"/> never reference a disposed view. The owning document is still open
    /// (only one of its non-primary views was removed), so its primary is a safe fallback.</summary>
    private void OnViewportRemoved(Viewport removed)
    {
        if (ReferenceEquals(_focusedViewport, removed))
            SetFocus(removed.Owner.Primary);
    }

    public CoreSettings Config => _config;
    public ColourEffect ActiveColourEffect => ActiveDocument?.ColourEffect ?? _config.ColourEffect;
    public float ActiveColourIntensity => (float)_config.ColourEffectIntensity;
    public AnalysisWorker? Worker => _worker;
    public AnnotationFileManager AnnotationManager => _annotationManager;

    /// <summary>The document the focused view belongs to — the input/search/annotation target.
    /// Derived from <see cref="FocusedViewport"/>. <b>Internal (Phase 3):</b> the public
    /// single-viewport facade was removed — external hosts read <c>FocusedViewport?.Owner</c>.</summary>
    internal DocumentModel? ActiveDocument => _focusedViewport?.Owner;

    /// <summary>Index of <see cref="ActiveDocument"/> in <see cref="Documents"/> (-1 if none). Setting
    /// it focuses that document's primary view. <b>Internal (Phase 3):</b> external hosts set
    /// <see cref="FocusedViewport"/> directly.</summary>
    internal int ActiveDocumentIndex
    {
        get => _focusedViewport?.Owner is { } d ? Documents.IndexOf(d) : -1;
        set => SetFocus((uint)value < (uint)Documents.Count ? Documents[value].Primary : null);
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

    // Phase 3: the controller-level PageChanged / ReadingPositionChanged facades were removed.
    // A host subscribes per-viewport via Viewport.PageChanged / Viewport.ReadingPositionChanged
    // (the focused view, plus any detached panes it shows).

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
            () => FocusedViewport,
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

    // --- Document management ---

    /// <summary>
    /// Creates a DocumentModel for the given path (synchronous). Call LoadDocumentAsync for bitmap loading.
    /// For an encrypted PDF, pass the
    /// <paramref name="password"/>; this throws <see cref="Services.PdfPasswordRequiredException"/>
    /// when the document is encrypted and the password is missing or wrong, so the
    /// caller can prompt the user and retry.
    /// </summary>
    public DocumentModel CreateDocument(string path, string? password = null)
        => new(path, _pdfFactory.CreatePdfService(path, password), _pdfFactory.CreatePdfTextService(),
            _pdfFactory.CreatePdfLinkService(), _config, _marshaller, _logger);

    /// <summary>
    /// Adds a document to the tab list, restores reading position, and submits analysis.
    /// Call after bitmap is loaded.
    /// </summary>
    public void AddDocument(DocumentModel state)
    {
        // The host sizes the primary view (via state.Primary.SetSize) before adding the document;
        // the controller no longer keeps an ambient size (Phase 3). Initial centre / restore use
        // the primary's own size — it falls back to the Viewport default until the host resizes it,
        // at which point the host re-centres.
        var (ww, wh) = (state.Primary.Width, state.Primary.Height);
        // Auto-scroll/jump state changes of the FOCUSED view surface through the controller's
        // StateChanged — wired centrally in SetFocus when this document becomes focused below
        // (ActiveDocumentIndex setter), so detached panes get the same treatment once focused.
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
            SetFocus(Documents.Count > 0 ? Documents[Math.Min(index, Documents.Count - 1)].Primary : null);
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
    // Stacks live on DocumentModel so each tab has independent history.

    public bool CanGoBack => ActiveDocument is { } d && d.BackStackCount > 0;
    public bool CanGoForward => ActiveDocument is { } d && d.ForwardStackCount > 0;

    /// <summary>Pushes the current page onto the back stack and clears forward history.</summary>
    private void PushHistory()
    {
        if (FocusedViewport is not { } vp) return;
        vp.Owner.PushHistory(vp.CurrentPage);
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
        if (FocusedViewport is not { } vp) return;
        var doc = vp.Owner;
        if (doc.BackStackCount == 0) return;
        GoToPage(doc.PopBack(vp.CurrentPage));
    }

    public void NavigateForward()
    {
        if (FocusedViewport is not { } vp) return;
        var doc = vp.Owner;
        if (doc.ForwardStackCount == 0) return;
        GoToPage(doc.PopForward(vp.CurrentPage));
    }

    /// <summary>
    /// Scrolls the camera so the destination position is visible.
    /// Coordinates are in PDF user space; converted using the target page dimensions.
    /// </summary>
    private void ScrollToDestination(PageDestination dest)
    {
        if (FocusedViewport is not { } vp) return;
        if (dest.PdfX is null && dest.PdfY is null) return;

        var (ww, wh) = (vp.Width, vp.Height);

        if (dest.PdfY is { } pdfY)
        {
            double pageY = vp.PageHeight - pdfY;
            vp.Camera.OffsetY = -pageY * vp.Camera.Zoom + wh * CoreTuning.DestMarginTop;
        }

        if (dest.PdfX is { } pdfX)
        {
            vp.Camera.OffsetX = -pdfX * vp.Camera.Zoom + ww * CoreTuning.DestMarginLeft;
        }

        vp.ClampCamera(ww, wh);
        vp.UpdateRailZoom(ww, wh);
    }

    // --- Navigation ---

    public void GoToPage(int page)
    {
        if (FocusedViewport is not { } vp) return;
        vp.Zoom.Cancel();
        var doc = vp.Owner;
        var (ww, wh) = (vp.Width, vp.Height);
        int prevPage = vp.CurrentPage;
        if (!doc.GoToPage(vp, page, _worker, _config.NavigableRoles, ww, wh))
        {
            NotifyRenderFailed(page);
            return;
        }
        doc.QueueLookahead(vp, _config.AnalysisLookaheadPages);
        Search.UpdateCurrentPageMatches();
        // DocumentModel.GoToPage returns true without changing anything when the
        // target clamps to the current page; only announce a real transition.
        if (vp.CurrentPage != prevPage)
            RaisePageChanged(vp);
    }

    private void NotifyRenderFailed(int page)
        => StatusMessage?.Invoke($"Page {page + 1} could not be rendered (corrupted?)");

    public void FitPage()
    {
        if (FocusedViewport is not { } vp) return;
        vp.Zoom.Cancel();
        var (ww, wh) = (vp.Width, vp.Height);
        vp.CenterPage(ww, wh);
        vp.UpdateRailZoom(ww, wh);
    }

    public void FitWidth()
    {
        if (FocusedViewport is not { } vp) return;
        vp.Zoom.Cancel();
        var (ww, wh) = (vp.Width, vp.Height);
        vp.FitWidth(ww, wh);
        vp.UpdateRailZoom(ww, wh);
    }

    // --- Camera ---

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (FocusedViewport is not { } vp) return;
        var (ww, wh) = (vp.Width, vp.Height);

        if (ctrlHeld && vp.Rail.Active && !RailPaused)
        {
            double step = scrollDelta * 2.0 * vp.Camera.Zoom;
            vp.Camera.OffsetX += step;
            vp.ClampCamera(ww, wh);
        }
        else
        {
            double factor = 1.0 + scrollDelta * CoreTuning.ZoomScrollSensitivity;
            double baseZoom = vp.Zoom.PendingTargetZoom ?? vp.Camera.Zoom;
            double newZoom = Math.Clamp(baseZoom * factor, Camera.ZoomMin, Camera.ZoomMax);
            vp.Zoom.Start(vp, newZoom, cursorX, cursorY, ww);
        }
    }

    public void HandlePan(double dx, double dy, bool ctrlHeld = false)
    {
        if (FocusedViewport is not { } vp) return;
        vp.Zoom.Cancel();
        if (AutoScrollActive) StopAutoScroll();
        var (ww, wh) = (vp.Width, vp.Height);

        if (ctrlHeld && vp.Rail.Active && !RailPaused)
            StartRailPause(vp);

        vp.Camera.OffsetX += dx;
        vp.Camera.OffsetY += dy;
        vp.ClampCamera(ww, wh);
        if (vp.Rail.Active && !RailPaused)
            vp.Rail.CaptureVerticalBias(vp.Camera.OffsetY, vp.Camera.Zoom, wh);
    }

    private void StartRailPause(Viewport vp)
    {
        vp.RailPause = new(vp.Rail.CurrentBlock, vp.Rail.CurrentLine, vp.Rail.VerticalBias, vp.Camera.Zoom);
        StatusMessage?.Invoke("Free pan — release Ctrl to return");
    }

    /// <summary>
    /// End rail pause: restore block/line/bias/zoom from before the free pan and snap back.
    /// </summary>
    public void ResumeRailFromPause()
    {
        if (FocusedViewport is not { } vp) return;
        if (vp.RailPause is not { } pause) return;
        vp.RailPause = null;

        var (ww, wh) = (vp.Width, vp.Height);

        // Restore zoom if it changed during free pan (may re-enter rail mode)
        if (Math.Abs(vp.Camera.Zoom - pause.Zoom) > 0.001)
        {
            vp.Camera.Zoom = pause.Zoom;
            vp.Camera.NotifyZoomChange();
            vp.UpdateRailZoom(ww, wh);
            vp.UpdateRenderDpiIfNeeded();
        }

        if (!vp.Rail.Active) return;

        // Clamp indices in case analysis changed while paused
        vp.Rail.CurrentBlock = Math.Clamp(pause.Block, 0, Math.Max(vp.Rail.NavigableCount - 1, 0));
        vp.Rail.CurrentLine = Math.Clamp(pause.Line, 0, Math.Max(vp.Rail.CurrentLineCount - 1, 0));
        vp.Rail.VerticalBias = pause.VerticalBias;

        vp.StartSnap(ww, wh);
        FireReadingPositionChanged();
        StatusMessage?.Invoke("");
    }

    public void HandleZoomKey(bool zoomIn)
    {
        if (FocusedViewport is not { } vp) return;
        var (ww, wh) = (vp.Width, vp.Height);

        double baseZoom = vp.Zoom.PendingTargetZoom ?? vp.Camera.Zoom;
        double newZoom = Math.Clamp(
            zoomIn ? baseZoom * CoreTuning.ZoomStep : baseZoom / CoreTuning.ZoomStep,
            Camera.ZoomMin, Camera.ZoomMax);

        vp.Zoom.Start(vp, newZoom, ww / 2.0, wh / 2.0, ww);
        if (!vp.Rail.Active && AutoScrollActive) StopAutoScroll();
    }

    /// <summary>
    /// Smoothly animate zoom AND pan to an explicit camera target (native 180 ms cubic
    /// ease-out). Works in browse or rail mode.
    /// </summary>
    public void AnimateCameraTo(double targetZoom, double targetOffsetX, double targetOffsetY)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (FocusedViewport is not { } vp) return;
        var (ww, wh) = (vp.Width, vp.Height);
        double z = Math.Clamp(targetZoom, Camera.ZoomMin, Camera.ZoomMax);
        double cpx = (ww / 2.0 - targetOffsetX) / z; // target viewport-centre in page space
        double cpy = (wh / 2.0 - targetOffsetY) / z;
        vp.Zoom.StartTo(vp, z, targetOffsetX, targetOffsetY, cpx, cpy);
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
        if (FocusedViewport is not { } vp) return false;
        var doc = vp.Owner;
        if (!doc.TryGetAnalysis(vp.CurrentPage, vp.AnalysisParams, out var analysis)) return false;
        if (pageBlockIndex < 0 || pageBlockIndex >= analysis.Blocks.Count) return false;

        // Sync RailNav to THIS page's analysis so the index space + chunk framing below
        // refer to the current page. Skip the rebuild when it already holds this exact
        // analysis (the common case) — ReapplyNavigableRoles itself always rebuilds.
        if (!ReferenceEquals(vp.Rail.Analysis, analysis))
            doc.ReapplyNavigableRoles(vp, _config.NavigableRoles);

        var box = analysis.Blocks[pageBlockIndex].BBox;

        // Non-navigable role (figure/table/chart): centre it geometrically instead of failing.
        if (!vp.Rail.TrySetCurrentByPageIndex(pageBlockIndex, line))
            return CenterBlockGeometric(vp, box, targetZoom, durationMs);

        // Keep THIS block (and seated line) when the zoom crosses the rail threshold
        // mid-flight, regardless of overlapping block geometry under the focus point.
        vp.Rail.PinCurrentBlockForActivation();

        var (ww, wh) = (vp.Width, vp.Height);
        // Floor at the rail threshold (not ZoomMin) so rail framing actually applies.
        double z = Math.Clamp(targetZoom ?? vp.ComputeBlockFitZoom(box, ww, wh),
            _config.RailZoomThreshold, Camera.ZoomMax);

        var (ox, oy) = vp.Rail.ComputeSnapTarget(z, ww, wh);
        var lineInfo = vp.Rail.CurrentLineInfo; // seated block's target line
        if (AutoScrollActive) StopAutoScroll();
        vp.Zoom.StartTo(vp, z, ox, oy, lineInfo.X + lineInfo.Width / 2.0, lineInfo.Y, durationMs);
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
        if (FocusedViewport is not { } vp) return false;
        var doc = vp.Owner;
        if (!doc.TryGetAnalysis(vp.CurrentPage, vp.AnalysisParams, out var analysis)) return false;
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
    private bool CenterBlockGeometric(Viewport vp, BBox box, double? targetZoom, double? durationMs = null)
    {
        var (ww, wh) = (vp.Width, vp.Height);
        var (z, ox, oy) = vp.ComputeCenteredFrame(box, ww, wh, targetZoom);
        if (AutoScrollActive) StopAutoScroll();
        vp.Rail.Deactivate(); // drive the camera directly; no rail seat/snap
        vp.Zoom.StartCameraOnly(vp, z, ox, oy, durationMs);
        FireReadingPositionChanged(); // rail now inactive → reading position cleared
        return true;
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
        AutoScrollActive && (FocusedViewport?.Rail.AutoScrollParked ?? false);

    /// <summary>Resume flow from a semi-auto park (the reader pressed the advance key).</summary>
    public void ResumeAutoScrollFromPark() => FocusedViewport?.Rail.ResumeAutoScrollFromPark();

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
            // Flip the doc-level analysis params (and caches/queue) FIRST, once per document, so the
            // per-view loop below sees the new table/cell-nav variant.
            bool paramsChanged = doc.UpdateBackgroundSettings(_config);

            // Per-view settings reach EVERY view (rail, auto-scroll, render-DPI); a detached pane
            // must respond to a live settings change too (§8).
            foreach (var vp in doc.Viewports)
            {
                vp.AutoScroll.UpdateConfig(newConfig);
                vp.Rail.UpdateConfig(_config);
                vp.OnRenderQualityChanged(_config.RenderDpi);

                // A table-row / cell-nav toggle changes the cached analysis variant: re-fetch the
                // current page under the new params so the on-screen rail reflects it now (cache hit
                // re-seats synchronously; miss schedules a re-analysis and the §5.4 fan-out re-seats
                // when it lands). Only for pages already analysed — don't trigger fresh analysis on a
                // page that wasn't being shown with structure. Otherwise just re-seat the existing
                // analysis with the (possibly changed) navigable-role set.
                if (paramsChanged && doc.IsPageAnalysed(vp.CurrentPage))
                    doc.SubmitAnalysis(vp, _worker, _config.NavigableRoles);
                else
                    doc.ReapplyNavigableRoles(vp, _config.NavigableRoles);
            }
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

    private DocumentModel? ResolveDocument(int? index)
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
        // No index → the focused view is the single source of truth (it may be a secondary/detached
        // pane of the active document), matching the push event FireReadingPositionChanged(vp). An
        // explicit index targets another tab, which has no focused view, so report its Primary.
        Viewport? vp = index is null ? FocusedViewport : ResolveDocument(index)?.Primary;
        return vp is null ? null : BuildReadingPosition(vp, withText: true);
    }

    /// <summary>
    /// Builds a <see cref="ReadingPosition"/> for the given viewport's current rail
    /// position, or null if rail mode is inactive / has no navigable block / the
    /// block has no lines. When <paramref name="withText"/> is false the text
    /// fields are left empty (the push path skips text extraction for performance);
    /// callers that need text use <see cref="GetReadingPosition"/>. This is the
    /// single source of truth shared by the pull query and the push event so the
    /// two cannot drift.
    /// </summary>
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
        // Compute the horizontal fraction against THIS view's own width, not the ambient one, so a
        // detached pane with its own size reports a correct fraction for its AT-SPI / portal line
        // anchoring (railreader2#180 #3). The primary's Width tracks the ambient size, so the
        // single-view path is unchanged; a host sizes a detached pane via Viewport.SetSize.
        double hFraction = vp.Rail.ComputeHorizontalFraction(vp.Camera.OffsetX, vp.Camera.Zoom, vp.Width);
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
        if (!doc.TryGetAnalysis(targetPage, out var analysis)) return null;

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
        if (FocusedViewport is not { } vp) return false;
        if (!vp.Rail.Active || !vp.Rail.HasAnalysis) return false;
        if (!_config.NavigableRoles.Contains(target)) return false;
        if (!vp.Rail.TryNavigateToRole(target, forward)) return false;

        var (ww, wh) = (vp.Width, vp.Height);
        vp.StartSnap(ww, wh);
        FireReadingPositionChanged();
        return true;
    }

    /// <summary>
    /// Sets the pageChanged flag and announces the page change for <paramref name="vp"/> in one call,
    /// so a code path can't forget one or the other. Fires the view's own <see cref="Viewport.PageChanged"/>.
    /// </summary>
    private void FirePageChanged(ref bool pageChanged, Viewport vp)
    {
        pageChanged = true;
        RaisePageChanged(vp);
    }

    /// <summary>Announces a page change for <paramref name="vp"/> (no TickResult flag) via the view's
    /// own <see cref="Viewport.PageChanged"/>.</summary>
    private void RaisePageChanged(Viewport vp)
    {
        vp.PageChanged?.Invoke(vp.CurrentPage);
    }

    private void FireReadingPositionChanged()
    {
        if (FocusedViewport is { } vp) FireReadingPositionChanged(vp);
    }

    /// <summary>Announces a rail reading-position change for <paramref name="vp"/> via the view's own
    /// <see cref="Viewport.ReadingPositionChanged"/>. Builds the position lazily and only when someone
    /// is listening.</summary>
    private void FireReadingPositionChanged(Viewport vp)
    {
        if (vp.ReadingPositionChanged is null) return;
        if (BuildReadingPosition(vp, withText: false) is not { } pos) return;
        vp.ReadingPositionChanged?.Invoke(pos);
    }
}
