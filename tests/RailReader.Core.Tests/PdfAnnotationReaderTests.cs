using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PR 1 step 3 — reading native PDF annotations back into the Core model.
/// The always-on tests round-trip through the export writer (which bakes Core
/// annotations into a real PDF); an optional test reads a genuine Acrobat
/// review PDF when present on the box.
/// </summary>
public class PdfAnnotationReaderTests
{
    private const float Tol = 0.5f;

    /// <summary>Writes the given annotations into a real PDF and reads them back.</summary>
    private static AnnotationFile RoundTripThroughPdf(Action<AnnotationFile> populate)
    {
        var srcPath = TestFixtures.GetTestPdfPath();
        var outPath = Path.Combine(Path.GetTempPath(), $"rr-annot-pdf-{Guid.NewGuid():N}.pdf");
        var factory = TestFixtures.CreatePdfFactory();
        var pdf = factory.CreatePdfService(srcPath);

        var toWrite = new AnnotationFile();
        populate(toWrite);

        try
        {
            AnnotationExportService.Export(pdf, toWrite, outPath);
            var bytes = File.ReadAllBytes(outPath);
            return new PdfAnnotationReader().Read(bytes);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void Highlight_GeometryAndColor_RoundTrip()
    {
        var file = RoundTripThroughPdf(f => f.Pages[0] =
        [
            new HighlightAnnotation
            {
                Rects = [new HighlightRect(72, 100, 200, 16)],
                Color = "#FF8800",
            },
        ]);

        Assert.True(file.Pages.ContainsKey(0));
        var hl = Assert.IsType<HighlightAnnotation>(file.Pages[0].Single());
        Assert.Equal(AnnotationSource.InPdf, hl.Source);

        var rect = hl.Rects.Single();
        Assert.Equal(72f, rect.X, Tol);
        Assert.Equal(100f, rect.Y, Tol);
        Assert.Equal(200f, rect.W, Tol);
        Assert.Equal(16f, rect.H, Tol);

        // No /AP stream on writer output → /C is readable and colour round-trips.
        Assert.NotNull(hl.ColorComponents);
        Assert.Equal("#FF8800", hl.Color);
    }

    [Fact]
    public void TextNote_PositionAndContents_RoundTrip()
    {
        var file = RoundTripThroughPdf(f => f.Pages[0] =
        [
            new TextNoteAnnotation { X = 300, Y = 150, Text = "a sticky note" },
        ]);

        var note = Assert.IsType<TextNoteAnnotation>(file.Pages[0].Single());
        Assert.Equal(300f, note.X, Tol);
        Assert.Equal(150f, note.Y, Tol);
        // /Contents is mapped onto the base Contents field by the reader; the legacy Text
        // field stays empty for in-PDF notes. EffectiveContents (what the renderer/writer use)
        // bridges the two so the popup is non-empty. Regression for issue #34.
        Assert.Equal("a sticky note", note.Contents);
        Assert.Equal("", note.Text);
        Assert.Equal("a sticky note", note.EffectiveContents);
    }

    [Fact]
    public void MultiplePagesAndTypes_AreKeyedByPage()
    {
        var file = RoundTripThroughPdf(f =>
        {
            f.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }];
            f.Pages[2] = [new TextNoteAnnotation { X = 100, Y = 100, Text = "p3" }];
        });

        Assert.True(file.Pages.ContainsKey(0));
        Assert.True(file.Pages.ContainsKey(2));
        Assert.False(file.Pages.ContainsKey(1));
        Assert.IsType<HighlightAnnotation>(file.Pages[0].Single());
        Assert.IsType<TextNoteAnnotation>(file.Pages[2].Single());
    }

    [Theory]
    [InlineData("D:20260528100411+02'00'", 2026, 5, 28, 10, 4, 11, 2)]
    [InlineData("D:20260101000000Z", 2026, 1, 1, 0, 0, 0, 0)]
    [InlineData("D:20260315", 2026, 3, 15, 0, 0, 0, 0)]
    public void ParsePdfDate_ParsesAcrobatFormats(string input,
        int y, int mo, int d, int h, int mi, int s, int offsetHours)
    {
        var parsed = PdfAnnotationReader.ParsePdfDate(input);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromHours(offsetHours)), parsed!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParsePdfDate_ReturnsNullOnJunk(string? input)
        => Assert.Null(PdfAnnotationReader.ParsePdfDate(input));

    /// <summary>
    /// Reads the genuine Acrobat review PDF when it is present on the machine.
    /// Skips otherwise so CI/other boxes stay green.
    /// </summary>
    [RealAcrobatPdfFact]
    public void RealAcrobatPdf_ReadsReviewerComments()
    {
        var file = new PdfAnnotationReader().Read(
            File.ReadAllBytes(AnnotationTestHelpers.RealAcrobatPdfPath));

        var all = file.Pages.Values.SelectMany(p => p).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, a => Assert.Equal(AnnotationSource.InPdf, a.Source));
        Assert.All(all, a => Assert.Equal("cclohessy", a.Author));

        // Page index 1 (page 2) carries the "accuracy" highlight and a Caret.
        var page2 = file.Pages[1];
        Assert.Contains(page2, a => a is HighlightAnnotation && a.Contents == "accuracy");
        Assert.Contains(page2, a => a is CaretAnnotation);

        // Opacity comes from /CA on the real (Acrobat) annotations.
        var accuracy = page2.First(a => a.Contents == "accuracy");
        Assert.Equal(0.4f, accuracy.Opacity, 0.05f);
        Assert.NotNull(accuracy.NativeId);
        Assert.NotNull(accuracy.CreatedUtc);
    }
}
