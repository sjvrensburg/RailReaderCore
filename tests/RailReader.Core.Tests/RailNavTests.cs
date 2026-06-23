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

    // ===== Equation framing (regression) =====

    [Fact]
    public void DisplayMathEquation_CentersOnOwnBounds_NotChunkLeftEdge()
    {
        // Regression (chunk framing, 4d890b6): a DisplayMath equation tightly following a paragraph in
        // the same column shares its reading chunk. It must still frame/centre on ITS OWN bounds, not
        // left-align to the wide chunk's left edge (the symptom: "snap to the super-block left edge,
        // equations no longer centred"). Prose stays chunk-framed.
        const float colX = 72f, colW = 468f;                    // a full-column paragraph
        const float eqW = 150f, eqX = colX + (colW - eqW) / 2f; // a narrow equation centred in the column
        var para = new LayoutBlock
        {
            BBox = new BBox(colX, 72f, colW, 48f), Role = BlockRole.Text, Confidence = 0.95f, Order = 0,
            Lines = [new LineInfo(80f, 16f, colX, colW)],
        };
        var eq = new LayoutBlock
        {
            BBox = new BBox(eqX, 130f, eqW, 16f), Role = BlockRole.DisplayMath, Confidence = 0.95f, Order = 1,
            Lines = [new LineInfo(138f, 16f, eqX, eqW)],
        };
        var analysis = new PageAnalysis { Blocks = [para, eq], PageWidth = 612, PageHeight = 792 };
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text, BlockRole.DisplayMath });
        _nav.Active = true;

        // The paragraph and equation are one reading chunk (tight gap, overlapping column).
        Assert.Equal(1, _nav.ChunkCount);

        // Seat on the equation and frame it.
        _nav.CurrentBlock = 1;
        Assert.Equal(BlockRole.DisplayMath, _nav.CurrentNavigableBlock.Role);

        const double z = 3.0;
        var (x, _) = _nav.ComputeSnapTarget(z, WindowWidth, WindowHeight);

        // Centred on the equation's OWN centre (its 5% margin is symmetric, so the centre is unchanged).
        double eqCenter = eqX + eqW / 2.0;
        Assert.Equal(WindowWidth / 2.0 - eqCenter * z, x, 3);

        // And NOT left-aligned to the chunk's (paragraph) left edge — the regression.
        Assert.NotEqual(WindowWidth * 0.05 - colX * z, x, 3);
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

    // ===== Forced activation (low-zoom rail) =====

    [Fact]
    public void ForceActivateAt_ActivatesBelowThreshold()
    {
        var analysis = CreateAnalysis(2, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });
        Assert.False(_nav.Active);

        // Seat on the second block's region at unity zoom (well below the 3.0 threshold).
        var box = analysis.Blocks[1].BBox;
        _nav.ForceActivateAt(box.X + box.W / 2.0, box.Y + box.H / 2.0);

        Assert.True(_nav.Active);
        Assert.True(_nav.ForceActive);
        Assert.Equal(1, _nav.CurrentBlock);
    }

    [Fact]
    public void ForceActivateAt_SurvivesSubThresholdUpdateZoom()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });

        _nav.ForceActivateAt(100, 100);
        Assert.True(_nav.Active);

        // A normal zoom re-evaluation below threshold must NOT deactivate a forced rail.
        _nav.UpdateZoom(1.0, 0, 0, WindowWidth, WindowHeight);
        Assert.True(_nav.Active);
        Assert.True(_nav.ForceActive);
    }

    [Fact]
    public void ClearForceActive_ThenSubThresholdUpdateZoom_Deactivates()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });

        _nav.ForceActivateAt(100, 100);
        Assert.True(_nav.Active);

        _nav.ClearForceActive();
        Assert.False(_nav.ForceActive);

        // Once the force is released, the normal zoom gate applies again.
        _nav.UpdateZoom(1.0, 0, 0, WindowWidth, WindowHeight);
        Assert.False(_nav.Active);
    }

    [Fact]
    public void Deactivate_ClearsForceActive()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole });

        _nav.ForceActivateAt(100, 100);
        Assert.True(_nav.ForceActive);

        _nav.Deactivate();
        Assert.False(_nav.Active);
        Assert.False(_nav.ForceActive);
    }

    [Fact]
    public void ForceActivateAt_NoAnalysis_NoOp()
    {
        // No SetAnalysis call → nothing seated.
        _nav.ForceActivateAt(100, 100);
        Assert.False(_nav.Active);
        Assert.False(_nav.ForceActive);
    }

    [Fact]
    public void ForceActivateAt_ThenNewAnalysis_ClearsForce_NoLeakAcrossPages()
    {
        _nav.SetAnalysis(CreateAnalysis(1, 3), new HashSet<BlockRole> { TextRole });
        _nav.ForceActivateAt(100, 100);
        Assert.True(_nav.ForceActive);

        // Paging to a new page applies a DIFFERENT analysis — the forced session must not leak onto it.
        _nav.SetAnalysis(CreateAnalysis(1, 3), new HashSet<BlockRole> { TextRole });
        Assert.False(_nav.ForceActive);

        // And at low zoom the new page must NOT auto-engage rail.
        _nav.UpdateZoom(1.0, 0, 0, WindowWidth, WindowHeight);
        Assert.False(_nav.Active);
    }

    [Fact]
    public void ForceActivateAt_ThenZoomAboveThreshold_ConsumesForce()
    {
        _nav.SetAnalysis(CreateAnalysis(1, 3), new HashSet<BlockRole> { TextRole });
        _nav.ForceActivateAt(100, 100);

        // Zoom up past the threshold: now in normal rail territory, the force flag is consumed.
        _nav.UpdateZoom(4.0, 0, 0, WindowWidth, WindowHeight);
        Assert.True(_nav.Active);
        Assert.False(_nav.ForceActive);

        // Zooming back below the threshold now deactivates rail as usual (no longer sticky).
        _nav.UpdateZoom(1.0, 0, 0, WindowWidth, WindowHeight);
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
    public void Chunks_FullWidthSpanner_DoesNotMergeWithColumn()
    {
        // Repro of the "rail frame spans both columns" bug (symptom 2). A
        // full-width abstract sitting directly above the right column overlaps it
        // by ≥50% of the (narrower) column, so the old overlap test merged them
        // and the chunk's union spanned both columns — framing the right column
        // across the gutter even though its lines highlight only the right column.
        // A left-column block makes the page genuinely multi-column.
        var a = ChunkAnalysis(
            ChunkBlock(40, 50, 520, 30),    // 0: full-width abstract (520/600 = 0.87)
            ChunkBlock(320, 90, 240, 40),   // 1: right-column body, directly below
            ChunkBlock(40, 90, 240, 40));   // 2: left-column body → page is multi-column
        _nav.SetAnalysis(a, NavRoles);

        _nav.CurrentBlock = 0; int abstractChunk = _nav.CurrentChunk;
        _nav.CurrentBlock = 1; int rightColumnChunk = _nav.CurrentChunk;
        Assert.NotEqual(abstractChunk, rightColumnChunk);
    }

    [Fact]
    public void Chunks_SingleColumn_WideBodyStillMergesWithHeading()
    {
        // The spanner barrier must NOT fire on a single-column page: a wide body
        // there is normal and should still chunk with its narrow heading (no
        // side-by-side block exists, so the page is not multi-column).
        var a = ChunkAnalysis(
            ChunkBlock(40, 50, 200, 14, BlockRole.Heading), // narrow heading
            ChunkBlock(40, 70, 520, 60));                   // full-width body, no column beside it
        _nav.SetAnalysis(a, NavRoles);
        Assert.Equal(1, _nav.ChunkCount);
    }

    [Fact]
    public void Chunks_SpannerAboveStaggeredColumns_StaysSeparate()
    {
        // Edge #1 (band detection): the right column is staggered below the left and so
        // vertically overlaps no left block. A pairwise side-by-side test would flag
        // neither as a column, letting the full-width title merge with the left column
        // and frame the camera across the gutter. Band detection recognises both columns
        // by their X-band, so the title is kept in its own chunk.
        var a = ChunkAnalysis(
            ChunkBlock(40, 40, 520, 20),    // 0: full-width title (520/600 = 0.87)
            ChunkBlock(40, 70, 240, 60),    // 1: left column (top)
            ChunkBlock(320, 200, 240, 60)); // 2: right column (staggered low, no y-overlap with [1])
        _nav.SetAnalysis(a, NavRoles);

        _nav.CurrentBlock = 0; int titleChunk = _nav.CurrentChunk;
        _nav.CurrentBlock = 1; int leftColumnChunk = _nav.CurrentChunk;
        Assert.NotEqual(titleChunk, leftColumnChunk);
    }

    [Fact]
    public void Chunks_IncidentalSideFloat_OverArms_DocumentedLimitation()
    {
        // Edge #2 (intentionally NOT fixed — see BlockGeom.MarkColumnBlocks remarks):
        // a single-column body with a narrow side-float (caption/footnote) beside it.
        // The pairwise floor flags the body as a column (the float sits beside it), so
        // the full-width abstract above is split from the body into a separate chunk.
        // This is a benign over-segmentation — both frame at single-column width,
        // nothing is framed across a gutter. It is left as-is because clearing the
        // body's flag (to re-merge) is the same "remove a barrier" move that would let
        // a genuine column be framed across the gutter (symptom-2). This test pins the
        // current behaviour so any future change to it is deliberate.
        var a = ChunkAnalysis(
            ChunkBlock(40, 40, 520, 24),   // 0: full-width abstract (spanner)
            ChunkBlock(40, 74, 260, 200),  // 1: single-column body, directly below
            ChunkBlock(320, 74, 100, 80)); // 2: narrow side-float beside the body
        _nav.SetAnalysis(a, NavRoles);

        _nav.CurrentBlock = 0; int abstractChunk = _nav.CurrentChunk;
        _nav.CurrentBlock = 1; int bodyChunk = _nav.CurrentChunk;
        Assert.NotEqual(abstractChunk, bodyChunk); // over-armed: separate chunks
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
    public void TrySetCurrentByPageIndex_SeatsSpecifiedLine_ClearsBias()
    {
        // Page blocks [Text, Figure, Text] → navigable subset = page indices {0, 2}.
        var analysis = CreateMixedAnalysis(3, 3, BlockRole.Text, BlockRole.Figure, BlockRole.Text);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });
        _nav.VerticalBias = 17;

        bool ok = _nav.TrySetCurrentByPageIndex(2, line: 2); // page index 2, third line

        Assert.True(ok);
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(2, _nav.CurrentLine);  // seated the requested line, not line 0
        Assert.Equal(0, _nav.VerticalBias); // still clears bias
    }

    [Fact]
    public void TrySetCurrentByPageIndex_ClampsLineToBlockRange()
    {
        var analysis = CreateAnalysis(1, 3); // one block, valid lines 0..2
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });

        Assert.True(_nav.TrySetCurrentByPageIndex(0, line: 99)); // past the end → last line
        Assert.Equal(2, _nav.CurrentLine);

        Assert.True(_nav.TrySetCurrentByPageIndex(0, line: -5)); // negative → first line
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void Activation_PreservesPinnedLine_NotJustBlock()
    {
        // Two stacked text blocks, 3 lines each. Rail starts inactive (below threshold).
        var analysis = CreateAnalysis(2, 3);
        _nav.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });

        // Seat block 1 at line 2 and pin it, mirroring SmoothlyFrameBlock's pre-activation setup.
        Assert.True(_nav.TrySetCurrentByPageIndex(1, line: 2));
        _nav.PinCurrentBlockForActivation();

        // Crossing the rail threshold consumes the pin on activation.
        _nav.UpdateZoom(Zoom, 0, 0, WindowWidth, WindowHeight);

        Assert.True(_nav.Active);
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(2, _nav.CurrentLine); // pinned line preserved, NOT reset to 0
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

    // ===== Cell navigation (table rows split into cells) =====

    /// <summary>
    /// Creates a single Table block of <paramref name="rowCount"/> rows, each row carrying
    /// <paramref name="cellsPerRow"/> evenly-spaced cells. Mirrors what
    /// <c>LineDetector.AssignCells</c> produces so the navigation can be exercised without
    /// running detection.
    /// </summary>
    private static PageAnalysis CreateTableAnalysis(int rowCount, int cellsPerRow)
    {
        const float xOffset = 72f, blockWidth = 468f, rowHeight = 16f, top = 72f;
        float cellWidth = blockWidth / cellsPerRow;

        var lines = new List<LineInfo>();
        for (int r = 0; r < rowCount; r++)
        {
            var cells = new List<CellInfo>();
            for (int c = 0; c < cellsPerRow; c++)
                cells.Add(new CellInfo(xOffset + c * cellWidth, cellWidth * 0.6f));
            lines.Add(new LineInfo(top + r * rowHeight, rowHeight, xOffset, blockWidth, cells));
        }

        var block = new LayoutBlock
        {
            BBox = new BBox(xOffset, top, blockWidth, rowCount * rowHeight),
            Role = BlockRole.Table,
            Confidence = 0.95f,
            Order = 0,
            Lines = lines,
        };
        return new PageAnalysis { Blocks = [block], PageWidth = 612, PageHeight = 792 };
    }

    private void ActivateTable(int rowCount, int cellsPerRow)
    {
        _nav.SetAnalysis(CreateTableAnalysis(rowCount, cellsPerRow), new HashSet<BlockRole> { BlockRole.Table });
        _nav.Active = true;
    }

    [Fact]
    public void NextCell_AdvancesWithinRow()
    {
        ActivateTable(rowCount: 2, cellsPerRow: 3);

        Assert.True(_nav.HasCells);
        Assert.Equal(0, _nav.CurrentCell);

        Assert.Equal(NavResult.Ok, _nav.NextCell());
        Assert.Equal(1, _nav.CurrentCell);
        Assert.Equal(0, _nav.CurrentLine); // same row
    }

    [Fact]
    public void NextCell_AtRowEnd_AdvancesLineAndResetsCell()
    {
        ActivateTable(rowCount: 2, cellsPerRow: 2);

        Assert.Equal(NavResult.Ok, _nav.NextCell()); // cell 0 -> 1
        Assert.Equal(1, _nav.CurrentCell);

        Assert.Equal(NavResult.Ok, _nav.NextCell()); // last cell -> next row
        Assert.Equal(1, _nav.CurrentLine);
        Assert.Equal(0, _nav.CurrentCell); // seated at first cell of the new row
    }

    [Fact]
    public void NextCell_AtLastCellOfLastRow_ReturnsPageBoundary()
    {
        ActivateTable(rowCount: 1, cellsPerRow: 2);

        _nav.NextCell();                                       // cell 0 -> 1
        Assert.Equal(NavResult.PageBoundaryNext, _nav.NextCell()); // nowhere left on the page
    }

    [Fact]
    public void PrevCell_AtRowStart_MovesToPrevRowLastCell()
    {
        ActivateTable(rowCount: 2, cellsPerRow: 3);

        _nav.NextLine(); // move to row 1, cell reset to 0
        Assert.Equal(1, _nav.CurrentLine);
        Assert.Equal(0, _nav.CurrentCell);

        Assert.Equal(NavResult.Ok, _nav.PrevCell());
        Assert.Equal(0, _nav.CurrentLine);
        Assert.Equal(2, _nav.CurrentCell); // last cell of the 3-cell row
    }

    [Fact]
    public void PrevCell_AtFirstCellFirstRow_ReturnsPageBoundary()
    {
        ActivateTable(rowCount: 1, cellsPerRow: 3);

        Assert.Equal(NavResult.PageBoundaryPrev, _nav.PrevCell());
    }

    [Fact]
    public void CellStep_OnNonTableLine_ReturnsNotApplicable()
    {
        ActivateWithAnalysis(1, 3); // plain text blocks — no cells

        Assert.False(_nav.HasCells);
        Assert.Null(_nav.CurrentCellInfo);
        Assert.Equal(NavResult.NotApplicable, _nav.NextCell());
        Assert.Equal(NavResult.NotApplicable, _nav.PrevCell());
    }

    [Fact]
    public void CurrentLineAssignment_ResetsCurrentCell()
    {
        ActivateTable(rowCount: 2, cellsPerRow: 3);

        _nav.NextCell(); // CurrentCell -> 1
        Assert.Equal(1, _nav.CurrentCell);

        _nav.CurrentLine = 1; // any line assignment re-seats at the first cell
        Assert.Equal(0, _nav.CurrentCell);
    }

    [Fact]
    public void NextLine_ResetsCurrentCell()
    {
        ActivateTable(rowCount: 2, cellsPerRow: 3);

        _nav.NextCell();
        _nav.NextCell(); // CurrentCell -> 2
        Assert.Equal(2, _nav.CurrentCell);

        _nav.NextLine();
        Assert.Equal(0, _nav.CurrentCell);
    }

    [Fact]
    public void CurrentCellInfo_TracksActiveCell()
    {
        ActivateTable(rowCount: 1, cellsPerRow: 3);

        var first = _nav.CurrentCellInfo;
        Assert.NotNull(first);

        _nav.NextCell();
        var second = _nav.CurrentCellInfo;
        Assert.NotNull(second);
        Assert.True(second!.Value.CenterX > first!.Value.CenterX); // moved right
    }

    [Fact]
    public void StartSnapToCell_CentersCellHorizontally()
    {
        // The block (468pt) is far wider than the window at zoom 4, so it scrolls.
        // Snapping to a right-hand cell must pull the camera further left than a
        // left-hand cell, bringing the cell to the centre.
        ActivateTable(rowCount: 1, cellsPerRow: 3);

        double camLeft = 0, camY0 = 0;
        _nav.CurrentCell = 0;
        _nav.StartSnapToCell(camLeft, camY0, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        _nav.Tick(ref camLeft, ref camY0, 0.016, Zoom, WindowWidth);

        double camRight = 0, camY1 = 0;
        _nav.CurrentCell = 2;
        _nav.StartSnapToCell(camRight, camY1, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        _nav.Tick(ref camRight, ref camY1, 0.016, Zoom, WindowWidth);

        Assert.True(camRight < camLeft,
            $"right cell should scroll camera further left: camRight={camRight}, camLeft={camLeft}");
    }

}
