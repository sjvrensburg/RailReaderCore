using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Editing an annotation's metadata (author, subject, review state) through
/// <see cref="PdfAnnotationWriter.WriteReconciled"/>. These used to be dropped: the reconciler
/// compared content, style and geometry only, so a metadata-only edit looked unchanged, and the
/// in-place path for notes rewrote neither <c>/T</c> nor <c>/Subj</c> (RailMark #54).
/// </summary>
public class PdfAnnotationMetadataEditTests
{
    private static byte[] Plain() => AnnotationTestHelpers.PlainPdfBytes();
    private static List<Annotation> ReadBack(byte[] b) => AnnotationTestHelpers.ReadBack(b);

    [Fact]
    public void NoteAuthor_ChangedAlone_IsWrittenInPlace()
    {
        var writer = new PdfAnnotationWriter();
        var note = new TextNoteAnnotation { X = 100, Y = 100, Text = "note A", Author = "Alice" };
        var file = AnnotationTestHelpers.OnePage(note);
        var b1 = writer.WriteReconciled(Plain(), file);
        var id = note.NativeId;

        note.Author = "Bob";
        var b2 = writer.WriteReconciled(b1, file);

        var back = Assert.IsType<TextNoteAnnotation>(Assert.Single(ReadBack(b2)));
        Assert.Equal("Bob", back.Author);
        Assert.Equal("note A", back.EffectiveContents);
        Assert.Equal(id, back.NativeId);
    }

    [Fact]
    public void NoteAuthor_ChangedWithComment_IsWritten()
    {
        // The exact shape of #54: comment and author in one update; the comment used to land, the author not.
        var writer = new PdfAnnotationWriter();
        var note = new TextNoteAnnotation { X = 100, Y = 100, Text = "note A", Author = "Alice" };
        var file = AnnotationTestHelpers.OnePage(note);
        var b1 = writer.WriteReconciled(Plain(), file);

        note.Author = "Bob";
        note.Text = note.Contents = "note A (edited)";
        var b2 = writer.WriteReconciled(b1, file);

        var back = Assert.Single(ReadBack(b2));
        Assert.Equal("Bob", back.Author);
        Assert.Equal("note A (edited)", back.EffectiveContents);
    }

    [Fact]
    public void NoteAuthor_SetOnAnUnauthoredNote_IsWritten()
    {
        var writer = new PdfAnnotationWriter();
        var note = new TextNoteAnnotation { X = 100, Y = 100, Text = "anon" };
        var file = AnnotationTestHelpers.OnePage(note);
        var b1 = writer.WriteReconciled(Plain(), file);
        Assert.Null(Assert.Single(ReadBack(b1)).Author);

        note.Author = "Stefan";
        var b2 = writer.WriteReconciled(b1, file);

        Assert.Equal("Stefan", Assert.Single(ReadBack(b2)).Author);
    }

    [Fact]
    public void NoteAuthor_Cleared_ReadsBackAsNone_AndStaysSettled()
    {
        var writer = new PdfAnnotationWriter();
        var note = new TextNoteAnnotation { X = 100, Y = 100, Text = "n", Author = "Alice", Subject = "Note" };
        var file = AnnotationTestHelpers.OnePage(note);
        var b1 = writer.WriteReconciled(Plain(), file);

        note.Author = null;
        note.Subject = null;
        var b2 = writer.WriteReconciled(b1, file);
        var back = Assert.Single(ReadBack(b2));
        Assert.Null(back.Author);
        Assert.Null(back.Subject);

        // The blanked keys read back as null, so the next save sees nothing to do.
        Assert.True(AnnotationEquivalence.MetadataEquivalent(back, note));
    }

    [Fact]
    public void NoteState_Cleared_ReadsBackAsNone()
    {
        var writer = new PdfAnnotationWriter();
        var note = new TextNoteAnnotation { X = 100, Y = 100, Text = "n", State = ReviewState.Accepted };
        var file = AnnotationTestHelpers.OnePage(note);
        var b1 = writer.WriteReconciled(Plain(), file);
        Assert.Equal(ReviewState.Accepted, Assert.Single(ReadBack(b1)).State);

        note.State = ReviewState.None;
        var b2 = writer.WriteReconciled(b1, file);

        Assert.Equal(ReviewState.None, Assert.Single(ReadBack(b2)).State);
    }

    [Fact]
    public void HighlightAuthor_ChangedAlone_IsWritten()
    {
        var writer = new PdfAnnotationWriter();
        var hl = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Author = "Alice" };
        var file = AnnotationTestHelpers.OnePage(hl);
        var b1 = writer.WriteReconciled(Plain(), file);
        var id = hl.NativeId;

        hl.Author = "Bob";
        var b2 = writer.WriteReconciled(b1, file);

        var back = Assert.Single(ReadBack(b2));
        Assert.Equal("Bob", back.Author);
        Assert.Equal(id, back.NativeId);
    }

    [Fact]
    public void HighlightSubject_ChangedAlone_IsWritten()
    {
        var writer = new PdfAnnotationWriter();
        var hl = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] };
        var file = AnnotationTestHelpers.OnePage(hl);
        var b1 = writer.WriteReconciled(Plain(), file);

        hl.Subject = "Replacement";
        var b2 = writer.WriteReconciled(b1, file);

        Assert.Equal("Replacement", Assert.Single(ReadBack(b2)).Subject);
    }
}
