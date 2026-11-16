using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// #208: annotations RailReader Core creates in a PDF carry a private /RailReader key, read back as
/// <see cref="Annotation.WrittenByRailReader"/>; other producers' annotations never get it, even when
/// the reader edits them.
/// </summary>
public class AnnotationOriginMarkTests
{
    private static readonly PdfAnnotationWriter Writer = new();

    /// <summary>A one-page PDF holding a reviewer's annotations, written by "another producer".</summary>
    private static byte[] ReviewerPdf(params string[] annots) => PdfAnnotationIndirectTests.BuildPdf(
        [$"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{string.Join(" ", annots)}] /Contents 4 0 R >>"],
        ["<< /Length 0 >>\nstream\n\nendstream"]);

    private const string ReviewerSquare =
        "<< /Type /Annot /Subtype /Square /Rect [100 600 200 700] /C [1 0 0] /NM (rev-square) /T (Reviewer) >>";
    private const string ReviewerNote =
        "<< /Type /Annot /Subtype /Text /Rect [300 700 320 720] /C [1 1 0] /NM (rev-note) /T (Reviewer) /Contents (check this) >>";

    private static AnnotationFile Page0(List<Annotation> anns) => new() { Pages = { [0] = anns } };

    private static List<Annotation> Read(byte[] bytes) => AnnotationTestHelpers.ReadBack(bytes);

    [Fact]
    public void NewAnnotation_IsMarkedInThePdfAndInTheModel()
    {
        var hl = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] };
        var saved = Writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), AnnotationTestHelpers.OnePage(hl));

        Assert.True(hl.WrittenByRailReader);
        Assert.True(Assert.Single(Read(saved)).WrittenByRailReader);
    }

    [Fact]
    public void ReviewerAnnotations_ReadUnmarked()
    {
        var read = Read(ReviewerPdf(ReviewerSquare, ReviewerNote));
        Assert.Equal(2, read.Count);
        Assert.All(read, a => Assert.False(a.WrittenByRailReader));
    }

    [Fact]
    public void EditingAReviewersAnnotation_NeverMarksIt()
    {
        var pdf = ReviewerPdf(ReviewerSquare, ReviewerNote);
        var anns = Read(pdf);
        var square = Assert.IsType<RectAnnotation>(anns.Single(a => a.NativeId == "rev-square"));
        var note = Assert.IsType<TextNoteAnnotation>(anns.Single(a => a.NativeId == "rev-note"));
        square.W += 30;                 // geometry change → delete + recreate
        note.Contents = "edited";       // rect-based → edited in place

        var saved = Writer.WriteReconciled(pdf, Page0(anns));

        var back = Read(saved);
        Assert.Equal(2, back.Count);
        Assert.All(back, a => Assert.False(a.WrittenByRailReader));
        Assert.Equal("Reviewer", back.Single(a => a.NativeId == "rev-square").Author);
        Assert.False(square.WrittenByRailReader);
    }

    [Fact]
    public void EditingOurAnnotation_KeepsTheMark_InPlaceAndOnRecreate()
    {
        var hl = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] };
        var note = new TextNoteAnnotation { X = 300, Y = 100, Text = "mine" };
        var first = Writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), AnnotationTestHelpers.OnePage(hl, note));

        var anns = Read(first);
        var readHl = Assert.IsType<HighlightAnnotation>(anns.Single(a => a is HighlightAnnotation));
        var readNote = Assert.IsType<TextNoteAnnotation>(anns.Single(a => a is TextNoteAnnotation));
        readHl.Rects = [new HighlightRect(72, 140, 120, 16)];   // recreated
        readNote.Contents = "still mine";                       // in place

        var back = Read(Writer.WriteReconciled(first, Page0(anns)));
        Assert.Equal(2, back.Count);
        Assert.All(back, a => Assert.True(a.WrittenByRailReader));
    }

    [Fact]
    public void ReplyToAReviewer_IsMarked_TheReviewerIsNot()
    {
        var pdf = ReviewerPdf(ReviewerNote);
        var anns = Read(pdf);
        anns.Add(new TextNoteAnnotation { X = 310, Y = 80, Text = "agreed", InReplyTo = "rev-note" });

        var back = Read(Writer.WriteReconciled(pdf, Page0(anns)));

        Assert.False(back.Single(a => a.NativeId == "rev-note").WrittenByRailReader);
        Assert.True(back.Single(a => a.InReplyTo == "rev-note").WrittenByRailReader);
    }

    [Fact]
    public void PastedCopyOfAReviewersAnnotation_IsMarked()
    {
        var pdf = ReviewerPdf(ReviewerSquare);
        var anns = Read(pdf);
        var copy = Annotation.CloneForPaste(anns[0]);
        Assert.False(copy.WrittenByRailReader);   // not written yet
        anns.Add(copy);

        var back = Read(Writer.WriteReconciled(pdf, Page0(anns)));

        Assert.False(back.Single(a => a.NativeId == "rev-square").WrittenByRailReader);
        Assert.True(back.Single(a => a.NativeId != "rev-square").WrittenByRailReader);
    }

    [Fact]
    public void Caret_KeepsTheMarkThroughThePostPassRetype()
    {
        var caret = new CaretAnnotation { X = 200, Y = 100, W = 8, H = 10 };
        var saved = Writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), AnnotationTestHelpers.OnePage(caret));

        var back = Assert.IsType<CaretAnnotation>(Assert.Single(Read(saved)));
        Assert.True(back.WrittenByRailReader);
    }

    [Fact]
    public void TheMarkIsNotPartOfTheSidecarFormat()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new HighlightAnnotation { WrittenByRailReader = true }, RailReaderJsonContext.Default.Annotation);
        Assert.DoesNotContain("written", json, StringComparison.OrdinalIgnoreCase);
    }
}
