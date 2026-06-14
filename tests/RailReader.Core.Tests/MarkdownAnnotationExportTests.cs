using RailReader.Core.Models;
using RailReader.Export;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Verifies the Markdown export surfaces every reader-visible annotation type — the
/// moderation / dissertation-markup case — not just highlights and sticky notes.
/// Exercises the pure rendering via <see cref="PageMarkdownBuilder.BuildPlainTextForPage"/>;
/// markup text-recovery (which needs char boxes) is covered separately by the highlight path.
/// </summary>
public class MarkdownAnnotationExportTests
{
    private static string Render(params Annotation[] annotations)
    {
        var pageText = new PageText("", []);
        var pa = new PageMarkdownBuilder.PageAnnotations(annotations);
        return PageMarkdownBuilder.BuildPlainTextForPage(pageText, [], pa);
    }

    [Fact]
    public void TextMarkups_RenderTypedColourMarkers_AndAttachedComments()
    {
        var md = Render(
            new UnderlineAnnotation { Color = "#00A000", Contents = "check this reference" },
            new StrikeOutAnnotation { Color = "#FF0000" },
            new SquigglyAnnotation { Color = "#0000FF", Contents = "spelling" });

        Assert.Contains("[underline: #00A000]", md);
        Assert.Contains("> — check this reference", md);   // reviewer comment on the underline
        Assert.Contains("[strikeout: #FF0000]", md);
        Assert.Contains("[squiggly: #0000FF]", md);
        Assert.Contains("> — spelling", md);
    }

    [Fact]
    public void Highlight_StillRendersHighlightMarker()
    {
        var md = Render(new HighlightAnnotation { Color = "#FFFF00" });
        Assert.Contains("[highlight: #FFFF00]", md);
    }

    [Fact]
    public void FreeText_And_Caret_RenderTheirComments()
    {
        var md = Render(
            new FreeTextAnnotation { Contents = "see rubric section 3" },
            new CaretAnnotation { Contents = "insert citation here" });

        Assert.Contains("**Comment:** see rubric section 3", md);
        Assert.Contains("**Inserted text:** insert citation here", md);
    }

    [Fact]
    public void StickyNote_RendersAsNote_FromContentsOrLegacyText()
    {
        // Legacy authored note (body in Text) and an in-PDF note (body in Contents)
        // both surface via EffectiveContents.
        var md = Render(
            new TextNoteAnnotation { Text = "well argued" },
            new TextNoteAnnotation { Contents = "needs a source" });

        Assert.Contains("**Note:** well argued", md);
        Assert.Contains("**Note:** needs a source", md);
    }

    [Fact]
    public void Drawings_SurfaceOnlyWhenTheyCarryAComment()
    {
        var md = Render(
            new RectAnnotation { Contents = "regrade this question" },
            new FreehandAnnotation()); // no comment → nothing emitted

        Assert.Contains("**Box:** regrade this question", md);
        Assert.DoesNotContain("Drawing", md);
    }
}
