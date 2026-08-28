using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Canonical set of layout-detection models RailReader knows how to download
/// and run, plus the recommended <see cref="Default"/>. Pure data — no ONNX, no
/// filesystem — so it belongs in Core. Resolve a descriptor to a running
/// analyzer with <c>LayoutAnalyzerFactory</c> (RailReader.Core.Analysis) and to
/// a path on disk with <c>LayoutModelLocator</c> (RailReader.Core.Pdfium).
/// </summary>
public static class LayoutModelRegistry
{
    /// <summary>
    /// Backbone-only INT8 Docling Heron — the recommended default. ~2.6–3×
    /// faster than FP32 V3 on a VNNI-capable CPU, validated at ~0.99
    /// detection agreement with FP32 Heron on held-out academic pages. Only
    /// the CNN backbone is quantized; the RT-DETR decoder stays FP32 (full-graph
    /// quantization corrupts the decoder's anchor scales).
    /// </summary>
    public static LayoutModelDescriptor HeronInt8 { get; } = new(
        Id: "heron-int8",
        DisplayName: "Docling Heron — INT8 (recommended)",
        Architecture: LayoutModelArchitecture.Heron,
        FileName: "docling-layout-heron-int8.onnx",
        DownloadUrl: "https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx/resolve/main/docling-layout-heron-int8.onnx",
        RasterInputSize: 640,
        ProvidesReadingOrder: false,
        Quantized: true,
        ApproxSizeMb: 69,
        Sha256: "087ed4fa1ae3ee03003d8f02f8e5c4d1497cb45e4122e62dfa531acbbe841364");

    /// <summary>Docling Heron FP32 (RT-DETRv2, 17-class).</summary>
    public static LayoutModelDescriptor Heron { get; } = new(
        Id: "heron",
        DisplayName: "Docling Heron — FP32",
        Architecture: LayoutModelArchitecture.Heron,
        FileName: "docling-layout-heron.onnx",
        DownloadUrl: "https://huggingface.co/docling-project/docling-layout-heron-onnx/resolve/main/model.onnx",
        RasterInputSize: 640,
        ProvidesReadingOrder: false,
        ApproxSizeMb: 164);

    /// <summary>
    /// Docling Heron FP16 (RT-DETRv2, 17-class) — re-exported from the PyTorch/HF
    /// Transformers checkpoint (not converted from the FP32 ONNX; see
    /// <c>tools/onnx-fp16-export/</c>) for GPU inference via the native WebGPU
    /// execution provider (<c>RailReader.Core.Analysis.WebGpu</c>). Same I/O
    /// contract as <see cref="Heron"/> — drop-in on the analyzer side. Runs on
    /// CPU too (ORT upconverts), but is not the CPU-optimal choice —
    /// <see cref="HeronInt8"/> is faster there.
    /// <b>⚠ GPU inference via WebGPU is NOT recommended for this model (confirmed at
    /// scale, 2026-08-28).</b> An earlier "severe under-detection" report was mostly a
    /// bug in the measurement tool (<c>tools/gpu-threshold-probe</c>'s low-threshold
    /// trick), now fixed — but on a 42-page corpus spanning both academic PDFs and
    /// plain single-column documents (forms, invoices), the *fixed* tool still shows
    /// severe, real GPU-vs-CPU divergence for this model: 50 missed detections + 13
    /// spurious extras, hitting plain/simple documents hardest (e.g. 7 misses on one
    /// page of a short form). This reproduces field reports from RailReader2 of
    /// frequent, visible rail-reading misses. <see cref="PPDocLayoutV3Fp16"/> shows no
    /// such divergence on the same corpus, so this is Heron-specific, not a generic
    /// WebGPU EP problem. Not root-caused — an <c>enc_score_head</c> fp32-promotion
    /// graph-surgery experiment did not fix it. See <c>WebGpuAccelerator</c>'s doc
    /// comment and memory project-webgpu-gridsample-bug.
    /// </summary>
    public static LayoutModelDescriptor HeronFp16 { get; } = new(
        Id: "heron-fp16",
        DisplayName: "Docling Heron — FP16 (GPU)",
        Architecture: LayoutModelArchitecture.Heron,
        FileName: "docling-layout-heron-fp16.onnx",
        DownloadUrl: "https://huggingface.co/stefanj0/docling-layout-heron-fp16-onnx/resolve/main/docling-layout-heron-fp16.onnx",
        RasterInputSize: 640,
        ProvidesReadingOrder: false,
        ApproxSizeMb: 86,
        Sha256: "2289730c3b83d1b6ba19b1b59d035da3c3867f6bafcdc19cb982bcd940445ed8");

    /// <summary>PP-DocLayoutV3 FP32 (25-class, model-supplied reading order).</summary>
    public static LayoutModelDescriptor PPDocLayoutV3 { get; } = new(
        Id: "ppdoclayoutv3",
        DisplayName: "PP-DocLayoutV3 — FP32",
        Architecture: LayoutModelArchitecture.PPDocLayoutV3,
        FileName: "PP-DocLayoutV3.onnx",
        // Mirrored to our own HF account (was a third-party repo) for a stable,
        // checksum-verified source. The Sha256 is the canonical 130,502,049-byte file.
        DownloadUrl: "https://huggingface.co/stefanj0/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx",
        RasterInputSize: 800,
        ProvidesReadingOrder: true,
        ApproxSizeMb: 125,
        Sha256: "d24809294b2f9f1a9a2767043a64df2714b66e5be056887be2233d1117d784f6");

    /// <summary>
    /// PP-DocLayoutV3 FP16 — re-exported from the <c>PaddlePaddle/PP-DocLayoutV3_safetensors</c>
    /// PyTorch/HF Transformers port (not converted from the FP32 ONNX; see
    /// <c>tools/onnx-fp16-export/</c>) for GPU inference via the native WebGPU
    /// execution provider (<c>RailReader.Core.Analysis.WebGpu</c>). Same
    /// <c>[N,7]</c> detection-tensor contract (including model-supplied reading
    /// order) as <see cref="PPDocLayoutV3"/> — drop-in on the analyzer side.
    /// <b>Correctness re-validated 2026-08-28</b> — the "severe under-detection"
    /// previously attributed to this model was a bug in the measurement tool
    /// (<c>tools/gpu-threshold-probe</c>'s low-threshold-then-refilter trick, broken by
    /// <c>SuppressNestedBlocks</c>'s non-confidence-aware suppression), now fixed. With
    /// the tool fixed, this model shows <b>zero</b> CPU-vs-GPU detection misses on the
    /// project's real-PDF corpus check — unlike <see cref="HeronFp16"/>, which still
    /// shows a small residual divergence. Re-validate on a larger corpus before fully
    /// trusting this for production default, but there is currently no evidence of a
    /// real correctness problem for this model. See <c>WebGpuAccelerator</c>'s doc
    /// comment and memory project-webgpu-gridsample-bug.
    /// </summary>
    public static LayoutModelDescriptor PPDocLayoutV3Fp16 { get; } = new(
        Id: "ppdoclayoutv3-fp16",
        DisplayName: "PP-DocLayoutV3 — FP16 (GPU)",
        Architecture: LayoutModelArchitecture.PPDocLayoutV3,
        FileName: "PP-DocLayoutV3-fp16.onnx",
        DownloadUrl: "https://huggingface.co/stefanj0/PP-DocLayoutV3-FP16-ONNX/resolve/main/PP-DocLayoutV3-fp16.onnx",
        RasterInputSize: 800,
        ProvidesReadingOrder: true,
        ApproxSizeMb: 68,
        Sha256: "8bb693ed3b5dcc1cf926b15d89dfe6abf62bc11cdd0afd33c8ffe039db6f8209");

    /// <summary>PP-DocLayout-S (PicoDet/GFL, ~4.7 MB; intended for web/mobile).</summary>
    public static LayoutModelDescriptor PPDocLayoutS { get; } = new(
        Id: "pp-doclayout-s",
        DisplayName: "PP-DocLayout-S — lightweight",
        Architecture: LayoutModelArchitecture.PPDocLayoutS,
        FileName: "pp_doclayout_s.onnx",
        DownloadUrl: "https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/resolve/main/pp_doclayout_s.onnx",
        RasterInputSize: 1920,
        ProvidesReadingOrder: false,
        ApproxSizeMb: 5,
        Sha256: "33688dbee1c23e34b81777e97cb428eb40f24b242c02b5f623484959e830aec8");

    /// <summary>The recommended model for new consumers (backbone-INT8 Heron).</summary>
    public static LayoutModelDescriptor Default => HeronInt8;

    /// <summary>All known models, default first.</summary>
    public static IReadOnlyList<LayoutModelDescriptor> All { get; } =
        [HeronInt8, Heron, HeronFp16, PPDocLayoutV3, PPDocLayoutV3Fp16, PPDocLayoutS];

    /// <summary>Looks up a descriptor by its <see cref="LayoutModelDescriptor.Id"/>; null if unknown.</summary>
    public static LayoutModelDescriptor? ById(string id)
    {
        foreach (var d in All)
            if (string.Equals(d.Id, id, System.StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    /// <summary>
    /// Routes an architecture + <see cref="AcceleratorPreference"/> to the descriptor
    /// that backend wants — CPU gets the CPU-optimal export (INT8 for Heron, plain
    /// FP32 otherwise), GPU gets the FP16 export where one exists. Falls back to the
    /// CPU descriptor for a GPU request when no FP16 export exists yet (PP-DocLayout-S)
    /// — the caller still gets a working model, just not GPU-optimized; this is
    /// deliberately never a hard failure so a caller can always ask for GPU and get
    /// something back. This only picks the model <em>file</em>; it does not enable a
    /// GPU execution provider — pair a <see cref="AcceleratorPreference.Gpu"/> result
    /// with <c>WebGpuAccelerator.TryEnable</c> (<c>RailReader.Core.Analysis.WebGpu</c>)
    /// before constructing the analyzer, and fall back to
    /// <c>Resolve(architecture, AcceleratorPreference.Cpu)</c> if that returns false or
    /// analyzer construction still throws (device presence doesn't guarantee every
    /// model loads on it).
    /// </summary>
    public static LayoutModelDescriptor Resolve(LayoutModelArchitecture architecture, AcceleratorPreference accelerator) =>
        (architecture, accelerator) switch
        {
            (LayoutModelArchitecture.Heron, AcceleratorPreference.Gpu) => HeronFp16,
            (LayoutModelArchitecture.Heron, _) => HeronInt8,
            (LayoutModelArchitecture.PPDocLayoutV3, AcceleratorPreference.Gpu) => PPDocLayoutV3Fp16,
            (LayoutModelArchitecture.PPDocLayoutV3, _) => PPDocLayoutV3,
            (LayoutModelArchitecture.PPDocLayoutS, _) => PPDocLayoutS,
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(architecture), architecture, "Unknown layout-model architecture"),
        };

    /// <summary>
    /// The recommended model for <paramref name="accelerator"/>, independent of any
    /// particular architecture choice — <see cref="Default"/>'s GPU-aware counterpart.
    /// Currently routes through Heron (<see cref="HeronInt8"/>/<see cref="HeronFp16"/>)
    /// for both preferences, so switching accelerators doesn't also switch the model's
    /// class taxonomy underneath the caller.
    /// </summary>
    public static LayoutModelDescriptor DefaultFor(AcceleratorPreference accelerator) =>
        Resolve(LayoutModelArchitecture.Heron, accelerator);
}
