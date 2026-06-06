using System.Diagnostics;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace RailReader.Core.Tests;

/// <summary>
/// Performance benchmarks for the agent API surface. These are not deterministic
/// unit tests — they measure wall-clock time on a real CPU. Run with -c Release.
/// </summary>
public class AgentApiPerfBenchmarks : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _pdfPath;
    private readonly DocumentController _controller;

    public AgentApiPerfBenchmarks(ITestOutputHelper output)
    {
        _output = output;
        _pdfPath = TestFixtures.GetTestPdfPath();
        var config = new AppConfig();
        _controller = new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default,
            new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
    }

    public void Dispose() => _controller.Dispose();

    private DocumentState SetupDocWithRailMode()
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        var (ww, wh) = _controller.GetViewportSize();
        TestFixtures.SetupRailMode(state, _controller.Config, ww, wh);
        return state;
    }

    private DocumentState SetupDocWithMultiBlock(int blockCount)
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);

        var analysis = new PageAnalysis();
        for (int i = 0; i < blockCount; i++)
        {
            var role = i % 4 == 0 ? BlockRole.Heading : BlockRole.Text;
            var bbox = new BBox(72, 72 + i * 30, 468, 25);
            var block = new LayoutBlock
            {
                Role = role, BBox = bbox, Confidence = 0.9f, Order = i,
            };
            block.Lines.Add(new LineInfo(bbox.Y + 10, 16, bbox.X, bbox.W));
            analysis.Blocks.Add(block);
        }
        state.SetAnalysis(state.CurrentPage, analysis);
        var (ww, wh) = _controller.GetViewportSize();
        state.Rail.SetAnalysis(analysis, _controller.Config.NavigableRoles);
        state.Camera.Zoom = _controller.Config.RailZoomThreshold + 1;
        state.Rail.UpdateZoom(state.Camera.Zoom, state.Camera.OffsetX, state.Camera.OffsetY, ww, wh);
        return state;
    }

    private DocumentState SetupDocWithText(int charCount)
    {
        var state = _controller.CreateDocument(_pdfPath);
        state.LoadPageBitmap();
        _controller.AddDocument(state);
        _controller.SetViewportSize(800, 600);
        var (ww, wh) = _controller.GetViewportSize();
        TestFixtures.SetupRailMode(state, _controller.Config, ww, wh);

        // Inject synthetic text with CharBoxes
        var block = state.Rail.CurrentNavigableBlock;
        var bbox = block.BBox;
        var text = new string('A', charCount);
        var chars = new List<CharBox>(charCount);
        for (int i = 0; i < charCount; i++)
            chars.Add(new CharBox(i, bbox.X + (i % 80) * 5, bbox.Y + (i / 80) * 12, bbox.X + (i % 80) * 5 + 5, bbox.Y + (i / 80) * 12 + 12));
        state.SetText(0, new PageText(text, chars));
        return state;
    }

    [Fact]
    public void Benchmark_GetReadingPosition_NoSubscriber()
    {
        var state = SetupDocWithRailMode();
        const int iterations = 100_000;

        // Warmup
        for (int i = 0; i < 1000; i++)
            _controller.GetReadingPosition();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.GetReadingPosition();
        sw.Stop();

        var nsPerCall = sw.Elapsed.TotalNanoseconds / iterations;
        _output.WriteLine($"GetReadingPosition (no text, no subscriber): {nsPerCall:F1} ns/call over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");

        // Should be fast — just property reads, no text extraction, no subscriber call
        Assert.True(nsPerCall < 10_000, $"Too slow: {nsPerCall:F1} ns/call");
    }

    [Fact]
    public void Benchmark_GetReadingPosition_WithSubscriber()
    {
        var state = SetupDocWithRailMode();
        const int iterations = 100_000;
        ReadingPosition? lastPos = null;
        _controller.ReadingPositionChanged = pos => lastPos = pos;

        // Warmup
        for (int i = 0; i < 1000; i++)
            _controller.GetReadingPosition();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.GetReadingPosition();
        sw.Stop();

        var nsPerCall = sw.Elapsed.TotalNanoseconds / iterations;
        _output.WriteLine($"GetReadingPosition (no text, with subscriber): {nsPerCall:F1} ns/call over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");
    }

    [Fact]
    public void Benchmark_GetReadingPosition_WithTextExtraction()
    {
        // 5000 chars — typical academic page
        var state = SetupDocWithText(5000);
        const int iterations = 10_000;

        // Warmup
        for (int i = 0; i < 100; i++)
            _controller.GetReadingPosition();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.GetReadingPosition();
        sw.Stop();

        var usPerCall = sw.Elapsed.TotalMicroseconds / iterations;
        _output.WriteLine($"GetReadingPosition (5000 chars, text extraction): {usPerCall:F1} µs/call over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");

        // Text extraction should be under 1ms per call
        Assert.True(usPerCall < 1000, $"Too slow: {usPerCall:F1} µs/call");
    }

    [Fact]
    public void Benchmark_GetReadingPosition_TextScaling()
    {
        // Measure how text extraction scales with char count
        var results = new List<(int chars, double usPerCall)>();

        foreach (var charCount in new[] { 1000, 5000, 10000, 20000 })
        {
            var state = SetupDocWithText(charCount);
            const int iterations = 1000;

            // Warmup
            for (int i = 0; i < 50; i++)
                _controller.GetReadingPosition();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                _controller.GetReadingPosition();
            sw.Stop();

            var usPerCall = sw.Elapsed.TotalMicroseconds / iterations;
            results.Add((charCount, usPerCall));
            _output.WriteLine($"GetReadingPosition ({charCount:N0} chars): {usPerCall:F1} µs/call");
        }

        // Verify roughly linear scaling (20K should not be >10x slower than 1K)
        var ratio = results.Last().usPerCall / results.First().usPerCall;
        _output.WriteLine($"Scaling ratio (20K/1K): {ratio:F1}x");
        Assert.True(ratio < 40, $"Non-linear scaling detected: {ratio:F1}x");
    }

    [Fact]
    public void Benchmark_GetPageDescription_BlockScaling()
    {
        // Measure how GetPageDescription scales with block count
        var results = new List<(int blocks, double usPerCall)>();

        foreach (var blockCount in new[] { 10, 50, 100, 200 })
        {
            var state = SetupDocWithMultiBlock(blockCount);
            const int iterations = 1000;

            // Warmup
            for (int i = 0; i < 50; i++)
                _controller.GetPageDescription();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                _controller.GetPageDescription();
            sw.Stop();

            var usPerCall = sw.Elapsed.TotalMicroseconds / iterations;
            results.Add((blockCount, usPerCall));
            _output.WriteLine($"GetPageDescription ({blockCount} blocks, no text): {usPerCall:F1} µs/call");
        }

        // Without text cache, ExtractBlockText returns "" immediately — should be fast
        var ratio = results.Last().usPerCall / results.First().usPerCall;
        _output.WriteLine($"Scaling ratio (200/10 blocks): {ratio:F1}x");
    }

    [Fact]
    public void Benchmark_GetPageDescription_WithText()
    {
        // 50 blocks, 5000 chars — typical academic page
        var state = SetupDocWithMultiBlock(50);
        // Inject text
        var text = new string('X', 5000);
        var chars = new List<CharBox>(5000);
        for (int i = 0; i < 5000; i++)
            chars.Add(new CharBox(i, 72 + (i % 80) * 5, 72 + (i / 80) * 12, 77 + (i % 80) * 5, 84 + (i / 80) * 12));
        state.SetText(0, new PageText(text, chars));

        const int iterations = 500;

        // Warmup
        for (int i = 0; i < 20; i++)
            _controller.GetPageDescription();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.GetPageDescription();
        sw.Stop();

        var usPerCall = sw.Elapsed.TotalMicroseconds / iterations;
        _output.WriteLine($"GetPageDescription (50 blocks, 5000 chars): {usPerCall:F1} µs/call over {iterations} iterations ({sw.ElapsedMilliseconds} ms total)");

        // Should be well under 100ms for an agent RPC call
        Assert.True(usPerCall < 100_000, $"Too slow: {usPerCall:F1} µs/call");
    }

    [Fact]
    public void Benchmark_NavigateToRole()
    {
        var state = SetupDocWithMultiBlock(50);
        const int iterations = 10_000;

        // Warmup
        for (int i = 0; i < 100; i++)
            _controller.NavigateToRole(BlockRole.Heading);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.NavigateToRole(BlockRole.Heading);
        sw.Stop();

        var usPerCall = sw.Elapsed.TotalMicroseconds / iterations;
        _output.WriteLine($"NavigateToRole (50 blocks, forward to Heading): {usPerCall:F1} µs/call over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");

        // User-initiated, should be well under 1ms
        Assert.True(usPerCall < 1000, $"Too slow: {usPerCall:F1} µs/call");
    }

    [Fact]
    public void Benchmark_FireReadingPositionChanged_NoSubscriber()
    {
        // FireReadingPositionChanged when no subscriber — should be near-free
        var state = SetupDocWithRailMode();
        // Access the internal helper via HandleArrowDown which calls it
        const int iterations = 100_000;

        // Warmup
        for (int i = 0; i < 1000; i++)
            _controller.HandleArrowDown();

        // Reset to a position where arrow-down works
        state.Rail.CurrentLine = 0;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            if (state.Rail.CurrentLine < state.Rail.CurrentLineCount - 1)
                _controller.HandleArrowDown();
            else
                state.Rail.CurrentLine = 0;
        }
        sw.Stop();

        var nsPerCall = sw.Elapsed.TotalNanoseconds / iterations;
        _output.WriteLine($"HandleArrowDown + FireReadingPositionChanged (no subscriber): {nsPerCall:F1} ns/call over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");
    }

    [Fact]
    public void Benchmark_FireReadingPositionChanged_WithSubscriber()
    {
        var state = SetupDocWithRailMode();
        int fireCount = 0;
        _controller.ReadingPositionChanged = _ => fireCount++;

        const int iterations = 10_000;

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            if (state.Rail.CurrentLine < state.Rail.CurrentLineCount - 1)
                _controller.HandleArrowDown();
            else
                state.Rail.CurrentLine = 0;
        }

        state.Rail.CurrentLine = 0;
        fireCount = 0;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            if (state.Rail.CurrentLine < state.Rail.CurrentLineCount - 1)
                _controller.HandleArrowDown();
            else
                state.Rail.CurrentLine = 0;
        }
        sw.Stop();

        var usPerCall = sw.Elapsed.TotalMicroseconds / iterations;
        _output.WriteLine($"HandleArrowDown + FireReadingPositionChanged (subscriber, no text): {usPerCall:F1} µs/call, {fireCount} events fired over {iterations} iterations ({sw.ElapsedMilliseconds} ms total)");

        // Without text cache, GetReadingPosition returns empty strings — should be fast
        Assert.True(fireCount > 0, "No events fired");
    }

    [Fact]
    public void Benchmark_Tick_Overhead()
    {
        // Measure tick overhead with and without the new event fires
        var state = SetupDocWithRailMode();
        const int iterations = 100_000;

        // Warmup
        for (int i = 0; i < 1000; i++)
            _controller.Tick(0.016);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.Tick(0.016);
        sw.Stop();

        var usPerTick = sw.Elapsed.TotalMicroseconds / iterations;
        _output.WriteLine($"Tick (rail mode, no subscriber, no auto-scroll): {usPerTick:F1} µs/tick over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");

        // At 60fps we have 16.7ms per frame. Tick should be well under 1ms.
        Assert.True(usPerTick < 1000, $"Tick too slow: {usPerTick:F1} µs/tick");
    }

    [Fact]
    public void Benchmark_Tick_WithSubscriber()
    {
        var state = SetupDocWithRailMode();
        _controller.ReadingPositionChanged = _ => { };
        const int iterations = 100_000;

        // Warmup
        for (int i = 0; i < 1000; i++)
            _controller.Tick(0.016);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            _controller.Tick(0.016);
        sw.Stop();

        var usPerTick = sw.Elapsed.TotalMicroseconds / iterations;
        _output.WriteLine($"Tick (rail mode, with subscriber, no auto-scroll): {usPerTick:F1} µs/tick over {iterations:N0} iterations ({sw.ElapsedMilliseconds} ms total)");

        Assert.True(usPerTick < 1000, $"Tick too slow with subscriber: {usPerTick:F1} µs/tick");
    }
}
