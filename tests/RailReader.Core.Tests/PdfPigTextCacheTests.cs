using RailReader.Core.Models;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for the single-entry page memo in <c>RailReader.Core.PdfPig.PdfTextService</c>.
///
/// <para>
/// Text extraction on this backend costs roughly 0.4 s for a dense page — dominated by glyph
/// processing and word grouping, not by opening the document (measured at ~1.5% of the total).
/// <c>GetTextRangeRects</c> derives its rects from a full extraction of the same page its
/// caller has just read, which made search highlighting pay that cost twice per page. The memo
/// removes the repeat without changing what either call returns.
/// </para>
/// </summary>
public class PdfPigTextCacheTests
{
    private static RailReader.Core.PdfPig.PdfTextService Service() => new();

    [Fact]
    public void RepeatedExtractionOfOnePage_ReturnsTheSameInstance()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = Service();

        var first = svc.ExtractPageText(bytes, 0);
        var second = svc.ExtractPageText(bytes, 0);

        Assert.Same(first, second);
    }

    [Fact]
    public void DifferentPage_RotationOrDocument_IsNotServedFromTheMemo()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var otherBytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = Service();

        var page0 = svc.ExtractPageText(bytes, 0);

        Assert.NotSame(page0, svc.ExtractPageText(bytes, 1));            // different page
        Assert.NotSame(page0, svc.ExtractPageText(bytes, 0, 1));         // different rotation
        // A different document that happens to have identical content must not be served the
        // first one's text: the key is the byte array's identity, not its contents.
        Assert.NotSame(page0, svc.ExtractPageText(otherBytes, 0));
    }

    [Fact]
    public void MemoIsSingleEntry_SoAnAlternatingWalkStillReturnsCorrectText()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = Service();

        // Page 0 → 1 → 0 evicts and re-extracts; the text must still be page 0's.
        string firstPass = svc.ExtractPageText(bytes, 0).Text;
        svc.ExtractPageText(bytes, 1);
        string secondPass = svc.ExtractPageText(bytes, 0).Text;

        Assert.Equal(firstPass, secondPass);
        Assert.Contains("Page 1", firstPass);
    }

    [Fact]
    public void RangeRectsAgreeWithTheTextTheyWereDerivedFrom()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = Service();

        var text = svc.ExtractPageText(bytes, 0);
        int at = text.Text.IndexOf("Page", StringComparison.Ordinal);
        Assert.True(at >= 0);

        var rects = svc.GetTextRangeRects(bytes, 0, [(at, 4)]);

        Assert.Single(rects);
        Assert.NotEmpty(rects[0]);
        // The rect must cover the same glyphs the char boxes report for that range.
        var expected = text.CharBoxes.Where(c => c.Index >= at && c.Index < at + 4).ToList();
        Assert.NotEmpty(expected);
        var r = rects[0][0];
        Assert.Equal(expected.Min(c => c.Left), r.Left, 1f);
        Assert.Equal(expected.Max(c => c.Right), r.Right, 1f);
    }

    [Fact]
    public void RangeRectsAreUnchangedWhetherOrNotTheMemoIsWarm()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        int at = Service().ExtractPageText(bytes, 0).Text.IndexOf("test", StringComparison.Ordinal);
        Assert.True(at >= 0);

        // Cold service: rects come from a fresh parse.
        var cold = Service().GetTextRangeRects(bytes, 0, [(at, 4)]);

        // Warm service: rects come from the memo.
        var warmSvc = Service();
        warmSvc.ExtractPageText(bytes, 0);
        var warm = warmSvc.GetTextRangeRects(bytes, 0, [(at, 4)]);

        Assert.Equal(cold.Count, warm.Count);
        Assert.Equal(cold[0].Count, warm[0].Count);
        for (int i = 0; i < cold[0].Count; i++)
            Assert.Equal(cold[0][i], warm[0][i]);
    }

    [Fact]
    public void OutOfRangePage_StillReturnsEmptyAndDoesNotPoisonTheMemo()
    {
        var bytes = File.ReadAllBytes(TestFixtures.GetTestPdfPath());
        var svc = Service();

        Assert.Empty(svc.ExtractPageText(bytes, 99).Text);
        Assert.Contains("Page 1", svc.ExtractPageText(bytes, 0).Text);
    }
}
