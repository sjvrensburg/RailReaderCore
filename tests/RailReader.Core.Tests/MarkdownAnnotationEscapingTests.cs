using RailReader.Core.Models;
using RailReader.Export;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Annotation text in the Markdown export must not break the export's structure: a
/// multi-line comment stays inside its blockquote, raw HTML in a comment can't hide the
/// rest of the document, and a malformed colour can't close the HTML comment.
/// </summary>
public class MarkdownAnnotationEscapingTests
{
    private static string[] Lines(PageText? pageText, params Annotation[] anns)
    {
        var md = PageMarkdownBuilder.BuildPlainTextForPage(
            pageText ?? new PageText("", []), [], new PageMarkdownBuilder.PageAnnotations(anns));
        return md.ReplaceLineEndings("\n").Split('\n');
    }

    private static PageText Line(string text)
    {
        var boxes = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
            boxes.Add(new CharBox(i, i * 10, 0, i * 10 + 10, 12));
        return new PageText(text, boxes);
    }

    [Fact]
    public void MultiLineNote_StaysInsideTheBlockquote()
    {
        var lines = Lines(null, new TextNoteAnnotation { Contents = "first point\n\n# not a heading\n- not a list\r\nlast" });

        int start = Array.FindIndex(lines, l => l.StartsWith("> **Note:**"));
        Assert.Equal(
            ["> **Note:** first point", ">", "> # not a heading", "> - not a list", "> last"],
            lines[start..(start + 5)]);
    }

    [Fact]
    public void MultiLineMarkupComment_StaysInsideTheBlockquote()
    {
        var lines = Lines(null, new HighlightAnnotation { Color = "#FFFF00", Contents = "why?\n\nexplain" });
        Assert.Contains("> — why?", lines);
        Assert.Contains(">", lines);
        Assert.Contains("> explain", lines);
        Assert.DoesNotContain("explain", lines);
    }

    [Fact]
    public void CoveredTextSpanningLines_StaysInsideTheBlockquote()
    {
        var page = Line("end of one\nstart of next");
        var lines = Lines(page, new HighlightAnnotation
        {
            Color = "#FFFF00",
            Rects = [new HighlightRect(0, 0, page.Text.Length * 10, 12)],
        });

        Assert.Contains("> end of one", lines);
        Assert.Contains("> start of next", lines);
        Assert.Contains("<!-- highlight: #FFFF00 -->", lines);
    }

    [Fact]
    public void RawHtmlInComment_IsEscaped_ButLiteralLessThanIsKept()
    {
        var lines = Lines(null,
            new TextNoteAnnotation { Contents = "<!-- unfinished <b>bold</b> <?pi" },
            new FreeTextAnnotation { Contents = "x < y and a<3" });

        Assert.Contains(@"> **Note:** \<!-- unfinished \<b>bold\</b> \<?pi", lines);
        Assert.Contains("> **Comment:** x < y and a<3", lines);
    }

    [Fact]
    public void EmphasisCharacters_AreLeftAlone()
    {
        var lines = Lines(null, new TextNoteAnnotation { Contents = "see *Smith* and file_name" });
        Assert.Contains("> **Note:** see *Smith* and file_name", lines);
    }

    [Fact]
    public void MalformedColour_CannotCloseTheHtmlCommentOrBreakTheMarker()
    {
        var page = Line("covered");
        var rects = new List<HighlightRect> { new(0, 0, 70, 12) };
        var lines = Lines(page,
            new HighlightAnnotation { Color = "red --> <b>", Rects = rects },
            new UnderlineAnnotation { Color = "x] [y" });

        Assert.Contains("<!-- highlight: red - -> <b> -->", lines);
        Assert.Contains(@"> [underline: x\] \[y]", lines);
    }

    [Fact]
    public void ValidColours_AreWrittenExactlyAsStored()
    {
        var lines = Lines(null,
            new HighlightAnnotation { Color = "#80FFFF00" },
            new UnderlineAnnotation { Color = "#00a000" });

        Assert.Contains("> [highlight: #80FFFF00]", lines);
        Assert.Contains("> [underline: #00a000]", lines);
    }

    [Fact]
    public void InvisibleOnlyComment_RendersNothing()
    {
        var lines = Lines(null, new TextNoteAnnotation { Contents = "\u0002­" });
        Assert.DoesNotContain(lines, l => l.Contains("Note"));
    }
}
