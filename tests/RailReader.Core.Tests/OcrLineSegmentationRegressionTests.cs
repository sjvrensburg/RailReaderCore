using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// End-to-end regression tests for the line-merging bug found investigating a follow-up to
/// railreader2#209: OCR CharBoxes inherited the full word/line height as their own vertical
/// extent, which inflated <see cref="LineDetector.DetectLinesFromChars"/>'s median-char-height
/// split threshold past the real gap between printed lines on tightly-set text — merging
/// several real lines into one both for rail navigation and for
/// <see cref="AnnotationInteractionHandler.BuildHighlightRects"/>'s multi-line drag-selection
/// rects. Fixed by extending <see cref="CharBoxTightener"/> to also tighten Top/Bottom to each
/// glyph's own ink extent, exactly like a real PDFium char box.
///
/// <para>
/// Rendered at generous line spacing (<see cref="RapidOcrServiceTests"/>'s default 1.6× pitch)
/// this bug does not reproduce — reproducing it needs pitch tight enough that inherited
/// full-line-height char boxes from adjacent lines overlap, which single-spaced book text
/// commonly has. <see cref="RenderTightLines"/> renders at ~1.05× pitch for that reason.
/// </para>
/// </summary>
public class OcrLineSegmentationRegressionTests
{
    private static (byte[] Rgb, int W, int H) RenderTightLines(string[] lines, float textSize = 48f,
        int width = 900, int height = 500)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, textSize);
        for (int i = 0; i < lines.Length; i++)
            canvas.DrawText(lines[i], 40f, 80f + i * (textSize * 1.05f), font, paint);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        var rgb = new byte[width * height * 3];
        var pixels = bitmap.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = pixels[i].Red;
            rgb[i * 3 + 1] = pixels[i].Green;
            rgb[i * 3 + 2] = pixels[i].Blue;
        }
        return (rgb, width, height);
    }

    private static readonly string[] Lines =
    [
        "This is the first line of a tightly set paragraph",
        "and this is the second line right below it here",
        "followed by a third line with similar content now",
        "and finally a fourth and last line to close it out",
    ];

    [OcrModelFact]
    public void TightlySpacedLines_DoNotMergeInLineDetector()
    {
        var (rgb, w, h) = RenderTightLines(Lines);
        using var ocr = new RapidOcrService();
        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);
        var (pageText, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);
        Assert.NotNull(pageText);

        var block = new LayoutBlock { BBox = new BBox(0, 0, w, h), Role = BlockRole.Text, Confidence = 1f };
        var detected = LineDetector.DetectLinesFromChars(block.BBox, pageText!.DedupedCharBoxes);

        // Real OCR is not pixel-perfect, but the fix's whole point is that real lines stop
        // merging — any number close to the four rendered lines confirms it; the pre-fix bug
        // collapsed everything into 1-2 giant bands regardless of how many lines were rendered.
        Assert.InRange(detected.Count, 3, 5);
    }

    [OcrModelFact]
    public void TightlySpacedLines_MultiLineSelectionProducesMultipleRects()
    {
        var (rgb, w, h) = RenderTightLines(Lines);
        using var ocr = new RapidOcrService();
        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);
        var (pageText, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);
        Assert.NotNull(pageText);

        var rects = AnnotationInteractionHandler.BuildHighlightRects(pageText!, 0, pageText.Text.Length);

        // Pre-fix this collapsed to a single rect spanning (almost) the whole block regardless
        // of how many lines the selection actually crossed.
        Assert.InRange(rects.Count, 3, 5);
    }
}
