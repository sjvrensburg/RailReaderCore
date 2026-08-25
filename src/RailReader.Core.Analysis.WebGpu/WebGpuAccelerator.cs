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
                opts.AppendExecutionProvider(OrtEnv.Instance(), new[] { device }, new Dictionary<string, string>()));
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
