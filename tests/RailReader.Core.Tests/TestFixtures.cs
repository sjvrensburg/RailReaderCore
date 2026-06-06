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
    /// Configures a DocumentState for rail mode testing: injects synthetic analysis,
    /// sets zoom above threshold, and activates rail navigation.
    /// </summary>
    public static void SetupRailMode(DocumentState doc, CoreSettings config,
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
    /// Configures a DocumentState for rail mode testing with multiple blocks of
    /// different roles. Creates one line per block — suitable for block-level
    /// navigation tests only; do not use for line-advance or snap-behaviour tests,
    /// which need multiple lines per block like the single-block overload provides.
    /// </summary>
    public static void SetupRailMode(DocumentState doc, CoreSettings config,
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
    public static void SetupRailMode(DocumentState doc, CoreSettings config,
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

    private static void ActivateRailMode(DocumentState doc, CoreSettings config,
        PageAnalysis analysis, double vpWidth, double vpHeight)
    {
        doc.SetAnalysis(doc.CurrentPage, analysis);
        doc.Rail.SetAnalysis(analysis, config.NavigableRoles);
        doc.Camera.Zoom = config.RailZoomThreshold + 1;
        doc.Rail.UpdateZoom(doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, vpWidth, vpHeight);
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
