using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Issue #155: invisible OCR text layer written into a PDF copy.</summary>
public class PdfTextLayerWriterTests
{
    private static OcrPage SamplePage(string text = "Hello")
    {
        var chars = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
            chars.Add(new CharBox(i, 72f + i * 16f, 100f, 72f + i * 16f + 14f, 120f));
        return new OcrPage([
            new OcrLine(new BBox(72f, 100f, text.Length * 16f, 20f), text, chars),
        ]);
    }

    private static (byte[] Bytes, TextLayerWriteResult Result) WriteSample(
        byte[] source, TextLayerOptions? options = null)
        => PdfTextLayerWriter.WriteToBytes(source, new Dictionary<int, OcrPage> { [0] = SamplePage() },
            options: options);

    [Fact]
    public void WrittenLayer_IsExtractableAndFlaggedAsOcr()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);

        var (bytes, result) = WriteSample(source);

        Assert.Equal(1, result.PagesWritten);
        Assert.Equal(0, result.PagesSkipped);
        Assert.False(result.SourceWasSigned);

        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains("Hello", extracted.Text);
        Assert.True(extracted.FromOcr);
        Assert.NotEmpty(extracted.CharBoxes);
    }

    [Fact]
    public void WrittenLayer_CharBoxesCoverThePrintedGlyphs()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);

        var (bytes, _) = WriteSample(source);
        var extracted = new PdfTextService().ExtractPageText(bytes, 0);

        var boxes = extracted.CharBoxes.Where(c => c.Index < extracted.Text.Length && extracted.Text[c.Index] != '\n')
            .OrderBy(c => c.Left).ToList();
        Assert.Equal(5, boxes.Count);
        // First glyph sits over the OCR box (72..86, 100..120), within a few points.
        Assert.InRange(boxes[0].Left, 66f, 78f);
        Assert.InRange(boxes[0].Top, 92f, 108f);
        Assert.InRange(boxes[0].Right - boxes[0].Left, 8f, 22f);
        Assert.InRange(boxes[^1].Right, 140f, 160f);
    }

    [Fact]
    public void NativeTextPages_AreSkippedByDefault()
    {
        var path = TestFixtures.GetTestPdfPath(); // born-digital text
        var source = File.ReadAllBytes(path);
        var before = new PdfTextService().ExtractPageText(source, 0);

        var (bytes, result) = WriteSample(source);

        Assert.Equal(0, result.PagesWritten);
        Assert.Equal(1, result.PagesSkipped);
        var after = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Equal(before.Text, after.Text);
        Assert.False(after.FromOcr);
    }

    [Fact]
    public void Rewriting_ReplacesTheLayerInsteadOfStacking()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);

        var (once, _) = WriteSample(source);
        var (twice, result) = WriteSample(once);

        Assert.Equal(1, result.PagesWritten);
        var extracted = new PdfTextService().ExtractPageText(twice, 0);
        Assert.Equal(1, extracted.Text.Split("Hello").Length - 1);
        Assert.True(extracted.FromOcr);
    }

    [Fact]
    public void OutputFile_IsWrittenAndSourceUntouched()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var outPath = Path.Combine(Path.GetTempPath(), $"railreader_ocr_{Guid.NewGuid():N}.pdf");
        try
        {
            var factory = TestFixtures.CreatePdfFactory();
            var svc = factory.CreatePdfService(path);
            var result = PdfTextLayerWriter.Write(svc,
                new Dictionary<int, OcrPage> { [0] = SamplePage() }, outPath);

            Assert.Equal(1, result.PagesWritten);
            Assert.True(File.Exists(outPath));
            Assert.Equal(source, File.ReadAllBytes(path)); // never in place
            var extracted = new PdfTextService().ExtractPageText(File.ReadAllBytes(outPath), 0);
            Assert.Contains("Hello", extracted.Text);
            (svc as IDisposable)?.Dispose();
        }
        finally { try { File.Delete(outPath); } catch { } }
    }

    [Fact]
    public void CjkText_RoundTripsThroughTheGlyphlessFont()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        const string text = "中文テスト";
        var chars = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
            chars.Add(new CharBox(i, 72f + i * 20f, 100f, 72f + i * 20f + 18f, 120f));
        var page = new OcrPage([new OcrLine(new BBox(72f, 100f, 100f, 20f), text, chars)]);

        var (bytes, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = page });

        Assert.Equal(1, result.PagesWritten);
        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains(text, extracted.Text);
        Assert.True(extracted.FromOcr);
    }

    [Fact]
    public void WordFallback_WithoutCharBoxes_StillLandsText()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var page = new OcrPage([new OcrLine(new BBox(72f, 100f, 160f, 20f), "Hello world")]);

        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = page });

        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains("Hello", extracted.Text);
        Assert.Contains("world", extracted.Text);
    }

    [Fact]
    public void WrittenLayer_DoesNotPaint()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var (bytes, _) = WriteSample(source);
        var outPath = Path.Combine(Path.GetTempPath(), $"railreader_ocr_{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(outPath, bytes);
            var factory = TestFixtures.CreatePdfFactory();
            var before = factory.CreatePdfService(path);
            var after = factory.CreatePdfService(outPath);
            var (rgbBefore, w1, h1) = before.RenderPagePixmap(0, 800);
            var (rgbAfter, w2, h2) = after.RenderPagePixmap(0, 800);
            Assert.Equal((w1, h1), (w2, h2));
            Assert.Equal(rgbBefore, rgbAfter); // render mode 3: nothing new is painted
        }
        finally { try { File.Delete(outPath); } catch { } }
    }

    [Fact]
    public void SkewedLine_ExtractsWithTiltedBoxes()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        const string text = "Hello";
        float angle = 10f * MathF.PI / 180f;
        var chars = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
        {
            float x = 72f + i * 16f;
            float y = 100f + i * 16f * MathF.Tan(angle);
            chars.Add(new CharBox(i, x, y, x + 14f, y + 20f));
        }
        var line = new OcrLine(new BBox(72f, 100f, 80f, 34f), text, chars, Angle: angle);
        var (bytes, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = new OcrPage([line]) });

        Assert.Equal(1, result.PagesWritten);
        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains("Hello", extracted.Text);
        var boxes = extracted.CharBoxes.OrderBy(c => c.Left).ToList();
        Assert.True(boxes[^1].Top > boxes[0].Top + 5f,
            $"expected downhill tilt, got first Top={boxes[0].Top}, last Top={boxes[^1].Top}");
    }

    [Fact]
    public void CancelledWrite_ThrowsAndProgressReports()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PdfTextLayerWriter.WriteToBytes(source,
                new Dictionary<int, OcrPage> { [0] = SamplePage() }, ct: cts.Token));

        var sync = new SyncProgress();
        PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = SamplePage() }, progress: sync);
        Assert.Contains((1, 1), sync.Reports);
    }

    private sealed class SyncProgress : IProgress<(int Page, int Total)>
    {
        public List<(int, int)> Reports { get; } = [];
        public void Report((int Page, int Total) value) => Reports.Add(value);
    }

    [Fact]
    public void EncryptedSource_StaysEncrypted()
    {
        var path = TestFixtures.GetEncryptedTestPdfPath();
        var source = File.ReadAllBytes(path);

        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = SamplePage() },
            password: TestFixtures.EncryptedPdfPassword);

        // Without the password the copy still yields nothing; with it, it opens.
        var locked = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.DoesNotContain("Hello", locked.Text);
        var opened = new PdfTextService().ExtractPageText(bytes, 0, TestFixtures.EncryptedPdfPassword);
        Assert.NotNull(opened);
    }

    [Fact]
    public void AstralCharacter_BecomesReplacementInsteadOfCollidingAtFFFF()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        // U+20000 (CJK Ext-B): a surrogate pair at UTF-16 indices 1..2.
        const string text = "A\U00020000B";
        var chars = new List<CharBox>
        {
            new(0, 72f, 100f, 86f, 120f),
            new(1, 88f, 100f, 102f, 120f), // the astral character
            new(3, 104f, 100f, 118f, 120f),
        };
        var page = new OcrPage([new OcrLine(new BBox(72f, 100f, 48f, 20f), text, chars)]);

        var (bytes, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = page });

        Assert.Equal(1, result.PagesWritten);
        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains("A", extracted.Text);
        Assert.Contains("B", extracted.Text);
        Assert.Contains('�', extracted.Text);       // deterministic U+FFFD
        Assert.DoesNotContain('￿', extracted.Text); // never the U+FFFF clamp collision
    }

    [Fact]
    public void GlyphlessFont_HighCodepointCostsOnlyItsOwnGlyph()
    {
        // Dense assignment: one distinct character at U+9FFF needs two glyphs
        // total, not 40960 — the font stays a few KB however high it sits.
        var font = GlyphlessFont.Build(2, new Dictionary<int, int> { [0x9FFF] = 1 });
        Assert.True(font.Length < 8192, $"glyphless font was {font.Length} bytes for one distinct character");
    }

    [Fact]
    public void GlyphlessFont_TablesDeclareTheRealMetrics()
    {
        var font = GlyphlessFont.Build(2, new Dictionary<int, int> { [0x41] = 1 });
        var post = TableData(font, "post");
        Assert.Equal(1u, ReadU32(post, 12));  // isFixedPitch (was written at 16)
        Assert.Equal(0u, ReadU32(post, 16));  // minMemType42 stays zero
        var head = TableData(font, "head");
        Assert.Equal((short)1000, ReadI16(head, 40)); // xMax
        Assert.Equal((short)1000, ReadI16(head, 42)); // yMax
        var hhea = TableData(font, "hhea");
        Assert.Equal((short)1000, ReadI16(hhea, 16)); // xMaxExtent
    }

    private static byte[] TableData(byte[] font, string tag)
    {
        int numTables = (font[4] << 8) | font[5];
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            if (System.Text.Encoding.ASCII.GetString(font, at, 4) != tag) continue;
            int offset = (int)ReadU32(font, at + 8);
            int length = (int)ReadU32(font, at + 12);
            return font.AsSpan(offset, length).ToArray();
        }
        throw new InvalidOperationException($"table {tag} not found");
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int at)
        => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    private static short ReadI16(ReadOnlySpan<byte> b, int at)
        => (short)((b[at] << 8) | b[at + 1]);

    [Fact]
    public void LiveOcrPageText_IsFlaggedAsOcr()
    {
        var page = new OcrPage([
            new OcrLine(new BBox(0f, 0f, 20f, 10f), "ab",
                [new CharBox(0, 0f, 0f, 10f, 10f), new CharBox(1, 10f, 0f, 20f, 10f)]),
        ]);
        var (text, _, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);
        Assert.NotNull(text);
        Assert.True(text!.FromOcr);
    }

    private sealed class FakeOcr : IOcrService
    {
        public OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default)
        {
            var chars = new List<CharBox>();
            const string text = "Hi";
            // Pixel space: pixmap is page/72*dpi, helper must scale back to points.
            float s = width / 612f;
            for (int i = 0; i < text.Length; i++)
                chars.Add(new CharBox(i, (72f + i * 16f) * s, 100f * s, (86f + i * 16f) * s, 120f * s));
            return new OcrPage([new OcrLine(
                new BBox(72f * s, 100f * s, 32f * s, 20f * s), text, chars)]);
        }
        public void Dispose() { }
    }

    [Fact]
    public void RecognizePage_MapsPixelsToPagePoints()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var factory = TestFixtures.CreatePdfFactory();
        var svc = factory.CreatePdfService(path);
        try
        {
            var page = OcrRecognizer.RecognizePage(svc, new FakeOcr(), 0, OcrMode.Full, dpi: 72);
            Assert.Single(page.Lines);
            Assert.Equal(72f, page.Lines[0].Box.X, 1f);
            Assert.Equal(100f, page.Lines[0].Box.Y, 1f);
            Assert.Equal("Hi", page.Lines[0].Text);
        }
        finally { (svc as IDisposable)?.Dispose(); }
    }
}
