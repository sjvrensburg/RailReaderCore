using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PR 5 — lazy sidecar→PDF migration hardening: content-aware dedup keeps migration
/// idempotent (no duplicates even if sidecar cleanup is missed), and a one-time
/// heads-up signal fires when a writable PDF's sidecar annotations will be baked in.
/// </summary>
public class PdfAnnotationMigrationTests
{
    /// <summary>Single-PDF in-memory sidecar whose state mutates like the real one.</summary>
    private sealed class FakeSidecar : IAnnotationStore
    {
        public AnnotationFile? Current;
        public AnnotationFile? Load(string pdfPath, string? password = null) => Current;
        public bool Save(string pdfPath, AnnotationFile annotations, string? password = null) { Current = annotations; return true; }
        public bool Delete(string pdfPath, string? password = null) { Current = null; return true; }
    }

    private static string WritablePdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-mig-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, File.ReadAllBytes(TestFixtures.GetTestPdfPath()));
        return path;
    }

    private static AnnotationFile FileWith(params Annotation[] anns)
    {
        var f = new AnnotationFile();
        f.Pages[0] = [.. anns];
        return f;
    }

    // --- task 1: content-aware dedup ---

    [Fact]
    public void ContentEquivalent_IgnoresSourceAndNativeId_WithinTolerance()
    {
        var inPdf = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10, 20, 30, 12)], Contents = "x",
            Source = AnnotationSource.InPdf, NativeId = "n1",
        };
        var sidecar = new HighlightAnnotation
        {
            Rects = [new HighlightRect(10.3f, 20.2f, 30f, 12f)], Contents = "x", // within tolerance
            Source = AnnotationSource.RailReader,
        };
        Assert.True(AnnotationEquivalence.ContentEquivalent(inPdf, sidecar));

        Assert.False(AnnotationEquivalence.ContentEquivalent(inPdf,
            new HighlightAnnotation { Rects = [new HighlightRect(10, 20, 30, 12)], Contents = "different" }));
        Assert.False(AnnotationEquivalence.ContentEquivalent(inPdf,
            new UnderlineAnnotation { Rects = [new HighlightRect(10, 20, 30, 12)], Contents = "x" })); // type differs
    }

    [Fact]
    public void Load_DedupsStaleSidecarCopyByContent_WhenCleanupWasMissed()
    {
        var path = WritablePdf();
        try
        {
            // The annotation already lives in the PDF (with a fresh /NM).
            new PdfAnnotationStore().Save(path,
                FileWith(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Contents = "note" }));

            // Simulate a sidecar that still holds the pre-migration copy (no /NM) — cleanup missed.
            var fake = new FakeSidecar
            {
                Current = FileWith(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)], Contents = "note" }),
            };
            var composite = new CompositeAnnotationStore(fake);

            var loaded = composite.Load(path)!;

            // Deduped by content against the in-PDF copy → one annotation, not two.
            var only = Assert.Single(loaded.Pages[0]);
            Assert.Equal(AnnotationSource.InPdf, only.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- end-to-end lazy migration ---

    [Fact]
    public void MigrateOnSave_BakesSidecarAnnotsIntoPdf_AndCleansSidecar_NoDuplicates()
    {
        var path = WritablePdf();
        try
        {
            var fake = new FakeSidecar
            {
                Current = FileWith(new TextNoteAnnotation { X = 100, Y = 200, Text = "my note" }),
            };
            var composite = new CompositeAnnotationStore(fake);

            // Open: plain PDF has no native annots → model is the sidecar's annotation.
            var file = composite.Load(path)!;
            Assert.Single(file.Pages[0]);
            Assert.Equal(AnnotationSource.RailReader, file.Pages[0][0].Source);

            // First save migrates it into the PDF and cleans the sidecar.
            Assert.True(composite.Save(path, file));
            Assert.Null(fake.Current); // sidecar cleaned (no bookmarks)

            // Reopen: comes from the PDF now, exactly once.
            var reloaded = composite.Load(path)!;
            var note = Assert.IsType<TextNoteAnnotation>(Assert.Single(reloaded.Pages[0]));
            Assert.Equal("my note", note.Contents);
            Assert.Equal(AnnotationSource.InPdf, note.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MigrateOnSave_KeepsBookmarksInSidecar()
    {
        var path = WritablePdf();
        try
        {
            var seed = FileWith(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] });
            seed.Bookmarks.Add(new BookmarkEntry { Name = "intro", Page = 0 });
            var fake = new FakeSidecar { Current = seed };
            var composite = new CompositeAnnotationStore(fake);

            var file = composite.Load(path)!;
            composite.Save(path, file);

            // Annotation went to the PDF; the bookmark stayed in the (now bookmarks-only) sidecar.
            Assert.NotNull(fake.Current);
            Assert.Equal(0, fake.Current!.Pages.Values.Sum(p => p.Count));
            Assert.Single(fake.Current.Bookmarks);
            Assert.Single(new PdfAnnotationReader().Read(File.ReadAllBytes(path)).Pages[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- task 3: heads-up signal ---

    [Fact]
    public void Load_FiresMigrationHeadsUpOnce_ForWritablePdfWithSidecarAnnots()
    {
        var path = WritablePdf();
        try
        {
            var fake = new FakeSidecar { Current = FileWith(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }) };
            var composite = new CompositeAnnotationStore(fake);
            var fired = new List<string>();
            composite.OnSidecarMigration = fired.Add;

            composite.Load(path);
            composite.Load(path); // second open must not re-fire

            Assert.Equal([path], fired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DoesNotFireMigrationHeadsUp_ForReadOnlyPdf()
    {
        var path = WritablePdf();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var fake = new FakeSidecar { Current = FileWith(new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }) };
            var composite = new CompositeAnnotationStore(fake);
            bool fired = false;
            composite.OnSidecarMigration = _ => fired = true;

            composite.Load(path);

            Assert.False(fired); // read-only PDFs keep annots in the sidecar — no migration
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }
}
