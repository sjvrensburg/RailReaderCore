using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis.WebGpu;

/// <summary>
/// Opt-in GPU acceleration for the layout analyzers in <c>RailReader.Core.Analysis</c>,
/// via ONNX Runtime's native WebGPU plugin execution provider (Dawn dispatching to
/// D3D12/Vulkan on Windows, Vulkan on Linux, Metal on macOS — no vendor SDK required).
///
/// <para>
/// <b>✅ FIXED (2026-08-28) — GPU inference now uses the plain FP32 models, not FP16
/// exports, for both Heron and PP-DocLayoutV3.</b> <c>LayoutModelRegistry.Resolve</c> no
/// longer routes <see cref="AcceleratorPreference.Gpu"/> requests to
/// <c>HeronFp16</c>/<c>PPDocLayoutV3Fp16</c> — both are kept in the registry for manual
/// use but are no longer the GPU default. See memory: project-webgpu-gridsample-bug for
/// the full multi-pass diagnosis history; summary below.
/// </para>
///
/// <para>
/// <b>Root cause (final diagnosis):</b> not a WebGPU EP kernel bug — the WGSL
/// <c>GridSample</c> shader and ORT's CPU kernel (read side by side, ONNX Runtime source
/// at <c>~/onnxruntime</c>) implement identical math, both computing in f32 internally.
/// Both Heron and PP-DocLayoutV3's deformable-attention decoders select their initial
/// queries via <c>TopK(ReduceMax(enc_score_head(...)))</c> over ~8400 candidate tokens.
/// On real pages, many candidate scores cluster within a single FP16 ULP of the k=300
/// cutoff (several are bit-identical ties) — and CPU's and WebGPU's independently-
/// implemented FP16 kernels accumulate enough ordinary rounding drift through the
/// backbone/encoder (measured: <c>ReduceMax</c> cosSim 0.99999, but mean absolute
/// difference ~0.014 — <em>larger</em> than the ~0.002 gap between adjacent-ranked
/// candidates at the cutoff) to select a genuinely different ~10% of the top-300 query
/// set between the two EPs. Those different queries then sample completely different
/// spatial locations via <c>GridSample</c>, which is why its output collapses (cosSim as
/// low as 0.39) even though <c>GridSample</c>'s own arithmetic is correct on both sides —
/// it's faithfully reflecting queries selected from different tokens. Heron hits this
/// ~10.2% of the time per page vs PP-DocLayoutV3's ~0.77% (a ~13x lower exposure, from
/// each model's own learned score/reference-point distribution) — explaining why Heron
/// showed 50 missed detections on a 42-page/11-document corpus while V3 showed none,
/// despite both having the identical vulnerable architecture pattern.
/// </para>
///
/// <para>
/// <b>Two targeted FP32-promotion graph-surgery mitigations were tried and both measured
/// to make zero difference</b> (identical CPU-vs-GPU cosSim before and after, to 5 decimal
/// places): promoting the grid-renormalization <c>Mul → Sub(-1.0)</c> step to FP32 (a
/// plausible-looking catastrophic-cancellation site, mathematically real but not the
/// dominant driver); and adding a deterministic FP32 index-based tiebreak before
/// <c>TopK</c> (targeting tie-breaking-convention differences). Neither helped because the
/// actual CPU/GPU score disagreement (~0.014 absolute, accumulated across the whole
/// backbone+encoder) is an order of magnitude larger than the ~0.002 rank-spacing at the
/// cutoff — no local patch downstream of that accumulation can close a gap that size.
/// <b>What actually works, measured on the same 42-page corpus that found the original
/// misses:</b> running the plain FP32 ONNX model (already published, no re-export needed)
/// on the WebGPU EP gives cosSim 1.00000 at every checkpoint including <c>GridSample</c>,
/// and <b>0 misses / 0 extras</b> for both models — because FP32 kernels across different
/// hardware backends agree far more tightly (~1e-6 relative) than FP16's ~5e-4, so the
/// same razor-thin TopK margin is never crossed. Speed cost of skipping FP16 turned out to
/// be negligible: 9.85x (Heron) and 7.98x (PP-DocLayoutV3) CPU→GPU speedup, matching what
/// the FP16 exports themselves had claimed (~9.5x / ~7.3x on a single-page spike) — GPU
/// parallelism, not FP16's halved memory bandwidth, was already the dominant speedup
/// factor for these models on the hardware tested. This makes the FP16 export path
/// (<c>tools/onnx-fp16-export</c>) effectively unnecessary for GPU use going forward.
/// </para>
///
/// <para>
/// <b>The tooling bug (fixed).</b> <c>tools/gpu-threshold-probe</c> used to run GPU inference
/// once at a low confidence floor (0.01) and re-filter the resulting block list by
/// score for each threshold being evaluated, on the assumption that NMS only ever lets
/// a higher-scoring box suppress a lower one — true of <c>LayoutAnalyzer.Nms</c> itself,
/// but not of <c>SuppressNestedBlocks</c> (runs after NMS, purely geometric: the smaller
/// of any two overlapping boxes loses regardless of confidence). Admitting a sea of
/// low-confidence candidates reliably produced large, low-confidence, page-spanning
/// noise boxes that geometrically contained real detections, and <c>SuppressNestedBlocks</c>
/// deleted the real (smaller, correct, higher-confidence) blocks outright — a deletion no
/// later score-based re-filter can undo. Fixed by re-running GPU inference directly at each
/// threshold actually needed instead of the low-threshold-then-refilter trick. This bug was
/// real and did inflate early numbers, but fixing it alone was NOT sufficient to see Heron's
/// real problem — the original 4-PDF/8-page academic corpus was too small and too narrow
/// (sampling bias) to surface it even with the tool fixed; only widening the corpus did.
/// </para>
///
/// <para>
/// Diagnostic tooling: <c>tools/gpu-threshold-probe</c> (corpus-level recall/precision,
/// tool bug fixed 2026-08-28), <c>tools/webgpu-diag</c> (per-layer CPU-vs-GPU activation
/// diff, now supports both <c>heron</c> and <c>v3</c> architectures — <c>WebGpuDiag &lt;pdf&gt;
/// &lt;heron|v3&gt; &lt;debugModelPath&gt; [page]</c>, debug model built via
/// <c>tools/webgpu-diag/make_debug_model.py</c>). Both confirmed the fix above at
/// corpus scale (42 pages, 0 misses/0 extras) — if re-validating on a new model export,
/// point <c>Resolve</c>'s FP32 descriptor at it and re-run <c>gpu-threshold-probe</c>
/// before trusting an FP16 GPU export again.
/// </para>
///
/// <para>
/// <b>Usage.</b> Call <see cref="TryEnable"/> for the architecture you're about to
/// construct an analyzer for, then build the analyzer via <c>LayoutAnalyzerFactory</c>
/// (or the concrete constructor) as normal — GPU is applied through the analyzer's
/// existing static <c>ConfigureSession</c> hook, so no other call site changes. If
/// <see cref="TryEnable"/> returns <c>false</c> (no WebGPU-capable device — missing
/// Vulkan loader, no supported GPU, etc.) the hook is left untouched and construction
/// proceeds on CPU exactly as before: GPU is additive, never required.
/// </para>
///
/// <para>
/// <b>Construction-time failures still need a caller-side fallback.</b> Device presence
/// (checked here) doesn't guarantee every model loads on it — an unsupported op, a
/// driver quirk, or a model that needs an FP16/FP32 variant it doesn't have can still
/// throw when <c>InferenceSession</c> is constructed. Wrap analyzer construction in
/// try/catch; on failure call <see cref="Disable"/> for the architecture and retry —
/// that reruns construction on CPU. See memory: project-onnx-gpu-ep-investigation.
/// </para>
///
/// <para>
/// <b>Switching backends means reconstructing the analyzer.</b> The execution provider
/// is fixed at <c>InferenceSession</c> creation; there's no live hot-swap. A consumer
/// that lets the user toggle CPU/GPU at runtime (e.g. a settings panel) must dispose the
/// current analyzer/worker and rebuild it — the same shape as swapping layout models or
/// toggling OCR mode elsewhere in this codebase.
/// </para>
///
/// <para>
/// <b>Thread safety.</b> <c>ConfigureSession</c> on each concrete analyzer class is
/// process-wide static state with no synchronization of its own (see
/// <c>AnalyzerSessionOptions</c>) — <see cref="TryEnable"/>/<see cref="Disable"/> only
/// serialize against each other and the device probe via <see cref="ConstructionLock"/>,
/// they cannot make a *plain* analyzer construction elsewhere safe by themselves. A
/// caller that constructs analyzers from more than one thread — including a CPU-only
/// construction, since it depends on the hook being null — must hold
/// <see cref="ConstructionLock"/> for the entire "set hook (if any) → construct →
/// reset hook" sequence, not just the calls into this class. <see cref="ConstructionLock"/>
/// is reentrant-safe (a plain <c>lock</c>), so nesting is fine.
/// </para>
/// </summary>
public static class WebGpuAccelerator
{
    /// <summary>
    /// Guards the device probe and every read/write of a <c>ConfigureSession</c> hook
    /// via this class. See the type doc's "Thread safety" section — a caller
    /// constructing analyzers from multiple threads must hold this for its whole
    /// construction sequence, not just calls into <see cref="TryEnable"/>/<see cref="Disable"/>.
    /// </summary>
    public static readonly object ConstructionLock = new();

    private static bool _probed;
    private static OrtEpDevice? _device;

    /// <summary>
    /// Whether a WebGPU-capable device was found. Probes and registers the plugin EP
    /// library on first access; the result is cached for the process lifetime (device
    /// presence doesn't change at runtime).
    /// </summary>
    public static bool IsAvailable
    {
        get { lock (ConstructionLock) { Probe(); return _device is not null; } }
    }

    /// <summary>Human-readable device identity, once probed; null if unavailable.</summary>
    public static string? DeviceDescription
    {
        get { lock (ConstructionLock) { Probe(); return _device is null ? null : $"{_device.EpName} / {_device.EpVendor}"; } }
    }

    /// <summary>Caller must hold <see cref="ConstructionLock"/>.</summary>
    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var env = OrtEnv.Instance();
            env.RegisterExecutionProviderLibrary("webgpu_ep_registration", WebGpuEp.GetLibraryPath());
            foreach (var d in env.GetEpDevices())
            {
                if (d.EpName == WebGpuEp.GetEpName()) { _device = d; break; }
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Warn($"[WebGPU] Plugin EP registration failed, staying on CPU: {ex.Message}");
        }
    }

    /// <summary>
    /// ONNX Runtime's WebGPU EP registers int64 kernels (Add, Sub, Equal, …) only when
    /// this provider option is set — off by default (see upstream
    /// microsoft/onnxruntime#29392, #29844). Without it, a graph with an int64 node on
    /// the EP's covered ops (e.g. Heron/RT-DETR's <c>orig_target_sizes</c>-driven
    /// post-processing) fails kernel lookup at <c>Run()</c> time with "GetElementType is
    /// not implemented" — a plugin-EP kernel-registration gap, not a construction
    /// failure, so it isn't caught by the caller-side construction fallback described
    /// above. WebGPU int64 arithmetic is backed by i32 (low 32 bits only); safe here
    /// since every int64 tensor these models pass is page pixel dimensions, far inside
    /// int32 range. See issue #108.
    /// </summary>
    private const string EnableInt64Option = "ep.webgpuexecutionprovider.enableInt64";

    /// <summary>
    /// Points <paramref name="architecture"/>'s analyzer at the WebGPU device for the
    /// next construction. Returns <c>false</c> (and leaves the hook untouched) if no
    /// device was found — the analyzer then builds on CPU exactly as if this were never
    /// called.
    /// </summary>
    public static bool TryEnable(LayoutModelArchitecture architecture)
    {
        lock (ConstructionLock)
        {
            Probe();
            if (_device is null) return false;
            var device = _device;
            SetHook(architecture, opts =>
                opts.AppendExecutionProvider(OrtEnv.Instance(), new[] { device },
                    new Dictionary<string, string> { [EnableInt64Option] = "1" }));
            return true;
        }
    }

    /// <summary>Reverts <paramref name="architecture"/>'s analyzer to CPU-only for the next construction.</summary>
    public static void Disable(LayoutModelArchitecture architecture)
    {
        lock (ConstructionLock) { SetHook(architecture, null); }
    }

    /// <summary>Caller must hold <see cref="ConstructionLock"/>.</summary>
    private static void SetHook(LayoutModelArchitecture architecture, Action<SessionOptions>? hook)
    {
        switch (architecture)
        {
            case LayoutModelArchitecture.Heron: HeronLayoutAnalyzer.ConfigureSession = hook; break;
            case LayoutModelArchitecture.PPDocLayoutS: PPDocLayoutSLayoutAnalyzer.ConfigureSession = hook; break;
            case LayoutModelArchitecture.PPDocLayoutV3: LayoutAnalyzer.ConfigureSession = hook; break;
            default: throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unknown layout-model architecture");
        }
    }
}
