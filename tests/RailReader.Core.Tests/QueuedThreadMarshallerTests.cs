using System.Collections.Concurrent;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #174: <see cref="QueuedThreadMarshaller"/> gives a headless host a real UI-thread guarantee.
/// Callbacks run only when the owning thread pumps, never inline on the thread that posted them.
/// </summary>
public class QueuedThreadMarshallerTests
{
    [Fact]
    public void Post_NeverRunsInline_OnlyPumpRunsCallbacks()
    {
        var marshaller = new QueuedThreadMarshaller();
        int ran = 0;

        marshaller.Post(() => ran++);
        marshaller.Post(() => ran++);

        Assert.Equal(0, ran);
        Assert.Equal(2, marshaller.Pending);
        Assert.Equal(2, marshaller.Pump());
        Assert.Equal(2, ran);
        Assert.Equal(0, marshaller.Pending);
        Assert.Equal(0, marshaller.Pump());
    }

    [Fact]
    public void PostsFromManyThreads_RunOnlyOnTheOwnerThread()
    {
        var marshaller = new QueuedThreadMarshaller();
        int owner = Environment.CurrentManagedThreadId;
        var threads = new ConcurrentBag<int>();

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ => marshaller.Post(() => threads.Add(Environment.CurrentManagedThreadId)));

        Assert.Empty(threads);                       // posting ran nothing
        Assert.Equal(200, marshaller.Pump());
        Assert.Equal(200, threads.Count);
        Assert.All(threads, t => Assert.Equal(owner, t));
    }

    [Fact]
    public void Pump_RunsCallbacksInPostOrder()
    {
        var marshaller = new QueuedThreadMarshaller();
        var order = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            int n = i;
            marshaller.Post(() => order.Add(n));
        }

        marshaller.Pump();

        Assert.Equal(Enumerable.Range(0, 10), order);
    }

    [Fact]
    public void CallbackThatPosts_RunsInTheSamePump()
    {
        var marshaller = new QueuedThreadMarshaller();
        var order = new List<string>();
        marshaller.Post(() =>
        {
            order.Add("outer");
            marshaller.Post(() => order.Add("inner"));
        });

        Assert.Equal(2, marshaller.Pump());          // the count includes the nested post
        Assert.Equal(["outer", "inner"], order);
        Assert.Equal(0, marshaller.Pending);
    }

    [Fact]
    public void PumpAndAssertUIThread_ThrowOffTheOwnerThread()
    {
        var marshaller = new QueuedThreadMarshaller();
        marshaller.AssertUIThread();                 // the owner may

        var pump = Record.Exception(() => Task.Run(() => marshaller.Pump()).GetAwaiter().GetResult());
        var assert = Record.Exception(() => Task.Run(marshaller.AssertUIThread).GetAwaiter().GetResult());

        Assert.IsType<InvalidOperationException>(pump);
        Assert.IsType<InvalidOperationException>(assert);
    }

    [Fact]
    public void Controller_OnQueuedMarshaller_SeatsRailWithEveryViewMutationOnTheOwnerThread()
    {
        // End to end: open a document, let the prep task and the worker run on their own threads,
        // and drive frames from this one. Each view state change must happen here. Viewport's
        // SetField asserts the UI thread, and this marshaller throws when that assert fails, so an
        // off-thread write would also have thrown out of the pump.
        var marshaller = new QueuedThreadMarshaller();
        var appConfig = new AppConfig();
        using var controller = new DocumentController(appConfig.ToCoreSettings(), appConfig,
            AnnotationService.Default, marshaller, TestFixtures.CreatePdfFactory());
        controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(() => new PageAnalysis
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
            }));
        controller.SetViewportSize(800, 600);

        int owner = Environment.CurrentManagedThreadId;
        var mutationThreads = new ConcurrentBag<int>();
        var doc = controller.CreateDocument(TestFixtures.GetTestPdfPath());
        doc.Primary.StateChanged += _ => mutationThreads.Add(Environment.CurrentManagedThreadId);
        doc.LoadPageBitmap();
        controller.AddDocument(doc);

        Assert.True(TestWait.Until(() => !doc.Primary.PendingRailSetup && doc.Primary.Rail.HasAnalysis,
            marshaller, () => controller.TickViewport(doc.Primary, 1 / 60.0)),
            "the rail was never seated");
        Assert.NotEmpty(mutationThreads);
        Assert.All(mutationThreads, t => Assert.Equal(owner, t));
    }
}
