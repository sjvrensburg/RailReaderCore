using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Merging overlapping same-style text markup, and comments on text markup.</summary>
public class TextMarkupMergeTests : IDisposable
{
    private readonly DocumentModel _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler;

    public TextMarkupMergeTests()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _doc = new DocumentModel(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config.ToCoreSettings(), marshaller);
        _doc.LoadPageBitmap();
        _manager = new AnnotationFileManager(AnnotationService.Default, marshaller);
        _doc.LoadAnnotations(_manager);
        _handler = new AnnotationInteractionHandler();
    }

    public void Dispose()
    {
        _doc.Dispose();
        _manager.Dispose();
    }

    private static HighlightAnnotation Hl(string color = "#FFFF00", float opacity = 0.4f, params HighlightRect[] rects)
        => new() { Color = color, Opacity = opacity, Rects = [.. rects] };

    private List<Annotation> Page0 => _doc.Annotations.Pages[0];

    // --- UnionRects ---

    [Fact]
    public void UnionRects_JoinsOverlappingIntervalsOnOneLine()
    {
        var rects = TextMarkupMerge.UnionRects([new(10, 100, 50, 12), new(40, 101, 50, 11)]);
        var r = Assert.Single(rects);
        Assert.Equal(10, r.X); Assert.Equal(100, r.Y); Assert.Equal(80, r.W); Assert.Equal(12, r.H);
    }

    [Fact]
    public void UnionRects_KeepsDisjointIntervalsOnOneLineSeparate()
    {
        var rects = TextMarkupMerge.UnionRects([new(10, 100, 20, 12), new(60, 100, 20, 12)]);
        Assert.Equal(2, rects.Count);
        Assert.Equal(10, rects[0].X);
        Assert.Equal(60, rects[1].X);
    }

    [Fact]
    public void UnionRects_SeparateLinesStaySeparate_AndAreOrderedTopDown()
    {
        var rects = TextMarkupMerge.UnionRects([new(10, 130, 50, 12), new(10, 100, 50, 12), new(30, 100, 50, 12)]);
        Assert.Equal(2, rects.Count);
        Assert.Equal(100, rects[0].Y); Assert.Equal(70, rects[0].W);
        Assert.Equal(130, rects[1].Y); Assert.Equal(50, rects[1].W);
    }

    // --- Style / overlap rules ---

    [Fact]
    public void CanMerge_RequiresSameSubtypeColourOpacityAndOverlap()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 50, 12));
        Assert.True(TextMarkupMerge.CanMerge(a, Hl("#ffff00", 0.4f, new HighlightRect(40, 100, 50, 12)))); // colour case-insensitive
        Assert.False(TextMarkupMerge.CanMerge(a, Hl("#00A000", 0.4f, new HighlightRect(40, 100, 50, 12))));
        Assert.False(TextMarkupMerge.CanMerge(a, Hl("#FFFF00", 1f, new HighlightRect(40, 100, 50, 12))));
        Assert.False(TextMarkupMerge.CanMerge(a, Hl(rects: new HighlightRect(60, 100, 50, 12))));       // edge-touching only
        Assert.False(TextMarkupMerge.CanMerge(a, Hl(rects: new HighlightRect(10, 130, 50, 12))));       // next line
        var underline = new UnderlineAnnotation { Color = "#FFFF00", Opacity = 0.4f, Rects = [new(40, 100, 50, 12)] };
        Assert.False(TextMarkupMerge.CanMerge(a, underline));
        Assert.False(TextMarkupMerge.CanMerge(a, a));
    }

    [Fact]
    public void FindCluster_IsTransitive()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        var b = Hl(rects: new HighlightRect(30, 100, 30, 12));
        var c = Hl(rects: new HighlightRect(50, 100, 30, 12)); // overlaps b, not a
        var d = Hl(rects: new HighlightRect(200, 100, 30, 12)); // nothing
        var cluster = TextMarkupMerge.FindCluster([d, c, b, a], a);
        Assert.Equal([c, b, a], cluster); // page-list order
    }

    [Fact]
    public void TryBuildMerged_RefusesConflictingComments_AndKeepsAgreedOne()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12)); a.Contents = "first";
        var b = Hl(rects: new HighlightRect(30, 100, 30, 12)); b.Contents = "second";
        Assert.Equal(TextMarkupMerge.Refusal.ConflictingComments, TextMarkupMerge.TryBuildMerged([a, b], out var none));
        Assert.Null(none);

        b.Contents = "   ";
        Assert.Equal(TextMarkupMerge.Refusal.None, TextMarkupMerge.TryBuildMerged([a, b], out var merged));
        Assert.Equal("first", merged!.Contents);
        Assert.IsType<HighlightAnnotation>(merged);
        Assert.Null(merged.NativeId);
        Assert.Equal(AnnotationSource.RailReader, merged.Source);
        Assert.Equal(TextMarkupMerge.Refusal.NothingToMerge, TextMarkupMerge.TryBuildMerged([a], out _));
    }

    // --- DocumentModel operations ---

    [Fact]
    public void MergeOverlapping_Seed_ReplacesClusterWithOne_UndoRestoresOrder()
    {
        var below = new RectAnnotation { X = 0, Y = 0, W = 5, H = 5 };
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        var between = new RectAnnotation { X = 0, Y = 0, W = 5, H = 5 };
        var b = Hl(rects: new HighlightRect(30, 100, 30, 12));
        var other = Hl("#FF0000", 0.4f, new HighlightRect(30, 100, 30, 12));
        foreach (var ann in new Annotation[] { below, a, between, b, other }) _doc.AddAnnotation(0, ann);

        var merged = _doc.MergeOverlappingTextMarkup(0, a, out var refusal);
        Assert.NotNull(merged);
        Assert.Equal(TextMarkupMerge.Refusal.None, refusal);
        Assert.Equal([below, merged, between, other], Page0); // merged sits where `a` was
        var r = Assert.Single(merged!.Rects);
        Assert.Equal(10, r.X); Assert.Equal(50, r.W);

        _doc.Undo();
        Assert.Equal([below, a, between, b, other], Page0);
        _doc.Redo();
        Assert.Equal([below, merged, between, other], Page0);
    }

    [Fact]
    public void MergeOverlapping_NoPartner_ReturnsNullAndLeavesPage()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        _doc.AddAnnotation(0, a);
        int undoDepth = _doc.UndoStack.Count;
        Assert.Null(_doc.MergeOverlappingTextMarkup(0, a, out var refusal));
        Assert.Equal(TextMarkupMerge.Refusal.NothingToMerge, refusal);
        Assert.Equal([a], Page0);
        Assert.Equal(undoDepth, _doc.UndoStack.Count);
    }

    [Fact]
    public void MergeOverlapping_WholePage_MergesEachCluster_SkipsConflicts()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        var b = Hl(rects: new HighlightRect(30, 100, 30, 12));
        var c = Hl(rects: new HighlightRect(10, 200, 30, 12)); c.Contents = "x";
        var d = Hl(rects: new HighlightRect(30, 200, 30, 12)); d.Contents = "y";
        var e = Hl(rects: new HighlightRect(10, 300, 30, 12));
        var f = Hl(rects: new HighlightRect(30, 300, 30, 12));
        var g = Hl(rects: new HighlightRect(50, 300, 30, 12));
        foreach (var ann in new Annotation[] { a, b, c, d, e, f, g }) _doc.AddAnnotation(0, ann);

        Assert.Equal(3, _doc.MergeOverlappingTextMarkup(0));
        Assert.Equal(4, Page0.Count); // ab, c, d, efg
        Assert.Contains(c, Page0);
        Assert.Contains(d, Page0);
        Assert.Equal(0, _doc.MergeOverlappingTextMarkup(0)); // idempotent
    }

    [Fact]
    public void AddTextMarkupMerging_MergesWithExisting_AsOneUndoStep()
    {
        var existing = Hl(rects: new HighlightRect(10, 100, 30, 12));
        _doc.AddAnnotation(0, existing);
        int undoDepth = _doc.UndoStack.Count;

        var added = _doc.AddTextMarkupMerging(0, Hl(rects: new HighlightRect(30, 100, 30, 12)));
        Assert.NotNull(added);
        Assert.NotSame(existing, added);
        Assert.Equal([added!], Page0);
        Assert.Equal(undoDepth + 1, _doc.UndoStack.Count);

        _doc.Undo();
        Assert.Equal([existing], Page0); // the new markup was never added on its own
    }

    [Fact]
    public void AddTextMarkupMerging_NoOverlap_PlainAdd()
    {
        var fresh = Hl(rects: new HighlightRect(10, 100, 30, 12));
        Assert.Same(fresh, _doc.AddTextMarkupMerging(0, fresh));
        Assert.Equal([fresh], Page0);
    }

    [Fact]
    public void Handler_AutoMerge_OffByDefault_OnMergesCommit()
    {
        var vp = _doc.Primary;
        var pageText = _doc.GetOrExtractText(0);
        Assert.True(pageText.CharBoxes.Count > 20, "fixture page needs text");
        var c0 = pageText.CharBoxes[0];
        var c10 = pageText.CharBoxes[10];
        var c5 = pageText.CharBoxes[5];
        var c15 = pageText.CharBoxes[15];

        _handler.SetAnnotationTool(AnnotationTool.Highlight);
        _handler.HandleAnnotationPointerDown(vp, c0.Left, c0.Top);
        _handler.HandleAnnotationPointerMove(vp, c10.Left, c10.Top);
        _handler.HandleAnnotationPointerUp(vp, c10.Left, c10.Top);
        _handler.HandleAnnotationPointerDown(vp, c5.Left, c5.Top);
        _handler.HandleAnnotationPointerMove(vp, c15.Left, c15.Top);
        _handler.HandleAnnotationPointerUp(vp, c15.Left, c15.Top);
        Assert.Equal(2, Page0.Count); // default: stacked

        _handler.AutoMergeTextMarkup = true;
        _handler.HandleAnnotationPointerDown(vp, c5.Left, c5.Top);
        _handler.HandleAnnotationPointerMove(vp, c15.Left, c15.Top);
        _handler.HandleAnnotationPointerUp(vp, c15.Left, c15.Top);
        Assert.Single(Page0); // all three overlapped → one
    }

    [Fact]
    public void Handler_MergeSelected_SelectsMerged()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        var b = Hl(rects: new HighlightRect(30, 100, 30, 12));
        _doc.AddAnnotation(0, a); _doc.AddAnnotation(0, b);
        _handler.SelectedAnnotation = a;
        var merged = _handler.MergeSelectedTextMarkup(_doc.Primary, out _);
        Assert.NotNull(merged);
        Assert.Same(merged, _handler.SelectedAnnotation);
    }

    // --- Comments on markup ---

    [Fact]
    public void UpdateAnnotationContents_Markup_IsUndoable_AndStampsModified()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        _doc.AddAnnotation(0, a);
        _doc.UpdateAnnotationContents(0, a, "why this matters");
        Assert.Equal("why this matters", a.EffectiveContents);
        Assert.NotNull(a.ModifiedUtc);

        _doc.Undo();
        Assert.Equal("", a.EffectiveContents);
        _doc.Redo();
        Assert.Equal("why this matters", a.EffectiveContents);

        _doc.UpdateAnnotationContents(0, a, "");
        Assert.Null(a.Contents);
    }

    [Fact]
    public void UpdateAnnotationContents_Unchanged_PushesNoUndo()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        _doc.AddAnnotation(0, a);
        int depth = _doc.UndoStack.Count;
        _doc.UpdateAnnotationContents(0, a, null);
        _doc.UpdateAnnotationContents(0, a, "");
        _doc.UpdateAnnotationContents(0, a, "   "); // whitespace-only is "no comment", same as merge/renderer
        Assert.Equal(depth, _doc.UndoStack.Count);
        Assert.Null(a.Contents);
    }

    [Fact]
    public void MergeOverlapping_RepointsRepliesOfMergedSources_AndUndoRestoresThem()
    {
        var parent = Hl(rects: new HighlightRect(10, 100, 30, 12));
        parent.NativeId = "nm-parent"; parent.Source = AnnotationSource.InPdf;
        var reply = new TextNoteAnnotation { X = 15, Y = 106, Contents = "reply", InReplyTo = "nm-parent" };
        var other = Hl(rects: new HighlightRect(30, 100, 30, 12));
        foreach (var ann in new Annotation[] { parent, reply, other }) _doc.AddAnnotation(0, ann);
        int depth = _doc.UndoStack.Count;

        var merged = _doc.MergeOverlappingTextMarkup(0, other, out var refusal);
        Assert.NotNull(merged);
        Assert.Equal(TextMarkupMerge.Refusal.None, refusal);
        Assert.False(string.IsNullOrEmpty(merged!.NativeId));        // minted so the reply has something to point at
        Assert.Equal(merged.NativeId, reply.InReplyTo);              // thread follows the merged markup
        Assert.Equal(depth + 1, _doc.UndoStack.Count);               // still one undo step

        _doc.Undo();
        Assert.Equal("nm-parent", reply.InReplyTo);
        Assert.Null(merged.NativeId);
        Assert.Equal([parent, reply, other], Page0);

        _doc.Redo();
        Assert.Equal(merged.NativeId, reply.InReplyTo);
    }

    [Fact]
    public void MergeOverlapping_StillRefuses_WhenASourceIsItselfAReply()
    {
        var parent = Hl(rects: new HighlightRect(10, 100, 30, 12));
        var other = Hl(rects: new HighlightRect(30, 100, 30, 12));
        other.InReplyTo = "someone-else";
        foreach (var ann in new Annotation[] { parent, other }) _doc.AddAnnotation(0, ann);

        Assert.Null(_doc.MergeOverlappingTextMarkup(0, parent, out var refusal));
        Assert.Equal(TextMarkupMerge.Refusal.Threaded, refusal);
        Assert.Equal(0, _doc.MergeOverlappingTextMarkup(0)); // the sweep skips it too

        other.InReplyTo = null;
        Assert.NotNull(_doc.MergeOverlappingTextMarkup(0, parent, out refusal));
        Assert.Equal(TextMarkupMerge.Refusal.None, refusal);
    }

    [Fact]
    public void TryBuildMerged_CarriesSeedMetadata_WithFreshIdentity()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        a.NativeId = "nm-a"; a.Source = AnnotationSource.InPdf; a.Flags = 4; a.State = ReviewState.Accepted;
        a.ColorComponents = [1f, 1f, 0f]; a.Author = "  "; a.Subject = null;
        a.CreatedUtc = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var b = Hl(rects: new HighlightRect(30, 100, 30, 12));
        b.Author = "Reviewer"; b.Subject = "Comment on Text";
        b.CreatedUtc = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(TextMarkupMerge.Refusal.None, TextMarkupMerge.TryBuildMerged([a, b], out var merged));
        Assert.Null(merged!.NativeId);
        Assert.Equal(AnnotationSource.RailReader, merged.Source);
        Assert.Equal(4, merged.Flags);
        Assert.Equal(ReviewState.Accepted, merged.State);
        Assert.Equal([1f, 1f, 0f], merged.ColorComponents!);
        Assert.Equal("Reviewer", merged.Author);
        Assert.Equal("Comment on Text", merged.Subject);
        Assert.Equal(b.CreatedUtc, merged.CreatedUtc);
        Assert.NotNull(merged.ModifiedUtc);
    }

    [Fact]
    public void UpdateAnnotationText_Note_NowUndoable_KeepsTextAndContentsInSync()
    {
        var note = new TextNoteAnnotation { X = 50, Y = 50, Text = "old" };
        _doc.AddAnnotation(0, note);
        _doc.UpdateAnnotationText(0, note, "new");
        Assert.Equal("new", note.Text);
        Assert.Equal("new", note.Contents);
        _doc.Undo();
        Assert.Equal("old", note.EffectiveContents);
        Assert.Equal("old", note.Text);
    }

    [Fact]
    public void HandleBrowseClick_DoubleClickOnMarkup_ReturnsItForEditing_SingleClickDoesNot()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        _doc.AddAnnotation(0, a);
        var vp = _doc.Primary;

        var single = _handler.HandleBrowseClick(vp, 20, 106, isDoubleClick: false);
        Assert.False(single.Handled);
        Assert.Null(single.Edit);

        var dbl = _handler.HandleBrowseClick(vp, 20, 106, isDoubleClick: true);
        Assert.True(dbl.Handled);
        Assert.Same(a, dbl.Edit);
        Assert.Same(a, _handler.SelectedAnnotation);
    }

    [Fact]
    public void HandleBrowseClick_NoteInsideMarkup_NoteWins()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 100, 12));
        var note = new TextNoteAnnotation { X = 50, Y = 106, Text = "n" };
        _doc.AddAnnotation(0, note);
        _doc.AddAnnotation(0, a); // markup drawn later still loses to the note
        var dbl = _handler.HandleBrowseClick(_doc.Primary, 50, 106, isDoubleClick: true);
        Assert.Same(note, dbl.Edit);
    }

    [Fact]
    public void CompleteCommentEdit_SetsMarkupComment()
    {
        var a = Hl(rects: new HighlightRect(10, 100, 30, 12));
        _doc.AddAnnotation(0, a);
        _handler.CompleteCommentEdit(_doc.Primary, a, "noted");
        Assert.Equal("noted", a.Contents);
    }

    // --- Renderer ---

    [Fact]
    public void Renderer_CommentedHighlight_DrawsGlyph_AndBodyWhenSelected()
    {
        var h = Hl(rects: new HighlightRect(10, 30, 40, 12));
        static SKBitmap Draw(HighlightAnnotation hl, bool selected)
        {
            var bmp = new SKBitmap(200, 120);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            AnnotationRenderer.DrawAnnotation(canvas, hl, selected);
            canvas.Flush();
            return bmp;
        }

        var glyph = AnnotationRenderer.GetMarkupCommentGlyphRect(h)!.Value;
        // Sample the coloured fold corner (top-right): the glyph's paper fill is white on this
        // white canvas. Sits above the rect's top edge (y=30) and right of its end (x=50).
        int gx = (int)(glyph.Right - 1), gy = (int)(glyph.Top + 1);
        Assert.True(gy < 30 && gx > 50);

        using (var plain = Draw(h, selected: false))
            Assert.Equal(SKColors.White, plain.GetPixel(gx, gy)); // no comment → no glyph

        h.Contents = "a comment";
        using (var withGlyph = Draw(h, selected: false))
        {
            Assert.NotEqual(SKColors.White, withGlyph.GetPixel(gx, gy));
            // Popup would sit right of the glyph; unselected → not drawn.
            Assert.Equal(SKColors.White, withGlyph.GetPixel((int)glyph.Right + 10, (int)glyph.Bottom + 8));
        }
        using (var selected = Draw(h, selected: true))
            Assert.NotEqual(SKColors.White, selected.GetPixel((int)glyph.Right + 10, (int)glyph.Bottom + 8));
    }
}
