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
/// configureSession: WebGpuAccelerator.TryBuildSessionHook())</c>.
/// </para>
///
/// <para>
/// <b>Choosing a GPU (issue #182).</b> The WebGPU EP ignores which <c>OrtEpDevice</c> it is
/// handed: Dawn picks the physical adapter itself. Without help it takes its
/// high-performance choice (a discrete card over an integrated one), and that is what the
/// no-argument <see cref="TryEnable(LayoutModelArchitecture)"/>/<see cref="TryBuildSessionHook()"/>
/// still do; <see cref="DeviceDescription"/> names that device. To target a specific one,
/// pass an index from <see cref="AvailableDevices"/> to
/// <see cref="TryEnable(LayoutModelArchitecture, int)"/>/<see cref="TryBuildSessionHook(int)"/>.
/// The two lists are in different orders: ORT reports devices in the OS's order (on the dev
/// box, Intel Iris Xe then NVIDIA RTX A2000), while Dawn ranks discrete before integrated
/// and appends software adapters such as llvmpipe that ORT never lists. This class
/// translates the index into the plugin's <c>adapterIndex</c> provider option by ranking
/// <see cref="AvailableDevices"/> the way Dawn does: discrete first (ORT's
/// <c>Discrete</c> hardware metadata), ORT's order kept within a group. Measured on the dev
/// box from the process's own counters (NVIDIA memory per PID, i915 <c>drm-engine-*</c>
/// time in fdinfo), not from the description string. Two cards of the same kind keep ORT's
/// relative order, which is the OS's and is untested against Dawn's.
/// </para>
///
/// <para>
/// <b>One GPU per process.</b> The plugin's default context owns a single device for the
/// lifetime of the process: the first WebGPU session fixes it (a second adapter would need
/// a native <c>WGPUInstance</c>/<c>WGPUDevice</c>, which can't be supplied from .NET).
/// <see cref="ActiveDeviceIndex"/> reports the device once a session has been configured.
/// After that, an explicit request for a <i>different</i> device returns <c>false</c>/<c>null</c>
/// instead of letting session construction throw (<c>TryEnable</c> then leaves any hook already
/// installed for that architecture in place, so call <see cref="Disable"/> for CPU), and the
/// no-argument forms join the active device. A settings UI that lets a user pick a GPU should
/// therefore say the change applies after a restart. In particular, the layout analyzer and
/// OCR cannot run on two different GPUs in one process.
/// </para>
///
/// <para>
/// <b>Concurrent inference across WebGPU sessions (issue #165, re-measured 2026-10-31).</b>
/// Up to ORT 1.29 with WebGPU plugin EP 0.3, calling <c>Session.Run()</c> on two
/// WebGPU-backed sessions from two threads at once crashed the process (SIGSEGV/SIGABRT,
/// <see href="https://github.com/microsoft/onnxruntime/issues/32561">microsoft/onnxruntime#32561</see>).
/// Plugin 0.5.0 adds concurrent independent-session execution
/// (<see href="https://github.com/microsoft/onnxruntime/pull/32587">#32587</see>), but only
/// on ORT 1.31+ (or 1.28.3+ / 1.30.1+): on other hosts, 1.29.x included, the plugin falls
/// back to a serialized mode it does not enforce, so the caller must still serialize every
/// WebGPU operation on a device. That is why core and plugin are bumped as a matched pair.
/// On 1.31.1 + 0.5.0, two PP-DocLayoutV3 or Heron sessions ran up to 1,000 concurrent
/// <c>Run()</c> calls without a crash; on 1.29 + 0.3 the same repro crashed every time.
/// <b>Scope of that result:</b> both sessions were on the same physical GPU, which is the only
/// case that can arise: a process uses one GPU (see "One GPU per process" above). Concurrent
/// mode does not allow I/O binding to overlap <c>Run()</c> on the same session; each
/// analyzer here owns its own session, so that does not arise. <c>AnalysisWorker</c> still
/// does not route both its OCR and layout stages through WebGPU: enabling that is a
/// separate change, to be made with its own validation. (An earlier version of this comment
/// said cross-device use was verified on the dev box; before #182 was fixed that check ran
/// both sessions on the NVIDIA card.)
/// </para>
///
/// <para>
/// <b>OCR spot-check (issue #121, 2026-09-11).</b> Unlike the layout analyzers above,
/// PP-OCR's detector (DBNet) and recognizer (CRNN) are plain CNN/RNN graphs with no
/// deformable-attention TopK step, so they were never expected to hit the FP16 tie-break
/// bug documented above — and a one-page spot-check (<c>tools/ocr-cost-probe</c> + a throwaway
/// CPU-vs-GPU text diff) bears that out. It was labelled as running on the Intel Iris Xe iGPU,
/// but before #182 was fixed it ran on the NVIDIA card, so the timings below are the NVIDIA card's:
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
/// WebGPU-backed sessions from two threads at once; see the "Concurrent inference" paragraph
/// above.
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
    // _adapterIndex[i] = Dawn's adapterIndex for _devices[i]; see DawnAdapterOrder.
    private static int[] _adapterIndex = [];

    // The device this process's WebGPU context is bound to, as an index into _devices; -1
    // until a hook from this class first configures a session. Written from inside a hook,
    // which can run outside ConstructionLock (a consumer such as RapidOcrService invokes
    // its delegate on its own schedule), hence Interlocked rather than the lock.
    private static int _activeDevice = -1;

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
    /// Human-readable identity of the device WebGPU inference runs on: the active device once
    /// a session has been configured (<see cref="ActiveDeviceIndex"/>), otherwise the one the
    /// no-argument <see cref="TryEnable(LayoutModelArchitecture)"/>/<see cref="TryBuildSessionHook()"/>
    /// would pick (Dawn's high-performance choice, a discrete card before an integrated one).
    /// <c>null</c> if unavailable. On a multi-GPU machine this is only one of several
    /// candidates: enumerate <see cref="AvailableDevices"/> to see (and let a user pick) the rest.
    /// </summary>
    public static string? DeviceDescription
    {
        get
        {
            lock (ConstructionLock)
            {
                Probe();
                int i = EffectiveAutomaticDevice();
                return i < 0 ? null : Describe(_devices[i]);
            }
        }
    }

    /// <summary>
    /// The <see cref="AvailableDevices"/> index this process's WebGPU inference is bound to,
    /// or <c>null</c> before any session has been configured through this class. The plugin
    /// uses one GPU per process, fixed by the first WebGPU session; after that, a request for
    /// another device is refused (see the type doc's "One GPU per process"), so a new choice
    /// takes effect only after a restart.
    /// </summary>
    public static int? ActiveDeviceIndex
    {
        get
        {
            int i = Volatile.Read(ref _activeDevice);
            return i < 0 ? null : i;
        }
    }

    /// <summary>
    /// Every WebGPU-capable device ORT's plugin EP reports, in the (OS-decided) order it
    /// reports them. That is not necessarily the device the no-argument
    /// <see cref="TryEnable(LayoutModelArchitecture)"/>/<see cref="TryBuildSessionHook()"/>
    /// use (see <see cref="DeviceDescription"/>). Pass an index
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
            _adapterIndex = DawnAdapterOrder(found.Select(IsDiscrete).ToList());
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Warn($"[WebGPU] Plugin EP registration failed, staying on CPU: {ex.Message}");
        }
    }

    /// <summary>
    /// ORT marks a discrete card with a <c>Discrete=1</c> hardware-metadata entry (observed on
    /// Linux/Vulkan for an NVIDIA dGPU; the Intel iGPU had no entry).
    /// </summary>
    private static bool IsDiscrete(OrtEpDevice device) =>
        device.HardwareDevice.Metadata.Entries.TryGetValue("Discrete", out var v) && v == "1";

    /// <summary>
    /// Maps each ORT device (by position) to its index in Dawn's adapter enumeration, which
    /// the plugin's <c>adapterIndex</c> option indexes. Under the plugin's default
    /// high-performance preference Dawn ranks discrete adapters, then integrated, then CPU
    /// (llvmpipe and the like, which ORT never lists, so they only ever come last). Within a
    /// group ORT's order is kept. Measured on Linux/Vulkan with one Intel iGPU and one NVIDIA
    /// dGPU: ORT lists Intel, NVIDIA; Dawn's adapterIndex 0 is NVIDIA, 1 Intel, 2 llvmpipe.
    /// </summary>
    internal static int[] DawnAdapterOrder(IReadOnlyList<bool> isDiscrete)
    {
        var result = new int[isDiscrete.Count];
        int next = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantDiscrete = pass == 0;
            for (int i = 0; i < isDiscrete.Count; i++)
                if (isDiscrete[i] == wantDiscrete) result[i] = next++;
        }
        return result;
    }

    /// <summary>
    /// The <see cref="_devices"/> index a no-argument request lands on: the active device if
    /// there is one, otherwise the one Dawn ranks first; -1 if there are no devices. Caller
    /// must have probed.
    /// </summary>
    private static int EffectiveAutomaticDevice()
    {
        int active = Volatile.Read(ref _activeDevice);
        if (active >= 0) return active;
        return Array.IndexOf(_adapterIndex, 0);
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
    /// Provider-option key for the plugin's physical adapter selector (WebGPU plugin EP 0.5.0+,
    /// microsoft/onnxruntime#32319). Unprefixed: <c>AppendExecutionProvider</c> prepends
    /// <c>ep.webgpuexecutionprovider.</c> to every key itself, so a prefixed key arrives doubled
    /// and is silently ignored.
    /// </summary>
    /// <remarks>
    /// Up to 0.85.0 this class also passed <c>ep.webgpuexecutionprovider.enableInt64</c> — doubled
    /// by that prefixing, so the plugin never saw it. Passing it correctly makes Heron abort the
    /// process inside the plugin on EP 0.5.0 (<c>OrtDevice: Invalid memory type: -1</c>, not a
    /// catchable exception), and Heron runs without it, so it is no longer passed.
    /// </remarks>
    private const string AdapterIndexOption = "adapterIndex";

    /// <summary>
    /// Points <paramref name="architecture"/>'s analyzer at a WebGPU device for the next
    /// construction, letting Dawn choose which one (its high-performance pick, named by
    /// <see cref="DeviceDescription"/>; the active device once one is bound). Returns
    /// <c>false</c> (and leaves the hook untouched) if there is no WebGPU device — the analyzer
    /// then builds on CPU exactly as if this were never called.
    /// </summary>
    public static bool TryEnable(LayoutModelArchitecture architecture) => TryEnableCore(architecture, deviceIndex: null);

    /// <summary>
    /// Points <paramref name="architecture"/>'s analyzer at the device at
    /// <paramref name="deviceIndex"/> in <see cref="AvailableDevices"/> for the next
    /// construction. Returns <c>false</c> (and leaves the hook untouched) if there is no device
    /// at that index, or if this process is already bound to a different device
    /// (<see cref="ActiveDeviceIndex"/>) — the analyzer then builds exactly as if this were
    /// never called: on CPU, unless an earlier <c>TryEnable</c> left a hook in place, which
    /// keeps targeting the device it was built for (call <see cref="Disable"/> to force CPU).
    /// </summary>
    public static bool TryEnable(LayoutModelArchitecture architecture, int deviceIndex) =>
        TryEnableCore(architecture, deviceIndex);

    private static bool TryEnableCore(LayoutModelArchitecture architecture, int? deviceIndex)
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
    /// Architecture-independent form of <see cref="TryEnable(LayoutModelArchitecture)"/>:
    /// probes for WebGPU devices (cached after the first call, same probe as
    /// <see cref="IsAvailable"/>) and returns a <c>SessionOptions</c> configurator that lets
    /// Dawn choose the device, or <c>null</c> if there is none. Unlike <see cref="TryEnable(LayoutModelArchitecture)"/>
    /// this does not touch any analyzer's <c>ConfigureSession</c> hook — it hands the delegate
    /// to the caller, so any ONNX consumer (OCR's <c>RapidOcrService(configureSession: ...)</c>,
    /// or a future one) can opt in without this assembly knowing about it.
    /// </summary>
    public static Action<SessionOptions>? TryBuildSessionHook()
    {
        lock (ConstructionLock) { return TryBuildSessionHookLocked(deviceIndex: null); }
    }

    /// <summary>
    /// Like <see cref="TryBuildSessionHook()"/>, but for the device at
    /// <paramref name="deviceIndex"/> in <see cref="AvailableDevices"/> (e.g. a user's choice
    /// from a settings dropdown). Returns <c>null</c> if there is no device at that index, or
    /// if this process is already bound to a different device (<see cref="ActiveDeviceIndex"/>).
    /// A hook built before another device was bound throws <see cref="InvalidOperationException"/>
    /// when it configures a session — the same construction-time failure a caller already
    /// falls back to CPU on.
    /// </summary>
    public static Action<SessionOptions>? TryBuildSessionHook(int deviceIndex)
    {
        lock (ConstructionLock) { return TryBuildSessionHookLocked(deviceIndex); }
    }

    /// <summary>
    /// <paramref name="deviceIndex"/> <c>null</c> = automatic: the active device if one is bound,
    /// otherwise Dawn's first-ranked one, resolved when the hook runs. Every hook passes an
    /// explicit <c>adapterIndex</c>, automatic ones included: the plugin compares a session's
    /// <c>adapterIndex</c> against the one its context was initialised with and throws on any
    /// difference, "unset" versus "0" included, so a context first initialised without one
    /// would reject a later explicit request even for the same GPU.
    /// Caller must hold <see cref="ConstructionLock"/>.
    /// </summary>
    private static Action<SessionOptions>? TryBuildSessionHookLocked(int? deviceIndex)
    {
        Probe();
        if (_devices.Count == 0) return null;

        if (deviceIndex is not int requested)
        {
            return opts =>
            {
                // Bind-or-join in one step: reading the active device and then binding would
                // race an explicit hook on another thread (e.g. OCR built on its worker) and
                // throw, where an automatic request should simply join whichever device won.
                int dawnFirst = Array.IndexOf(_adapterIndex, 0);
                int prior = Interlocked.CompareExchange(ref _activeDevice, dawnFirst, -1);
                Append(opts, prior >= 0 ? prior : dawnFirst);
            };
        }

        if ((uint)requested >= (uint)_devices.Count) return null;
        int active = Volatile.Read(ref _activeDevice);
        if (active >= 0 && active != requested)
        {
            RailReaderLogging.Logger.Warn(
                $"[WebGPU] {Describe(_devices[requested])} requested, but this process is already using " +
                $"{Describe(_devices[active])}; WebGPU uses one GPU per process, so staying on CPU. Restart to switch.");
            return null;
        }

        return opts =>
        {
            // Append first: if it throws, no session can exist on the device, so the process
            // must not be pinned to it. Bind still runs before the session is constructed.
            Append(opts, requested);
            Bind(requested);
        };
    }

    private static void Append(SessionOptions opts, int device) =>
        opts.AppendExecutionProvider(OrtEnv.Instance(), new[] { _devices[device] },
            new Dictionary<string, string>
            {
                [AdapterIndexOption] = _adapterIndex[device].ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    /// <summary>
    /// Records <paramref name="device"/> as the process's WebGPU device if none is yet, or
    /// throws if another one is: the plugin would reject the session anyway, with a less
    /// helpful message.
    /// </summary>
    private static void Bind(int device)
    {
        int prior = Interlocked.CompareExchange(ref _activeDevice, device, -1);
        if (prior >= 0 && prior != device)
            throw new InvalidOperationException(
                $"WebGPU is already using {Describe(_devices[prior])} in this process and cannot also use " +
                $"{Describe(_devices[device])}: the plugin binds one GPU per process.");
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
