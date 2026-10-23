using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Author + CreatedUtc stamping on newly authored annotations (#150).</summary>
public class AnnotationAuthorStampTests : IDisposable
{
    private readonly DocumentModel _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler = new();

    public AnnotationAuthorStampTests()
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

    private List<Annotation> Page0 => _doc.Annotations.Pages[0];

    private void Draw(AnnotationTool tool)
    {
        _handler.SetAnnotationTool(tool);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 100, 100);
        _handler.HandleAnnotationPointerMove(_doc.Primary, 160, 150);
        _handler.HandleAnnotationPointerUp(_doc.Primary, 160, 150);
    }

    [Theory]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Rectangle)]
    public void DrawingTools_StampAuthorAndCreationTime(AnnotationTool tool)
    {
        _handler.Author = "Ada";
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Draw(tool);

        var ann = Assert.Single(Page0);
        Assert.Equal("Ada", ann.Author);
        Assert.True(ann.CreatedUtc >= before);
    }

    [Fact]
    public void TextNote_FreeText_AndPaste_AreStamped()
    {
        _handler.Author = "Ada";
        _handler.CompleteTextNote(_doc.Primary, 50, 50, "note");
        var free = _handler.AddFreeText(_doc.Primary, 10, 10, 100, 30, "typed");
        Assert.All(Page0, a => Assert.Equal("Ada", a.Author));
        Assert.Equal("Ada", free!.Author);

        // A paste is a new annotation by the current author, not a copy of the source's.
        var source = Assert.IsType<TextNoteAnnotation>(Page0[0]);
        source.Author = "Grace";
        _handler.SelectedAnnotation = source;
        Assert.True(_handler.CopySelectedAnnotation(_doc.Primary));
        var pasted = _handler.PasteAnnotation(_doc.Primary)!;
        Assert.Equal("Ada", pasted.Author);
        Assert.NotNull(pasted.CreatedUtc);
        Assert.Equal("Grace", source.Author);
    }

    [Fact]
    public void Reply_IsStampedWithTheHandlersAuthor()
    {
        var root = new RectAnnotation { X = 10, Y = 10, W = 40, H = 40 };
        _doc.AddAnnotation(0, root);
        _handler.Author = "Ada";

        var reply = _handler.CreateReply(_doc.Primary, root, "agreed")!;

        Assert.Equal("Ada", reply.Author);
        Assert.NotNull(reply.CreatedUtc);
    }

    [Fact]
    public void NoAuthor_KeepsHistoricalBehaviour()
    {
        foreach (var blank in new string?[] { null, "", "   " })
        {
            _handler.Author = blank;
            _handler.CompleteTextNote(_doc.Primary, 50, 50, "note");
        }

        Assert.All(Page0, a =>
        {
            Assert.Null(a.Author);
            Assert.Null(a.CreatedUtc);
        });
    }

    [Fact]
    public void ExistingAnnotations_AreNeverReauthored()
    {
        var existing = new TextNoteAnnotation { Author = "Grace", Source = AnnotationSource.InPdf, Text = "old" };
        _doc.AddAnnotation(0, existing);
        _handler.Author = "Ada";

        _handler.CompleteTextNote(_doc.Primary, 50, 50, "new");
        _handler.CompleteCommentEdit(_doc.Primary, existing, "edited");

        Assert.Equal("Grace", existing.Author);
        Assert.Equal("Ada", Page0[1].Author);
    }

    [Fact]
    public void Merge_KeepsSourceAuthor_AndFillsOnlyAGap()
    {
        var a = new HighlightAnnotation { Rects = [new HighlightRect(10, 100, 30, 12)], Color = "#FFFF00", Opacity = 0.4f, Author = "Grace" };
        var b = new HighlightAnnotation { Rects = [new HighlightRect(30, 100, 30, 12)], Color = "#FFFF00", Opacity = 0.4f };
        _doc.AddAnnotation(0, a);
        _doc.AddAnnotation(0, b);
        _handler.Author = "Ada";
        _handler.SelectedAnnotation = b;

        var merged = _handler.MergeSelectedTextMarkup(_doc.Primary, out _)!;
        Assert.Equal("Grace", merged.Author);

        var c = new HighlightAnnotation { Rects = [new HighlightRect(100, 200, 30, 12)], Color = "#00A000", Opacity = 0.4f };
        var d = new HighlightAnnotation { Rects = [new HighlightRect(120, 200, 30, 12)], Color = "#00A000", Opacity = 0.4f };
        _doc.AddAnnotation(0, c);
        _doc.AddAnnotation(0, d);
        _handler.SelectedAnnotation = d;
        Assert.Equal("Ada", _handler.MergeSelectedTextMarkup(_doc.Primary, out _)!.Author);
    }

    [Fact]
    public void MovingAnAnnotation_UpdatesModifiedUtc_NotAuthor()
    {
        var note = new TextNoteAnnotation { X = 50, Y = 50, Text = "n", Author = "Grace" };
        _doc.AddAnnotation(0, note);
        _handler.Author = "Ada";

        _handler.HandleBrowsePointerDown(_doc.Primary, 55, 55);
        _handler.HandleBrowsePointerMove(95, 95);
        _handler.HandleBrowsePointerUp(_doc.Primary, 95, 95);

        Assert.True(note.X > 50);
        Assert.NotNull(note.ModifiedUtc);
        Assert.Equal("Grace", note.Author);
    }

    [Fact]
    public void Settings_FlowThroughConfigAndController()
    {
        var cfg = new AppConfig { AnnotationAuthor = "Ada" };
        Assert.Equal("Ada", cfg.ToCoreSettings().AnnotationAuthor);
        Assert.Null(new AppConfig().ToCoreSettings().AnnotationAuthor);

        using var controller = new DocumentController(cfg.ToCoreSettings(), cfg, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        Assert.Equal("Ada", controller.Annotations.Author);

        controller.OnConfigChanged(new AppConfig { AnnotationAuthor = "Grace" }.ToCoreSettings());
        Assert.Equal("Grace", controller.Annotations.Author);
        controller.OnConfigChanged(new AppConfig().ToCoreSettings());
        Assert.Null(controller.Annotations.Author);
    }
}
