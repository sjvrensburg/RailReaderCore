using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Full-state navigation history (issue #143), host-controlled link clicks (issue #142) and
/// <see cref="DocumentController.SmoothlyFrameBlock"/>'s horizontal focus hint (issue #144).
/// </summary>
public class NavigationHistoryLinkTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly AppConfig _config;
    private readonly DocumentController _controller;

    private const double Vw = 800, Vh = 600;

    public NavigationHistoryLinkTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        _config = new AppConfig
        {
            SnapDurationMs = 1,    // near-instant completion snap
            PixelSnapping = false, // exact assertions
        };
        _controller = new DocumentController(_config.ToCoreSettings(), _config,
            AnnotationService.Default, new SynchronousThreadMarshaller(),
            TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    /// <summary>Two navigable text blocks (page indices 0 and 2) around a figure (index 1); three
    /// full-width lines each.</summary>
    private static PageAnalysis SampleAnalysis()
    {
        var a = new PageAnalysis { PageWidth = 612, PageHeight = 792 };
        LayoutBlock Block(float y, BlockRole role)
        {
            var b = new LayoutBlock
            {
                BBox = new BBox(72, y, 468, 48), Role = role, Confidence = 0.9f, Order = a.Blocks.Count,
            };
            for (int i = 0; i < 3; i++) b.Lines.Add(new LineInfo(y + 8 + i * 16, 16, 72, 468));
            return b;
        }
        a.Blocks.Add(Block(72, BlockRole.Text));
        a.Blocks.Add(Block(360, BlockRole.Figure));
        a.Blocks.Add(Block(560, BlockRole.Text));
        return a;
    }

    private DocumentModel Open(bool withAnalysis = true)
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(Vw, Vh);
        if (withAnalysis)
            for (int p = 0; p < state.PageCount; p++)
                state.SetAnalysis(p, state.DefaultAnalysisParams, SampleAnalysis());
        return state;
    }

    private void Settle(int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            _controller.Tick(1.0 / 60.0);
            Thread.Sleep(5);
        } while (_controller.FocusedViewport!.IsAnimating && sw.ElapsedMilliseconds < timeoutMs);
        _controller.Tick(1.0 / 60.0);
    }

    // --- #143: which navigation records history ---

    [Fact]
    public void GoToPage_RecordsHistory_OnlyWhenThePageChanges()
    {
        var state = Open(withAnalysis: false);
        var vp = state.Primary;

        _controller.GoToPage(0);                       // same page: nothing to go back to
        Assert.Equal(0, vp.BackStackCount);

        _controller.GoToPage(2);
        Assert.Equal(1, vp.BackStackCount);
        Assert.Equal(0, vp.PeekBack()!.Value.Page);
    }

    [Fact]
    public void GoToPage_RecordHistoryFalse_DoesNotPush()
    {
        var state = Open(withAnalysis: false);
        _controller.GoToPage(1, recordHistory: false);
        Assert.Equal(1, state.CurrentPage);
        Assert.Equal(0, state.Primary.BackStackCount);
    }

    [Fact]
    public void PushHistory_ClearsForward_CoalescesDuplicates_AndCapsDepth()
    {
        var state = Open(withAnalysis: false);
        var vp = state.Primary;
        int changes = 0;
        vp.HistoryChanged += () => changes++;

        var e = new HistoryEntry(1, 2.0, -10, -20);
        vp.PushHistory(e);
        vp.PushHistory(e);                             // identical to the top: not pushed again
        Assert.Equal(1, vp.BackStackCount);
        Assert.Equal(1, changes);

        vp.PopBack(HistoryEntry.ForPage(0));
        Assert.Equal(1, vp.ForwardStackCount);
        vp.PushHistory(HistoryEntry.ForPage(2));       // a new branch drops forward history
        Assert.Equal(0, vp.ForwardStackCount);

        for (int i = 0; i < Viewport.MaxHistoryDepth + 10; i++)
            vp.PushHistory(new HistoryEntry(i % 3, i));
        Assert.Equal(Viewport.MaxHistoryDepth, vp.BackStackCount);
    }

    [Fact]
    public void SearchMatchNavigation_RecordsOnlyPageChanges()
    {
        var state = Open(withAnalysis: false);
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(10, 10, 50, 20)]),
            new(0, 30, 4, [new RectF(10, 40, 50, 50)]),
            new(2, 0, 4, [new RectF(10, 10, 50, 20)]),
        };
        _controller.Search.FinalizeSearch(state, matches);   // lands on match 0 (this page)
        Assert.Equal(0, state.Primary.BackStackCount);

        _controller.Search.NextMatch();                      // same page: not a jump
        Assert.Equal(0, state.Primary.BackStackCount);

        _controller.Search.NextMatch();                      // page 2
        Assert.Equal(2, state.CurrentPage);
        Assert.Equal(1, state.Primary.BackStackCount);
        Assert.Equal(0, state.PeekBack());
    }

    [Fact]
    public void NavigateToRole_RecordsHistory()
    {
        var state = Open();
        Assert.True(_controller.SmoothlyFrameBlock(0, targetZoom: 5.0));
        Settle();
        Assert.True(state.Primary.Rail.Active);

        Assert.True(_controller.NavigateToRole(BlockRole.Text));
        Assert.Equal(1, state.Primary.BackStackCount);
        Assert.Equal(0, state.Primary.PeekBack()!.Value.RailBlock);
    }

    // --- #143: Back/Forward restore the full view state ---

    [Fact]
    public void NavigateBack_RestoresZoomCameraAndRailLine()
    {
        var state = Open();
        var vp = state.Primary;
        Assert.True(_controller.SmoothlyFrameBlock(2, targetZoom: 5.0, line: 1));
        Settle();
        Assert.True(vp.Rail.Active);
        double zoom = vp.Camera.Zoom, ox = vp.Camera.OffsetX, oy = vp.Camera.OffsetY;

        _controller.GoToPage(1);
        Assert.Equal(1, state.CurrentPage);
        _controller.FitPage();                          // move away from the magnified spot

        _controller.NavigateBack();
        Settle();

        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(zoom, vp.Camera.Zoom, 6);
        Assert.Equal(ox, vp.Camera.OffsetX, 3);
        Assert.Equal(oy, vp.Camera.OffsetY, 3);
        Assert.True(vp.Rail.Active);
        Assert.Equal(2, vp.Rail.CurrentNavigableArrayIndex);
        Assert.Equal(1, vp.Rail.CurrentLine);

        // And forward returns to the fitted page-1 view.
        _controller.NavigateForward();
        Assert.Equal(1, state.CurrentPage);
        Assert.False(vp.Rail.Active);
    }

    [Fact]
    public void NavigateBack_PageOnlyEntry_LandsOnPage()
    {
        var state = Open(withAnalysis: false);
        state.Primary.PushHistory(2);                  // legacy page-only push
        _controller.NavigateBack();
        Assert.Equal(2, state.CurrentPage);
    }

    [Fact]
    public void NavigateBack_RestoreWhileAnalysisPending_SeatsOnArrival()
    {
        _controller.InitializeWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(SampleAnalysis));
        var state = Open(withAnalysis: false);
        var vp = state.Primary;

        // Magnify onto block 2 of page 0 (cached), and remember that view as if it were page 2's.
        state.SetAnalysis(0, state.DefaultAnalysisParams, SampleAnalysis());
        state.ReapplyNavigableRoles(vp, _controller.Config.NavigableRoles);
        Assert.True(_controller.SmoothlyFrameBlock(2, targetZoom: 5.0, line: 1));
        Settle();
        // (Built by hand: the worker's own analysis of page 0 may still be in flight, which
        // CaptureHistoryEntry rightly reports as "rail not seated".)
        var entry = new HistoryEntry(2, vp.Camera.Zoom, vp.Camera.OffsetX, vp.Camera.OffsetY,
            RailBlock: 2, RailLine: 1);

        _controller.GoToPage(1, recordHistory: false);
        vp.PushHistory(entry);

        // Page 2 was never analysed: the restore lands with the analysis pending and the rail cleared,
        // not engaged on page 1's stale geometry.
        _controller.NavigateBack();
        Assert.Equal(2, state.CurrentPage);
        Assert.True(vp.PendingRailSetup);
        Assert.False(vp.Rail.Active);
        Assert.Equal(5.0, vp.Camera.Zoom, 6);

        var sw = Stopwatch.StartNew();
        while (vp.PendingRailSetup && sw.ElapsedMilliseconds < 5000)
        {
            _controller.PollAnalysisResults();
            Thread.Sleep(10);
        }

        Assert.False(vp.PendingRailSetup);
        Assert.True(vp.Rail.Active);
        Assert.Equal(2, vp.Rail.CurrentNavigableArrayIndex); // the remembered block, not the nearest one
        Assert.Null(vp.PendingHistorySeat);
    }

    // --- #142: link click behaviour ---

    private static PdfLink LinkTo(int page, float? pageY = null) => new()
    {
        Rect = new RectF(50, 50, 200, 200),
        Destination = new PageDestination { PageIndex = page, PageY = pageY },
    };

    [Fact]
    public void HandleClick_FollowLinksFalse_IgnoresTheLink()
    {
        var state = Open(withAnalysis: false);
        state.SetLinks(0, [LinkTo(2)]);
        double cx = 100 * state.Camera.Zoom + state.Camera.OffsetX;
        double cy = 100 * state.Camera.Zoom + state.Camera.OffsetY;

        var (_, link) = _controller.HandleClick(cx, cy, followLinks: false);

        Assert.Null(link);
        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(0, state.Primary.BackStackCount);
    }

    [Fact]
    public void FollowLink_PageDestination_NavigatesAndRecords()
    {
        var state = Open(withAnalysis: false);
        Assert.True(_controller.FollowLink(new PageDestination { PageIndex = 2 }));
        Assert.Equal(2, state.CurrentPage);
        Assert.Equal(1, state.Primary.BackStackCount);
        Assert.Equal(0, state.PeekBack());
    }

    [Fact]
    public void FollowLink_Uri_ReturnsFalse()
    {
        var state = Open(withAnalysis: false);
        Assert.False(_controller.FollowLink(new UriDestination { Uri = "https://example.com" }));
        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(0, state.Primary.BackStackCount);
    }

    [Fact]
    public void FollowLink_ConfinedView_ReturnsFalse()
    {
        var state = Open();
        var b = SampleAnalysis().Blocks[0].BBox;
        state.Primary.Focus = new FocusBlock(0, 0, b);
        Assert.False(_controller.FollowLink(new PageDestination { PageIndex = 2 }));
        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(0, state.Primary.BackStackCount);
    }

    [Fact]
    public void ResolveDestinationPreview_ReturnsContainingBlock_WhenAnalysisCached()
    {
        Open();
        var preview = _controller.ResolveDestinationPreview(
            new PageDestination { PageIndex = 1, PageX = 100, PageY = 570 });
        Assert.NotNull(preview);
        Assert.Equal(1, preview!.Value.Page);
        Assert.Equal(2, preview.Value.BlockIndex);
        Assert.Equal(560, preview.Value.Rect.Top, 3);
        Assert.Equal(608, preview.Value.Rect.Bottom, 3);
    }

    [Fact]
    public void ResolveDestinationPreview_FallsBackToBand_WithoutAnalysis()
    {
        var state = Open(withAnalysis: false);
        var preview = _controller.ResolveDestinationPreview(
            new PageDestination { PageIndex = 2, PageY = 300 }, bandHeight: 120);
        Assert.NotNull(preview);
        Assert.Null(preview!.Value.BlockIndex);
        Assert.Equal(300, preview.Value.Rect.Top, 3);
        Assert.Equal(420, preview.Value.Rect.Bottom, 3);
        Assert.Equal(0, preview.Value.Rect.Left, 3);

        var top = _controller.ResolveDestinationPreview(new PageDestination { PageIndex = 2 });
        Assert.Equal(0, top!.Value.Rect.Top, 3);

        Assert.Null(_controller.ResolveDestinationPreview(new PageDestination { PageIndex = state.PageCount }));
    }

    // --- #144: SmoothlyFrameBlock focusPageX ---

    [Fact]
    public void SmoothlyFrameBlock_FocusPageX_KeepsThePointCentred()
    {
        var state = Open();
        var vp = state.Primary;
        const double z = 5.0, fx = 400;                // line spans 72…540 → 2340 px wide at 5×

        Assert.True(_controller.SmoothlyFrameBlock(0, targetZoom: z, focusPageX: fx));
        Settle();

        Assert.True(vp.Rail.Active);
        Assert.Equal(z, vp.Camera.Zoom, 6);
        Assert.Equal(Vw / 2.0 - fx * z, vp.Camera.OffsetX, 1);
    }

    [Fact]
    public void SmoothlyFrameBlock_FocusPageX_ClampsToTheLineEnd()
    {
        var state = Open();
        var vp = state.Primary;
        const double z = 5.0;

        Assert.True(_controller.SmoothlyFrameBlock(0, targetZoom: z, focusPageX: 600)); // past the line end
        Settle();

        // Aimed at the line end (540), which centring would put mid-window; the rail's scroll range
        // stops it at its right boundary instead, with the line end still on screen.
        var (expected, _) = vp.Rail.ComputeSnapTarget(z, Vw, Vh, 540);
        Assert.Equal(expected, vp.Camera.OffsetX, 1);
        Assert.True(expected > Vw / 2.0 - 540 * z);
        double lineEndOnScreen = 540 * z + vp.Camera.OffsetX;
        Assert.InRange(lineEndOnScreen, Vw / 2.0, Vw);
    }

    [Fact]
    public void SmoothlyFrameBlock_WithoutHint_LandsAtLineStart()
    {
        var state = Open();
        var vp = state.Primary;
        Assert.True(_controller.SmoothlyFrameBlock(0, targetZoom: 5.0));
        Settle();
        var (ox, _) = vp.Rail.ComputeSnapTarget(5.0, Vw, Vh);
        Assert.Equal(ox, vp.Camera.OffsetX, 1);
        // Line start on the left half of the window: the default left-aligned framing.
        Assert.InRange(72 * 5.0 + vp.Camera.OffsetX, 0, Vw / 2.0);
    }
}
