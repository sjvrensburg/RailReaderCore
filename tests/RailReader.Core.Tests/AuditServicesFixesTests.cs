using System.Diagnostics;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the code-audit fixes in Core services/models/geometry:
/// RemoveAnnotationAction robustness, stale selection cleanup, box-drag guards,
/// search regex timeout, non-overlapping matches, snippet clamping, hex-colour
/// fallback, and pixel-projection lower-bound clamping.
/// </summary>
public class AuditServicesFixesTests : IDisposable
{
    private readonly DocumentModel _doc;
    private readonly AnnotationFileManager _manager;
    private readonly AnnotationInteractionHandler _handler;
    private readonly SearchService _search;

    public AuditServicesFixesTests()
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
        _search = new SearchService(() => _doc.Primary, _ => { });
    }

    public void Dispose()
    {
        _doc.Dispose();
        _manager.Dispose();
    }

    private static RectAnnotation MakeRect(float x = 10, float y = 10, float w = 50, float h = 30) =>
        new() { X = x, Y = y, W = w, H = h, Color = "#0066FF", Opacity = 0.5f, StrokeWidth = 2f };

    // ---------------------------------------------------------------
    // RemoveAnnotationAction robustness (finding: Undo after no-op Redo)
    // ---------------------------------------------------------------

    [Fact]
    public void RemoveAnnotationAction_AnnotationAbsent_UndoIsNoOp()
    {
        var present = MakeRect();
        _doc.AddAnnotation(0, present);
        var absent = MakeRect(100, 100);

        // Remove an annotation that is NOT in the page list: Redo removes nothing.
        _doc.RemoveAnnotation(0, absent);
        Assert.Single(_doc.Annotations!.Pages[0]);

        // Undo must not throw (was List.Insert(-1)) and must not add anything.
        _doc.Undo();
        Assert.Single(_doc.Annotations.Pages[0]);
        Assert.Same(present, _doc.Annotations.Pages[0][0]);
    }

    [Fact]
    public void RemoveAnnotationAction_PageKeyMissing_UndoDoesNotDuplicate()
    {
        var annotation = MakeRect();

        // Remove targeting a page that has no annotation list at all.
        _doc.RemoveAnnotation(3, annotation);
        _doc.Undo();

        // Must not silently insert the annotation onto page 3.
        Assert.False(_doc.Annotations!.Pages.TryGetValue(3, out var list) && list.Count > 0);
    }

    [Fact]
    public void RemoveAnnotationAction_NormalRemove_UndoRedoRoundTrips()
    {
        var a = MakeRect(10, 10);
        var b = MakeRect(80, 80);
        _doc.AddAnnotation(0, a);
        _doc.AddAnnotation(0, b);

        _doc.RemoveAnnotation(0, a);
        Assert.Single(_doc.Annotations!.Pages[0]);

        _doc.Undo();
        Assert.Equal(2, _doc.Annotations.Pages[0].Count);
        Assert.Same(a, _doc.Annotations.Pages[0][0]); // restored at its original index

        _doc.Redo();
        Assert.Single(_doc.Annotations.Pages[0]);
        Assert.Same(b, _doc.Annotations.Pages[0][0]);
    }

    // ---------------------------------------------------------------
    // Stale selection / drag references (eraser + undo paths)
    // ---------------------------------------------------------------

    [Fact]
    public void EraseAtPoint_ClearsSelectedAnnotation()
    {
        var rect = MakeRect(10, 10, 50, 30);
        _doc.AddAnnotation(0, rect);
        _handler.SetAnnotationTool(AnnotationTool.Eraser);
        _handler.SelectedAnnotation = rect; // e.g. host-driven selection

        _handler.HandleAnnotationPointerDown(_doc.Primary, 20, 20); // erases the rect

        Assert.False(_doc.Annotations!.Pages.TryGetValue(0, out var list) && list.Contains(rect));
        Assert.Null(_handler.SelectedAnnotation);
    }

    [Fact]
    public void UndoAnnotation_ClearsSelectionOfRemovedAnnotation()
    {
        var rect = MakeRect();
        _doc.AddAnnotation(0, rect);
        _handler.SelectedAnnotation = rect;

        _handler.UndoAnnotation(_doc.Primary); // AddAnnotationAction.Undo removes it

        Assert.Null(_handler.SelectedAnnotation);
    }

    [Fact]
    public void DeleteSelectedAnnotation_StaleSelection_NoOpAndSafeUndo()
    {
        var rect = MakeRect();
        _doc.AddAnnotation(0, rect);
        _handler.SelectedAnnotation = rect;
        _handler.UndoAnnotation(_doc.Primary); // rect removed, selection cleared

        // Even if the host re-injects a stale selection, Delete must not corrupt undo.
        _handler.SelectedAnnotation = rect;
        Assert.False(_handler.DeleteSelectedAnnotation(_doc.Primary));

        // Redo restores the annotation; a further Undo must not throw.
        _handler.RedoAnnotation(_doc.Primary);
        Assert.Single(_doc.Annotations!.Pages[0]);
        _handler.UndoAnnotation(_doc.Primary);
        Assert.Empty(_doc.Annotations.Pages[0]);
    }

    // ---------------------------------------------------------------
    // Box-drag guards (Rectangle/FreeText move/up without pointer-down)
    // ---------------------------------------------------------------

    [Fact]
    public void RectangleMove_WithoutPointerDown_NoPreview()
    {
        _handler.SetAnnotationTool(AnnotationTool.Rectangle);

        bool changed = _handler.HandleAnnotationPointerMove(_doc.Primary, 120, 140);

        Assert.False(changed);
        Assert.Null(_handler.PreviewAnnotation);
    }

    [Fact]
    public void FreeTextUp_WithoutPointerDown_NoPendingFreeText()
    {
        _handler.SetAnnotationTool(AnnotationTool.FreeText);

        bool changed = _handler.HandleAnnotationPointerUp(_doc.Primary, 120, 140);

        Assert.False(changed);
        Assert.Null(_handler.PendingFreeText);
    }

    [Fact]
    public void RectangleDrag_WithPointerDown_StillCommits()
    {
        _handler.SetAnnotationTool(AnnotationTool.Rectangle);
        _handler.HandleAnnotationPointerDown(_doc.Primary, 10, 10);
        Assert.True(_handler.HandleAnnotationPointerMove(_doc.Primary, 60, 50));
        Assert.True(_handler.HandleAnnotationPointerUp(_doc.Primary, 60, 50));

        Assert.Single(_doc.Annotations!.Pages[0]);

        // A stray move after the gesture ended must not rebuild a preview.
        Assert.False(_handler.HandleAnnotationPointerMove(_doc.Primary, 200, 200));
        Assert.Null(_handler.PreviewAnnotation);
    }

    // ---------------------------------------------------------------
    // Search: regex timeout, snippet clamping
    // ---------------------------------------------------------------

    [Fact]
    public void PrepareSearchParams_Regex_HasFiniteMatchTimeout()
    {
        var (regex, _, error) = SearchService.PrepareSearchParams(@"(a+)+$", caseSensitive: true, useRegex: true);
        Assert.Null(error);
        Assert.NotNull(regex);
        Assert.NotEqual(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout, regex!.MatchTimeout);
    }

    [Fact]
    public void ExecuteSearch_CatastrophicRegex_ReturnsInsteadOfHanging()
    {
        // Exponential backtracking pattern over a long non-matching run of 'a':
        // without the match timeout this would hang the (UI) thread effectively forever.
        _doc.SetText(0, new PageText(new string('a', 64), []));

        var sw = Stopwatch.StartNew();
        _search.ExecuteSearch("(a+)+X", caseSensitive: true, useRegex: true);
        sw.Stop();

        Assert.Empty(_search.SearchMatches);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"search did not return promptly ({sw.Elapsed})");
    }

    [Fact]
    public void GetMatchSnippet_StaleMatchBeyondText_DoesNotThrow()
    {
        _doc.SetText(0, new PageText("short text", []));
        // Stale match from a previous (longer) document: CharStart beyond the new text.
        var match = new SearchMatch(0, 500, 10, [new RectF(10, 10, 50, 20)]);

        var (pre, matchStr, post) = _search.GetMatchSnippet(match, contextChars: 40);

        Assert.Equal("", matchStr);
        Assert.Equal("", post);
        Assert.EndsWith("text", pre.TrimEnd());
    }

    // ---------------------------------------------------------------
    // ColorUtils: malformed hex falls back to yellow
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("#GGHHII")]   // right length, non-hex digits
    [InlineData("#GGHHIIJJ")] // 9-char variant
    [InlineData("#12345Z")]
    public void ParseHexColor_NonHexDigits_FallsBackToYellow(string hex)
    {
        var color = ColorUtils.ParseHexColor(hex, 100);

        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
        Assert.Equal(100, color.A);
    }

    // ---------------------------------------------------------------
    // LineDetector: pixel projection with a negative block origin
    // ---------------------------------------------------------------

    [Fact]
    public void DetectLinesFromPixels_NegativeOrigin_DoesNotThrow()
    {
        const int imgW = 20, imgH = 20;
        var rgb = new byte[imgW * imgH * 3]; // all-black page (every pixel "dark")

        var block = new LayoutBlock
        {
            BBox = new BBox(-3, -2, 10, 10),
            Role = BlockRole.Text,
        };

        var lines = LineDetector.DetectLinesFromPixels(block, rgb, imgW, imgH, 1f, 1f);
        Assert.NotNull(lines); // degrades gracefully instead of IndexOutOfRangeException
    }
}
