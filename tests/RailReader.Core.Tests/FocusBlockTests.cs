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
}
