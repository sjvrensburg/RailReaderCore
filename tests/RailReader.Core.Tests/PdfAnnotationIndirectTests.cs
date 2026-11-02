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
    private static List<string> AnnotsArrays(byte[] bytes, string? password = null)
    {
        var flat = new PdfAnnotationWriter().WriteReconciled(bytes, new AnnotationFile(), password);
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

    private static void AssertAllIndirect(byte[] bytes, int expectedRefs, string? password = null)
    {
        var arrays = AnnotsArrays(bytes, password);
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

    // --- #186: direct dictionaries a save didn't write are promoted too ---

    /// <summary>Legacy (pre-0.79.1) direct notes on pages 0 and 1, as an older version saved them.</summary>
    private static byte[] LegacyTwoPages(byte[]? source = null, string? password = null)
    {
        var file = new AnnotationFile();
        file.Pages[0] = [new TextNoteAnnotation { X = 100, Y = 100, Text = "p1", Author = "Alice", NativeId = "n-1" }];
        file.Pages[1] =
        [
            new TextNoteAnnotation { X = 100, Y = 100, Text = "p2a", Author = "Alice", NativeId = "n-2" },
            new HighlightAnnotation { Rects = [new HighlightRect(72, 200, 80, 16)], Author = "Alice", NativeId = "n-3" },
        ];
        var legacy = new PdfAnnotationWriter().AddAuthoredAnnotations(source ?? Plain(), file, password);
        Assert.Equal(2, AnnotsArrays(legacy, password).Count(a => a.Contains("<<")));
        return legacy;
    }

    [Fact]
    public void LegacyDirectAnnotations_ArePromoted_InAnAes256File()
    {
        // One key for the whole file, so the encrypted strings move into new objects unchanged.
        const string pw = TestFixtures.EncryptedPdfPassword;
        var legacy = LegacyTwoPages(File.ReadAllBytes(TestFixtures.GetEncryptedTestPdfPath()), pw);
        var file = new PdfAnnotationReader().Read(legacy, pw);
        Assert.Single(file.Pages[0]).Author = "Bob";

        var saved = new PdfAnnotationWriter().WriteReconciled(legacy, file, pw);

        AssertAllIndirect(saved, 3, pw);
        var back = new PdfAnnotationReader().Read(saved, pw);
        Assert.Equal("Bob", Assert.Single(back.Pages[0]).Author);
        Assert.Contains(back.Pages[1], a => a is TextNoteAnnotation { Contents: "p2a", Author: "Alice" });
    }

    [Fact]
    public void LegacyDirectAnnotations_StayDirect_InAnAes128File()
    {
        // Strings are keyed by object number before AES-256: moving a dictionary would garble them.
        const string pw = TestFixtures.EncryptedPdfPassword;
        var legacy = LegacyTwoPages(File.ReadAllBytes(TestFixtures.GetAes128TestPdfPath()), pw);
        var file = new PdfAnnotationReader().Read(legacy, pw);
        Assert.Single(file.Pages[0]).Author = "Bob";

        var saved = new PdfAnnotationWriter().WriteReconciled(legacy, file, pw);

        Assert.Equal(2, AnnotsArrays(saved, pw).Count(a => a.Contains("<<")));
        var back = new PdfAnnotationReader().Read(saved, pw);
        Assert.Equal("Bob", Assert.Single(back.Pages[0]).Author);
        Assert.Contains(back.Pages[1], a => a is TextNoteAnnotation { Contents: "p2a", Author: "Alice" });
    }

    [Fact]
    public void LegacyDirectAnnotations_OnUntouchedPages_ArePromoted_WhenTheSaveWritesAnything()
    {
        var legacy = LegacyTwoPages();
        var file = new PdfAnnotationReader().Read(legacy);
        Assert.Single(file.Pages[0]).Author = "Bob"; // the only edit: page 0

        var saved = new PdfAnnotationWriter().WriteReconciled(legacy, file);

        AssertAllIndirect(saved, 3);
        Assert.Equal("Bob", Assert.Single(ReadBack(saved, 0)).Author);
        var page1 = ReadBack(saved, 1);
        Assert.Equal(["n-2", "n-3"], page1.Select(a => a.NativeId).Order().ToList());
        Assert.Contains(page1, a => a is TextNoteAnnotation { Contents: "p2a" });
    }

    [Fact]
    public void LegacyDirectAnnotations_ArePromoted_WhenTheSaveOnlyDeletes()
    {
        // A deletion writes nothing the post-pass would otherwise tag, but it is still a change.
        var legacy = LegacyTwoPages();
        var file = new PdfAnnotationReader().Read(legacy);
        file.Pages[0].Clear();

        var saved = new PdfAnnotationWriter().WriteReconciled(legacy, file);

        AssertAllIndirect(saved, 2);
        Assert.Empty(ReadBack(saved, 0));
        Assert.Equal(["n-2", "n-3"], ReadBack(saved, 1).Select(a => a.NativeId).Order().ToList());
    }

    [Fact]
    public void DeletionOnlySave_OfAnAllIndirectFile_AppendsNothing()
    {
        // Nothing direct to sweep: PDFium's rewrite alone, not an empty incremental update after it.
        var writer = new PdfAnnotationWriter();
        var file = AnnotationTestHelpers.OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] },
            new TextNoteAnnotation { X = 200, Y = 100, Text = "n" });
        var first = writer.WriteReconciled(Plain(), file);
        file.Pages[0].RemoveAt(1);

        var second = writer.WriteReconciled(first, file);

        Assert.Single(Regex.Matches(Encoding.Latin1.GetString(second), "startxref"));
        AssertAllIndirect(second, 1);
    }

    [Fact]
    public void LegacyDirectAnnotations_UnchangedResave_AppendsNothing()
    {
        // Nothing written means no post-pass at all: the file is left as it was, direct entries included.
        var legacy = LegacyTwoPages();

        var saved = new PdfAnnotationWriter().WriteReconciled(legacy, new PdfAnnotationReader().Read(legacy));

        Assert.Single(Regex.Matches(Encoding.Latin1.GetString(saved), "startxref"));
        Assert.Equal(2, AnnotsArrays(saved).Count(a => a.Contains("<<")));
    }

    [Fact]
    public void ForeignDirectAnnotations_ArePromoted_InlineAndInIndirectArrays()
    {
        // Another producer's direct dictionaries, with no /NM (the reconciler never touches those), on a
        // page with an inline /Annots and on one whose /Annots is an indirect array object.
        const string square = "<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] /C [1 0 0] /Contents (sq) >>";
        const string circle = "<</Type/Annot/Subtype/Circle/Rect[60 60 90 90]/C[0 0 1]>>";
        var pdf = BuildPdf(
            [
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{square}{circle}] /Contents 5 0 R >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots 6 0 R /Contents 5 0 R >>",
            ],
            ["<< /Length 0 >>\nstream\n\nendstream", $"[ {square} ]"]);
        var file = AnnotationTestHelpers.OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });

        var saved = new PdfAnnotationWriter().WriteReconciled(pdf, file);

        AssertAllIndirect(saved, 4);
        var flat = Encoding.Latin1.GetString(AnnotsFlat(saved));
        Assert.Equal(2, Regex.Matches(flat, @"/Subtype\s*/Square").Count);
        Assert.Single(Regex.Matches(flat, @"/Subtype\s*/Circle"));
        Assert.Contains("(sq)", flat);
    }

    [Fact]
    public void ForeignDirectAnnotations_StayDirect_WhenThePostPassBacksOff()
    {
        const string square = "<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] >>";
        var pdf = BuildPdf([$"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{square}] /Contents 4 0 R >>"],
            ["<< /Length 0 >>\nstream\n\nendstream"]);
        var writer = new PdfAnnotationWriter { LinkApplier = (_, _) => null };

        var saved = writer.WriteReconciled(pdf, AnnotationTestHelpers.OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }));

        Assert.DoesNotContain("RRM", Encoding.Latin1.GetString(saved));
        Assert.Contains(AnnotsArrays(saved), a => a.Contains("<<"));
    }

    [PyMuPdfFact]
    public void PromotedFile_IsSeenWhole_ByPyMuPdf()
    {
        // The point of #186: PyMuPDF's page.annots() skips direct /Annots dictionaries (xref 0), so it
        // must see every annotation after the save — not only the one the save wrote. (MuPDF's own
        // `mutool run` API does list direct ones, so it can't stand in.)
        var legacy = LegacyTwoPages();
        Assert.Equal([0, 0, 0], PyMuPdfAnnotCounts(legacy)!); // the bug: all hidden

        var file = new PdfAnnotationReader().Read(legacy);
        Assert.Single(file.Pages[0]).Author = "Bob";
        var saved = new PdfAnnotationWriter().WriteReconciled(legacy, file);

        Assert.Equal([1, 2, 0], PyMuPdfAnnotCounts(saved)!);
    }

    /// <summary>Annotations PyMuPDF's <c>page.annots()</c> yields on each page, or null when Python
    /// or PyMuPDF is not installed.</summary>
    internal static int[]? PyMuPdfAnnotCounts(byte[] pdf)
    {
        var dir = Directory.CreateTempSubdirectory("rr-pymupdf-");
        try
        {
            var pdfPath = Path.Combine(dir.FullName, "doc.pdf");
            File.WriteAllBytes(pdfPath, pdf);
            var psi = new System.Diagnostics.ProcessStartInfo("python3")
                { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-I", "-c",
                         "import sys\ntry:\n import pymupdf\nexcept ImportError:\n sys.exit(3)\n"
                         + "print(' '.join(str(len(list(p.annots()))) for p in pymupdf.open(sys.argv[1])))", pdfPath })
                psi.ArgumentList.Add(arg);
            System.Diagnostics.Process p;
            try { p = System.Diagnostics.Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception) { return null; } // no python3
            using (p)
            {
                var errorTask = p.StandardError.ReadToEndAsync(); // both pipes at once, or a full one stalls the child
                var output = p.StandardOutput.ReadToEnd();
                var error = errorTask.Result;
                p.WaitForExit();
                if (p.ExitCode == 3) return null;
                Assert.True(p.ExitCode == 0, error);
                return output.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>A one-page PDF whose page has <c>/Annots 4 0 R</c>, an empty indirect array.</summary>
    private static byte[] PdfWithIndirectAnnotsArray() => BuildPdf(
        ["<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots 4 0 R /Contents 5 0 R >>"],
        ["[ ]", "<< /Length 0 >>\nstream\n\nendstream"]);

    /// <summary>
    /// A PDF with the given page dictionaries (objects 3…) followed by <paramref name="extra"/> objects;
    /// object 1 is the catalog, 2 the page tree.
    /// </summary>
    private static byte[] BuildPdf(string[] pages, string[] extra)
    {
        var kids = string.Join(" ", pages.Select((_, i) => $"{i + 3} 0 R"));
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>",
            .. pages,
            .. extra,
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

/// <summary>
/// A [Fact] that is skipped (with a visible reason) when python3 with PyMuPDF is not installed, so the
/// PyMuPDF visibility check never reports green without having run.
/// </summary>
public sealed class PyMuPdfFactAttribute : FactAttribute
{
    // Any failure of the probe (a python3 that is a stub, as on Windows without Store Python, or a broken
    // PyMuPDF) means unavailable: thrown here it would error every test carrying the attribute.
    private static readonly Lazy<bool> Available = new(() =>
    {
        try { return PdfAnnotationIndirectTests.PyMuPdfAnnotCounts(AnnotationTestHelpers.PlainPdfBytes()) is not null; }
        catch (Exception) { return false; }
    });

    public PyMuPdfFactAttribute()
    {
        if (!Available.Value) Skip = "python3 with PyMuPDF not installed (pip install pymupdf to run).";
    }
}
