using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Phase 0 coverage for docs/continuous-scroll-plan.md: <see cref="CoreSettings"/> defaults
/// (I1: continuous scroll must default off) and <see cref="DocumentModel.EnsurePageLayout"/>
/// wiring against a real PDF. <see cref="ContinuousScrollRenderWindowTests"/> below covers Phase 1
/// (the render window); browse-mode re-anchoring, continuous rail, and screenshot compositing have
/// their own files — see <c>ContinuousScrollBrowseModeTests</c>, <c>ContinuousScrollRailTests</c>,
/// and <c>ContinuousScrollScreenshotTests</c>.
/// </summary>
public class ContinuousScrollTests : IDisposable
{
    private readonly DocumentModel _state;

    public ContinuousScrollTests()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _state = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config.ToCoreSettings(), marshaller);
    }

    [Fact]
    public void CoreSettings_ContinuousScroll_DefaultsOff()
    {
        var settings = new CoreSettings();
        Assert.False(settings.ContinuousScroll);
        Assert.Equal(12.0, settings.ContinuousPageGapPts);
        Assert.Equal(4, settings.ContinuousRenderWindowPages);
    }

    [Fact]
    public void PageLayout_IsNullUntilEnsured()
    {
        Assert.Null(_state.PageLayout);
        _state.EnsurePageLayout();
        Assert.NotNull(_state.PageLayout);
    }

    [Fact]
    public void EnsurePageLayout_IsIdempotent()
    {
        _state.EnsurePageLayout();
        var first = _state.PageLayout;
        _state.EnsurePageLayout();
        Assert.Same(first, _state.PageLayout);
    }

    [Fact]
    public void EnsurePageLayout_CoversEveryPage()
    {
        _state.EnsurePageLayout();
        var layout = _state.PageLayout!;
        Assert.Equal(_state.PageCount, layout.Count);
        // Every page has a positive extent and the pages are stacked in increasing order.
        for (int i = 1; i < layout.Count; i++)
            Assert.True(layout.Top(i) > layout.Top(i - 1));
    }

    [Fact]
    public void EnsurePageLayout_UsesConfiguredGap()
    {
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig().ToCoreSettings() with { ContinuousPageGapPts = 40.0 };
        var state = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config, marshaller);

        state.EnsurePageLayout();
        var layout = state.PageLayout!;
        if (layout.Count > 1)
            Assert.Equal(layout.Height(0) + 40.0, layout.Top(1), precision: 6);
    }

    public void Dispose() => _state.Dispose();
}

/// <summary>
/// Phase 1 coverage: the per-view render window (docs/continuous-scroll-plan.md §5 Phase 1).
/// Phases 2-4 (camera re-anchoring/clamp, continuous rail, screenshot compositing) are not yet
/// implemented on this branch — see the plan document's status line.
/// </summary>
public class ContinuousScrollRenderWindowTests : IDisposable
{
    private readonly DocumentModel _state;

    /// <summary>Window renders run on a background task (mirrors Prefetch/UpdateRenderDpiIfNeeded)
    /// — a test that needs to observe an actual bitmap having landed (not just the pending
    /// placeholder <see cref="VisiblePages"/> already reports synchronously) waits for
    /// <see cref="Viewport.HasPendingWindowRenders"/> to clear instead of asserting immediately.</summary>
    private static void WaitForPendingRenders(Viewport vp, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (vp.HasPendingWindowRenders && Environment.TickCount64 < deadline)
            Thread.Sleep(5);
    }

    public ContinuousScrollRenderWindowTests()
    {
        var config = new AppConfig().ToCoreSettings() with { ContinuousScroll = true };
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _state = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config, marshaller);
        _state.Primary.SetSize(800, 600);
        _state.LoadPageBitmap();
    }

    [Fact]
    public void ContinuousScroll_TrueOnViewport_WhenConfigEnabled()
    {
        Assert.True(_state.Primary.ContinuousScroll);
    }

    [Fact]
    public void SinglePageMode_VisiblePages_HasExactlyOneEntry()
    {
        var offConfig = new AppConfig().ToCoreSettings(); // ContinuousScroll = false
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        using var state = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), offConfig, marshaller);
        state.Primary.SetSize(800, 600);
        state.LoadPageBitmap();

        var visible = state.Primary.VisiblePages;
        Assert.Single(visible);
        Assert.Equal(state.Primary.CurrentPage, visible[0].Page);
        Assert.Equal(state.Primary.Camera.OffsetX, visible[0].OffsetX);
        Assert.Equal(state.Primary.Camera.OffsetY, visible[0].OffsetY);
    }

    [Fact]
    public void ResolvePoint_SinglePageMode_MatchesLegacyFormula()
    {
        var offConfig = new AppConfig().ToCoreSettings();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        using var state = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), offConfig, marshaller);
        state.Primary.SetSize(800, 600);
        state.LoadPageBitmap();

        var vp = state.Primary;
        var (page, px, py) = vp.ResolvePoint(120, 340);
        Assert.Equal(vp.CurrentPage, page);
        Assert.Equal((120 - vp.Camera.OffsetX) / vp.Camera.Zoom, px, precision: 6);
        Assert.Equal((340 - vp.Camera.OffsetY) / vp.Camera.Zoom, py, precision: 6);
    }

    [Fact]
    public void EnsureRenderWindow_AtPageBoundary_ProducesTwoVisiblePages()
    {
        var vp = _state.Primary;
        var layout = _state.PageLayout!;
        Assert.True(layout.Count >= 2, "test PDF must have at least 2 pages");

        // Scroll the anchor (page 0) camera so the viewport straddles the page 0/1 boundary:
        // put the boundary near the vertical centre of the 600px-tall viewport.
        double z = vp.Camera.Zoom;
        double boundaryDocY = layout.Height(0);
        vp.Camera.OffsetY = -boundaryDocY * z + 300; // boundary sits 300px down the viewport
        vp.EnsureRenderWindow(vp.Width, vp.Height);

        var visible = vp.VisiblePages;
        Assert.Contains(visible, v => v.Page == 0);
        Assert.Contains(visible, v => v.Page == 1);

        var p0 = visible.First(v => v.Page == 0);
        var p1 = visible.First(v => v.Page == 1);
        double expectedDelta = (layout.Top(1) - layout.Top(0)) * z;
        Assert.Equal(expectedDelta, p1.OffsetY - p0.OffsetY, precision: 3);
    }

    [Fact]
    public void EnsureRenderWindow_BitmapsPresentAfterScheduledRenders()
    {
        var vp = _state.Primary;
        var layout = _state.PageLayout!;
        if (layout.Count < 2) return;

        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);

        foreach (var v in vp.VisiblePages)
            Assert.NotNull(v.Bitmap);
    }

    [Fact]
    public void EnsureRenderWindow_EntriesBeyondWindow_AreDisposed()
    {
        // Needs enough pages that "near page 1" and "near the last page" are disjoint windows.
        var config = new AppConfig().ToCoreSettings() with { ContinuousScroll = true };
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var path = Path.Combine(Path.GetTempPath(), $"railreader_test_{Guid.NewGuid():N}.pdf");
        try
        {
            TestFixtures.CreateTestPdf(path, pageCount: 10);
            using var state = new DocumentModel(path, factory.CreatePdfService(path),
                factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config, marshaller);
            state.Primary.SetSize(800, 600);
            state.LoadPageBitmap();

            var vp = state.Primary;
            var layout = state.PageLayout!;
            double z = vp.Camera.Zoom;
            vp.Camera.OffsetY = -layout.Height(0) * z + 300;
            vp.EnsureRenderWindow(vp.Width, vp.Height);
            Assert.Contains(vp.VisiblePages, v => v.Page == 1);
            // Let page 1's background render actually land before moving on — otherwise the
            // eviction below would just clear a pending placeholder instead of exercising the
            // WindowEntry.Dispose() path this test is named for, and a late-arriving completion
            // could re-insert page 1 into _renderWindow after the assertion below (see the
            // EnsureRenderWindow doc comment on tolerated one-frame staleness).
            WaitForPendingRenders(vp);

            // Re-anchor to the last page and scroll there too — page 1 should no longer be wanted.
            vp.CurrentPage = layout.Count - 1;
            vp.LoadPageBitmap();
            vp.Camera.OffsetY = -(layout.Height(layout.Count - 1) / 2.0) * z;
            vp.EnsureRenderWindow(vp.Width, vp.Height);
            Assert.DoesNotContain(vp.VisiblePages, v => v.Page == 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CachedPage_IsAnchorsBitmap_InContinuousMode()
    {
        var vp = _state.Primary;
        Assert.NotNull(vp.CachedPage);
        var anchorEntry = vp.VisiblePages.First(v => v.Page == vp.CurrentPage);
        Assert.Same(vp.CachedPage, anchorEntry.Bitmap);
    }

    [Fact]
    public void EnsureRenderWindow_AfterZoomIncrease_ReRendersVisiblePageAtHigherDpi()
    {
        // §6 "Render window": "DPI re-render after a zoom applies to every visible page" —
        // exercised here for a representative neighbour (the same DPI-hysteresis staleness check
        // EnsureRenderWindow applies to every wanted page).
        var vp = _state.Primary;
        var layout = _state.PageLayout!;
        if (layout.Count < 2) return;

        double z1 = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z1 + 300;
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);
        int dpiBefore = vp.VisiblePages.First(v => v.Page == 1).Dpi;
        Assert.True(dpiBefore > 0);

        double z2 = z1 * 3.0; // well past any DPI hysteresis band
        vp.Camera.Zoom = z2;
        vp.Camera.OffsetY = -layout.Height(0) * z2 + 300; // keep the same boundary on screen
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);

        var after = vp.VisiblePages.First(v => v.Page == 1);
        Assert.NotNull(after.Bitmap);
        Assert.True(after.Dpi > dpiBefore, $"expected a higher DPI after zooming in ({dpiBefore} -> {after.Dpi})");
    }

    [Fact]
    public void ViewRotationChange_DropsOldFrameBitmap_AndRendersAFreshOneInTheNewFrame()
    {
        // §6 "Render window": "rotation/quality change clears and re-renders". Asserted via bitmap
        // IDENTITY rather than "the window must be momentarily empty" — OnViewRotationChanged's own
        // LoadPageBitmap call already re-triggers EnsureRenderWindow internally (using the
        // not-yet-reclamped camera), so a placeholder/entry for page 1 can legitimately already be
        // back by the time control returns here. What must hold is that whatever ends up cached for
        // page 1 afterward is NOT the pre-rotation (old-frame) bitmap.
        var vp = _state.Primary;
        var layout = _state.PageLayout!;
        if (layout.Count < 2) return;

        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);
        var before = vp.VisiblePages.First(v => v.Page == 1);
        Assert.NotNull(before.Bitmap);

        _state.ViewRotation = 1;
        WaitForPendingRenders(vp);

        var newLayout = _state.PageLayout!;
        Assert.NotSame(layout, newLayout); // I7: one layout per (document, ViewRotation)

        // Re-establish the same boundary-at-300px framing in the (possibly W/H-swapped) new frame.
        vp.Camera.OffsetY = -newLayout.Height(0) * vp.Camera.Zoom + 300;
        vp.ClampCamera(vp.Width, vp.Height);
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);

        var after = vp.VisiblePages.First(v => v.Page == 1);
        Assert.NotNull(after.Bitmap);
        Assert.NotSame(before.Bitmap, after.Bitmap);
    }

    [Fact]
    public void OnRenderQualityChanged_DropsOldBitmap_AndRendersAFreshOneAtTheNewDpiBand()
    {
        // §6 "Render window": "rotation/quality change clears and re-renders" (quality half). Same
        // identity-based assertion as the rotation test above, for the same reason:
        // UpdateRenderDpiIfNeeded (called from OnRenderQualityChanged) also re-triggers
        // EnsureRenderWindow internally before control returns here.
        var vp = _state.Primary;
        var layout = _state.PageLayout!;
        if (layout.Count < 2) return;

        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);
        var before = vp.VisiblePages.First(v => v.Page == 1);
        Assert.NotNull(before.Bitmap);

        _state.OnRenderQualityChanged(RenderDpiSettings.ForPreset(RenderQuality.Ultra));
        WaitForPendingRenders(vp);
        vp.EnsureRenderWindow(vp.Width, vp.Height);
        WaitForPendingRenders(vp);

        var after = vp.VisiblePages.First(v => v.Page == 1);
        Assert.NotNull(after.Bitmap);
        Assert.NotSame(before.Bitmap, after.Bitmap);
    }

    [Fact]
    public void ModeSwitch_OnEnteringContinuous_SeedsWindow_AndOnLeaving_DisposesExtras()
    {
        var config = new AppConfig().ToCoreSettings(); // starts off
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        using var state = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config, marshaller);
        state.Primary.SetSize(800, 600);
        state.LoadPageBitmap();

        Assert.False(state.Primary.ContinuousScroll);
        Assert.Single(state.Primary.VisiblePages);

        // Mirrors DocumentController.OnConfigChanged's real call order: the document-level config
        // flips FIRST — Viewport.ContinuousScroll reads Owner.Config, not OnScrollModeChanged's own
        // bool parameter, so calling OnScrollModeChanged(true) before this would make EnsureRenderWindow
        // (which itself gates on ContinuousScroll) a no-op and ClampCamera take the page clamp, not the
        // document one — exactly the gap that made this test previously pass without exercising either.
        var onConfig = config with { ContinuousScroll = true };
        state.UpdateBackgroundSettings(onConfig);
        state.Primary.OnScrollModeChanged(true);

        Assert.NotNull(state.PageLayout); // EnsurePageLayout ran
        Assert.True(state.Primary.ContinuousScroll);
        // Every visible-page entry is a real, in-range page — proves EnsureRenderWindow actually ran
        // (rather than the pre-fix no-op) instead of just leaving the single legacy entry in place.
        Assert.All(state.Primary.VisiblePages, v => Assert.InRange(v.Page, 0, state.PageCount - 1));
        Assert.Contains(state.Primary.VisiblePages, v => v.Page == state.Primary.CurrentPage);

        state.UpdateBackgroundSettings(config); // back off
        state.Primary.OnScrollModeChanged(false);

        Assert.False(state.Primary.ContinuousScroll);
        // Leaving continuous mode disposes every window entry and falls back to the single anchor —
        // the "extras disposed" half of this test's name.
        Assert.Single(state.Primary.VisiblePages);
        Assert.NotNull(state.Primary.CachedPage);
    }

    public void Dispose() => _state.Dispose();
}
