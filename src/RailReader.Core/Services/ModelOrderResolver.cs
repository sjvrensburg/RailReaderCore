using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Trusts the analyzer's reading-order hints. Sorts by the existing
/// <see cref="LayoutBlock.Order"/> with Y as a tiebreaker, then renumbers
/// so the result is dense (<c>0..N-1</c>).
///
/// Default pick for models that set <see cref="LayoutModelCapabilities.ProvidesReadingOrder"/>
/// (e.g. PP-DocLayoutV3).
/// </summary>
public sealed class ModelOrderResolver : IReadingOrderResolver
{
    public void AssignOrder(IList<LayoutBlock> blocks, double pageWidth, double pageHeight)
    {
        var sorted = blocks
            .OrderBy(b => b.Order)
            .ThenBy(b => b.BBox.Y)
            .ToList();

        blocks.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].Order = i;
            blocks.Add(sorted[i]);
        }
    }
}
