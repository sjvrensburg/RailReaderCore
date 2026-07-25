namespace RailReader.Core.Models;

/// <summary>
/// One axis-aligned ruling line drawn on a page — a table's column or row separator, a
/// header rule, an underline. <paramref name="Position"/> is the cross-axis coordinate
/// (x for a vertical ruling, y for a horizontal one) and <paramref name="Start"/> …
/// <paramref name="End"/> its extent along its own axis, all in page points in the displayed
/// frame (top-left origin, Y-down) — the same space as <see cref="CharBox"/> and
/// <see cref="BBox"/>.
/// </summary>
public readonly record struct RulingSegment(float Position, float Start, float End)
{
    public float Length => End - Start;
}

/// <summary>
/// The ruling lines found on one page, split by orientation and already merged so that a rule
/// drawn as several segments — or as a thin filled rectangle, which is how many producers draw
/// hairlines — appears once.
///
/// <para>
/// These come from the PDF's own vector content, which makes them exact: a table's column
/// grid recovered from them needs no thresholds, no rasterisation, and no guessing about
/// whether a dark pixel run is a rule or a tall glyph. Only backends that can read page paths
/// produce them (see <see cref="Services.IPdfRulingService"/>); everything else falls back to
/// the raster scan in <see cref="Services.LineDetector.DetectColumnGrid"/>.
/// </para>
/// </summary>
public sealed record PageRulings(List<RulingSegment> Vertical, List<RulingSegment> Horizontal)
{
    public static readonly PageRulings Empty = new([], []);

    public bool IsEmpty => Vertical.Count == 0 && Horizontal.Count == 0;
}
