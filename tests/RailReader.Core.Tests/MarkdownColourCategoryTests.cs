using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #219: host-supplied colour-category names (yellow = "Definition") label the
/// annotations in the Markdown export; with no names the output is byte-identical.
/// </summary>
public class MarkdownColourCategoryTests
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["#FFFF00"] = "Definition",
        ["#00A000"] = "Example",
    };

    private static string Render(IReadOnlyDictionary<string, string>? names, PageText? pageText, params Annotation[] anns)
    {
        var pa = new PageMarkdownBuilder.PageAnnotations(anns) { ColourCategories = names };
        return PageMarkdownBuilder.BuildPlainTextForPage(pageText ?? new PageText("", []), [], pa);
    }

    /// <summary>One line of text, one 10-pt-wide glyph per character from x = 0.</summary>
    private static PageText Line(string text)
    {
        var boxes = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
            boxes.Add(new CharBox(i, i * 10, 0, i * 10 + 10, 12));
        return new PageText(text, boxes);
    }

    private static HighlightAnnotation HighlightOver(PageText page, string color) =>
        new() { Color = color, Rects = [new HighlightRect(0, 0, page.Text.Length * 10, 12)] };

    private static Annotation[] Mixed() =>
    [
        new HighlightAnnotation { Color = "#FFFF00", Contents = "key term" },
        new UnderlineAnnotation { Color = "#00A000" },
        new TextNoteAnnotation { Color = "#FFFF00", Contents = "define this" },
        new FreeTextAnnotation { Color = "#123456", Contents = "typed" },
        new RectAnnotation { Color = "#00A000", Contents = "boxed" },
    ];

    [Fact]
    public void NoMapping_OrEmptyMapping_OrUnmatched_IsByteIdentical()
    {
        var page = Line("covered");
        var anns = Mixed().Append(HighlightOver(page, "#FFFF00")).ToArray();
        var baseline = Render(null, page, anns);

        Assert.Equal(baseline, Render(new Dictionary<string, string>(), page, anns));
        Assert.Equal(baseline, Render(new Dictionary<string, string> { ["#ABCDEF"] = "Other" }, page, anns));
        Assert.Contains("[highlight: #FFFF00]", baseline);
        Assert.Contains("<!-- highlight: #FFFF00 -->", baseline);
    }

    [Fact]
    public void NamedHighlight_WithRecoveredText_GetsLeadInAndCommentName()
    {
        var page = Line("covered");
        var md = Render(Names, page, HighlightOver(page, "#FFFF00"));

        Assert.Contains("> **Definition:** covered\n", md.ReplaceLineEndings("\n"));
        Assert.Contains("<!-- highlight: #FFFF00 (Definition) -->", md);
    }

    [Fact]
    public void NamedMarkups_WithoutText_UseTheirOwnLabel()
    {
        var md = Render(Names, null,
            new HighlightAnnotation { Color = "#FFFF00", Contents = "why?" },
            new UnderlineAnnotation { Color = "#00A000" },
            new StrikeOutAnnotation { Color = "#FFFF00" },
            new SquigglyAnnotation { Color = "#00A000" });

        Assert.Contains("> [highlight: Definition (#FFFF00)]", md);
        Assert.Contains("> — why?", md);   // attached comment unchanged
        Assert.Contains("> [underline: Example (#00A000)]", md);
        Assert.Contains("> [strikeout: Definition (#FFFF00)]", md);
        Assert.Contains("> [squiggly: Example (#00A000)]", md);
    }

    [Fact]
    public void NamedComments_GetTheNameInTheirLabel_UnnamedLeftAlone()
    {
        var md = Render(Names, null, Mixed());

        Assert.Contains("> **Note (Definition):** define this", md);
        Assert.Contains("> **Box (Example):** boxed", md);
        Assert.Contains("> **Comment:** typed", md);   // #123456 has no name
    }

    [Theory]
    [InlineData("#ffff00", "#FFFF00")]
    [InlineData("ffff00", "#FFFF00")]
    [InlineData("#FF0", "#FFFF00")]
    [InlineData("  #ff0 ", "#FFFF00")]
    [InlineData("#FFFF00", "#80FFFF00")]   // alpha-bearing annotation colour
    [InlineData("#CCFFFF00", "#ffff00")]   // alpha-bearing key
    public void Normalisation_MatchesEquivalentSpellings(string key, string annotationColour)
    {
        var md = Render(new Dictionary<string, string> { [key] = "Definition" }, null,
            new HighlightAnnotation { Color = annotationColour });

        Assert.Contains($"[highlight: Definition ({annotationColour})]", md);
    }

    [Fact]
    public void BlankName_AndUnparseableKey_AreIgnored()
    {
        var anns = new Annotation[] { new HighlightAnnotation { Color = "#FFFF00" } };
        var baseline = Render(null, null, anns);

        Assert.Equal(baseline, Render(new Dictionary<string, string> { ["#FFFF00"] = "  " }, null, anns));
        Assert.Equal(baseline, Render(new Dictionary<string, string> { ["yellow"] = "Definition" }, null, anns));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("#GGHHII", null)]
    [InlineData("#12345", null)]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("AbCdEf", "#ABCDEF")]
    [InlineData("#FF00ff00", "#00FF00")]
    public void ColourKey_Normalise(string? input, string? expected) =>
        Assert.Equal(expected, ColourKey.Normalise(input));

    [Fact]
    public void UnsafeName_CannotBreakMarkdownOrTheHtmlComment()
    {
        const string evil = "*bold* _x_ <b> [link] -->\nnext";
        var page = Line("covered");
        var md = Render(new Dictionary<string, string> { ["#FFFF00"] = evil }, page,
            HighlightOver(page, "#FFFF00"),
            new TextNoteAnnotation { Color = "#FFFF00", Contents = "body" });

        var lines = md.ReplaceLineEndings("\n").Split('\n');
        var comment = Assert.Single(lines, l => l.StartsWith("<!--"));
        Assert.Equal("<!-- highlight: #FFFF00 (*bold* _x_ <b> [link] - -> next) -->", comment);
        Assert.Equal(1, CountOf(comment, "-->"));

        const string escaped = @"\*bold\* \_x\_ \<b\> \[link\] --\> next";
        Assert.Contains($"> **{escaped}:** covered", md);
        Assert.Contains($"> **Note ({escaped}):** body", md);
        Assert.DoesNotContain(lines, l => l.StartsWith("next"));   // the newline didn't end the quote
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
