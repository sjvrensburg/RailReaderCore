using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// railreader2#276: a host that exits right after disposing must not find the analyzer still inside an
/// inference, or still holding its session. Dispose cancels the in-flight run and waits for the stage
/// threads, so by the time it returns the analyzer has been disposed.
/// </summary>
public class AnalysisWorkerShutdownTests
{
    private static readonly byte[] Rgb = new byte[800 * 800 * 3];

    private sealed class BlockingAnalyzer : ILayoutAnalyzer
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public volatile bool Disposed;
        public volatile bool SawCancellation;
        public LayoutModelCapabilities Capabilities => FakeLayoutAnalyzer.DefaultCapabilities;

        public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
            IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
        {
            Entered.Set();
            // Like ORT's Terminate: the run only ends when the token asks it to.
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            SawCancellation = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            return new PageAnalysis();
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Dispose_DuringInference_CancelsItAndDisposesTheAnalyzerBeforeReturning()
    {
        var analyzer = new BlockingAnalyzer();
        var worker = new AnalysisWorker(FakeLayoutAnalyzer.DefaultCapabilities, () => analyzer,
            new SynchronousThreadMarshaller());
        Assert.True(worker.Submit(new AnalysisRequest("/tmp/doc.pdf", 0, Rgb, 800, 800, 400d, 400d, null,
            new AnalysisParams())));
        Assert.True(analyzer.Entered.Wait(TimeSpan.FromSeconds(10)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        worker.Dispose();

        Assert.True(analyzer.SawCancellation);
        Assert.True(analyzer.Disposed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {sw.Elapsed}");
    }

    [Fact]
    public void Dispose_WhenIdle_DisposesTheAnalyzer_AndIsIdempotent()
    {
        var analyzer = new BlockingAnalyzer();
        var worker = new AnalysisWorker(FakeLayoutAnalyzer.DefaultCapabilities, () => analyzer,
            new SynchronousThreadMarshaller());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!worker.IsReady && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);

        worker.Dispose();
        worker.Dispose();

        Assert.True(analyzer.Disposed);
    }
}
