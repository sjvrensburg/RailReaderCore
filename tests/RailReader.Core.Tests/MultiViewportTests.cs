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
