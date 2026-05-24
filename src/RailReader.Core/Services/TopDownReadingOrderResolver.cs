using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Sorts blocks top-to-bottom by Y, then left-to-right by X. Default fallback
/// for models that do not emit reading order. Suitable for simple single-column
/// pages; multi-column layouts will need an algorithmic resolver (e.g. XY-cut)
/// supplied by the application.
/// </summary>
public sealed class TopDownReadingOrderResolver : IReadingOrderResolver
{
    public void AssignOrder(IList<LayoutBlock> blocks, double pageWidth, double pageHeight)
    {
        var sorted = blocks
            .OrderBy(b => b.BBox.Y)
            .ThenBy(b => b.BBox.X)
            .ToList();

        blocks.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].Order = i;
            blocks.Add(sorted[i]);
        }
    }
}
