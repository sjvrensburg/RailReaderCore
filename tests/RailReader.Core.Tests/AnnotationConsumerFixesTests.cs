using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the consumer-side code-review fixes: new subtypes render (#5)
/// and are hit-testable (#6), export no longer duplicates native annotations (#9), and
/// the shared store's caches are thread-safe (#14).
/// </summary>
public class AnnotationConsumerFixesTests
{
    // ---- #6: GetAnnotationBounds / HitTest for the new subtypes ----

    public static IEnumerable<object[]> NewSubtypes()
    {
        yield return [new UnderlineAnnotation { Rects = [new HighlightRect(20, 20, 100, 16)] }];
        yield return [new StrikeOutAnnotation { Rects = [new HighlightRect(20, 20, 100, 16)] }];
        yield return [new SquigglyAnnotation { Rects = [new HighlightRect(20, 20, 100, 16)] }];
        yield return [new CaretAnnotation { X = 20, Y = 20, W = 10, H = 12 }];
        yield return [new FreeTextAnnotation { X = 20, Y = 20, W = 120, H = 40, Contents = "hi" }];
    }

    [Theory]
    [MemberData(nameof(NewSubtypes))]
    public void NewSubtypes_HaveBounds_AndAreHitTestable(Annotation ann)
    {
        var bounds = AnnotationGeometry.GetAnnotationBounds(ann);
        Assert.NotNull(bounds);
        // A point inside the bounds hits.
        Assert.True(AnnotationGeometry.HitTest(ann, bounds!.Value.MidX, bounds.Value.MidY));
        // A far-away point does not.
        Assert.False(AnnotationGeometry.HitTest(ann, 1000, 1000));
    }

    // ---- #5: the new subtypes render (non-blank) in the overlay ----

    private static int NonWhitePixels(Annotation ann, int w, int h, (int X0, int Y0, int X1, int Y1) region)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            AnnotationRenderer.DrawAnnotation(canvas, ann, isSelected: false);
            canvas.Flush();
        }
        int n = 0;
        for (int y = region.Y0; y < region.Y1; y++)
            for (int x = region.X0; x < region.X1; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250) n++;
            }
        return n;
    }

    [Fact]
    public void Underline_StrikeOut_Squiggly_Caret_FreeText_Render()
    {
        Assert.True(NonWhitePixels(
            new UnderlineAnnotation { Rects = [new HighlightRect(20, 20, 100, 16)], Color = "#FF0000" },
            200, 80, (20, 20, 120, 40)) > 10);
        Assert.True(NonWhitePixels(
            new StrikeOutAnnotation { Rects = [new HighlightRect(20, 20, 100, 16)], Color = "#FF0000" },
            200, 80, (20, 20, 120, 40)) > 10);
        Assert.True(NonWhitePixels(
            new SquigglyAnnotation { Rects = [new HighlightRect(20, 20, 100, 16)], Color = "#FF0000" },
            200, 80, (20, 20, 120, 40)) > 10);
        Assert.True(NonWhitePixels(
            new CaretAnnotation { X = 20, Y = 20, W = 12, H = 16, Color = "#FF0000" },
            200, 80, (20, 20, 34, 38)) > 10);
        Assert.True(NonWhitePixels(
            new FreeTextAnnotation { X = 20, Y = 20, W = 120, H = 40, Contents = "hello", Color = "#FF0000" },
            200, 80, (20, 20, 140, 60)) > 10);
    }

    [Fact]
    public void SortByZOrder_KeepsEveryAnnotation_IncludingNewSubtypes()
    {
        List<Annotation> input =
        [
            new HighlightAnnotation { Rects = [new HighlightRect(0, 0, 1, 1)] },
            new UnderlineAnnotation { Rects = [new HighlightRect(0, 0, 1, 1)] },
            new StrikeOutAnnotation { Rects = [new HighlightRect(0, 0, 1, 1)] },
            new SquigglyAnnotation { Rects = [new HighlightRect(0, 0, 1, 1)] },
            new CaretAnnotation(),
            new FreeTextAnnotation(),
            new TextNoteAnnotation(),
            new RectAnnotation(),
            new FreehandAnnotation(),
        ];

        var sorted = AnnotationRenderer.SortByZOrder(input);

        Assert.Equal(input.Count, sorted.Count);
        Assert.Equal(input.Count, sorted.Distinct().Count()); // none dropped or duplicated
    }

    // ---- #9: export does not duplicate native annotations ----

    [Fact]
    public void Export_DoesNotDuplicateNativeAnnotations()
    {
        const string src = "/home/stefan/Downloads/Day-ahead-photovoltaic-power-forecasting---Short.pdf";
        if (!File.Exists(src)) return; // soft-skip

        var pdf = TestFixtures.CreatePdfFactory().CreatePdfService(src);
        var annots = new PdfAnnotationReader().Read(pdf.PdfBytes);
        int before = annots.Pages.Values.Sum(p => p.Count);

        var outPath = Path.Combine(Path.GetTempPath(), $"rr-export-{Guid.NewGuid():N}.pdf");
        try
        {
            AnnotationExportService.Export(pdf, annots, outPath);
            int after = new PdfAnnotationReader().Read(File.ReadAllBytes(outPath))
                .Pages.Values.Sum(p => p.Count);
            // Native annots come from FPDF_ImportPages exactly once; we no longer re-write them.
            Assert.Equal(before, after);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    // ---- #14: the shared store's caches are thread-safe ----

    private sealed class AnnotatedSidecar : IAnnotationStore
    {
        public AnnotationFile? Load(string pdfPath, string? password = null)
        {
            var f = new AnnotationFile();
            f.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(1, 1, 1, 1)] }];
            return f;
        }
        public bool Save(string pdfPath, AnnotationFile annotations, string? password = null) => true;
        public bool Delete(string pdfPath, string? password = null) => true;
    }

    [Fact]
    public void Load_IsThreadSafe_UnderConcurrentAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-conc-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, File.ReadAllBytes(TestFixtures.GetTestPdfPath()));
        try
        {
            // Exercises the migration heads-up (AddOnce on _migrationWarned) + IsSigned
            // (_signedCache) + WarnOnce concurrently against the unsynchronized caches.
            var store = new CompositeAnnotationStore(new AnnotatedSidecar());
            var ex = Record.Exception(() => Parallel.For(0, 1000, _ => store.Load(path)));
            Assert.Null(ex);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
