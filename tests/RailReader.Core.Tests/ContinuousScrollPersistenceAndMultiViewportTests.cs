using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Remaining §6 coverage: persistence round-trip and multi-viewport isolation for continuous
/// scroll (docs/continuous-scroll-plan.md §5/§6). Confined-view inertness lives alongside the
/// existing <c>FocusBlockTests</c> in <see cref="FocusBlockTests"/> — see the extension there.
/// </summary>
public class ContinuousScrollPersistenceAndMultiViewportTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly AppConfig _appConfig;
    private DocumentController? _controller;

    public ContinuousScrollPersistenceAndMultiViewportTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        _appConfig = new AppConfig();
    }

    public void Dispose() => _controller?.Dispose();

    private DocumentController NewController()
    {
        _controller?.Dispose();
        var config = _appConfig.ToCoreSettings() with { ContinuousScroll = true };
        _controller = new DocumentController(config, _appConfig, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        return _controller;
    }

    [Fact]
    public void PersistenceRoundTrip_RestoresPage_AndLayoutIsDeterministic()
    {
        // Close on page 2 with a saved position...
        var c1 = NewController();
        var doc1 = c1.CreateDocument(_pdfPath);
        doc1.LoadPageBitmap();
        c1.AddDocument(doc1);
        c1.SetViewportSize(800, 600);
        c1.GoToPage(2);
        var layoutBeforeClose = doc1.PageLayout!;
        var offsetsBeforeClose = new (double, double)[layoutBeforeClose.Count];
        for (int p = 0; p < layoutBeforeClose.Count; p++)
            offsetsBeforeClose[p] = (layoutBeforeClose.Top(p), layoutBeforeClose.Left(p));
        c1.CloseDocument(0); // saves the reading position (page 2) via SaveReadingPosition

        // ...and reopen: the page restores, and PageLayout — rebuilt fresh from the same PDF and
        // the same ContinuousPageGapPts — is byte-identical (pure function of page sizes + gap).
        var c2 = NewController();
        var doc2 = c2.CreateDocument(_pdfPath);
        doc2.LoadPageBitmap();
        c2.AddDocument(doc2);
        c2.SetViewportSize(800, 600);

        Assert.Equal(2, doc2.Primary.CurrentPage);
        var layoutAfterReopen = doc2.PageLayout!;
        Assert.Equal(layoutBeforeClose.Count, layoutAfterReopen.Count);
        for (int p = 0; p < layoutAfterReopen.Count; p++)
        {
            Assert.Equal(offsetsBeforeClose[p].Item1, layoutAfterReopen.Top(p), precision: 6);
            Assert.Equal(offsetsBeforeClose[p].Item2, layoutAfterReopen.Left(p), precision: 6);
        }
    }

    [Fact]
    public void MultiViewport_ReanchoringOneView_LeavesTheOthersCameraUntouched()
    {
        var controller = NewController();
        var doc = controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        controller.AddDocument(doc);
        controller.SetViewportSize(800, 600);

        var vp1 = doc.Primary;
        var vp2 = doc.AddViewport();
        vp2.SetSize(800, 600);
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();

        var layout = doc.PageLayout!;
        double z2 = vp2.Camera.Zoom;
        vp2.Camera.OffsetY = 12345; // an arbitrary, distinguishable value
        vp2.ClampCamera(vp2.Width, vp2.Height);
        var vp2OffsetBefore = (vp2.Camera.OffsetX, vp2.Camera.OffsetY);

        // Re-anchor vp1 across a page boundary.
        double z1 = vp1.Camera.Zoom = Math.Min(0.5, vp1.Camera.Zoom);
        vp1.ClampCamera(vp1.Width, vp1.Height);
        vp1.Camera.OffsetY += -(layout.Height(0) * vp1.Camera.Zoom) - 50;
        vp1.ClampCamera(vp1.Width, vp1.Height);
        controller.FocusedViewport = vp1;
        controller.ReanchorIfNeeded(vp1, vp1.Width, vp1.Height);
        Assert.Equal(1, vp1.CurrentPage);

        // vp2's camera and anchor are completely untouched by vp1's re-anchor.
        Assert.Equal(1, vp2.CurrentPage);
        Assert.Equal(vp2OffsetBefore.Item1, vp2.Camera.OffsetX, precision: 6);
        Assert.Equal(vp2OffsetBefore.Item2, vp2.Camera.OffsetY, precision: 6);
    }

    [Fact]
    public void MultiViewport_EvictDistantPageCaches_HonoursBothViewports()
    {
        // Real cache-internals check (code review follow-up): a page must survive eviction if it's
        // within CoreSettings.PageCacheRadius of EITHER viewport, and must be evicted only when it's
        // outside BOTH — DocumentModel.AnyViewportNeeds unions every live viewport's CurrentPage, not
        // just the Primary's. Needs enough pages, and a tight enough radius, that "near vp1-only",
        // "near vp2-only" and "near neither" are three genuinely disjoint sets.
        var config = _appConfig.ToCoreSettings() with { ContinuousScroll = true, PageCacheRadius = 1 };
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var path = Path.Combine(Path.GetTempPath(), $"railreader_test_{Guid.NewGuid():N}.pdf");
        try
        {
            TestFixtures.CreateTestPdf(path, pageCount: 10);
            _controller?.Dispose();
            _controller = new DocumentController(config, _appConfig, AnnotationService.Default,
                marshaller, factory);
            var controller = _controller;
            var doc = controller.CreateDocument(path);
            doc.LoadPageBitmap();
            controller.AddDocument(doc);
            controller.SetViewportSize(800, 600);

            var vp1 = doc.Primary;   // page 0
            var vp2 = doc.AddViewport();
            vp2.SetSize(800, 600);
            vp2.CurrentPage = 5;     // far from vp1, radius 1
            vp2.LoadPageBitmap();

            // Populate real text/link cache entries directly (not via analysis) for: near vp1 only (0),
            // near vp2 only (5), and near neither (2 and 8) — extraction alone never evicts, so all
            // four are present before the eviction call below.
            foreach (int page in new[] { 0, 2, 5, 8 })
            {
                doc.GetOrExtractText(page);
                doc.GetOrExtractLinks(page);
            }
            Assert.Equal(4, doc.TextCache.Count);
            Assert.Equal(4, doc.LinkCache.Count);

            doc.EvictDistantPageCaches();

            Assert.True(doc.TextCache.ContainsKey(0), "page 0 is within radius of vp1 and must survive");
            Assert.True(doc.TextCache.ContainsKey(5), "page 5 is within radius of vp2 and must survive");
            Assert.False(doc.TextCache.ContainsKey(2), "page 2 is outside BOTH viewports' radius and must be evicted");
            Assert.False(doc.TextCache.ContainsKey(8), "page 8 is outside BOTH viewports' radius and must be evicted");

            Assert.True(doc.LinkCache.ContainsKey(0));
            Assert.True(doc.LinkCache.ContainsKey(5));
            Assert.False(doc.LinkCache.ContainsKey(2));
            Assert.False(doc.LinkCache.ContainsKey(8));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
