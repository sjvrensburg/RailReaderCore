using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Scans the analysis cache and builds an index of all detected
/// figures, tables, and equations, sorted by page then reading order.
/// </summary>
public static class PeekIndexBuilder
{
    public static PeekIndex Build(IReadOnlyDictionary<int, PageAnalysis> cache, int pageCount)
    {
        var figures = new List<PeekEntry>();
        var tables = new List<PeekEntry>();
        var equations = new List<PeekEntry>();

        for (int page = 0; page < pageCount; page++)
        {
            if (!cache.TryGetValue(page, out var analysis)) continue;

            for (int b = 0; b < analysis.Blocks.Count; b++)
            {
                var block = analysis.Blocks[b];
                switch (block.Role)
                {
                    case BlockRole.Figure:
                    case BlockRole.Chart:
                        figures.Add(MakeEntry(page, b, block));
                        break;
                    case BlockRole.Table:
                        tables.Add(MakeEntry(page, b, block));
                        break;
                    case BlockRole.DisplayMath:
                        equations.Add(MakeEntry(page, b, block));
                        break;
                }
            }
        }

        return new PeekIndex(figures, tables, equations, cache.Count, pageCount);
    }

    private static PeekEntry MakeEntry(int page, int blockIndex, LayoutBlock block) =>
        new()
        {
            PageIndex = page,
            BlockIndex = blockIndex,
            Role = block.Role,
            BBox = block.BBox,
        };
}
