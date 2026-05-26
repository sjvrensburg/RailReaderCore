using System.Linq;
using RailReader.Core.PdfPig;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PdfPig-backed text/link/outline services exercised against the synthetic
/// SkiaSharp-generated test PDF.
///
/// <para>
/// <b>Note on cross-backend comparison:</b> calling both <c>PdfTextService</c>
/// (PDFium-backed, in <c>RailReader.Core.Pdfium</c>) and the PdfPig services
/// in the same xUnit test process crashes the test host — likely a native-vs-
/// managed allocator interaction when PDFium's loaded shared library and
/// PdfPig's managed parser both work on the same byte array. Each backend
/// works fine on its own. Convergence-style cross-checks should live in
/// separate test assemblies or rely on golden-file output rather than
/// in-process comparison.
/// </para>
/// </summary>
public class PdfPigServiceTests
{
    [Fact]
    public void Extracts_text_from_synthetic_fixture()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var page = new PdfTextService().ExtractPageText(bytes, 0);

        Assert.False(string.IsNullOrWhiteSpace(page.Text));
        var compact = new string(page.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("Page1of3", compact);
        Assert.Contains("testparagraph", compact, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PageText_inserts_word_spaces_between_visible_glyphs()
    {
        // 0.7.2 regression guard: PdfPig.Letters has no explicit space
        // tokens, so we reconstruct them from horizontal gaps. Without
        // this, multi-word search and drag-to-copy returned garbage
        // like "Page1of3" / "testparagraphwith…".
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var page = new PdfTextService().ExtractPageText(bytes, 0);

        Assert.Contains("Page 1 of 3", page.Text);
        Assert.Contains("test paragraph", page.Text);
    }

    [Fact]
    public void PageText_inserts_newlines_between_lines()
    {
        // The fixture writes three separate baseline lines on each page;
        // the extractor should report two line breaks between them.
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var page = new PdfTextService().ExtractPageText(bytes, 0);

        Assert.Contains('\n', page.Text);
        int newlines = page.Text.Count(c => c == '\n');
        // At least two newlines (between three lines). May be more if
        // pdftoX-style line detection over-splits, which is fine for
        // selection/search.
        Assert.True(newlines >= 2, $"expected ≥2 newlines, got {newlines}");
    }

    [Fact]
    public void Charbox_indexes_are_within_text_bounds()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var page = new PdfTextService().ExtractPageText(bytes, 0);

        Assert.NotEmpty(page.CharBoxes);
        foreach (var cb in page.CharBoxes)
        {
            Assert.InRange(cb.Index, 0, page.Text.Length - 1);
            Assert.True(cb.Bottom >= cb.Top, $"Y-flip broken at char {cb.Index}");
        }
    }

    [Fact]
    public void Charbox_y_coordinates_are_within_page_height()
    {
        // US Letter from the SkiaSharp fixture is 612×792 pts.
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var page = new PdfTextService().ExtractPageText(bytes, 0);

        foreach (var cb in page.CharBoxes)
        {
            Assert.InRange(cb.Top, -5f, 800f);
            Assert.InRange(cb.Bottom, -5f, 800f);
        }
    }

    [Fact]
    public void Out_of_range_page_returns_empty()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());

        var text = new PdfTextService().ExtractPageText(bytes, 99);
        Assert.Equal("", text.Text);
        Assert.Empty(text.CharBoxes);

        var links = new PdfLinkService().ExtractPageLinks(bytes, 99);
        Assert.Empty(links);
    }

    [Fact]
    public void Range_rects_return_at_least_one_rect_for_nonempty_range()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = new PdfTextService();
        var page = svc.ExtractPageText(bytes, 0);

        Assert.True(page.Text.Length >= 5);
        var rects = svc.GetTextRangeRects(bytes, 0,
            new List<(int CharStart, int CharLength)> { (0, 5) });

        Assert.Single(rects);
        Assert.NotEmpty(rects[0]);
    }

    [Fact]
    public void Outline_extraction_on_unbookmarked_pdf_is_empty()
    {
        // SkiaSharp's PDF backend doesn't emit bookmarks; PdfPig should
        // return an empty outline without throwing.
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var outline = new PdfOutlineService().Extract(bytes);
        Assert.Empty(outline);
    }

    [Fact]
    public void Links_on_unannotated_pdf_are_empty()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var links = new PdfLinkService().ExtractPageLinks(bytes, 0);
        Assert.Empty(links);
    }

    [Fact]
    public void Each_page_extracts_its_own_text()
    {
        // Fixture creates a 3-page PDF where each page has "Page N of 3".
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = new PdfTextService();

        for (int p = 0; p < 3; p++)
        {
            var page = svc.ExtractPageText(bytes, p);
            var compact = new string(page.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            Assert.Contains($"Page{p + 1}of3", compact);
        }
    }
}
