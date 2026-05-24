using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Page-layout inference. Implementations wrap a specific model (PP-DocLayoutV3
/// on desktop today, ORT Web for Lite, a YOLO variant, etc.) and declare what
/// they produce via <see cref="Capabilities"/>. Each implementation is
/// responsible for stamping the appropriate <see cref="BlockRole"/> on every
/// block it returns; Core never branches on the model-specific ClassId.
/// </summary>
public interface ILayoutAnalyzer : IDisposable
{
    /// <summary>
    /// Static, per-instance description of this model: input size, class table,
    /// whether it provides reading order. Read by the analysis pipeline and the
    /// debug overlay.
    /// </summary>
    LayoutModelCapabilities Capabilities { get; }

    /// <summary>
    /// Run detection on a rasterised page. Returned blocks must have
    /// <see cref="LayoutBlock.Role"/> populated; <see cref="LayoutBlock.Order"/>
    /// should be set if the model provides a reading-order signal (otherwise
    /// the <see cref="IReadingOrderResolver"/> in the pipeline assigns it).
    /// The analyzer is responsible for confidence filtering and NMS but should
    /// not perform line detection — that runs after reading order is assigned.
    /// </summary>
    PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default);
}
