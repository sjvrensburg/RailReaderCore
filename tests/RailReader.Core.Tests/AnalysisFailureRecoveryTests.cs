using System.Collections.Concurrent;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #172: a request that dies inside the worker publishes no result, so before the fix a view
/// waiting on it kept <see cref="Viewport.PendingRailSetup"/> with nothing in flight — no rail for
/// the page, and background analysis held off for the whole document until the reader turned the
/// page. The worker now reports such requests (<see cref="AnalysisWorker.PollFailure"/>) and the
/// controller retries a waiting view once, then stops it waiting.
/// </summary>
public class AnalysisFailureRecoveryTests : IDisposable
{
    private readonly DocumentController _controller;
    private readonly WarnLogger _logger = new();

    private sealed class WarnLogger : ILogger
    {
        public readonly ConcurrentQueue<string> Warnings = new();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Enqueue(message);
        public void Error(string message, Exception? ex = null) { }
    }

    public AnalysisFailureRecoveryTests()
    {
        var appConfig = new AppConfig();
        _controller = new DocumentController(appConfig.ToCoreSettings(), appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory(), _logger);
        _controller.SetViewportSize(800, 600);
    }

    public void Dispose() => _controller.Dispose();

    private DocumentModel OpenDoc()
    {
        var doc = _controller.CreateDocument(TestFixtures.GetTestPdfPath()); // 3 pages
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);   // submits page 0's foreground analysis
        return doc;
    }

    /// <summary>Drives the controller's frame until <paramref name="done"/> holds or 10 s pass.</summary>
    private bool TickUntil(DocumentModel doc, Func<bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < 10000)
        {
            _controller.TickViewport(doc.Primary, 1 / 60.0);
            Thread.Sleep(5);
        }
        return done();
    }

    private static PageAnalysis Navigable() => new()
    {
        Blocks =
        [
            new LayoutBlock
            {
                BBox = new BBox(72f, 72f, 468f, 48f),
                Role = BlockRole.Text,
                Lines = [new LineInfo(80f, 16f, 72f, 468f), new LineInfo(96f, 16f, 72f, 468f)],
            },
        ],
    };

    /// <summary>An analyzer that throws on its first <paramref name="failures"/> calls.</summary>
    private static Func<ILayoutAnalyzer> FailingFirst(int failures, Action onCall) => () =>
    {
        int calls = 0;
        return new FakeLayoutAnalyzer(() =>
        {
            onCall();
            if (Interlocked.Increment(ref calls) <= failures)
                throw new InvalidOperationException("analyzer failure");
            return Navigable();
        });
    };

    [Fact]
    public void TransientFailure_OnForegroundPage_RetriesAndSeatsRail()
    {
        int calls = 0;
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            FailingFirst(1, () => Interlocked.Increment(ref calls)));
        var doc = OpenDoc();

        Assert.True(TickUntil(doc, () => !doc.Primary.PendingRailSetup),
            "view still waiting on a page whose only request failed");
        Assert.True(doc.Primary.Rail.HasAnalysis);   // the retry's result seated it
        Assert.True(doc.IsPageAnalysed(0));
        Assert.Empty(_logger.Warnings);              // retried, never given up

        // Background analysis is no longer held off: the other two pages follow.
        Assert.True(TickUntil(doc, () => doc.IsPageAnalysed(1) && doc.IsPageAnalysed(2)),
            "background analysis never resumed");
        Assert.Equal(4, Volatile.Read(ref calls));   // failed + retry + two background pages
    }

    [Fact]
    public void RepeatedFailure_OnForegroundPage_GivesUpAndBackgroundAnalysisResumes()
    {
        int calls = 0;
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            FailingFirst(2, () => Interlocked.Increment(ref calls)));
        var doc = OpenDoc();

        Assert.True(TickUntil(doc, () => !doc.Primary.PendingRailSetup),
            "view still waiting after the retry also failed");
        Assert.False(doc.Primary.Rail.HasAnalysis);  // given up, not seated
        Assert.Contains(_logger.Warnings, w => w.Contains("Page 0") && w.Contains("2 times"));

        // The document's other pages are analysed rather than stalled behind the failed one; the
        // background sweep also picks page 0 up again, and this time the analyzer succeeds.
        Assert.True(TickUntil(doc, () => doc.IsPageAnalysed(0) && doc.IsPageAnalysed(1) && doc.IsPageAnalysed(2)),
            "background analysis never resumed");
        Assert.True(TickUntil(doc, () => _controller.Worker!.IsIdle));
        Assert.Equal(5, Volatile.Read(ref calls));
    }

    [Fact]
    public void FatalAnalyzerStartup_ClearsPendingRailSetup()
    {
        // Every request is refused or stranded once the analyzer fails to load; neither may leave
        // the view waiting.
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => throw new InvalidOperationException("model missing"));
        var doc = OpenDoc();

        Assert.True(TickUntil(doc, () => !doc.Primary.PendingRailSetup),
            "view still waiting on a worker that can never analyse");
        Assert.NotNull(_controller.Worker!.StartupError);
        Assert.True(_controller.Worker.IsIdle);
    }

    [Fact]
    public void Worker_RequestsQueuedBeforeFatalStartup_AreReportedAsFailures()
    {
        using var loading = new ManualResetEventSlim(false);
        var rgb = new byte[800 * 800 * 3];
        using var worker = new AnalysisWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () =>
            {
                loading.Wait(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("model missing");
            },
            new SynchronousThreadMarshaller());

        try
        {
            Assert.True(worker.Submit(new AnalysisRequest("/tmp/doc.pdf", 4, rgb, 800, 800, 400d, 400d,
                null, new AnalysisParams())));
        }
        finally
        {
            loading.Set();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AnalysisFailure? failure = null;
        while ((failure = worker.PollFailure()) is null && sw.ElapsedMilliseconds < 10000)
            Thread.Sleep(5);

        Assert.NotNull(failure);
        Assert.Equal(4, failure.Page);
        Assert.IsType<InvalidOperationException>(failure.Error);
        Assert.True(worker.IsIdle);                  // the stranded key was released
        Assert.Null(worker.Poll());
    }
}
