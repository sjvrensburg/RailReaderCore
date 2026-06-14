using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the code-review fixes: NM-less native duplication (#1),
/// sidecar-not-wiped-on-failed-write (#2), style edits persisted (#3),
/// Filled/StrokeWidth round-trip (#4), empty-geometry not falsely persisted (#10),
/// review-state round-trip (#11).
/// </summary>
public class PdfAnnotationFixesTests
{
    private static byte[] PlainPdf() => File.ReadAllBytes(TestFixtures.GetTestPdfPath());

    private static AnnotationFile OnePage(params Annotation[] anns)
    {
        var f = new AnnotationFile();
        f.Pages[0] = [.. anns];
        return f;
    }

    private static List<Annotation> ReadBack(byte[] bytes)
    {
        var f = new PdfAnnotationReader().Read(bytes);
        return f.Pages.TryGetValue(0, out var l) ? l : [];
    }

    private static string TempPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-fix-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, PlainPdf());
        return path;
    }

    // #1 — an in-PDF annotation without a /NM must not be re-created (duplicated) on save.
    [Fact]
    public void Reconcile_DoesNotDuplicate_InPdfAnnotationLackingNativeId()
    {
        var file = OnePage(new HighlightAnnotation
        {
            Rects = [new HighlightRect(72, 100, 80, 16)],
            Source = AnnotationSource.InPdf,
            NativeId = null, // as a reader would produce for markup lacking /NM
        });

        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdf(), file);

        Assert.Empty(ReadBack(bytes));                    // not written as a new copy
        Assert.Null(file.Pages[0][0].NativeId);           // model untouched (no spurious /NM)
        Assert.Equal(AnnotationSource.InPdf, file.Pages[0][0].Source);
    }

    // #10 — empty-geometry annots write nothing and must NOT be flagged persisted.
    [Theory]
    [InlineData("highlight")]
    [InlineData("ink")]
    [InlineData("caret")]
    public void Reconcile_DoesNotMarkPersisted_WhenNothingWasWritten(string kind)
    {
        Annotation ann = kind switch
        {
            "highlight" => new HighlightAnnotation { Rects = [] },               // 0 rects
            "ink" => new FreehandAnnotation { Points = [new PointF(10, 10)] },   // < 2 points
            _ => new CaretAnnotation { X = 50, Y = 50, W = 6, H = 8 },           // PDFium can't create
        };
        var file = OnePage(ann);

        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdf(), file);

        Assert.Empty(ReadBack(bytes));
        Assert.Null(file.Pages[0][0].NativeId);                                  // not falsely persisted
        Assert.Equal(AnnotationSource.RailReader, file.Pages[0][0].Source);
    }

    // #2 — a failed PDF write must NOT wipe the sidecar (the only copy of un-migrated annots).
    [Fact]
    public void Save_FailedPdfWrite_DoesNotDeleteSidecar()
    {
        // A writable file that is NOT a valid PDF → CanWriteFile=true, unsigned, but
        // WriteReconciled throws → _pdfStore.Save returns false.
        var path = Path.Combine(Path.GetTempPath(), $"rr-bad-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "%PDF-1.7 not really a pdf");
        try
        {
            var sidecar = new SingleFileSidecar
            {
                Current = OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }),
            };
            var store = new CompositeAnnotationStore(sidecar);

            bool ok = store.Save(path, sidecar.Current);

            Assert.False(ok);                 // PDF write failed
            Assert.NotNull(sidecar.Current);  // sidecar preserved — annotations not lost
        }
        finally
        {
            File.Delete(path);
        }
    }

    // #4 — Filled + StrokeWidth (square) and StrokeWidth (ink) survive a round trip.
    [Fact]
    public void RectFillAndStrokeWidth_RoundTrip()
    {
        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdf(),
            OnePage(new RectAnnotation { X = 72, Y = 100, W = 120, H = 40, Filled = true, StrokeWidth = 4, Color = "#FF0000" }));

        var rect = Assert.IsType<RectAnnotation>(Assert.Single(ReadBack(bytes)));
        Assert.True(rect.Filled);
        Assert.Equal(4f, rect.StrokeWidth, 0.5f);
    }

    [Fact]
    public void InkStrokeWidth_RoundTrips()
    {
        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdf(),
            OnePage(new FreehandAnnotation { Points = [new PointF(72, 100), new PointF(200, 120)], StrokeWidth = 5 }));

        var ink = Assert.IsType<FreehandAnnotation>(Assert.Single(ReadBack(bytes)));
        Assert.Equal(5f, ink.StrokeWidth, 0.5f);
    }

    // #3 — a colour-only edit to an existing highlight is persisted (not skipped as "unchanged").
    [Fact]
    public void Edit_HighlightColorOnly_IsPersisted()
    {
        var path = TempPdf();
        try
        {
            var store = new PdfAnnotationStore();
            store.Save(path, OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Color = "#FF0000" }));

            var reloaded = store.Load(path)!;
            var hl = (HighlightAnnotation)reloaded.Pages[0][0];
            Assert.Equal("#FF0000", hl.Color);
            hl.Color = "#00FF00"; // recolor, no geometry change

            store.Save(path, reloaded);

            Assert.Equal("#00FF00", ((HighlightAnnotation)store.Load(path)!.Pages[0][0]).Color);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // #3 — toggling a rect's fill off is persisted (needs delete+recreate, since /IC can't be cleared in place).
    [Fact]
    public void Edit_RectFillToggle_IsPersisted()
    {
        var path = TempPdf();
        try
        {
            var store = new PdfAnnotationStore();
            store.Save(path, OnePage(new RectAnnotation { X = 72, Y = 100, W = 120, H = 40, Filled = true, Color = "#FF0000" }));

            var reloaded = store.Load(path)!;
            var rect = (RectAnnotation)reloaded.Pages[0][0];
            Assert.True(rect.Filled);
            rect.Filled = false;

            store.Save(path, reloaded);

            Assert.False(((RectAnnotation)store.Load(path)!.Pages[0][0]).Filled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // #11 — review state (/State + /StateModel) survives a round trip.
    [Fact]
    public void ReviewState_RoundTrips()
    {
        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdf(),
            OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], State = ReviewState.Accepted }));

        Assert.Equal(ReviewState.Accepted, Assert.Single(ReadBack(bytes)).State);
    }

    // #8 — a sidecar annotation with its own distinct /NM is kept even if it looks
    // content-identical to a native one (only /NM-less migration leftovers are content-deduped).
    [Fact]
    public void Merge_KeepsDistinctSidecarAnnotation_WithItsOwnNativeId()
    {
        var path = TempPdf();
        try
        {
            var store = new PdfAnnotationStore();
            store.Save(path, OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Contents = "note" }));

            var sidecar = new SingleFileSidecar
            {
                Current = OnePage(new HighlightAnnotation
                {
                    Rects = [new HighlightRect(72, 100, 80, 16)], Contents = "note", // content-identical
                    NativeId = "a-distinct-id", // …but its own identity
                }),
            };

            var merged = new CompositeAnnotationStore(sidecar).Load(path)!;
            Assert.Equal(2, merged.Pages[0].Count); // both kept, not over-suppressed
        }
        finally
        {
            File.Delete(path);
        }
    }

    // #12 — annotation flags (/F) survive a round trip instead of being reset to Print-only.
    [Fact]
    public void AnnotationFlags_RoundTrip()
    {
        const int flags = 4 | 32; // Print | NoView
        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdf(),
            OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Flags = flags }));

        Assert.Equal(flags, Assert.Single(ReadBack(bytes)).Flags);
    }

    /// <summary>Single-PDF in-memory sidecar whose state mutates like the real one.</summary>
    private sealed class SingleFileSidecar : IAnnotationStore
    {
        public AnnotationFile? Current;
        public AnnotationFile? Load(string pdfPath, string? password = null) => Current;
        public bool Save(string pdfPath, AnnotationFile annotations, string? password = null) { Current = annotations; return true; }
        public bool Delete(string pdfPath, string? password = null) { Current = null; return true; }
    }
}
