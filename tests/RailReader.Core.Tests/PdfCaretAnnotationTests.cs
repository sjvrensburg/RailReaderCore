using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Creating /Caret annotations (which PDFium cannot) and /RT /Group links — Acrobat's "Inserted Text"
/// and "Replace Text". The writer creates a placeholder that PdfReplyLinker retypes; these tests drive
/// that through the public writer and read it back with the (PDFium-based) reader.
/// </summary>
public class PdfCaretAnnotationTests
{
    private static byte[] Plain() => AnnotationTestHelpers.PlainPdfBytes();
    private static List<Annotation> ReadBack(byte[] b) => AnnotationTestHelpers.ReadBack(b);
    private static Annotation ById(List<Annotation> l, string id) => l.Single(a => a.NativeId == id);
    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    private static CaretAnnotation Caret(string id = "caret-1") => new()
    {
        X = 150, Y = 110, W = 8, H = 7, NativeId = id, Contents = "inserted words", Author = "Carol",
        Subject = "Inserted Text", Color = "#0066FF",
        CreatedUtc = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void A_Caret_Is_Written_And_Reads_Back_As_A_Caret()
    {
        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));

        var caret = Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(bytes)));
        Assert.Equal("caret-1", caret.NativeId);
        Assert.Equal("inserted words", caret.Contents);
        Assert.Equal("Carol", caret.Author);
        Assert.Equal("Inserted Text", caret.Subject);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero), caret.CreatedUtc);
        Assert.Equal(150, caret.X, 0.5f);
        Assert.Equal(110, caret.Y, 0.5f);
        Assert.Equal(8, caret.W, 0.5f);
        Assert.Equal(7, caret.H, 0.5f);
    }

    [Fact]
    public void The_Caret_Has_Its_Own_Appearance_And_No_Square_Leftovers()
    {
        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));
        // A plain re-save keeps live objects only, so what is left is what a reader sees.
        var live = Latin1(new PdfAnnotationWriter().WriteReconciled(bytes, AnnotationTestHelpers.OnePage(Caret())));

        // PDFium writes names without separating whitespace (/Subtype/Caret), the post-pass with it.
        static bool Has(string pdf, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(pdf, pattern);
        Assert.True(Has(live, @"/Subtype\s*/Caret"));
        Assert.False(Has(live, @"/Subtype\s*/Square"));
        Assert.False(Has(live, @"/IC\b"));
        Assert.True(Has(live, @"/Sy\s*/None"));
        Assert.True(Has(live, @"/Subtype\s*/Form"));   // the appearance stream
        Assert.DoesNotContain("RRM", live);            // no marker survives
    }

    [Fact]
    public void Resaving_An_Unchanged_Caret_Appends_Nothing()
    {
        var writer = new PdfAnnotationWriter();
        var first = writer.WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));
        var second = writer.WriteReconciled(first, AnnotationTestHelpers.OnePage(Caret()));

        Assert.Equal(1, Latin1(second).Split("startxref").Length - 1);
        Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(second)));
    }

    [Fact]
    public void The_Caret_Colour_Reads_Back_From_Its_Appearance()
    {
        // PDFium won't report /C for an annotation with an appearance stream; the reader takes the
        // caret's colour from the stream instead, so an unchanged caret really is unchanged.
        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));

        Assert.Equal("#0066FF", Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(bytes))).Color, ignoreCase: true);
        Assert.Equal([0f, 0.4f, 1f], PdfAnnotationReader.FillColorOfContent("0 0.4 1 rg 0 0 m 4 7 l h f")!);
        Assert.Equal([0.5f, 0.5f, 0.5f], PdfAnnotationReader.FillColorOfContent("q 0.5 g 0 0 m f Q")!);
        Assert.Null(PdfAnnotationReader.FillColorOfContent("0 0 m 1 1 l S"));
    }

    [Fact]
    public void Recolouring_A_Caret_Redraws_Its_Appearance()
    {
        var writer = new PdfAnnotationWriter();
        var first = writer.WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));
        var red = Caret();
        red.Color = "#FF0000";

        var second = writer.WriteReconciled(first, AnnotationTestHelpers.OnePage(red));

        Assert.Equal("#FF0000", Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(second))).Color, ignoreCase: true);
    }

    [Fact]
    public void Editing_A_Caret_Keeps_It_A_Caret()
    {
        var writer = new PdfAnnotationWriter();
        var first = writer.WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));
        var edited = Caret();
        edited.Contents = "different words";

        var second = writer.WriteReconciled(first, AnnotationTestHelpers.OnePage(edited));

        var caret = Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(second)));
        Assert.Equal("different words", caret.Contents);
    }

    [Fact]
    public void Replace_Text_Is_A_Caret_With_A_Strikeout_Grouped_To_It()
    {
        var caret = Caret();
        caret.Subject = "Replace Text";
        caret.Contents = "four";
        var strike = new StrikeOutAnnotation
        {
            Rects = [new HighlightRect(120, 108, 30, 12)], NativeId = "strike-1", Author = "Carol",
            InReplyTo = "caret-1", ReplyType = AnnotationReplyType.Group, Color = "#0066FF",
        };

        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(caret, strike));

        var back = ReadBack(bytes);
        Assert.IsType<CaretAnnotation>(ById(back, "caret-1"));
        var s = Assert.IsType<StrikeOutAnnotation>(ById(back, "strike-1"));
        Assert.Equal("caret-1", s.InReplyTo);
        Assert.Equal(AnnotationReplyType.Group, s.ReplyType);
        Assert.Contains("/RT /Group", Latin1(bytes));
    }

    [Fact]
    public void Changing_Only_The_Reply_Type_Rewrites_The_Link()
    {
        var writer = new PdfAnnotationWriter();
        var parent = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "p" };
        var child = new StrikeOutAnnotation { Rects = [new HighlightRect(72, 130, 80, 12)], NativeId = "c", InReplyTo = "p" };
        var first = writer.WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(parent, child));
        Assert.Equal(AnnotationReplyType.Reply, ById(ReadBack(first), "c").ReplyType);

        child.ReplyType = AnnotationReplyType.Group;
        var second = writer.WriteReconciled(first, AnnotationTestHelpers.OnePage(parent, child));

        Assert.Equal(AnnotationReplyType.Group, ById(ReadBack(second), "c").ReplyType);
    }

    [Fact]
    public void Without_The_Post_Pass_A_Caret_Is_Not_Written_At_All()
    {
        // The linker backs off → the writer redoes the save with no post-pass. A placeholder must not be
        // left behind as a square; the caret is simply not written (and not marked persisted).
        var writer = new PdfAnnotationWriter { LinkApplier = (_, _) => null };
        var caret = Caret();
        var file = AnnotationTestHelpers.OnePage(caret, new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });

        var bytes = writer.WriteReconciled(Plain(), file);

        var back = ReadBack(bytes);
        Assert.DoesNotContain(back, a => a is RectAnnotation or CaretAnnotation);
        Assert.Single(back.OfType<HighlightAnnotation>());
        Assert.NotEqual(AnnotationSource.InPdf, caret.Source);
    }

    [Fact]
    public void A_Grouped_Annotation_Is_Not_A_Reply_But_Is_Deleted_With_Its_Group()
    {
        var caret = Caret();
        var strike = new StrikeOutAnnotation { NativeId = "s", InReplyTo = "caret-1", ReplyType = AnnotationReplyType.Group };
        var reply = new TextNoteAnnotation { NativeId = "r", InReplyTo = "caret-1" };
        var file = AnnotationTestHelpers.OnePage(caret, strike, reply);

        var thread = Assert.Single(AnnotationThreads.Build(file), t => ReferenceEquals(t.Root, caret));
        Assert.Equal([reply], thread.Replies);
        Assert.Contains(AnnotationThreads.Build(file), t => ReferenceEquals(t.Root, strike));

        var goesWith = AnnotationThreads.Descendants(file, caret);
        Assert.Contains(strike, goesWith);
        Assert.Contains(reply, goesWith);
    }
}
