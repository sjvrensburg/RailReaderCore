using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the controller/rotation concurrency audit findings: stale-generation
/// renders (rotation / render-quality change racing an in-flight prefetch or DPI re-render),
/// stale-frame analysis results (rotation racing an in-flight worker inference), the GoToPage
/// rollback that the confinement chokepoint used to swallow, ViewportRemoved as a proper event,
/// background-tab close preserving the focused tab's search, and the analysis worker surviving
/// per-request analyzer exceptions.
/// </summary>
public class ControllerConcurrencyAuditTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;
    private readonly AppConfig _appConfig;

    public ControllerConcurrencyAuditTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath(); // 3-page synthetic PDF
        _appConfig = new AppConfig();
        _controller = new DocumentController(_appConfig.ToCoreSettings(), _appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    private DocumentModel SetupDoc()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        return doc;
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(10);
        Assert.True(condition(), "condition not met within timeout");
    }

    // --- Finding: ViewportRemoved must be a real event, not a clobberable single-slot callback ---

    [Fact]
    public void ViewportRemoved_HostSubscription_DoesNotClobberControllerFocusHook()
    {
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();

        // A host subscribing for its own pane teardown AFTER AddDocument must not displace the
        // controller's dangling-focus hook (with the old single-slot Action it silently did,
        // leaving FocusedViewport pointing at the disposed view).
        Viewport? hostSaw = null;
        doc.ViewportRemoved += vp => hostSaw = vp;

        _controller.FocusedViewport = vp2;
        Assert.Same(vp2, _controller.FocusedViewport);

        doc.RemoveViewport(vp2);

        Assert.Same(vp2, hostSaw);                              // host notified
        Assert.Same(doc.Primary, _controller.FocusedViewport);  // controller hook survived too
    }

    // --- Finding: analysis worker loop must survive a per-request analyzer exception ---

    [Fact]
    public void AnalysisWorker_SurvivesPerRequestException_AndClearsInFlight()
    {
        int calls = 0;
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(() =>
                Interlocked.Increment(ref calls) == 1
                    ? throw new InvalidOperationException("synthetic analyzer failure")
                    : new PageAnalysis()));

        var doc = SetupDoc(); // AddDocument submits page-0 analysis → first RunAnalysis throws
        var vp = doc.Primary;
        var pars = vp.AnalysisParams;

        // Wait for the failing request to reach the analyzer and drain (the worker releases the
        // _inFlight key on failure — before the fix it stayed stuck forever and the loop died).
        WaitUntil(() => Volatile.Read(ref calls) >= 1);
        WaitUntil(() => !_controller.Worker!.IsInFlight(doc.FilePath, 0, pars));
        Assert.True(_controller.Worker!.IsIdle);
        Assert.False(doc.IsPageAnalysed(0));

        // The loop must still be alive: a resubmission for the same page completes normally.
        doc.SubmitAnalysis(vp, _controller.Worker, _controller.Config.NavigableRoles);
        WaitUntil(() =>
        {
            _controller.PollAnalysisResults();
            return doc.IsPageAnalysed(0);
        });
        Assert.False(vp.PendingRailSetup);
        Assert.True(_controller.Worker!.IsIdle);
    }

    // --- Finding: a rotation racing an in-flight inference must not seat old-frame analysis ---

    [Fact]
    public void SetViewRotation_WhileAnalysisInFlight_RejectsStaleFrameAndReseats()
    {
        using var gate = new SemaphoreSlim(0);
        int calls = 0;
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(() =>
            {
                // Hold the FIRST inference (the pre-rotation frame) until the test rotated the view.
                if (Interlocked.Increment(ref calls) == 1) gate.Wait(TimeSpan.FromSeconds(10));
                return new PageAnalysis();
            }));

        var doc = SetupDoc(); // AddDocument submits page-0 analysis in the rotation-0 frame
        var vp = doc.Primary;
        var pars = vp.AnalysisParams;
        WaitUntil(() => _controller.Worker!.IsInFlight(doc.FilePath, 0, pars));

        // Rotate while that inference is in flight: caches clear, and the per-view resubmission is
        // suppressed by IsInFlight (this very request) — only PendingRailSetup is set.
        _controller.SetViewRotation(1);
        Assert.True(vp.PendingRailSetup);
        Assert.True(vp.PageWidth > vp.PageHeight, "quarter-turn must swap the displayed axes");

        gate.Release(); // let the STALE (rotation-0) result land

        // The stale result must be rejected (not cached / not seated) and a fresh submission made;
        // the reseat completes with NEW-frame geometry (FakeLayoutAnalyzer stamps the submitted
        // page dimensions into the analysis, so the frame it ran in is observable).
        WaitUntil(() =>
        {
            _controller.PollAnalysisResults();
            return !vp.PendingRailSetup && doc.IsAnalysed(0, pars);
        });

        Assert.True(doc.TryGetAnalysis(0, pars, out var cached));
        Assert.True(cached.PageWidth > cached.PageHeight,
            "cached analysis must be in the rotated (landscape) frame, not the stale pre-rotation one");
        Assert.Equal(vp.PageWidth, cached.PageWidth, 0.5);
    }

    // --- Finding: GoToPage's failure rollback must beat the confinement chokepoint ---

    /// <summary>Delegates to a real Skia-backed service but fails to render one page, simulating a
    /// corrupted destination page mid-navigation.</summary>
    private sealed class FailingRenderPdfService(IPdfService inner, int failPage) : IPdfService
    {
        public byte[] PdfBytes => inner.PdfBytes;
        public string? Password => inner.Password;
        public int PageCount => inner.PageCount;
        public List<OutlineEntry> Outline => inner.Outline;
        public (double Width, double Height) GetPageSize(int pageIndex) => inner.GetPageSize(pageIndex);
        public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
            => pageIndex == failPage
                ? throw new InvalidOperationException("synthetic corrupt page")
                : inner.RenderPage(pageIndex, dpi);
        public IRenderedPage RenderThumbnail(int pageIndex) => inner.RenderThumbnail(pageIndex);
        public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
            => inner.RenderPagePixmap(pageIndex, targetSize);
    }

    [Fact]
    public void GoToPage_RenderFailure_RollsBackEvenWhenFocusTargetsDestination()
    {
        var factory = TestFixtures.CreatePdfFactory();
        var pdf = new FailingRenderPdfService(factory.CreatePdfService(_pdfPath), failPage: 1);
        var config = _appConfig.ToCoreSettings();
        using var doc = new DocumentModel(_pdfPath, pdf, factory.CreatePdfTextService(),
            factory.CreatePdfLinkService(), config, new SynchronousThreadMarshaller());
        var vp = doc.Primary;
        vp.SetSize(800, 600);
        Assert.True(doc.LoadPageBitmap()); // page 0 renders fine

        // The RetargetFocus flow: Focus is assigned for the DESTINATION page first (momentarily
        // un-confining the view), then the navigation runs. When the destination page fails to
        // render, the rollback must not be swallowed by the CurrentPage setter's confinement guard
        // (the view became confined the instant the destination page committed).
        vp.Focus = new FocusBlock(1, 0, new BBox(10, 10, 100, 100));
        bool ok = doc.GoToPage(vp, 1, null, config.NavigableRoles, 800, 600);

        Assert.False(ok);
        Assert.Equal(0, vp.CurrentPage); // rolled back — not stranded on the unrendered page
        Assert.NotNull(vp.CachedPage);   // still showing page 0's bitmap, consistent with CurrentPage
    }

    // --- Finding: stale-generation renders must not install over a rotation/quality re-render ---

    [Fact]
    public void RenderGeneration_Bumps_OnRotationAndRenderQualityChange()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;

        int g0 = vp.RenderGeneration;
        doc.ViewRotation = 1;
        Assert.True(vp.RenderGeneration > g0, "rotation must invalidate in-flight renders");

        int g1 = vp.RenderGeneration;
        vp.OnRenderQualityChanged(RenderDpiSettings.ForPreset(RenderQuality.Performance));
        Assert.True(vp.RenderGeneration > g1, "a render-quality change must invalidate in-flight renders");
    }

    [Fact]
    public void LoadPageBitmap_DropsStaleGenerationPrefetch()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;

        // Hand-craft a prefetch buffer for the current page stamped with a PREVIOUS generation —
        // exactly what an in-flight prefetch scheduled before a rotation / quality change carries.
        var stalePage = doc.Pdf.RenderPage(vp.CurrentPage, 96, 0);
        var staleMini = doc.Pdf.RenderThumbnail(vp.CurrentPage, 0);
        vp.Prefetched = new Viewport.PrefetchedPageData(vp.CurrentPage, 96, stalePage, staleMini,
            612, 792, vp.RenderGeneration - 1);

        Assert.True(vp.LoadPageBitmap());

        Assert.Null(vp.Prefetched);            // stale buffer dropped, not left to be consumed later
        Assert.NotSame(stalePage, vp.CachedPage); // a fresh render was installed instead
    }

    // --- Finding: LoadPageBitmap's background-thread contract vs UI-thread-only SetField ---

    /// <summary>A marshaller that fails hard on off-UI-thread mutation (like the desktop's
    /// Debug-build assert) and queues posted work like a real dispatcher.</summary>
    private sealed class StrictQueueingMarshaller : IThreadMarshaller
    {
        private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
        private readonly List<Action> _queue = [];
        public void Post(Action action) { lock (_queue) _queue.Add(action); }
        public void AssertUIThread()
        {
            if (Environment.CurrentManagedThreadId != _uiThreadId)
                throw new InvalidOperationException("Viewport mutated off the UI thread");
        }
        public void Drain()
        {
            List<Action> pending;
            lock (_queue) { pending = [.. _queue]; _queue.Clear(); }
            foreach (var a in pending) a();
        }
    }

    [Fact]
    public async Task LoadPageBitmap_FromBackgroundThread_MarshalsNotificationsToUIThread()
    {
        var factory = TestFixtures.CreatePdfFactory();
        var marshaller = new StrictQueueingMarshaller();
        using var doc = new DocumentModel(_pdfPath, factory.CreatePdfService(_pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(),
            _appConfig.ToCoreSettings(), marshaller);

        var fired = new List<string>();
        doc.StateChanged += fired.Add;

        // The desktop host's document-open flow: LoadPageBitmap inside Task.Run, per the method's
        // own contract. Before the fix, the PageWidth/PageHeight SetField path asserted UI-thread
        // (crashing a Debug host) and invoked StateChanged synchronously on the pool thread.
        bool ok = await Task.Run(() => doc.LoadPageBitmap());

        Assert.True(ok);
        Assert.True(doc.PageWidth > 0);
        Assert.Empty(fired); // no subscriber ran on the pool thread

        marshaller.Drain();  // the "dispatcher" cycle delivers the notifications on the UI thread
        Assert.Contains(nameof(DocumentModel.PageWidth), fired);
        Assert.Contains(nameof(DocumentModel.PageHeight), fired);
    }

    // --- Finding: closing a background tab must not drop the focused tab's active search ---

    [Fact]
    public void CloseDocument_BackgroundTab_PreservesFocusedTabSearch()
    {
        var docA = SetupDoc();
        var docB = SetupDoc(); // AddDocument focuses docB
        Assert.Same(docB.Primary, _controller.FocusedViewport);

        _controller.Search.ExecuteSearch("test", caseSensitive: false, useRegex: false);
        Assert.NotEmpty(_controller.Search.SearchMatches);

        _controller.CloseDocument(0); // close the BACKGROUND tab (docA)
        Assert.Same(docB.Primary, _controller.FocusedViewport);
        Assert.NotEmpty(_controller.Search.SearchMatches); // in-progress search survives

        _controller.CloseDocument(0); // now close the FOCUSED (searched) tab
        Assert.Empty(_controller.Search.SearchMatches);    // search context gone → cleared
    }
}
