using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Capstone slice 5: a document can own more than one <see cref="Viewport"/>, and each renders
/// and ticks independently (its own page, DPI, camera, and zoom animation). These tests exercise
/// the additive multi-viewport surface end-to-end so it is never write-only — see
/// <c>docs/multi-viewport-design.md</c>.
/// </summary>
public class MultiViewportTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;

    public MultiViewportTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath(); // 3-page synthetic PDF
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        // No analysis worker — these tests exercise rendering + camera ticking, not analysis.
    }

    public void Dispose() => _controller.Dispose();

    private DocumentState SetupDoc()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        _controller.SetViewportSize(800, 600);
        return doc;
    }

    [Fact]
    public void AddViewport_SeedsLiveViewSharingTheDocument()
    {
        var doc = SetupDoc();
        _controller.SetViewportSize(1024, 768);

        var vp2 = doc.AddViewport();

        Assert.Equal(2, doc.Viewports.Count);
        Assert.Same(doc.Primary, doc.Viewports[0]);
        Assert.Same(doc, vp2.Owner);
        Assert.True(vp2.IsLive);                 // focus-tracking default
        Assert.Contains(vp2, doc.Viewports);
        Assert.Equal(0, vp2.CurrentPage);        // a fresh view starts on page 0
        Assert.Equal(1024, vp2.Width);           // inherits the primary's (ambient) size
        Assert.Equal(768, vp2.Height);
    }

    [Fact]
    public void TwoViewports_RenderTheirOwnPageAtTheirOwnDpi()
    {
        var doc = SetupDoc();
        var vp1 = doc.Primary;
        var vp2 = doc.AddViewport();

        // Each view sits on its own page at its own zoom → its own rasterisation.
        vp1.CurrentPage = 0;
        vp1.Camera.Zoom = 1.0;
        Assert.True(vp1.LoadPageBitmap());

        vp2.CurrentPage = 1;
        vp2.Camera.Zoom = 3.0;
        Assert.True(vp2.LoadPageBitmap());

        Assert.Equal(0, vp1.CurrentPage);
        Assert.Equal(1, vp2.CurrentPage);
        Assert.NotNull(vp1.CachedPage);
        Assert.NotNull(vp2.CachedPage);
        Assert.NotSame(vp1.CachedPage, vp2.CachedPage);   // distinct bitmaps
        Assert.True(vp1.PageWidth > 0);
        Assert.True(vp2.PageWidth > 0);
        Assert.True(vp2.CachedDpi > vp1.CachedDpi);        // DPI ∝ zoom, per-view
    }

    [Fact]
    public void TwoViewports_TickIndependently()
    {
        var doc = SetupDoc();
        var vp1 = doc.Primary;
        var vp2 = doc.AddViewport();

        vp1.CurrentPage = 0;
        vp1.LoadPageBitmap();
        vp1.CenterPage(800, 600);
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        vp2.CenterPage(800, 600);

        // A distinct zoom animation per view.
        vp1.Camera.Zoom = 1.0;
        vp2.Camera.Zoom = 1.0;
        vp1.Zoom.Start(vp1, 2.0, 400, 300, 800);
        vp2.Zoom.Start(vp2, 4.0, 400, 300, 800);

        Thread.Sleep(220); // past the zoom duration so one tick completes each

        // Tick ONLY vp1: it animates to its own target and leaves vp2 untouched.
        _controller.TickViewport(vp1, 0.25);
        Assert.Equal(2.0, vp1.Camera.Zoom, 3);
        Assert.False(vp1.Zoom.IsAnimating);
        Assert.Equal(1.0, vp2.Camera.Zoom, 3);   // vp1's tick must not move vp2
        Assert.True(vp2.Zoom.IsAnimating);        // vp2's animation is still pending

        // Now tick vp2: it completes to its own, different target.
        _controller.TickViewport(vp2, 0.25);
        Assert.Equal(4.0, vp2.Camera.Zoom, 3);
        Assert.False(vp2.Zoom.IsAnimating);
        Assert.Equal(2.0, vp1.Camera.Zoom, 3);   // vp1 unchanged by vp2's tick
    }

    [Fact]
    public void RemoveViewport_ShrinksAndRejectsPrimary()
    {
        var doc = SetupDoc();
        var vp2 = doc.AddViewport();
        vp2.CurrentPage = 1;
        vp2.LoadPageBitmap();
        Assert.NotNull(vp2.CachedPage);
        Assert.Equal(2, doc.Viewports.Count);

        doc.RemoveViewport(vp2);

        Assert.Single(doc.Viewports);
        Assert.Same(doc.Primary, doc.Viewports[0]);
        // The primary view lives for the document's lifetime and cannot be removed.
        Assert.Throws<InvalidOperationException>(() => doc.RemoveViewport(doc.Primary));
    }

    [Fact]
    public void PrimaryViewport_StillDrivesTheDocumentFacade()
    {
        // Adding a second view must not change the single-viewport facade: DocumentState's
        // CurrentPage/Camera still reflect the primary view.
        var doc = SetupDoc();
        doc.AddViewport();

        doc.Primary.CurrentPage = 2;
        Assert.Equal(2, doc.CurrentPage);
        Assert.Same(doc.Primary.Camera, doc.Camera);
    }
}
