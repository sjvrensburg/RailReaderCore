using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader.Core.Tests;

/// <summary>
/// Creates minimal test PDFs using SkiaSharp's PDF backend.
/// </summary>
public static class TestFixtures
{
    private static readonly List<string> s_tempFiles = [];

    static TestFixtures()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var file in s_tempFiles)
                try { File.Delete(file); } catch { }
        };
    }

    /// <summary>
    /// Returns path to a new 3-page test PDF. Each call creates a unique file
    /// to avoid file locking conflicts between concurrent tests.
    /// </summary>
    public static string GetTestPdfPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_test_{Guid.NewGuid():N}.pdf");
        CreateTestPdf(path, pageCount: 3);
        s_tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Creates the standard SkiaPdfServiceFactory for tests.
    /// </summary>
    public static IPdfServiceFactory CreatePdfFactory() => new SkiaPdfServiceFactory();

    /// <summary>
    /// Configures a DocumentModel for rail mode testing: injects synthetic analysis,
    /// sets zoom above threshold, and activates rail navigation.
    /// </summary>
    public static void SetupRailMode(DocumentModel doc, CoreSettings config,
        double vpWidth = 800, double vpHeight = 600)
    {
        var block = new LayoutBlock
        {
            Role = BlockRole.Text, BBox = new BBox(72, 72, 468, 200),
            Confidence = 0.9f, Order = 0,
        };
        for (int i = 0; i < 5; i++)
            block.Lines.Add(new LineInfo(72 + i * 20, 16, 72, 468));
        var analysis = new PageAnalysis();
        analysis.Blocks.Add(block);
        ActivateRailMode(doc, config, analysis, vpWidth, vpHeight);
    }

    /// <summary>
    /// Configures a DocumentModel for rail mode testing with multiple blocks of
    /// different roles. Creates one line per block — suitable for block-level
    /// navigation tests only; do not use for line-advance or snap-behaviour tests,
    /// which need multiple lines per block like the single-block overload provides.
    /// </summary>
    public static void SetupRailMode(DocumentModel doc, CoreSettings config,
        double vpWidth, double vpHeight, params (BlockRole Role, BBox BBox)[] blocks)
    {
        var analysis = new PageAnalysis();
        for (int i = 0; i < blocks.Length; i++)
        {
            var (role, bbox) = blocks[i];
            var block = new LayoutBlock
            {
                Role = role, BBox = bbox, Confidence = 0.9f, Order = i,
            };
            block.Lines.Add(new LineInfo(bbox.Y + 10, 16, bbox.X, bbox.W));
            analysis.Blocks.Add(block);
        }
        ActivateRailMode(doc, config, analysis, vpWidth, vpHeight);
    }

    /// <summary>
    /// Configures rail mode with multiple blocks of differing roles AND a chosen
    /// number of lines per block. Use this for tests that must distinguish line
    /// indices within a block, mix navigable and non-navigable roles, or otherwise
    /// need realistic geometry that the one-line-per-block overload cannot express.
    /// Each block's lines are evenly stacked within its bbox (LineInfo.Y = centre).
    /// </summary>
    public static void SetupRailMode(DocumentModel doc, CoreSettings config,
        double vpWidth, double vpHeight, params (BlockRole Role, BBox BBox, int LineCount)[] blocks)
    {
        var analysis = new PageAnalysis();
        for (int i = 0; i < blocks.Length; i++)
        {
            var (role, bbox, lineCount) = blocks[i];
            var block = new LayoutBlock
            {
                Role = role, BBox = bbox, Confidence = 0.9f, Order = i,
            };
            int n = Math.Max(1, lineCount);
            float lineH = bbox.H / n;
            for (int j = 0; j < n; j++)
                block.Lines.Add(new LineInfo(bbox.Y + lineH * (j + 0.5f), lineH, bbox.X, bbox.W));
            analysis.Blocks.Add(block);
        }
        ActivateRailMode(doc, config, analysis, vpWidth, vpHeight);
    }

    private static void ActivateRailMode(DocumentModel doc, CoreSettings config,
        PageAnalysis analysis, double vpWidth, double vpHeight)
    {
        doc.SetAnalysis(doc.CurrentPage, doc.DefaultAnalysisParams, analysis);
        doc.Rail.SetAnalysis(analysis, config.NavigableRoles);
        doc.Camera.Zoom = config.RailZoomThreshold + 1;
        doc.Rail.UpdateZoom(doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, vpWidth, vpHeight);
    }

    /// <summary>The user password for <see cref="GetEncryptedTestPdfPath"/>.</summary>
    public const string EncryptedPdfPassword = "secret";

    // A 3-page PDF encrypted with AES-256 (qpdf R=6), user password "secret".
    // Embedded as base64 so the test suite stays self-contained and does not depend
    // on qpdf being installed (CI runners don't have it). Regenerate with:
    //   qpdf --encrypt secret owner 256 -- plain.pdf enc.pdf && base64 -w0 enc.pdf
    private const string EncryptedPdfBase64 =
        "JVBERi0xLjcKJb/3ov4KMSAwIG9iago8PCAvRXh0ZW5zaW9ucyA8PCAvQURCRSA8PCAvQmFzZVZlcnNpb24gLzEuNyAvRXh0ZW5zaW9uTGV2ZWwgOCA+PiA+PiAvUGFnZXMgMyAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1Byb2R1Y2VyIDxhYjkxNWI0NjI0MTZlNzFiNDZkY2M5MTJlMWIyNzVhOGVkOWE2MmFlZjQ1ZGVlNDNkZWEyMDliYmJjNjllMzA4PiA+PgplbmRvYmoKMyAwIG9iago8PCAvQ291bnQgMyAvS2lkcyBbIDQgMCBSIDUgMCBSIDYgMCBSIF0gL1R5cGUgL1BhZ2VzID4+CmVuZG9iago0IDAgb2JqCjw8IC9Db250ZW50cyA3IDAgUiAvTWVkaWFCb3ggWyAwIDAgNjEyIDc5MiBdIC9QYXJlbnQgMyAwIFIgL1Jlc291cmNlcyA8PCAvRXh0R1N0YXRlIDw8IC9HMyA4IDAgUiA+PiAvUHJvY1NldCBbIC9QREYgL1RleHQgL0ltYWdlQiAvSW1hZ2VDIC9JbWFnZUkgXSA+PiAvU3RydWN0UGFyZW50cyAwIC9UeXBlIC9QYWdlID4+CmVuZG9iago1IDAgb2JqCjw8IC9Db250ZW50cyA5IDAgUiAvTWVkaWFCb3ggWyAwIDAgNjEyIDc5MiBdIC9QYXJlbnQgMyAwIFIgL1Jlc291cmNlcyA8PCAvRXh0R1N0YXRlIDw8IC9HMyA4IDAgUiA+PiAvUHJvY1NldCBbIC9QREYgL1RleHQgL0ltYWdlQiAvSW1hZ2VDIC9JbWFnZUkgXSA+PiAvU3RydWN0UGFyZW50cyAxIC9UeXBlIC9QYWdlID4+CmVuZG9iago2IDAgb2JqCjw8IC9Db250ZW50cyAxMCAwIFIgL01lZGlhQm94IFsgMCAwIDYxMiA3OTIgXSAvUGFyZW50IDMgMCBSIC9SZXNvdXJjZXMgPDwgL0V4dEdTdGF0ZSA8PCAvRzMgOCAwIFIgPj4gL1Byb2NTZXQgWyAvUERGIC9UZXh0IC9JbWFnZUIgL0ltYWdlQyAvSW1hZ2VJIF0gPj4gL1N0cnVjdFBhcmVudHMgMiAvVHlwZSAvUGFnZSA+PgplbmRvYmoKNyAwIG9iago8PCAvTGVuZ3RoIDgwIC9GaWx0ZXIgL0ZsYXRlRGVjb2RlID4+CnN0cmVhbQrJR0l5hq2rtWVMC5Lrb9V9M/7RPV9EvuQH5Gbdx1SPN4HO2+BjmqU+TOd7N3TH+KHrorzCjE7EYlxYxw68p3ZkgZHhj13oZZHdGLdvn6PJ52VuZHN0cmVhbQplbmRvYmoKOCAwIG9iago8PCAvQk0gL05vcm1hbCAvY2EgMSA+PgplbmRvYmoKOSAwIG9iago8PCAvTGVuZ3RoIDgwIC9GaWx0ZXIgL0ZsYXRlRGVjb2RlID4+CnN0cmVhbQpYXYVoA8R/MC28EUUXTULXt7R+uNL+RhpnqSVfww4zAWYERVhOzh1A1c6pCdH7yGsgTeSOjfL3ufbsCUjNO2UgdKVJWbqJZlcq5YHKsZK8dmVuZHN0cmVhbQplbmRvYmoKMTAgMCBvYmoKPDwgL0xlbmd0aCA4MCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0K8+DQP6pJ5OsIQadpcXGtTPAdDGSdF70ADXNiYdvhNRVXS7K4Y8dea6bsAG/7aUBLbYLx1UdTs+q1sE4CnbJ/9um8exe45gRBr1EMW2c97wFlbmRzdHJlYW0KZW5kb2JqCjExIDAgb2JqCjw8IC9DRiA8PCAvU3RkQ0YgPDwgL0F1dGhFdmVudCAvRG9jT3BlbiAvQ0ZNIC9BRVNWMyAvTGVuZ3RoIDMyID4+ID4+IC9GaWx0ZXIgL1N0YW5kYXJkIC9MZW5ndGggMjU2IC9PIDwwN2M3MzUxYzMyMTI1MDM1ZjlmYWRlMDBmMTI1Yjk0NGYzOWU1ZjY3Yzk2Y2RmMmJlODg5MjE0YjY5ZTU2ZGQyNzkzZWU0NjdiZmU0NDkzYTFiZTJkNTk2MzI0NTg3NjA+IC9PRSA8OWU2Y2NhZjQxODU0YjRkMGMwMWQzZmVmYmQwM2E1YTA0MWQyODY0ZGI5NjNhMTkyNGE1Y2M2MDNjODM1MmZlMD4gL1AgLTQgL1Blcm1zIDwwODVhYTY0MjQ5MDYwY2Y1YjQzNjc2YjIxZDcwNzU0OT4gL1IgNiAvU3RtRiAvU3RkQ0YgL1N0ckYgL1N0ZENGIC9VIDwyNGQ2MmNiZTQ2NzMyZTM2ZmIyY2IyZmY1MTdiMmU3N2E3N2EzYzAzNzgwYWU1OWExZWNkYzMwNjc1OWEzMzhjYTQ3ODgzN2VhYTU0NGY5ZGIzZTlhOTZjNzkzYTdhMzI+IC9VRSA8MTQ0NTRiN2I4ZTBhNjk2N2QwOGYzYjU3YzYyM2I3MWY0MDJmN2UxNzI3ODA5MTkyNWYwYWQ2MWMyYzQ2YjhkND4gL1YgNSA+PgplbmRvYmoKeHJlZgowIDEyCjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMTMwIDAwMDAwIG4gCjAwMDAwMDAyMjggMDAwMDAgbiAKMDAwMDAwMDI5OSAwMDAwMCBuIAowMDAwMDAwNDk3IDAwMDAwIG4gCjAwMDAwMDA2OTUgMDAwMDAgbiAKMDAwMDAwMDg5NCAwMDAwMCBuIAowMDAwMDAxMDQ0IDAwMDAwIG4gCjAwMDAwMDEwODMgMDAwMDAgbiAKMDAwMDAwMTIzMyAwMDAwMCBuIAowMDAwMDAxMzg0IDAwMDAwIG4gCnRyYWlsZXIgPDwgL0luZm8gMiAwIFIgL1Jvb3QgMSAwIFIgL1NpemUgMTIgL0lEIFs8YjQ4ZTUyYWE0ODgyYzM0N2QzYzUwMTRiMTlmNWI4MTQ+PGI0OGU1MmFhNDg4MmMzNDdkM2M1MDE0YjE5ZjViODE0Pl0gL0VuY3J5cHQgMTEgMCBSID4+CnN0YXJ0eHJlZgoxOTMyCiUlRU9GCg==";

    /// <summary>
    /// Returns a path to a freshly-written copy of the AES-256 encrypted test PDF
    /// (user password <see cref="EncryptedPdfPassword"/>). Each call creates a unique
    /// file so writable-PDF (annotation) tests don't collide.
    /// </summary>
    public static string GetEncryptedTestPdfPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_enc_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Convert.FromBase64String(EncryptedPdfBase64));
        s_tempFiles.Add(path);
        return path;
    }

    public static void CreateTestPdf(string path, int pageCount = 3)
    {
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 14);

        for (int i = 0; i < pageCount; i++)
        {
            using var canvas = doc.BeginPage(612, 792); // US Letter
            canvas.DrawText($"Page {i + 1} of {pageCount}", 72, 72, font, paint);
            canvas.DrawText("This is a test paragraph with some text content.", 72, 120, font, paint);
            canvas.DrawText("Second line of text for testing purposes.", 72, 140, font, paint);
            doc.EndPage();
        }

        doc.Close();
    }
}

/// <summary>
/// A controllable <see cref="ILayoutAnalyzer"/> for driving the real analysis
/// worker pipeline in tests without an ONNX model. <c>RunAnalysis</c> returns
/// whatever the supplied factory builds (default: an empty <see cref="PageAnalysis"/>).
/// </summary>
public sealed class FakeLayoutAnalyzer : ILayoutAnalyzer
{
    public static LayoutModelCapabilities DefaultCapabilities { get; } =
        new(800, new List<LayoutClassDescriptor>(), ProvidesReadingOrder: true);

    private readonly Func<PageAnalysis> _factory;

    public FakeLayoutAnalyzer(Func<PageAnalysis>? factory = null)
        => _factory = factory ?? (() => new PageAnalysis());

    public LayoutModelCapabilities Capabilities => DefaultCapabilities;

    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
    {
        var analysis = _factory();
        analysis.PageWidth = pageW;
        analysis.PageHeight = pageH;
        return analysis;
    }

    public void Dispose() { }
}
