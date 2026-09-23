using RailReader.Core.Services;
using RailReader.Renderer.PdfPigSkia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Renderer-level smoke tests for <see cref="PdfPigSkiaPdfService"/>
/// against the synthetic SkiaSharp-generated fixture. Like
/// <see cref="PdfPigServiceTests"/>, these intentionally do not mix with
/// PDFium-backed services in the same test method — that combination
/// crashes the test host (see PdfPigServiceTests class summary).
/// </summary>
public class PdfPigSkiaPdfServiceTests
{
    [Fact]
    public void Service_constructs_and_reports_page_count()
    {
        var path = TestFixtures.GetTestPdfPath();
        var svc = new PdfPigSkiaPdfService(path);

        Assert.Equal(3, svc.PageCount);
        Assert.NotEmpty(svc.PdfBytes);
        Assert.NotNull(svc.Outline); // empty list, not null
    }

    [Fact]
    public void GetPageSize_returns_us_letter_for_synthetic_fixture()
    {
        var path = TestFixtures.GetTestPdfPath();
        var svc = new PdfPigSkiaPdfService(path);

        var (w, h) = svc.GetPageSize(0);
        // US Letter is 612×792 pts; allow tiny rounding tolerance.
        Assert.InRange(w, 611, 613);
        Assert.InRange(h, 791, 793);
    }

    [Fact]
    public void RenderPage_returns_nonempty_bitmap()
    {
        var path = TestFixtures.GetTestPdfPath();
        var svc = new PdfPigSkiaPdfService(path);
        using var page = svc.RenderPage(0, dpi: 96);

        Assert.True(page.Width > 0);
        Assert.True(page.Height > 0);
    }

    [Fact]
    public void RenderThumbnail_fits_within_200pt_box()
    {
        var path = TestFixtures.GetTestPdfPath();
        var svc = new PdfPigSkiaPdfService(path);
        using var thumb = svc.RenderThumbnail(0);

        Assert.True(thumb.Width > 0);
        Assert.True(thumb.Height > 0);
        Assert.True(thumb.Width <= 210, $"thumb width {thumb.Width} should be ≲200");
        Assert.True(thumb.Height <= 210, $"thumb height {thumb.Height} should be ≲200");
    }

    [Fact]
    public void RenderPagePixmap_returns_rgb_buffer_with_sane_shape()
    {
        var path = TestFixtures.GetTestPdfPath();
        var svc = new PdfPigSkiaPdfService(path);
        var (rgb, w, h) = svc.RenderPagePixmap(0, targetSize: 200);

        Assert.True(w > 0 && h > 0);
        Assert.Equal(w * h * 3, rgb.Length);

        // The fixture renders black text on white — at least most pixels
        // should be near-white. Cheap sanity check.
        int brightCount = 0;
        for (int i = 0; i < rgb.Length; i += 3)
            if (rgb[i] > 200 && rgb[i + 1] > 200 && rgb[i + 2] > 200)
                brightCount++;
        int totalPixels = w * h;
        Assert.True(brightCount > totalPixels / 2,
            $"expected mostly-white pixmap; got {brightCount}/{totalPixels} bright");
    }

    [Fact]
    public void Factory_exposes_full_iface_surface()
    {
        IPdfServiceFactory factory = new PdfPigSkiaPdfServiceFactory();
        Assert.NotNull(factory.CreatePdfTextService());
        Assert.NotNull(factory.CreatePdfLinkService());

        var pdf = factory.CreatePdfService(TestFixtures.GetTestPdfPath());
        Assert.True(pdf.PageCount >= 1);
    }

    [Fact]
    public void Constructor_from_bytes_matches_constructor_from_path()
    {
        // The byte[] overload is what the Lite/web flow uses to avoid a
        // temp-file hop. Result should be identical to the path overload
        // on every observable surface.
        var path = TestFixtures.GetTestPdfPath();
        var bytes = File.ReadAllBytes(path);

        var fromPath  = new PdfPigSkiaPdfService(path);
        var fromBytes = new PdfPigSkiaPdfService(bytes);

        Assert.Equal(fromPath.PageCount, fromBytes.PageCount);
        Assert.Equal(fromPath.PdfBytes.Length, fromBytes.PdfBytes.Length);
        Assert.Equal(fromPath.Outline.Count, fromBytes.Outline.Count);

        var (w1, h1) = fromPath.GetPageSize(0);
        var (w2, h2) = fromBytes.GetPageSize(0);
        Assert.Equal(w1, w2);
        Assert.Equal(h1, h2);

        fromPath.Dispose();
        fromBytes.Dispose();
    }

    [Fact]
    public void Multiple_renders_reuse_the_cached_document()
    {
        // Sanity: rendering N pages from one instance should not blow up
        // even though the cached document is reused under the gate. This
        // is the case that used to re-parse on every call.
        var svc = new PdfPigSkiaPdfService(File.ReadAllBytes(TestFixtures.GetTestPdfPath()));
        for (int i = 0; i < svc.PageCount; i++)
        {
            var (rgb, w, h) = svc.RenderPagePixmap(i, targetSize: 200);
            Assert.True(w > 0 && h > 0);
            Assert.Equal(w * h * 3, rgb.Length);
        }
        svc.Dispose();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var svc = new PdfPigSkiaPdfService(File.ReadAllBytes(TestFixtures.GetTestPdfPath()));
        svc.Dispose();
        svc.Dispose(); // must not throw
    }
}
