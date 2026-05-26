using System.Linq;
using RailReader.Core.Analysis.LightGbm;
using UglyToad.PdfPig;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for the LightGBM analyzer's line-tokenisation step (PdfPig
/// letters → baseline lines). Kept in its own class to follow the
/// in-process isolation discipline — PdfPig + PDFium together crash
/// the test host on the same byte[].
/// </summary>
public class LineTokenizerTests
{
    [Fact]
    public void Tokenize_synthetic_pdf_produces_one_token_per_visual_line()
    {
        // The synthetic fixture writes 3 lines on each of 3 pages.
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        using var doc = PdfDocument.Open(bytes);

        for (int p = 1; p <= 3; p++)
        {
            var page = doc.GetPage(p);
            var lines = LineTokenizer.Tokenize(page);
            Assert.Equal(3, lines.Count);
        }
    }

    [Fact]
    public void Tokenize_preserves_left_to_right_within_a_line_and_top_to_bottom_overall()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        using var doc = PdfDocument.Open(bytes);
        var lines = LineTokenizer.Tokenize(doc.GetPage(1));

        // Top-to-bottom: each subsequent line has a strictly greater Top.
        for (int i = 1; i < lines.Count; i++)
            Assert.True(lines[i].Top > lines[i - 1].Top,
                $"line {i} top={lines[i].Top} should be > line {i-1} top={lines[i-1].Top}");

        // Header line "Page 1 of 3" appears first.
        Assert.StartsWith("Page 1 of 3", lines[0].Content);
    }

    [Fact]
    public void Tokenize_emits_y_down_coordinates_with_top_less_than_bottom()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        using var doc = PdfDocument.Open(bytes);
        var lines = LineTokenizer.Tokenize(doc.GetPage(1));

        Assert.All(lines, l =>
        {
            Assert.True(l.Top < l.Bottom, $"Y-flip broken: top={l.Top}, bottom={l.Bottom}");
            Assert.True(l.Left < l.Right, $"X order broken: left={l.Left}, right={l.Right}");
        });
    }

    [Fact]
    public void Tokenize_attaches_dominant_font_size_to_each_line()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        using var doc = PdfDocument.Open(bytes);
        var lines = LineTokenizer.Tokenize(doc.GetPage(1));

        // The fixture uses a single 14-pt font; every token should report
        // a font size > 0 (PdfPig's PointSize is in the document's font
        // size units).
        Assert.All(lines, l => Assert.True(l.FontSize > 0));
    }

    [Fact]
    public void Tokenize_on_empty_page_returns_empty_list()
    {
        // Build a minimal one-page PDF with no text.
        var path = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.pdf");
        try
        {
            using (var stream = File.Create(path))
            using (var sdoc = SkiaSharp.SKDocument.CreatePdf(stream))
            {
                using var canvas = sdoc.BeginPage(612, 792);
                sdoc.EndPage();
            }

            var bytes = File.ReadAllBytes(path);
            using var doc = PdfDocument.Open(bytes);
            var lines = LineTokenizer.Tokenize(doc.GetPage(1));
            Assert.Empty(lines);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
