using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// PR 2 step 3 — CompositeAnnotationStore Save routing: writable+unsigned PDFs are
/// written in place; read-only/signed PDFs fall back to the sidecar with a one-time signal.
/// </summary>
public class CompositeAnnotationStoreRoutingTests
{
    private sealed class FakeSidecar : IAnnotationStore
    {
        public readonly Dictionary<string, AnnotationFile> Saved = new(StringComparer.OrdinalIgnoreCase);
        public AnnotationFile? Load(string pdfPath, string? password = null) => null;
        public bool Save(string pdfPath, AnnotationFile annotations, string? password = null)
        {
            Saved[Path.GetFullPath(pdfPath)] = annotations;
            return true;
        }
        public bool Delete(string pdfPath, string? password = null) => Saved.Remove(Path.GetFullPath(pdfPath));
    }

    private static string WritablePdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-route-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, File.ReadAllBytes(TestFixtures.GetTestPdfPath()));
        return path;
    }

    private static List<Annotation> AnnotsInPdf(string path)
    {
        var f = new PdfAnnotationReader().Read(File.ReadAllBytes(path));
        return f.Pages.Values.SelectMany(p => p).ToList();
    }

    [Fact]
    public void WritableUnsigned_WritesAnnotationsIntoThePdf()
    {
        var path = WritablePdf();
        try
        {
            var fake = new FakeSidecar();
            var store = new CompositeAnnotationStore(fake);
            bool fellBack = false;
            store.OnSidecarFallback = (_, _) => fellBack = true;

            var file = new AnnotationFile();
            file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }];

            Assert.True(store.Save(path, file));

            Assert.False(fellBack);
            Assert.Equal(AnnotationSource.InPdf, file.Pages[0][0].Source); // written into the PDF
            Assert.Single(AnnotsInPdf(path));                              // and present in the file
            Assert.False(fake.Saved.ContainsKey(Path.GetFullPath(path)));  // sidecar not used for annots
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Writable_BookmarksGoToSidecar_AnnotationsToPdf()
    {
        var path = WritablePdf();
        try
        {
            var fake = new FakeSidecar();
            var store = new CompositeAnnotationStore(fake);

            var file = new AnnotationFile();
            file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }];
            file.Bookmarks.Add(new BookmarkEntry { Name = "intro", Page = 1 });

            Assert.True(store.Save(path, file));

            Assert.Single(AnnotsInPdf(path));
            var sidecar = fake.Saved[Path.GetFullPath(path)];
            Assert.Equal(0, sidecar.Pages.Values.Sum(p => p.Count)); // no annots in the sidecar
            Assert.Single(sidecar.Bookmarks);                        // only the bookmark
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadOnly_FallsBackToSidecar_AndSignalsOnce()
    {
        var path = WritablePdf();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var fake = new FakeSidecar();
            var store = new CompositeAnnotationStore(fake);
            var reasons = new List<SidecarFallbackReason>();
            store.OnSidecarFallback = (_, r) => reasons.Add(r);

            var file = new AnnotationFile();
            file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }];
            file.Bookmarks.Add(new BookmarkEntry { Name = "bm", Page = 0 });

            Assert.True(store.Save(path, file));

            // Signalled once, with ReadOnly.
            Assert.Equal([SidecarFallbackReason.ReadOnly], reasons);
            // The PDF itself is untouched.
            Assert.Empty(AnnotsInPdf(path));
            // Authored annotation + bookmark went to the sidecar.
            var sidecar = fake.Saved[Path.GetFullPath(path)];
            Assert.Single(sidecar.Pages[0]);
            Assert.Single(sidecar.Bookmarks);

            // A second save does not re-fire the signal.
            store.Save(path, file);
            Assert.Single(reasons);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void GetSidecarFallbackReason_ReportsTheSaveVerdict_WithoutSavingOrSignalling()
    {
        var path = WritablePdf();
        try
        {
            var fake = new FakeSidecar();
            var store = new CompositeAnnotationStore(fake);
            var reasons = new List<SidecarFallbackReason>();
            store.OnSidecarFallback = (_, r) => reasons.Add(r);

            Assert.Null(store.GetSidecarFallbackReason(path)); // writable + unsigned → PDF is canonical

            File.SetAttributes(path, FileAttributes.ReadOnly);
            Assert.Equal(SidecarFallbackReason.ReadOnly, store.GetSidecarFallbackReason(path));

            // The query is side-effect free: nothing saved, nothing signalled...
            Assert.Empty(reasons);
            Assert.Empty(fake.Saved);

            // ...and it does not consume the once-per-path signal a real save fires.
            var file = new AnnotationFile();
            file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 80, 16)] }];
            Assert.True(store.Save(path, file));
            Assert.Equal([SidecarFallbackReason.ReadOnly], reasons);

            File.SetAttributes(path, FileAttributes.Normal);
            Assert.Null(store.GetSidecarFallbackReason(path)); // re-probed each call, not cached
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void SignatureProbe_RunsOnce_PerPathAndPassword()
    {
        var path = WritablePdf();
        try
        {
            var store = new CompositeAnnotationStore(new FakeSidecar());
            Assert.Null(store.GetSidecarFallbackReason(path));
            Assert.Null(store.GetSidecarFallbackReason(path));
            Assert.Equal(1, store.SignatureProbeCount);          // second verdict was a cache hit

            Assert.Null(store.GetSidecarFallbackReason(path, "other"));
            Assert.Equal(2, store.SignatureProbeCount);          // a different password is its own entry
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SignatureProbe_PathLoad_OpensTheDocumentNatively()
    {
        // The lazy path load is what keeps the probe cheap; if the binding or the path encoding
        // were wrong the probe would still pass via the whole-file fallback, so check it directly.
        var path = WritablePdf();
        try
        {
            lock (PdfiumGate.Lock)
            {
                PdfiumResolver.EnsureLibraryInitialized();
                var doc = PdfiumNative.FPDF_LoadDocument(path, null);
                Assert.NotEqual(IntPtr.Zero, doc);
                Assert.Equal(0, PdfiumNative.FPDF_GetSignatureCount(doc));
                PdfiumNative.FPDF_CloseDocument(doc);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SignatureProbe_FailedProbe_IsCachedToo()
    {
        // Not a PDF at all: PDFium can't parse it, the probe fails, and that used to be re-run
        // (whole-file read included) on every later verdict.
        var path = Path.Combine(Path.GetTempPath(), $"rr-garbage-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "this is not a pdf");
        try
        {
            var store = new CompositeAnnotationStore(new FakeSidecar());
            Assert.Null(store.GetSidecarFallbackReason(path)); // writable, unparseable → treated as unsigned
            Assert.Null(store.GetSidecarFallbackReason(path));
            Assert.Equal(1, store.SignatureProbeCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetSidecarFallbackReason_MissingFile_IsReadOnly()
    {
        var store = new CompositeAnnotationStore(new FakeSidecar());
        var missing = Path.Combine(Path.GetTempPath(), $"rr-missing-{Guid.NewGuid():N}.pdf");
        Assert.Equal(SidecarFallbackReason.ReadOnly, store.GetSidecarFallbackReason(missing));
    }

    [Fact]
    public void Writable_LoadAfterSave_SurfacesTheWrittenAnnotation()
    {
        var path = WritablePdf();
        try
        {
            var store = new CompositeAnnotationStore(new FakeSidecar());
            var file = new AnnotationFile();
            file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 120, 100, 16)], Contents = "c" }];
            store.Save(path, file);

            var loaded = store.Load(path)!;
            var hl = Assert.IsType<HighlightAnnotation>(Assert.Single(loaded.Pages[0]));
            Assert.Equal("c", hl.Contents);
            Assert.Equal(AnnotationSource.InPdf, hl.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
