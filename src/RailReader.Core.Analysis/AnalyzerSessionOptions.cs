using Microsoft.ML.OnnxRuntime;

namespace RailReader.Core.Analysis;

/// <summary>
/// Builds the <see cref="SessionOptions"/> shared by all three layout
/// analyzers in this assembly, applying conservative CPU defaults.
///
/// <para>
/// <b>Why defaults matter.</b> A bare <c>new SessionOptions()</c> lets ONNX
/// Runtime default <c>IntraOpNumThreads</c> to the physical-core count and
/// leaves the CPU memory arena enabled. On a typical desktop that means a
/// single page inference fans out across every core (~13× CPU on a 14-core
/// box) and the arena reserves a power-of-two block sized to the largest
/// activation tensor that it never returns to the OS — a sticky native-RSS
/// floor the .NET GC can't see. Those two defaults were the dominant cause of
/// the RailReader2 AppImage's CPU/RAM appetite. Crucially they are <i>not</i>
/// tunable from outside the process: the managed <c>Microsoft.ML.OnnxRuntime</c>
/// build ignores <c>OMP_NUM_THREADS</c>/<c>ORT_INTRA_OP_NUM_THREADS</c>, and
/// ORT sets its own per-thread affinity so <c>taskset</c> is partly defeated.
/// The only effective lever is <see cref="SessionOptions"/> itself, so we set
/// sane defaults here rather than leaving each consumer to remember.
/// </para>
///
/// <para>
/// The per-analyzer <c>ConfigureSession</c> hook still runs <i>after</i> these
/// defaults, so a consumer that wants the old all-cores behaviour (or a higher
/// cap) can override any of them.
/// </para>
/// </summary>
internal static class AnalyzerSessionOptions
{
    /// <summary>
    /// Intra-op thread cap. Uses up to 4 cores — enough to keep per-page
    /// latency reasonable without pinning the whole machine. Clamped to the
    /// available processor count on smaller boxes.
    /// </summary>
    internal static readonly int IntraOpThreads = Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// Creates a <see cref="SessionOptions"/> with the shared analyzer defaults
    /// applied, then invokes <paramref name="configure"/> (the analyzer's
    /// <c>ConfigureSession</c> hook) so callers can override.
    /// </summary>
    internal static SessionOptions Create(Action<SessionOptions>? configure)
    {
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // Suppress noisy NCHWc Conv kernel warnings while preserving genuine errors.
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;

        // Conservative CPU defaults — see the type doc for the rationale.
        opts.IntraOpNumThreads = IntraOpThreads;
        opts.InterOpNumThreads = 1;
        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        // Drop the retained arena: the analyzers infer intermittently, so the
        // small per-inference alloc cost is worth a non-sticky native RSS.
        opts.EnableCpuMemArena = false;

        // Hook runs last so consumers can override any default above.
        configure?.Invoke(opts);
        return opts;
    }
}
