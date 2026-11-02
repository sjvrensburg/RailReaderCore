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

    /// <summary>
    /// A one-page PDF holding an Acrobat-style caret: rich text, /RD, /Sy /P, a pop-up and its own
    /// appearance stream (tagged <c>%ACROAP</c> so its survival can be seen). Hand-built, classic xref.
    /// </summary>
    private const string Ap = "0 0 1 rg 0 0 m 4 7 l 8 0 l f %ACROAP";

    private static byte[] AcrobatCaretPdf(string symbol = "/P")
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 6 0 R /Annots [4 0 R 7 0 R] >>",
            "<< /Type /Annot /Subtype /Caret /Rect [100 130 108 137] /C [0 0 1] /NM (acro-caret) /T (Acro) "
                + $"/Contents (old words) /RC (<body><p>old words</p></body>) /RD [1 1 1 1] /Sy {symbol} /F 4 /P 3 0 R "
                + "/Popup 7 0 R /AP << /N 5 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 8 7] /Length {Ap.Length} >>\nstream\n{Ap}\nendstream",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Popup /Rect [120 100 220 160] /Parent 4 0 R /F 28 >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Latin1Bytes(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Every stream's content, inflated where it is Flate-compressed (PDFium compresses on save).</summary>
    private static string StreamText(byte[] pdf)
    {
        var text = Latin1(pdf);
        var sb = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"stream\r?\n(.*?)\r?\n?endstream", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var raw = Latin1Bytes(m.Groups[1].Value);
            try
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
                using var r = new StreamReader(z, Encoding.Latin1);
                sb.Append(r.ReadToEnd());
            }
            catch (InvalidDataException)
            {
                sb.Append(m.Groups[1].Value);   // not compressed
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The PDF as a reader sees it: a plain re-save keeps only the live objects.</summary>
    private static string Live(byte[] pdf)
    {
        var model = new PdfAnnotationReader().Read(pdf);
        return Latin1(new PdfAnnotationWriter().WriteReconciled(pdf, model));
    }

    [Fact]
    public void Editing_Another_Producers_Caret_Keeps_Everything_It_Carries()
    {
        var source = AcrobatCaretPdf();
        var model = new PdfAnnotationReader().Read(source);
        var caret = Assert.IsType<CaretAnnotation>(Assert.Single(model.Pages[0]));
        caret.Author = "Someone else";
        caret.Contents = "new words";

        var saved = new PdfAnnotationWriter().WriteReconciled(source, model);

        var back = Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(saved)));
        Assert.Equal("Someone else", back.Author);
        Assert.Equal("new words", back.Contents);
        var live = Live(saved);
        static bool Has(string pdf, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(pdf, pattern);
        Assert.True(Has(live, @"/RD\s*\["), "/RD lost");
        Assert.True(Has(live, @"/RC\s*\("), "/RC lost");
        Assert.True(Has(live, @"/Sy\s*/P\b"), "/Sy /P lost");
        Assert.True(Has(live, @"/Popup\s*\d+\s+0\s+R"), "/Popup lost");
        Assert.Contains("%ACROAP", StreamText(saved));   // its own appearance, not a generated one
    }

    [Fact]
    public void Recolouring_Another_Producers_Caret_Replaces_Only_Its_Colour_And_Appearance()
    {
        // PDFium won't set /C over an /AP, and recreating the caret would drop its rich text, padding
        // and pop-up; the post-pass rewrites /C and the appearance and keeps the rest.
        var source = AcrobatCaretPdf("/None");
        var model = new PdfAnnotationReader().Read(source);
        ((CaretAnnotation)model.Pages[0][0]).Color = "#FF0000";

        var saved = new PdfAnnotationWriter().WriteReconciled(source, model);

        var back = Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(saved)));
        Assert.Equal("#FF0000", back.Color, ignoreCase: true);
        Assert.Equal("acro-caret", back.NativeId);
        Assert.Equal("Acro", back.Author);
        var live = Live(saved);
        var streams = StreamText(Latin1Bytes(live));
        Assert.DoesNotContain("%ACROAP", streams);
        Assert.Contains("1 0 0 rg 1 1 m 4 6 l 7 1 l", streams);   // drawn inside the /RD [1 1 1 1] inset
        Assert.Matches(@"/RD\s*\[", live);
        Assert.Matches(@"/RC\s*\(", live);
        Assert.Matches(@"/Popup\s*\d+\s+0\s+R", live);
        Assert.Matches(@"/C\s*\[\s*1\s+0\s+0\s*\]", live);
        Assert.DoesNotContain("RRM", live);

        // Now stable: the colour reads back from the new appearance, so a re-save sees no change and
        // runs no post-pass (PDFium's rewrite alone: one cross-reference section).
        var again = new PdfAnnotationWriter().WriteReconciled(saved, new PdfAnnotationReader().Read(saved));
        Assert.Equal(1, Latin1(again).Split("startxref").Length - 1);
    }

    [Fact]
    public void A_Paragraph_Caret_From_Another_Producer_Is_Not_Recoloured()
    {
        // /Sy /P is drawn as a pilcrow, not a "^": the post-pass has no appearance to replace it with.
        var source = AcrobatCaretPdf();
        var model = new PdfAnnotationReader().Read(source);
        ((CaretAnnotation)model.Pages[0][0]).Color = "#FF0000";

        var live = Live(new PdfAnnotationWriter().WriteReconciled(source, model));

        Assert.Contains("%ACROAP", StreamText(Latin1Bytes(live)));
        Assert.Matches(@"/RD\s*\[", live);
    }

    [Theory]
    [InlineData(TestFixtures.EncryptedPdfPassword)]   // user password
    [InlineData("owner")]                             // owner password: the key comes from /OE instead
    public void A_Caret_Is_Created_In_An_Aes256_File_With_An_Encrypted_Appearance(string password)
    {
        var source = File.ReadAllBytes(TestFixtures.GetEncryptedTestPdfPath());
        var caret = Caret();
        var file = AnnotationTestHelpers.OnePage(caret);

        var bytes = new PdfAnnotationWriter().WriteReconciled(source, file, password);

        var back = Assert.IsType<CaretAnnotation>(Assert.Single(new PdfAnnotationReader().Read(bytes, password).Pages[0]));
        // The colour is read from the appearance stream, so it reads back only if PDFium could decrypt it.
        Assert.Equal("#0066FF", back.Color, ignoreCase: true);
        Assert.Equal("inserted words", back.Contents);
        Assert.Equal(AnnotationSource.InPdf, caret.Source);
        Assert.DoesNotContain("0 0.4 1 rg", Latin1(bytes));   // nothing in the clear
    }

    [Fact]
    public void Aes256_Stream_Encryption_Round_Trips()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var data = Encoding.Latin1.GetBytes("0 0.4 1 rg 0 0 m 4 7 l 8 0 l h f");

        var sealedData = PdfAes256Security.EncryptStream(key, data);

        Assert.Equal(16 + (data.Length / 16 + 1) * 16, sealedData.Length);   // IV + PKCS#7-padded blocks
        Assert.Equal(data, PdfAes256Security.DecryptStream(key, sealedData));
    }

    [Theory]
    [InlineData("(a\\(b\\)\\\\\\101\\n)", new byte[] { (byte)'a', (byte)'(', (byte)'b', (byte)')', (byte)'\\', 0x41, 10 })]
    [InlineData("<41 42 4>", new byte[] { 0x41, 0x42, 0x40 })]
    public void Pdf_Strings_Decode(string token, byte[] expected)
        => Assert.Equal(expected, PdfReplyLinker.DecodeString(token));

    [Fact]
    public void No_Caret_Is_Created_In_An_Aes128_File()
    {
        // Under RC4/AES-128 the post-pass can't encrypt an appearance stream (Poppler would draw
        // nothing) or promote the caret (MuPDF would see nothing), so it is not written at all.
        var source = File.ReadAllBytes(TestFixtures.GetAes128TestPdfPath());
        var caret = Caret();
        var file = AnnotationTestHelpers.OnePage(caret, new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });

        var bytes = new PdfAnnotationWriter().WriteReconciled(source, file, TestFixtures.EncryptedPdfPassword);

        var back = new PdfAnnotationReader().Read(bytes, TestFixtures.EncryptedPdfPassword).Pages[0];
        Assert.DoesNotContain(back, a => a is CaretAnnotation or RectAnnotation);
        Assert.Single(back.OfType<HighlightAnnotation>());
        Assert.NotEqual(AnnotationSource.InPdf, caret.Source);
    }

    [Fact]
    public void Without_The_Post_Pass_A_Content_Edit_Of_A_Caret_Still_Lands()
    {
        var first = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(Caret()));
        var edited = Caret();
        edited.Contents = "edited without the post-pass";

        var bytes = new PdfAnnotationWriter { LinkApplier = (_, _) => null }
            .WriteReconciled(first, AnnotationTestHelpers.OnePage(edited));

        var caret = Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(bytes)));
        Assert.Equal("edited without the post-pass", caret.Contents);
    }

    [Fact]
    public void A_Grouped_Annotation_Is_Not_A_Reply_But_Is_Deleted_With_Its_Group()
    {
        var caret = Caret();
        var strike = new StrikeOutAnnotation { NativeId = "s", InReplyTo = "caret-1", ReplyType = AnnotationReplyType.Group };
        var reply = new TextNoteAnnotation { NativeId = "r", InReplyTo = "caret-1" };
        var file = AnnotationTestHelpers.OnePage(caret, strike, reply);

        var thread = Assert.Single(AnnotationThreads.Build(file));
        Assert.Same(caret, thread.Root);
        Assert.Equal([reply], thread.Replies);
        Assert.Equal([strike], thread.Grouped);

        var goesWith = AnnotationThreads.Descendants(file, caret);
        Assert.Contains(strike, goesWith);
        Assert.Contains(reply, goesWith);
    }

    // --- rotated pages ---

    /// <summary>A one-page 300×200 PDF with the given /Rotate. Hand-built, classic xref.</summary>
    private static byte[] RotatedPdf(int rotate)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Rotate {rotate} /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Theory]
    // The /Rect is in unrotated space (w × h); where the apex must sit for the "^" to point up on screen.
    [InlineData(0, "0 0 m 4 7 l 8 0 l")]     // apex top centre
    [InlineData(1, "7 0 m 0 4 l 7 8 l")]     // shown turned 90° clockwise: up on screen is −x
    [InlineData(2, "8 7 m 4 0 l 0 7 l")]     // upside down: apex bottom centre
    [InlineData(3, "0 8 m 7 4 l 0 0 l")]     // 270°: up on screen is +x
    public void The_Caret_Appearance_Points_Up_As_The_Page_Is_Displayed(int quarterTurns, string path)
    {
        // An 8 × 7 caret as displayed; on a 90°/270° page its /Rect is 7 × 8.
        var (w, h) = (quarterTurns & 1) == 0 ? (8f, 7f) : (7f, 8f);
        var content = PdfReplyLinker.CaretAppearance(new CaretRetype(w, h, 0, 0, 1, quarterTurns));
        Assert.Contains(path, content);
    }

    [Theory]
    [InlineData(90, "7 0 m 0 4 l 7 8 l")]
    [InlineData(270, "0 8 m 7 4 l 0 0 l")]
    public void A_Caret_On_A_Rotated_Page_Keeps_Its_Box_And_Points_Up(int rotate, string path)
    {
        var bytes = new PdfAnnotationWriter().WriteReconciled(RotatedPdf(rotate), AnnotationTestHelpers.OnePage(Caret()));

        var back = Assert.IsType<CaretAnnotation>(Assert.Single(ReadBack(bytes)));
        Assert.Equal(150, back.X, 0.5f);
        Assert.Equal(110, back.Y, 0.5f);
        Assert.Equal(8, back.W, 0.5f);
        Assert.Equal(7, back.H, 0.5f);
        Assert.Contains(path, StreamText(bytes));
        Assert.Matches(@"/BBox\s*\[0 0 7 8\]", Latin1(bytes));
    }
}
