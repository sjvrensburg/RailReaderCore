using RailReader.Renderer.PdfPigSkia;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Locks down the switch from per-render <c>GetPageAsSKBitmap</c> to a cached
/// <see cref="SKPicture"/> display list in <see cref="PdfPigSkiaPdfService"/>.
///
/// <para>
/// The win is that content-stream parsing — the expensive half of rendering, and the half
/// that does not depend on scale — happens once per page instead of once per render. The risk
/// is that a recorded picture might sit in a different coordinate space than the direct
/// bitmap path, which would show up as shifted or rotated output on pages carrying a
/// <c>/Rotate</c> attribute. These tests compare the two paths directly, on both an upright
/// synthetic page and the rotation fixtures.
/// </para>
/// <para>
/// Like the other PdfPig tests, these deliberately do not mix with PDFium-backed services in
/// one test method (see <see cref="PdfPigServiceTests"/>).
/// </para>
/// </summary>
public class PdfPigSkiaPictureCacheTests
{
    private static string RotationFixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "rotation", name);

    /// <summary>Renders a page the old way: straight to a bitmap, no picture involved.</summary>
    private static SKBitmap RenderDirect(string path, int pageIndex, float scale)
    {
        using var doc = PdfDocument.Open(path);
        doc.AddSkiaPageFactory();
        return doc.GetPageAsSKBitmap(pageIndex + 1, scale, SKColors.White);
    }

    /// <summary>
    /// Fraction of pixels differing by more than a small per-channel tolerance. Rasterising
    /// through a replayed display list is not bit-exact against a direct render — Skia may
    /// batch and anti-alias differently — so equivalence is judged on the pixels, with a
    /// tolerance well below anything that could hide a coordinate-space error (which would
    /// misplace most of the page, not a fringe of edge pixels).
    /// </summary>
    private static double PixelDifferenceFraction(SKBitmap a, SKBitmap b, int channelTolerance = 24)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);

        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int pixels = a.Width * a.Height;
        int differing = 0;

        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (Math.Abs(pa[o] - pb[o]) > channelTolerance
                || Math.Abs(pa[o + 1] - pb[o + 1]) > channelTolerance
                || Math.Abs(pa[o + 2] - pb[o + 2]) > channelTolerance)
            {
                differing++;
            }
        }
        return (double)differing / pixels;
    }

    [Fact]
    public void PictureRender_MatchesDirectBitmapRender()
    {
        var path = TestFixtures.GetTestPdfPath();
        using var svc = new PdfPigSkiaPdfService(path);

        using var viaPicture = (PdfPigSkiaRenderedPage)svc.RenderPage(0, dpi: 96);
        using var direct = RenderDirect(path, 0, 96f / 72f);

        Assert.Equal(direct.Width, viaPicture.Width);
        Assert.Equal(direct.Height, viaPicture.Height);
        Assert.True(PixelDifferenceFraction(direct, viaPicture.Bitmap) < 0.01,
            "picture-replayed render diverges from the direct render");
    }

    [Theory]
    [InlineData("rotate-suite.pdf", 0)]
    [InlineData("rotate-suite.pdf", 1)]
    [InlineData("landscape-scan.pdf", 0)]
    [InlineData("sideways-table.pdf", 0)]
    public void PictureRender_MatchesDirectBitmapRender_OnRotatedPages(string fixture, int pageIndex)
    {
        // /Rotate is where a coordinate-space mismatch between the two paths would surface:
        // the page box and the recorded content can disagree about which way is up.
        var path = RotationFixture(fixture);
        Assert.True(File.Exists(path), $"missing fixture {path}");

        using var svc = new PdfPigSkiaPdfService(path);
        using var viaPicture = (PdfPigSkiaRenderedPage)svc.RenderPage(pageIndex, dpi: 96);
        using var direct = RenderDirect(path, pageIndex, 96f / 72f);

        Assert.Equal(direct.Width, viaPicture.Width);
        Assert.Equal(direct.Height, viaPicture.Height);
        Assert.True(PixelDifferenceFraction(direct, viaPicture.Bitmap) < 0.01,
            $"{fixture} page {pageIndex}: picture render diverges from the direct render");
    }

    [Fact]
    public void RepeatedRendersOfOnePage_AreStable()
    {
        // The second render replays the cached picture rather than re-recording it; it must
        // produce the same image, and the cached picture must not have been disposed.
        var path = TestFixtures.GetTestPdfPath();
        using var svc = new PdfPigSkiaPdfService(path);

        using var first = (PdfPigSkiaRenderedPage)svc.RenderPage(0, dpi: 96);
        using var second = (PdfPigSkiaRenderedPage)svc.RenderPage(0, dpi: 96);

        Assert.Equal(0d, PixelDifferenceFraction(first.Bitmap, second.Bitmap));
    }

    [Fact]
    public void SamePageAtDifferentScales_UsesOneRecordingAndScalesCorrectly()
    {
        var path = TestFixtures.GetTestPdfPath();
        using var svc = new PdfPigSkiaPdfService(path);

        using var small = (PdfPigSkiaRenderedPage)svc.RenderPage(0, dpi: 72);
        using var large = (PdfPigSkiaRenderedPage)svc.RenderPage(0, dpi: 144);

        // One recording serves both zoom levels — that is the whole point of the cache.
        Assert.Equal(small.Width * 2, large.Width);
        Assert.Equal(small.Height * 2, large.Height);
    }

    [Fact]
    public void CacheEvictionUnderPressure_KeepsRendersCorrect()
    {
        // More distinct pages than the cache holds, rendered twice: the second pass hits
        // evicted entries and must re-record them rather than replay a disposed picture.
        var path = Path.Combine(Path.GetTempPath(), $"railreader_pictures_{Guid.NewGuid():N}.pdf");
        TestFixtures.CreateTestPdf(path, pageCount: 12);
        try
        {
            using var svc = new PdfPigSkiaPdfService(path);

            var firstPass = new List<SKBitmap>();
            try
            {
                for (int p = 0; p < 12; p++)
                {
                    var rendered = (PdfPigSkiaRenderedPage)svc.RenderPage(p, dpi: 48);
                    firstPass.Add(rendered.Bitmap);
                }

                for (int p = 0; p < 12; p++)
                {
                    using var again = (PdfPigSkiaRenderedPage)svc.RenderPage(p, dpi: 48);
                    Assert.Equal(0d, PixelDifferenceFraction(firstPass[p], again.Bitmap));
                }
            }
            finally
            {
                foreach (var bmp in firstPass) bmp.Dispose();
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RenderPagePixmap_StillProducesAnalysisReadyRgb()
    {
        var path = TestFixtures.GetTestPdfPath();
        using var svc = new PdfPigSkiaPdfService(path);

        var (rgb, w, h) = svc.RenderPagePixmap(0, 800);

        Assert.Equal(w * h * 3, rgb.Length);
        Assert.True(w > 0 && h > 0);
        // A page of black text on white: mostly white, but not blank.
        Assert.Contains(rgb, b => b < 128);
        Assert.Contains(rgb, b => b > 200);
    }
}
