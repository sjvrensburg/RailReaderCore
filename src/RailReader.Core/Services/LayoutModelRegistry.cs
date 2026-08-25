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
}
