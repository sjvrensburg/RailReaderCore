using System.Diagnostics;
using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// /IRT + /RT reply linkage written into the PDF (#149). PDFium cannot store an indirect reference, so
/// PdfAnnotationWriter.WriteReconciled appends it with PdfReplyLinker; these tests drive that through the
/// public writer and read the result back with the (PDFium-based) reader.
/// </summary>
public class PdfReplyLinkTests
{
    private static byte[] Plain() => AnnotationTestHelpers.PlainPdfBytes();
    private static List<Annotation> ReadBack(byte[] b) => AnnotationTestHelpers.ReadBack(b);

    private static (AnnotationFile File, HighlightAnnotation Parent, TextNoteAnnotation Reply) Thread()
    {
        var parent = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "parent-1" };
        var reply = new TextNoteAnnotation { X = 72, Y = 100, Text = "re", InReplyTo = "parent-1", NativeId = "reply-1" };
        return (AnnotationTestHelpers.OnePage(parent, reply), parent, reply);
    }

    private static int CountOf(byte[] bytes, string needle)
    {
        var text = Encoding.Latin1.GetString(bytes);
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static Annotation ById(List<Annotation> l, string id) => l.Single(a => a.NativeId == id);

    [Fact]
    public void WriteThenRead_RoundTripsInReplyTo()
    {
        var (file, _, _) = Thread();

        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), file);

        var back = ReadBack(bytes);
        Assert.Equal(2, back.Count);
        Assert.Equal("parent-1", ById(back, "reply-1").InReplyTo);
        Assert.Null(ById(back, "parent-1").InReplyTo);
        Assert.Contains("/IRT", Encoding.Latin1.GetString(bytes));
        Assert.Contains("/RT /R", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void LocatingMarkers_LeaveNothingBehindInTheLiveObjects()
    {
        var (file, _, _) = Thread();
        var writer = new PdfAnnotationWriter();
        var linked = writer.WriteReconciled(Plain(), file);

        // The incremental update leaves PDFium's superseded revision as it was (marker included); the
        // next PDFium save rewrites only live objects, so a marker still present would be a live one.
        file.Pages[0][1].Contents = "touch";
        var rewritten = writer.WriteReconciled(linked, file);

        Assert.DoesNotContain("RRM", Encoding.Latin1.GetString(rewritten));
        Assert.Equal("parent-1", ById(ReadBack(rewritten), "reply-1").InReplyTo);
    }

    [Fact]
    public void UnchangedResave_AppendsNothing()
    {
        var (file, _, _) = Thread();
        var writer = new PdfAnnotationWriter();
        var first = writer.WriteReconciled(Plain(), file);

        var second = writer.WriteReconciled(first, file);

        // The first save is PDFium's file plus one incremental update; the second finds every link
        // already in place, so it is a plain PDFium rewrite with a single cross-reference section.
        Assert.Equal(2, CountOf(first, "startxref"));
        Assert.Equal(1, CountOf(second, "startxref"));
        Assert.Equal("parent-1", ById(ReadBack(second), "reply-1").InReplyTo);
    }

    [Fact]
    public void PdfiumRewrite_OfLinkedFile_KeepsTheLink()
    {
        // A later save by PDFium (full rewrite of a file we incrementally updated) must still read back.
        var (file, _, reply) = Thread();
        var writer = new PdfAnnotationWriter();
        var linked = writer.WriteReconciled(Plain(), file);

        reply.Contents = "edited";                       // forces a real (non-link) change
        var edited = writer.WriteReconciled(linked, file);

        var back = ReadBack(edited);
        Assert.Equal("parent-1", ById(back, "reply-1").InReplyTo);
        Assert.Equal("edited", ById(back, "reply-1").EffectiveContents);
    }

    [Fact]
    public void OrphanedInReplyTo_IsDroppedNotDangling()
    {
        var reply = new TextNoteAnnotation { X = 72, Y = 100, Text = "re", InReplyTo = "ghost", NativeId = "reply-1" };

        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(reply));

        Assert.Null(ReadBack(bytes).Single().InReplyTo);
        Assert.DoesNotContain("/IRT", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void Reparenting_RewritesTheLink()
    {
        var a = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "a" };
        var b = new HighlightAnnotation { Rects = [new HighlightRect(72, 130, 80, 16)], NativeId = "b" };
        var reply = new TextNoteAnnotation { X = 72, Y = 100, Text = "re", InReplyTo = "a", NativeId = "r" };
        var file = AnnotationTestHelpers.OnePage(a, b, reply);
        var writer = new PdfAnnotationWriter();
        var bytes = writer.WriteReconciled(Plain(), file);
        Assert.Equal("a", ById(ReadBack(bytes), "r").InReplyTo);

        reply.InReplyTo = "b";
        bytes = writer.WriteReconciled(bytes, file);
        Assert.Equal("b", ById(ReadBack(bytes), "r").InReplyTo);

        reply.InReplyTo = null;
        bytes = writer.WriteReconciled(bytes, file);
        Assert.Null(ById(ReadBack(bytes), "r").InReplyTo);
    }

    [Fact]
    public void DeletingAMiddleComment_WithItsThread_LeavesNoDanglingLink()
    {
        var root = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "root" };
        var mid = new TextNoteAnnotation { X = 72, Y = 100, Text = "mid", InReplyTo = "root", NativeId = "mid" };
        var leaf = new TextNoteAnnotation { X = 72, Y = 100, Text = "leaf", InReplyTo = "mid", NativeId = "leaf" };
        var file = AnnotationTestHelpers.OnePage(root, mid, leaf);
        var writer = new PdfAnnotationWriter();
        var bytes = writer.WriteReconciled(Plain(), file);

        // What DocumentModel.RemoveAnnotation does: the comment goes together with the replies beneath it.
        file.Pages[0].Remove(mid);
        file.Pages[0].Remove(leaf);
        bytes = writer.WriteReconciled(bytes, file);

        var back = ReadBack(bytes);
        Assert.Equal("root", Assert.Single(back).NativeId);
    }

    [Fact]
    public void DeletingTheParent_WithoutRepointing_ClearsTheStaleLink()
    {
        var (file, parent, _) = Thread();
        var writer = new PdfAnnotationWriter();
        var bytes = writer.WriteReconciled(Plain(), file);

        file.Pages[0].Remove(parent);                     // foreign/legacy case: reply still names it
        bytes = writer.WriteReconciled(bytes, file);

        Assert.Null(ReadBack(bytes).Single().InReplyTo);
    }

    [Fact]
    public void RecreatingTheParent_RelinksItsReplies()
    {
        var (file, parent, _) = Thread();
        var writer = new PdfAnnotationWriter();
        var bytes = writer.WriteReconciled(Plain(), file);

        parent.Rects = [new HighlightRect(72, 100, 120, 16)];   // geometry edit → delete + recreate, same /NM
        bytes = writer.WriteReconciled(bytes, file);

        var back = ReadBack(bytes);
        Assert.Equal("parent-1", ById(back, "reply-1").InReplyTo);
    }

    [Fact]
    public void BrandNewReplyAndParent_WithoutNativeIds_AreNotLinkedUntilIdentified()
    {
        // A reply naming an id nothing carries is an orphan; once the parent has an id it links.
        var parent = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] };
        var reply = new TextNoteAnnotation { X = 72, Y = 100, Text = "re", InReplyTo = "later" };
        var file = AnnotationTestHelpers.OnePage(parent, reply);
        var writer = new PdfAnnotationWriter();
        var bytes = writer.WriteReconciled(Plain(), file);
        Assert.All(ReadBack(bytes), a => Assert.Null(a.InReplyTo));

        reply.InReplyTo = parent.NativeId;                       // minted by the first save
        bytes = writer.WriteReconciled(bytes, file);
        Assert.Equal(parent.NativeId, ReadBack(bytes).Single(a => a is TextNoteAnnotation).InReplyTo);
    }

    [Fact]
    public void ReplyAcrossPages_Links()
    {
        var parent = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "p" };
        var reply = new TextNoteAnnotation { X = 72, Y = 100, Text = "re", InReplyTo = "p", NativeId = "r" };
        var file = new AnnotationFile();
        file.Pages[0] = [parent];
        file.Pages[1] = [reply];

        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), file);

        var back = new PdfAnnotationReader().Read(bytes);
        Assert.Equal("p", back.Pages[1].Single().InReplyTo);
    }

    [Fact]
    public void EncryptedSource_StaysEncrypted_AndLinksRoundTrip()
    {
        var path = TestFixtures.GetEncryptedTestPdfPath();
        var source = File.ReadAllBytes(path);
        var (file, _, _) = Thread();

        var bytes = new PdfAnnotationWriter().WriteReconciled(source, file, TestFixtures.EncryptedPdfPassword);

        Assert.Contains("/Encrypt", Encoding.Latin1.GetString(bytes));
        var back = new PdfAnnotationReader().Read(bytes, TestFixtures.EncryptedPdfPassword);
        var list = back.Pages[0];
        Assert.Equal("parent-1", ById(list, "reply-1").InReplyTo);
        Assert.Equal("re", ById(list, "reply-1").EffectiveContents);   // string survived byte-for-byte
        Assert.Empty(new PdfAnnotationReader().Read(bytes).Pages);   // still locked without the password
    }

    [Fact]
    public void Output_IsStructurallyValid_AndOtherReadersSeeTheThread()
    {
        var (file, _, _) = Thread();
        var bytes = new PdfAnnotationWriter().WriteReconciled(Plain(), file);
        var dir = Directory.CreateTempSubdirectory("rr-irt-").FullName;
        try
        {
            var pdf = Path.Combine(dir, "threaded.pdf");
            File.WriteAllBytes(pdf, bytes);

            if (Tool("qpdf", $"--check \"{pdf}\"") is { } qpdf)
                Assert.True(qpdf.ExitCode == 0, qpdf.Output);   // no xref/trailer damage

            // mupdf reads /IRT natively; a dangling or malformed reference makes it warn or drop the link.
            if (Tool("mutool", $"show \"{pdf}\" trailer") is { } mutool)
                Assert.True(mutool.ExitCode == 0, mutool.Output);
            if (Tool("qpdf", $"--json=2 --json-key=objects \"{pdf}\"") is { } json && json.ExitCode == 0)
            {
                Assert.Contains("\"/IRT\"", json.Output);
                Assert.Contains("\"/RT\": \"/R\"", json.Output);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AnnotationWithAnIdButNoPdfCounterpart_IsMarkedPersistedOnceWritten()
    {
        // The state AddReply leaves a fresh annotation in: authored here, but already carrying an id.
        var root = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "minted" };
        var empty = new HighlightAnnotation { NativeId = "no-geometry" };      // writes nothing
        var file = AnnotationTestHelpers.OnePage(root, empty);
        Assert.Equal(AnnotationSource.RailReader, root.Source);

        var writer = new PdfAnnotationWriter();
        var bytes = writer.WriteReconciled(Plain(), file);

        Assert.Equal(AnnotationSource.InPdf, root.Source);
        Assert.Equal(AnnotationSource.RailReader, empty.Source);               // not flagged: nothing was written
        Assert.Equal("minted", Assert.Single(ReadBack(bytes)).NativeId);

        // Saved again, it is matched rather than recreated.
        var again = writer.WriteReconciled(bytes, file);
        Assert.Equal("minted", Assert.Single(ReadBack(again)).NativeId);
    }

    [Fact]
    public void LinkerBackOff_SavesWithoutLinks_AndWithoutLeftoverMarkers()
    {
        var (file, _, _) = Thread();
        var writer = new PdfAnnotationWriter { LinkApplier = (_, _) => null };   // as if the post-pass gave up

        var bytes = writer.WriteReconciled(Plain(), file);

        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("RRM", text);
        Assert.Equal(1, CountOf(bytes, "startxref"));
        var back = ReadBack(bytes);
        Assert.Equal(2, back.Count);                         // annotations all still written…
        Assert.All(back, a => Assert.Null(a.InReplyTo));     // …just unlinked

        // And a later, working save links them (the model kept its InReplyTo).
        bytes = new PdfAnnotationWriter().WriteReconciled(bytes, file);
        Assert.Equal("parent-1", ById(ReadBack(bytes), "reply-1").InReplyTo);
    }

    [Fact]
    public void LinkerRejectsInputItCannotExtend_WithoutClaimingSuccess()
    {
        var plan = new ReplyLinkPlan();
        plan.Links.Add((plan.MarkerFor("c"), plan.MarkerFor("p")));

        Assert.Null(PdfReplyLinker.Apply(Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj\n<< >>\nendobj\n"), plan));
        // Markers present in the plan but absent from the bytes can't be stripped either.
        var pdf = new PdfAnnotationWriter().WriteReconciled(Plain(), AnnotationTestHelpers.OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "x" }));
        Assert.Null(PdfReplyLinker.Apply(pdf, plan));
    }

    [Fact]
    public void FileWithoutReplies_SkipsTheLinkerEntirely()
    {
        var calls = 0;
        var writer = new PdfAnnotationWriter { LinkApplier = (b, _) => { calls++; return b; } };
        var file = AnnotationTestHelpers.OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], NativeId = "a" },
            new TextNoteAnnotation { X = 200, Y = 100, Text = "n", NativeId = "b" });

        var bytes = writer.WriteReconciled(Plain(), file);
        writer.WriteReconciled(bytes, file);

        Assert.Equal(0, calls);
    }

    private static (int ExitCode, string Output)? Tool(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;   // tool not installed: this check is best-effort interop coverage
        }
    }
}
