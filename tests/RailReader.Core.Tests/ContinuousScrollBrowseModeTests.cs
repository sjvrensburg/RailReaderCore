using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Phase 2 coverage: continuous browse mode (docs/continuous-scroll-plan.md §5 Phase 2) — the
/// document-extent clamp (I4), re-anchoring via <see cref="DocumentController.HandlePan"/> (I2),
/// and <see cref="Viewport.ResolvePoint"/> for a neighbouring page. Phases 3 (continuous rail) and
/// 4 (screenshot compositing) have their own coverage in <c>ContinuousScrollRailTests</c> and
/// <c>ContinuousScrollScreenshotTests</c> respectively.
/// </summary>
public class ContinuousScrollBrowseModeTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;
    private readonly AppConfig _appConfig;

    public ContinuousScrollBrowseModeTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath(); // 3-page synthetic PDF
        _appConfig = new AppConfig();
        var config = _appConfig.ToCoreSettings() with { ContinuousScroll = true };
        _controller = new DocumentController(config, _appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    private DocumentModel SetupDoc()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        return doc;
    }

    [Fact]
    public void ClampCamera_AtDocumentTop_OffsetYIsZero()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;
        vp.Camera.OffsetY = 100; // would be invalid without clamping down to the document top
        vp.ClampCamera(vp.Width, vp.Height);
        Assert.Equal(0.0, vp.DocumentOffsetY, precision: 6);
    }

    [Fact]
    public void ClampCamera_AtDocumentEnd_MatchesFormula()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -1_000_000; // push far past the end
        vp.ClampCamera(vp.Width, vp.Height);
        double expected = vp.Height - layout.TotalHeight * z;
        Assert.Equal(expected, vp.DocumentOffsetY, precision: 3);
    }

    [Fact]
    public void SinglePageDocument_ClampDegeneratesToPageClamp_I4()
    {
        // Compare the continuous-mode clamp against the same document opened in single-page mode:
        // for a 1-page document TotalHeight == H[0] and MaxWidth == W[0], so I4 requires the two to
        // agree exactly for any camera state.
        var singlePagePath = Path.Combine(Path.GetTempPath(), $"railreader_test_{Guid.NewGuid():N}.pdf");
        try
        {
            TestFixtures.CreateTestPdf(singlePagePath, pageCount: 1);

            var continuousDoc = _controller.CreateDocument(singlePagePath);
            continuousDoc.LoadPageBitmap();
            _controller.AddDocument(continuousDoc);
            _controller.SetViewportSize(800, 600);
            var continuousVp = continuousDoc.Primary;

            var offConfig = new AppConfig().ToCoreSettings(); // ContinuousScroll = false
            using var offController = new DocumentController(offConfig, _appConfig, AnnotationService.Default,
                new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
            var singlePageDoc = offController.CreateDocument(singlePagePath);
            singlePageDoc.LoadPageBitmap();
            offController.AddDocument(singlePageDoc);
            offController.SetViewportSize(800, 600);
            var singlePageVp = singlePageDoc.Primary;

            foreach (double offsetY in new[] { 12345.0, -12345.0, 0.0 })
            {
                continuousVp.Camera.OffsetY = offsetY;
                continuousVp.ClampCamera(continuousVp.Width, continuousVp.Height);

                singlePageVp.Camera.OffsetY = offsetY;
                singlePageVp.ClampCamera(singlePageVp.Width, singlePageVp.Height);

                Assert.Equal(singlePageVp.Camera.OffsetY, continuousVp.Camera.OffsetY, precision: 6);
            }
        }
        finally
        {
            File.Delete(singlePagePath);
        }
    }

    [Fact]
    public void HandlePan_PastPageBoundary_ChangesAnchorAndFiresPageChanged()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        double z = vp.Camera.Zoom;

        vp.Camera.Zoom = Math.Min(0.5, z);
        vp.ClampCamera(vp.Width, vp.Height);

        int pageChangedCount = 0;
        vp.PageChanged += _ => pageChangedCount++;

        // Pan far enough down that the viewport centre now sits on page 1.
        _controller.FocusedViewport = vp;
        _controller.HandlePan(0, -(layout.Height(0) * vp.Camera.Zoom) - 50);

        Assert.Equal(1, vp.CurrentPage);
        Assert.Equal(1, pageChangedCount);
    }

    [Fact]
    public void ReanchorIfNeeded_InIsolation_NeverMovesTheScreen_I2()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;

        // Zoom out enough that page 0's full height plus a chunk of page 1 both fit, then pan
        // (WITHOUT triggering the auto-reanchor built into HandlePan) so the viewport centre sits
        // over page 1, while the anchor is still page 0.
        vp.Camera.Zoom = Math.Min(0.5, vp.Camera.Zoom);
        vp.ClampCamera(vp.Width, vp.Height);
        vp.Camera.OffsetY += -(layout.Height(0) * vp.Camera.Zoom) - 50;
        vp.ClampCamera(vp.Width, vp.Height);
        Assert.Equal(0, vp.CurrentPage); // anchor hasn't moved yet — isolates the reanchor step

        var before = new (double X, double Y)[layout.Count];
        for (int p = 0; p < layout.Count; p++) before[p] = vp.PageOffset(p);

        int pageChangedCount = 0;
        vp.PageChanged += _ => pageChangedCount++;
        _controller.FocusedViewport = vp;
        _controller.ReanchorIfNeeded(vp, vp.Width, vp.Height);

        Assert.Equal(1, vp.CurrentPage);
        Assert.Equal(1, pageChangedCount);

        // I2: PageOffset(p) unchanged (to 1e-9) for every page across the re-anchor itself.
        for (int p = 0; p < layout.Count; p++)
        {
            var after = vp.PageOffset(p);
            Assert.Equal(before[p].X, after.OffsetX, precision: 6);
            Assert.Equal(before[p].Y, after.OffsetY, precision: 6);
        }
    }

    [Fact]
    public void ResolvePoint_OnNeighbourPage_ResolvesCorrectPageAndCoords()
    {
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        double z = vp.Camera.Zoom;

        // Position the anchor's camera so page 1's top lands at screen Y = 100.
        var (_, page1TopOffsetY) = vp.PageOffset(1); // = OffsetY + (Top(1)-Top(0))*z when anchor is 0
        vp.Camera.OffsetY += 100 - page1TopOffsetY;
        vp.EnsureRenderWindow(vp.Width, vp.Height);

        // A screen point 150px below where page 1 starts should resolve onto page 1, at page-local
        // Y = 150/zoom (page 1's own top).
        var (page, _, py) = vp.ResolvePoint(50, 100 + 150);
        Assert.Equal(1, page);
        Assert.Equal(150.0 / z, py, precision: 3);
    }

    [Fact]
    public void ClampCamera_HorizontalOverscroll_ClampsAgainstDocumentMaxWidth()
    {
        // §6: "horizontal clamp against MaxW" — mirrors ClampCamera_AtDocumentTop_OffsetYIsZero /
        // _AtDocumentEnd_MatchesFormula (Y axis) for X. All pages in the synthetic PDF share one
        // width, so MaxWidth == PageWidth here, but the clamp must go through the DOCUMENT-extent
        // branch (ContinuousScrollDocumentOffsetX), not the single-page one.
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;

        vp.Camera.Zoom = 3.0; // wide enough that the page no longer fits the 800px viewport
        vp.Camera.OffsetX = 1_000_000; // push far past the left edge
        vp.ClampCamera(vp.Width, vp.Height);
        Assert.Equal(0.0, vp.DocumentOffsetX, precision: 6);

        vp.Camera.OffsetX = -1_000_000; // push far past the right edge
        vp.ClampCamera(vp.Width, vp.Height);
        double expected = vp.Width - layout.MaxWidth * vp.Camera.Zoom;
        Assert.Equal(expected, vp.DocumentOffsetX, precision: 3);
    }

    [Fact]
    public void Zoom_AboutFocusOnNeighbourPage_PreservesDocumentPointUnderFocus_ThenSurvivesReanchor()
    {
        // §6 "Zoom invariance": zoom about a focus over page 1 while anchored on page 0; the
        // document point under the focus must be fixed once the animation completes, and must stay
        // fixed after a SUBSEQUENT re-anchor onto that neighbour (PreserveScreen) — proving
        // PageOffset/ResolvePoint stay self-consistent across both a zoom and an anchor change.
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        Assert.True(layout.Count >= 2);

        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z + 300; // page 0/1 boundary at screen Y = 300
        vp.ClampCamera(vp.Width, vp.Height);

        const double focusX = 400, focusY = 350; // below the boundary → on page 1
        var before = vp.ResolvePoint(focusX, focusY);
        Assert.Equal(1, before.Page); // precondition

        vp.Zoom.Start(vp, z * 1.5, focusX, focusY, vp.Width);
        Thread.Sleep(250); // past the (short) zoom animation duration
        bool cameraChanged = false, animating = false;
        vp.Zoom.Tick(vp, vp.Width, vp.Height, ref cameraChanged, ref animating);
        Assert.False(animating);
        Assert.False(vp.Zoom.IsAnimating);

        var afterZoom = vp.ResolvePoint(focusX, focusY);
        Assert.Equal(before.Page, afterZoom.Page);
        Assert.Equal(before.PageX, afterZoom.PageX, precision: 3);
        Assert.Equal(before.PageY, afterZoom.PageY, precision: 3);

        _controller.FocusedViewport = vp;
        _controller.AnchorToPage(1); // PreserveScreen re-anchor onto the very page the focus sat on
        Assert.Equal(1, vp.CurrentPage);

        var afterReanchor = vp.ResolvePoint(focusX, focusY);
        Assert.Equal(afterZoom.Page, afterReanchor.Page);
        Assert.Equal(afterZoom.PageX, afterReanchor.PageX, precision: 3);
        Assert.Equal(afterZoom.PageY, afterReanchor.PageY, precision: 3);
    }

    [Fact]
    public void HandleClick_OnNeighbourPageLink_AnchorsFirstThenResolvesTheLink()
    {
        // §6 "Hit-test on a neighbour page": HandleClick on a page-1 link, while still anchored on
        // page 0, must re-anchor to page 1 before hit-testing (links are page-local) — proven here
        // by `handled` being true at all: the link is registered ONLY on page 1, so a hit-test still
        // running against the old anchor (page 0) would find nothing. HandleClick then follows the
        // resolved link (a PageDestination) via GoToPage, same as the single-page-mode path, so the
        // final CurrentPage is the link's TARGET (2), not the page clicked on (1).
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.ClampCamera(vp.Width, vp.Height);

        doc.SetLinks(1,
        [
            new PdfLink
            {
                Rect = new RectF(50, 50, 200, 200),
                Destination = new PageDestination { PageIndex = 2 },
            }
        ]);

        var (offX, offY) = vp.PageOffset(1);
        double canvasX = 100 * z + offX, canvasY = 100 * z + offY; // page-1-local (100,100)

        _controller.FocusedViewport = vp;
        var (handled, dest) = _controller.HandleClick(canvasX, canvasY);

        Assert.True(handled, "the link must be found — proves hit-testing ran against page 1, not the stale anchor (page 0)");
        var pageDest = Assert.IsType<PageDestination>(dest);
        Assert.Equal(2, pageDest.PageIndex);
        Assert.Equal(2, vp.CurrentPage); // HandleClick followed the resolved link to its target page
    }

    [Fact]
    public void ActivateRailAt_OnNeighbourPage_AnchorsFirstThenSeatsTheRailThere()
    {
        // §6 "Hit-test on a neighbour page": ActivateRailAt on page 1 must anchor there first —
        // rail is page-local and always seats on vp.CurrentPage.
        var doc = SetupDoc();
        var vp = doc.Primary;
        var layout = doc.PageLayout!;
        double z = vp.Camera.Zoom;
        vp.Camera.OffsetY = -layout.Height(0) * z + 300;
        vp.ClampCamera(vp.Width, vp.Height);

        var block = new LayoutBlock
        {
            Role = BlockRole.Text, BBox = new BBox(72, 72, 468, 40), Confidence = 0.9f, Order = 0,
        };
        block.Lines.Add(new LineInfo(72, 16, 72, 468));
        var analysis = new PageAnalysis();
        analysis.Blocks.Add(block);
        doc.SetAnalysis(1, doc.DefaultAnalysisParams, analysis);

        var (offX, offY) = vp.PageOffset(1);
        double canvasX = 100 * z + offX, canvasY = (72 + 8) * z + offY; // inside the page-1 block/line

        _controller.FocusedViewport = vp;
        bool ok = _controller.ActivateRailAt(canvasX, canvasY);

        Assert.True(ok);
        Assert.Equal(1, vp.CurrentPage);
        Assert.True(vp.Rail.Active);
        Assert.True(vp.Rail.HasAnalysis);
    }
}
