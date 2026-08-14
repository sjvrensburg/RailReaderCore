using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for the OCR path: a page with no text layer gets its lines — and in
/// <see cref="OcrMode.Full"/> its text and char boxes — recovered before the analysis
/// pipeline runs, so it takes the same route a born-digital page takes.
/// </summary>
public class OcrPipelineTests
{
    // --- Mapping OCR output (pixmap pixels) into page-point space ---

    [Fact]
    public void Mapper_ScalesLineGeometryIntoPageSpace()
    {
        var page = new OcrPage([new OcrLine(new BBox(100f, 200f, 300f, 20f))]);

        var (text, lines, _) = OcrPageMapper.ToPageSpace(page, 0.5f, 0.25f);

        Assert.Null(text);                       // detection only: nothing transcribed
        Assert.Equal(new BBox(50f, 50f, 150f, 5f), lines[0]);
    }

    [Fact]
    public void Mapper_ReplacesTheInflatedHeightWithTheTrueOne()
    {
        // A 400-wide line at 5° has an axis-aligned height of ~55 even though the line itself
        // is 20 tall. Carrying that inflation forward is what lets neighbouring lines' bands
        // overlap past the merge threshold and fuse.
        var page = new OcrPage([new OcrLine(new BBox(0f, 100f, 400f, 55f), TrueHeight: 20f)]);

        var (_, lines, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);

        // Same centre (127.5), true height — the axis-aligned bound of a rotated rectangle is
        // centred on that rectangle's own centre, so deflating about the centre is exact.
        Assert.Equal(20f, lines[0].H, 0.001f);
        Assert.Equal(127.5f, lines[0].Y + lines[0].H / 2f, 0.001f);
        Assert.Equal(0f, lines[0].X, 0.001f);
        Assert.Equal(400f, lines[0].W, 0.001f);
    }

    [Fact]
    public void Mapper_KeepsTheAxisAlignedHeightWhenNoQuadWasMeasured()
    {
        var page = new OcrPage([new OcrLine(new BBox(0f, 100f, 400f, 55f))]);

        var (_, lines, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);

        Assert.Equal(new BBox(0f, 100f, 400f, 55f), lines[0]);
    }

    [Fact]
    public void Mapper_CarriesTheSkewAngleIntoPageSpace()
    {
        float skew = 3f * MathF.PI / 180f;
        var page = new OcrPage([new OcrLine(new BBox(0f, 0f, 400f, 20f))], skew);

        var (_, _, uniform) = OcrPageMapper.ToPageSpace(page, 2f, 2f);
        Assert.Equal(skew, uniform, 0.0001f);

        // Under a non-uniform scale an angle is not preserved: the tangent picks up the axis
        // ratio. Today's rasteriser is uniform, but the conversion must not assume it.
        var (_, _, squashed) = OcrPageMapper.ToPageSpace(page, 2f, 1f);
        Assert.Equal(MathF.Atan(MathF.Tan(skew) * 0.5f), squashed, 0.0001f);
    }

    [Fact]
    public void Mapper_AssemblesPageTextAndOffsetsCharIndices()
    {
        var page = new OcrPage([
            new OcrLine(new BBox(0f, 0f, 20f, 10f), "ab",
                [new CharBox(0, 0f, 0f, 10f, 10f), new CharBox(1, 10f, 0f, 20f, 10f)]),
            new OcrLine(new BBox(0f, 20f, 10f, 10f), "c",
                [new CharBox(0, 0f, 20f, 10f, 30f)]),
        ]);

        var (text, _, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);

        Assert.NotNull(text);
        Assert.Equal("ab\nc\n", text!.Text);
        // Each line's own 0-based indices are rebased onto the assembled page string, so
        // every box still addresses the character it was drawn for.
        Assert.Equal([0, 1, 3], text.CharBoxes.Select(c => c.Index));
        foreach (var cb in text.CharBoxes)
            Assert.InRange(cb.Index, 0, text.Text.Length - 1);
        Assert.Equal('c', text.Text[text.CharBoxes[2].Index]);
    }

    [Fact]
    public void Mapper_DropsCharBoxesPointingOutsideTheirLine()
    {
        // An index past its own line's text would land on some other line's character once
        // offset. Dropping it loses one glyph's geometry; keeping it would corrupt extraction.
        var page = new OcrPage([
            new OcrLine(new BBox(0f, 0f, 10f, 10f), "a",
                [new CharBox(0, 0f, 0f, 10f, 10f), new CharBox(5, 0f, 0f, 10f, 10f)]),
        ]);

        var (text, _, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);

        Assert.Single(text!.CharBoxes);
        Assert.Equal("a\n", text.Text);
    }

    [Fact]
    public void Mapper_SkipsLinesWithNoTextWhenAssembling()
    {
        var page = new OcrPage([
            new OcrLine(new BBox(0f, 0f, 10f, 10f)),                 // detected, not read
            new OcrLine(new BBox(0f, 20f, 10f, 10f), "x", [new CharBox(0, 0f, 20f, 10f, 30f)]),
        ]);

        var (text, lines, _) = OcrPageMapper.ToPageSpace(page, 1f, 1f);

        Assert.Equal(2, lines.Count);            // geometry survives for both
        Assert.Equal("x\n", text!.Text);
        Assert.Equal(0, text.CharBoxes[0].Index);
    }

    // --- Assigning detected lines to layout blocks ---

    private static LayoutBlock BlockAt(float x, float y, float w, float h)
        => new() { BBox = new BBox(x, y, w, h), Role = BlockRole.Text };

    [Fact]
    public void LinesFromOcr_TakesLinesWhoseCentreIsInTheBlock()
    {
        var block = BlockAt(100f, 100f, 200f, 100f).BBox;
        List<BBox> ocr = [
            new(100f, 105f, 200f, 12f),   // inside
            new(100f, 150f, 200f, 12f),   // inside
            new(100f, 250f, 200f, 12f),   // below the block
        ];

        var lines = LineDetector.LinesFromOcr(block, ocr);

        Assert.Equal(2, lines.Count);
        Assert.Equal(111f, lines[0].Y, 3);
    }

    [Fact]
    public void LinesFromOcr_LeavesTheOtherColumnsLinesAlone()
    {
        // Two-column page: both columns' lines share a Y band, so vertical position alone
        // cannot separate them. Requiring most of the line's width inside the block does.
        var left = BlockAt(100f, 100f, 150f, 100f).BBox;
        List<BBox> ocr = [
            new(100f, 105f, 140f, 12f),   // left column
            new(300f, 105f, 140f, 12f),   // right column, same band
        ];

        var lines = LineDetector.LinesFromOcr(left, ocr);

        Assert.Single(lines);
        Assert.Equal(100f, lines[0].X, 3);
    }

    [Fact]
    public void LinesFromOcr_ClipsToTheBlock()
    {
        var block = BlockAt(100f, 100f, 100f, 100f).BBox;
        List<BBox> ocr = [new(80f, 110f, 110f, 12f)];   // starts left of the block

        var lines = LineDetector.LinesFromOcr(block, ocr);

        Assert.Equal(100f, lines[0].X, 3);
        Assert.Equal(90f, lines[0].Width, 3);           // 100..190
    }

    [Fact]
    public void DetectLines_PrefersOcrLinesOverPixelProjection()
    {
        // No char boxes and a blank pixmap: pixel projection has nothing to find, so without
        // OCR the block would collapse to a single full-height line.
        var block = BlockAt(100f, 100f, 200f, 60f);
        var pixmap = new byte[400 * 200 * 3];
        Array.Fill(pixmap, (byte)255);
        List<BBox> ocr = [
            new(100f, 102f, 200f, 12f),
            new(100f, 122f, 200f, 12f),
            new(100f, 142f, 200f, 12f),
        ];

        var lines = LineDetector.DetectLines(block, charBoxes: null, pixmap, 400, 200, 1f, 1f,
            ocrLines: ocr);

        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void DetectLines_IgnoresOcrLinesWhenCharBoxesExist()
    {
        // A page with a text layer must keep the char-clustering result: OCR lines only ever
        // accompany a page that had none, but the precedence has to be explicit.
        var block = BlockAt(100f, 100f, 200f, 40f);
        var pixmap = new byte[400 * 200 * 3];
        Array.Fill(pixmap, (byte)255);
        List<CharBox> chars = [
            new(0, 100f, 100f, 107f, 110f),
            new(1, 108f, 100f, 115f, 110f),
        ];
        List<BBox> ocr = [
            new(100f, 100f, 200f, 10f),
            new(100f, 115f, 200f, 10f),
            new(100f, 130f, 200f, 10f),
        ];

        var lines = LineDetector.DetectLines(block, chars, pixmap, 400, 200, 1f, 1f, ocrLines: ocr);

        Assert.Single(lines);
    }

    // --- The worker: OCR runs only where it should, and its output reaches the result ---

    /// <summary>OCR engine that reports fixed lines and records how often it was asked.</summary>
    private sealed class FakeOcrService : IOcrService
    {
        private readonly Func<OcrMode, OcrPage> _factory;
        private int _calls;

        public FakeOcrService(Func<OcrMode, OcrPage> factory) => _factory = factory;

        public int Calls => Volatile.Read(ref _calls);
        public OcrMode? LastMode { get; private set; }

        public OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LastMode = mode;
            return _factory(mode);
        }

        public void Dispose() { }
    }

    /// <summary>Three text lines across the top half of an 800×800 pixmap.</summary>
    private static OcrPage ThreeLines(bool withText) =>
        new([
            Line(0f, withText, "alpha"),
            Line(40f, withText, "beta"),
            Line(80f, withText, "gamma"),
        ]);

    private static OcrLine Line(float top, bool withText, string text)
    {
        var box = new BBox(0f, top, 400f, 20f);
        if (!withText) return new OcrLine(box);
        var chars = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
            chars.Add(new CharBox(i, i * 20f, top, i * 20f + 18f, top + 20f));
        return new OcrLine(box, text, chars);
    }

    private static PageAnalysis OneFullPageBlock() => new()
    {
        Blocks = [new LayoutBlock { BBox = new BBox(0f, 0f, 400f, 400f), Role = BlockRole.Text }],
    };

    private static AnalysisRequest Request(IReadOnlyList<CharBox>? charBoxes, int page = 0, float skew = 0f) =>
        new("/tmp/scan.pdf", page, new byte[800 * 800 * 3], 800, 800, 400d, 400d,
            charBoxes, new AnalysisParams(), OcrSkew: skew);

    private static AnalysisResult PollUntilResult(AnalysisWorker worker)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (worker.Poll() is { } r) return r;
            Thread.Sleep(10);
        }
        throw new TimeoutException("worker produced no result");
    }

    private static AnalysisWorker MakeWorker(IOcrService ocr, OcrMode mode) =>
        new(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneFullPageBlock),
            new SynchronousThreadMarshaller(),
            ocrServiceFactory: () => ocr,
            ocrMode: mode);

    [Fact]
    public void Worker_LinesMode_UsesOcrLinesForLineDetection()
    {
        var ocr = new FakeOcrService(_ => ThreeLines(withText: false));
        using var worker = MakeWorker(ocr, OcrMode.Lines);

        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Equal(OcrMode.Lines, ocr.LastMode);
        // Detected lines become the block's lines instead of the pixel-projection fallback.
        Assert.Equal(3, result.Analysis.Blocks[0].Lines.Count);
        // Lines mode transcribes nothing, so no text is published.
        Assert.Null(result.OcrText);
    }

    [Fact]
    public void Worker_FullMode_PublishesRecoveredTextAndCharBoxes()
    {
        var ocr = new FakeOcrService(_ => ThreeLines(withText: true));
        using var worker = MakeWorker(ocr, OcrMode.Full);

        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Equal(OcrMode.Full, ocr.LastMode);
        Assert.NotNull(result.OcrText);
        Assert.Equal("alpha\nbeta\ngamma\n", result.OcrText!.Text);
        Assert.Equal(14, result.OcrText.CharBoxes.Count);

        // Geometry is in page points: the pixmap is 800px for a 400pt page, so a line box
        // 20px tall is 10pt tall.
        Assert.All(result.OcrText.CharBoxes, cb => Assert.InRange(cb.Bottom - cb.Top, 9f, 11f));

        // With char boxes recovered, line detection takes the char-clustering path.
        Assert.Equal(3, result.Analysis.Blocks[0].Lines.Count);
    }

    [Fact]
    public void Worker_SkipsOcrWhenThePageHasATextLayer()
    {
        var ocr = new FakeOcrService(_ => ThreeLines(withText: true));
        using var worker = MakeWorker(ocr, OcrMode.Full);

        List<CharBox> existing = [new(0, 10f, 10f, 17f, 20f)];
        Assert.True(worker.Submit(Request(existing)));
        var result = PollUntilResult(worker);

        Assert.Equal(0, ocr.Calls);
        Assert.Null(result.OcrText);
    }

    [Fact]
    public void Worker_OcrModeOff_NeverCallsTheEngine()
    {
        var ocr = new FakeOcrService(_ => ThreeLines(withText: true));
        using var worker = MakeWorker(ocr, OcrMode.Off);

        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Equal(0, ocr.Calls);
        Assert.Null(result.OcrText);
    }

    [Fact]
    public void Worker_OcrModeIsSettableAtRuntime()
    {
        var ocr = new FakeOcrService(_ => ThreeLines(withText: false));
        using var worker = MakeWorker(ocr, OcrMode.Off);

        Assert.True(worker.Submit(Request(charBoxes: null)));
        PollUntilResult(worker);
        Assert.Equal(0, ocr.Calls);

        worker.OcrMode = OcrMode.Lines;
        Assert.True(worker.Submit(Request(charBoxes: null)));
        PollUntilResult(worker);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public void Worker_SurvivesAnOcrFailure()
    {
        // OCR is best-effort: a page it cannot read must still get its layout analysis, and
        // the worker must keep serving later requests.
        var ocr = new FakeOcrService(_ => throw new InvalidOperationException("synthetic OCR failure"));
        using var worker = MakeWorker(ocr, OcrMode.Full);

        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Null(result.OcrText);
        Assert.Single(result.Analysis.Blocks);       // analysis still ran
        Assert.True(worker.IsIdle);
    }

    [Fact]
    public void Worker_WithoutAnOcrService_BehavesAsBefore()
    {
        using var worker = new AnalysisWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneFullPageBlock),
            new SynchronousThreadMarshaller());

        Assert.Equal(OcrMode.Off, worker.OcrMode);
        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Null(result.OcrText);
        Assert.Single(result.Analysis.Blocks);
    }

    [Fact]
    public void Worker_OcrLoadFailure_IsRecordedAndAnalysisContinues()
    {
        using var worker = new AnalysisWorker(FakeLayoutAnalyzer.DefaultCapabilities,
            () => new FakeLayoutAnalyzer(OneFullPageBlock),
            new SynchronousThreadMarshaller(),
            ocrServiceFactory: () => throw new FileNotFoundException("no models here"),
            ocrMode: OcrMode.Full);

        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Single(result.Analysis.Blocks);
        Assert.Contains("no models here", worker.OcrStartupError);
    }

    // --- Recognition must not stall layout inference for other pages (issue #100) ---

    /// <summary>OCR engine that blocks until released, standing in for a heavy model set: one
    /// page of PP-OCRv6 Medium was measured at over a minute.</summary>
    private sealed class BlockingOcrService : IOcrService
    {
        private readonly ManualResetEventSlim _release = new(false);

        public void Release() => _release.Set();

        public OcrPage Recognize(byte[] rgbBytes, int width, int height, OcrMode mode, CancellationToken ct = default)
        {
            _release.Wait(ct);
            return ThreeLines(withText: true);
        }

        public void Dispose() => _release.Dispose();
    }

    [Fact]
    public void Worker_PageNeedingOcr_DoesNotStallAPageThatDoesNot()
    {
        // The reported failure in miniature: a scanned page under a heavy model set held the only
        // worker for its whole recognition, and layout analysis stopped everywhere — including
        // for a different document with a perfectly good text layer.
        var ocr = new BlockingOcrService();          // the worker owns and disposes it
        using var worker = MakeWorker(ocr, OcrMode.Full);

        Assert.True(worker.Submit(Request(charBoxes: null, page: 0)));            // needs OCR; will block
        List<CharBox> existing = [new(0, 10f, 10f, 17f, 20f)];
        Assert.True(worker.Submit(Request(existing, page: 1)));                   // has a text layer

        // The second request must come back while the first is still inside the engine.
        var result = PollUntilResult(worker);
        Assert.Equal(1, result.Page);
        Assert.False(worker.IsIdle);     // page 0 is still in flight

        ocr.Release();
        Assert.Equal(0, PollUntilResult(worker).Page);
        Assert.True(worker.IsIdle);
    }

    // --- Reusing an earlier pass's OCR output instead of re-running it (issue #100) ---

    // Tightly-set body text on a page a couple of degrees off square: the geometry from
    // DeskewLineGroupingTests, which grouping only resolves into one band per printed line when
    // the shear is applied.
    private const int SkewLines = 12, SkewGlyphs = 40;
    private const float SkewDegrees = 2f;

    private static List<CharBox> SkewedParagraph()
    {
        const float advance = 10f, glyphW = 8f, glyphH = 10f, pitch = 14f, firstBaseline = 100f;
        float pivotX = SkewGlyphs * advance / 2f;
        float tan = MathF.Tan(SkewDegrees * MathF.PI / 180f);

        var boxes = new List<CharBox>(SkewLines * SkewGlyphs);
        int index = 0;
        for (int line = 0; line < SkewLines; line++)
        {
            float baseline = firstBaseline + pitch * line;
            for (int g = 0; g < SkewGlyphs; g++)
            {
                float left = g * advance;
                float centreY = baseline + (left + glyphW / 2f - pivotX) * tan;
                boxes.Add(new CharBox(index++, left, centreY - glyphH / 2f, left + glyphW, centreY + glyphH / 2f));
            }
        }
        return boxes;
    }

    private static float SkewRadians => SkewDegrees * MathF.PI / 180f;

    [Fact]
    public void Worker_ReusesSuppliedOcrCharBoxes_WithoutRecognisingAgain()
    {
        // A page whose OCR output the consumer cached and handed back: recognition is the
        // expensive half and must not run twice, but the correction it measured has to survive,
        // or the caller would be trading tens of seconds for wrong line grouping.
        var ocr = new FakeOcrService(_ => ThreeLines(withText: true));
        using var worker = MakeWorker(ocr, OcrMode.Full);
        worker.DeskewEnabled = true;

        Assert.True(worker.Submit(Request(SkewedParagraph(), skew: SkewRadians)));
        var result = PollUntilResult(worker);

        Assert.Equal(0, ocr.Calls);
        Assert.Equal(SkewLines, result.Analysis.Blocks[0].Lines.Count);
    }

    [Fact]
    public void Worker_SuppliedSkew_IsGatedByTheCurrentDeskewSetting()
    {
        // The paired half: the gate is applied where the shear is consumed, not where it was
        // measured, so turning deskew off reaches a page whose angle was measured earlier.
        var ocr = new FakeOcrService(_ => ThreeLines(withText: true));
        using var worker = MakeWorker(ocr, OcrMode.Full);
        worker.DeskewEnabled = false;

        Assert.True(worker.Submit(Request(SkewedParagraph(), skew: SkewRadians)));
        var result = PollUntilResult(worker);

        Assert.Equal(0, ocr.Calls);
        Assert.True(result.Analysis.Blocks[0].Lines.Count < SkewLines,
            "without the shear the skewed glyphs must not resolve into one band per printed line");
    }

    [Fact]
    public void Worker_PublishesTheRawMeasuredSkew_EvenWithDeskewOff()
    {
        // What makes the reuse above possible: the result carries the measurement itself rather
        // than the gated value, so a consumer can cache it and have it re-gated later.
        var ocr = new FakeOcrService(_ => new OcrPage(ThreeLines(withText: true).Lines, SkewRadians));
        using var worker = MakeWorker(ocr, OcrMode.Full);
        worker.DeskewEnabled = false;

        Assert.True(worker.Submit(Request(charBoxes: null)));
        var result = PollUntilResult(worker);

        Assert.Equal(1, ocr.Calls);
        Assert.InRange(result.OcrSkew, SkewRadians - 1e-5f, SkewRadians + 1e-5f);
    }
}
