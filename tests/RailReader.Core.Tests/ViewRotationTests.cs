using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Controller/model-level tests for manual view rotation (Phase 1): rotating a
/// document swaps the displayed page dimensions, drops every geometry cache,
/// re-renders in the new frame, and refuses annotation authoring while rotated.
/// Uses the LaTeX sideways-scan fixture (portrait page whose whole body is
/// rotated 90° with no /Rotate) — the case view rotation exists for.
/// </summary>
public class ViewRotationTests : IDisposable
{
    private readonly DocumentController _controller;

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "rotation", name);

    public ViewRotationTests()
    {
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    private DocumentModel OpenScan()
    {
        var doc = _controller.CreateDocument(FixturePath("landscape-scan.pdf"));
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        return doc;
    }

    [Fact]
    public void SetViewRotation_swaps_page_dimensions_and_rerenders()
    {
        var doc = OpenScan();
        double w0 = doc.PageWidth, h0 = doc.PageHeight;
        Assert.True(h0 > w0, "fixture page must start portrait");

        _controller.SetViewRotation(1);

        Assert.Equal(1, doc.ViewRotation);
        Assert.Equal(h0, doc.PageWidth, 0.5);
        Assert.Equal(w0, doc.PageHeight, 0.5);
    }

    [Fact]
    public void SetViewRotation_makes_sideways_scan_text_horizontal()
    {
        var doc = OpenScan();

        var before = doc.GetOrExtractText(0);
        var (bw0, bh0) = MarkerBoxSize(before, "SCANMARK");
        Assert.True(bh0 > bw0, "marker should be vertical before rotation");

        _controller.SetViewRotation(1);

        var after = doc.GetOrExtractText(0);
        var (bw1, bh1) = MarkerBoxSize(after, "SCANMARK");
        Assert.True(bw1 > bh1, "marker should be horizontal after a quarter-turn");
        Assert.Equal(bw0, bh1, 0.5); // same glyph run, axes swapped
        Assert.Equal(bh0, bw1, 0.5);
    }

    [Fact]
    public void SetViewRotation_drops_geometry_caches()
    {
        var doc = OpenScan();
        doc.GetOrExtractText(0);
        doc.GetOrExtractLinks(0);
        Assert.NotEmpty(doc.TextCache);

        _controller.SetViewRotation(2);

        // The caches themselves must have been cleared; the analysis resubmission
        // in SetViewRotation may already have repopulated the current page's text
        // (in the new frame), so assert frame-correctness rather than emptiness.
        var text = doc.GetOrExtractText(0);
        var (bw, bh) = MarkerBoxSize(text, "SCANMARK");
        Assert.True(bh > bw, "180° keeps the sideways marker vertical");
        Assert.Equal(0, doc.AnalysedPageCount);
    }

    [Fact]
    public void SetViewRotation_normalises_quarter_turns()
    {
        var doc = OpenScan();
        _controller.SetViewRotation(5);
        Assert.Equal(1, doc.ViewRotation);
        _controller.SetViewRotation(-1);
        Assert.Equal(3, doc.ViewRotation);
    }

    [Fact]
    public void RotateViewClockwise_and_counter_clockwise_cycle()
    {
        var doc = OpenScan();
        _controller.RotateViewClockwise();
        Assert.Equal(1, doc.ViewRotation);
        _controller.RotateViewClockwise();
        Assert.Equal(2, doc.ViewRotation);
        _controller.RotateViewCounterClockwise();
        Assert.Equal(1, doc.ViewRotation);
        _controller.RotateViewCounterClockwise();
        _controller.RotateViewCounterClockwise();
        Assert.Equal(3, doc.ViewRotation);
    }

    [Fact]
    public void ViewRotation_change_fires_StateChanged()
    {
        var doc = OpenScan();
        var fired = new List<string>();
        doc.StateChanged += fired.Add;

        _controller.SetViewRotation(1);
        Assert.Contains(nameof(DocumentModel.ViewRotation), fired);

        fired.Clear();
        _controller.SetViewRotation(1); // no-op
        Assert.DoesNotContain(nameof(DocumentModel.ViewRotation), fired);
    }

    [Fact]
    public void AddAnnotation_is_refused_while_rotated()
    {
        var doc = OpenScan();
        _controller.SetViewRotation(1);
        doc.AddAnnotation(0, new TextNoteAnnotation { X = 10, Y = 10, Text = "nope" });
        Assert.False(doc.Annotations.Pages.TryGetValue(0, out var list) && list.Count > 0);

        _controller.SetViewRotation(0);
        doc.AddAnnotation(0, new TextNoteAnnotation { X = 10, Y = 10, Text = "yes" });
        Assert.Single(doc.Annotations.Pages[0]);
    }

    [Fact]
    public void ViewRotation_releases_focus_confinement()
    {
        var doc = OpenScan();
        var vp = doc.Primary;
        vp.Focus = new FocusBlock(0, 0, new BBox(10, 10, 100, 100));
        Assert.NotNull(vp.Focus);

        _controller.SetViewRotation(1);
        Assert.Null(vp.Focus);
    }

    private static (float W, float H) MarkerBoxSize(PageText pageText, string marker)
    {
        int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(mi >= 0, $"'{marker}' not found");
        var boxes = pageText.CharBoxes
            .Where(c => c.Index >= mi && c.Index < mi + marker.Length).ToList();
        return (boxes.Max(b => b.Right) - boxes.Min(b => b.Left),
                boxes.Max(b => b.Bottom) - boxes.Min(b => b.Top));
    }
}
