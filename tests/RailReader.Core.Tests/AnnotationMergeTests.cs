using System.Text.Json;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// <see cref="AnnotationService.MergeInto(AnnotationFile, AnnotationFile, bool)"/> with
/// <c>skipEquivalent</c> (#168): importing a copy of a document's annotations adds only what the
/// target lacks, and keeps reply threads attached.
/// </summary>
public class AnnotationMergeTests
{
    private static AnnotationFile Clone(AnnotationFile f) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(f, RailReaderJsonContext.Default.AnnotationFile),
        RailReaderJsonContext.Default.AnnotationFile)!;

    private static HighlightAnnotation Highlight(float y, string? id = null) =>
        new() { Rects = [new HighlightRect(72, y, 80, 16)], NativeId = id };

    private static TextNoteAnnotation Note(string text, string? id = null, string? inReplyTo = null) =>
        new() { X = 72, Y = 100, Text = text, Contents = text, NativeId = id, InReplyTo = inReplyTo };

    private static Annotation ById(IEnumerable<Annotation> l, string id) => l.Single(a => a.NativeId == id);

    [Fact]
    public void SameFileMergedIntoItself_AddsNothing()
    {
        var file = AnnotationTestHelpers.OnePage(
            Highlight(100), new RectAnnotation { X = 10, Y = 10, W = 20, H = 20 },
            Highlight(140, "root"), Note("re", "reply", "root"));
        file.Pages[2] = [new FreehandAnnotation { Points = [new PointF(1, 1), new PointF(5, 5)] }];
        file.Bookmarks.Add(new BookmarkEntry { Name = "Intro", Page = 0 });

        int added = AnnotationService.MergeInto(file, Clone(file), skipEquivalent: true);

        Assert.Equal(0, added);
        Assert.Equal(4, file.Pages[0].Count);
        Assert.Single(file.Pages[2]);
        Assert.Single(file.Bookmarks);
    }

    [Fact]
    public void TwoArgumentOverload_StillAppendsEverything()
    {
        var file = AnnotationTestHelpers.OnePage(Highlight(100));

        int added = AnnotationService.MergeInto(file, Clone(file));

        Assert.Equal(1, added);
        Assert.Equal(2, file.Pages[0].Count);
    }

    [Fact]
    public void SidecarAnnotationWithoutNm_EquivalentToTarget_IsSkipped()
    {
        var target = AnnotationTestHelpers.OnePage(Highlight(100, "in-pdf-1"));
        target.Pages[0][0].Source = AnnotationSource.InPdf;
        // The same highlight from a read-only copy's sidecar: no /NM, geometry within tolerance.
        var imported = AnnotationTestHelpers.OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72.3f, 100, 80, 16)] });

        Assert.Equal(0, AnnotationService.MergeInto(target, imported, skipEquivalent: true));
        Assert.Single(target.Pages[0]);
    }

    [Fact]
    public void Matching_IsSamePageOnly_AndOneToOne()
    {
        var target = AnnotationTestHelpers.OnePage(Highlight(100));
        var imported = AnnotationTestHelpers.OnePage(Highlight(100), Highlight(100));
        imported.Pages[1] = [Highlight(100)];

        int added = AnnotationService.MergeInto(target, imported, skipEquivalent: true);

        Assert.Equal(2, added);
        Assert.Equal(2, target.Pages[0].Count); // the second equal copy is the import's own
        Assert.Single(target.Pages[1]);         // an equal annotation on another page is not a duplicate
    }

    [Fact]
    public void SameNm_IsADuplicate_EvenWhenContentDiffers()
    {
        var target = AnnotationTestHelpers.OnePage(Highlight(100, "nm-1"));
        var imported = AnnotationTestHelpers.OnePage(Highlight(300, "nm-1"));

        Assert.Equal(0, AnnotationService.MergeInto(target, imported, skipEquivalent: true));
        Assert.Single(target.Pages[0]);
    }

    [Fact]
    public void InPdfAnnotations_SharedOnesSkipped_NewOneWrittenIntoThePdf()
    {
        var writer = new PdfAnnotationWriter();
        var reader = new PdfAnnotationReader();
        var shared = Highlight(100, "shared");
        var targetBytes = writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), AnnotationTestHelpers.OnePage(shared));
        var importBytes = writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), AnnotationTestHelpers.OnePage(
            Highlight(100, "shared"), new RectAnnotation { X = 200, Y = 300, W = 50, H = 40, NativeId = "only-import" }));

        var target = reader.Read(targetBytes);
        var imported = reader.Read(importBytes);
        Assert.All(imported.Pages[0], a => Assert.Equal(AnnotationSource.InPdf, a.Source));

        int added = AnnotationService.MergeInto(target, imported, skipEquivalent: true);

        Assert.Equal(1, added);
        var fresh = ById(target.Pages[0], "only-import");
        Assert.Equal(AnnotationSource.RailReader, fresh.Source); // so the writer writes it

        var back = AnnotationTestHelpers.ReadBack(writer.WriteReconciled(targetBytes, target));
        Assert.Equal(2, back.Count);
        Assert.IsType<RectAnnotation>(ById(back, "only-import"));
        Assert.Single(back, a => a.NativeId == "shared");
    }

    [Fact]
    public void ReplyToSkippedRoot_AttachesToTargetRoot_AndSurvivesWriteAndReload()
    {
        var writer = new PdfAnnotationWriter();
        var targetBytes = writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(),
            AnnotationTestHelpers.OnePage(Highlight(100, "target-root")));
        var target = new PdfAnnotationReader().Read(targetBytes);

        // The other machine's copy: the same highlight under another /NM, now with a reply.
        var imported = AnnotationTestHelpers.OnePage(Highlight(100, "import-root"), Note("agreed", "import-reply", "import-root"));

        Assert.Equal(1, AnnotationService.MergeInto(target, imported, skipEquivalent: true));
        Assert.Equal("target-root", ById(target.Pages[0], "import-reply").InReplyTo);

        var back = AnnotationTestHelpers.ReadBack(writer.WriteReconciled(targetBytes, target));
        Assert.Equal(2, back.Count);
        Assert.Equal("target-root", ById(back, "import-reply").InReplyTo);
    }

    [Fact]
    public void ReplyToSkippedRoot_WithoutNm_MintsTheRootsId()
    {
        // A sidecar annotation of a read-only PDF: authored, never written, so no /NM yet.
        var target = AnnotationTestHelpers.OnePage(Highlight(100));
        var imported = AnnotationTestHelpers.OnePage(Highlight(100, "import-root"), Note("agreed", inReplyTo: "import-root"));

        Assert.Equal(1, AnnotationService.MergeInto(target, imported, skipEquivalent: true));

        var root = target.Pages[0][0];
        Assert.False(string.IsNullOrEmpty(root.NativeId));
        Assert.Equal(root.NativeId, target.Pages[0][1].InReplyTo);
        Assert.Single(AnnotationThreads.Build(target));
    }

    [Fact]
    public void ReplyToSkippedInPdfRootWithoutNm_IsAddedUnthreaded()
    {
        var target = AnnotationTestHelpers.OnePage(Highlight(100));
        target.Pages[0][0].Source = AnnotationSource.InPdf; // unidentifiable to the writer
        var imported = AnnotationTestHelpers.OnePage(Highlight(100, "import-root"), Note("agreed", inReplyTo: "import-root"));

        Assert.Equal(1, AnnotationService.MergeInto(target, imported, skipEquivalent: true));

        Assert.Null(target.Pages[0][0].NativeId);
        Assert.Null(target.Pages[0][1].InReplyTo);
    }

    [Fact]
    public void DuplicateReply_IsSkipped_ButAnEqualReplyInAnotherThreadIsNot()
    {
        // Two roots sharing a top-left corner, so replies placed there are equal in content. The
        // replies carry no /NM, so only content (and the parent) can match them.
        var target = AnnotationTestHelpers.OnePage(Highlight(100, "a"), Note("ok", inReplyTo: "a"),
            new RectAnnotation { X = 72, Y = 100, W = 30, H = 30, NativeId = "b" });
        var imported = Clone(target);
        // First, so it meets the target's reply to "a" while that is still unclaimed.
        imported.Pages[0].Insert(0, Note("ok", inReplyTo: "b"));

        int added = AnnotationService.MergeInto(target, imported, skipEquivalent: true);

        Assert.Equal(1, added);
        Assert.Equal("b", target.Pages[0][^1].InReplyTo);
        var threads = AnnotationThreads.Build(target);
        Assert.Equal(2, threads.Count);
        Assert.All(threads, t => Assert.Single(t.Replies));
    }

    [Fact]
    public void NewReplyUnderNewRoot_KeepsItsLink_EvenWhenTheRootsNmCollides()
    {
        var target = AnnotationTestHelpers.OnePage(Highlight(100, "r"));
        target.Pages[1] = [Highlight(500, "x")];
        // A new root whose /NM collides with an unrelated target annotation on another page.
        var imported = AnnotationTestHelpers.OnePage(new RectAnnotation { X = 300, Y = 300, W = 10, H = 10, NativeId = "x" },
            Note("new thread", "new-reply", "x"), Note("other", inReplyTo: "r2"));
        imported.Pages[0].Add(Highlight(200, "r2"));

        int added = AnnotationService.MergeInto(target, imported, skipEquivalent: true);

        Assert.Equal(4, added);
        var newRoot = target.Pages[0].OfType<RectAnnotation>().Single();
        Assert.NotEqual("x", newRoot.NativeId);
        Assert.False(string.IsNullOrEmpty(newRoot.NativeId));
        Assert.Equal(newRoot.NativeId, ById(target.Pages[0], "new-reply").InReplyTo);
        Assert.Equal("r2", target.Pages[0].Single(a => a.Contents == "other").InReplyTo);

        var threads = AnnotationThreads.Build(target, 0);
        Assert.Contains(threads, t => ReferenceEquals(t.Root, newRoot) && t.Replies.Count == 1);
    }

    [Fact]
    public void NewThread_SurvivesWriteAndReload()
    {
        var writer = new PdfAnnotationWriter();
        var targetBytes = writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(),
            AnnotationTestHelpers.OnePage(Highlight(100, "kept")));
        var target = new PdfAnnotationReader().Read(targetBytes);
        var imported = AnnotationTestHelpers.OnePage(Highlight(100, "kept"),
            Highlight(300, "new-root"), Note("first", "new-reply", "new-root"));
        imported.Pages[0][0].Source = AnnotationSource.InPdf;

        Assert.Equal(2, AnnotationService.MergeInto(target, imported, skipEquivalent: true));

        var back = AnnotationTestHelpers.ReadBack(writer.WriteReconciled(targetBytes, target));
        Assert.Equal(3, back.Count);
        Assert.Equal("new-root", ById(back, "new-reply").InReplyTo);
    }
}
