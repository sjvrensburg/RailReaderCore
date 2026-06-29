using System.Collections.Generic;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for <see cref="FocusBlock"/> confinement (a "portal" view pinned to one block):
/// the camera clamp (<see cref="Viewport.ClampCamera"/> bounding pan/zoom to the block rect) and the
/// rail restriction (<c>RailNav.SetAnalysis</c> collapsing the navigable set to the focus block).
/// </summary>
public class FocusBlockTests
{
    private const BlockRole TextRole = BlockRole.Text;

    // ---- Rail confinement: navigable-set collapses to the focus block -------------------------------

    /// <summary>A stack of <paramref name="blockCount"/> text blocks, 2 lines each, used by the rail tests.</summary>
    private static PageAnalysis MakeAnalysis(int blockCount, BlockRole role = TextRole)
    {
        var blocks = new List<LayoutBlock>();
        float y = 72f;
        const float w = 468f, x = 72f, lineH = 16f, gap = 20f;
        for (int b = 0; b < blockCount; b++)
        {
            float h = 2 * lineH;
            blocks.Add(new LayoutBlock
            {
                BBox = new BBox(x, y, w, h),
                Role = role,
                Confidence = 0.95f,
                Order = b,
                Lines = new List<LineInfo> { new(y, lineH, x, w), new(y + lineH, lineH, x, w) },
            });
            y += h + gap;
        }
        return new PageAnalysis { Blocks = blocks, PageWidth = 612, PageHeight = 792 };
    }

    [Fact]
    public void Focus_CollapsesNavigableSetToTheOneBlock()
    {
        var nav = new RailNav(new AppConfig().ToCoreSettings());
        nav.SetAnalysis(MakeAnalysis(3), new HashSet<BlockRole> { TextRole }, focusBlockIndex: 1);

        Assert.Equal(1, nav.NavigableCount);
        // The navigable set is exactly {page-block 1} — only that page index resolves.
        Assert.True(nav.TrySetCurrentByPageIndex(1));
        Assert.False(nav.TrySetCurrentByPageIndex(0));
        Assert.False(nav.TrySetCurrentByPageIndex(2));
    }

    [Fact]
    public void Focus_RailCannotAdvanceOffTheBlock()
    {
        var nav = new RailNav(new AppConfig { SnapDurationMs = 1, PixelSnapping = false }.ToCoreSettings());
        nav.SetAnalysis(MakeAnalysis(3), new HashSet<BlockRole> { TextRole }, focusBlockIndex: 1);
        nav.Active = true;

        Assert.Equal(0, nav.CurrentBlock);
        Assert.Equal(0, nav.CurrentLine);

        Assert.Equal(NavResult.Ok, nav.NextLine());        // line 0 -> 1 within the focus block
        Assert.Equal(1, nav.CurrentLine);

        // At the block's last line there is no "next block" to cross into — the page boundary is hit
        // instead, and the position holds. Without confinement this would step into block 2.
        Assert.Equal(NavResult.PageBoundaryNext, nav.NextLine());
        Assert.Equal(0, nav.CurrentBlock);
        Assert.Equal(1, nav.CurrentLine);
    }

    [Fact]
    public void Focus_IncludesBlockEvenWhenItsRoleIsNotNavigable()
    {
        var analysis = MakeAnalysis(3);
        analysis.Blocks[1].Role = BlockRole.Figure; // not in the navigable set below

        var nav = new RailNav(new AppConfig().ToCoreSettings());
        nav.SetAnalysis(analysis, new HashSet<BlockRole> { TextRole }, focusBlockIndex: 1);

        // Force-included despite its role, so a focused figure/table/equation is still seatable.
        Assert.Equal(1, nav.NavigableCount);
        Assert.True(nav.TrySetCurrentByPageIndex(1));
    }

    [Fact]
    public void NoFocus_KeepsTheFullNavigableSet()
    {
        var nav = new RailNav(new AppConfig().ToCoreSettings());
        nav.SetAnalysis(MakeAnalysis(3), new HashSet<BlockRole> { TextRole });
        Assert.Equal(3, nav.NavigableCount);
    }

    [Fact]
    public void ReapplyFocus_RestoresCursorPositionOnExpand()
    {
        // Collapsing to a focus block then restoring the full set must return the cursor to the block it
        // was on (page-block 2), not drop it to block 0.
        var nav = new RailNav(new AppConfig().ToCoreSettings());
        nav.SetAnalysis(MakeAnalysis(4), new HashSet<BlockRole> { TextRole });
        Assert.True(nav.TrySetCurrentByPageIndex(2));

        nav.ReapplyFocus(2);                      // collapse to the focus block
        Assert.Equal(1, nav.NavigableCount);
        Assert.Equal(0, nav.CurrentBlock);

        nav.ReapplyFocus(null);                   // restore the full set
        Assert.Equal(4, nav.NavigableCount);
        Assert.Equal(2, nav.CurrentBlock);        // back to page-block 2, not 0
    }

    [Fact]
    public void Focus_OutOfRangeIndexIsIgnored()
    {
        var nav = new RailNav(new AppConfig().ToCoreSettings());
        nav.SetAnalysis(MakeAnalysis(3), new HashSet<BlockRole> { TextRole }, focusBlockIndex: 99);
        // Stale focus → normal navigable set, not an empty/broken one.
        Assert.Equal(3, nav.NavigableCount);
    }

    // ---- Camera confinement: pan + zoom clamped to the block rect -----------------------------------

    private static DocumentModel LoadDoc()
    {
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        var doc = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(),
            new AppConfig().ToCoreSettings(), new SynchronousThreadMarshaller());
        doc.LoadPageBitmap();
        // Seed a dummy 8-block analysis for page 0 under the primary's params, so CurrentFocusBlockIndex
        // (which range-checks the focus index against the seated analysis) resolves for indices 0..7. The
        // camera/fit confinement gates on it, so without a seated analysis a Focus would (correctly) be
        // inert. Block geometry is irrelevant here — only the block COUNT (valid index range) matters; the
        // page dims used by the clamp come from the loaded PDF, not this analysis. No-focus tests are
        // unaffected (they never set Focus).
        var blocks = new List<LayoutBlock>();
        for (int i = 0; i < 8; i++)
            blocks.Add(new LayoutBlock
            {
                BBox = new BBox(0, i * 20, 100, 18),
                Role = BlockRole.Text,
                Order = i,
                // One line per block (real analyses always have ≥1 line) so a rail seated on this analysis
                // can compute a snap target — an empty Lines list would crash CurrentLineInfo on StartSnap.
                Lines = [new LineInfo(i * 20, 18, 0, 100)],
            });
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, new PageAnalysis { Blocks = blocks, PageWidth = 612, PageHeight = 792 });
        return doc;
    }

    [Fact]
    public void ClampCamera_Focus_FloorsZoomAtBlockFit()
    {
        var doc = LoadDoc();
        double pw = doc.PageWidth, ph = doc.PageHeight;
        var bounds = new BBox((float)(pw * 0.3), (float)(ph * 0.3), (float)(pw * 0.4), (float)(ph * 0.1));
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);

        const double vpW = 400, vpH = 400;
        double expectedFit = doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH);

        doc.Camera.Zoom = 0.1; // well below fit
        doc.ClampCamera(vpW, vpH);

        // Confinement raises a too-far-out zoom to exactly the block's fit-zoom — you can't zoom out
        // past seeing the whole block.
        Assert.Equal(expectedFit, doc.Camera.Zoom, precision: 4);
        Assert.True(expectedFit > 0.1);
        doc.Dispose();
    }

    [Fact]
    public void ClampCamera_Focus_PanCannotRevealBeyondTheBlock()
    {
        var doc = LoadDoc();
        double pw = doc.PageWidth, ph = doc.PageHeight;
        var bounds = new BBox((float)(pw * 0.3), (float)(ph * 0.3), (float)(pw * 0.4), (float)(ph * 0.1));
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);

        const double vpW = 400, vpH = 400;
        // Zoom in past fit so the block overflows the viewport horizontally (the clamp branch, not centering).
        double z = doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH) * 2.0;
        doc.Camera.Zoom = z;

        // Shove the camera hard in each direction; confinement must pull the block back to fully cover
        // the viewport so no off-block page content is ever visible.
        foreach (double shove in new[] { 1e6, -1e6 })
        {
            doc.Camera.OffsetX = shove;
            doc.ClampCamera(vpW, vpH);
            double left = bounds.X * z + doc.Camera.OffsetX;
            double right = (bounds.X + bounds.W) * z + doc.Camera.OffsetX;
            Assert.True(left <= 1e-6, $"block left edge {left} should sit at/left of the viewport left");
            Assert.True(right >= vpW - 1e-6, $"block right edge {right} should sit at/right of the viewport right");
        }
        doc.Dispose();
    }

    [Fact]
    public void CurrentFocusBlockIndex_AnalysisAbsentStaysConfined_ResidentOutOfRangeUnconfined()
    {
        // A live portal must NOT silently un-confine just because its page analysis isn't resident.
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        var doc = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(),
            new AppConfig().ToCoreSettings(), new SynchronousThreadMarshaller());
        doc.LoadPageBitmap();   // page 0, NO analysis seeded

        // Analysis absent → page-match defaults to CONFINED.
        doc.Primary.Focus = new FocusBlock(0, 3, new BBox(0, 0, 10, 10));
        Assert.Equal(3, doc.Primary.CurrentFocusBlockIndex);

        // Seat an analysis with only 2 blocks → index 3 now resident-but-out-of-range → UNCONFINED.
        var blocks = new List<LayoutBlock>();
        for (int i = 0; i < 2; i++)
            blocks.Add(new LayoutBlock { BBox = new BBox(0, i * 20, 100, 18), Role = BlockRole.Text, Lines = [] });
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, new PageAnalysis { Blocks = blocks, PageWidth = 612, PageHeight = 792 });
        Assert.Null(doc.Primary.CurrentFocusBlockIndex);
        doc.Dispose();
    }

    [Fact]
    public void CurrentPage_RefusedWhileConfined()
    {
        // The CurrentPage setter is the deepest confinement chokepoint (catches the Primary-facade path).
        var doc = LoadDoc();
        doc.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 10, 10));
        Assert.Equal(0, doc.Primary.CurrentFocusBlockIndex);   // confined (page 0 seeded with 8 blocks)

        doc.Primary.CurrentPage = 1;                 // refused
        Assert.Equal(0, doc.Primary.CurrentPage);

        doc.Primary.Focus = null;                    // un-pin → page changes allowed again
        doc.Primary.CurrentPage = 1;
        Assert.Equal(1, doc.Primary.CurrentPage);
        doc.Dispose();
    }

    [Fact]
    public void ClampCamera_NoFocus_IsUnchangedWholePageClamp()
    {
        // A viewport with no focus clamps to the page exactly as before: a page smaller than the
        // viewport is centred.
        var doc = LoadDoc();
        const double vpW = 2000, vpH = 2000;
        doc.Camera.Zoom = 0.5;
        doc.ClampCamera(vpW, vpH);

        double scaledW = doc.PageWidth * doc.Camera.Zoom;
        Assert.Equal((vpW - scaledW) / 2.0, doc.Camera.OffsetX, precision: 1);
        doc.Dispose();
    }

    [Fact]
    public void Focus_GetFitRect_ReturnsBlockBounds()
    {
        // Fit/centre operations (FitPage, FitWidth, margin-crop) read GetFitRect — a confined view must
        // return the focus block so reset-zoom re-frames the block instead of the whole page.
        var doc = LoadDoc();
        var bounds = new BBox(100, 120, 240, 90);
        doc.Primary.Focus = new FocusBlock(0, 3, bounds);

        var (x, y, w, h) = doc.Primary.GetFitRect();
        Assert.Equal(bounds.X, x, precision: 3);
        Assert.Equal(bounds.Y, y, precision: 3);
        Assert.Equal(bounds.W, w, precision: 3);
        Assert.Equal(bounds.H, h, precision: 3);
        doc.Dispose();
    }

    [Fact]
    public void ClampCamera_Focus_ConfinesOnBothAxesRegardlessOfRail()
    {
        // ClampCameraToBlock owns the block bound on both axes whether or not rail is active — there's no
        // rail-active escape hole. A manual offset outside the block is pulled back to the block bound in
        // both rail states (the block sits right of the page origin, so its max OffsetX is −b.X·z < 0).
        var doc = LoadDoc();
        double pw = doc.PageWidth, ph = doc.PageHeight;
        var bounds = new BBox((float)(pw * 0.4), (float)(ph * 0.4), (float)(pw * 0.2), (float)(ph * 0.1));
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);
        const double vpW = 400, vpH = 400;
        doc.Camera.Zoom = doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH) * 2.0; // block + page both overflow

        foreach (bool railActive in new[] { false, true })
        {
            doc.Primary.Rail.Active = railActive;
            doc.Camera.OffsetX = 0;   // outside the block range (max = −b.X·z < 0)
            doc.ClampCamera(vpW, vpH);
            Assert.True(doc.Camera.OffsetX < 0, $"focus clamp should confine to the block (rail={railActive})");
        }
        doc.Dispose();
    }

    [Fact]
    public void Focus_FitPage_DoesNotExceedZoomMax()
    {
        // GetFitRect returns a tiny focus block; FitPage/CenterPage must clamp the resulting zoom to
        // ZoomMax (the block clamp only ever raises zoom, so an overshoot would be unrecoverable).
        var doc = LoadDoc();
        var tiny = new BBox(100, 100, 6, 3);   // ~6×3pt → raw fit zoom far above ZoomMax
        doc.Primary.Focus = new FocusBlock(0, 0, tiny);
        doc.Primary.SetSize(400, 300);
        doc.Primary.CenterPage(400, 300);
        Assert.True(doc.Camera.Zoom <= Camera.ZoomMax + 1e-9, $"zoom {doc.Camera.Zoom} exceeded ZoomMax");
        doc.Dispose();
    }

    [Fact]
    public void ClampCamera_Focus_AcrossFitBoundary_DoesNotThrow()
    {
        // Regression: at the zoom where the block's scaled W/H crosses the viewport size, the branch test
        // (b.W·z) and the clamp bounds ((b.X+b.W)·z − b.X·z) could disagree by a float ULP, leaving
        // min>max → Math.Clamp throws. Sweep the zoom across both crossings (the original crash geometry).
        var doc = LoadDoc();
        var bounds = new BBox(66, 72, 484, 204);
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);
        doc.Primary.Rail.Active = false;
        const double vpW = 392.5, vpH = 392.5;

        for (int i = 0; i < 4000; i++)
        {
            doc.Camera.Zoom = 0.5 + i * 0.001; // 0.5 → ~4.5; crosses 484·z≈vp (z≈0.81) and 204·z≈vp (z≈1.92)
            doc.Camera.OffsetX = 12.3;
            doc.Camera.OffsetY = -45.6;
            Assert.Null(Record.Exception(() => doc.ClampCamera(vpW, vpH)));
        }
        doc.Dispose();
    }

    // ---- Setter / ReapplyFocus state teardown (xhigh review) -----------------------------------------

    [Fact]
    public void ReapplyFocus_ClearsForcedActivation()
    {
        // A forced ("start rail here") low-zoom session must not survive a confine/un-confine cycle —
        // ReapplyFocus clears it (mirrors SetAnalysis), else rail stays stuck active below threshold.
        var nav = new RailNav(new AppConfig().ToCoreSettings());
        nav.SetAnalysis(MakeAnalysis(3), new HashSet<BlockRole> { TextRole });
        nav.ForceActivateAt(100, 80);   // point inside block 0
        Assert.True(nav.ForceActive);

        nav.ReapplyFocus(1);
        Assert.False(nav.ForceActive);
    }

    // LoadDoc + seat the PRIMARY rail with the same cached page-0 analysis, so rail navigation / auto-scroll
    // actually run (LoadDoc only fills the document analysis cache, not the rail's seated analysis).
    private static DocumentModel LoadDocSeatedRail()
    {
        var doc = LoadDoc();
        Assert.True(doc.TryGetAnalysis(0, doc.DefaultAnalysisParams, out var a));
        doc.Primary.Rail.SetAnalysis(a, new HashSet<BlockRole> { BlockRole.Text });
        return doc;
    }

    [Fact]
    public void Focus_PinWhileAutoScrolling_StopsAutoScroll()
    {
        var doc = LoadDocSeatedRail();
        doc.Primary.SetSize(400, 400);
        doc.Primary.Rail.Active = true;
        doc.Primary.Rail.StartAutoScroll(10.0);
        Assert.True(doc.Primary.Rail.AutoScrolling);

        // A real Focus pin tears down in-flight state, including auto-scroll (state machine cleared).
        doc.Primary.Focus = new FocusBlock(0, 1, new BBox(100, 100, 200, 80));
        Assert.False(doc.Primary.Rail.AutoScrolling);
        doc.Dispose();
    }

    [Fact]
    public void Focus_RepinSameLogicalBlock_SkipsTeardownButStoresLatestBounds()
    {
        var doc = LoadDocSeatedRail();
        doc.Primary.SetSize(400, 400);
        doc.Primary.Focus = new FocusBlock(0, 1, new BBox(100, 100, 200, 80));
        doc.Primary.Rail.Active = true;
        doc.Primary.Rail.StartAutoScroll(10.0);
        Assert.True(doc.Primary.Rail.AutoScrolling);

        // Re-pinning the SAME logical block (page + index) with float-jittered bounds must NOT re-run the
        // teardown (auto-scroll keeps running) — but must still store the refined bounds for the live clamp.
        var jittered = new BBox(100.0001f, 100, 200, 80);
        doc.Primary.Focus = new FocusBlock(0, 1, jittered);
        Assert.True(doc.Primary.Rail.AutoScrolling);
        Assert.Equal(jittered, doc.Primary.Focus!.Bounds);
        doc.Dispose();
    }

    [Fact]
    public void Focus_RepinSameBlockMovedBounds_ReclampsToNewBoundsWithoutTeardown()
    {
        // Review fix C: a same-logical re-pin whose bounds MATERIALLY moved (not just ULP jitter) must still
        // re-clamp the camera to track the new rectangle, while skipping the rail/auto-scroll teardown.
        var doc = LoadDocSeatedRail();
        doc.Primary.SetSize(400, 400);

        var a = new BBox(50, 50, 80, 40);
        doc.Primary.Focus = new FocusBlock(0, 1, a);
        doc.Primary.Rail.Active = true;
        doc.Primary.Rail.StartAutoScroll(10.0);
        Assert.True(doc.Primary.Rail.AutoScrolling);
        double offsetXForA = doc.Camera.OffsetX;

        // Same logical block (page 0, index 1) but bounds moved far across the page.
        var b = new BBox(400, 500, 80, 40);
        doc.Primary.Focus = new FocusBlock(0, 1, b);

        Assert.True(doc.Primary.Rail.AutoScrolling);          // teardown skipped (no auto-scroll stop)
        Assert.Equal(b, doc.Primary.Focus!.Bounds);           // latest bounds stored
        Assert.NotEqual(offsetXForA, doc.Camera.OffsetX);     // camera re-clamped to track the moved bounds
        doc.Dispose();
    }

    [Fact]
    public void Focus_CenterPage_FramesBlockWithMargin_MatchingClampFloor()
    {
        // Issue #81 item F: CenterPage's confined fit routes through ComputeBlockFitZoom (the shared
        // 8%-margin fit, identical to the confinement clamp's floor) instead of a hand-rolled no-margin
        // Math.Min(...). FitPage then frames the block the same way the host framing animation does, and
        // the post-fit ClampCameraToBlock (which only raises zoom) finds it already at the floor.
        var doc = LoadDoc();
        var bounds = new BBox(100, 120, 200, 80);
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);
        const double vpW = 400, vpH = 400;

        doc.Primary.CenterPage(vpW, vpH);

        double marginedFit = doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH);
        double noMarginFit = Math.Min(vpW / bounds.W, vpH / bounds.H);
        Assert.Equal(marginedFit, doc.Camera.Zoom, precision: 4);   // margined fit, in one place
        Assert.True(marginedFit < noMarginFit);                     // strictly below the old edge-to-edge fit

        // The block is centred within the margin (same offsets ClampCameraToBlock centres to), so a
        // following clamp leaves the camera put — no rebound between FitPage and the clamp.
        double offX = doc.Camera.OffsetX, offY = doc.Camera.OffsetY;
        doc.ClampCamera(vpW, vpH);
        Assert.Equal(offX, doc.Camera.OffsetX, precision: 3);
        Assert.Equal(offY, doc.Camera.OffsetY, precision: 3);
        Assert.Equal(marginedFit, doc.Camera.Zoom, precision: 4);
        doc.Dispose();
    }

    [Fact]
    public void Focus_CenterPage_OversizedBlock_ShownWholeBelowZoomMin()
    {
        // Issue #81 item F regression: a focus block larger than the viewport (whose 8%-margin fit falls
        // below Camera.ZoomMin) must be framed WHOLE, not raised to ZoomMin and cropped. CenterPage,
        // ConfinedZoomFloor, and ClampCameraToBlock all floor at the block's true (sub-ZoomMin) fit, so the
        // whole block shows and the post-fit clamp doesn't rebound it back to ZoomMin.
        var doc = LoadDoc();
        var bounds = new BBox(0, 0, 600, 760);              // bigger than a 400×400 viewport even at ZoomMin
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);
        const double vpW = 400, vpH = 400;

        double trueFit = doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH, floorAtZoomMin: false);
        Assert.True(trueFit < Camera.ZoomMin);              // genuinely oversized
        // The default (ZoomMin-floored) fit would crop — confirm the two diverge so the test is meaningful.
        Assert.Equal(Camera.ZoomMin, doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH), precision: 4);

        doc.Primary.CenterPage(vpW, vpH);
        Assert.Equal(trueFit, doc.Camera.Zoom, precision: 4);   // shown whole, below ZoomMin

        // The confinement clamp must NOT raise it back to ZoomMin (no rebound on the next pan/zoom/resize).
        doc.ClampCamera(vpW, vpH);
        Assert.Equal(trueFit, doc.Camera.Zoom, precision: 4);
        // And the confined zoom-out floor is the true fit, so a host zoom gesture can reach it.
        Assert.Equal(trueFit, doc.Primary.ConfinedZoomFloor(vpW, vpH), precision: 4);
        doc.Dispose();
    }

    [Fact]
    public void Focus_PinWithStaleSamePageAnalysis_ReseatsAndCollapsesRail()
    {
        // Issue #81 item G: when the rail is seated on a DIFFERENT analysis instance for the SAME page than
        // the one resident in the cache (a re-analysis replaced the entry, or a stale params variant),
        // pinning Focus must reseat onto the resident instance AND collapse the navigable set — not leave
        // it un-collapsed (rail roaming other on-page blocks while the camera is pinned to the focus block).
        var doc = LoadDocSeatedRail();                       // rail seated on instance A == cached cur
        doc.Primary.SetSize(400, 400);
        Assert.True(doc.TryGetAnalysis(0, doc.DefaultAnalysisParams, out var a));
        Assert.Same(a, doc.Primary.Rail.Analysis);
        Assert.Equal(8, doc.Primary.Rail.NavigableCount);    // full navigable set before pinning

        // Replace the cached analysis for page 0 with a fresh, equivalent instance B, leaving the rail
        // seated on the now-stale instance A (the bug's trigger: Rail.Analysis != resident analysis).
        var b = new PageAnalysis { Blocks = a.Blocks, PageWidth = a.PageWidth, PageHeight = a.PageHeight };
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, b);
        Assert.NotSame(b, doc.Primary.Rail.Analysis);        // rail is stale (still on A)

        doc.Primary.Focus = new FocusBlock(0, 1, new BBox(0, 20, 100, 18));

        Assert.Equal(1, doc.Primary.CurrentFocusBlockIndex);
        Assert.Equal(1, doc.Primary.Rail.NavigableCount);    // collapsed to the one focus block
        Assert.Same(b, doc.Primary.Rail.Analysis);           // reseated onto the resident instance
        Assert.True(doc.Primary.Rail.TrySetCurrentByPageIndex(1)); // and it is the navigable block
        doc.Dispose();
    }

    [Fact]
    public void Focus_PinBlockWithNoLines_DoesNotThrow()
    {
        // Issue #81 regression: pinning Focus now unconditionally reseats+collapses the rail and, when the
        // block-fit zoom crosses the rail threshold, activates rail and snaps to the current line. A focus
        // block carrying ZERO detected lines (a directly-built analysis, or a visual block line detection
        // found nothing in — confinement force-includes the focus block even when its role isn't navigable)
        // must not crash: CurrentLineInfo synthesises a full-block line instead of indexing Lines[-1].
        // Before the fix this threw ArgumentOutOfRangeException on StartSnap → ComputeTargetCamera.
        var doc = LoadDoc();
        // A small block so its fit-zoom clamps well above RailZoomThreshold (3.0): pinning activates rail
        // and runs the StartSnap → ComputeTargetCamera → CurrentLineInfo path that indexed Lines[-1].
        var bounds = new BBox(300, 300, 20, 10);
        doc.SetAnalysis(0, doc.DefaultAnalysisParams, new PageAnalysis
        {
            Blocks = [new LayoutBlock { BBox = bounds, Role = BlockRole.Text, Order = 0, Lines = [] }],
            PageWidth = 612,
            PageHeight = 792,
        });
        doc.Primary.SetSize(400, 400);

        doc.Primary.Focus = new FocusBlock(0, 0, bounds);   // must not throw

        Assert.Equal(0, doc.Primary.CurrentFocusBlockIndex);
        Assert.True(doc.Primary.Rail.Active);               // fit-zoom crossed the threshold → rail engaged
        // CurrentLineInfo returns the synthesised full-block line (full width, vertical centre).
        var line = doc.Primary.Rail.CurrentLineInfo;
        Assert.Equal(bounds.X, line.X, precision: 3);
        Assert.Equal(bounds.W, line.Width, precision: 3);
        Assert.Equal(bounds.Y + bounds.H / 2f, line.Y, precision: 3);
        doc.Dispose();
    }

    [Fact]
    public void FitWidthPreservingTop_Focus_ReassertsBlockClamp()
    {
        // Margin-crop toggle on a confined view must re-assert the block clamp (zoom floored at block fit),
        // not re-fit width to the block against a page-space top anchor.
        var doc = LoadDoc();
        var bounds = new BBox(100, 120, 200, 80);
        doc.Primary.Focus = new FocusBlock(0, 0, bounds);

        const double vpW = 400, vpH = 400;
        double fit = doc.Primary.ComputeBlockFitZoom(bounds, vpW, vpH);
        doc.Camera.Zoom = fit * 0.5;
        doc.Primary.FitWidthPreservingTop(vpW, vpH);

        Assert.True(doc.Camera.Zoom >= fit - 1e-6);
        doc.Dispose();
    }
}
