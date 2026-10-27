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

    // A page carrying a PdfTextLayerWriter OCR layer ("Baked OCR text"),
    // generated out-of-process with the PDFium writer and embedded here as a
    // golden fixture (the two backends must not be driven in one test process).
    private const string BakedOcrPdfBase64 = "JVBERi0xLjQNCiWhs8XXDQoxIDAgb2JqDQo8PC9Qcm9kdWNlcihTa2lhL1BERiBtMTE5KT4+DQplbmRvYmoNCjIgMCBvYmoNCjw8L0NvbnRlbnRzIDE3IDAgUiAvTWVkaWFCb3hbIDAgMCA2MTIgNzkyXS9QYXJlbnQgNSAwIFIgL1Jlc291cmNlczw8L0V4dEdTdGF0ZTw8L0ZYRTEgMTUgMCBSIC9HMyAzIDAgUiA+Pi9Gb250PDwvRlhGMSA3IDAgUiA+Pi9Qcm9jU2V0Wy9QREYvVGV4dC9JbWFnZUIvSW1hZ2VDL0ltYWdlSV0+Pi9TdHJ1Y3RQYXJlbnRzIDAvVHlwZS9QYWdlPj4NCmVuZG9iag0KMyAwIG9iag0KPDwvQk0vTm9ybWFsL2NhIDE+Pg0KZW5kb2JqDQo0IDAgb2JqDQo8PC9GaWx0ZXIvRmxhdGVEZWNvZGUvTGVuZ3RoIDY2Pj5zdHJlYW0NCnicM1QwUDBQ0DVUMFAwtzRSSM7l0jM1MFJAEEHuCmgiRelc+u7GCunFXKYGCqYGCkamBgqGpgYKRalcaVwAaj8Pyw0KZW5kc3RyZWFtDQplbmRvYmoNCjUgMCBvYmoNCjw8L0NvdW50IDEvS2lkc1sgMiAwIFIgXS9UeXBlL1BhZ2VzPj4NCmVuZG9iag0KNiAwIG9iag0KPDwvUGFnZXMgNSAwIFIgL1R5cGUvQ2F0YWxvZz4+DQplbmRvYmoNCjcgMCBvYmoNCjw8L0Jhc2VGb250L1JhaWxSZWFkZXJHbHlwaGxlc3MvRGVzY2VuZGFudEZvbnRzWyA4IDAgUiBdL0VuY29kaW5nL0lkZW50aXR5LUgvU3VidHlwZS9UeXBlMC9Ub1VuaWNvZGUgMTQgMCBSIC9UeXBlL0ZvbnQ+Pg0KZW5kb2JqDQo4IDAgb2JqDQo8PC9CYXNlRm9udC9SYWlsUmVhZGVyR2x5cGhsZXNzL0NJRFN5c3RlbUluZm8gOSAwIFIgL0NJRFRvR0lETWFwIDEzIDAgUiAvRm9udERlc2NyaXB0b3IgMTAgMCBSIC9TdWJ0eXBlL0NJREZvbnRUeXBlMi9UeXBlL0ZvbnQvVyAxMiAwIFIgPj4NCmVuZG9iag0KOSAwIG9iag0KPDwvT3JkZXJpbmcoSWRlbnRpdHkpL1JlZ2lzdHJ5KEFkb2JlKS9TdXBwbGVtZW50IDA+Pg0KZW5kb2JqDQoxMCAwIG9iag0KPDwvQXNjZW50IDEwMDAvQ2FwSGVpZ2h0IDEwMDAvRGVzY2VudCAwL0ZsYWdzIDMzL0ZvbnRCQm94WyAwIDAgMTAwMCAxMDAwXS9Gb250RmlsZTIgMTEgMCBSIC9Gb250TmFtZS9SYWlsUmVhZGVyR2x5cGhsZXNzL0l0YWxpY0FuZ2xlIDAvU3RlbVYgNzAvVHlwZS9Gb250RGVzY3JpcHRvcj4+DQplbmRvYmoNCjExIDAgb2JqDQo8PC9GaWx0ZXIvRmxhdGVEZWNvZGUvTGVuZ3RoIDM5MS9MZW5ndGgxIDEyMzY+PnN0cmVhbQ0KeJztk78vQ1EUxz+n92mVhrUxvYEOFomIQQyChEFIarPI1T6eeNVGO7SbQUwGg8HQwWggMfgTDCaJzWZ9FrFImjDofb1CyyIxGJzkk9wfJ+f7vTfnIEAPuyjcpeWR0aHjlXvgDFjMFXSJ/u594BU42Qhq6xONzSeQOsiF7+n82svsDagFYNz3PZ24VQ+g9oBBv1CpqhBAnQIDQTGnoacB6goYK+hqiT66QDWa9d1tXfBSKh0DJw3US8VyBYWAc2nuQYVyaPJ/Gtms6zIFvPsJ7YWgmq+HFKlof2TPe1t6EeaHpi0xYMaigCWLY0Qsxp+2xIG8JQF4lm5gy5IEKhbjpWrpjZRRoQqNlohIy/lr+tMb/nP+Vo7A43X6fLV/8vlTn7VFVMX0QKxVFXnP7OjOj5DvK7XFIDAPBMBhc77MzNy1ZldckFkQH+TAzG6knML5cPDVJsNm+pwkQibam7VDhoxdx8mwQBbNJgFZPDR5PHaYI6BGCZ8AjzLlX81S7X/TafwNoRaVSg0KZW5kc3RyZWFtDQplbmRvYmoNCjEyIDAgb2JqDQpbIDAgMTEgMTAwMF0NCmVuZG9iag0KMTMgMCBvYmoNCjw8L0ZpbHRlci9GbGF0ZURlY29kZS9MZW5ndGggMjk+PnN0cmVhbQ0KeJwFwQcBADAIACB07vfvKyCkoUzLdlzPbwISAEMNCmVuZHN0cmVhbQ0KZW5kb2JqDQoxNCAwIG9iag0KPDwvRmlsdGVyL0ZsYXRlRGVjb2RlL0xlbmd0aCAyNzY+PnN0cmVhbQ0KeJxd0c2KgzAQB/C7TzHH9lASbW27IEJrETzsB+v2AWwyuoE1hhgPvv3ijHRhDwn8yH9gMiOK6lZZE0B8+EHVGKA1Vnsch8krhAd2xkZxAtqosIpu1TcuEkV1q+cxYF/ZdoAsA/GJnRmDn2Fz0cMDtyDevUZvbAebe1FvQdSTcz/Yow0gIc9BYxuJ4rVxb02PIKhsV2m0wYR5dy/qv8TX7BAScszNqEHj6BqFvrEdRpmUUuaQlWVZ5hFa/e89XsserfpuPMXjHDIpE5mTEtIhYe1Ze9aBVbJSUromj6RjzDqxDqwzK2W9sK6sC+m0Jq+sM7W+9rh8Ypn1c0Jq8h5toIXQVJZ5GIvPnbnBLVV0fgEd6oqhDQplbmRzdHJlYW0NCmVuZG9iag0KMTUgMCBvYmoNCjw8L0JNL05vcm1hbC9DQSAxL2NhIDE+Pg0KZW5kb2JqDQoxNiAwIG9iag0KPDwvRmlsdGVyL0ZsYXRlRGVjb2RlL0xlbmd0aCAyMjA+PnN0cmVhbQ0KeJy91E1rwkAQBuD7/or3D9jMbPYrIEITEqEgJWEPuYpfVOrBePDviwot9DCHMnjaYWD3gR3mPRsGgTC7H7Gy2JwMPTrDEs9iOoBxBeEDhKMpurFlHC4ohvXX97Bbb3fTZzOgXjXm/Hvl55E6481HdjaFR4sRLUJMyCcU3dgx2CHvUSJPmBORXSAf0Wb0pl015t9MYpEJSkxFIlNpMZXIJCWGKYlO1HI4ig5rOTaIjtNySi86pZbjnOh4LceXr5lPkMPgXcuJchqo7U+S46BWc+Q8+PNvvbkBu15b8A0KZW5kc3RyZWFtDQplbmRvYmoNCjE3IDAgb2JqDQpbIDQgMCBSICAxNiAwIFIgXQ0KZW5kb2JqDQp4cmVmDQowIDE4DQowMDAwMDAwMDAwIDY1NTM1IGYNCjAwMDAwMDAwMTcgMDAwMDAgbg0KMDAwMDAwMDA2NCAwMDAwMCBuDQowMDAwMDAwMjc5IDAwMDAwIG4NCjAwMDAwMDAzMTcgMDAwMDAgbg0KMDAwMDAwMDQ1NCAwMDAwMCBuDQowMDAwMDAwNTEwIDAwMDAwIG4NCjAwMDAwMDA1NTkgMDAwMDAgbg0KMDAwMDAwMDY5OCAwMDAwMCBuDQowMDAwMDAwODU1IDAwMDAwIG4NCjAwMDAwMDA5MjYgMDAwMDAgbg0KMDAwMDAwMTExMSAwMDAwMCBuDQowMDAwMDAxNTg4IDAwMDAwIG4NCjAwMDAwMDE2MjAgMDAwMDAgbg0KMDAwMDAwMTcyMSAwMDAwMCBuDQowMDAwMDAyMDcwIDAwMDAwIG4NCjAwMDAwMDIxMTQgMDAwMDAgbg0KMDAwMDAwMjQwNyAwMDAwMCBuDQp0cmFpbGVyDQo8PC9JbmZvIDEgMCBSIC9Sb290IDYgMCBSIC9TaXplIDE4L0lEWzxBN0MzRjM1Njg1ODI5NThFNjFEQ0E0MDBCMkI0NTBBNj48QTdDM0YzNTY4NTgyOTU4RTYxRENBNDAwQjJCNDUwQTY+XT4+DQpzdGFydHhyZWYNCjI0NDQNCiUlRU9GDQo=";

    [Fact]
    public void Baked_ocr_layer_reports_FromOcr()
    {
        var bytes = Convert.FromBase64String(BakedOcrPdfBase64);
        var svc = new PdfTextService();

        var page = svc.ExtractPageText(bytes, 0);
        var compact = new string(page.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("Baked", compact);
        Assert.True(page.FromOcr);
        Assert.True(page.IsOcrText);

        // The flag also survives a view rotation (RotateBoxes preserves it).
        var rotated = svc.ExtractPageText(bytes, 0, 1);
        Assert.True(rotated.FromOcr);
    }

    // Provenance parity (#155): the same two fixtures are asserted against the
    // PDFium backend in PdfTextLayerWriterTests — both backends apply one rule.

    [Fact]
    public void Marked_invisible_text_reports_FromOcr()
    {
        // The writer's Helvetica-fallback shape: no glyphless font, only the mark.
        var bytes = Convert.FromBase64String(OcrProvenanceFixtures.MarkedInvisibleHelveticaPdfBase64);
        var page = new PdfTextService().ExtractPageText(bytes, 0);
        var compact = new string(page.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("MarkedOCR", compact);
        Assert.True(page.FromOcr);
    }

    [Fact]
    public void Foreign_invisible_text_is_not_FromOcr()
    {
        var bytes = Convert.FromBase64String(OcrProvenanceFixtures.UnmarkedInvisibleHelveticaPdfBase64);
        var page = new PdfTextService().ExtractPageText(bytes, 0);
        var compact = new string(page.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("Fallback", compact);
        Assert.False(page.FromOcr);
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
