using Microsoft.ML.OnnxRuntime;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// Builds the <see cref="SessionOptions"/> used for the OCR models, applying the same
/// conservative CPU defaults as the layout analyzers.
///
/// <para>
/// RapidOcrNet's own <c>RapidOcr.GetDefaultSessionOptions</c> passes <c>numThread = 0</c>,
/// which leaves ONNX Runtime to default <c>IntraOpNumThreads</c> to the core count and to
/// keep the CPU memory arena enabled. That pair — inference fanning out across every core,
/// plus an arena that never returns its largest activation block to the OS — was the
/// dominant cause of the RailReader2 AppImage's CPU and RAM appetite, and it is not
/// tunable from outside the process (the managed ORT build ignores
/// <c>OMP_NUM_THREADS</c>/<c>ORT_INTRA_OP_NUM_THREADS</c>, and ORT sets its own thread
/// affinity so <c>taskset</c> is only partly effective). Three OCR sessions would triple
/// the exposure, so the defaults are set here rather than inherited.
/// </para>
/// </summary>
internal static class OcrSessionOptions
{
    /// <summary>
    /// Intra-op thread cap, clamped to the machine. Matches the layout analyzers so a page
    /// that runs both does not oversubscribe.
    /// </summary>
    internal static readonly int IntraOpThreads = Math.Clamp(Environment.ProcessorCount, 1, 4);

    internal static SessionOptions Create(Action<SessionOptions>? configure)
    {
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        opts.IntraOpNumThreads = IntraOpThreads;
        opts.InterOpNumThreads = 1;
        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        // OCR runs intermittently (only on pages with no text layer), so a retained arena
        // would be a sticky native-RSS floor for a rarely-used feature.
        opts.EnableCpuMemArena = false;

        // Hook runs last so consumers can override any default above.
        configure?.Invoke(opts);
        return opts;
    }
}
