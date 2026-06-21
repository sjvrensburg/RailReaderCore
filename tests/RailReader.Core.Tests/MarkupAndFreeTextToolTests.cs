using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Authoring tests for the drag-over-text markup tools (Underline/StrikeOut/Squiggly) and
/// the FreeText typewriter tool added to <see cref="AnnotationInteractionHandler"/>. The
/// markup tools reuse the Highlight selection→rects pipeline, differing only in the committed
/// <see cref="TextMarkupAnnotation"/> subtype.
/// </summary>
public class MarkupAndFreeTextToolTests : IDisposable
{
    private readonly DocumentState _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler;

    public MarkupAndFreeTextToolTests()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        _doc = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
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

    // --- subtype-parameterized commit helper -------------------------------------------

    [Theory]
    [InlineData(AnnotationTool.Highlight, typeof(HighlightAnnotation))]
    [InlineData(AnnotationTool.Underline, typeof(UnderlineAnnotation))]
    [InlineData(AnnotationTool.StrikeOut, typeof(StrikeOutAnnotation))]
    [InlineData(AnnotationTool.Squiggly, typeof(SquigglyAnnotation))]
    public void CreateTextMarkup_ReturnsCorrectSubtype(AnnotationTool tool, Type expected)
    {
        var rects = new List<HighlightRect> { new(72, 100, 120, 14) };
        var markup = AnnotationInteractionHandler.CreateTextMarkup(tool, rects, "#123456", 0.5f);

        Assert.IsType(expected, markup);
        Assert.Same(rects, markup.Rects);
        Assert.Equal("#123456", markup.Color);
        Assert.Equal(0.5f, markup.Opacity);
    }

    [Theory]
    [InlineData(AnnotationTool.Highlight, true)]
    [InlineData(AnnotationTool.Underline, true)]
    [InlineData(AnnotationTool.StrikeOut, true)]
    [InlineData(AnnotationTool.Squiggly, true)]
    [InlineData(AnnotationTool.Pen, false)]
    [InlineData(AnnotationTool.FreeText, false)]
    [InlineData(AnnotationTool.TextSelect, false)]
    public void IsTextMarkupTool_ClassifiesTools(AnnotationTool tool, bool expected)
        => Assert.Equal(expected, AnnotationInteractionHandler.IsTextMarkupTool(tool));

    // --- tool colour defaults ----------------------------------------------------------

    [Theory]
    [InlineData(AnnotationTool.Underline, "#00A000")]
    [InlineData(AnnotationTool.Squiggly, "#00A000")]
    [InlineData(AnnotationTool.StrikeOut, "#FF0000")]
    [InlineData(AnnotationTool.FreeText, "#000000")]
    public void SetTool_AppliesExpectedDefaultColor(AnnotationTool tool, string expectedColor)
    {
        _handler.SetAnnotationTool(tool);
        Assert.Equal(expectedColor, _handler.ActiveAnnotationColor);
        Assert.Equal(1.0f, _handler.ActiveAnnotationOpacity);
    }

    // --- full drag-over-text path ------------------------------------------------------

    [Theory]
    [InlineData(AnnotationTool.Highlight, typeof(HighlightAnnotation))]
    [InlineData(AnnotationTool.Underline, typeof(UnderlineAnnotation))]
    [InlineData(AnnotationTool.StrikeOut, typeof(StrikeOutAnnotation))]
    [InlineData(AnnotationTool.Squiggly, typeof(SquigglyAnnotation))]
    public void DragOverText_CommitsCorrectSubtype(AnnotationTool tool, Type expected)
    {
        // Precondition: the synthetic page has an extractable text layer to drag over.
        Assert.NotEmpty(_doc.GetOrExtractText(_doc.CurrentPage).CharBoxes);

        _handler.SetAnnotationTool(tool);

        // Drag across the first text line ("This is a test paragraph…", baseline y≈120).
        _handler.HandleAnnotationPointerDown(_doc.Primary, 74, 116);
        bool moved = _handler.HandleAnnotationPointerMove(_doc.Primary, 300, 116);

        Assert.True(moved);
        var preview = Assert.IsAssignableFrom<TextMarkupAnnotation>(_handler.PreviewAnnotation);
        Assert.IsType(expected, preview);
        Assert.NotEmpty(preview.Rects);

        _handler.HandleAnnotationPointerUp(_doc.Primary, 300, 116);

        Assert.Null(_handler.PreviewAnnotation);
        var committed = Assert.Single(_doc.Annotations.Pages[_doc.CurrentPage]);
        Assert.IsType(expected, committed);
        var markup = Assert.IsAssignableFrom<TextMarkupAnnotation>(committed);
        Assert.NotEmpty(markup.Rects);
    }

    // --- FreeText gesture --------------------------------------------------------------

    [Fact]
    public void FreeText_DragBuildsPreview_AndStashesPendingOnUp()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 100, 200);
        _handler.HandleAnnotationPointerMove(_doc.Primary, 260, 248);

        var preview = Assert.IsType<FreeTextAnnotation>(_handler.PreviewAnnotation);
        Assert.Equal(100f, preview.X, 0.5f);
        Assert.Equal(200f, preview.Y, 0.5f);
        Assert.Equal(160f, preview.W, 0.5f);
        Assert.Equal(48f, preview.H, 0.5f);

        _handler.HandleAnnotationPointerUp(_doc.Primary, 260, 248);

        // Nothing committed yet — the box waits for the UI to supply text.
        Assert.Null(_handler.PreviewAnnotation);
        Assert.NotNull(_handler.PendingFreeText);
        Assert.False(_doc.Annotations.Pages.TryGetValue(_doc.CurrentPage, out var list) && list.Count > 0);
    }

    [Fact]
    public void FreeText_CommitPending_AddsAnnotationWithText()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 100, 200);
        _handler.HandleAnnotationPointerMove(_doc.Primary, 260, 248);
        _handler.HandleAnnotationPointerUp(_doc.Primary, 260, 248);

        var created = _handler.CommitPendingFreeText(_doc.Primary, "Typed comment");

        Assert.NotNull(created);
        Assert.Null(_handler.PendingFreeText);
        var ft = Assert.IsType<FreeTextAnnotation>(Assert.Single(_doc.Annotations.Pages[_doc.CurrentPage]));
        Assert.Equal("Typed comment", ft.Contents);
        Assert.Equal("Typed comment", ft.EffectiveContents);
        Assert.Equal("#000000", ft.Color);
    }

    [Fact]
    public void FreeText_CommitEmptyText_DiscardsPending()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 100, 200);
        _handler.HandleAnnotationPointerUp(_doc.Primary, 110, 205); // tiny drag → default box

        var created = _handler.CommitPendingFreeText(_doc.Primary, "   ");

        Assert.Null(created);
        Assert.Null(_handler.PendingFreeText);
        Assert.False(_doc.Annotations.Pages.TryGetValue(_doc.CurrentPage, out var list) && list.Count > 0);
    }

    [Fact]
    public void FreeText_Click_FallsBackToDefaultBox()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 150, 300);
        _handler.HandleAnnotationPointerUp(_doc.Primary, 152, 301); // sub-threshold → default size

        var pending = Assert.IsType<FreeTextAnnotation>(_handler.PendingFreeText);
        Assert.Equal(150f, pending.X, 0.5f);
        Assert.Equal(300f, pending.Y, 0.5f);
        Assert.Equal(200f, pending.W, 0.5f);
        Assert.Equal(48f, pending.H, 0.5f);
    }

    [Fact]
    public void FreeText_Cancel_ClearsPendingWithoutAdding()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 100, 200);
        _handler.HandleAnnotationPointerUp(_doc.Primary, 260, 248);
        Assert.NotNull(_handler.PendingFreeText);

        _handler.CancelPendingFreeText();

        Assert.Null(_handler.PendingFreeText);
        Assert.False(_doc.Annotations.Pages.TryGetValue(_doc.CurrentPage, out var list) && list.Count > 0);
    }

    [Fact]
    public void AddFreeText_CreatesAnnotationDirectly()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        var ft = _handler.AddFreeText(_doc.Primary, 80, 90, 220, 40, "Direct text", fontSize: 16f);

        Assert.NotNull(ft);
        Assert.Equal(16f, ft!.FontSize);
        var stored = Assert.IsType<FreeTextAnnotation>(Assert.Single(_doc.Annotations.Pages[_doc.CurrentPage]));
        Assert.Equal("Direct text", stored.Contents);
        Assert.Equal(220f, stored.W, 0.5f);
    }

    [Fact]
    public void SwitchingTool_ClearsPendingFreeText()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 100, 200);
        _handler.HandleAnnotationPointerUp(_doc.Primary, 260, 248);
        Assert.NotNull(_handler.PendingFreeText);

        _handler.SetAnnotationTool(AnnotationTool.Highlight);

        Assert.Null(_handler.PendingFreeText);
    }
}
