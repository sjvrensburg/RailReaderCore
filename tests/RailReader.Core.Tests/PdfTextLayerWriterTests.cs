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
    public void MixedPage_SkipLeavesNativeAndExistingOcrUntouched()
    {
        var path = TestFixtures.GetTestPdfPath(); // native text
        var source = File.ReadAllBytes(path);
        var noSkip = new TextLayerOptions { SkipPagesWithNativeText = false };
        var (mixed, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = SamplePage() }, options: noSkip);
        Assert.True(new PdfTextService().ExtractPageText(mixed, 0).FromOcr);

        // Default rewrite must skip — and skipping must not delete the old layer.
        var (after, result) = PdfTextLayerWriter.WriteToBytes(mixed,
            new Dictionary<int, OcrPage> { [0] = SamplePage("World") });
        Assert.Equal(0, result.PagesWritten);
        Assert.Equal(1, result.PagesSkipped);
        var extracted = new PdfTextService().ExtractPageText(after, 0);
        Assert.Contains("Hello", extracted.Text);   // old OCR layer survives
        Assert.DoesNotContain("World", extracted.Text);
        Assert.True(extracted.FromOcr);
    }

    [Fact]
    public void EmptyReplacement_PreservesExistingOcrLayer()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var (once, _) = WriteSample(source);

        var (after, result) = PdfTextLayerWriter.WriteToBytes(once,
            new Dictionary<int, OcrPage> { [0] = new OcrPage([]) });
        Assert.Equal(0, result.PagesWritten);
        Assert.Equal(1, result.PagesSkipped);
        var extracted = new PdfTextService().ExtractPageText(after, 0);
        Assert.Contains("Hello", extracted.Text);
        Assert.True(extracted.FromOcr);
    }

    [Fact]
    public void AstralWithCodeUnitBoxes_IsWrittenOnce()
    {
        // Engines emit one box per UTF-16 code unit, so the astral glyph at
        // indices 1..2 arrives as two boxes; it must still be written once.
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        const string text = "A\U00020000B";
        var chars = new List<CharBox>
        {
            new(0, 72f, 100f, 86f, 120f),
            new(1, 88f, 100f, 102f, 120f),
            new(2, 88f, 100f, 102f, 120f), // low-surrogate half of the same glyph
            new(3, 104f, 100f, 118f, 120f),
        };
        var page = new OcrPage([new OcrLine(new BBox(72f, 100f, 48f, 20f), text, chars)]);
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = page });

        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Equal(1, extracted.Text.Count(c => c == '�'));
        Assert.Contains("A", extracted.Text);
        Assert.Contains("B", extracted.Text);
    }

    [Fact]
    public void ControlAndNoncharacterCodepoints_AreSanitized()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var page = new OcrPage([new OcrLine(new BBox(72f, 100f, 80f, 20f), "A\0￿B")]);
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = page },
            options: new TextLayerOptions { UseCharBoxes = false });

        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.DoesNotContain('\0', extracted.Text);
        Assert.DoesNotContain('￿', extracted.Text);
        Assert.Contains('�', extracted.Text);
    }

    [Fact]
    public void EmptyDictionary_WritesNothingAndEmbedsNoFont()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var (bytes, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage>());
        Assert.Equal(0, result.PagesWritten);
        Assert.Equal(0, result.PagesSkipped);
        Assert.True(bytes.Length <= source.Length + 400,
            $"empty write grew {source.Length} -> {bytes.Length} bytes (unused font embedded?)");
        Assert.Equal("", new PdfTextService().ExtractPageText(bytes, 0).Text);
    }

    [Fact]
    public void OutOfRangePage_CountsAsSkipped()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        var (_, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [99] = SamplePage() });
        Assert.Equal(0, result.PagesWritten);
        Assert.Equal(1, result.PagesSkipped);
    }

    [Fact]
    public void SkewedWordFallback_FollowsTheBaseline()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var source = File.ReadAllBytes(path);
        float angle = 10f * MathF.PI / 180f;
        var line = new OcrLine(new BBox(72f, 100f, 320f, 20f), "Hello world foo bar", Angle: angle);
        var (bytes, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = new OcrPage([line]) },
            options: new TextLayerOptions { UseCharBoxes = false });

        Assert.Equal(1, result.PagesWritten);
        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains("Hello", extracted.Text);
        var boxes = extracted.CharBoxes.Where(c => c.Right > c.Left).OrderBy(c => c.Left).ToList();
        Assert.NotEmpty(boxes);
        Assert.True(boxes[^1].Top > boxes[0].Top + 15f,
            $"words did not descend with the baseline: first Top={boxes[0].Top}, last Top={boxes[^1].Top}");
    }

    [Fact]
    public void RecognizePage_NonPositiveDpi_FallsBackToDefault()
    {
        var path = TestFixtures.GetTextlessTestPdfPath();
        var factory = TestFixtures.CreatePdfFactory();
        var svc = factory.CreatePdfService(path);
        var ocr = new FakeOcr();
        var page = OcrRecognizer.RecognizePage(svc, ocr, 0, OcrMode.Full, dpi: 0);
        // Default 300 dpi on a 792pt page => a multi-thousand-pixel raster,
        // not the 1-pixel clamp an unchecked dpi=0 used to produce.
        Assert.True(ocr.LastWidth > 1000, $"raster width was {ocr.LastWidth}");
        Assert.Single(page.Lines);
    }

    // Hand-crafted one-page PDFs (generated out-of-process, embedded as golden
    // fixtures): native text drawn only inside a form XObject, and visible
    // native text in a font whose name contains "RailReaderGlyphless".
    private const string FormNativePdfBase64 = "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXSAvQ29udGVudHMgNCAwIFIgL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNSAwIFIgPj4gL1hPYmplY3QgPDwgL0ZtMSA2IDAgUiA+PiA+PiA+PgplbmRvYmoKNCAwIG9iago8PCAvTGVuZ3RoIDExID4+CnN0cmVhbQpxIC9GbTEgRG8gUQplbmRzdHJlYW0KZW5kb2JqCjUgMCBvYmoKPDwgL1R5cGUgL0ZvbnQgL1N1YnR5cGUgL1R5cGUxIC9CYXNlRm9udCAvSGVsdmV0aWNhIC9FbmNvZGluZyAvV2luQW5zaUVuY29kaW5nID4+CmVuZG9iago2IDAgb2JqCjw8IC9UeXBlIC9YT2JqZWN0IC9TdWJ0eXBlIC9Gb3JtIC9CQm94IFswIDAgNjEyIDc5Ml0gL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNSAwIFIgPj4gPj4gL0xlbmd0aCA0MSA+PgpzdHJlYW0KQlQgL0YxIDEyIFRmIDUwIDcwMCBUZCAoTkFUSVZFRk9STSkgVGogRVQKZW5kc3RyZWFtCmVuZG9iagp4cmVmCjAgNwowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMDkgMDAwMDAgbiAKMDAwMDAwMDA1OCAwMDAwMCBuIAowMDAwMDAwMTE1IDAwMDAwIG4gCjAwMDAwMDAyNjcgMDAwMDAgbiAKMDAwMDAwMDMyOCAwMDAwMCBuIAowMDAwMDAwNDI1IDAwMDAwIG4gCnRyYWlsZXIKPDwgL1NpemUgNyAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKNjA1CiUlRU9GCg==";
    private const string SimilarFontPdfBase64 = "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXSAvQ29udGVudHMgNCAwIFIgL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNSAwIFIgPj4gL1hPYmplY3QgPDwgL0ZtMSA2IDAgUiA+PiA+PiA+PgplbmRvYmoKNCAwIG9iago8PCAvTGVuZ3RoIDM2ID4+CnN0cmVhbQpCVCAvRjEgMTIgVGYgNTAgNzAwIFRkIChIZWxsbykgVGogRVQKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqCjw8IC9UeXBlIC9Gb250IC9TdWJ0eXBlIC9UeXBlMSAvQmFzZUZvbnQgL0FBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFSYWlsUmVhZGVyR2x5cGhsZXNzIC9FbmNvZGluZyAvV2luQW5zaUVuY29kaW5nID4+CmVuZG9iagp4cmVmCjAgNgowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMDkgMDAwMDAgbiAKMDAwMDAwMDA1OCAwMDAwMCBuIAowMDAwMDAwMTE1IDAwMDAwIG4gCjAwMDAwMDAyNjcgMDAwMDAgbiAKMDAwMDAwMDM1MyAwMDAwMCBuIAp0cmFpbGVyCjw8IC9TaXplIDYgL1Jvb3QgMSAwIFIgPj4Kc3RhcnR4cmVmCjU2MAolJUVPRgo=";

    [Fact]
    public void MixedFormNativePage_SkipLeavesEverythingUntouched()
    {
        var source = Convert.FromBase64String(FormNativePdfBase64);
        Assert.Contains("NATIVEFORM", new PdfTextService().ExtractPageText(source, 0).Text);

        var noSkip = new TextLayerOptions { SkipPagesWithNativeText = false };
        var (mixed, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = SamplePage() }, options: noSkip);

        var (after, result) = PdfTextLayerWriter.WriteToBytes(mixed,
            new Dictionary<int, OcrPage> { [0] = SamplePage("World") });
        Assert.Equal(0, result.PagesWritten);
        Assert.Equal(1, result.PagesSkipped);
        var extracted = new PdfTextService().ExtractPageText(after, 0);
        Assert.Contains("NATIVEFORM", extracted.Text);
        Assert.Contains("Hello", extracted.Text);
        Assert.DoesNotContain("World", extracted.Text);
    }

    [Fact]
    public void VisibleTextInSimilarlyNamedFont_IsNotRemoved()
    {
        var source = Convert.FromBase64String(SimilarFontPdfBase64);
        var (after, result) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = SamplePage("NEWOCR") });
        Assert.Equal(0, result.PagesWritten);
        var extracted = new PdfTextService().ExtractPageText(after, 0);
        Assert.Contains("Hello", extracted.Text); // native text survives
        Assert.False(extracted.FromOcr);
    }

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
        public int LastWidth { get; private set; }

        public OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default)
        {
            LastWidth = width;
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

    // --- Read-back fidelity (review of #156) ----------------------------------

    /// <summary>
    /// A line boxed the way RapidOcrService boxes it: one ink-tight box per
    /// non-space character, none on the spaces, glyph tops and bottoms following
    /// the ink (x-height letters, descenders, apostrophe high, hyphen mid, period low).
    /// </summary>
    private static OcrLine InkBoxedLine(string text, float x0, float top, float advance = 10f)
    {
        var chars = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ') continue;
            var (t, b) = c switch
            {
                '\'' => (0f, 4f),
                '-' => (7f, 8.5f),
                '.' => (12f, 14f),
                'p' or 'g' or 'y' => (4f, 18f),
                _ when char.IsLower(c) && "bdfhklt".IndexOf(c) < 0 => (4f, 14f),
                _ => (0f, 14f),
            };
            float left = x0 + i * advance;
            chars.Add(new CharBox(i, left, top + t, left + advance * 0.8f, top + b));
        }
        return new OcrLine(new BBox(x0, top, text.Length * advance, 18f), text, chars);
    }

    [Fact]
    public void InkBoxedLines_ReadBackAsWholeWordsAndLines()
    {
        // Per-glyph objects seated on these boxes read back as "H e l l o" with
        // the apostrophe and hyphen on lines of their own.
        var source = File.ReadAllBytes(TestFixtures.GetTextlessTestPdfPath());
        var page = new OcrPage([
            InkBoxedLine("Hello brave world", 72f, 100f),
            InkBoxedLine("don't stop-gap typing.", 72f, 130f),
        ]);
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source, new Dictionary<int, OcrPage> { [0] = page });

        var lines = new PdfTextService().ExtractPageText(bytes, 0).Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["Hello brave world", "don't stop-gap typing."], lines);
    }

    [Fact]
    public void WordsWithoutCharBoxes_AreWrittenInTheirOwnSlot()
    {
        // The engine boxed "Hello" and "again" but could not locate "world".
        var source = File.ReadAllBytes(TestFixtures.GetTextlessTestPdfPath());
        const string text = "Hello world again";
        var chars = new List<CharBox>();
        foreach (int i in new[] { 0, 1, 2, 3, 4, 12, 13, 14, 15, 16 })
            chars.Add(new CharBox(i, 72f + i * 10f, 100f, 80f + i * 10f, 114f));
        var line = new OcrLine(new BBox(72f, 100f, 170f, 14f), text, chars);
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = new OcrPage([line]) });

        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains(text, extracted.Text);
        // "world" sits between its fitted neighbours: after "Hello", before "again".
        int w = extracted.Text.IndexOf("world", StringComparison.Ordinal);
        var boxes = extracted.CharBoxes.ToDictionary(c => c.Index);
        Assert.InRange(boxes[w].Left, boxes[w - 2].Right, boxes[w + 6].Left);
        Assert.InRange(boxes[w + 4].Right, boxes[w].Left, boxes[w + 6].Left);
    }

    [Fact]
    public void ReadBackGlyphHeights_FollowTheInkNotTheDetectorBand()
    {
        // The detector's band (TrueHeight) is dilated well past the ink; read-back
        // glyphs that tall would overlap the next line and merge the two in
        // char-clustering line detection.
        var source = File.ReadAllBytes(TestFixtures.GetTextlessTestPdfPath());
        var chars = new List<CharBox>();
        for (int i = 0; i < 5; i++) chars.Add(new CharBox(i, 72f + i * 12f, 100f, 82f + i * 12f, 114f));
        var line = new OcrLine(new BBox(72f, 92f, 60f, 30f), "Hello", chars, TrueHeight: 30f);
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = new OcrPage([line]) });

        var glyphs = new PdfTextService().ExtractPageText(bytes, 0).CharBoxes.Where(c => c.Right > c.Left).ToList();
        Assert.Equal(5, glyphs.Count);
        Assert.All(glyphs, g =>
        {
            Assert.InRange(g.Top, 99f, 101f);
            Assert.InRange(g.Bottom, 113f, 115f);
        });
    }

    [Fact]
    public void GlyphBoxesLeakingIntoTheNextLine_DoNotMergeTheLines()
    {
        // Tightly set lines (12pt glyphs, 16pt pitch) where the tightener's window
        // caught a neighbour's ink: one glyph per line reaches deep into the other
        // line. A plain union of the boxes would overlap the two bands.
        var source = File.ReadAllBytes(TestFixtures.GetTextlessTestPdfPath());
        OcrLine Line(string text, float top, int leaker, float leakTop, float leakBottom)
        {
            var chars = new List<CharBox>();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == ' ') continue;
                float t = i == leaker ? leakTop : top, b = i == leaker ? leakBottom : top + 12f;
                chars.Add(new CharBox(i, 72f + i * 8f, t, 78f + i * 8f, b));
            }
            return new OcrLine(new BBox(72f, top - 3f, text.Length * 8f, 18f), text, chars, TrueHeight: 18f);
        }
        var page = new OcrPage([
            Line("first line of text", 100f, leaker: 2, leakTop: 100f, leakBottom: 122f),
            Line("second line of text", 116f, leaker: 3, leakTop: 104f, leakBottom: 128f),
        ]);
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source, new Dictionary<int, OcrPage> { [0] = page });

        var lines = new PdfTextService().ExtractPageText(bytes, 0).Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["first line of text", "second line of text"], lines);
    }

    [Fact]
    public void WordsSeparatedByNonAsciiWhitespace_AreJoinedWithARealSpace()
    {
        // No U+0020 anywhere in the input; the joining space must still be one.
        var source = File.ReadAllBytes(TestFixtures.GetTextlessTestPdfPath());
        var line = new OcrLine(new BBox(72f, 100f, 120f, 14f), "中文\u3000テスト");
        var (bytes, _) = PdfTextLayerWriter.WriteToBytes(source,
            new Dictionary<int, OcrPage> { [0] = new OcrPage([line]) });

        var extracted = new PdfTextService().ExtractPageText(bytes, 0);
        Assert.Contains("中文 テスト", extracted.Text);
        Assert.DoesNotContain('\uFFFD', extracted.Text);
    }

    [OcrModelFact]
    public void RealOcr_RoundTripsThroughTheLayerVerbatim()
    {
        // End to end on a rasterised page: whatever the engine read must read back
        // from the written layer unchanged — same words, same spacing, same lines.
        string[] lines =
        [
            "The quick brown fox doesn't jump over the lazy dog.",
            "Rail reading guides the eye line-by-line, e.g. at high zoom.",
            "Searching for typography should find this exact word, right?",
        ];
        const float scale = 200f / 72f;
        using var bitmap = new SkiaSharp.SKBitmap((int)(612 * scale), (int)(792 * scale));
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true })
        using (var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 12f * scale))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            for (int i = 0; i < lines.Length; i++)
                canvas.DrawText(lines[i], 72f * scale, (100f + i * 16f) * scale, font, paint);
        }
        var path = Path.Combine(Path.GetTempPath(), $"railreader_scan_{Guid.NewGuid():N}.pdf");
        try
        {
            using (var stream = File.Create(path))
            using (var doc = SkiaSharp.SKDocument.CreatePdf(stream))
            {
                doc.BeginPage(612, 792).DrawBitmap(bitmap, SkiaSharp.SKRect.Create(0, 0, 612, 792));
                doc.EndPage();
                doc.Close();
            }

            var svc = TestFixtures.CreatePdfFactory().CreatePdfService(path);
            using var ocr = new RailReader.Core.Ocr.RapidOcr.RapidOcrService();
            var pages = OcrRecognizer.RecognizeDocument(svc, ocr);
            var recognised = pages[0].Lines.Select(l => l.Text!.Trim()).ToArray();
            Assert.Equal(lines.Length, recognised.Length);

            var (bytes, result) = PdfTextLayerWriter.WriteToBytes(svc.PdfBytes, pages);
            Assert.Equal(1, result.PagesWritten);
            var readBack = new PdfTextService().ExtractPageText(bytes, 0).Text
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(recognised, readBack);
            (svc as IDisposable)?.Dispose();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ProvenanceRule_MatchesThePdfPigBackend()
    {
        // Same fixtures as PdfPigServiceTests: our mark ⇒ OCR; another producer's
        // invisible text ⇒ not ours to vouch for.
        var svc = new PdfTextService();
        var marked = svc.ExtractPageText(Convert.FromBase64String(OcrProvenanceFixtures.MarkedInvisibleHelveticaPdfBase64), 0);
        Assert.Contains("Marked", marked.Text);
        Assert.True(marked.FromOcr);

        var foreign = svc.ExtractPageText(Convert.FromBase64String(OcrProvenanceFixtures.UnmarkedInvisibleHelveticaPdfBase64), 0);
        Assert.Contains("Fallback", foreign.Text);
        Assert.False(foreign.FromOcr);
    }
}
