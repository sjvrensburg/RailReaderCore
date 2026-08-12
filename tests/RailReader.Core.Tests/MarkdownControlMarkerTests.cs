using SkiaSharp;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #101: PDFium's text layer signals a soft-hyphen join with an in-band U+0002, so a
/// word the producer split across a line break ("inter-" / "pretable") comes back as
/// "inter\u0002pretable". That marker is invisible, so leaking it into Markdown produces
/// output that reads as "interpretable" while containing no such string — grep, Ctrl-F, and
/// anything that quotes text back out of the export all miss it. Markdown must carry the
/// joined word, not the marker.
/// </summary>
public class MarkdownControlMarkerTests
{
    private const string Joined = "the model described here is inter\u0002pretable in practice";

    [Fact]
    public void PlainTextPage_StripsSoftHyphenJoinMarker()
    {
        var md = PageMarkdownBuilder.BuildPlainTextForPage(
            new PageText(Joined, []), [], null);

        Assert.DoesNotContain('\u0002', md);
        Assert.Contains("is interpretable in", md);
    }

    [Fact]
    public void LayoutBlocks_StripMarkersFromEveryRenderedRole()
    {
        var blocks = new List<LayoutBlock>
        {
            new() { Role = BlockRole.Heading, BBox = new BBox(0, 0, 10, 10) },
            new() { Role = BlockRole.Text, BBox = new BBox(0, 10, 10, 10) },
            new() { Role = BlockRole.Caption, BBox = new BBox(0, 20, 10, 10) },
            new() { Role = BlockRole.Table, BBox = new BBox(0, 30, 10, 10) },
            new() { Role = BlockRole.DisplayMath, BBox = new BBox(0, 40, 10, 10) },
        };
        var texts = new Dictionary<int, string>
        {
            [0] = "Inter\u0002pretable Models",
            [1] = Joined,
            [2] = "Figure 1: coun\u0002terexample",
            [3] = "col\u0002umn\theader",
            [4] = "x = inter\u0002cept",
        };

        var md = PageMarkdownBuilder.Build(
            blocks,
            new Dictionary<int, int> { [0] = 2 },
            texts,
            vlmResults: null,
            figurePaths: null);

        Assert.DoesNotContain('\u0002', md);
        Assert.Contains("Interpretable Models", md);
        Assert.Contains("is interpretable in", md);
        Assert.Contains("counterexample", md);
        Assert.Contains("column\theader", md);   // real tabs survive
        Assert.Contains("x = intercept", md);
    }

    [Fact]
    public void OutlineHeadings_AndAnnotationComments_AreAlsoStripped()
    {
        var md = PageMarkdownBuilder.BuildPlainTextForPage(
            new PageText("", []),
            [new HeadingLevelResolver.FlatOutlineEntry("Inter\u0002pretability", 0, 1)],
            new PageMarkdownBuilder.PageAnnotations(
            [
                new TextNoteAnnotation { Contents = "coun\u0002terexample?" },
                new HighlightAnnotation { Color = "#FFFF00", Contents = "re\u0002phrase" },
            ]));

        Assert.DoesNotContain('\u0002', md);
        Assert.Contains("# Interpretability", md);
        Assert.Contains("counterexample?", md);
        Assert.Contains("rephrase", md);
    }

    [Fact]
    public void SoftHyphen_AndOtherInvisibleControls_AreStrippedButLineBreaksSurvive()
    {
        var md = PageMarkdownBuilder.BuildPlainTextForPage(
            new PageText("first\u00ADline here\nsecond line", []), [], null);

        Assert.Contains("firstline here\nsecond line", md);
    }

    /// <summary>
    /// End-to-end over a real PDF: a line ending in "inter-" followed by one starting
    /// "pretable" is exactly what makes PDFium synthesise the marker, so this pins the
    /// reported repro rather than the marker's hand-written stand-in.
    /// </summary>
    [Fact]
    public async Task Export_OfHyphenWrappedWord_ContainsTheJoinedWordAndNoMarker()
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_hyphen_{Guid.NewGuid():N}.pdf");
        try
        {
            WriteHyphenWrappedPdf(path);

            // The repro's precondition: extraction really does hand the marker to the renderer.
            var extracted = new PdfTextService().ExtractPageText(File.ReadAllBytes(path), 0).Text;
            Assert.Contains('\u0002', extracted);

            var sw = new StringWriter();
            await new MarkdownExportService(TestFixtures.CreatePdfFactory())
                .ExportAsync(path, sw, new MarkdownExportOptions { EnableVlm = false });
            var md = sw.ToString();

            Assert.DoesNotContain('\u0002', md);
            Assert.Contains("interpretable", md);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void WriteHyphenWrappedPdf(string path)
    {
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 14);

        using (var canvas = doc.BeginPage(612, 792))
        {
            canvas.DrawText("The model described here is inter-", 72, 120, font, paint);
            canvas.DrawText("pretable in practice, which is the point.", 72, 140, font, paint);
            doc.EndPage();
        }
        doc.Close();
    }
}
