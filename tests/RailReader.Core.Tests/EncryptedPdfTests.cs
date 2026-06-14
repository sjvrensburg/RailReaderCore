using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Covers opening, rendering, and annotating password-protected (AES-256) PDFs —
/// the exam-moderation scenario where exams are distributed as encrypted PDFs.
/// </summary>
public class EncryptedPdfTests
{
    private const string Pwd = TestFixtures.EncryptedPdfPassword;

    [Fact]
    public void OpenWithoutPassword_ThrowsPasswordRequired()
    {
        var factory = TestFixtures.CreatePdfFactory();
        var path = TestFixtures.GetEncryptedTestPdfPath();

        var ex = Assert.Throws<PdfPasswordRequiredException>(() => factory.CreatePdfService(path));
        Assert.False(ex.WrongPassword); // no password supplied for an encrypted doc
    }

    [Fact]
    public void OpenWithWrongPassword_ThrowsWrongPassword()
    {
        var factory = TestFixtures.CreatePdfFactory();
        var path = TestFixtures.GetEncryptedTestPdfPath();

        var ex = Assert.Throws<PdfPasswordRequiredException>(
            () => factory.CreatePdfService(path, "not-the-password"));
        Assert.True(ex.WrongPassword);
    }

    [Fact]
    public void OpenWithCorrectPassword_Succeeds()
    {
        var factory = TestFixtures.CreatePdfFactory();
        var path = TestFixtures.GetEncryptedTestPdfPath();

        var pdf = factory.CreatePdfService(path, Pwd);

        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(Pwd, pdf.Password);

        using var page = pdf.RenderPage(0);
        Assert.True(page.Width > 0 && page.Height > 0);
    }

    [Fact]
    public void DocumentState_WithPassword_RendersAndExtractsWithoutThrowing()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var path = TestFixtures.GetEncryptedTestPdfPath();

        var pdf = factory.CreatePdfService(path, Pwd);
        var state = new DocumentState(path, pdf,
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(),
            config.ToCoreSettings(), marshaller);
        state.LoadPageBitmap();

        // The stateless text/link services must reopen the encrypted document with the
        // threaded password — these would silently return empty (or throw) if not.
        var text = state.GetOrExtractText(0);
        Assert.NotNull(text);
        var links = state.GetOrExtractLinks(0);
        Assert.NotNull(links);
    }

    [Fact]
    public void NativeAnnotationRoundTrip_PreservesAnnotationAndEncryption()
    {
        // Exam-moderation flow: open an encrypted exam, add a markup, save back into
        // the PDF, and reopen — the annotation must survive AND the document must stay
        // encrypted (the moderated copy must not silently lose its password).
        var path = TestFixtures.GetEncryptedTestPdfPath();
        var store = new PdfAnnotationStore();

        var file = new AnnotationFile();
        file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 72, 200, 20)] }];

        Assert.True(store.Save(path, file, Pwd));

        var reloaded = store.Load(path, Pwd);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Pages.TryGetValue(0, out var page0));
        Assert.Contains(page0!, a => a is HighlightAnnotation);

        // Still encrypted: a reader given no password sees nothing (the load fails closed).
        var withoutPassword = new PdfAnnotationReader().Read(System.IO.File.ReadAllBytes(path));
        Assert.True(withoutPassword.Pages.Count == 0);
    }

    [Fact]
    public void FlattenedExport_OfEncryptedPdf_RefusesRatherThanDecrypt()
    {
        // The flatten-to-new-document export cannot carry the source's encryption, so it
        // must refuse rather than silently write a plaintext copy of a confidential PDF.
        var factory = TestFixtures.CreatePdfFactory();
        var path = TestFixtures.GetEncryptedTestPdfPath();
        var pdf = factory.CreatePdfService(path, Pwd);

        var file = new AnnotationFile();
        file.Pages[0] = [new HighlightAnnotation { Rects = [new HighlightRect(72, 100, 120, 16)] }];
        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rr-enc-export-{System.Guid.NewGuid():N}.pdf");

        Assert.Throws<InvalidOperationException>(() => AnnotationExportService.Export(pdf, file, outPath));
        Assert.False(System.IO.File.Exists(outPath)); // nothing leaked to disk
    }
}
