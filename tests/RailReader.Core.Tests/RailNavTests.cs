using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class RailNavTests
{
    private readonly AppConfig _config;
    private readonly RailNav _nav;

    // Standard viewport dimensions for tests
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;
    private const double Zoom = 4.0; // above default RailZoomThreshold of 3.0

    // Block roles
    private const BlockRole TextRole = BlockRole.Text;
    private const BlockRole ImageRole = BlockRole.Figure;
    private const BlockRole HeaderRole = BlockRole.Header;

    public RailNavTests()
    {
        _config = new AppConfig
        {
            SnapDurationMs = 1, // near-instant snaps for testing
            PixelSnapping = false, // avoid rounding complications in assertions
        };
        _nav = new RailNav(_config.ToCoreSettings());
    }

    /// <summary>
    /// Creates a PageAnalysis with the given number of blocks, each containing
    /// the specified number of lines. Blocks are 468pt wide text blocks stacked
    /// vertically with 20pt gaps. Each block's lines are evenly spaced.
    /// </summary>
    private static PageAnalysis CreateAnalysis(int blockCount, int linesPerBlock, BlockRole role = TextRole)
    {
        var blocks = new List<LayoutBlock>();
        float yOffset = 72f; // top margin
        const float blockWidth = 468f;
        const float lineHeight = 16f;
        const float blockGap = 20f;
        const float xOffset = 72f;

        for (int b = 0; b < blockCount; b++)
        {
            float blockHeight = linesPerBlock * lineHeight;
            var lines = new List<LineInfo>();
            for (int l = 0; l < linesPerBlock; l++)
            {
                lines.Add(new LineInfo(yOffset + l * lineHeight, lineHeight, xOffset, blockWidth));
            }

            blocks.Add(new LayoutBlock
            {
                BBox = new BBox(xOffset, yOffset, blockWidth, blockHeight),
                Role = role,
                Confidence = 0.95f,
                Order = b,
                Lines = lines,
            });

            yOffset += blockHeight + blockGap;
        }

        return new PageAnalysis
        {
            Blocks = blocks,
            PageWidth = 612,
            PageHeight = 792,
        };
    }

    /// <summary>
    /// Creates an analysis with blocks of mixed roles. Alternates between the
    /// given roles for each block.
    /// </summary>
    private static PageAnalysis CreateMixedAnalysis(int blockCount, int linesPerBlock, params BlockRole[] roles)
    {
        var analysis = CreateAnalysis(blockCount, linesPerBlock);
        for (int i = 0; i < analysis.Blocks.Count; i++)
            analysis.Blocks[i].Role = roles[i % roles.Length];
        return analysis;
    }

    private void ActivateWithAnalysis(int blockCount, int linesPerBlock)
    {
        var analysis = CreateAnalysis(blockCount, linesPerBlock);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });
        _nav.Active = true;
    }

    // ===== Line Navigation (7 tests) =====

    [Fact]
    public void NextLine_AdvancesWithinBlock()
    {
        ActivateWithAnalysis(1, 3);

        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);

        var result = _nav.NextLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);
    }

    [Fact]
    public void NextLine_CrossesBlockBoundary()
    {
        ActivateWithAnalysis(2, 2);

        // Advance to last line of first block
        _nav.NextLine(); // line 0 -> 1
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);

        // Cross to next block
        var result = _nav.NextLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void NextLine_AtEnd_ReturnsPageBoundary()
    {
        ActivateWithAnalysis(1, 2);

        _nav.NextLine(); // line 0 -> 1 (last line of last block)

        var result = _nav.NextLine();

        Assert.Equal(NavResult.PageBoundaryNext, result);
        // Position should not change
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);
    }

    [Fact]
    public void PrevLine_DecrementsWithinBlock()
    {
        ActivateWithAnalysis(1, 3);
        _nav.NextLine(); // 0 -> 1
        _nav.NextLine(); // 1 -> 2

        var result = _nav.PrevLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);
    }

    [Fact]
    public void PrevLine_CrossesBlockBoundary()
    {
        ActivateWithAnalysis(2, 3);

        // Move to block 1, line 0
        _nav.NextLine(); // b0 l0 -> l1
        _nav.NextLine(); // b0 l1 -> l2
        _nav.NextLine(); // b0 l2 -> b1 l0
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);

        var result = _nav.PrevLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(2, _nav.CurrentLine); // last line of previous block
    }

    [Fact]
    public void PrevLine_AtStart_ReturnsPageBoundary()
    {
        ActivateWithAnalysis(1, 3);

        var result = _nav.PrevLine();

        Assert.Equal(NavResult.PageBoundaryPrev, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void NextLine_WhenInactive_NoStateChange()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });
        _nav.Active = false; // explicitly inactive

        var result = _nav.NextLine();

        Assert.Equal(NavResult.Ok, result); // returns Ok but does nothing
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    // ===== SetAnalysis (4 tests) =====

    [Fact]
    public void SetAnalysis_FiltersNavigableBlocks()
    {
        // 4 blocks: text, image, text, header — only text (22) is navigable
        var analysis = CreateMixedAnalysis(4, 2, TextRole, ImageRole, TextRole, HeaderRole);
        var navigable = new HashSet<BlockRole> { TextRole };

        _nav.SetAnalysis(analysis, navigable);

        Assert.Equal(2, _nav.NavigableCount); // only 2 text blocks
    }

    [Fact]
    public void SetAnalysis_ResetsPosition()
    {
        ActivateWithAnalysis(2, 3);

        // Advance position
        _nav.NextLine();
        _nav.NextLine();
        Assert.Equal(2, _nav.CurrentLine);

        // Set a NEW analysis (different object) — should reset
        var newAnalysis = CreateAnalysis(2, 4);
        _nav.SetAnalysis(newAnalysis, new HashSet<BlockRole> { TextRole });

        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void HasAnalysis_TrueWithBlocks()
    {
        var analysis = CreateAnalysis(2, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });

        Assert.True(_nav.HasAnalysis);
    }

    [Fact]
    public void SetAnalysis_EmptyBlocks_HasAnalysisFalse()
    {
        // All blocks are images — none match the navigable set
        var analysis = CreateAnalysis(3, 2, ImageRole);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });

        Assert.False(_nav.HasAnalysis);
        Assert.Equal(0, _nav.NavigableCount);
    }

    // ===== Snap Animation (4 tests) =====

    [Fact]
    public void StartSnap_CreatesAnimation()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // Tick should report animating (snap in progress)
        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        Assert.True(animating);
    }

    [Fact]
    public void Tick_SnapCompletes()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // With SnapDurationMs=1, a brief sleep ensures the stopwatch advances past the duration
        Thread.Sleep(5);

        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        // Snap should have completed (t >= 1.0), so Tick returns false
        Assert.False(animating);
    }

    [Fact]
    public void StartSnap_WhenInactive_NoEffect()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });
        _nav.Active = false;

        double cx = 100, cy = 200;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        double origX = cx, origY = cy;
        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        Assert.False(animating);
        Assert.Equal(origX, cx);
        Assert.Equal(origY, cy);
    }

    [Fact]
    public void MultipleSnaps_LastWins()
    {
        ActivateWithAnalysis(2, 3);

        double cx = 0, cy = 0;

        // First snap: to block 0, line 0 (current position)
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // Advance to block 1 and start a second snap
        _nav.NextLine(); // b0 l0 -> l1
        _nav.NextLine(); // b0 l1 -> l2
        _nav.NextLine(); // b0 l2 -> b1 l0
        Assert.Equal(1, _nav.CurrentBlock);

        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // Let it complete
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        // Camera Y should reflect block 1's first line position, not block 0's.
        // Block 1 starts at a lower Y (further down the page), so cameraY
        // should be different from the centered position of block 0 line 0.
        var block1Line0 = _nav.CurrentLineInfo;
        double expectedY = WindowHeight / 2.0 - block1Line0.Y * Zoom;
        Assert.Equal(expectedY, cy, precision: 1);
    }

    // ===== Scroll Hold (4 tests) =====

    [Fact]
    public void StartScroll_SetsDirection()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        // Position camera at line start so there's room to scroll
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        _nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        Assert.True(_nav.ScrollSpeed > 0);
    }

    [Fact]
    public void StopScroll_ClearsSpeed()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        Assert.True(_nav.ScrollSpeed > 0);

        _nav.StopScroll();

        Assert.Equal(0.0, _nav.ScrollSpeed);
    }

    [Fact]
    public void ScrollHold_SpeedIncreases()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartScroll(ScrollDirection.Forward, cx);

        // Sample speed early
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        double earlySpeed = _nav.ScrollSpeed;

        // Wait for ramp to increase speed — the ramp formula is quadratic
        // so even a short additional wait should yield higher speed.
        // We use a generous sleep to ensure the stopwatch advances enough.
        Thread.Sleep(200);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        double laterSpeed = _nav.ScrollSpeed;

        Assert.True(laterSpeed > earlySpeed,
            $"Expected later speed ({laterSpeed}) > early speed ({earlySpeed})");
    }

    [Fact]
    public void StopScrollAndEdgeHold_ClearsAll()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        _nav.StopScrollAndEdgeHold();

        Assert.Equal(0.0, _nav.ScrollSpeed);

        // After stopping, Tick should not report scroll animation
        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        Assert.False(animating);
    }

    // ===== Auto-scroll (3 tests) =====

    [Fact]
    public void StartAutoScroll_Activates()
    {
        ActivateWithAnalysis(1, 3);

        _nav.StartAutoScroll(100.0);

        Assert.True(_nav.AutoScrolling);
    }

    [Fact]
    public void StopAutoScroll_Deactivates()
    {
        ActivateWithAnalysis(1, 3);

        _nav.StartAutoScroll(100.0);
        Assert.True(_nav.AutoScrolling);

        _nav.StopAutoScroll();

        Assert.False(_nav.AutoScrolling);
    }

    [Fact]
    public void SetAutoScrollBoost_AffectsState()
    {
        ActivateWithAnalysis(1, 3);

        // Inject a controlled clock so wall-clock positioning is deterministic.
        // elapsed = 1.0s, speed = 100, zoom = 4 → displacement = -400 (no boost)
        //                                                          = -800 (with boost)
        _nav.AutoScrollElapsedSecondsOverride = () => 1.0;

        // Tick without boost
        _nav.StartAutoScroll(100.0);
        double cx1 = 0;
        _nav.TickAutoScroll(ref cx1, 0, Zoom, WindowWidth);

        // Tick with boost (restart so clock re-captures from 0)
        _nav.StopAutoScroll();
        _nav.StartAutoScroll(100.0);
        _nav.SetAutoScrollBoost(true);
        double cx2 = 0;
        _nav.TickAutoScroll(ref cx2, 0, Zoom, WindowWidth);

        // Boosted should have moved further (more negative = scrolled more)
        Assert.True(cx2 < cx1,
            $"Expected boosted position ({cx2}) < non-boosted ({cx1})");
        Assert.Equal(cx1 * 2, cx2, precision: 10);
    }

    [Fact]
    public void PendingPause_SuppressesScrollDuringSnap()
    {
        // Simulate entering a narrow block (like an equation) where the entire
        // block fits on screen. A deferred pause is set, and the snap is running.
        // Auto-scroll must NOT advance past the block while the snap is in progress.
        ActivateWithAnalysis(2, 1);
        _nav.StartAutoScroll(100.0);

        // Start a snap (simulates entering a new block)
        _nav.StartSnapToCurrent(0, 0, Zoom, WindowWidth, WindowHeight);

        // Set a deferred pause (as the controller does on block entry)
        _nav.PauseAutoScroll(600);

        // Tick auto-scroll while snap is still running — should NOT scroll
        double cx = 0;
        bool reachedEnd = _nav.TickAutoScroll(ref cx, 0.1, Zoom, WindowWidth);
        Assert.False(reachedEnd, "Should not advance while snap is running with pending pause");
        Assert.Equal(0, cx); // camera X should not have moved
    }

    [Fact]
    public void PendingPause_ActivatesAfterSnapCompletes()
    {
        ActivateWithAnalysis(2, 1);
        _nav.StartAutoScroll(100.0);

        // Start snap and deferred pause. The pause is generous (200ms) and the
        // post-pause sleep is ~3x the pause to leave headroom for CI scheduling
        // jitter — Thread.Sleep is approximate, especially on shared runners.
        _nav.StartSnapToCurrent(0, 0, Zoom, WindowWidth, WindowHeight);
        const int pauseMs = 200;
        _nav.PauseAutoScroll(pauseMs);

        // Complete the snap
        double cx = 0, cy = 0;
        Thread.Sleep(10);
        _nav.Tick(ref cx, ref cy, 1.0, Zoom, WindowWidth); // large dt to finish snap

        // Now tick auto-scroll — pause should be active (not scrolling)
        double cxBefore = cx;
        bool reachedEnd = _nav.TickAutoScroll(ref cx, 0.1, Zoom, WindowWidth);
        Assert.False(reachedEnd, "Should be pausing, not advancing");
        Assert.Equal(cxBefore, cx); // camera X should not move during pause

        // After pause expires, first tick clears the timer, second tick scrolls.
        // Use larger dt (50ms equivalent) so even a slow runner sees observable
        // camera movement on the scroll tick.
        Thread.Sleep(pauseMs * 3);
        _nav.TickAutoScroll(ref cx, 0.05, Zoom, WindowWidth); // clears pause timer
        reachedEnd = _nav.TickAutoScroll(ref cx, 0.05, Zoom, WindowWidth); // actually scrolls
        // For a narrow block that already fits on screen, reachedEnd may be
        // immediately true — the key assertion is that the pause was applied.
        Assert.True(reachedEnd || cx != cxBefore,
            "After pause expires, auto-scroll should resume or reach block end");
    }

    // ===== UpdateZoom (3 tests) =====

    [Fact]
    public void UpdateZoom_ActivatesAboveThreshold()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });
        Assert.False(_nav.Active);

        // Zoom above threshold (default 3.0)
        _nav.UpdateZoom(4.0, 0, 0, WindowWidth, WindowHeight);

        Assert.True(_nav.Active);
    }

    [Fact]
    public void UpdateZoom_DeactivatesBelowThreshold()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });

        // First activate
        _nav.UpdateZoom(4.0, 0, 0, WindowWidth, WindowHeight);
        Assert.True(_nav.Active);

        // Then zoom below threshold
        _nav.UpdateZoom(2.0, 0, 0, WindowWidth, WindowHeight);

        Assert.False(_nav.Active);
    }

    [Fact]
    public void ScaleVerticalBias_ScalesProportionally()
    {
        ActivateWithAnalysis(1, 3);

        _nav.VerticalBias = 100.0;
        double previousZoom = 4.0;
        double newZoom = 8.0;

        _nav.ScaleVerticalBias(previousZoom, newZoom);

        // Bias should scale by newZoom/previousZoom = 8/4 = 2x
        Assert.Equal(200.0, _nav.VerticalBias, precision: 5);
    }

    // ===== Navigation chunks (benefit B) =====

    private static LayoutBlock ChunkBlock(float x, float y, float w, float h, BlockRole role = BlockRole.Text)
    {
        var b = new LayoutBlock { BBox = new BBox(x, y, w, h), Role = role, Confidence = 0.9f };
        b.Lines.Add(new LineInfo(y + h / 2, h, x, w)); // one line per block
        return b;
    }

    private static PageAnalysis ChunkAnalysis(params LayoutBlock[] blocks)
    {
        for (int i = 0; i < blocks.Length; i++) blocks[i].Order = i;
        return new PageAnalysis { Blocks = [.. blocks], PageWidth = 600, PageHeight = 800 };
    }

    private static readonly HashSet<BlockRole> NavRoles = [BlockRole.Text, BlockRole.Heading];

    [Fact]
    public void Chunks_TightColumnRun_FormsOneChunk()
    {
        // Narrow heading + two paragraphs, same column, tight gaps → one chunk.
        var a = ChunkAnalysis(
            ChunkBlock(40, 50, 100, 14, BlockRole.Heading),
            ChunkBlock(40, 70, 240, 40),
            ChunkBlock(40, 116, 240, 40));
        _nav.SetAnalysis(a, NavRoles);
        Assert.Equal(1, _nav.ChunkCount);
    }

    [Fact]
    public void Chunks_DifferentColumns_AreSeparateChunks()
    {
        var a = ChunkAnalysis(
            ChunkBlock(40, 50, 240, 40),
            ChunkBlock(40, 96, 240, 40),
            ChunkBlock(320, 50, 240, 40),   // right column → new chunk
            ChunkBlock(320, 96, 240, 40));
        _nav.SetAnalysis(a, NavRoles);
        Assert.Equal(2, _nav.ChunkCount);
    }

    [Fact]
    public void Chunks_LargeVerticalGap_StartsNewChunk()
    {
        var a = ChunkAnalysis(
            ChunkBlock(40, 50, 240, 40),     // bottom 90
            ChunkBlock(40, 300, 240, 40));   // gap 210 ≫ ChunkMaxGapPoints
        _nav.SetAnalysis(a, NavRoles);
        Assert.Equal(2, _nav.ChunkCount);
    }

    [Fact]
    public void CurrentChunk_StableWithinChunk_AdvancesAcrossBoundary()
    {
        var a = ChunkAnalysis(
            ChunkBlock(40, 50, 240, 40),    // chunk 0
            ChunkBlock(40, 96, 240, 40),    // chunk 0 (same column, tight)
            ChunkBlock(320, 50, 240, 40));  // chunk 1 (right column)
        _nav.SetAnalysis(a, NavRoles);
        _nav.Active = true;

        Assert.Equal(0, _nav.CurrentChunk);
        _nav.NextLine();                                  // → block 1, still chunk 0
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentChunk);
        _nav.NextLine();                                  // → block 2, new chunk
        Assert.Equal(2, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentChunk);
    }

    [Fact]
    public void SnapTarget_ForWideBlock_IsClampStable()
    {
        // A block far wider than the window triggers ClampX's soft-clamp. The
        // carriage-return snap target must already equal the clamped position so
        // auto-scroll resume doesn't nudge the camera — the "overshoot left then
        // snap right to line start" the user reported on each line advance.
        var a = ChunkAnalysis(ChunkBlock(40, 50, 400, 30)); // 400pt × zoom 4 = 1600px ≫ 800
        _nav.SetAnalysis(a, NavRoles);
        _nav.Active = true;

        double camX = 0, camY = 0;
        _nav.StartSnapToCurrent(camX, camY, Zoom, WindowWidth, WindowHeight);
        // Snap uses a real Stopwatch (SnapDurationMs=1); let wall-clock time pass
        // so it completes and camX settles on the snap target.
        for (int i = 0; i < 5; i++) { System.Threading.Thread.Sleep(2); _nav.Tick(ref camX, ref camY, 0.05, Zoom, WindowWidth); }

        double reclamped = ((ICameraClamp)_nav).ClampX(camX, Zoom, WindowWidth);
        Assert.Equal(reclamped, camX, precision: 2); // re-clamping must not move it
    }

    // ===== Smooth frame-block support (TrySetCurrentByPageIndex / ComputeSnapTarget) =====

    [Fact]
    public void TrySetCurrentByPageIndex_SeatsNavigableBlock_ResetsLineAndBias()
    {
        // Page blocks [Text, Figure, Text] → navigable subset = page indices {0, 2}.
        var analysis = CreateMixedAnalysis(3, 3, BlockRole.Text, BlockRole.Figure, BlockRole.Text);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });
        _nav.CurrentLine = 2;
        _nav.VerticalBias = 17;

        bool ok = _nav.TrySetCurrentByPageIndex(2); // page index 2 → navigable position 1

        Assert.True(ok);
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(2, _nav.CurrentNavigableArrayIndex); // page-level index round-trips
        Assert.Equal(0, _nav.CurrentLine);
        Assert.Equal(0, _nav.VerticalBias);
    }

    [Fact]
    public void TrySetCurrentByPageIndex_NonNavigableBlock_ReturnsFalse_LeavesStateUnchanged()
    {
        var analysis = CreateMixedAnalysis(3, 3, BlockRole.Text, BlockRole.Figure, BlockRole.Text);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });
        Assert.True(_nav.TrySetCurrentByPageIndex(0));
        _nav.CurrentLine = 1;

        bool ok = _nav.TrySetCurrentByPageIndex(1); // page index 1 is a Figure (non-navigable)

        Assert.False(ok);
        Assert.Equal(0, _nav.CurrentBlock); // unchanged
        Assert.Equal(1, _nav.CurrentLine);  // unchanged
    }

    [Fact]
    public void ComputeSnapTarget_CentersCurrentLineVertically()
    {
        var analysis = CreateAnalysis(2, 3); // two stacked 468-wide text blocks, 3 lines each
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });
        Assert.True(_nav.TrySetCurrentByPageIndex(1)); // second block

        var line = _nav.CurrentLineInfo; // first line of block 1
        var (_, y) = _nav.ComputeSnapTarget(Zoom, WindowWidth, WindowHeight);

        // Vertical target centers the current line: y = wh/2 - lineY*zoom (bias 0, no pixel snap).
        Assert.Equal(WindowHeight / 2.0 - line.Y * Zoom, y, precision: 3);
    }

    [Fact]
    public void ComputeSnapTarget_ReflectsSeatedBlock()
    {
        var analysis = CreateAnalysis(2, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });

        _nav.TrySetCurrentByPageIndex(0);
        var (_, y0) = _nav.ComputeSnapTarget(Zoom, WindowWidth, WindowHeight);
        _nav.TrySetCurrentByPageIndex(1);
        var (_, y1) = _nav.ComputeSnapTarget(Zoom, WindowWidth, WindowHeight);

        Assert.NotEqual(y0, y1); // different blocks → different vertical frame
    }

    [Fact]
    public void ComputeSnapTarget_NarrowBlockCentered_WideBlockLeftAligned()
    {
        var analysis = CreateAnalysis(1, 3); // single 468-wide text block at x=72
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });
        _nav.TrySetCurrentByPageIndex(0);
        const double blockCenterX = 72 + 468 / 2.0;

        // Narrow: 468*1 ≤ 0.75*800 → centered, so the block centre maps to viewport centre.
        var (xNarrow, _) = _nav.ComputeSnapTarget(1.0, WindowWidth, WindowHeight);
        Assert.Equal(WindowWidth / 2.0, blockCenterX * 1.0 + xNarrow, precision: 1);

        // Wide: 468*4 > 0.75*800 → left-aligned, so the block centre sits right of viewport centre.
        var (xWide, _) = _nav.ComputeSnapTarget(4.0, WindowWidth, WindowHeight);
        Assert.True(blockCenterX * 4.0 + xWide > WindowWidth / 2.0);
    }

    // ===== Forward line-advance trigger fires at the line end, not the block edge =====

    /// <summary>Single wide block whose only line has the given (short) width at x=72.</summary>
    private static PageAnalysis ShortLineAnalysis(float blockWidth, float lineWidth)
    {
        var block = new LayoutBlock
        {
            BBox = new BBox(72, 72, blockWidth, 20), Role = BlockRole.Text, Confidence = 0.9f, Order = 0,
        };
        block.Lines.Add(new LineInfo(82, 16, 72, lineWidth)); // single line, x=72
        return new PageAnalysis { Blocks = [block], PageWidth = 612, PageHeight = 792 };
    }

    [Fact]
    public void IsAtHardEdge_Forward_TriggersAtLineEnd_NotBlockEnd()
    {
        // 468pt-wide block, but the current line is only 150pt wide.
        _nav.SetAnalysis(ShortLineAnalysis(blockWidth: 468, lineWidth: 150),
            new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;

        const double zoom = 4.0;
        // Line right (5% margin): 72 + 150 + 7.5 = 229.5 → camera at line end = 800 - 229.5*4 = -118.
        double lineEndCamX = WindowWidth - (72 + 150 + 150 * 0.05) * zoom;

        // At the line end the forward advance can fire.
        Assert.True(_nav.IsAtHardEdge(lineEndCamX, zoom, WindowWidth, ScrollDirection.Forward));
        // Scrolled a little past the line end but FAR from the block's right edge: the old
        // block-edge rule would still be false here; the line-edge rule fires.
        Assert.True(_nav.IsAtHardEdge(lineEndCamX - 100, zoom, WindowWidth, ScrollDirection.Forward));
        // Before the line end is on screen → not yet at the edge.
        Assert.False(_nav.IsAtHardEdge(lineEndCamX + 100, zoom, WindowWidth, ScrollDirection.Forward));
    }

    [Fact]
    public void IsAtHardEdge_Forward_FullWidthLine_StillTriggersAtBlockExtent()
    {
        // Regression guard: a line spanning the full block width must still require
        // scrolling to the block's right extent (no premature advance).
        _nav.SetAnalysis(ShortLineAnalysis(blockWidth: 468, lineWidth: 468),
            new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;

        const double zoom = 4.0;
        double blockEndCamX = WindowWidth - (72 + 468 + 468 * 0.05) * zoom;

        Assert.True(_nav.IsAtHardEdge(blockEndCamX, zoom, WindowWidth, ScrollDirection.Forward));
        Assert.False(_nav.IsAtHardEdge(blockEndCamX + 200, zoom, WindowWidth, ScrollDirection.Forward));
    }

    [Fact]
    public void AutoScroll_ShortLine_HoldsReadingBeat_While_WideLine_KeepsScrolling()
    {
        // Single-line wide block; the line's width decides when auto-scroll reaches the end
        // (line extent, not block extent). A SHORT line reaches its end with no scrolling,
        // so instead of flashing past it now holds a reading beat (stops scrolling); a
        // FULL-WIDTH line is still off-screen to the right, so it keeps scrolling.
        PageAnalysis Wide(float lineWidth)
        {
            var b = new LayoutBlock
            {
                BBox = new BBox(72, 72, 468, 20), Role = BlockRole.Text, Confidence = 0.9f, Order = 0,
            };
            b.Lines.Add(new LineInfo(82, 16, 72, lineWidth));
            return new PageAnalysis { Blocks = [b], PageWidth = 612, PageHeight = 792 };
        }

        // SHORT line (150pt): reaches the line end immediately, then holds the beat rather
        // than advancing — TickAutoScroll returns false and the scroll speed drops to 0.
        _nav.SetAnalysis(Wide(150), new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;
        _nav.AutoScrollElapsedSecondsOverride = () => 0.0;
        _nav.StartAutoScroll(20.0);
        double camX = 0;
        Assert.False(_nav.TickAutoScroll(ref camX, 0.016, Zoom, WindowWidth));
        Assert.Equal(0.0, _nav.ScrollSpeed); // paused on the reading beat, not flashing past

        // FULL-WIDTH line (468pt) in the same wide block: still off-screen to the right at
        // the framed position, so auto-scroll keeps scrolling (does NOT advance yet).
        var nav2 = new RailNav(_config.ToCoreSettings());
        nav2.SetAnalysis(Wide(468), new HashSet<BlockRole> { BlockRole.Text });
        nav2.Active = true;
        nav2.AutoScrollElapsedSecondsOverride = () => 0.0;
        nav2.StartAutoScroll(20.0);
        double camX2 = 0;
        Assert.False(nav2.TickAutoScroll(ref camX2, 0.016, Zoom, WindowWidth));
        Assert.True(nav2.ScrollSpeed > 0.0); // still scrolling across the full line
    }

    [Fact]
    public void LineReadBudgetMs_IsClampedAndZoomIndependent()
    {
        // 468pt-wide block, current line 90pt wide.
        _nav.SetAnalysis(ShortLineAnalysis(blockWidth: 468, lineWidth: 90),
            new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;

        // Budget is page-space (line.Width / pace): no zoom argument, so it is identical at
        // any magnification — the same text reads for the same time. 90pt / 30pt-per-sec
        // = 3000ms, clamped to the [Min, Max] reading-beat window.
        double budget = _nav.LineReadBudgetMs(30.0);
        Assert.Equal(1200.0, budget); // MaxLineReadMs cap

        // A fast pace shrinks the beat but never below the floor.
        Assert.Equal(350.0, _nav.LineReadBudgetMs(100000.0)); // MinLineReadMs floor

        // A proportional middle: 90pt / 0.18pt-per-ms... pick a pace landing inside the band.
        double mid = _nav.LineReadBudgetMs(90.0 / 0.6); // 90 / 150 *1000 = 600ms
        Assert.Equal(600.0, mid, precision: 1);
    }

    [Fact]
    public void ForwardAdvance_ShortLine_GatedUntilReadingBeatElapses()
    {
        // Short line (90pt) in a wide block: at the hard edge the instant it is framed, so a
        // forward advance must be held until the line has been read for its beat.
        _nav.SetAnalysis(ShortLineAnalysis(blockWidth: 468, lineWidth: 90),
            new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;

        _nav.LineDwellElapsedMsOverride = () => 0.0; // just arrived
        Assert.True(_nav.ForwardAdvanceHeldForReadingBeat(Zoom, WindowWidth));

        _nav.LineDwellElapsedMsOverride = () => 5000.0; // read long enough
        Assert.False(_nav.ForwardAdvanceHeldForReadingBeat(Zoom, WindowWidth));
    }

    [Fact]
    public void ForwardAdvance_FullWidthLine_NeverGated()
    {
        // A full-width line is only at the edge after the user scrolls across it, which
        // already provides the reading time — so the beat gate must never apply to it,
        // even immediately on arrival.
        _nav.SetAnalysis(ShortLineAnalysis(blockWidth: 468, lineWidth: 468),
            new HashSet<BlockRole> { BlockRole.Text });
        _nav.Active = true;

        _nav.LineDwellElapsedMsOverride = () => 0.0;
        Assert.False(_nav.ForwardAdvanceHeldForReadingBeat(Zoom, WindowWidth));
    }
}
