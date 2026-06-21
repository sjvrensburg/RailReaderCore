using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Capstone slice 5: a document can own more than one <see cref="Viewport"/>, and each renders
/// and ticks independently (its own page, DPI, camera, and zoom animation). These tests exercise
/// the additive multi-viewport surface end-to-end so it is never write-only — see
/// <c>docs/multi-viewport-design.md</c>.
/// </summary>
public class MultiViewportTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;

    public MultiViewportTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath(); // 3-page synthetic PDF
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        // No analysis worker — these tests exercise rendering + camera ticking, not analysis.
    }

    public void Dispose() => _controller.Dispose();

    private DocumentState SetupDoc()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        return doc;
    }

    [Fact]
    public void AddViewport_SeedsLiveViewSharingTheDocument()
    {
        var doc = SetupDoc();
        _controller.SetViewportSize(1024, 768);

        var vp2 = doc.AddViewport();

        Assert.Equal(2, doc.Viewports.Count);
        Assert.Same(doc.Primary, doc.Viewports[0]);
        Assert.Same(doc, vp2.Owner);
        Assert.True(vp2.IsLive);                 // focus-tracking default
        Assert.Contains(vp2, doc.Viewports);
        Assert.Equal(0, vp2.CurrentPage);        // a fresh view starts on page 0
        Assert.Equal(1024, vp2.Width);           // inherits the primary's (ambient) size
        Assert.Equal(768, vp2.Height);
    }

    [Fact]
    public void TwoViewports_RenderTheirOwnPageAtTheirOwnDpi()
    {
        var doc = SetupDoc();
        var vp1 = doc.Primary;
        var vp2 = doc.AddViewport();

        // Each view sits on its own page at its own zoom → its own rasterisation.
        vp1.CurrentPage = 0;
        vp1.Camera.Zoom = 1.0;
        Assert.True(vp1.LoadPageBitmap());

        vp2.CurrentPage = 1;
        vp2.Camera.Zoom = 3.0;
        Assert.True(vp2.LoadPageBitmap());

        Assert.Equal(0, vp1.CurrentPage);
        Assert.Equal(1, vp2.CurrentPage);
        Assert.NotNull(vp1.CachedPage);
        Assert.NotNull(vp2.CachedPage);
        Assert.NotSame(vp1.CachedPage, vp2.CachedPage);   // distinct bitmaps
        Assert.True(vp1.PageWidth > 0);
        Assert.True(vp2.PageWidth > 0);
        Assert.True(vp2.CachedDpi > vp1.CachedDpi);        // DPI ∝ zoom, per-view
    }

    [Fact]
    public void TwoViewports_TickIndependently()
    {
        var doc = SetupDoc();
        var vp1 = doc.Primary;
        var vp2 = doc.AddViewport();

        vp1.CurrentPage = 0;
        vp1.LoadPageBitmap();
        vp1.CenterPage(800, 600);
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        vp2.CenterPage(800, 600);

        // A distinct zoom animation per view.
        vp1.Camera.Zoom = 1.0;
        vp2.Camera.Zoom = 1.0;
        vp1.Zoom.Start(vp1, 2.0, 400, 300, 800);
        vp2.Zoom.Start(vp2, 4.0, 400, 300, 800);

        Thread.Sleep(220); // past the zoom duration so one tick completes each

        // Tick ONLY vp1: it animates to its own target and leaves vp2 untouched.
        _controller.TickViewport(vp1, 0.25);
        Assert.Equal(2.0, vp1.Camera.Zoom, 3);
        Assert.False(vp1.Zoom.IsAnimating);
        Assert.Equal(1.0, vp2.Camera.Zoom, 3);   // vp1's tick must not move vp2
        Assert.True(vp2.Zoom.IsAnimating);        // vp2's animation is still pending

        // Now tick vp2: it completes to its own, different target.
        _controller.TickViewport(vp2, 0.25);
        Assert.Equal(4.0, vp2.Camera.Zoom, 3);
        Assert.False(vp2.Zoom.IsAnimating);
        Assert.Equal(2.0, vp1.Camera.Zoom, 3);   // vp1 unchanged by vp2's tick
    }

    [Fact]
    public void RemoveViewport_ShrinksAndRejectsPrimary()
    {
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        Assert.NotNull(vp2.CachedPage);
        Assert.Equal(2, doc.Viewports.Count);

        doc.RemoveViewport(vp2);

        Assert.Single(doc.Viewports);
        Assert.Same(doc.Primary, doc.Viewports[0]);
        // The primary view lives for the document's lifetime and cannot be removed.
        Assert.Throws<InvalidOperationException>(() => doc.RemoveViewport(doc.Primary));
    }

    [Fact]
    public void PrimaryViewport_StillDrivesTheDocumentFacade()
    {
        // Adding a second view must not change the single-viewport facade: DocumentState's
        // CurrentPage/Camera still reflect the primary view.
        var doc = SetupDoc();
        doc.AddViewport();

        doc.Primary.CurrentPage = 2;
        Assert.Equal(2, doc.CurrentPage);
        Assert.Same(doc.Primary.Camera, doc.Camera);
    }

    [Fact]
    public void Analysis_FansOutToSecondaryViewportOnItsOwnPage()
    {
        // The §5.4 fan-out: analysis arriving for a SECONDARY viewport's page seats that view's
        // own rail (not the primary's), via the real worker pipeline with a fake analyzer.
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(MakeNavigableAnalysis));

        var doc = SetupDoc(); // Primary on page 0; AddDocument submits page-0 analysis
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();

        // Submit analysis for vp2's page (1). Primary stays on page 0.
        doc.SubmitAnalysis(vp2, _controller.Worker, _controller.Config.NavigableRoles);
        Assert.True(vp2.PendingRailSetup);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp2.PendingRailSetup && sw.ElapsedMilliseconds < 5000)
        {
            _controller.PollAnalysisResults();
            Thread.Sleep(10);
        }

        Assert.False(vp2.PendingRailSetup);                 // the fan-out cleared it
        Assert.True(vp2.Rail.HasAnalysis);                  // vp2's OWN rail got seated
        Assert.Equal(1, vp2.CurrentPage);
        Assert.True(doc.AnalysisCache.ContainsKey(1));      // cached at the model level
        Assert.NotSame(doc.Primary.Rail, vp2.Rail);         // independent rails
    }

    [Fact]
    public void PerViewportPageChanged_FiresForFocusedViewAndMirrorsToController()
    {
        var doc = SetupDoc();
        int? viewEvent = null, controllerEvent = null;
        doc.Primary.PageChanged += p => viewEvent = p;          // per-viewport event
        _controller.PageChanged = p => controllerEvent = p;     // focused-view facade

        _controller.GoToPage(2);

        Assert.Equal(2, doc.Primary.CurrentPage);
        Assert.Equal(2, viewEvent);        // the view's own PageChanged fired
        Assert.Equal(2, controllerEvent);  // and the controller-level facade mirrored it
    }

    [Fact]
    public void Analysis_FiresPerViewportReadingPosition_ForLiveNonFocusedView()
    {
        // A live (IsLive) detached pane that is NOT focused still fires its own ReadingPositionChanged
        // when the fan-out seats its rail — gap #3 (IsLive consulted), not just the focused view.
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(MakeNavigableAnalysis));

        var doc = SetupDoc();                // focus stays on doc.Primary
        var vp2 = doc.AddViewport();         // a detached pane; IsLive defaults true
        Assert.NotSame(vp2, _controller.FocusedViewport);
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        vp2.Camera.Zoom = 6.0;               // above RailZoomThreshold (3.0) so the seated rail activates

        ReadingPosition? vp2Pos = null;
        vp2.ReadingPositionChanged += p => vp2Pos = p;

        doc.SubmitAnalysis(vp2, _controller.Worker, _controller.Config.NavigableRoles);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp2.PendingRailSetup && sw.ElapsedMilliseconds < 5000)
        {
            _controller.PollAnalysisResults();
            Thread.Sleep(10);
        }

        Assert.True(vp2.Rail.Active);        // seated + activated
        Assert.NotNull(vp2Pos);              // the live, non-focused view fired its OWN event
        Assert.Equal(1, vp2Pos!.Page);
    }

    [Fact]
    public void FocusedViewport_IsSourceOfTruth_ActiveDocumentFollows()
    {
        var docA = SetupDoc(); // single doc; AddDocument focuses its primary
        Assert.Same(docA, _controller.ActiveDocument);
        Assert.Same(docA.Primary, _controller.FocusedViewport);

        var docB = _controller.CreateDocument(_pdfPath);
        docB.LoadPageBitmap();
        _controller.AddDocument(docB); // focuses docB.Primary
        Assert.Same(docB, _controller.ActiveDocument);
        Assert.Equal(1, _controller.ActiveDocumentIndex);

        // Setting focus back to docA's primary moves the active document + index with it.
        _controller.FocusedViewport = docA.Primary;
        Assert.Same(docA, _controller.ActiveDocument);
        Assert.Equal(0, _controller.ActiveDocumentIndex);

        // A view whose document isn't open is rejected (focus unchanged).
        var orphan = _controller.CreateDocument(_pdfPath);
        _controller.FocusedViewport = orphan.Primary;
        Assert.Same(docA, _controller.ActiveDocument);
        orphan.Dispose();
    }

    [Fact]
    public void ConfigChange_PropagatesToEveryViewport()
    {
        // Slice C: per-view settings reach EVERY view, not just the primary — a detached pane must
        // respond to a live settings change too (§8). Rail.ZoomThreshold mirrors RailZoomThreshold.
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();
        Assert.Equal(3.0, vp2.Rail.ZoomThreshold, 3);   // default RailZoomThreshold

        _controller.OnConfigChanged(_controller.Config with { RailZoomThreshold = 7.0 });

        Assert.Equal(7.0, doc.Primary.Rail.ZoomThreshold, 3);  // primary tracks the new config
        Assert.Equal(7.0, vp2.Rail.ZoomThreshold, 3);          // and the detached pane too
    }

    [Fact]
    public void HorizontalArrow_PansFocusedViewport_LeavingPrimaryUntouched()
    {
        // Regression (railreader2#180): horizontal arrow nav (and click) must act on the FOCUSED view,
        // not the document's primary. Vertical nav / zoom / pan were routed through FocusedViewport in
        // 0.39.0, but horizontal scroll + click were missed, so a focused secondary's left/right moved
        // the primary instead. Rail inactive here → arrow = a plain horizontal pan.
        var doc = SetupDoc();                       // focused primary on page 0
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 0;
        vp2.LoadPageBitmap();

        // Fit, then zoom the focused view far past fit (CenterPage itself resets zoom to fit) so the
        // small synthetic page overflows the viewport and there is real room to pan; clamp to a valid
        // start. The primary is left at its fit position and must not move.
        vp2.CenterPage(800, 600);
        vp2.Camera.Zoom = 40.0;
        vp2.ClampCamera(800, 600);
        double primaryX = doc.Primary.Camera.OffsetX;
        double vp2X = vp2.Camera.OffsetX;

        _controller.FocusedViewport = vp2;
        _controller.HandleArrowRight();             // horizontal pan forward on the focused view

        Assert.NotEqual(vp2X, vp2.Camera.OffsetX);             // the focused secondary panned
        Assert.Equal(primaryX, doc.Primary.Camera.OffsetX, 6); // the primary did NOT move (the bug)
    }

    [Fact]
    public void PumpedTick_DrainsWorkerAndFansOutToAnUntickedView()
    {
        // Slice D pump-once: the analysis pump is document-global. A host ticks ONE view with
        // pumpAnalysis:true; that single pump drains the worker and fans results out to every
        // view of the document — including views never ticked this frame (§5.5).
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(MakeNavigableAnalysis));

        var doc = SetupDoc();             // focused primary on page 0
        var vp1 = doc.Primary;
        var vp2 = doc.AddViewport();      // never ticked below
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();

        doc.SubmitAnalysis(vp2, _controller.Worker, _controller.Config.NavigableRoles);
        Assert.True(vp2.PendingRailSetup);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp2.PendingRailSetup && sw.ElapsedMilliseconds < 5000)
        {
            _controller.TickViewport(vp1, 0.016, pumpAnalysis: true); // only vp1 is ticked
            Thread.Sleep(10);
        }

        Assert.False(vp2.PendingRailSetup);   // vp1's pump fanned out to the un-ticked vp2
        Assert.True(vp2.Rail.HasAnalysis);
    }

    [Fact]
    public void PumplessTickOverload_AdvancesViewOwnAnimation()
    {
        // Slice D: the pumpless overload still advances the ticked view's own camera animation —
        // it only skips the document-global analysis pump (which a host runs once per frame).
        var doc = SetupDoc();             // no worker initialised → no rail/analysis interference
        var vp = doc.Primary;
        vp.CenterPage(800, 600);
        vp.Camera.Zoom = 1.0;
        vp.Zoom.Start(vp, 2.0, 400, 300, 800);
        Thread.Sleep(220);                // past the zoom duration

        var r = _controller.TickViewport(vp, 0.25, pumpAnalysis: false);

        Assert.Equal(2.0, vp.Camera.Zoom, 3);   // the pumpless overload still ticked the view
        Assert.False(vp.Zoom.IsAnimating);
        Assert.True(r.CameraChanged);
    }

    [Fact]
    public void RemovingFocusedViewport_RepointsFocusToPrimary_AndTickIsSafe()
    {
        // Review fix: FocusedViewport is the single source of truth (it backs ActiveDocument and the
        // per-frame tick). Removing the focused view must re-point focus off the now-disposed view —
        // otherwise the next Tick dereferences a torn-down viewport (disposed Cts → ObjectDisposedException).
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();
        vp2.LoadPageBitmap();
        _controller.FocusedViewport = vp2;
        Assert.Same(vp2, _controller.FocusedViewport);

        doc.RemoveViewport(vp2);

        Assert.Same(doc.Primary, _controller.FocusedViewport);   // re-pointed, not left dangling
        Assert.Same(doc, _controller.ActiveDocument);
        Assert.Null(Record.Exception(() => _controller.Tick(0.016))); // ticking after removal is safe

        // The setter also rejects focusing an already-removed view.
        var vp3 = doc.AddViewport();
        doc.RemoveViewport(vp3);
        _controller.FocusedViewport = vp3;
        Assert.Same(doc.Primary, _controller.FocusedViewport);   // unchanged — rejected
    }

    [Fact]
    public void PumpDrainsLookaheadForANonFocusedViewport()
    {
        // Review fix (§5.5): eager lookahead is per-view. The pump must drain EVERY live view's
        // PendingAnalysis queue, not just the focused/primary one — else a secondary view's
        // read-ahead never fires. Driven entirely by the focused primary's pump.
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(MakeNavigableAnalysis));

        var doc = SetupDoc();                  // focused primary on page 0
        var vp2 = doc.AddViewport();           // never ticked, never focused
        doc.Primary.PendingAnalysis.Clear();   // isolate: only vp2 has lookahead queued
        doc.QueueLookahead(vp2, 2);            // enqueue pages 1,2 into vp2's own queue
        Assert.Equal(2, vp2.PendingAnalysis.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp2.PendingAnalysis.Count == 2 && sw.ElapsedMilliseconds < 5000)
        {
            _controller.TickViewport(doc.Primary, 0.016, pumpAnalysis: true); // only the primary ticks
            Thread.Sleep(10);
        }

        Assert.True(vp2.PendingAnalysis.Count < 2);  // the pump drained the non-focused view's queue
    }

    [Fact]
    public void ControllerNavInput_RoutesToFocusedSecondaryViewport()
    {
        // Input routing: with a secondary focused, controller-level GoToPage must move THAT view
        // (not Primary) and fire the controller-level PageChanged facade for it.
        var doc = SetupDoc();                 // focus on doc.Primary, page 0
        var vp2 = doc.AddViewport();
        vp2.LoadPageBitmap();
        _controller.FocusedViewport = vp2;

        int? controllerPage = null;
        _controller.PageChanged = p => controllerPage = p;

        _controller.GoToPage(2);

        Assert.Equal(2, vp2.CurrentPage);          // the FOCUSED secondary moved
        Assert.Equal(0, doc.Primary.CurrentPage);  // Primary untouched
        Assert.Equal(2, controllerPage);           // controller PageChanged fired for the focused view
    }

    [Fact]
    public void ControllerCameraInput_RoutesToFocusedSecondaryViewport()
    {
        // A synchronous camera op (FitWidth) targets the focused secondary, leaving Primary's camera
        // alone — proving zoom/pan/fit input follows focus, not Primary.
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();
        vp2.LoadPageBitmap();
        vp2.Camera.Zoom = 5.0;                     // a distinct starting zoom
        double primaryZoom = doc.Primary.Camera.Zoom;
        _controller.FocusedViewport = vp2;

        _controller.FitWidth();

        Assert.NotEqual(5.0, vp2.Camera.Zoom);                       // vp2 was refit
        Assert.Equal(primaryZoom, doc.Primary.Camera.Zoom, 3);       // Primary untouched
    }

    [Fact]
    public void DisplayPrefs_ArePerViewport_AndDocFacadeTracksPrimary()
    {
        // railreader2#180 #2: each viewport carries its own display prefs, exposed publicly on
        // Viewport; the DocumentState facade still reflects the primary's value.
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();

        string? vp2Notified = null;
        vp2.StateChanged = n => vp2Notified = n;

        vp2.LineFocusBlur = true;                              // per-view set fires the view's event
        Assert.Equal(nameof(Viewport.LineFocusBlur), vp2Notified);

        Assert.True(vp2.LineFocusBlur);
        Assert.False(doc.Primary.LineFocusBlur);              // independent of the primary
        Assert.False(doc.LineFocusBlur);                      // doc facade tracks the primary, not vp2

        doc.LineHighlightEnabled = false;                     // facade drives the primary's per-view value
        Assert.False(doc.Primary.LineHighlightEnabled);
        Assert.True(vp2.LineHighlightEnabled);                // vp2 keeps its own (default true)

        vp2.DebugOverlay = true;                              // the rest of the per-view set is exposed too
        vp2.MarginCropping = true;
        vp2.ColourEffect = ColourEffect.Invert;
        Assert.True(vp2.DebugOverlay && vp2.MarginCropping);
        Assert.Equal(ColourEffect.Invert, vp2.ColourEffect);
        Assert.False(doc.DebugOverlay);                       // primary untouched throughout
    }

    [Fact]
    public void ReadingPosition_UsesPerViewWidth_ForHorizontalFraction()
    {
        // railreader2#180 #3: a detached pane's horizontal fraction is computed against ITS width
        // (set via Viewport.SetSize), not the controller's ambient size.
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(MakeNavigableAnalysis));

        var doc = SetupDoc();                 // ambient width 800
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        vp2.Camera.Zoom = 6.0;                // block (468pt) → ~2808px, exceeds both 800 and 1234
        vp2.SetSize(1234, 600);               // a per-view width distinct from the ambient 800

        ReadingPosition? pos = null;
        vp2.ReadingPositionChanged += p => pos = p;

        doc.SubmitAnalysis(vp2, _controller.Worker, _controller.Config.NavigableRoles);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vp2.PendingRailSetup && sw.ElapsedMilliseconds < 5000)
        {
            _controller.PollAnalysisResults();
            Thread.Sleep(10);
        }

        Assert.NotNull(pos);
        // The emitted fraction matches the one computed against vp2's OWN width — not the ambient 800.
        Assert.Equal(
            vp2.Rail.ComputeHorizontalFraction(vp2.Camera.OffsetX, vp2.Camera.Zoom, vp2.Width),
            pos!.HorizontalFraction, 5);
    }

    [Fact]
    public void FocusedSecondaryViewport_AnnotatesAndHitTestsItsOwnPage_NotPrimary()
    {
        // Finding 1 (issue #74): annotation add / hit-test / erase resolve the FOCUSED view's
        // CurrentPage — not the document's primary. A focused secondary pane on a different page than
        // the primary must author + hit-test on ITS page.
        var doc = SetupDoc();                    // Primary focused on page 0
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        _controller.FocusedViewport = vp2;       // focus the secondary (on page 1)
        var focus = _controller.FocusedViewport;

        // Author a freehand through the focused view: it lands on page 1, not the primary's page 0.
        _controller.Annotations.SetAnnotationTool(AnnotationTool.Pen);
        _controller.Annotations.HandleAnnotationPointerDown(focus, 100, 100);
        _controller.Annotations.HandleAnnotationPointerMove(focus, 110, 110);
        _controller.Annotations.HandleAnnotationPointerMove(focus, 120, 120);
        _controller.Annotations.HandleAnnotationPointerUp(focus, 120, 120);

        Assert.True(doc.Annotations.Pages.ContainsKey(1));   // authored on the focused view's page
        Assert.Single(doc.Annotations.Pages[1]);
        Assert.False(doc.Annotations.Pages.ContainsKey(0));  // the primary's page is untouched (the bug)

        // The static page-annotations helper is likewise keyed to the view's page.
        Assert.Single(AnnotationInteractionHandler.GetCurrentPageAnnotations(vp2)!);
        Assert.Null(AnnotationInteractionHandler.GetCurrentPageAnnotations(doc.Primary));

        // Eraser hit-tests the focused view's page too: erasing the page-1 markup empties page 1.
        _controller.Annotations.SetAnnotationTool(AnnotationTool.Eraser);
        _controller.Annotations.HandleAnnotationPointerDown(focus, 110, 110);
        Assert.Empty(doc.Annotations.Pages[1]);
    }

    [Fact]
    public void Search_ResolvesFocusedViewportsPage_AndExposesPerPageMatches()
    {
        // Finding 1 (issue #74): "current page" search matches track the FOCUSED view's page, and a host
        // can fetch any page's matches via MatchesForPage to render each pane's own highlights.
        var doc = SetupDoc();                 // Primary focused on page 0
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();

        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(10, 10, 50, 20)]),
            new(1, 0, 4, [new RectF(10, 10, 50, 20)]),
            new(1, 30, 4, [new RectF(10, 40, 50, 20)]),
        };
        _controller.Search.FinalizeSearch(doc, matches);

        // Per-page accessor exposes each page's matches independently (for per-pane highlight rendering).
        Assert.Single(_controller.Search.MatchesForPage(0)!);
        Assert.Equal(2, _controller.Search.MatchesForPage(1)!.Count);
        Assert.Null(_controller.Search.MatchesForPage(2));

        // Focus is on the primary (page 0): current-page matches reflect page 0.
        Assert.Single(_controller.Search.CurrentPageSearchMatches!);

        // Focus the secondary (page 1): current-page matches now reflect ITS page, not the primary's.
        _controller.FocusedViewport = vp2;
        _controller.Search.UpdateCurrentPageMatches();
        Assert.Equal(2, _controller.Search.CurrentPageSearchMatches!.Count);
    }

    [Fact]
    public void AsyncAnalysisSeat_FramesEachViewportAgainstItsOwnSize()
    {
        // Finding 2 (issue #74): a cache-miss analysis result arriving asynchronously must seat each
        // waiting view's rail against THAT view's size, not the controller's single ambient size. Two
        // panes of different widths on the same page/zoom therefore frame the block to different camera
        // offsets; before the fix the shared ambient size landed them identically.
        var cfg = new AppConfig { PixelSnapping = false, SnapDurationMs = 1 };
        using var controller = new DocumentController(cfg.ToCoreSettings(), cfg, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(MakeNavigableAnalysis));

        var doc = controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        controller.AddDocument(doc);
        controller.SetViewportSize(800, 600);    // ambient — distinct from both panes below

        // Two detached panes on the same page at the same zoom but DIFFERENT widths. Zoom 3.2 keeps the
        // 468pt block (≈1498px) narrower than both widths so neither hard-clamps and the frame is a pure
        // function of window width.
        var narrow = doc.AddViewport();
        narrow.CurrentPage = 1; narrow.LoadPageBitmap();
        narrow.Camera.Zoom = 3.2; narrow.SetSize(1600, 600);

        var wide = doc.AddViewport();
        wide.CurrentPage = 1; wide.LoadPageBitmap();
        wide.Camera.Zoom = 3.2; wide.SetSize(2600, 600);

        doc.SubmitAnalysis(narrow, controller.Worker, controller.Config.NavigableRoles);
        doc.SubmitAnalysis(wide, controller.Worker, controller.Config.NavigableRoles);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((narrow.PendingRailSetup || wide.PendingRailSetup) && sw.ElapsedMilliseconds < 5000)
        {
            controller.PollAnalysisResults();
            Thread.Sleep(10);
        }
        Assert.True(narrow.Rail.Active && wide.Rail.Active);

        // Sanity: the frame for this block genuinely depends on window width, so the two per-view targets
        // differ — guards against an accidental width-insensitive regime.
        var (narrowTarget, _) = narrow.Rail.ComputeSnapTarget(3.2, narrow.Width, narrow.Height);
        var (wideTarget, _) = wide.Rail.ComputeSnapTarget(3.2, wide.Width, wide.Height);
        Assert.NotEqual(narrowTarget, wideTarget);

        // Complete each pane's seat snap (TickViewport drives the per-view size too).
        Thread.Sleep(10);
        controller.TickViewport(narrow, 0.1, pumpAnalysis: false);
        controller.TickViewport(wide, 0.1, pumpAnalysis: false);

        // Each pane landed on the frame computed for ITS own width — not a shared ambient frame.
        Assert.Equal(narrowTarget, narrow.Camera.OffsetX, 3);
        Assert.Equal(wideTarget, wide.Camera.OffsetX, 3);
    }

    // A one-block (3-line) navigable Text analysis so a seated rail reports HasAnalysis.
    private static PageAnalysis MakeNavigableAnalysis()
    {
        var lines = new List<LineInfo>();
        for (int l = 0; l < 3; l++)
            lines.Add(new LineInfo(72f + l * 16f, 16f, 72f, 468f));
        return new PageAnalysis
        {
            PageWidth = 612,
            PageHeight = 792,
            Blocks =
            [
                new LayoutBlock
                {
                    BBox = new BBox(72f, 72f, 468f, 48f),
                    Role = BlockRole.Text,
                    Confidence = 0.95f,
                    Order = 0,
                    Lines = lines,
                },
            ],
        };
    }
}
