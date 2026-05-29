using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Self-describing entry for a layout-detection model: which architecture it is
/// (→ which <see cref="ILayoutAnalyzer"/> runs it and its I/O contract), the
/// canonical on-disk filename to probe for, where to fetch it, and the page
/// raster size the caller should render to before analysis.
///
/// <para>
/// Pure data (no ONNX, no filesystem), so it lives in Core. The canonical set
/// is in <see cref="LayoutModelRegistry"/>; resolve a descriptor to a running
/// analyzer via <c>LayoutAnalyzerFactory</c> in RailReader.Core.Analysis, and
/// to a file on disk via <c>LayoutModelLocator</c> in RailReader.Core.Pdfium.
/// </para>
/// </summary>
/// <param name="Id">Stable lookup key, e.g. <c>"heron-int8"</c>.</param>
/// <param name="DisplayName">Human-facing name for model-picker UI.</param>
/// <param name="Architecture">Architecture family → analyzer + tensor contract.</param>
/// <param name="FileName">Canonical on-disk filename the locator probes for.</param>
/// <param name="DownloadUrl">Direct-download URL (HuggingFace resolve link).</param>
/// <param name="RasterInputSize">
/// Longest-edge pixel size the page should be rasterised to before analysis.
/// Must match the analyzer's advertised <see cref="LayoutModelCapabilities.InputSize"/>.
/// </param>
/// <param name="ProvidesReadingOrder">True if the model emits per-detection reading order.</param>
/// <param name="Quantized">True if this is an INT8/quantized export.</param>
/// <param name="ApproxSizeMb">Approximate download size in MB, for UI/progress.</param>
public sealed record LayoutModelDescriptor(
    string Id,
    string DisplayName,
    LayoutModelArchitecture Architecture,
    string FileName,
    string DownloadUrl,
    int RasterInputSize,
    bool ProvidesReadingOrder,
    bool Quantized = false,
    int ApproxSizeMb = 0);
