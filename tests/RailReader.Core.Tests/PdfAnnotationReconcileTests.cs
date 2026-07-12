using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PR 2 step 2 — PdfAnnotationWriter.WriteReconciled / PdfAnnotationStore:
/// idempotent add, delete-by-/NM, and value-based edit (in-place vs recreate),
/// leaving unchanged annotations untouched.
/// </summary>
public class PdfAnnotationReconcileTests
{
    private const float Tol = 0.6f;

    private static byte[] PlainPdfBytes() => AnnotationTestHelpers.PlainPdfBytes();

    private static AnnotationFile OnePage(params Annotation[] anns)
        => AnnotationTestHelpers.OnePage(anns);

    private static List<Annotation> ReadBack(byte[] bytes, int page = 0)
        => AnnotationTestHelpers.ReadBack(bytes, page);

    [Fact]
    public void Add_AssignsNativeId_AndMarksPersisted()
    {
        var file = OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });
        var ann = file.Pages[0][0];

        var bytes = new PdfAnnotationWriter().WriteReconciled(PlainPdfBytes(), file);

        Assert.False(string.IsNullOrEmpty(ann.NativeId)); // /NM minted and written back
        Assert.Equal(AnnotationSource.InPdf, ann.Source);  // now persisted
        Assert.Single(ReadBack(bytes));
    }

    [Fact]
    public void Resave_WithNoChanges_DoesNotDuplicate()
    {
        var writer = new PdfAnnotationWriter();
        var file = OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] },
            new TextNoteAnnotation { X = 200, Y = 100, Text = "hi" });

        var b1 = writer.WriteReconciled(PlainPdfBytes(), file);
        Assert.Equal(2, ReadBack(b1).Count);

        // Same file instance (now carrying /NM on both) saved again → still 2.
        var b2 = writer.WriteReconciled(b1, file);
        Assert.Equal(2, ReadBack(b2).Count);

        var b3 = writer.WriteReconciled(b2, file);
        Assert.Equal(2, ReadBack(b3).Count);
    }

    [Fact]
    public void Delete_RemovesAnnotationByNativeId()
    {
        var writer = new PdfAnnotationWriter();
        var file = OnePage(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] },
            new HighlightAnnotation { Rects = [new HighlightRect(72, 300, 80, 16)] });

        var b1 = writer.WriteReconciled(PlainPdfBytes(), file);
        Assert.Equal(2, ReadBack(b1).Count);
        var keptId = file.Pages[0][1].NativeId;

        // User deletes the first annotation from the model.
        file.Pages[0].RemoveAt(0);
        var b2 = writer.WriteReconciled(b1, file);

        var remaining = ReadBack(b2);
        Assert.Single(remaining);
        Assert.Equal(keptId, remaining[0].NativeId);
    }

    [Fact]
    public void Edit_TextNoteContents_InPlace()
    {
        var writer = new PdfAnnotationWriter();
        var file = OnePage(new TextNoteAnnotation { X = 100, Y = 100, Text = "old" });

        var b1 = writer.WriteReconciled(PlainPdfBytes(), file);
        var note = (TextNoteAnnotation)file.Pages[0][0];
        var id = note.NativeId;
        note.Text = "updated";

        var b2 = writer.WriteReconciled(b1, file);

        var back = (TextNoteAnnotation)ReadBack(b2).Single();
        Assert.Equal("updated", back.Contents);
        Assert.Equal(id, back.NativeId); // identity preserved
    }

    [Fact]
    public void Edit_HighlightGeometry_RecreatesWithSameNativeId()
    {
        var writer = new PdfAnnotationWriter();
        var file = OnePage(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });

        var b1 = writer.WriteReconciled(PlainPdfBytes(), file);
        var hl = (HighlightAnnotation)file.Pages[0][0];
        var id = hl.NativeId;
        hl.Rects = [new HighlightRect(72, 400, 80, 16)]; // moved down the page

        var b2 = writer.WriteReconciled(b1, file);

        var back = (HighlightAnnotation)ReadBack(b2).Single();
        Assert.Equal(400f, back.Rects.Single().Y, Tol);
        Assert.Equal(id, back.NativeId);
        Assert.Single(ReadBack(b2)); // not duplicated
    }

    [Fact]
    public void Store_RoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-store-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, PlainPdfBytes());
        try
        {
            var store = new PdfAnnotationStore();
            var file = store.Load(path) ?? new AnnotationFile();
            file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 120, 100, 16)], Contents = "note" }];

            Assert.True(store.Save(path, file));

            var reloaded = store.Load(path);
            var hl = Assert.IsType<HighlightAnnotation>(Assert.Single(reloaded!.Pages[0]));
            Assert.Equal("note", hl.Contents);
            Assert.Equal(AnnotationSource.InPdf, hl.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RealAcrobatPdfFact]
    public void RealAcrobatPdf_SaveWithNoChanges_PreservesAllFortyLosslessly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-real-{Guid.NewGuid():N}.pdf");
        File.Copy(AnnotationTestHelpers.RealAcrobatPdfPath, path, overwrite: true);
        try
        {
            var store = new PdfAnnotationStore();
            var file = store.Load(path)!;
            int before = file.Pages.Values.Sum(p => p.Count);

            // Save with no modifications — must touch nothing.
            Assert.True(store.Save(path, file));

            var after = store.Load(path)!;
            Assert.Equal(before, after.Pages.Values.Sum(p => p.Count));
            // A known reviewer comment survives intact.
            Assert.Contains(after.Pages.Values.SelectMany(p => p),
                a => a is HighlightAnnotation && a.Contents == "accuracy" && a.Author == "cclohessy");

            // Idempotent: a second no-op save keeps the count.
            Assert.True(store.Save(path, after));
            Assert.Equal(before, store.Load(path)!.Pages.Values.Sum(p => p.Count));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
