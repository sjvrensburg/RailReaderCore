using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PR 1 step 4 — CompositeAnnotationStore merges native PDF annotations with the
/// sidecar on load and persists only RailReader-authored annotations on save.
/// </summary>
public class CompositeAnnotationStoreTests
{
    /// <summary>In-memory sidecar double so tests don't touch the config dir.</summary>
    private sealed class FakeSidecar : IAnnotationStore
    {
        public readonly Dictionary<string, AnnotationFile> Saved = new(StringComparer.OrdinalIgnoreCase);
        public AnnotationFile? Seed;

        public AnnotationFile? Load(string pdfPath, string? password = null) => Seed;
        public bool Save(string pdfPath, AnnotationFile annotations, string? password = null)
        {
            Saved[Path.GetFullPath(pdfPath)] = annotations;
            return true;
        }
        public bool Delete(string pdfPath, string? password = null)
        {
            return Saved.Remove(Path.GetFullPath(pdfPath));
        }
    }

    /// <summary>Bakes a single native highlight into a real PDF on disk; returns its path.</summary>
    private static string CreatePdfWithNativeHighlight()
    {
        var srcPath = TestFixtures.GetTestPdfPath();
        var outPath = Path.Combine(Path.GetTempPath(), $"rr-composite-{Guid.NewGuid():N}.pdf");
        var pdf = TestFixtures.CreatePdfFactory().CreatePdfService(srcPath);
        var file = new AnnotationFile();
        file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 120, 16)] }];
        AnnotationExportService.Export(pdf, file, outPath);
        return outPath;
    }

    [Fact]
    public void Load_MergesNativeWithSidecar()
    {
        var pdfPath = CreatePdfWithNativeHighlight();
        try
        {
            var sidecar = new FakeSidecar
            {
                Seed = BuildSidecar(
                    authored: new TextNoteAnnotation { X = 10, Y = 10, Text = "mine" },
                    bookmark: new BookmarkEntry { Name = "bm", Page = 1 }),
            };
            var store = new CompositeAnnotationStore(sidecar);

            var loaded = store.Load(pdfPath);

            Assert.NotNull(loaded);
            var page0 = loaded!.Pages[0];
            Assert.Equal(2, page0.Count);
            Assert.Contains(page0, a => a is HighlightAnnotation && a.Source == AnnotationSource.InPdf);
            Assert.Contains(page0, a => a is TextNoteAnnotation && a.Source == AnnotationSource.RailReader);
            // Bookmarks are carried from the sidecar.
            Assert.Single(loaded.Bookmarks);
            Assert.Equal("bm", loaded.Bookmarks[0].Name);
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void Load_NoNativeAnnots_ReturnsSidecarUnchanged()
    {
        // A plain test PDF with no annotations.
        var pdfPath = TestFixtures.GetTestPdfPath();
        var seed = BuildSidecar(new TextNoteAnnotation { X = 1, Y = 2, Text = "only mine" }, bookmark: null);
        var sidecar = new FakeSidecar { Seed = seed };
        var store = new CompositeAnnotationStore(sidecar);

        var loaded = store.Load(pdfPath);

        Assert.Same(seed, loaded); // identical instance — pure passthrough
    }

    [Fact]
    public void Save_PersistsOnlyAuthoredAnnotations()
    {
        var sidecar = new FakeSidecar();
        var store = new CompositeAnnotationStore(sidecar);
        var pdfPath = Path.Combine(Path.GetTempPath(), $"rr-{Guid.NewGuid():N}.pdf");

        var file = new AnnotationFile();
        file.Pages[0] =
        [
            new HighlightAnnotation { Source = AnnotationSource.InPdf, NativeId = "native-1" },
            new TextNoteAnnotation { Source = AnnotationSource.RailReader, Text = "mine" },
        ];
        file.Bookmarks.Add(new BookmarkEntry { Name = "b", Page = 0 });

        Assert.True(store.Save(pdfPath, file));

        var saved = sidecar.Saved[Path.GetFullPath(pdfPath)];
        var savedAnn = Assert.Single(saved.Pages[0]);
        Assert.IsType<TextNoteAnnotation>(savedAnn);
        Assert.Equal(AnnotationSource.RailReader, savedAnn.Source);
        Assert.Single(saved.Bookmarks);
    }

    [Fact]
    public void Save_OnlyNativeAnnots_CleansSidecarAndSucceeds()
    {
        var sidecar = new FakeSidecar();
        var store = new CompositeAnnotationStore(sidecar);
        var pdfPath = Path.Combine(Path.GetTempPath(), $"rr-{Guid.NewGuid():N}.pdf");
        // pre-existing stale sidecar entry
        sidecar.Saved[Path.GetFullPath(pdfPath)] = new AnnotationFile();

        var file = new AnnotationFile();
        file.Pages[0] = [new HighlightAnnotation { Source = AnnotationSource.InPdf, NativeId = "native-1" }];

        Assert.True(store.Save(pdfPath, file)); // success, not a save-failure
        Assert.False(sidecar.Saved.ContainsKey(Path.GetFullPath(pdfPath))); // stale entry removed
    }

    [Fact]
    public void Load_DedupesSidecarCopyByNativeId()
    {
        // Uses the genuine Acrobat PDF (its annotations carry /NM); skips otherwise.
        const string path = "/home/stefan/Downloads/Day-ahead-photovoltaic-power-forecasting---Short.pdf";
        if (!File.Exists(path)) return;

        // Discover a real native id and its page from a plain native read.
        var nativeOnly = new PdfAnnotationReader().Read(File.ReadAllBytes(path));
        var (page, sample) = nativeOnly.Pages
            .SelectMany(kv => kv.Value.Select(a => (kv.Key, a)))
            .First(t => t.a.NativeId is not null);
        int nativeCountOnPage = nativeOnly.Pages[page].Count;

        // Seed the sidecar with a stale copy carrying the same /NM.
        var seed = new AnnotationFile();
        seed.Pages[page] = [new HighlightAnnotation { NativeId = sample.NativeId, Contents = "stale copy" }];
        var store = new CompositeAnnotationStore(new FakeSidecar { Seed = seed });

        var merged = store.Load(path);

        // The stale sidecar copy is dropped; native count is unchanged.
        Assert.Equal(nativeCountOnPage, merged!.Pages[page].Count);
        Assert.DoesNotContain(merged.Pages[page], a => a.Contents == "stale copy");
    }

    private static AnnotationFile BuildSidecar(Annotation authored, BookmarkEntry? bookmark)
    {
        var f = new AnnotationFile();
        f.Pages[0] = [authored];
        if (bookmark is not null) f.Bookmarks.Add(bookmark);
        return f;
    }
}
