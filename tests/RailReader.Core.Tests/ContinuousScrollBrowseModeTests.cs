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
}
