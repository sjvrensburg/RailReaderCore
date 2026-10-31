using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Annotations written by <see cref="PdfAnnotationWriter.WriteReconciled"/> end up as indirect
/// objects referenced from <c>/Annots</c> (ISO 32000-1 Table 30), not as the direct dictionaries
/// PDFium's <c>FPDFPage_CreateAnnot</c> leaves there — MuPDF/PyMuPDF does not see direct ones at all
/// (RailMark #55). <see cref="PdfReplyLinker"/> does the promotion in its incremental-update post-pass.
/// </summary>
public class PdfAnnotationIndirectTests
{
    private static byte[] Plain() => AnnotationTestHelpers.PlainPdfBytes();
    private static List<Annotation> ReadBack(byte[] b, int page = 0) => AnnotationTestHelpers.ReadBack(b, page);

    /// <summary>
    /// Collapses the incremental revisions with a plain PDFium rewrite (it keeps direct and indirect
    /// objects as they are), then returns the contents of every <c>/Annots</c> array in the file —
    /// inline arrays, and indirect array objects alike.
    /// </summary>
    private static List<string> AnnotsArrays(byte[] bytes)
    {
        var flat = new PdfAnnotationWriter().WriteReconciled(bytes, new AnnotationFile());
        var text = Encoding.Latin1.GetString(flat);
        var arrays = Regex.Matches(text, @"/Annots\s*\[(.*?)\]", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        foreach (Match r in Regex.Matches(text, @"/Annots\s+(\d+)\s+(\d+)\s+R"))
        {
            var obj = Regex.Match(text, $@"(?<![0-9]){r.Groups[1].Value}\s+{r.Groups[2].Value}\s+obj\s*\[(.*?)\]\s*endobj",
                RegexOptions.Singleline);
            Assert.True(obj.Success, "indirect /Annots array not found");
            arrays.Add(obj.Groups[1].Value);
        }
        return arrays;
    }

    private static void AssertAllIndirect(byte[] bytes, int expectedRefs)
    {
        var arrays = AnnotsArrays(bytes);
        Assert.NotEmpty(arrays);
        foreach (var a in arrays)
        {
            Assert.DoesNotContain("<<", a);
            // Whole references, each delimited: PDFium writes neighbouring dictionaries with no space
            // between them, and a promotion that replaced them as-is would read "17 0 R16 0 R".
            Assert.Matches(@"^(\s*\d+\s+\d+\s+R(?=\s|$))*\s*$", a);
        }
        Assert.Equal(expectedRefs, arrays.Sum(a => Regex.Matches(a, @"\d+\s+\d+\s+R").Count));
    }

    [Fact]
    public void NewAnnotations_AreIndirectObjects()
    {
        var file = AnnotationTestHelpers.OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Author = "Alice" },
            new TextNoteAnnotation { X = 200, Y = 100, Text = "note", Author = "Alice" });

        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), file);

        AssertAllIndirect(bytes, 2);
        var back = ReadBack(bytes);
        Assert.Equal(2, back.Count);
        Assert.All(back, a => Assert.Equal("Alice", a.Author));
        Assert.DoesNotContain("RRM", Encoding.Latin1.GetString(AnnotsFlat(bytes)));
    }

    private static byte[] AnnotsFlat(byte[] bytes) => new PdfAnnotationWriter().WriteReconciled(bytes, new AnnotationFile());

    [Fact]
    public void RecreatedAnnotation_IsIndirect()
    {
        var writer = new PdfAnnotationWriter();
        var hl = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] };
        var file = AnnotationTestHelpers.OnePage(hl);
        var b1 = writer.WriteReconciled(Plain(), file);

        hl.Color = "#00FF00"; // markup edits go through delete + recreate
        var b2 = writer.WriteReconciled(b1, file);

        AssertAllIndirect(b2, 1);
        Assert.Equal("#00FF00", Assert.Single(ReadBack(b2)).Color, ignoreCase: true);
    }

    [Fact]
    public void LegacyDirectNote_IsPromoted_WhenEdited()
    {
        // AddAuthoredAnnotations has no post-pass, so it leaves the direct dictionary PDFium made,
        // as WriteReconciled did before promotion — the shape of a file an older version saved.
        var legacy = new PdfAnnotationWriter().AddAuthoredAnnotations(Plain(),
            AnnotationTestHelpers.OnePage(new TextNoteAnnotation { X = 100, Y = 100, Text = "old", Author = "Alice", NativeId = "n-1" }));
        Assert.Contains(AnnotsArrays(legacy), a => a.Contains("<<"));

        var file = new PdfAnnotationReader().Read(legacy);
        var note = Assert.Single(file.Pages[0]);
        note.Author = "Bob";
        var edited = new PdfAnnotationWriter().WriteReconciled(legacy, file);

        AssertAllIndirect(edited, 1);
        var back = Assert.Single(ReadBack(edited));
        Assert.Equal("Bob", back.Author);
        Assert.Equal("n-1", back.NativeId);
    }

    [Fact]
    public void UnchangedResave_AppendsNothing()
    {
        var writer = new PdfAnnotationWriter();
        var file = AnnotationTestHelpers.OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });
        var first = writer.WriteReconciled(Plain(), file);

        var second = writer.WriteReconciled(first, file);

        // Nothing written, nothing to promote: a plain PDFium rewrite with one cross-reference section.
        Assert.Single(Regex.Matches(Encoding.Latin1.GetString(second), "startxref"));
        AssertAllIndirect(second, 1);
    }

    [Fact]
    public void Promotion_BacksOffCleanly_WhenThePostPassFails()
    {
        // The linker's back-off path redoes the save untagged: direct dictionaries, but no stray markers.
        var writer = new PdfAnnotationWriter { LinkApplier = (_, _) => null };
        var file = AnnotationTestHelpers.OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });

        var bytes = writer.WriteReconciled(Plain(), file);

        Assert.DoesNotContain("RRM", Encoding.Latin1.GetString(bytes));
        Assert.Single(ReadBack(bytes));
    }

    [Fact]
    public void IndirectAnnotsArray_HoldsPromotedReferences()
    {
        // A page whose /Annots is itself an indirect array object (common from other producers):
        // PDFium appends the new dictionary inside that array object, which the linker must rewrite.
        var file = AnnotationTestHelpers.OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] },
            new TextNoteAnnotation { X = 200, Y = 100, Text = "n" });

        var bytes = new PdfAnnotationWriter().WriteReconciled(PdfWithIndirectAnnotsArray(), file);

        var text = Encoding.Latin1.GetString(AnnotsFlat(bytes));
        Assert.Matches(@"/Annots\s+\d+\s+\d+\s+R", text);
        AssertAllIndirect(bytes, 2);
        Assert.Equal(2, ReadBack(bytes).Count);
    }

    /// <summary>A one-page PDF whose page has <c>/Annots 4 0 R</c>, an empty indirect array.</summary>
    private static byte[] PdfWithIndirectAnnotsArray()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots 4 0 R /Contents 5 0 R >>",
            "[ ]",
            "<< /Length 0 >>\nstream\n\nendstream",
        ];
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
