using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// End-to-end tests for <see cref="RapidOcrService"/> against the real PP-OCR models.
///
/// <para>
/// These exercise the parts no fake can cover: the RGB-pixmap → SKBitmap conversion, the
/// engine's own preprocessing and coordinate mapping, and the polygon → char-box matching
/// that turns recogniser output into offsets in the line's text. They are skipped when the
/// models cannot be located, so a checkout without them still runs green.
/// </para>
/// </summary>
public class RapidOcrServiceTests
{
    /// <summary>
    /// Renders text to a white RGB pixmap in the tightly-packed layout the analysis worker
    /// produces, so the service is fed exactly what it sees in production.
    /// </summary>
    private static (byte[] Rgb, int W, int H) RenderText(string[] lines, float textSize = 48f,
        int width = 900, int height = 400)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, textSize);
        for (int i = 0; i < lines.Length; i++)
            canvas.DrawText(lines[i], 40f, 80f + i * (textSize * 1.6f), font, paint);

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

    [OcrModelFact]
    public void FullMode_ReadsTheTextAndLocatesItsCharacters()
    {
        var (rgb, w, h) = RenderText(["HELLO WORLD", "SECOND LINE"]);
        using var ocr = new RapidOcrService();

        var page = ocr.Recognize(rgb, w, h, OcrMode.Full);

        Assert.NotEmpty(page.Lines);
        Assert.True(page.HasText);

        var all = string.Join(" ", page.Lines.Select(l => l.Text));
        Assert.Contains("HELLO", all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WORLD", all, StringComparison.OrdinalIgnoreCase);

        foreach (var line in page.Lines)
        {
            // Geometry stays inside the submitted pixmap...
            Assert.InRange(line.Box.X, 0f, w);
            Assert.InRange(line.Box.Y, 0f, h);
            Assert.True(line.Box.W > 0 && line.Box.H > 0);

            if (line.Chars is null) continue;
            foreach (var c in line.Chars)
            {
                // ...and every char box addresses a real offset in its own line's text,
                // which is what OcrPageMapper relies on when it rebases them onto the page.
                Assert.InRange(c.Index, 0, line.Text!.Length - 1);
                Assert.InRange(c.Left, 0f, w);
                Assert.True(c.Right > c.Left);
            }
        }
    }

    [OcrModelFact]
    public void FullMode_CharBoxesRunLeftToRightAcrossTheLine()
    {
        var (rgb, w, h) = RenderText(["ABCDEFGH"]);
        using var ocr = new RapidOcrService();

        var line = ocr.Recognize(rgb, w, h, OcrMode.Full).Lines
            .FirstOrDefault(l => l.Chars is { Count: > 1 });
        // Per-character boxes are what Full mode exists to produce; their absence would mean
        // a scanned page silently loses char clustering, table cells and the order tie-break.
        Assert.NotNull(line);

        // Later characters sit further right: the ordering line detection and cell splitting
        // both assume. (Sorted by text offset, not by the order the engine emitted them.)
        var ordered = line.Chars!.OrderBy(c => c.Index).ToList();
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].Left >= ordered[i - 1].Left - 1f,
                $"char {i} at {ordered[i].Left} is left of char {i - 1} at {ordered[i - 1].Left}");
    }

    [OcrModelFact]
    public void LinesMode_FindsLineGeometryWithoutTranscribing()
    {
        var (rgb, w, h) = RenderText(["FIRST LINE", "SECOND LINE", "THIRD LINE"]);
        using var ocr = new RapidOcrService();

        var page = ocr.Recognize(rgb, w, h, OcrMode.Lines);

        Assert.NotEmpty(page.Lines);
        Assert.False(page.HasText);
        Assert.All(page.Lines, l => Assert.Null(l.Text));
        Assert.All(page.Lines, l => Assert.True(l.Box.H > 0));
    }

    [OcrModelFact]
    public void OffMode_DoesNoWork()
    {
        var (rgb, w, h) = RenderText(["ANYTHING"]);
        using var ocr = new RapidOcrService();

        Assert.Empty(ocr.Recognize(rgb, w, h, OcrMode.Off).Lines);
    }

    [OcrModelFact]
    public void UndersizedBuffer_IsRejectedRatherThanReadOutOfBounds()
    {
        using var ocr = new RapidOcrService();

        Assert.Empty(ocr.Recognize(new byte[10], 100, 100, OcrMode.Full).Lines);
        Assert.Empty(ocr.Recognize([], 0, 0, OcrMode.Full).Lines);
    }

    [OcrModelFact]
    public void DisposedService_ThrowsRatherThanUsingAFreedSession()
    {
        var ocr = new RapidOcrService();
        ocr.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => ocr.Recognize(new byte[300], 10, 10, OcrMode.Full));
    }

    [Fact]
    public void Locator_RejectsAModelSetItCannotResolve()
    {
        var bogus = RapidOcrNet.RapidOcrModelSet.PPOCRv5Latin with
        {
            DetModelPath = Path.Combine("definitely-not-here", "nope.onnx"),
        };

        Assert.Null(OcrModelLocator.Locate(bogus));
    }

    [Fact]
    public void Locator_KeepsAbsolutePathsThatExist()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.Equal(file, OcrModelLocator.Resolve(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// A [Fact] that is skipped (with a visible reason) when the PP-OCR models cannot be found,
/// so a checkout without them reports honestly rather than passing vacuously. The models
/// normally arrive with the RapidOcrNet package.
/// </summary>
public sealed class OcrModelFactAttribute : FactAttribute
{
    public OcrModelFactAttribute()
    {
        if (OcrModelLocator.LocateDefault() is null)
            Skip = "PP-OCR models not found; install them beside the test binaries to run.";
    }
}
