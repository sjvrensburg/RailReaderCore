using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Shared side-by-side / overlap / column-band geometry for layout blocks.
/// Centralises the geometric definition of a "column" so that the XY-Cut++
/// reading-order density guard (<see cref="XYCutPlusPlusResolver"/>) and rail-mode
/// chunking (<see cref="RailNav"/>) cannot drift apart on what counts as a column.
/// Tune the definitions here, in one place.
///
/// <para>
/// Two levels of "column" live here. <see cref="IsSideBySide"/> /
/// <see cref="AnySideBySide"/> are the cheap pairwise signal — "is anything beside
/// this block?" — used by the resolver's density guard. <see cref="MarkColumnBlocks"/>
/// is the chunking signal: the pairwise floor OR-ed with additive column-band
/// detection that also catches staggered columns. It is deliberately additive (it
/// can only add chunk boundaries, never remove one) so that improving staggered
/// detection can never regress the across-the-gutter framing the barrier prevents.
/// </para>
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
    /// Minimum width (page points) of a vertical whitespace gap for it to count as
    /// a column gutter. Mirrors <see cref="XYCutPlusPlusResolver.MinColumnGutterPoints"/>
    /// — journal column gutters run as tight as 8–11pt — kept independent so the two
    /// detectors can be tuned for their (different) jobs without surprise coupling.
    /// </summary>
    public const float ColumnGutterPoints = 7f;

    /// <summary>
    /// A block at or above this fraction of the page width is treated as a
    /// near-full-width spanner (title, abstract, full-width figure / divider) and is
    /// excluded from gutter detection in <see cref="MarkColumnBlocks"/>: such a block
    /// straddles the column gutter and, left in, would hide it — defeating column
    /// detection on every page that carries a title above its columns. Excluding it
    /// is sound because a full-width block can never itself be a single-column block.
    /// Deliberately distinct from <see cref="RailNav.ChunkSpannerWidthFraction"/>
    /// (which governs the chunk-merge barrier, a separate decision): this one only
    /// gates which blocks participate in gutter detection.
    /// </summary>
    public const float ColumnBandSpannerWidthFraction = 0.55f;

    /// <summary>
    /// A column band must be at least this fraction of the region width — rejects
    /// sliver bands. Mirrors <see cref="XYCutPlusPlusResolver.MinColumnWidthFraction"/>.
    /// </summary>
    public const float MinColumnBandWidthFraction = 0.15f;

    /// <summary>
    /// A column band's width must be at least this fraction of the <i>widest</i>
    /// band's width. This is the discriminator that separates a genuine second
    /// column (comparable in width to the first) from an incidental narrow
    /// side-float — a caption / footnote / margin note beside the body — which is
    /// much narrower than the body band and so fails this balance test. Without it,
    /// a tall-enough float beside the body would be mis-flagged as a column and a
    /// title above the body wrongly barred from chunking with it.
    /// </summary>
    public const float ColumnBandBalanceFraction = 0.5f;

    /// <summary>
    /// A column band's content must cover at least this fraction of the region
    /// height — a near-empty band is not a column. Mirrors
    /// <see cref="XYCutPlusPlusResolver.MinColumnCoverageFraction"/>.
    /// </summary>
    public const float MinColumnBandCoverageFraction = 0.15f;

    /// <summary>
    /// For each block, whether it belongs to a real column — content exists in
    /// another column, so framing a full-width spanner together with it would drag
    /// the camera across the gutter. A set bit is the signal the rail-mode chunk
    /// barrier (<see cref="RailNav"/>) uses to refuse to merge a spanner with a
    /// column.
    ///
    /// <para>
    /// Two signals are <b>OR</b>-ed:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Pairwise floor (robust).</b> A block with an explicit y-overlapping,
    /// x-disjoint neighbour. This is the load-bearing signal and is never cleared.
    /// </description></item>
    /// <item><description>
    /// <b>Column-band detection (additive).</b> Flags <i>staggered</i> columns the
    /// pairwise test misses — a column whose body vertically overlaps nothing in the
    /// other column because it sits higher or lower (see <see cref="AddColumnBandFlags"/>).
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>The OR is deliberate and load-bearing.</b> Band detection only ever
    /// <i>adds</i> flags, so this method can only make the chunk barrier fire
    /// <i>more</i> — adding a chunk boundary — never fewer. Only <i>removing</i> a
    /// boundary can let a spanner frame a real column across the gutter (the bug the
    /// barrier exists to prevent), so an additive-only signal cannot regress framing.
    /// Band detection's one fragility — a wide block bridging the gutter hides it, so
    /// the whole page reads as single-column — therefore costs at worst a <i>missed</i>
    /// staggered-column fix (we fall back to the pairwise floor), never a misframe.
    /// </para>
    ///
    /// <para>
    /// <b>Known limitation (intentional).</b> An incidental narrow side-float
    /// (a caption / footnote / margin note beside the body on an otherwise
    /// single-column page) is flagged by the pairwise floor, so a title above the
    /// body is split from it into a separate chunk. That is a benign over-segmentation
    /// (an extra chunk boundary, both framed at single-column width), not a misframe,
    /// and is left unfixed on purpose: distinguishing it from a genuine column whose
    /// neighbour happens to be short requires page-/region-level column knowledge,
    /// and any attempt to clear the flag risks the across-the-gutter regression above.
    /// </para>
    /// </summary>
    public static bool[] MarkColumnBlocks(IReadOnlyList<LayoutBlock> blocks, float pageWidth)
    {
        int n = blocks.Count;
        var isColumn = new bool[n];
        if (n < 2) return isColumn;

        // (1) Pairwise floor: an explicit y-overlapping, x-disjoint neighbour.
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (IsSideBySide(blocks[i].BBox, blocks[j].BBox))
                {
                    isColumn[i] = true;
                    isColumn[j] = true;
                }

        // (2) Additive band detection for staggered columns. OR-only — see remarks.
        AddColumnBandFlags(blocks, pageWidth, isColumn);
        return isColumn;
    }

    /// <summary>
    /// Sets (never clears) <paramref name="isColumn"/> bits for blocks that belong to
    /// a genuine column band — a region the page splits into two or more validated
    /// vertical columns separated by whitespace gutters. This catches <b>staggered</b>
    /// columns (a column whose body vertically overlaps nothing in the other column),
    /// which the pairwise <see cref="IsSideBySide"/> test in <see cref="MarkColumnBlocks"/>
    /// cannot see.
    ///
    /// <para>
    /// Method: drop near-full-width spanners (<see cref="ColumnBandSpannerWidthFraction"/>)
    /// — they straddle the gutter and would hide it — then sweep the remaining blocks
    /// left-to-right for non-straddled vertical gutters (<see cref="ColumnGutterPoints"/>),
    /// which partition them into X-bands. A band is a genuine column when it is wide
    /// enough absolutely (<see cref="MinColumnBandWidthFraction"/>), wide enough
    /// relative to the widest band (<see cref="ColumnBandBalanceFraction"/>, which
    /// rejects narrow side-floats), and its content covers enough of the region height
    /// (<see cref="MinColumnBandCoverageFraction"/>). Blocks are flagged only when at
    /// least two genuine columns exist. Because the result is OR-ed into the pairwise
    /// floor, a false negative here (a bridged/hidden gutter) costs only a missed
    /// staggered-column fix, and a false positive only a benign extra chunk boundary.
    /// </para>
    /// </summary>
    private static void AddColumnBandFlags(IReadOnlyList<LayoutBlock> blocks, float pageWidth, bool[] isColumn)
    {
        int n = blocks.Count;

        // Region bounds over all blocks — the content area the page region spans.
        float regLeft = float.MaxValue, regRight = float.MinValue;
        float regTop = float.MaxValue, regBottom = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var bb = blocks[i].BBox;
            if (bb.X < regLeft) regLeft = bb.X;
            if (bb.X + bb.W > regRight) regRight = bb.X + bb.W;
            if (bb.Y < regTop) regTop = bb.Y;
            if (bb.Y + bb.H > regBottom) regBottom = bb.Y + bb.H;
        }
        float regW = regRight - regLeft, regH = regBottom - regTop;
        if (regW <= 0 || regH <= 0) return;

        // Candidates for gutter detection: everything except near-full-width
        // spanners, which straddle the gutter and would hide it. Original indices
        // are kept so flags map back to the input.
        float spannerW = pageWidth > 0 ? ColumnBandSpannerWidthFraction * pageWidth : float.MaxValue;
        var cand = new List<int>(n);
        for (int i = 0; i < n; i++)
            if (blocks[i].BBox.W < spannerW) cand.Add(i);
        if (cand.Count < 2) return;

        // Non-straddled vertical gutters among the candidates (running-max-right
        // sweep): a gap opens a new band when the next block starts beyond every
        // block seen so far. By construction no candidate straddles a gutter.
        cand.Sort((a, b) => blocks[a].BBox.X.CompareTo(blocks[b].BBox.X));
        var gutters = new List<float>(); // X at which each new band begins (ascending)
        float runningMaxRight = blocks[cand[0]].BBox.X + blocks[cand[0]].BBox.W;
        for (int k = 1; k < cand.Count; k++)
        {
            var bb = blocks[cand[k]].BBox;
            if (bb.X - runningMaxRight >= ColumnGutterPoints) gutters.Add(bb.X);
            float right = bb.X + bb.W;
            if (right > runningMaxRight) runningMaxRight = right;
        }
        if (gutters.Count == 0) return;

        // Partition candidates into bands (bandIndex = number of gutters left of the
        // block's X) and track each band's horizontal extent. Every band is non-empty
        // by construction: each gutter is opened at a block's X and that block lands in
        // the band immediately to its right, and band 0 holds the leftmost candidate.
        int bandCount = gutters.Count + 1;
        var bandMembers = new List<int>[bandCount];
        var bandLeft = new float[bandCount];
        var bandRight = new float[bandCount];
        for (int bi = 0; bi < bandCount; bi++) { bandMembers[bi] = []; bandLeft[bi] = float.MaxValue; bandRight[bi] = float.MinValue; }
        foreach (int idx in cand)
        {
            var bb = blocks[idx].BBox;
            int band = 0;
            while (band < gutters.Count && bb.X >= gutters[band]) band++;
            bandMembers[band].Add(idx);
            if (bb.X < bandLeft[band]) bandLeft[band] = bb.X;
            if (bb.X + bb.W > bandRight[band]) bandRight[band] = bb.X + bb.W;
        }

        // The widest band sets the balance reference.
        float maxBandW = 0f;
        for (int bi = 0; bi < bandCount; bi++)
            maxBandW = Math.Max(maxBandW, bandRight[bi] - bandLeft[bi]);

        // A band is a genuine column when it is wide enough absolutely, wide enough
        // relative to the widest band (rejects narrow side-floats), and its content
        // covers enough of the region height (rejects near-empty bands).
        float minW = MinColumnBandWidthFraction * regW;
        float balanceW = ColumnBandBalanceFraction * maxBandW;
        float minCov = MinColumnBandCoverageFraction * regH;
        var genuine = new bool[bandCount];
        int genuineCount = 0;
        for (int bi = 0; bi < bandCount; bi++)
        {
            float w = bandRight[bi] - bandLeft[bi];
            if (w < minW || w < balanceW) continue;
            if (UnionHeight(blocks, bandMembers[bi]) < minCov) continue;
            genuine[bi] = true;
            genuineCount++;
        }
        if (genuineCount < 2) return;

        for (int bi = 0; bi < bandCount; bi++)
            if (genuine[bi])
                foreach (int idx in bandMembers[bi])
                    isColumn[idx] = true; // OR into the pairwise floor
    }

    /// <summary>Total vertical extent covered by the union of the indexed blocks' Y intervals.</summary>
    private static float UnionHeight(IReadOnlyList<LayoutBlock> blocks, List<int> indices)
    {
        var intervals = new List<(float S, float E)>(indices.Count);
        foreach (int i in indices)
        {
            var bb = blocks[i].BBox;
            intervals.Add((bb.Y, bb.Y + bb.H));
        }
        intervals.Sort((a, b) => a.S.CompareTo(b.S));
        float total = 0f, curS = intervals[0].S, curE = intervals[0].E;
        for (int i = 1; i < intervals.Count; i++)
        {
            var (s, e) = intervals[i];
            if (s > curE) { total += curE - curS; curS = s; curE = e; }
            else if (e > curE) curE = e;
        }
        total += curE - curS;
        return total;
    }
}
