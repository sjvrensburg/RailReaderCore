using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PR 2 step 1 — PdfAnnotationWriter.AddAuthoredAnnotations writes RailReader-authored
/// annotations into a PDF via a full FPDF_SaveAsCopy rewrite that preserves the
/// document's existing content. (Deliberately NOT an incremental update — PDFium's
/// incremental save corrupts the xref of linearised PDFs.)
/// </summary>
public class PdfAnnotationWriterTests
{
    private const float Tol = 0.5f;

    private static byte[] PlainPdfBytes() => AnnotationTestHelpers.PlainPdfBytes();

    private static AnnotationFile OnePage(params Annotation[] anns)
        => AnnotationTestHelpers.OnePage(anns);

    private static List<Annotation> ReadBack(byte[] bytes, int page = 0)
        => AnnotationTestHelpers.ReadBack(bytes, page);

    [Fact]
    public void FullRewriteSave_PreservesPreviouslyWrittenAnnotations()
    {
        var writer = new PdfAnnotationWriter();

        // First write: highlight A.
        var bytes1 = writer.AddAuthoredAnnotations(PlainPdfBytes(),
            OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }));
        Assert.Single(ReadBack(bytes1));

        // Second write onto the result: highlight B. A must survive the full rewrite.
        var bytes2 = writer.AddAuthoredAnnotations(bytes1,
            OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 200, 80, 16)] }));

        var page = ReadBack(bytes2);
        Assert.Equal(2, page.Count);
        Assert.All(page, a => Assert.IsType<HighlightAnnotation>(a));
        // Both Y positions present → A (100) preserved alongside B (200).
        var ys = page.Cast<HighlightAnnotation>().Select(h => h.Rects[0].Y).OrderBy(y => y).ToList();
        Assert.Equal(100f, ys[0], Tol);
        Assert.Equal(200f, ys[1], Tol);
    }

    [Fact]
    public void GeometryAndMetadata_RoundTripThroughWriter()
    {
        var bytes = new PdfAnnotationWriter().AddAuthoredAnnotations(PlainPdfBytes(),
            OnePage(new HighlightAnnotation
            {
                Rects = [new HighlightRect(72, 120, 200, 16)],
                Author = "reviewer",
                Contents = "needs work",
                Subject = "Comment on Text",
            }));

        var hl = Assert.IsType<HighlightAnnotation>(Assert.Single(ReadBack(bytes)));
        var r = hl.Rects.Single();
        Assert.Equal(72f, r.X, Tol);
        Assert.Equal(120f, r.Y, Tol);
        Assert.Equal(200f, r.W, Tol);
        Assert.Equal(16f, r.H, Tol);

        Assert.Equal("reviewer", hl.Author);
        Assert.Equal("needs work", hl.Contents);
        Assert.Equal("Comment on Text", hl.Subject);
        Assert.False(string.IsNullOrEmpty(hl.NativeId)); // /NM was stamped
        Assert.NotNull(hl.CreatedUtc);                    // /CreationDate was stamped
    }

    [Fact]
    public void NewSubtypes_AreWritten()
    {
        var bytes = new PdfAnnotationWriter().AddAuthoredAnnotations(PlainPdfBytes(), OnePage(
            new UnderlineAnnotation { Rects = [new HighlightRect(72, 100, 60, 12)] },
            new StrikeOutAnnotation { Rects = [new HighlightRect(72, 130, 60, 12)] },
            new TextNoteAnnotation { X = 300, Y = 100, Text = "note body" }));

        var page = ReadBack(bytes);
        Assert.Contains(page, a => a is UnderlineAnnotation);
        Assert.Contains(page, a => a is StrikeOutAnnotation);
        var note = page.OfType<TextNoteAnnotation>().Single();
        Assert.Equal("note body", note.Contents); // Text written to /Contents, read back as Contents
    }

    [Fact]
    public void FreeText_RoundTripsGeometryContentsColorAndFontSize()
    {
        // FreeText is the subtype PDFium can't author renderably on its own — it needs a
        // synthesised /DA. The writer emits /Helv + size + colour into /DA, and the reader
        // parses it back, so geometry, text, colour, and font size survive a round-trip.
        var bytes = new PdfAnnotationWriter().AddAuthoredAnnotations(PlainPdfBytes(),
            OnePage(new FreeTextAnnotation
            {
                X = 100, Y = 150, W = 220, H = 40,
                Contents = "Typewriter note",
                Color = "#C00000",
                FontSize = 18f,
            }));

        var ft = Assert.IsType<FreeTextAnnotation>(Assert.Single(ReadBack(bytes)));
        Assert.Equal(100f, ft.X, Tol);
        Assert.Equal(150f, ft.Y, Tol);
        Assert.Equal(220f, ft.W, Tol);
        Assert.Equal(40f, ft.H, Tol);
        Assert.Equal("Typewriter note", ft.Contents);
        Assert.Equal("Typewriter note", ft.EffectiveContents);
        // Colour recovered from /DA (not /C — FreeText carries no background fill).
        Assert.Equal("#C00000", ft.Color, ignoreCase: true);
        Assert.Equal(18f, ft.FontSize, Tol);
        Assert.False(string.IsNullOrEmpty(ft.NativeId)); // /NM stamped
    }

    [Fact]
    public void FreeText_EditInPlace_UpdatesDaColorAndSize()
    {
        var writer = new PdfAnnotationWriter();

        // Author + persist a FreeText (mints /NM, flips Source→InPdf).
        var file = OnePage(new FreeTextAnnotation
        {
            X = 100, Y = 150, W = 220, H = 40, Contents = "before", Color = "#000000", FontSize = 12f,
        });
        var bytes1 = writer.AddAuthoredAnnotations(PlainPdfBytes(), file);

        // Reload, edit colour/size/text, reconcile in place keyed by /NM.
        var model = new PdfAnnotationReader().Read(bytes1);
        var ft = Assert.IsType<FreeTextAnnotation>(Assert.Single(model.Pages[0]));
        ft.Contents = "after";
        ft.Color = "#0000FF";
        ft.FontSize = 20f;
        var bytes2 = writer.WriteReconciled(bytes1, model);

        var reread = Assert.IsType<FreeTextAnnotation>(Assert.Single(ReadBack(bytes2)));
        Assert.Equal("after", reread.Contents);
        Assert.Equal("#0000FF", reread.Color, ignoreCase: true);
        Assert.Equal(20f, reread.FontSize, Tol);
    }

    [Fact]
    public void Caret_IsNotCreatable_AndIsSkippedGracefully()
    {
        // PDFium cannot create caret annotations; the writer must skip (not crash, not
        // partially write). Carets are read-only — see CaretAnnotation docs.
        var bytes = new PdfAnnotationWriter().AddAuthoredAnnotations(PlainPdfBytes(),
            OnePage(new CaretAnnotation { X = 200, Y = 100, W = 8, H = 10 }));

        Assert.Empty(ReadBack(bytes));
    }

    [Fact]
    public void InPdfAnnotations_AreNotWritten()
    {
        var bytes = new PdfAnnotationWriter().AddAuthoredAnnotations(PlainPdfBytes(), OnePage(
            new HighlightAnnotation { Source = AnnotationSource.InPdf, NativeId = "existing",
                Rects = [new HighlightRect(72, 100, 60, 12)] },
            new TextNoteAnnotation { Source = AnnotationSource.RailReader, X = 200, Y = 100, Text = "mine" }));

        var note = Assert.Single(ReadBack(bytes));
        Assert.IsType<TextNoteAnnotation>(note);
    }

    [RealAcrobatPdfFact]
    public void RealAcrobatPdf_AddingAnnotationPreservesExistingForty()
    {
        var original = File.ReadAllBytes(AnnotationTestHelpers.RealAcrobatPdfPath);
        var before = new PdfAnnotationReader().Read(original);
        int beforeTotal = before.Pages.Values.Sum(p => p.Count);
        var knownId = before.Pages.Values.SelectMany(p => p).First(a => a.NativeId is not null).NativeId;

        var add = new AnnotationFile();
        add.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }];
        var updated = new PdfAnnotationWriter().AddAuthoredAnnotations(original, add);

        var after = new PdfAnnotationReader().Read(updated);
        int afterTotal = after.Pages.Values.Sum(p => p.Count);

        Assert.Equal(beforeTotal + 1, afterTotal); // 40 originals preserved + 1 new
        Assert.Contains(after.Pages.Values.SelectMany(p => p), a => a.NativeId == knownId);
    }
}
