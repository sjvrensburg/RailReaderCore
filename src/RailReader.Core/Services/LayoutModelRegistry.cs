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
        ApproxSizeMb: 69);

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

    /// <summary>PP-DocLayoutV3 FP32 (25-class, model-supplied reading order).</summary>
    public static LayoutModelDescriptor PPDocLayoutV3 { get; } = new(
        Id: "ppdoclayoutv3",
        DisplayName: "PP-DocLayoutV3 — FP32",
        Architecture: LayoutModelArchitecture.PPDocLayoutV3,
        FileName: "PP-DocLayoutV3.onnx",
        DownloadUrl: "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx",
        RasterInputSize: 800,
        ProvidesReadingOrder: true,
        ApproxSizeMb: 125);

    /// <summary>PP-DocLayout-S (PicoDet/GFL, ~4.7 MB; intended for web/mobile).</summary>
    public static LayoutModelDescriptor PPDocLayoutS { get; } = new(
        Id: "pp-doclayout-s",
        DisplayName: "PP-DocLayout-S — lightweight",
        Architecture: LayoutModelArchitecture.PPDocLayoutS,
        FileName: "pp_doclayout_s.onnx",
        DownloadUrl: "https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/resolve/main/pp_doclayout_s.onnx",
        RasterInputSize: 1920,
        ProvidesReadingOrder: false,
        ApproxSizeMb: 5);

    /// <summary>The recommended model for new consumers (backbone-INT8 Heron).</summary>
    public static LayoutModelDescriptor Default => HeronInt8;

    /// <summary>All known models, default first.</summary>
    public static IReadOnlyList<LayoutModelDescriptor> All { get; } =
        [HeronInt8, Heron, PPDocLayoutV3, PPDocLayoutS];

    /// <summary>Looks up a descriptor by its <see cref="LayoutModelDescriptor.Id"/>; null if unknown.</summary>
    public static LayoutModelDescriptor? ById(string id)
    {
        foreach (var d in All)
            if (string.Equals(d.Id, id, System.StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }
}
