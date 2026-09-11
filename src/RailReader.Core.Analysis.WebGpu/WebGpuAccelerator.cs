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
/// proceeds on CPU exactly as before: GPU is additive, never required. For any other ONNX
/// consumer that isn't a layout analyzer — e.g. <c>Core.Ocr.RapidOcr</c>'s
/// <c>RapidOcrService</c>, which takes its own <c>configureSession</c> delegate — use
/// <see cref="TryBuildSessionHook"/> instead: <c>new RapidOcrService(modelSet,
/// configureSession: WebGpuAccelerator.TryBuildSessionHook())</c>. On a machine with more
/// than one WebGPU-capable device (an integrated + discrete GPU pair, or multiple discrete
/// cards) both methods default to <see cref="AvailableDevices"/>[0] — whichever the driver
/// happens to report first, not necessarily the one a user wants. Read
/// <see cref="AvailableDevices"/> to list them (vendor, device ID, a ready-to-display
/// string) and pass the chosen index to <c>TryEnable(architecture, deviceIndex)</c> /
/// <c>TryBuildSessionHook(deviceIndex)</c> to target it.
/// </para>
///
/// <para>
/// <b>⚠ Different models on different GPUs — construction is safe, CONCURRENT INFERENCE
/// IS NOT (confirmed 2026-09-11, issue #121 follow-up).</b> Each <c>deviceIndex</c>
/// argument is independent per call, so nothing stops a multi-GPU host from constructing
/// a layout analyzer against one device and a <c>RapidOcrService</c> against another —
/// <c>WebGpuAccelerator.TryEnable(LayoutModelArchitecture.PPDocLayoutV3, deviceIndex: 0)</c>
/// alongside <c>new RapidOcrService(modelSet,
/// configureSession: WebGpuAccelerator.TryBuildSessionHook(deviceIndex: 1))</c> — and doing
/// so <i>sequentially</i> (construct both, run one, then the other) works cleanly, verified
/// on this repo's own dev box (an NVIDIA GPU + an Intel iGPU, both visible over Vulkan).
/// <b>But calling <c>Session.Run()</c> on two WebGPU-backed sessions from two threads at
/// the same time segfaults the process (exit 139) — measured on this hardware for both a
/// cross-device pair (layout on the NVIDIA device, OCR on the Intel device) and two
/// sessions on the <i>same</i> device.</b> This is a plugin-EP concurrency limitation
/// (likely the shared Dawn/WebGPU native instance is not reentrant across sessions), not
/// something under this assembly's control, and it is NOT hypothetical: this codebase's
/// own <c>AnalysisWorker</c> runs OCR and layout inference on two independent, chained
/// threads that are documented to "progress independently" — i.e. exactly the two
/// concurrent-<c>Run()</c> shape that crashes. <b>Do not route both the OCR stage and the
/// layout stage through WebGPU in that pipeline until this is fixed</b> — either upstream
/// (ORT WebGPU EP), or here via a process-wide lock serializing every WebGPU-backed
/// <c>Run()</c> call (not yet implemented; would need to touch <c>LayoutAnalyzer</c>,
/// <c>PPDocLayoutSLayoutAnalyzer</c>, <c>HeronLayoutAnalyzer</c>, and
/// <c>RapidOcrService</c>, none of which currently know whether their session is
/// GPU-backed). Single-GPU-only use (layout OR OCR on GPU, never both at once) is
/// unaffected — that's the one configuration this crash cannot reach.
/// </para>
///
/// <para>
/// <b>OCR spot-check (issue #121, 2026-09-11).</b> Unlike the layout analyzers above,
/// PP-OCR's detector (DBNet) and recognizer (CRNN) are plain CNN/RNN graphs with no
/// deformable-attention TopK step, so they were never expected to hit the FP16 tie-break
/// bug documented above — and a one-page spot-check on an Intel Iris Xe iGPU
/// (<c>tools/ocr-cost-probe</c> + a throwaway CPU-vs-GPU text diff) bears that out:
/// v5-latin, PP-OCRv6 Small and PP-OCRv6 Medium all produced byte-for-byte identical
/// recognized text, line count, and line order on GPU vs CPU. Speed scales with model
/// size the opposite way it does on CPU — GPU is where the expensive tiers pay off: on
/// that page, PP-OCRv6 Medium went from ~274&#160;s/page CPU to ~21&#160;s/page GPU
/// (~13x), Small ~29&#160;s→~20&#160;s (~1.4x), v5-latin/Tiny roughly a wash (small
/// models don't have enough work to hide dispatch overhead). This is a single-page,
/// single-device spot-check, not the 42-page corpus validation the layout analyzers
/// got — treat it as "promising, wire it behind an opt-in" rather than "proven safe",
/// and widen the corpus (different scripts, skewed/noisy scans, other GPU vendors)
/// before defaulting anyone into it.
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
/// is reentrant-safe (a plain <c>lock</c>), so nesting is fine. <see cref="ConstructionLock"/>
/// covers construction only — it says nothing about calling <c>Run()</c> on two already-built
/// WebGPU-backed sessions from two threads at once; that is a confirmed crash, see the
/// "Different models on different GPUs" paragraph below.
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
    private static IReadOnlyList<OrtEpDevice> _devices = [];

    /// <summary>
    /// Whether at least one WebGPU-capable device was found. Probes and registers the
    /// plugin EP library on first access; the result is cached for the process lifetime
    /// (device presence doesn't change at runtime).
    /// </summary>
    public static bool IsAvailable
    {
        get { lock (ConstructionLock) { Probe(); return _devices.Count > 0; } }
    }

    /// <summary>
    /// Human-readable identity of <see cref="AvailableDevices"/>[0] — the device every
    /// no-argument <see cref="TryEnable"/>/<see cref="TryBuildSessionHook"/> call picks —
    /// once probed; <c>null</c> if unavailable. On a multi-GPU machine this is only one of
    /// several candidates: enumerate <see cref="AvailableDevices"/> to see (and let a user
    /// pick) the rest.
    /// </summary>
    public static string? DeviceDescription
    {
        get { lock (ConstructionLock) { Probe(); return _devices.Count == 0 ? null : Describe(_devices[0]); } }
    }

    /// <summary>
    /// Every WebGPU-capable device ORT's plugin EP reports, in the (unspecified,
    /// driver-decided) order it reports them — index 0 is what every no-argument
    /// <see cref="TryEnable"/>/<see cref="TryBuildSessionHook"/> call uses. Pass an index
    /// from this list to <see cref="TryEnable(LayoutModelArchitecture, int)"/> or
    /// <see cref="TryBuildSessionHook(int)"/> to target a specific one — e.g. surfaced as a
    /// dropdown in a settings UI on a machine with more than one GPU (an integrated +
    /// discrete pair, or multiple discrete cards). Empty if <see cref="IsAvailable"/> is
    /// <c>false</c>.
    /// </summary>
    public static IReadOnlyList<WebGpuDeviceInfo> AvailableDevices
    {
        get
        {
            lock (ConstructionLock)
            {
                Probe();
                var result = new WebGpuDeviceInfo[_devices.Count];
                for (int i = 0; i < _devices.Count; i++) result[i] = ToInfo(i, _devices[i]);
                return result;
            }
        }
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
            var found = new List<OrtEpDevice>();
            foreach (var d in env.GetEpDevices())
            {
                if (d.EpName == WebGpuEp.GetEpName()) found.Add(d);
            }
            _devices = found;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Warn($"[WebGPU] Plugin EP registration failed, staying on CPU: {ex.Message}");
        }
    }

    private static string Describe(OrtEpDevice device)
    {
        var hw = device.HardwareDevice;
        string vendor = string.IsNullOrEmpty(hw.Vendor) ? VendorName(hw.VendorId) : hw.Vendor;
        return $"{vendor} {hw.Type} (device 0x{hw.DeviceId:X4})";
    }

    private static WebGpuDeviceInfo ToInfo(int index, OrtEpDevice device)
    {
        var hw = device.HardwareDevice;
        string vendor = string.IsNullOrEmpty(hw.Vendor) ? VendorName(hw.VendorId) : hw.Vendor;
        return new WebGpuDeviceInfo(index, vendor, hw.VendorId, hw.DeviceId, hw.Type.ToString(), Describe(device));
    }

    /// <summary>
    /// ORT's WebGPU/Dawn hardware-device report carries a PCI vendor ID but often no
    /// vendor name string (observed empty for both Intel and NVIDIA devices on Linux/Vulkan)
    /// — map the common GPU vendor IDs by hand rather than show a bare hex code for the
    /// overwhelmingly common case. Falls back to the hex ID for anything not listed.
    /// </summary>
    private static string VendorName(uint pciVendorId) => pciVendorId switch
    {
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        0x1002 => "AMD",
        0x13B5 => "ARM",
        0x5143 => "Qualcomm",
        0x106B => "Apple",
        _ => $"vendor 0x{pciVendorId:X4}",
    };

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
    /// Points <paramref name="architecture"/>'s analyzer at a WebGPU device for the next
    /// construction. <paramref name="deviceIndex"/> indexes into <see cref="AvailableDevices"/>
    /// (default 0 — the first device ORT's plugin EP reports; on a single-GPU machine
    /// that's the only choice, on a multi-GPU one it's driver-decided and not necessarily
    /// the one a user wants). Returns <c>false</c> (and leaves the hook untouched) if no
    /// device was found at that index — the analyzer then builds on CPU exactly as if this
    /// were never called.
    /// </summary>
    public static bool TryEnable(LayoutModelArchitecture architecture, int deviceIndex = 0)
    {
        lock (ConstructionLock)
        {
            var hook = TryBuildSessionHookLocked(deviceIndex);
            if (hook is null) return false;
            SetHook(architecture, hook);
            return true;
        }
    }

    /// <summary>
    /// Architecture-independent form of <see cref="TryEnable"/>: probes for WebGPU devices
    /// (cached after the first call, same probe as <see cref="IsAvailable"/>) and returns a
    /// <c>SessionOptions</c> configurator for the one at <paramref name="deviceIndex"/> in
    /// <see cref="AvailableDevices"/> (default 0), or <c>null</c> if no device exists at
    /// that index. Unlike <see cref="TryEnable"/> this does not touch any analyzer's
    /// <c>ConfigureSession</c> hook — it hands the delegate to the caller, so any ONNX
    /// consumer (OCR's <c>RapidOcrService(configureSession: ...)</c>, or a future one) can
    /// opt in without this assembly knowing about it. On a machine with more than one
    /// WebGPU-capable device, read <see cref="AvailableDevices"/> first (e.g. to populate a
    /// settings dropdown) and pass the user's choice here — there's no other way to steer
    /// which GPU gets used.
    /// </summary>
    public static Action<SessionOptions>? TryBuildSessionHook(int deviceIndex = 0)
    {
        lock (ConstructionLock) { return TryBuildSessionHookLocked(deviceIndex); }
    }

    /// <summary>Caller must hold <see cref="ConstructionLock"/>.</summary>
    private static Action<SessionOptions>? TryBuildSessionHookLocked(int deviceIndex)
    {
        Probe();
        if ((uint)deviceIndex >= (uint)_devices.Count) return null;
        var device = _devices[deviceIndex];
        return opts =>
            opts.AppendExecutionProvider(OrtEnv.Instance(), new[] { device },
                new Dictionary<string, string> { [EnableInt64Option] = "1" });
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

/// <summary>
/// One WebGPU-capable device as reported by ORT's plugin EP, for a caller to list (e.g. a
/// settings dropdown) and choose between — see <see cref="WebGpuAccelerator.AvailableDevices"/>.
/// </summary>
/// <param name="Index">Pass this to <see cref="WebGpuAccelerator.TryEnable"/>/<see cref="WebGpuAccelerator.TryBuildSessionHook"/> to select this device.</param>
/// <param name="Vendor">Human-readable vendor name (e.g. "NVIDIA", "Intel"), resolved from <paramref name="VendorId"/> where ORT reports no vendor string.</param>
/// <param name="VendorId">Raw PCI vendor ID.</param>
/// <param name="DeviceId">Raw PCI device ID — distinguishes multiple cards from the same vendor.</param>
/// <param name="HardwareType">ORT's <c>OrtHardwareDeviceType</c> as a string (typically "GPU").</param>
/// <param name="Description">Ready-to-display summary, e.g. "NVIDIA GPU (device 0x25BA)".</param>
public readonly record struct WebGpuDeviceInfo(
    int Index, string Vendor, uint VendorId, uint DeviceId, string HardwareType, string Description);
