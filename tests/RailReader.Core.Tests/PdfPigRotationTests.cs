using RailReader.Core.Models;
using RailReader.Renderer.PdfPigSkia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PdfPig-backend counterpart of <see cref="RotationTests"/>: letter boxes
/// must land on the rendered ink for all four /Rotate values and for
/// 90°-rotated in-content text. PdfPig returns letter geometry already in
/// the rotated display frame but as <b>oriented</b> rectangles whose
/// Left/Right can invert on rotated glyphs — the text service normalises
/// them to axis-aligned boxes. Kept PDFium-free per the backend-isolation
/// convention (see PdfPigServiceTests).
/// </summary>
public class PdfPigRotationTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "rotation", name);

    private static double InkCoverage(PdfPigSkiaPdfService service, PageText pageText, int pageIndex)
    {
        var (pageW, pageH) = service.GetPageSize(pageIndex);
        var (rgb, pixW, pixH) = service.RenderPagePixmap(pageIndex, 800);
        double scaleX = pixW / pageW, scaleY = pixH / pageH;

        var inBox = new bool[pixW * pixH];
        foreach (var b in pageText.CharBoxes)
        {
            if (b.Right <= b.Left || b.Bottom <= b.Top) continue;
            int x0 = Math.Clamp((int)(b.Left * scaleX) - 1, 0, pixW - 1);
            int x1 = Math.Clamp((int)(b.Right * scaleX) + 1, 0, pixW - 1);
            int y0 = Math.Clamp((int)(b.Top * scaleY) - 1, 0, pixH - 1);
            int y1 = Math.Clamp((int)(b.Bottom * scaleY) + 1, 0, pixH - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    inBox[y * pixW + x] = true;
        }

        long dark = 0, darkInBox = 0;
        for (int i = 0; i < pixW * pixH; i++)
        {
            if (rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2] < 384)
            {
                dark++;
                if (inBox[i]) darkInBox++;
            }
        }
        return dark == 0 ? 0 : (double)darkInBox / dark;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CharBoxes_cover_rendered_ink_on_all_rotations(int pageIndex)
    {
        using var service = new PdfPigSkiaPdfService(FixturePath("rotate-suite.pdf"));
        var pageText = new RailReader.Core.PdfPig.PdfTextService()
            .ExtractPageText(service.PdfBytes, pageIndex);

        Assert.Contains("MARKER", pageText.Text);
        Assert.True(InkCoverage(service, pageText, pageIndex) >= 0.95,
            $"PdfPig char boxes no longer cover the rendered ink on page {pageIndex}");
    }

    [Fact]
    public void Glyph_angles_match_the_pdfium_convention()
    {
        // Same displayed-clockwise-degree convention as the PDFium backend:
        // \rotatebox{90} content = 270°, upright prose = 0°, and a /Rotate 90
        // page's upright content = 90°.
        var text = new RailReader.Core.PdfPig.PdfTextService();

        var sideways = text.ExtractPageText(File.ReadAllBytes(FixturePath("sideways-table.pdf")), 0);
        AssertMarkerAngle(sideways, "Quarter", 270f);
        AssertMarkerAngle(sideways, "upright", 0f);

        var rotated = text.ExtractPageText(File.ReadAllBytes(FixturePath("rotate-suite.pdf")), 1);
        AssertMarkerAngle(rotated, "MARKER", 90f);

        // View rotation composes: one clockwise turn makes the sideways scan upright.
        var scan = text.ExtractPageText(File.ReadAllBytes(FixturePath("landscape-scan.pdf")), 0, 1);
        AssertMarkerAngle(scan, "SCANMARK", 0f);
    }

    private static void AssertMarkerAngle(PageText pageText, string marker, float expected)
    {
        int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(mi >= 0, $"'{marker}' not found");
        foreach (var c in pageText.CharBoxes.Where(c => c.Index >= mi && c.Index < mi + marker.Length))
            Assert.Equal(expected, c.Angle);
    }

    [Fact]
    public void CharBoxes_cover_rendered_ink_for_rotated_content_without_rotate_attr()
    {
        using var service = new PdfPigSkiaPdfService(FixturePath("landscape-scan.pdf"));
        var pageText = new RailReader.Core.PdfPig.PdfTextService()
            .ExtractPageText(service.PdfBytes, 0);

        Assert.Contains("SCANMARK", pageText.Text);
        Assert.True(InkCoverage(service, pageText, 0) >= 0.95,
            "PdfPig char boxes must stay valid for 90°-rotated in-content glyphs");
    }
}
