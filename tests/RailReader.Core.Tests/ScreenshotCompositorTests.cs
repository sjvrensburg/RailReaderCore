using RailReader.Core;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Regression tests for the screenshot compositing path: view rotation must be
/// threaded into the page render (and rotation-0 annotation geometry mapped into
/// the rotated frame), annotations must draw in z-order, and PNG saves must
/// truncate an existing file.
/// </summary>
public class ScreenshotCompositorTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentController _controller;
    private readonly ColourEffectShaders _shaders = new();

    public ScreenshotCompositorTests()
    {
        _pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose()
    {
        _controller.Dispose();
        _shaders.Dispose();
    }

    private DocumentModel Open()
    {
        var doc = _controller.CreateDocument(_pdfPath);
        doc.LoadPageBitmap();
        _controller.AddDocument(doc);
        return doc;
    }

    private static ScreenshotOptions BareOptions => new()
    {
        Dpi = 72,
        RailOverlay = false,
        SearchHighlights = false,
        Annotations = false,
        DebugOverlay = false,
    };

    [Fact]
    public void RenderPage_WithViewRotation_RendersInRotatedFrame()
    {
        var doc = Open();
        _controller.SetViewRotation(1);
        Assert.True(doc.PageWidth > doc.PageHeight, "rotated portrait page must report landscape dimensions");

        using var bmp = ScreenshotCompositor.RenderPage(doc, _controller, _shaders, BareOptions);

        // The page bitmap must be rendered in the SAME rotated frame the
        // viewport state lives in — landscape, matching PageWidth/PageHeight.
        Assert.True(bmp.Width > bmp.Height, "screenshot of a rotated view must be landscape");
        Assert.Equal(doc.PageWidth / doc.PageHeight, (double)bmp.Width / bmp.Height, 2);
    }

    [Fact]
    public void RenderPage_WithViewRotation_MapsRotationZeroAnnotationsIntoRotatedFrame()
    {
        var doc = Open();
        // Annotation geometry is stored in the rotation-0 frame; author it
        // before rotating (AddAnnotation refuses while rotated).
        doc.AddAnnotation(0, new RectAnnotation
        {
            X = 40, Y = 40, W = 20, H = 20, Filled = true, Color = "#FF0000", Opacity = 1f,
        });
        _controller.SetViewRotation(1);

        using var bmp = ScreenshotCompositor.RenderPage(doc, _controller, _shaders,
            BareOptions with { Annotations = true });

        // At 72 DPI, 1 px == 1 pt. Rotation-0 rect (40..60, 40..60) maps under
        // one clockwise quarter-turn to (H0−60..H0−40, 40..60) with H0 = 792.
        var centre = bmp.GetPixel(742, 50);
        Assert.True(centre.Red > 200 && centre.Green < 100 && centre.Blue < 100,
            $"rotated annotation expected at (742, 50); found {centre}");
    }

    [Fact]
    public void RenderPage_DrawsAnnotationsInZOrder_NotInsertionOrder()
    {
        var doc = Open();
        // Insertion order: note FIRST, highlight SECOND — storage order would
        // draw the highlight OVER the note popup; z-order draws markup first.
        doc.AddAnnotation(0, new TextNoteAnnotation
        {
            X = 100, Y = 100, Text = "hello world hello", IsExpanded = true,
        });
        doc.AddAnnotation(0, new HighlightAnnotation
        {
            Color = "#0000FF",
            Opacity = 0.5f,
            Rects = [new HighlightRect(90, 90, 120, 80)],
        });

        using var bmp = ScreenshotCompositor.RenderPage(doc, _controller, _shaders,
            BareOptions with { Annotations = true });

        // Sample inside the popup's top padding (popup origin ≈ (112, 110)).
        // Popup background on top → warm/near-white red channel; a blue 50%
        // highlight drawn over the popup would halve it (≈ 127).
        var inPopup = bmp.GetPixel(116, 113);
        Assert.True(inPopup.Red > 200,
            $"note popup must draw over the highlight (z-order), got {inPopup}");
    }

    [Fact]
    public void SavePng_OverwritingLargerFile_TruncatesStaleBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"railreader_shot_{Guid.NewGuid():N}.png");
        try
        {
            // Large noisy bitmap → big PNG.
            var rng = new Random(42);
            using (var big = new SKBitmap(256, 256))
            {
                for (int y = 0; y < big.Height; y++)
                    for (int x = 0; x < big.Width; x++)
                        big.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
                ScreenshotCompositor.SavePng(big, path);
            }
            long bigLength = new FileInfo(path).Length;

            // Overwrite the SAME path with a much smaller image.
            using (var small = new SKBitmap(4, 4))
            {
                small.Erase(SKColors.White);
                ScreenshotCompositor.SavePng(small, path);
            }
            long overwrittenLength = new FileInfo(path).Length;

            Assert.True(overwrittenLength < bigLength,
                $"overwritten PNG must be truncated ({overwrittenLength} vs stale {bigLength})");

            // And it must round-trip as a valid PNG with no trailing garbage:
            // the decoded size matches and the file ends at IEND.
            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(4, decoded.Width);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
