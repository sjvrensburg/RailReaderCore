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
    /// <see cref="LineDetector"/>. Pass <paramref name="tuning"/> to override the raster
    /// thresholds that fallback uses; see <see cref="LineDetectionTuning"/>.
    /// </summary>
    /// <param name="skewTan">
    /// Tangent of the page's text-baseline skew, or 0 for none. Only line grouping consumes
    /// it — block geometry, orientation and every value this method returns stay in the same
    /// axis-aligned page space they were in before.
    /// </param>
    public static void PostProcess(
        List<LayoutBlock> blocks,
        byte[] rgbBytes,
        int imgW,
        int imgH,
        float scaleX,
        float scaleY,
        IReadOnlyList<CharBox>? charBoxes,
        bool tableRowReading = true,
        bool cellNavigation = false,
        IReadOnlyList<BBox>? ocrLines = null,
        PageRulings? rulings = null,
        LineDetectionTuning? tuning = null,
        float skewTan = 0f)
    {
        // Overlap resolution and orientation are deliberately NOT given the skew. The first
        // trims model-produced block boxes, which are axis-aligned bounds of skewed content —
        // shearing that test would change the block set itself, which callers are promised is
        // invariant across post-processing options. The second only ever decides quarter
        // turns, and has far more slack than a few degrees of skew can consume.
        SuppressDuplicateTextBlocks(blocks, charBoxes);
        ResolveVerticalOverlaps(blocks);
        // Orientation before line detection: a sideways block must collapse to one
        // atomic line rather than be shattered by horizontal char clustering.
        OrientationDetector.DetectBlockOrientations(blocks, charBoxes, rgbBytes, imgW, imgH, scaleX, scaleY);
        DetectLinesForBlocks(blocks, rgbBytes, imgW, imgH, scaleX, scaleY, charBoxes, tableRowReading, cellNavigation, ocrLines, rulings, tuning, skewTan);
    }

    /// <summary>Share of a block's characters another block must also hold for the block to count
    /// as its duplicate (see <see cref="SuppressDuplicateTextBlocks"/>).</summary>
    internal const float DuplicateCharCoverage = 0.9f;

    /// <summary>
    /// Roles whose blocks can be suppressed as, or absorb, a duplicate. Prose only: a heading,
    /// caption, equation, algorithm or table carries meaning in its role (heading levels, equation
    /// labels, atomic lines), and a figure or chart overlapping its caption is normal layout, not a
    /// duplicate.
    /// </summary>
    private static bool IsProse(BlockRole role) => role is
        BlockRole.Text or BlockRole.Reference or BlockRole.Footnote or BlockRole.Aside or BlockRole.InlineMath;

    /// <summary>
    /// Removes a prose block when another prose block already holds nearly all its text (#216).
    /// The layout models can return two boxes over the same text that overlap heavily without
    /// either containing the other, e.g. a list box plus a box around its last item that sticks out
    /// a few points, or a reference list plus one of its entries. NMS (IoU) and nested-block
    /// suppression both let that pair through, and <see cref="ResolveVerticalOverlaps"/> leaves the
    /// second box alone when trimming it would leave under 5pt, so every consumer saw the shared
    /// lines twice: rail stepped them twice, and text extraction returned them in both blocks.
    /// <para>Block B is a duplicate of A when at least <see cref="DuplicateCharCoverage"/> of B's
    /// characters (by midpoint) lie inside A and A holds more characters (on a tie, the block read
    /// first stays). Which block wins never depends on reading order, so a small box read before the
    /// big one cannot take the big one with it. No text is lost: if some of B's characters are in no
    /// other block, A grows to cover B, unless growing would take in characters of a third block, in
    /// which case both are kept. A grown block loses its <see cref="LayoutBlock.Quad"/>, which
    /// described the old box.</para>
    /// <para>Needs characters: without a text layer (a scanned page with OCR off) nothing changes.
    /// Blocks must be in reading order; the survivors are renumbered so <c>blocks[i].Order == i</c>
    /// still holds.</para>
    /// </summary>
    internal static void SuppressDuplicateTextBlocks(List<LayoutBlock> blocks, IReadOnlyList<CharBox>? charBoxes)
    {
        if (charBoxes is null || charBoxes.Count == 0 || blocks.Count < 2) return;

        int n = charBoxes.Count;
        var midX = new float[n];
        var midY = new float[n];
        for (int c = 0; c < n; c++)
        {
            midX[c] = (charBoxes[c].Left + charBoxes[c].Right) / 2f;
            midY[c] = (charBoxes[c].Top + charBoxes[c].Bottom) / 2f;
        }

        bool Holds(BBox box, int c)
            => midX[c] >= box.X && midX[c] <= box.X + box.W && midY[c] >= box.Y && midY[c] <= box.Y + box.H;

        List<int> CharsIn(BBox box)
        {
            var list = new List<int>();
            for (int c = 0; c < n; c++) if (Holds(box, c)) list.Add(c);
            return list;
        }

        var chars = new List<int>[blocks.Count];
        for (int i = 0; i < blocks.Count; i++) chars[i] = CharsIn(blocks[i].BBox);

        var removed = new bool[blocks.Count];
        bool any = false;

        // Smallest text first, so a block is judged before it might absorb a neighbour.
        var bySize = Enumerable.Range(0, blocks.Count).OrderBy(i => chars[i].Count).ThenByDescending(i => i).ToArray();
        foreach (int b in bySize)
        {
            if (!IsProse(blocks[b].Role) || chars[b].Count == 0) continue;

            int a = -1;
            for (int j = 0; j < blocks.Count; j++)
            {
                if (j == b || removed[j] || !IsProse(blocks[j].Role)) continue;
                if (chars[j].Count < chars[b].Count || (chars[j].Count == chars[b].Count && j > b)) continue;
                int shared = 0;
                foreach (int c in chars[b]) if (Holds(blocks[j].BBox, c)) shared++;
                if (shared >= DuplicateCharCoverage * chars[b].Count) { a = j; break; }
            }
            if (a < 0) continue;

            // Characters of B that no other surviving block holds would be lost with it.
            bool orphans = false;
            foreach (int c in chars[b])
            {
                bool held = false;
                for (int j = 0; j < blocks.Count && !held; j++)
                    held = j != b && !removed[j] && Holds(blocks[j].BBox, c);
                if (!held) { orphans = true; break; }
            }

            if (orphans)
            {
                var ab = blocks[a].BBox;
                var bb = blocks[b].BBox;
                float x0 = Math.Min(ab.X, bb.X), y0 = Math.Min(ab.Y, bb.Y);
                var union = new BBox(x0, y0,
                    Math.Max(ab.X + ab.W, bb.X + bb.W) - x0, Math.Max(ab.Y + ab.H, bb.Y + bb.H) - y0);
                var grown = CharsIn(union);
                var ownedByAOrB = new HashSet<int>(chars[a]);
                ownedByAOrB.UnionWith(chars[b]);
                bool takesThirdBlocksText = false;
                foreach (int c in grown)
                {
                    if (ownedByAOrB.Contains(c)) continue;
                    for (int j = 0; j < blocks.Count && !takesThirdBlocksText; j++)
                        takesThirdBlocksText = j != a && j != b && !removed[j] && Holds(blocks[j].BBox, c);
                    if (takesThirdBlocksText) break;
                }
                if (takesThirdBlocksText) continue;

                blocks[a].BBox = union;
                blocks[a].Quad = null;
                chars[a] = grown;
            }

            removed[b] = true;
            any = true;
        }

        if (!any) return;
        int w = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (removed[i]) continue;
            blocks[w] = blocks[i];
            blocks[w].Order = w;
            w++;
        }
        blocks.RemoveRange(w, blocks.Count - w);
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
                    Quad = b.Quad,
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
        float scaleX, float scaleY, IReadOnlyList<CharBox>? charBoxes, bool tableRowReading,
        bool cellNavigation, IReadOnlyList<BBox>? ocrLines, PageRulings? rulings, LineDetectionTuning? tuning,
        float skewTan)
    {
        foreach (var block in blocks)
        {
            block.Lines = LineDetector.DetectLines(block, charBoxes, rgbBytes, imgW, imgH, scaleX, scaleY, tableRowReading, cellNavigation, ocrLines, rulings, tuning, skewTan);
            if (block.Lines.Count == 0)
                block.Lines.Add(new LineInfo(block.BBox.Y + block.BBox.H / 2, block.BBox.H,
                    block.BBox.X, block.BBox.W));
        }
    }
}
