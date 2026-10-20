namespace RailReader.Core.Models;

/// <summary>
/// One back/forward navigation-history entry: where a view was, not just which page. Restoring it
/// returns the reader to the same page, magnification and camera position and, when the rail was
/// seated, the same block and line (issue #143).
/// <para>
/// <see cref="Zoom"/> null marks a page-only entry (<see cref="ForPage"/>): restoring it lands on the
/// page with the default landing camera, as history did before 0.69.0. The offsets are only read when
/// <see cref="Zoom"/> is set. They are the anchor page's camera offsets, which in continuous-scroll mode
/// already pin the document-space position (the anchor is <see cref="Page"/>), so no separate
/// document offset is stored.
/// </para>
/// </summary>
/// <param name="Page">The view's current (anchor) page.</param>
/// <param name="Zoom">Camera zoom, or null for a page-only entry.</param>
/// <param name="OffsetX">Camera X offset on <paramref name="Page"/>.</param>
/// <param name="OffsetY">Camera Y offset on <paramref name="Page"/>.</param>
/// <param name="RailBlock">Page-level index (into <see cref="PageAnalysis.Blocks"/>) of the seated
/// rail block, or null when the rail was not active.</param>
/// <param name="RailLine">Line within <paramref name="RailBlock"/>; null when the rail was not active.</param>
public readonly record struct HistoryEntry(
    int Page,
    double? Zoom = null,
    double OffsetX = 0,
    double OffsetY = 0,
    int? RailBlock = null,
    int? RailLine = null)
{
    /// <summary>A page-only entry: restoring it lands on <paramref name="page"/> with the default camera.</summary>
    public static HistoryEntry ForPage(int page) => new(page);
}
