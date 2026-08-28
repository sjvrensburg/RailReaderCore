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
/// <b>⚠ Not currently recommended for either GPU-capable layout model (2026-08-26,
/// re-diagnosed same day).</b> Both Heron and PP-DocLayoutV3 are RT-DETR-family models
/// that select decoder queries via a <c>TopK</c> over encoder objectness scores and
/// derive each box from mask logits via a <c>Greater</c>-than-zero threshold plus
/// <c>ReduceMin</c>/<c>ReduceMax</c>. This was originally filed as a WebGPU EP
/// <c>GridSample</c> kernel bug (activations match the CPU EP at cosine similarity
/// 0.9999–1.0 through the entire backbone, then collapse to ~0.52 at the first
/// <c>GridSample</c> node) — <see href="https://github.com/microsoft/onnxruntime/issues/32275"/>,
/// filed then retracted/closed after further bisection. The real cause: the WGSL
/// <c>GridSample</c> kernel itself is correct (verified by extracting the generated
/// shader and running it directly against a NumPy reference on real Vulkan hardware —
/// exact match in both fp32 and fp16). What actually diverges is upstream of
/// <c>GridSample</c>: the <c>TopK</c> node's *scores* match CPU vs GPU almost exactly
/// (cosine similarity 1.00000, fp16-rounding-level differences), but that's enough to
/// flip tie-broken ordering right at the top-300 selection cutoff (295/300 indices
/// matched; the mismatches were adjacent-rank swaps of near-tied scores), and to flip
/// the <c>Greater</c> mask threshold near object boundaries. Because every downstream
/// per-query computation (mask head, box decode, then <c>GridSample</c> in deformable
/// attention) is keyed to query position, that small amount of ordinary cross-backend
/// floating-point noise cascades into substantial under-detection on GPU vs CPU —
/// <c>GridSample</c> just happened to be the first instrumented checkpoint downstream
/// of the cascade. This is not an ONNX Runtime bug and not fixable in this codebase;
/// a fix would need to happen in the model/export (e.g. keeping the score/mask heads
/// in fp32 even in an otherwise-fp16 export) or by accepting CPU-only inference for
/// these architectures. See memory: project-webgpu-gridsample-bug (superseded
/// diagnosis, kept for history — read the update). Diagnostic tooling:
/// <c>tools/gpu-threshold-probe</c> (corpus-level recall/precision),
/// <c>tools/webgpu-diag</c> (per-layer CPU-vs-GPU activation diff). Do not re-enable
/// GPU acceleration by default unless/until the score/mask heads are moved to fp32 in
/// the export and re-validated, since the fix is not an upstream ORT release to wait for.
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
