using System;
using System.Diagnostics;
using System.Threading;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// End-to-end coverage for the smooth "frame a detected block" primitives
/// (<see cref="DocumentController.SmoothlyFrameBlock"/> /
/// <see cref="DocumentController.SmoothlyFrameRole"/> /
/// <see cref="DocumentController.AnimateCameraTo"/>): the eased zoom+pan animation
/// must land on rail's exact frame for the targeted block.
/// </summary>
public class SmoothFrameTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly AppConfig _config;
    private readonly DocumentController _controller;

    private const double Vw = 800, Vh = 600;

    public SmoothFrameTests()
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

    /// <summary>
    /// Page-0 analysis: two navigable text blocks (page indices 0 and 2) separated
    /// by a non-navigable figure (index 1), each well apart so they form distinct
    /// chunks.
    /// </summary>
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
        a.Blocks.Add(Block(72, BlockRole.Text));    // index 0 (navigable)
        a.Blocks.Add(Block(360, BlockRole.Figure)); // index 1 (NOT navigable)
        a.Blocks.Add(Block(560, BlockRole.Text));   // index 2 (navigable)
        return a;
    }

    private DocumentState OpenWithAnalysis()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap(); // sets real PageWidth/Height so ClampCamera is correct
        _controller.AddDocument(state);
        _controller.SetViewportSize(Vw, Vh);
        state.SetAnalysis(state.CurrentPage, SampleAnalysis());
        return state;
    }

    /// <summary>Drive the real-time zoom + snap animations to completion.</summary>
    private void Settle(int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            _controller.Tick(1.0 / 60.0);
            Thread.Sleep(5);
        } while (_controller.IsAnimating && sw.ElapsedMilliseconds < timeoutMs);
        _controller.Tick(1.0 / 60.0); // final frame applies any completion snap
    }

    [Fact]
    public void SmoothlyFrameBlock_FramesNavigableBlock_AtExactRailFrame()
    {
        var state = OpenWithAnalysis();

        Assert.True(_controller.SmoothlyFrameBlock(2, targetZoom: 5.0));
        Settle();

        Assert.Equal(5.0, state.Camera.Zoom, precision: 2);
        Assert.Equal(2, state.Rail.CurrentNavigableArrayIndex); // activation handoff kept our block
        Assert.True(state.Rail.Active);                          // above threshold → rail engaged

        // Landed exactly on rail's frame for the seated block at the final zoom.
        var (ex, ey) = state.Rail.ComputeSnapTarget(state.Camera.Zoom, Vw, Vh);
        Assert.Equal(ex, state.Camera.OffsetX, precision: 1);
        Assert.Equal(ey, state.Camera.OffsetY, precision: 1);
    }

    [Fact]
    public void SmoothlyFrameBlock_AutoFitZoom_FloorsAtRailThresholdAndFrames()
    {
        var state = OpenWithAnalysis();

        // Block 0 is a wide block at the page TOP. Its raw fit zoom (~1.47) is below the
        // rail threshold; without the floor, rail would stay inactive and ClampCamera
        // would pin the page top, leaving the block unframed. The floor (fix) keeps it at
        // >= threshold so rail engages and the completion snap frames the block exactly.
        Assert.True(_controller.SmoothlyFrameBlock(0)); // null targetZoom → auto-fit
        Settle();

        Assert.True(state.Rail.Active);                              // floored to >= threshold
        Assert.True(state.Camera.Zoom >= _config.ToCoreSettings().RailZoomThreshold);
        Assert.Equal(0, state.Rail.CurrentNavigableArrayIndex);
        var (ex, ey) = state.Rail.ComputeSnapTarget(state.Camera.Zoom, Vw, Vh);
        Assert.Equal(ex, state.Camera.OffsetX, precision: 1);
        Assert.Equal(ey, state.Camera.OffsetY, precision: 1);
    }

    [Fact]
    public void SmoothlyFrameBlock_NonNavigableBlock_CentersGeometrically()
    {
        var state = OpenWithAnalysis();

        // Block 1 is a Figure (non-navigable): rail can't seat it, so framing falls back to a
        // geometric centred frame — eased zoom-to-fit, centred, with rail OFF (no snap hijack).
        Assert.True(_controller.SmoothlyFrameBlock(1));
        Settle();

        Assert.False(state.Rail.Active); // pure camera move never engages rail

        var box = SampleAnalysis().Blocks[1].BBox;
        double expectedZoom = state.ComputeBlockFitZoom(box, Vw, Vh);
        Assert.Equal(expectedZoom, state.Camera.Zoom, precision: 2);

        // The figure's centre lands at the viewport centre.
        AssertBlockCentredInViewport(state, box);
    }

    /// <summary>Assert the block's page-space centre maps to the viewport centre under the
    /// current camera (post-settle), i.e. screen = offset + pageCentre * zoom == (Vw/2, Vh/2).</summary>
    private static void AssertBlockCentredInViewport(DocumentState state, BBox box)
    {
        double cx = box.X + box.W / 2.0, cy = box.Y + box.H / 2.0;
        double screenX = state.Camera.OffsetX + cx * state.Camera.Zoom;
        double screenY = state.Camera.OffsetY + cy * state.Camera.Zoom;
        Assert.Equal(Vw / 2.0, screenX, precision: 0);
        Assert.Equal(Vh / 2.0, screenY, precision: 0);
    }

    [Fact]
    public void SmoothlyFrameBlock_IndexOutOfRange_ReturnsFalse()
    {
        OpenWithAnalysis();
        Assert.False(_controller.SmoothlyFrameBlock(99));
        Assert.False(_controller.SmoothlyFrameBlock(-1));
    }

    [Fact]
    public void SmoothlyFrameBlock_NoCurrentPageAnalysis_ReturnsFalse()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(Vw, Vh);
        // No analysis injected for the current page.
        Assert.False(_controller.SmoothlyFrameBlock(0));
    }

    [Fact]
    public void SmoothlyFrameRole_FramesNthOccurrenceInReadingOrder()
    {
        var state = OpenWithAnalysis();

        Assert.True(_controller.SmoothlyFrameRole(BlockRole.Text, occurrence: 1, targetZoom: 5.0));
        Settle();

        Assert.Equal(2, state.Rail.CurrentNavigableArrayIndex); // 2nd Text block is page index 2
    }

    [Fact]
    public void SmoothlyFrameRole_OutOfRangeOrMissingRole_ReturnsFalse()
    {
        OpenWithAnalysis();
        Assert.False(_controller.SmoothlyFrameRole(BlockRole.Text, occurrence: 5)); // only 2 Text blocks
        Assert.False(_controller.SmoothlyFrameRole(BlockRole.Table, occurrence: 0)); // no tables
    }

    [Fact]
    public void IsAnimating_TrueAfterFrame_FalseAfterSettle()
    {
        OpenWithAnalysis();
        _controller.SmoothlyFrameBlock(0, targetZoom: 5.0);
        Assert.True(_controller.IsAnimating);
        Settle();
        Assert.False(_controller.IsAnimating);
    }

    [Fact]
    public void GetReadingPosition_ReportsLineCountAndHorizontalFraction()
    {
        OpenWithAnalysis();
        Assert.True(_controller.SmoothlyFrameBlock(2, targetZoom: 5.0));
        Settle();

        var rp = _controller.GetReadingPosition();
        Assert.NotNull(rp);
        Assert.Equal(3, rp!.LineCount);                  // SampleAnalysis blocks have 3 lines
        Assert.InRange(rp.HorizontalFraction, 0.0, 1.0); // 0 = line start … 1 = line end
    }

    [Fact]
    public void SmoothlyFrameBlock_OverlappingBlocks_KeepsSeatedBlockThroughActivation()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(Vw, Vh);

        // Block 0 is a large text block; block 1 is a small text block that sits INSIDE
        // block 0's bbox. Both navigable. Framing block 1: its focus point also lies in
        // block 0, so the geometric activation pick (FindBlockNearPoint, first bbox hit in
        // reading order) would select block 0 without the activation pin.
        var a = new PageAnalysis { PageWidth = 612, PageHeight = 792 };
        var big = new LayoutBlock { BBox = new BBox(72, 72, 468, 500), Role = BlockRole.Text, Confidence = 0.9f, Order = 0 };
        for (int i = 0; i < 5; i++) big.Lines.Add(new LineInfo(120 + i * 80, 16, 72, 468));
        var small = new LayoutBlock { BBox = new BBox(72, 300, 200, 48), Role = BlockRole.Text, Confidence = 0.9f, Order = 1 };
        small.Lines.Add(new LineInfo(308, 16, 72, 200));
        a.Blocks.Add(big);   // page index 0
        a.Blocks.Add(small); // page index 1, inside block 0
        state.SetAnalysis(state.CurrentPage, a);

        Assert.True(_controller.SmoothlyFrameBlock(1, targetZoom: 5.0));
        Settle();

        Assert.True(state.Rail.Active);
        Assert.Equal(1, state.Rail.CurrentNavigableArrayIndex); // stayed on the seated block, not the enclosing one
    }
}
