using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Post-processing applied to detected layout blocks after the
/// <see cref="IReadingOrderResolver"/> has assigned ordering: trims vertical
/// overlaps so adjacent blocks don't include each other's content, then runs
/// line detection. Lives in Core (not in <c>Core.Analysis</c>) so the same
/// pipeline applies regardless of which layout model produced the blocks.
/// </summary>
public static class BlockPostProcessor
{
    /// <summary>
    /// Runs overlap resolution and line detection on the given blocks. Blocks
    /// are expected to already be sorted by reading order. The pixmap and
    /// scale factors are needed by the pixel-projection fallback in
    /// <see cref="LineDetector"/>.
    /// </summary>
    public static void PostProcess(
        List<LayoutBlock> blocks,
        byte[] rgbBytes,
        int imgW,
        int imgH,
        float scaleX,
        float scaleY,
        IReadOnlyList<CharBox>? charBoxes,
        bool tableRowReading = true,
        bool cellNavigation = false)
    {
        ResolveVerticalOverlaps(blocks);
        // Orientation before line detection: a sideways block must collapse to one
        // atomic line rather than be shattered by horizontal char clustering.
        OrientationDetector.DetectBlockOrientations(blocks, charBoxes, rgbBytes, imgW, imgH, scaleX, scaleY);
        DetectLinesForBlocks(blocks, rgbBytes, imgW, imgH, scaleX, scaleY, charBoxes, tableRowReading, cellNavigation);
    }

    /// <summary>
    /// Trims vertically overlapping blocks so each block's bounding box covers
    /// only its own content. When two blocks overlap vertically with similar
    /// X ranges, the later block's top is pushed down to the earlier block's
    /// bottom. This prevents line detection from finding text belonging to an
    /// adjacent block. Blocks must be in reading order.
    /// </summary>
    internal static void ResolveVerticalOverlaps(List<LayoutBlock> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var a = blocks[i];
            float aBottom = a.BBox.Y + a.BBox.H;

            for (int j = i + 1; j < blocks.Count; j++)
            {
                var b = blocks[j];
                float bBottom = b.BBox.Y + b.BBox.H;

                float overlapX = Math.Min(a.BBox.X + a.BBox.W, b.BBox.X + b.BBox.W)
                    - Math.Max(a.BBox.X, b.BBox.X);
                float minW = Math.Min(a.BBox.W, b.BBox.W);
                if (overlapX < minW * 0.5f) continue;

                float overlapY = aBottom - b.BBox.Y;
                if (overlapY <= 0) continue;

                float newY = aBottom;
                float newH = bBottom - newY;
                if (newH < 5) continue;

                blocks[j] = new LayoutBlock
                {
                    BBox = new BBox(b.BBox.X, newY, b.BBox.W, newH),
                    Role = b.Role,
                    ClassId = b.ClassId,
                    Confidence = b.Confidence,
                    Order = b.Order,
                    UprightTurns = b.UprightTurns,
                };
            }
        }
    }

    private static void DetectLinesForBlocks(
        List<LayoutBlock> blocks, byte[] rgbBytes, int imgW, int imgH,
        float scaleX, float scaleY, IReadOnlyList<CharBox>? charBoxes, bool tableRowReading, bool cellNavigation)
    {
        foreach (var block in blocks)
        {
            block.Lines = LineDetector.DetectLines(block, charBoxes, rgbBytes, imgW, imgH, scaleX, scaleY, tableRowReading, cellNavigation);
            if (block.Lines.Count == 0)
                block.Lines.Add(new LineInfo(block.BBox.Y + block.BBox.H / 2, block.BBox.H,
                    block.BBox.X, block.BBox.W));
        }
    }
}
