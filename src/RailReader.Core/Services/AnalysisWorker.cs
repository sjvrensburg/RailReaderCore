using System.Threading.Channels;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed record AnalysisRequest(
    string FilePath, int Page, byte[] RgbBytes,
    int PxW, int PxH, double PageW, double PageH,
    IReadOnlyList<CharBox>? CharBoxes = null,
    bool TableRowReading = true);

public sealed record AnalysisResult(
    string FilePath, int Page, PageAnalysis Analysis);

public sealed class AnalysisWorker : IDisposable
{
    private readonly Channel<AnalysisRequest> _requestChannel;
    private readonly Channel<AnalysisResult> _resultChannel;
    // UI-thread-only: accessed exclusively from Submit/Poll/IsInFlight/IsIdle on the UI thread.
    private readonly HashSet<(string FilePath, int Page)> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly ILogger _logger;
    private readonly IThreadMarshaller _marshaller;
    private readonly IReadingOrderResolver _readingOrder;

    /// <summary>Static capabilities of the analyzer running in this worker. Available before the analyzer finishes loading.</summary>
    public LayoutModelCapabilities Capabilities { get; }

    /// <summary>Page-rasterisation size the analyzer expects. Convenience alias for <c>Capabilities.InputSize</c>.</summary>
    public int InputSize => Capabilities.InputSize;

    /// <summary>Set to true once the worker loop has initialized the analyzer.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Set if the worker loop failed to start (e.g. model load failure).</summary>
    public string? StartupError { get; private set; }

    /// <summary>
    /// Create a worker. Pass the analyzer's <see cref="LayoutModelCapabilities"/>
    /// eagerly (these must match what <paramref name="analyzerFactory"/> will
    /// later construct) so consumers can read <see cref="InputSize"/> immediately
    /// without waiting for the model to load.
    ///
    /// <paramref name="readingOrderResolver"/> is optional: if null, the worker
    /// picks <see cref="ModelOrderResolver"/> when the model provides reading
    /// order, otherwise <see cref="XYCutPlusPlusResolver"/>.
    /// </summary>
    public AnalysisWorker(
        LayoutModelCapabilities capabilities,
        Func<ILayoutAnalyzer> analyzerFactory,
        IThreadMarshaller marshaller,
        IReadingOrderResolver? readingOrderResolver = null,
        ILogger? logger = null)
    {
        Capabilities = capabilities;
        _readingOrder = readingOrderResolver ?? (capabilities.ProvidesReadingOrder
            ? new ModelOrderResolver()
            : new XYCutPlusPlusResolver());
        _logger = logger ?? NullLogger.Instance;
        _marshaller = marshaller;
        _requestChannel = Channel.CreateUnbounded<AnalysisRequest>();
        _resultChannel = Channel.CreateUnbounded<AnalysisResult>();

        _workerTask = Task.Run(() => WorkerLoop(analyzerFactory, _cts.Token));
        // Observe the task to prevent UnobservedTaskException
        _workerTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.Error("[Worker] Task faulted", t.Exception?.InnerException);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task WorkerLoop(Func<ILayoutAnalyzer> analyzerFactory, CancellationToken ct)
    {
        ILayoutAnalyzer analyzer;
        try
        {
            analyzer = analyzerFactory();
            IsReady = true;
            _logger.Debug("[Worker] Layout analyzer ready, waiting for requests...");
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
            _logger.Error("[Worker] FATAL: Failed to create layout analyzer", ex);
            _resultChannel.Writer.TryComplete();
            return;
        }

        using (analyzer)
        {
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(ct))
            {
                _logger.Debug($"[Worker] Running analyzer for {Path.GetFileName(request.FilePath)} page {request.Page}...");
                var analysis = analyzer.RunAnalysis(
                    request.RgbBytes, request.PxW, request.PxH, request.PageW, request.PageH,
                    request.CharBoxes, ct);

                // Pipeline: assign reading order → trim overlaps + detect lines.
                _readingOrder.AssignOrder(analysis.Blocks, analysis.PageWidth, analysis.PageHeight,
                    request.CharBoxes);

                float mapScaleX = request.PxW > 0 ? (float)(request.PageW / request.PxW) : 1f;
                float mapScaleY = request.PxH > 0 ? (float)(request.PageH / request.PxH) : 1f;
                BlockPostProcessor.PostProcess(
                    analysis.Blocks, request.RgbBytes, request.PxW, request.PxH,
                    mapScaleX, mapScaleY, request.CharBoxes, request.TableRowReading);

                _logger.Debug($"[Worker] Page {request.Page}: {analysis.Blocks.Count} blocks detected");

                await _resultChannel.Writer.WriteAsync(
                    new AnalysisResult(request.FilePath, request.Page, analysis), ct);
            }
        }
    }

    /// <summary>Submit an analysis request. Must be called on the UI thread.</summary>
    public bool Submit(AnalysisRequest request)
    {
        _marshaller.AssertUIThread();
        var key = (request.FilePath, request.Page);
        if (!_inFlight.Add(key))
            return false;

        if (!_requestChannel.Writer.TryWrite(request))
        {
            _inFlight.Remove(key);
            return false;
        }
        return true;
    }

    /// <summary>Poll for completed results. Must be called on the UI thread.</summary>
    public AnalysisResult? Poll()
    {
        _marshaller.AssertUIThread();
        if (!_resultChannel.Reader.TryRead(out var result))
            return null;

        _inFlight.Remove((result.FilePath, result.Page));
        return result;
    }

    /// <summary>Check if a page is currently being analyzed. Must be called on the UI thread.</summary>
    public bool IsInFlight(string filePath, int page)
    {
        _marshaller.AssertUIThread();
        return _inFlight.Contains((filePath, page));
    }

    /// <summary>Check if no analysis requests are in flight. Must be called on the UI thread.</summary>
    public bool IsIdle
    {
        get
        {
            _marshaller.AssertUIThread();
            return _inFlight.Count == 0;
        }
    }

    public void Dispose()
    {
        _requestChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}
