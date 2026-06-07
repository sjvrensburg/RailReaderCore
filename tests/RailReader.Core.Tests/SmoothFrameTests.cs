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
    public void SmoothlyFrameBlock_AutoFitZoom_LandsOnRailFrame()
    {
        var state = OpenWithAnalysis();

        // Frame the lower text block (index 2): a top block can't be vertically
        // centred at the sub-threshold auto-fit zoom because ClampCamera pins the
        // page top, so its landing wouldn't equal the (unclamped) rail frame.
        Assert.True(_controller.SmoothlyFrameBlock(2)); // null targetZoom → auto-fit
        Settle();

        Assert.True(state.Camera.Zoom > 1.0); // zoomed in to fit the block
        var (ex, ey) = state.Rail.ComputeSnapTarget(state.Camera.Zoom, Vw, Vh);
        Assert.Equal(ex, state.Camera.OffsetX, precision: 1);
        Assert.Equal(ey, state.Camera.OffsetY, precision: 1);
    }

    [Fact]
    public void SmoothlyFrameBlock_NonNavigableBlock_ReturnsFalse()
    {
        OpenWithAnalysis();
        Assert.False(_controller.SmoothlyFrameBlock(1)); // the Figure
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
}
