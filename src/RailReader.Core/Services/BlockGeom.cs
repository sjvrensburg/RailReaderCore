using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Shared side-by-side / overlap geometry for layout blocks. Centralises the
/// single geometric definition of a "column" — a block with another block beside
/// it (vertically overlapping, horizontally disjoint) — so that the XY-Cut++
/// reading-order density guard (<see cref="XYCutPlusPlusResolver"/>) and rail-mode
/// chunking (<see cref="RailNav"/>) cannot drift apart on what counts as a column.
/// Tune the definition here, in one place: a change to <see cref="IsSideBySide"/>
/// (e.g. requiring a minimum overlap fraction to ignore a 1pt clip) is then seen
/// by both consumers at once.
/// </summary>
internal static class BlockGeom
{
    /// <summary>True when the two boxes overlap on the X axis.</summary>
    public static bool XOverlap(BBox a, BBox b) =>
        a.X < b.X + b.W && b.X < a.X + a.W;

    /// <summary>True when the two boxes overlap on the Y axis.</summary>
    public static bool YOverlap(BBox a, BBox b) =>
        a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> sit side by side —
    /// vertically overlapping but horizontally disjoint. That is the geometric
    /// signature of two blocks belonging to different columns at the same height,
    /// as opposed to one stacked above the other or a full-width block containing
    /// the other.
    /// </summary>
    public static bool IsSideBySide(BBox a, BBox b) =>
        YOverlap(a, b) && !XOverlap(a, b);

    /// <summary>
    /// True when any two blocks sit side by side — the geometric signature of more
    /// than one column in the region. O(n²).
    /// </summary>
    public static bool AnySideBySide(IReadOnlyList<LayoutBlock> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
            for (int j = i + 1; j < blocks.Count; j++)
                if (IsSideBySide(blocks[i].BBox, blocks[j].BBox))
                    return true;
        return false;
    }

    /// <summary>
    /// For each block, whether some other block in <paramref name="blocks"/> sits
    /// beside it (vertically overlapping, horizontally disjoint). A set bit marks a
    /// block that belongs to a real column — content exists in another column at
    /// the same height — as opposed to a full-width block or a lone narrow heading.
    /// O(n²).
    /// </summary>
    public static bool[] MarkColumnBlocks(IReadOnlyList<LayoutBlock> blocks)
    {
        var isColumn = new bool[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
            for (int j = i + 1; j < blocks.Count; j++)
                if (IsSideBySide(blocks[i].BBox, blocks[j].BBox))
                {
                    isColumn[i] = true;
                    isColumn[j] = true;
                }
        return isColumn;
    }
}
