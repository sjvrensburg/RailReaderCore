using System.Threading.Channels;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed record AnalysisRequest(
    string FilePath, int Page, byte[] RgbBytes,
    int PxW, int PxH, double PageW, double PageH,
    IReadOnlyList<CharBox>? CharBoxes,
    AnalysisParams Params,
    // The page's vector ruling lines, when the PDF backend can read them (see
    // IPdfRulingService). Null for a backend that cannot, or a page with no vector content.
    PageRulings? Rulings = null,
    // The document ViewRotation the pixmap was rasterised under. Carried through to the result so
    // the consumer can reject a result whose geometry is in a display frame the document has since
    // rotated away from (the caches were cleared; old-frame blocks must not repopulate them).
    int ViewRotation = 0);

public sealed record AnalysisResult(
    string FilePath, int Page, AnalysisParams Params, PageAnalysis Analysis,
    int ViewRotation = 0,
    // Text recovered by OCR for a page that had no text layer, in page-point space, or null
    // (page had a text layer, OCR is off, or it ran in Lines mode). The consumer caches it as
    // the page's text so search/export/VLM see a scanned page the same way as a digital one.
    PageText? OcrText = null);

public sealed class AnalysisWorker : IDisposable
{
    private readonly Channel<AnalysisRequest> _requestChannel;
    private readonly Channel<AnalysisResult> _resultChannel;
    // UI-thread-only: accessed exclusively from Submit/Poll/IsInFlight/IsIdle on the UI thread.
    // Keyed by params too (railreader2#180 #3) so the same page can be in flight under two
    // different post-processing variants (e.g. cell-nav on for one view, off for another).
    private readonly HashSet<(string FilePath, int Page, AnalysisParams Params)> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly ILogger _logger;
    private readonly IThreadMarshaller _marshaller;
    private readonly IReadingOrderResolver _readingOrder;
    private readonly Func<IOcrService>? _ocrServiceFactory;
    private readonly LayoutTuning? _tuning;

    /// <summary>Static capabilities of the analyzer running in this worker. Available before the analyzer finishes loading.</summary>
    public LayoutModelCapabilities Capabilities { get; }

    /// <summary>Page-rasterisation size the analyzer expects. Convenience alias for <c>Capabilities.InputSize</c>.</summary>
    public int InputSize => Capabilities.InputSize;

    /// <summary>Set to true once the worker loop has initialized the analyzer.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Set if the worker loop failed to start (e.g. model load failure).</summary>
    public string? StartupError { get; private set; }

    // Read by the worker thread, written by the UI thread (OcrMode setter) — int-backed so
    // the access is atomic without a lock.
    private int _ocrMode;

    /// <summary>
    /// How much OCR to run on pages that arrive with no char boxes. Defaults to the value
    /// passed to the constructor and may be changed at any time (the next request picks it
    /// up); has no effect when no OCR service was supplied. Pages that already have a text
    /// layer never invoke OCR regardless of this setting.
    /// </summary>
    public OcrMode OcrMode
    {
        get => (OcrMode)Volatile.Read(ref _ocrMode);
        set => Volatile.Write(ref _ocrMode, (int)value);
    }

    /// <summary>Set if the OCR service failed to load; layout analysis continues without it.</summary>
    public string? OcrStartupError { get; private set; }

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
    /// <param name="ocrServiceFactory">
    /// Optional OCR engine for pages with no text layer, constructed on the worker thread
    /// alongside the analyzer (so a missing or broken model surfaces as
    /// <see cref="OcrStartupError"/> rather than a startup crash). Used only when
    /// <paramref name="ocrMode"/> is not <see cref="Services.OcrMode.Off"/>.
    /// </param>
    /// <param name="ocrMode">Initial <see cref="OcrMode"/>; changeable later via the property.</param>
    /// <param name="tuning">
    /// Optional override of the raster thresholds used by post-processing (see
    /// <see cref="LayoutTuning"/>). Detection thresholds are the analyzer's business — set
    /// those on the instance <paramref name="analyzerFactory"/> builds. Null keeps the
    /// <see cref="LayoutConstants"/> defaults.
    /// </param>
    public AnalysisWorker(
        LayoutModelCapabilities capabilities,
        Func<ILayoutAnalyzer> analyzerFactory,
        IThreadMarshaller marshaller,
        IReadingOrderResolver? readingOrderResolver = null,
        ILogger? logger = null,
        Func<IOcrService>? ocrServiceFactory = null,
        OcrMode ocrMode = OcrMode.Off,
        LayoutTuning? tuning = null)
    {
        Capabilities = capabilities;
        _tuning = tuning;
        _ocrMode = (int)ocrMode;
        _ocrServiceFactory = ocrServiceFactory;
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

        // OCR is optional and secondary: a failure to load it must leave layout analysis
        // working, so it is constructed separately and its error recorded, not thrown.
        IOcrService? ocr = null;
        if (_ocrServiceFactory is not null)
        {
            try
            {
                ocr = _ocrServiceFactory();
                _logger.Debug("[Worker] OCR service ready");
            }
            catch (Exception ex)
            {
                OcrStartupError = ex.Message;
                _logger.Error("[Worker] OCR service failed to load; continuing without OCR", ex);
            }
        }

        using (analyzer)
        using (ocr)
        {
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(ct))
            {
                // A per-request failure (ORT exception on a bad raster, geometry edge case in the
                // resolver, …) must not fault the loop: that would silently kill analysis for the
                // rest of the session AND strand the request's _inFlight key, so IsInFlight stays
                // true forever (blocking resubmission for its page) and IsIdle stays false
                // (blocking lookahead/background analysis document-wide). Log, release the key on
                // the UI thread (its owner), and keep serving requests.
                try
                {
                    float mapScaleX = request.PxW > 0 ? (float)(request.PageW / request.PxW) : 1f;
                    float mapScaleY = request.PxH > 0 ? (float)(request.PageH / request.PxH) : 1f;

                    // A page with no char boxes is a scan (or a text-layer-less export). When OCR
                    // is available, recover what the text layer would have given us *before* the
                    // pipeline runs, so the rest of it — layout analysis, reading order, line and
                    // cell detection — takes the same path a born-digital page takes.
                    var charBoxes = request.CharBoxes;
                    var (ocrText, ocrLines) = RunOcr(ocr, request, charBoxes, mapScaleX, mapScaleY, ct);
                    if (ocrText is not null) charBoxes = ocrText.DedupedCharBoxes;

                    _logger.Debug($"[Worker] Running analyzer for {Path.GetFileName(request.FilePath)} page {request.Page}...");
                    var analysis = analyzer.RunAnalysis(
                        request.RgbBytes, request.PxW, request.PxH, request.PageW, request.PageH,
                        charBoxes, ct);

                    // Pipeline: assign reading order → trim overlaps + detect lines.
                    _readingOrder.AssignOrder(analysis.Blocks, analysis.PageWidth, analysis.PageHeight,
                        charBoxes);

                    BlockPostProcessor.PostProcess(
                        analysis.Blocks, request.RgbBytes, request.PxW, request.PxH,
                        mapScaleX, mapScaleY, charBoxes, request.Params.TableRowReading,
                        request.Params.CellNavigation, ocrLines, request.Rulings, _tuning);

                    _logger.Debug($"[Worker] Page {request.Page}: {analysis.Blocks.Count} blocks detected");

                    await _resultChannel.Writer.WriteAsync(
                        new AnalysisResult(request.FilePath, request.Page, request.Params, analysis,
                            request.ViewRotation, ocrText), ct);
                }
                catch (OperationCanceledException) { throw; } // Dispose path — let the loop end
                catch (Exception ex)
                {
                    _logger.Error($"[Worker] Analysis failed for {Path.GetFileName(request.FilePath)} page {request.Page}; worker continues", ex);
                    var key = (request.FilePath, request.Page, request.Params);
                    _marshaller.Post(() => _inFlight.Remove(key));
                }
            }
        }
    }

    /// <summary>
    /// Runs OCR for a page that arrived without a text layer, mapping the result into
    /// page-point space. Returns <c>(null, null)</c> whenever OCR does not apply: no engine,
    /// mode <see cref="OcrMode.Off"/>, the page already has char boxes, or nothing was found.
    ///
    /// <para>
    /// OCR is best-effort — a failure here (bad raster, model quirk) must not cost the page
    /// its layout analysis, so it is caught and logged and the page proceeds down the
    /// no-text-layer path it would have taken anyway. Cancellation is rethrown so the
    /// dispose path still ends the loop.
    /// </para>
    /// </summary>
    private (PageText? Text, List<BBox>? Lines) RunOcr(
        IOcrService? ocr, AnalysisRequest request, IReadOnlyList<CharBox>? charBoxes,
        float mapScaleX, float mapScaleY, CancellationToken ct)
    {
        var mode = OcrMode;
        if (ocr is null || mode == OcrMode.Off || charBoxes is { Count: > 0 })
            return (null, null);

        try
        {
            var page = ocr.Recognize(request.RgbBytes, request.PxW, request.PxH, mode, ct);
            if (page.Lines.Count == 0) return (null, null);

            var (text, lines) = OcrPageMapper.ToPageSpace(page, mapScaleX, mapScaleY);
            _logger.Debug(
                $"[Worker] Page {request.Page}: OCR ({mode}) found {lines.Count} lines, {text?.CharBoxes.Count ?? 0} chars");
            return (text, lines);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"[Worker] OCR failed for page {request.Page}; continuing without it", ex);
            return (null, null);
        }
    }

    /// <summary>Submit an analysis request. Must be called on the UI thread.</summary>
    public bool Submit(AnalysisRequest request)
    {
        _marshaller.AssertUIThread();
        var key = (request.FilePath, request.Page, request.Params);
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

        _inFlight.Remove((result.FilePath, result.Page, result.Params));
        return result;
    }

    /// <summary>Check if a page is currently being analyzed under the given post-processing params.
    /// Must be called on the UI thread.</summary>
    public bool IsInFlight(string filePath, int page, AnalysisParams pars)
    {
        _marshaller.AssertUIThread();
        return _inFlight.Contains((filePath, page, pars));
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
