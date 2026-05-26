using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core.Analysis.LightGbm;

/// <summary>
/// A layout analyzer that operates on the PDF text layer directly,
/// rather than on a rasterised pixmap of the page. Sibling shape to
/// <see cref="ILayoutAnalyzer"/> — separate interface because the
/// inputs are fundamentally different (PDF bytes + page index vs.
/// pre-rasterised RGB pixmap), and forcing the same signature would
/// either waste a pixmap allocation on every call or muddy the
/// rasterisation contract.
///
/// <para>
/// Implementations: <see cref="LightGbmLayoutAnalyzer"/> in this package.
/// Future text-only analyzers (e.g. a heuristic-only fallback for PDFs
/// without text layers, or per-region transformer models) would slot in
/// here without touching the vision-model interface used by PP-DocLayout
/// and Heron in <c>Core.Analysis</c>.
/// </para>
///
/// <para>
/// <see cref="LayoutModelCapabilities.InputSize"/> is meaningless for
/// text-only analyzers (no rasterisation step). Implementations
/// conventionally set it to zero; consumers reading
/// <c>Capabilities.InputSize</c> downstream should treat zero as
/// "skip rasterisation" before dispatching to this analyzer.
/// </para>
/// </summary>
public interface ITextLayoutAnalyzer
{
    LayoutModelCapabilities Capabilities { get; }

    /// <summary>
    /// Runs layout analysis on a single page of the supplied PDF bytes.
    /// <paramref name="pageIndex"/> is 0-indexed to match the rest of
    /// Core's PDF interfaces.
    /// </summary>
    PageAnalysis RunAnalysis(byte[] pdfBytes, int pageIndex, CancellationToken ct = default);
}
