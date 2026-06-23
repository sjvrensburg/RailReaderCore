namespace RailReader.Core.Models;

/// <summary>
/// The post-processing parameters that shape a page's cached <see cref="PageAnalysis"/>.
/// These are stored <b>per-viewport</b> (railreader2#180 decision #3) and the analysis cache is
/// keyed on <c>(page, <see cref="AnalysisParams"/>)</c> rather than the page alone, so the design
/// supports two viewports post-processing the same page differently (e.g. one splits a table into
/// navigable rows/cells while another keeps it as a single block) — see <see cref="DocumentModel"/>'s
/// analysis cache. In the current build the only writer is the document-wide config fan-out, which
/// sets every viewport to the same value; genuine per-viewport divergence awaits an API that sets
/// one view's params independently.
///
/// <para>Value type with structural equality so it can be a dictionary key. The top-level block
/// set (figures/tables/equations/headings and their positions) is invariant across these flags —
/// they only add sub-structure (rows/cells) inside table blocks — so document-wide consumers
/// (the figure/table/equation index, content-fraction, portals) can read <i>any</i> cached
/// variant for a page; only rail / table-cell navigation needs the matching variant.</para>
/// </summary>
public readonly record struct AnalysisParams(bool TableRowReading, bool CellNavigation)
{
    /// <summary>The default params (table-row reading on, cell navigation off) — matches the
    /// historical analyzer defaults and <see cref="AnalysisRequest"/>'s prior positional defaults.</summary>
    public static readonly AnalysisParams Default = new(TableRowReading: true, CellNavigation: false);
}
