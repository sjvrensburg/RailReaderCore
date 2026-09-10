using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for <see cref="AnalysisWorker"/>'s optional result-available notification
/// (issue #118) — the callback that lets a host stop polling a timer to notice when
/// <see cref="AnalysisWorker.Poll"/> has something to return.
/// </summary>
public class AnalysisWorkerResultSignalTests
{
    private static AnalysisRequest Request(int page = 0, CancellationToken cancellation = default) =>
        new("/tmp/doc.pdf", page, new byte[800 * 800 * 3], 800, 800, 400d, 400d,
            null, new AnalysisParams(), Cancellation: cancellation);

    private static PageAnalysis OneBlock() => new()
    {
        Blocks = [new LayoutBlock { BBox = new BBox(0f, 0f, 400f, 400f), Role = BlockRole.Text }],
    };

    private static void WaitUntil(Func<bool> condition, string failureMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (condition()) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException(failureMessage);
    }

    [Fact]
    public void FiresOnceForEachResultPublished()
    {
        int notifications = 0;
        using var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneBlock),
            new SynchronousThreadMarshaller(),
            onResultAvailable: () => Interlocked.Increment(ref notifications));

        Assert.True(worker.Submit(Request(page: 0)));
        Assert.True(worker.Submit(Request(page: 1)));

        WaitUntil(() => Volatile.Read(ref notifications) >= 2,
            "onResultAvailable did not fire for both submitted pages");

        // Exactly once each — not a speculative fire, not a double-fire from both stage paths.
        Assert.Equal(2, notifications);

        // The results are still sitting in the channel: the notification precedes draining.
        Assert.NotNull(worker.Poll());
        Assert.NotNull(worker.Poll());
        Assert.Null(worker.Poll());
    }

    [Fact]
    public void DoesNotFireForARequestThatFailedAnalysis()
    {
        int notifications = 0;
        using var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(() => throw new InvalidOperationException("synthetic failure")),
            new SynchronousThreadMarshaller(),
            onResultAvailable: () => Interlocked.Increment(ref notifications));

        Assert.True(worker.Submit(Request()));

        WaitUntil(() => worker.IsIdle, "worker never released the failed request's in-flight key");

        // No result was ever published, so the callback must never have run.
        Assert.Equal(0, Volatile.Read(ref notifications));
        Assert.Null(worker.Poll());
    }

    [Fact]
    public void DoesNotFireForAnAbandonedRequest()
    {
        int notifications = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneBlock),
            new SynchronousThreadMarshaller(),
            onResultAvailable: () => Interlocked.Increment(ref notifications));

        // Cancelled at submission time — Submit itself refuses it.
        Assert.False(worker.Submit(Request(cancellation: cts.Token)));
        Assert.Equal(0, Volatile.Read(ref notifications));
    }

    [Fact]
    public void OmittingTheCallbackBehavesExactlyAsBefore()
    {
        // No onResultAvailable supplied — must not throw and must behave like the pre-#118 worker.
        using var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneBlock),
            new SynchronousThreadMarshaller());

        Assert.True(worker.Submit(Request()));

        AnalysisResult? result = null;
        WaitUntil(() => (result = worker.Poll()) is not null, "worker produced no result");
        Assert.NotNull(result);
    }

    /// <summary>Captures posted actions instead of running them, so a test can control exactly
    /// when a marshalled notification is delivered relative to <see cref="AnalysisWorker.Dispose"/>.</summary>
    private sealed class CapturingThreadMarshaller : IThreadMarshaller
    {
        private readonly List<Action> _posted = [];
        public void Post(Action action) { lock (_posted) _posted.Add(action); }
        public int Count { get { lock (_posted) return _posted.Count; } }
        public Action Take() { lock (_posted) { var a = _posted[0]; _posted.RemoveAt(0); return a; } }
    }

    [Fact]
    public void InvokingAStaleNotificationAfterDisposeIsANoOp()
    {
        int notifications = 0;
        var marshaller = new CapturingThreadMarshaller();
        var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneBlock),
            marshaller,
            onResultAvailable: () => Interlocked.Increment(ref notifications));

        Assert.True(worker.Submit(Request()));

        // The worker thread has posted the notification but nothing has run it yet — exactly the
        // race the constraint in issue #118 describes: Dispose can land while it's in flight.
        WaitUntil(() => marshaller.Count >= 1, "worker never posted a result-available notification");
        var stalePost = marshaller.Take();

        worker.Dispose();

        // Delivering the stale post now must not crash and must not invoke the callback.
        var exception = Record.Exception(stalePost);
        Assert.Null(exception);
        Assert.Equal(0, Volatile.Read(ref notifications));
    }
}
