using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Assigns reading order to detected layout blocks. Decoupled from the
/// layout-detection model so an application can pair any model with any
/// ordering algorithm (model-emitted order, top-down, XY-cut, learned, …).
///
/// The pipeline in <see cref="AnalysisWorker"/> always runs exactly one
/// resolver after the analyzer returns; if none is supplied the worker
/// picks a sensible default based on
/// <see cref="LayoutModelCapabilities.ProvidesReadingOrder"/>.
/// </summary>
public interface IReadingOrderResolver
{
    /// <summary>
    /// Assigns <c>0..N-1</c> reading order to <paramref name="blocks"/> in place
    /// (by setting <see cref="LayoutBlock.Order"/>) and reorders the list so
    /// that <c>blocks[i].Order == i</c>. The resolver may consult or ignore
    /// any existing <c>Order</c> hints set by the analyzer.
    ///
    /// <paramref name="charBoxes"/> is an optional per-character bounding-box
    /// list from the PDF text layer (in the same page-point coordinate space as
    /// <see cref="LayoutBlock.BBox"/>). Geometry-only resolvers ignore it; a
    /// resolver may use the characters' content-stream <see cref="CharBox.Index"/>
    /// as a tie-break to disambiguate blocks that geometry alone cannot order.
    /// Null/empty for scanned PDFs with no text layer.
    /// </summary>
    void AssignOrder(IList<LayoutBlock> blocks, double pageWidth, double pageHeight,
        IReadOnlyList<CharBox>? charBoxes = null);
}
