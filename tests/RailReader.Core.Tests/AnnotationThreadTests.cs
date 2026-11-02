using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Reply threads (#151): grouping, safety against malformed links, cascading delete, undo.</summary>
public class AnnotationThreadTests : IDisposable
{
    private readonly DocumentModel _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler = new();

    public AnnotationThreadTests()
    {
        var config = new AppConfig();
        var marshaller = new QueuedThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _doc = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config.ToCoreSettings(), marshaller);
        _doc.LoadPageBitmap();
        _manager = new AnnotationFileManager(AnnotationService.Default, marshaller);
        _doc.LoadAnnotations(_manager);
    }

    public void Dispose()
    {
        _doc.Dispose();
        _manager.Dispose();
    }

    private static TextNoteAnnotation Note(string? id = null, string? irt = null, DateTimeOffset? at = null)
        => new() { NativeId = id, InReplyTo = irt, CreatedUtc = at, X = 10, Y = 10, Text = "n" };

    private static AnnotationFile File(params Annotation[] page0)
    {
        var f = new AnnotationFile();
        f.Pages[0] = [.. page0];
        return f;
    }

    // --- AnnotationThreads.Build ---

    [Fact]
    public void Build_GroupsRepliesUnderRoot_OrderedByDateThenDocumentOrder()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var root = Note("r");
        var late = Note("a", "r", t0.AddHours(2));
        var early = Note("b", "r", t0);
        var nested = Note("c", "a", t0.AddHours(1));      // reply to a reply: still in r's flat thread
        var tie1 = Note("d", "r", t0.AddHours(3));
        var tie2 = Note("e", "r", t0.AddHours(3));

        var threads = AnnotationThreads.Build(File(root, late, early, nested, tie1, tie2));

        var t = Assert.Single(threads);
        Assert.Same(root, t.Root);
        Assert.Equal([early, nested, late, tie1, tie2], t.Replies);
    }

    [Fact]
    public void Build_TreatsDanglingSelfAndCyclicLinksAsRoots_WithoutLooping()
    {
        var dangling = Note("d", "missing");
        var self = Note("s", "s");
        var a = Note("a", "b");
        var b = Note("b", "a");           // a <-> b cycle
        var tail = Note("t", "b");        // hangs off the cycle

        var threads = AnnotationThreads.Build(File(dangling, self, a, b, tail));

        Assert.Equal([dangling, self, a], threads.Select(t => t.Root));   // cycle cut at its earliest member
        Assert.Equal([b, tail], threads[2].Replies.OrderBy(r => r == tail));
        Assert.Empty(threads[0].Replies);
        Assert.Empty(threads[1].Replies);
    }

    [Fact]
    public void Build_PageFilter_KeepsRepliesFromOtherPages()
    {
        var f = new AnnotationFile();
        var root0 = Note("r");
        var root1 = Note("x");
        f.Pages[0] = [root0];
        f.Pages[1] = [root1, Note("y", "r")];

        var p0 = Assert.Single(AnnotationThreads.Build(f, 0));
        Assert.Same(root0, p0.Root);
        Assert.Single(p0.Replies);
        Assert.Single(AnnotationThreads.Build(f, 1));
        Assert.Empty(AnnotationThreads.Build(f, 2));
    }

    [Fact]
    public void Build_DuplicateIds_FirstWinsAndNothingThrows()
    {
        var first = Note("dup");
        var second = Note("dup");
        var reply = Note("r", "dup");

        var threads = AnnotationThreads.Build(File(first, second, reply));

        Assert.Equal(2, threads.Count);
        Assert.Same(reply, Assert.Single(threads[0].Replies));
        Assert.Empty(threads[1].Replies);
    }

    // --- CreateReply / AddReply ---

    [Fact]
    public void AddReply_MintsParentId_StampsAuthor_AndLinks()
    {
        var parent = new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] };
        _doc.AddAnnotation(0, parent);
        Assert.Null(parent.NativeId);

        var reply = _doc.AddReply(parent, "I agree", "Ada");

        Assert.NotNull(reply);
        Assert.False(string.IsNullOrEmpty(parent.NativeId));
        Assert.Equal(parent.NativeId, reply!.InReplyTo);
        Assert.Equal("Ada", reply.Author);
        Assert.NotNull(reply.CreatedUtc);
        Assert.Equal("I agree", reply.EffectiveContents);
        Assert.Equal(72, reply.X);
        Assert.Contains(reply, _doc.Annotations.Pages[0]);
    }

    [Fact]
    public void AddReply_ToAReply_AttachesToRoot_SoThreadsStayFlat()
    {
        var root = new RectAnnotation { X = 10, Y = 10, W = 40, H = 40 };
        _doc.AddAnnotation(0, root);
        var first = _doc.AddReply(root, "one")!;
        var second = _doc.AddReply(first, "two")!;

        Assert.Equal(root.NativeId, second.InReplyTo);
        var thread = Assert.Single(_doc.GetThreads(0));
        Assert.Equal([first, second], thread.Replies);
    }

    [Fact]
    public void AddReply_RefusesBlankTextForeignParentAndRotatedView()
    {
        var root = new RectAnnotation { X = 10, Y = 10, W = 40, H = 40 };
        _doc.AddAnnotation(0, root);
        int depth = _doc.UndoStack.Count;

        Assert.Null(_doc.AddReply(root, "  "));
        Assert.Null(_doc.AddReply(new RectAnnotation(), "orphan parent"));
        Assert.Equal(depth, _doc.UndoStack.Count);
        Assert.Null(root.NativeId);
    }

    [Fact]
    public void AddReply_UndoRedo_IsOneStep_AndUnmintsTheParentId()
    {
        var root = new RectAnnotation { X = 10, Y = 10, W = 40, H = 40 };
        _doc.AddAnnotation(0, root);
        int depth = _doc.UndoStack.Count;

        var reply = _doc.AddReply(root, "hello")!;
        var id = root.NativeId;
        Assert.Equal(depth + 1, _doc.UndoStack.Count);

        _doc.Undo();
        Assert.DoesNotContain(reply, _doc.Annotations.Pages[0]);
        Assert.Null(root.NativeId);

        _doc.Redo();
        Assert.Contains(reply, _doc.Annotations.Pages[0]);
        Assert.Equal(id, root.NativeId);
        Assert.Equal(id, reply.InReplyTo);
    }

    // --- delete cascades ---

    [Fact]
    public void RemoveAnnotation_RemovesTheWholeSubtree_AndUndoRestoresItInPlace()
    {
        var f = _doc.Annotations;
        var other = Note("o");
        var root = Note("r");
        var mid = Note("m", "r");
        var leaf = Note("l", "m");
        var sibling = Note("s", "r");
        f.Pages[0] = [other, root, mid, leaf, sibling];
        int depth = _doc.UndoStack.Count;

        _doc.RemoveAnnotation(0, mid);                       // mid and its reply go; root and sibling stay
        Assert.Equal([other, root, sibling], f.Pages[0]);
        Assert.Equal(depth + 1, _doc.UndoStack.Count);       // one step

        _doc.Undo();
        Assert.Equal([other, root, mid, leaf, sibling], f.Pages[0]);

        _doc.RemoveAnnotation(0, root);                      // whole thread
        Assert.Equal([other], f.Pages[0]);
        _doc.Undo();
        Assert.Equal([other, root, mid, leaf, sibling], f.Pages[0]);
        _doc.Redo();
        Assert.Equal([other], f.Pages[0]);
    }

    [Fact]
    public void RemoveAnnotation_TakesRepliesOnOtherPages_AndRestoresThem()
    {
        var f = _doc.Annotations;
        var root = Note("r");
        var far = Note("f", "r");
        f.Pages[0] = [root];
        f.Pages[1] = [Note("x"), far];

        _doc.RemoveAnnotation(0, root);
        Assert.Empty(f.Pages[0]);
        Assert.DoesNotContain(far, f.Pages[1]);

        _doc.Undo();
        Assert.Equal([root], f.Pages[0]);
        Assert.Equal(far, f.Pages[1][1]);
    }

    [Fact]
    public void RemoveAnnotation_InACycleOrWithoutAnId_TerminatesAndIsolatesTheSubtree()
    {
        var a = Note("a", "b");
        var b = Note("b", "a");
        var plain = Note();                                  // no id: cannot have replies
        var stray = Note("z", "missing");
        _doc.Annotations.Pages[0] = [a, b, plain, stray];

        _doc.RemoveAnnotation(0, a);
        Assert.Equal([plain, stray], _doc.Annotations.Pages[0]);

        _doc.RemoveAnnotation(0, plain);
        Assert.Equal([stray], _doc.Annotations.Pages[0]);
    }

    [Fact]
    public void RemoveAnnotation_NotOnThePage_RemovesNothing()
    {
        var root = Note("r");
        var reply = Note("a", "r");
        _doc.Annotations.Pages[0] = [reply];

        _doc.RemoveAnnotation(0, root);                      // stale: root is not here
        Assert.Equal([reply], _doc.Annotations.Pages[0]);
        _doc.Undo();
        Assert.Equal([reply], _doc.Annotations.Pages[0]);
    }

    // --- edit ---

    [Fact]
    public void EditingAnotherAuthorsReply_PreservesItsAuthor_AndIsUndoable()
    {
        var root = new RectAnnotation { X = 10, Y = 10, W = 40, H = 40 };
        _doc.AddAnnotation(0, root);
        var reply = _doc.AddReply(root, "typo", "Grace")!;
        var created = reply.CreatedUtc;

        _handler.Author = "Ada";
        _handler.CompleteCommentEdit(_doc.Primary, reply, "fixed");

        Assert.Equal("fixed", reply.EffectiveContents);
        Assert.Equal("Grace", reply.Author);
        Assert.Equal(created, reply.CreatedUtc);
        Assert.NotNull(reply.ModifiedUtc);

        _doc.Undo();
        Assert.Equal("typo", reply.EffectiveContents);
    }

    // --- groups (/RT /Group: Acrobat's Replace Text is a caret with a strikeout grouped to it) ---

    private static CaretAnnotation GroupCaret(string id = "c") => new() { NativeId = id, X = 10, Y = 10, W = 8, H = 7 };

    private static StrikeOutAnnotation GroupStrike(string id = "s", string leader = "c")
        => new() { NativeId = id, InReplyTo = leader, ReplyType = AnnotationReplyType.Group, Rects = [new HighlightRect(10, 10, 40, 10)] };

    [Fact]
    public void Build_ListsGroupMembersWithTheirLeader_NotAsThreads()
    {
        var caret = GroupCaret();
        var strike = GroupStrike();
        var reply = Note("r", "c");

        var t = Assert.Single(AnnotationThreads.Build(File(caret, strike, reply)));

        Assert.Same(caret, t.Root);
        Assert.Equal([strike], t.Grouped);
        Assert.Equal([reply], t.Replies);
    }

    [Fact]
    public void Build_AReplyToAGroupMember_JoinsTheLeadersThread()
    {
        var caret = GroupCaret();
        var strike = GroupStrike();
        var toStrike = Note("r", "s");

        var t = Assert.Single(AnnotationThreads.Build(File(caret, strike, toStrike)));

        Assert.Same(caret, t.Root);
        Assert.Equal([toStrike], t.Replies);
        Assert.Same(caret, AnnotationThreads.RootOf(File(caret, strike, toStrike), toStrike));
    }

    [Fact]
    public void RootOf_AGroupMember_IsItsLeadersRoot()
    {
        var root = Note("n");
        var caret = GroupCaret();
        caret.InReplyTo = "n";                      // the group itself replies to a note
        var strike = GroupStrike();
        var f = File(root, caret, strike);

        Assert.Same(root, AnnotationThreads.RootOf(f, strike));
        Assert.Same(root, AnnotationThreads.RootOf(f, caret));
    }

    [Fact]
    public void Build_DanglingOrCyclicGroupLinks_LeaveTheAnnotationUngrouped()
    {
        var dangling = GroupStrike("d", "missing");
        var a = GroupStrike("a", "b");
        var b = GroupStrike("b", "a");              // a ring of group links has no leader

        var threads = AnnotationThreads.Build(File(dangling, a, b));

        Assert.Equal([dangling, a, b], threads.Select(t => t.Root));
        Assert.All(threads, t => Assert.Empty(t.Grouped));
    }

    [Fact]
    public void Descendants_OfAGroupMember_IsTheWholeGroupAndItsReplies()
    {
        var caret = GroupCaret();
        var strike = GroupStrike();
        var second = GroupStrike("s2");
        var reply = Note("r", "c");
        var other = Note("o");

        var doomed = AnnotationThreads.Descendants(File(caret, strike, second, reply, other), strike);

        Assert.Equal(new HashSet<Annotation>([caret, second, reply]), doomed.ToHashSet());
    }

    [Fact]
    public void AddReply_ToAGroupMember_RepliesToTheLeader()
    {
        var caret = GroupCaret();
        var strike = GroupStrike();
        _doc.Annotations.Pages[0] = [caret, strike];

        var reply = _doc.AddReply(strike, "why strike this?")!;

        Assert.Equal("c", reply.InReplyTo);
        Assert.Equal(AnnotationReplyType.Reply, reply.ReplyType);
        Assert.Equal([reply], Assert.Single(_doc.GetThreads(0)).Replies);
    }

    [Fact]
    public void RemoveAnnotation_OfAGroupMember_RemovesTheGroup_AndUndoRestoresIt()
    {
        var f = _doc.Annotations;
        var other = Note("o");
        var caret = GroupCaret();
        var strike = GroupStrike();
        var reply = Note("r", "c");
        f.Pages[0] = [other, caret, strike, reply];

        _doc.RemoveAnnotation(0, strike);
        Assert.Equal([other], f.Pages[0]);

        _doc.Undo();
        Assert.Equal([other, caret, strike, reply], f.Pages[0]);
    }
}
