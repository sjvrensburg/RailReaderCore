using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>#127 part 2 — clone helper, handler clipboard, and the /NM-collision guard.</summary>
public class AnnotationCopyPasteTests : IDisposable
{
    private readonly DocumentModel _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler = new();

    public AnnotationCopyPasteTests()
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
    }

    public void Dispose()
    {
        _doc.Dispose();
        _manager.Dispose();
    }

    private Viewport Vp => _doc.Primary;
    private List<Annotation> Page0 => _doc.Annotations.Pages[0];

    public static TheoryData<Func<Annotation>> Copyable => new()
    {
        () => new TextNoteAnnotation { X = 100, Y = 100, Text = "n" },
        () => new FreeTextAnnotation { X = 100, Y = 100, W = 80, H = 30, Contents = "ft" },
        () => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 },
        () => new FreehandAnnotation { Points = [new PointF(100, 100), new PointF(150, 140)] },
    };

    private Annotation AddAndSelect(Func<Annotation> make)
    {
        var a = make();
        a.Color = "#00FF00"; a.Opacity = 0.4f; a.Author = "me"; a.Contents ??= "body";
        _doc.AddAnnotation(0, a);
        _handler.SelectedAnnotation = a;
        return a;
    }

    // --- CloneForPaste / IsCopyable ---

    [Fact]
    public void CloneForPaste_ResetsIdentityAndKeepsContent()
    {
        var src = new RectAnnotation
        {
            X = 10, Y = 20, W = 30, H = 40, Color = "#123456", Opacity = 0.3f, Author = "a",
            Contents = "c", ColorComponents = [0.1f, 0.2f, 0.3f], Flags = 4, State = ReviewState.Accepted,
            NativeId = "nm-1", Source = AnnotationSource.InPdf, InReplyTo = "p",
            CreatedUtc = DateTimeOffset.UnixEpoch, ModifiedUtc = DateTimeOffset.UnixEpoch,
        };

        var c = Assert.IsType<RectAnnotation>(Annotation.CloneForPaste(src));

        Assert.NotSame(src, c);
        Assert.Null(c.NativeId);
        Assert.Equal(AnnotationSource.RailReader, c.Source);
        Assert.Null(c.InReplyTo);
        Assert.Null(c.ModifiedUtc);
        Assert.True(c.CreatedUtc > DateTimeOffset.UnixEpoch);
        Assert.Equal((10f, 20f, 30f, 40f), (c.X, c.Y, c.W, c.H));
        Assert.Equal("#123456", c.Color);
        Assert.Equal(0.3f, c.Opacity);
        Assert.Equal("a", c.Author);
        Assert.Equal("c", c.Contents);
        Assert.Equal([0.1f, 0.2f, 0.3f], c.ColorComponents!);
        Assert.Equal(4, c.Flags);
        Assert.Equal(ReviewState.Accepted, c.State);
        Assert.Equal("nm-1", src.NativeId); // source untouched
    }

    [Fact]
    public void IsCopyable_MatchesTable()
    {
        Assert.True(Annotation.IsCopyable(new TextNoteAnnotation()));
        Assert.True(Annotation.IsCopyable(new FreeTextAnnotation()));
        Assert.True(Annotation.IsCopyable(new RectAnnotation()));
        Assert.True(Annotation.IsCopyable(new FreehandAnnotation { Points = [new PointF(1, 1)] }));
        Assert.False(Annotation.IsCopyable(new HighlightAnnotation()));
        Assert.False(Annotation.IsCopyable(new UnderlineAnnotation()));
        Assert.False(Annotation.IsCopyable(new CaretAnnotation()));
        Assert.False(Annotation.IsCopyable(null));
    }

    // --- Copy / cut ---

    [Fact]
    public void Copy_MarkupCaretOrNothing_ReturnsFalse()
    {
        Assert.False(_handler.CopySelectedAnnotation(Vp));

        _handler.SelectedAnnotation = new HighlightAnnotation { Rects = [new HighlightRect(1, 1, 5, 5)] };
        Assert.False(_handler.CopySelectedAnnotation(Vp));
        _handler.SelectedAnnotation = new CaretAnnotation();
        Assert.False(_handler.CopySelectedAnnotation(Vp));
        Assert.False(_handler.HasAnnotationClipboard);
    }

    [Fact]
    public void Cut_RemovesSelectionAndFillsClipboard_MarkupRefused()
    {
        var rect = AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        Assert.True(_handler.CutSelectedAnnotation(Vp));
        Assert.DoesNotContain(rect, Page0);
        Assert.True(_handler.HasAnnotationClipboard);

        var hl = new HighlightAnnotation { Rects = [new HighlightRect(1, 1, 5, 5)] };
        _doc.AddAnnotation(0, hl);
        _handler.SelectedAnnotation = hl;
        Assert.False(_handler.CutSelectedAnnotation(Vp));
        Assert.Contains(hl, Page0);
    }

    // --- Paste ---

    [Fact]
    public void Paste_WithEmptyClipboard_ReturnsNull()
        => Assert.Null(_handler.PasteAnnotation(Vp));

    [Theory]
    [MemberData(nameof(Copyable))]
    public void Paste_SamePage_OffsetsAddsSelectsAndUndoRedoWork(Func<Annotation> make)
    {
        var src = AddAndSelect(make);
        var srcBounds = AnnotationGeometry.GetAnnotationBounds(src)!.Value;
        Assert.True(_handler.CopySelectedAnnotation(Vp));

        var pasted = _handler.PasteAnnotation(Vp);

        Assert.NotNull(pasted);
        Assert.NotSame(src, pasted);
        Assert.IsType(src.GetType(), pasted);
        Assert.Contains(pasted, Page0);
        Assert.Same(pasted, _handler.SelectedAnnotation);
        var b = AnnotationGeometry.GetAnnotationBounds(pasted)!.Value;
        Assert.Equal(srcBounds.Left + 10, b.Left, 3);
        Assert.Equal(srcBounds.Top + 10, b.Top, 3);
        Assert.Equal("#00FF00", pasted.Color);

        _doc.Undo();
        Assert.DoesNotContain(pasted, Page0);
        Assert.Contains(src, Page0);
        _doc.Redo();
        Assert.Contains(pasted, Page0);
    }

    [Fact]
    public void Paste_Twice_YieldsTwoDistinctAnnotations()
    {
        AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        _handler.CopySelectedAnnotation(Vp);

        var a = _handler.PasteAnnotation(Vp)!;
        var b = _handler.PasteAnnotation(Vp)!;

        Assert.NotSame(a, b);
        Assert.Equal(3, Page0.Count);
        Assert.NotEqual(AnnotationGeometry.GetAnnotationBounds(a), AnnotationGeometry.GetAnnotationBounds(b));
    }

    [Fact]
    public void Paste_AtPoint_PutsTopLeftThere()
    {
        AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        _handler.CopySelectedAnnotation(Vp);

        var p = Assert.IsType<RectAnnotation>(_handler.PasteAnnotation(Vp, 200, 300));

        Assert.Equal((200f, 300f), (p.X, p.Y));
    }

    [Fact]
    public void Paste_PastPageEdge_IsClampedIntoPage()
    {
        AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        _handler.CopySelectedAnnotation(Vp);
        float pw = (float)Vp.PageWidth, ph = (float)Vp.PageHeight;

        var far = Assert.IsType<RectAnnotation>(_handler.PasteAnnotation(Vp, pw + 500, ph + 500));
        Assert.Equal(pw, far.X + far.W, 2);
        Assert.Equal(ph, far.Y + far.H, 2);

        var neg = Assert.IsType<RectAnnotation>(_handler.PasteAnnotation(Vp, -300, -300));
        Assert.Equal((0f, 0f), (neg.X, neg.Y));
    }

    [Fact]
    public void Paste_ClipboardIsSnapshot_SurvivesSourceEditAndDelete()
    {
        var src = Assert.IsType<RectAnnotation>(AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 }));
        _handler.CopySelectedAnnotation(Vp);
        src.W = 999;
        _handler.SelectedAnnotation = src;
        _handler.DeleteSelectedAnnotation(Vp);

        var p = Assert.IsType<RectAnnotation>(_handler.PasteAnnotation(Vp, 10, 10));
        Assert.Equal(50f, p.W);
    }

    [Fact]
    public void Paste_WhileRotated_IsRefused()
    {
        AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        _handler.CopySelectedAnnotation(Vp);
        _doc.ViewRotation = 1; // quarter turns

        Assert.True(AnnotationInteractionHandler.IsPasteBlockedByRotation(Vp));
        Assert.Null(_handler.PasteAnnotation(Vp));
        Assert.Single(Page0);
    }

    // --- /NM collision (the reason CloneForPaste exists) ---

    [Fact]
    public void PastedCopyOfInPdfAnnotation_SurvivesWriteBackAndReload()
    {
        var writer = new PdfAnnotationWriter();
        var file = AnnotationTestHelpers.OnePage(new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        var bytes = writer.WriteReconciled(AnnotationTestHelpers.PlainPdfBytes(), file);

        // Reload from the PDF: the annotation now carries its /NM and InPdf provenance.
        var loaded = new PdfAnnotationReader().Read(bytes);
        var original = loaded.Pages[0][0];
        Assert.False(string.IsNullOrEmpty(original.NativeId));

        var copy = Annotation.CloneForPaste(original);
        ((RectAnnotation)copy).X += 10;
        loaded.Pages[0].Add(copy);
        var bytes2 = writer.WriteReconciled(bytes, loaded);

        var back = AnnotationTestHelpers.ReadBack(bytes2);
        Assert.Equal(2, back.Count);
        Assert.Equal(2, back.Select(a => a.NativeId).Distinct().Count());
    }

    [Fact]
    public void MergeInto_ClearsNativeIdsThatCollide()
    {
        var target = new AnnotationFile();
        target.Pages[0] = [new RectAnnotation { NativeId = "nm-1" }];
        var imported = new AnnotationFile();
        imported.Pages[0] = [new RectAnnotation { NativeId = "nm-1", Source = AnnotationSource.InPdf }, new RectAnnotation { NativeId = "nm-2" }];
        imported.Pages[1] = [new RectAnnotation { NativeId = "nm-2" }];

        AnnotationService.MergeInto(target, imported);

        Assert.Null(target.Pages[0][1].NativeId);          // collided with target
        Assert.Equal(AnnotationSource.RailReader, target.Pages[0][1].Source); // writer skips InPdf w/o /NM
        Assert.Equal("nm-2", target.Pages[0][2].NativeId); // first sight keeps its id
        Assert.Null(target.Pages[1][0].NativeId);          // collided within the import
    }

    [Fact]
    public void BrowsePointerDown_MovableAnnotationBeatsOverlappingMarkup()
    {
        var rect = new RectAnnotation { X = 120, Y = 110, W = 20, H = 20 };
        _doc.AddAnnotation(0, rect);
        // Added later (so topmost) and its union bounds cover the rect.
        _doc.AddAnnotation(0, new HighlightAnnotation
        {
            Rects = [new HighlightRect(100, 100, 200, 14), new HighlightRect(100, 130, 200, 14)],
        });

        Assert.True(_handler.HandleBrowsePointerDown(Vp, 130, 120));
        Assert.Same(rect, _handler.SelectedAnnotation);
    }

    [Fact]
    public void Copy_EmptyFreehandOrStaleSelection_ReturnsFalse()
    {
        Assert.False(Annotation.IsCopyable(new FreehandAnnotation()));

        var rect = AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        _doc.RemoveAnnotation(0, rect);
        Assert.False(_handler.CopySelectedAnnotation(Vp));
        Assert.False(_handler.HasAnnotationClipboard);
    }

    [Fact]
    public void Cut_StaleSelection_KeepsPreviousClipboard()
    {
        AddAndSelect(() => new RectAnnotation { X = 100, Y = 100, W = 50, H = 40 });
        _handler.CopySelectedAnnotation(Vp);
        var stale = AddAndSelect(() => new RectAnnotation { X = 10, Y = 10, W = 5, H = 5 });
        _doc.RemoveAnnotation(0, stale);

        Assert.False(_handler.CutSelectedAnnotation(Vp));

        var p = Assert.IsType<RectAnnotation>(_handler.PasteAnnotation(Vp, 0, 0));
        Assert.Equal(50f, p.W); // the earlier copy is still on the clipboard
    }
}
