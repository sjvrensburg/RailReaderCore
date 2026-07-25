using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class BackgroundAnalysisAndCacheTests
{
    // Phase 3: TryGetNext now takes an isAnalysed predicate (no ambient cache dict).
    // An empty cache means nothing is analysed yet.
    private static readonly Func<int, bool> NothingAnalysed = _ => false;

    private static List<int> DrainAll(BackgroundAnalysisQueue queue)
    {
        var pages = new List<int>();
        while (queue.TryGetNext(NothingAnalysed, _ => false) is { } page)
            pages.Add(page);
        return pages;
    }

    [Fact]
    public void Queue_IsExhausted_BeforeReset()
    {
        var queue = new BackgroundAnalysisQueue(pageCount: 100, windowPages: 12);
        Assert.True(queue.IsExhausted);
        Assert.Null(queue.TryGetNext(NothingAnalysed, _ => false));
    }

    [Fact]
    public void Queue_BoundsSweepToWindowAroundOrigin()
    {
        var queue = new BackgroundAnalysisQueue(pageCount: 100, windowPages: 5);
        queue.Reset(currentPage: 50);

        var pages = DrainAll(queue);

        // Window is ±5 around page 50 → [45, 55] inclusive, 11 pages.
        Assert.Equal(11, pages.Count);
        Assert.All(pages, p => Assert.InRange(p, 45, 55));
        Assert.Equal(Enumerable.Range(45, 11).OrderBy(x => x), pages.OrderBy(x => x));
        Assert.True(queue.IsExhausted);
    }

    [Fact]
    public void Queue_ClampsWindowToDocumentEdges()
    {
        var queue = new BackgroundAnalysisQueue(pageCount: 8, windowPages: 5);
        queue.Reset(currentPage: 1);

        var pages = DrainAll(queue).OrderBy(x => x).ToList();

        // ±5 around page 1 clamps to [0, 6] within an 8-page document.
        Assert.Equal(Enumerable.Range(0, 7), pages);
    }

    [Fact]
    public void Queue_ServesForwardBeforeBackward()
    {
        var queue = new BackgroundAnalysisQueue(pageCount: 100, windowPages: 3);
        queue.Reset(currentPage: 50);

        // Forward (50..53) is served before backward (49..47).
        Assert.Equal([50, 51, 52, 53, 49, 48, 47], DrainAll(queue));
    }

    [Fact]
    public void Queue_NonPositiveWindow_CoversWholeDocument()
    {
        var queue = new BackgroundAnalysisQueue(pageCount: 10, windowPages: 0);
        queue.Reset(currentPage: 4);

        var pages = DrainAll(queue).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(0, 10), pages);
    }

    [Fact]
    public void Queue_ResetRecentresWindow()
    {
        var queue = new BackgroundAnalysisQueue(pageCount: 100, windowPages: 2);
        queue.Reset(10);
        Assert.All(DrainAll(queue), p => Assert.InRange(p, 8, 12));

        queue.Reset(80);
        Assert.All(DrainAll(queue), p => Assert.InRange(p, 78, 82));
    }

    private static DocumentModel NewDoc(CoreSettings settings)
    {
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        return new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), settings, marshaller);
    }

    private static PageText FakeText(int page) =>
        new($"page {page}", [new CharBox(0, 0, 0, 1, 1)]);

    [Fact]
    public void Caches_EvictTextAndLinksOutsideRadius_OnPageChange()
    {
        var state = NewDoc(new CoreSettings { PageCacheRadius = 2 });

        for (int p = 0; p <= 10; p++)
        {
            state.SetText(p, FakeText(p));
            state.SetLinks(p, []);
        }

        // Moving to page 5 keeps only [3, 7]; the rest are dropped.
        state.CurrentPage = 5;

        Assert.Equal(Enumerable.Range(3, 5), state.TextCache.Keys.OrderBy(x => x));
        Assert.Equal(Enumerable.Range(3, 5), state.LinkCache.Keys.OrderBy(x => x));

        state.Dispose();
    }

    [Fact]
    public void Caches_AnalysisGeometryIsNotEvicted()
    {
        var state = NewDoc(new CoreSettings { PageCacheRadius = 1 });

        for (int p = 0; p <= 6; p++)
            state.SetAnalysis(p, state.DefaultAnalysisParams, new PageAnalysis());

        state.CurrentPage = 3;

        // Analysis cache is intentionally retained (expensive to recompute).
        Assert.Equal(Enumerable.Range(0, 7), state.AnalysedPages.OrderBy(x => x));

        state.Dispose();
    }

    [Fact]
    public void Caches_OcrTextIsPinnedAgainstEviction()
    {
        var state = NewDoc(new CoreSettings { PageCacheRadius = 1 });

        state.SetText(0, new PageText("", []));   // page 0 has no text layer of its own
        state.SetOcrText(0, FakeText(0));         // …until OCR recovers one

        state.CurrentPage = 5;

        // Nothing re-runs OCR: it happens inside an analysis request, and an analysed page is
        // never resubmitted. Evicting the text would blank the page permanently for search,
        // export and reading position.
        Assert.True(state.TextCache.ContainsKey(0));

        state.Dispose();
    }

    [Fact]
    public void Caches_EmptyReExtraction_DoesNotOverwriteOcrText()
    {
        var state = NewDoc(new CoreSettings());
        state.SetOcrText(3, FakeText(3));

        // A re-prep of the same scanned page (new analysis params, a second pane) extracts the
        // same nothing it did the first time; it must not evict what OCR recovered.
        state.SetText(3, new PageText("", []));

        Assert.Equal("page 3", state.TextCache[3].Text);

        state.Dispose();
    }

    [Fact]
    public void Caches_RealTextLayerStillWinsOverOcrText()
    {
        var state = NewDoc(new CoreSettings());
        state.SetOcrText(3, FakeText(3));

        state.SetText(3, new PageText("real", [new CharBox(0, 0, 0, 1, 1)]));

        Assert.Equal("real", state.TextCache[3].Text);

        state.Dispose();
    }

    [Fact]
    public void InvalidateOcrDependentAnalysis_TouchesOnlyPagesWithNoTextLayer()
    {
        var state = NewDoc(new CoreSettings());
        state.SetText(0, FakeText(0));                 // digital page
        state.SetText(1, new PageText("", []));        // scanned page…
        state.SetOcrText(1, FakeText(1));              // …recovered by OCR
        state.SetAnalysis(0, state.DefaultAnalysisParams, new PageAnalysis());
        state.SetAnalysis(1, state.DefaultAnalysisParams, new PageAnalysis());

        var affected = state.InvalidateOcrDependentAnalysis();

        // Only the scanned page's answer depends on the OCR mode, so only it is dropped —
        // toggling OCR on a digital document costs nothing.
        Assert.Equal([1], affected);
        Assert.True(state.IsPageAnalysed(0));
        Assert.False(state.IsPageAnalysed(1));
        Assert.True(state.TextCache.ContainsKey(0));
        Assert.False(state.TextCache.ContainsKey(1));

        state.Dispose();
    }

    [Fact]
    public void Caches_NonPositiveRadius_DisablesEviction()
    {
        var state = NewDoc(new CoreSettings { PageCacheRadius = 0 });

        for (int p = 0; p <= 6; p++)
            state.SetText(p, FakeText(p));

        state.CurrentPage = 3;

        Assert.Equal(Enumerable.Range(0, 7), state.TextCache.Keys.OrderBy(x => x));

        state.Dispose();
    }
}
