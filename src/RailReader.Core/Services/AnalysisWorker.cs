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
    int ViewRotation = 0,
    // The page's skew in radians, when a previous OCR pass already measured it and the caller is
    // handing that pass's char boxes back in CharBoxes (see AnalysisResult.OcrSkew).
    // 0 means "not measured", which is also every request for a page with a real text layer.
    // Carrying it is what lets a re-analysis keep the deskew correction without re-running
    // recognition — the expensive half of the OCR path, and the whole of issue #100's cost.
    float OcrSkew = 0f);

public sealed record AnalysisResult(
    string FilePath, int Page, AnalysisParams Params, PageAnalysis Analysis,
    int ViewRotation = 0,
    // Text recovered by OCR for a page that had no text layer, in page-point space, or null
    // (page had a text layer, OCR is off, or it ran in Lines mode). The consumer caches it as
    // the page's text so search/export/VLM see a scanned page the same way as a digital one.
    PageText? OcrText = null,
    // The page skew OCR measured, in radians, BEFORE the DeskewOcrLines gate — the raw
    // measurement, so a consumer that caches it alongside OcrText can hand it back on a later
    // request and have the shear re-applied (or dropped) under whatever the setting is then.
    // 0 when OCR did not run or found no confident estimate.
    float OcrSkew = 0f);

/// <summary>
/// Runs layout analysis — and, for a page with no text layer, the OCR that has to precede it —
/// off the UI thread, taking requests on one channel and publishing results on another.
///
/// <para>
/// <b>Two stages, two threads.</b> OCR and layout inference run on separate threads connected by
/// a channel, because they are independent for different pages: recognising page N does not have
/// to block inference for page M. A single loop running OCR inline meant one scanned page under a
/// heavy model set held the only worker for its entire recognition — measured at over two minutes
/// with PP-OCRv6 Medium — during which layout analysis stopped for every open document
/// (issue #100). <see cref="Submit"/> routes each request to the stage it actually needs, so a
/// page that comes with its own char boxes never queues behind one that does not.
/// </para>
/// <para>
/// The stages have no ordering guarantee between them: a layout-only request submitted second can
/// complete first. Nothing downstream depends on order — every result carries its own
/// (file, page, params) key, and the in-flight set admits one request per key at a time.
/// </para>
/// </summary>
public sealed class AnalysisWorker : IDisposable
{
    /// <summary>
    /// A request that has cleared the OCR stage, carrying whatever that stage recovered. A
    /// request needing no OCR is written straight here by <see cref="Submit"/>.
    /// </summary>
    private readonly record struct LayoutJob(
        AnalysisRequest Request, PageText? OcrText, List<BBox>? OcrLines, float OcrSkew);

    private readonly Channel<AnalysisRequest> _ocrChannel;
    private readonly Channel<LayoutJob> _layoutChannel;
    private readonly Channel<AnalysisResult> _resultChannel;
    // UI-thread-only: accessed exclusively from Submit/Poll/IsInFlight/IsIdle on the UI thread.
    // Keyed by params too (railreader2#180 #3) so the same page can be in flight under two
    // different post-processing variants (e.g. cell-nav on for one view, off for another).
    private readonly HashSet<(string FilePath, int Page, AnalysisParams Params)> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _layoutTask;
    // Null when no OCR service was supplied: nothing is ever routed to the OCR stage, so
    // starting a thread to drain it would be a thread parked forever.
    private readonly Task? _ocrTask;
    private readonly ILogger _logger;
    private readonly IThreadMarshaller _marshaller;
    private readonly IReadingOrderResolver _readingOrder;
    private readonly Func<IOcrService>? _ocrServiceFactory;
    private readonly LineDetectionTuning _tuning;

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

    // Same shape and rationale as _ocrMode: read on the worker thread, written on the UI thread.
    private int _deskewEnabled;

    /// <summary>
    /// Whether to correct page skew when grouping OCR output into lines. Applies only to pages
    /// that went through OCR — a page with its own text layer was never skewed to begin with,
    /// and with <see cref="OcrMode.Off"/> there is nothing to estimate an angle from.
    /// </summary>
    public bool DeskewEnabled
    {
        get => Volatile.Read(ref _deskewEnabled) != 0;
        set => Volatile.Write(ref _deskewEnabled, value ? 1 : 0);
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
    /// <param name="lineTuning">
    /// Optional override of the raster thresholds used by post-processing (see
    /// <see cref="LineDetectionTuning"/>). Detection thresholds are the analyzer's business — set
    /// those on the instance <paramref name="analyzerFactory"/> builds.
    /// </param>
    public AnalysisWorker(
        LayoutModelCapabilities capabilities,
        Func<ILayoutAnalyzer> analyzerFactory,
        IThreadMarshaller marshaller,
        IReadingOrderResolver? readingOrderResolver = null,
        ILogger? logger = null,
        Func<IOcrService>? ocrServiceFactory = null,
        OcrMode ocrMode = OcrMode.Off,
        LineDetectionTuning? lineTuning = null)
    {
        Capabilities = capabilities;
        _tuning = lineTuning ?? LineDetectionTuning.Default;
        _ocrMode = (int)ocrMode;
        _ocrServiceFactory = ocrServiceFactory;
        _readingOrder = readingOrderResolver ?? (capabilities.ProvidesReadingOrder
            ? new ModelOrderResolver()
            : new XYCutPlusPlusResolver());
        _logger = logger ?? NullLogger.Instance;
        _marshaller = marshaller;
        _ocrChannel = Channel.CreateUnbounded<AnalysisRequest>();
        _layoutChannel = Channel.CreateUnbounded<LayoutJob>();
        _resultChannel = Channel.CreateUnbounded<AnalysisResult>();

        _layoutTask = Observe(Task.Run(() => LayoutLoop(analyzerFactory, _cts.Token)), "layout");
        if (_ocrServiceFactory is not null)
            _ocrTask = Observe(Task.Run(() => OcrLoop(_cts.Token)), "OCR");
    }

    /// <summary>Observes a stage task so a fault surfaces in the log instead of as an
    /// UnobservedTaskException at some unrelated later GC.</summary>
    private Task Observe(Task task, string stage)
    {
        task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.Error($"[Worker] {stage} task faulted", t.Exception?.InnerException);
        }, TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    /// <summary>Points-per-pixel for a request's pixmap — the factors that map detections and
    /// OCR geometry back into page space.</summary>
    private static (float X, float Y) MapScale(AnalysisRequest request) => (
        request.PxW > 0 ? (float)(request.PageW / request.PxW) : 1f,
        request.PxH > 0 ? (float)(request.PageH / request.PxH) : 1f);

    /// <summary>
    /// The OCR stage. Recovers what a missing text layer would have given us and hands the
    /// request on to the layout stage, which is free to be working on a different page
    /// throughout — the whole point of the split (issue #100).
    /// </summary>
    private async Task OcrLoop(CancellationToken ct)
    {
        // OCR is optional and secondary: a failure to load it must leave layout analysis working,
        // so the error is recorded and the stage keeps running as a pass-through. (Requests are
        // routed here on the presence of a *factory*, which is all Submit can see.)
        IOcrService? ocr = null;
        try
        {
            ocr = _ocrServiceFactory!();
            _logger.Debug("[Worker] OCR service ready");
        }
        catch (Exception ex)
        {
            OcrStartupError = ex.Message;
            _logger.Error("[Worker] OCR service failed to load; continuing without OCR", ex);
        }

        using (ocr)
        {
            await foreach (var request in _ocrChannel.Reader.ReadAllAsync(ct))
            {
                LayoutJob job;
                try
                {
                    var (sx, sy) = MapScale(request);
                    var (ocrText, ocrLines, ocrSkew) = RunOcr(ocr, request, request.CharBoxes, sx, sy, ct);
                    // A pass that measured nothing leaves the request's own carried-forward angle
                    // standing, so this is the effective skew from here on either way.
                    job = new LayoutJob(request, ocrText, ocrLines,
                        ocrLines is null ? request.OcrSkew : ocrSkew);
                }
                catch (OperationCanceledException) { throw; } // Dispose path — let the loop end
                catch (Exception ex)
                {
                    // RunOcr swallows engine failures itself, so reaching here means something
                    // outside it broke. Pass the request through unrecovered rather than dropping
                    // it: the page still deserves its layout analysis.
                    _logger.Error($"[Worker] OCR stage failed for page {request.Page}; analysing without it", ex);
                    job = new LayoutJob(request, null, null, request.OcrSkew);
                }

                if (!_layoutChannel.Writer.TryWrite(job))
                {
                    // The layout stage is gone (fatal analyzer failure, or disposal): release the
                    // key so IsInFlight/IsIdle don't stay stuck on a request nobody will finish.
                    var key = (request.FilePath, request.Page, request.Params);
                    _marshaller.Post(() => _inFlight.Remove(key));
                }
            }
        }
    }

    /// <summary>
    /// The layout stage: inference, reading order, and block post-processing. Every request
    /// reaches it, either straight from <see cref="Submit"/> or via the OCR stage.
    /// </summary>
    private async Task LayoutLoop(Func<ILayoutAnalyzer> analyzerFactory, CancellationToken ct)
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
            // Nothing downstream of here can run, so close both intake channels too — otherwise
            // the OCR stage would go on paying for recognition whose results have no consumer.
            _ocrChannel.Writer.TryComplete();
            _layoutChannel.Writer.TryComplete();
            _resultChannel.Writer.TryComplete();
            return;
        }

        using (analyzer)
        {
            await foreach (var job in _layoutChannel.Reader.ReadAllAsync(ct))
            {
                var request = job.Request;

                // A per-request failure (ORT exception on a bad raster, geometry edge case in the
                // resolver, …) must not fault the loop: that would silently kill analysis for the
                // rest of the session AND strand the request's _inFlight key, so IsInFlight stays
                // true forever (blocking resubmission for its page) and IsIdle stays false
                // (blocking lookahead/background analysis document-wide). Log, release the key on
                // the UI thread (its owner), and keep serving requests.
                try
                {
                    var (mapScaleX, mapScaleY) = MapScale(request);

                    // OCR, when it ran, stands in for the missing text layer so the rest of the
                    // pipeline — layout analysis, reading order, line and cell detection — takes
                    // the same path a born-digital page takes.
                    var charBoxes = job.OcrText?.DedupedCharBoxes ?? request.CharBoxes;

                    // The shear term line grouping reasons with. Tangent rather than the angle
                    // because every consumer wants exactly that, and because a bare float that
                    // is 0 on all but scanned skewed pages makes the "no angle ⇒ the code that
                    // ran before this feature existed" invariant visible at each call site. The
                    // DeskewEnabled gate lives here rather than at the measurement, so a page
                    // whose angle was measured by an earlier pass and handed back on the request
                    // honours the setting as it is *now*.
                    float skewTan = !DeskewEnabled || job.OcrSkew == 0f ? 0f : MathF.Tan(job.OcrSkew);

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
                        request.Params.CellNavigation, job.OcrLines, request.Rulings, _tuning, skewTan);

                    _logger.Debug($"[Worker] Page {request.Page}: {analysis.Blocks.Count} blocks detected");

                    await _resultChannel.Writer.WriteAsync(
                        new AnalysisResult(request.FilePath, request.Page, request.Params, analysis,
                            request.ViewRotation, job.OcrText, job.OcrSkew), ct);
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
    /// <para>
    /// The skew is returned <b>raw</b> — the <see cref="DeskewEnabled"/> gate is applied by the
    /// layout stage, so the measurement can be published on the result, cached by the consumer,
    /// and re-gated on a later request without paying for recognition twice.
    /// </para>
    /// </summary>
    private (PageText? Text, List<BBox>? Lines, float Skew) RunOcr(
        IOcrService? ocr, AnalysisRequest request, IReadOnlyList<CharBox>? charBoxes,
        float mapScaleX, float mapScaleY, CancellationToken ct)
    {
        var mode = OcrMode;
        if (ocr is null || mode == OcrMode.Off || charBoxes is { Count: > 0 })
            return (null, null, 0f);

        try
        {
            var page = ocr.Recognize(request.RgbBytes, request.PxW, request.PxH, mode, ct);
            if (page.Lines.Count == 0) return (null, null, 0f);

            var (text, lines, skew) = OcrPageMapper.ToPageSpace(page, mapScaleX, mapScaleY);
            // The skew is logged because it is the only diagnostic a field report will carry:
            // line grouping consumes it and nothing downstream of this worker ever sees it.
            _logger.Debug(
                $"[Worker] Page {request.Page}: OCR ({mode}) found {lines.Count} lines, " +
                $"{text?.CharBoxes.Count ?? 0} chars, skew {skew * 180f / MathF.PI:F2}°");
            return (text, lines, skew);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"[Worker] OCR failed for page {request.Page}; continuing without it", ex);
            return (null, null, 0f);
        }
    }

    /// <summary>
    /// Submit an analysis request. Must be called on the UI thread.
    ///
    /// <para>
    /// Routes to the stage the request actually needs: only a page that arrives with no char
    /// boxes, with an OCR engine wired and the mode on, goes through the OCR stage. Everything
    /// else — a page with a text layer, a page whose OCR text the caller is handing back — goes
    /// straight to layout inference and so never waits behind a recognition pass (issue #100).
    /// </para>
    /// </summary>
    public bool Submit(AnalysisRequest request)
    {
        _marshaller.AssertUIThread();
        var key = (request.FilePath, request.Page, request.Params);
        if (!_inFlight.Add(key))
            return false;

        // Deliberately keyed on the *factory*, not on whether the engine loaded: the load happens
        // on the OCR stage's own thread and may not have finished (or may have failed) yet. A
        // request routed there with no engine is passed straight through, which costs one hop.
        bool needsOcr = _ocrTask is not null
            && OcrMode != OcrMode.Off
            && request.CharBoxes is not { Count: > 0 };

        bool written = needsOcr
            ? _ocrChannel.Writer.TryWrite(request)
            : _layoutChannel.Writer.TryWrite(new LayoutJob(request, null, null, request.OcrSkew));

        if (!written)
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
        _ocrChannel.Writer.TryComplete();
        _layoutChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}
