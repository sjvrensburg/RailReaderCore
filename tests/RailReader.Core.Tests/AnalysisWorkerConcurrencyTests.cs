using System.Collections.Concurrent;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #167: with <see cref="SynchronousThreadMarshaller"/> (headless hosts, tests, CLI),
/// marshalled callbacks run inline on whatever thread posted them — analysis-prep pool threads,
/// worker stage threads — so the "UI thread" serialization a desktop host gets from its real
/// marshaller does not exist. State those callbacks touch (the worker's in-flight set above all)
/// must survive genuine concurrency, and posted callbacks must not overlap each other. The
/// pre-fix failure mode was a corrupted-HashSet <see cref="InvalidOperationException"/> inside
/// <see cref="AnalysisWorker.Submit"/>, swallowed by the prep task as "Failed to prepare
/// analysis input", which silently cleared the view's pending rail setup with no analysis.
/// </summary>
public class AnalysisWorkerConcurrencyTests
{
    private static readonly byte[] Rgb = new byte[800 * 800 * 3];

    private static AnalysisRequest Request(int page) =>
        new("/tmp/doc.pdf", page, Rgb, 800, 800, 400d, 400d, null, new AnalysisParams());

    private static PageAnalysis OneBlock() => new()
    {
        Blocks = [new LayoutBlock { BBox = new BBox(0f, 0f, 400f, 400f), Role = BlockRole.Text }],
    };

    [Fact]
    public async Task SubmitAndPoll_FromManyThreadsConcurrently_NoCorruptionAndAllResultsArrive()
    {
        const int pageCount = 200;
        using var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneBlock),
            new SynchronousThreadMarshaller());

        var failures = new ConcurrentQueue<Exception>();
        int accepted = 0, collected = 0;
        using var startGate = new ManualResetEventSlim(false);

        // Submitters mimic analysis-prep tasks whose marshalled Submit runs inline on pool
        // threads; the poller mimics the host thread draining results at the same time.
        var poller = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref collected) < pageCount && sw.ElapsedMilliseconds < 30000)
                {
                    while (worker.Poll() is not null) Interlocked.Increment(ref collected);
                    _ = worker.IsInFlight("/tmp/doc.pdf", 0, new AnalysisParams());
                    _ = worker.IsIdle;
                    Thread.Yield();
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        });

        var submitters = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            try
            {
                startGate.Wait();
                for (int page = t; page < pageCount; page += 8)
                    if (worker.Submit(Request(page))) Interlocked.Increment(ref accepted);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();

        startGate.Set();
        await Task.WhenAll(submitters);
        // Let the poller finish (it self-caps at 30 s) before asserting/disposing, so a
        // timeout failure is deterministic rather than racing a live poller.
        await Task.WhenAny(poller, Task.Delay(TimeSpan.FromSeconds(35)));
        await Task.WhenAny(poller, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Empty(failures);
        Assert.Equal(pageCount, Volatile.Read(ref accepted));   // distinct keys: none suppressed
        Assert.Equal(pageCount, Volatile.Read(ref collected));  // every request produced a result
        Assert.True(worker.IsIdle);                             // no in-flight key stranded
    }

    [Fact]
    public void Submit_SameKeyWhileInFlight_IsSuppressedUntilResultPolled()
    {
        // The in-flight key is single-owner: a duplicate (file, page, params) is refused while
        // the first request runs, and the key frees only when its result is polled — a stale
        // release can therefore never delete a successor's key (review of the #167 fix).
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var worker = new AnalysisWorker(
            FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return OneBlock();
            }),
            new SynchronousThreadMarshaller());

        try
        {
            Assert.True(worker.Submit(Request(page: 3)));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "analyzer never entered");

            Assert.False(worker.Submit(Request(page: 3)));      // same key still in flight
            Assert.True(worker.IsInFlight("/tmp/doc.pdf", 3, new AnalysisParams()));

            release.Set();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AnalysisResult? result = null;
            while ((result = worker.Poll()) is null && sw.ElapsedMilliseconds < 10000)
                Thread.Sleep(5);
            Assert.NotNull(result);
            Assert.False(worker.IsInFlight("/tmp/doc.pdf", 3, new AnalysisParams())); // Poll freed it

            Assert.True(worker.Submit(Request(page: 3)));       // successor admitted under same key
            result = null;
            sw.Restart();
            while ((result = worker.Poll()) is null && sw.ElapsedMilliseconds < 10000)
                Thread.Sleep(5);
            Assert.NotNull(result);
            Assert.True(worker.IsIdle);
        }
        finally
        {
            // Never dispose the events under a layout thread still parked in release.Wait
            // when an assert above throws before release.Set().
            release.Set();
        }
    }

    [Fact]
    public void SynchronousMarshaller_PostsFromManyThreads_NeverOverlap()
    {
        var marshaller = new SynchronousThreadMarshaller();
        const int posts = 200;
        int running = 0, maxRunning = 0, executed = 0;

        Parallel.For(0, posts, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            marshaller.Post(() =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while ((seen = Volatile.Read(ref maxRunning)) < now)
                    Interlocked.CompareExchange(ref maxRunning, now, seen);
                Thread.SpinWait(5000); // widen the window in which an overlap would show
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref executed);
            });
        });

        Assert.Equal(posts, Volatile.Read(ref executed));
        Assert.Equal(1, Volatile.Read(ref maxRunning)); // a stand-in UI thread serializes its posts
    }
}
