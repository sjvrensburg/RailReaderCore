using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Reading direction for <see cref="XYCutPlusPlusResolver"/>. Only
/// <see cref="LeftToRightTopToBottom"/> is implemented today — the other
/// values are placeholders for future CJK / Arabic support.
/// </summary>
public enum ReadingDirection
{
    /// <summary>Latin-script default: columns read left→right, lines top→bottom.</summary>
    LeftToRightTopToBottom,

    /// <summary>Arabic / Hebrew: columns read right→left, lines top→bottom.</summary>
    RightToLeftTopToBottom,

    /// <summary>Traditional CJK vertical: columns read top→bottom, columns advance right→left.</summary>
    TopToBottomRightToLeft,
}

/// <summary>
/// Column-aware recursive XY-cut reading-order resolver. Pure geometry (plus an
/// optional PDF-text-layer tie-break) — no model, no IO. Designed for the
/// academic-paper layouts that a naive top-down Y-then-X sort mis-orders:
/// multi-column pages, full-width titles and figures interleaved with column
/// text, page-bottom footnotes, and — the cases that break in practice —
/// <b>margin/side notes and footnotes embedded in complex multi-column layouts</b>.
///
/// <para>
/// Implements the three stages of Liu, Li &amp; Wei (2025), "XY-Cut++: Advanced
/// Layout Ordering via Hierarchical Mask Mechanism on a Novel Benchmark"
/// (arXiv:2504.10258), adapted to this codebase's needs:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Pre-mask (Phase 1).</b> Blocks that would corrupt a clean recursive cut —
/// margin notes / asides (narrow blocks beside a much wider body column) and
/// "clipping" spanners (a figure/table that geometrically overlaps a column
/// rather than dividing the page cleanly) — are lifted out before cutting.
/// Full-width blocks that <i>divide</i> the page (a title above the columns, a
/// footnote band below them, a full-width figure between top and bottom column
/// bands) are deliberately <b>not</b> masked: the straddler-aware cut already
/// orders those correctly, and masking them would lose their load-bearing role
/// as horizontal dividers.
/// </description></item>
/// <item><description>
/// <b>Density-aware cut (Phase 2).</b> The recursive kernel prefers a column
/// (vertical) cut over a horizontal one whenever a valid gutter exists — the
/// key fix over classical widest-gap XY-cut, which on dense pages can pick a
/// paragraph break inside a column ahead of the column gutter and produce a
/// Z-pattern. When no qualifying vertical gutter exists, a content-density
/// estimate guards against over-cutting a single dense column into spurious
/// horizontal slivers.
/// </description></item>
/// <item><description>
/// <b>Cross-modal re-insertion (Phase 3).</b> Masked blocks are re-inserted into
/// the ordered body stream by a combination of geometric proximity (nearest
/// already-ordered body block) and <see cref="BlockRole"/> priority (titles to
/// the front of their neighbourhood, footnotes/references to the back).
/// </description></item>
/// </list>
///
/// <para>
/// <b>Text-layer tie-break (optional).</b> When the caller supplies the PDF text
/// layer (<c>charBoxes</c>), leaf regions that geometry cannot disambiguate are
/// ordered by each block's median character content-stream index — the order in
/// which the characters appear in the PDF, which is usually reading order for
/// born-digital documents. Absent a text layer (scanned PDFs) this falls back to
/// the geometric Y-then-X leaf sort.
/// </para>
///
/// <para>
/// Operates on <see cref="LayoutBlock.BBox"/> coordinates as-is — PDF page points
/// with origin top-left (per the Core convention).
/// </para>
/// </summary>
public sealed class XYCutPlusPlusResolver : IReadingOrderResolver
{
    /// <summary>
    /// Minimum width (in page points) for a vertical gap to qualify as a
    /// column gutter. Many journals set column gutters as tight as 8–11pt, so
    /// this floor is deliberately low; the false positives a low floor would
    /// otherwise admit (ragged-text gaps, slivers) are rejected downstream by
    /// <see cref="MinColumnCoverageFraction"/> / <see cref="MinColumnWidthFraction"/>
    /// validation, which makes the floor safe to lower. Rail-mode chunking has its
    /// own twin, <see cref="BlockGeom.ColumnGutterPoints"/> (same value today, tuned
    /// independently — see that field's remarks).
    /// </summary>
    public const float MinColumnGutterPoints = 7f;

    /// <summary>
    /// Minimum height (in page points) for a horizontal gap to qualify as a
    /// cut. Smaller gaps usually correspond to leading between adjacent text
    /// lines inside a paragraph and should not be cut at this level.
    /// </summary>
    public const float MinHorizontalGapPoints = 4f;

    /// <summary>
    /// Scale factor (paper's β) applied to the document median block dimension
    /// to decide whether a block is "large" enough to be treated as a
    /// cross-layout spanner candidate during pre-masking.
    /// </summary>
    public const float SpannerSizeFactor = 1.3f;

    /// <summary>
    /// A block counts as a margin note candidate when its width is at most this
    /// fraction of the median body-block width and it sits beside (not above or
    /// below) a wider block.
    /// </summary>
    public const float MarginNoteWidthFraction = 0.5f;

    /// <summary>
    /// Minimum fraction of a region's height that each side of a candidate column
    /// gutter must carry content over. A one-sided phantom gutter (e.g. the
    /// whitespace right of a ragged column) has a near-empty side and is rejected;
    /// a genuine column — even a sparse one with a body at top and a footnote at
    /// bottom — clears this low floor. The stronger guard against slivers is
    /// <see cref="MinColumnWidthFraction"/>. Chunking's twin is
    /// <see cref="BlockGeom.MinColumnBandCoverageFraction"/> (same value, independent).
    /// </summary>
    public const float MinColumnCoverageFraction = 0.15f;

    /// <summary>
    /// Minimum width of each side of a column split, as a fraction of the region
    /// width. Rejects sliver "columns" produced by phantom gutters. Chunking's twin
    /// is <see cref="BlockGeom.MinColumnBandWidthFraction"/> (same value, independent).
    /// </summary>
    public const float MinColumnWidthFraction = 0.15f;

    /// <summary>
    /// Maximum vertical gap (in page points) between a heading and the body block
    /// directly below it for the two to be treated as attached during the
    /// heading-attachment pass.
    /// </summary>
    public const float HeadingAttachGapPoints = 36f;

    /// <summary>
    /// Maximum number of reading-order positions a heading may be moved during
    /// heading attachment. Bounds the relocation to a local nudge so a distant
    /// geometric match cannot reorder many intervening blocks.
    /// </summary>
    public const int HeadingAttachMaxShift = 8;

    /// <summary>
    /// Projection-profile gutter: an interior X position counts as a column
    /// gutter only if the fraction of the region height covered by content there
    /// is at or below this value. The straddler sweep requires a gutter to be
    /// crossed by <i>no</i> block; the projection profile relaxes that to "almost
    /// no" content, so a thin/short block crossing the gutter (a stray figure,
    /// an equation that pokes across) no longer hides the columns.
    /// </summary>
    public const float ProjectionValleyCoverage = 0.18f;

    /// <summary>
    /// Projection-profile gutter: the columns flanking the valley must each cover
    /// at least this fraction of the region height somewhere, so a mere dip in a
    /// single sparse column is not mistaken for a gutter.
    /// </summary>
    public const float ProjectionFlankCoverage = 0.45f;

    /// <summary>
    /// Maximum vertical gap (page points) between two blocks for them to join the
    /// same super-block during the merge pre-pass. Deliberately tight: consecutive
    /// paragraphs/headings within a column sit a few points apart, whereas a
    /// figure or section break leaves a much larger gap that ends the run.
    /// Validated as the sweet spot on a 1,195-page corpus.
    /// </summary>
    public const float MergeGapPoints = 6f;

    /// <summary>
    /// A block at or above this fraction of the page width is a merge barrier
    /// (kept as its own super-block): a full-width title, abstract, or divider
    /// must not absorb a single column's run.
    /// </summary>
    public const float MergeBarrierWidthFraction = 0.55f;

    /// <summary>
    /// A block whose width is at least this fraction of its <i>region</i> width,
    /// and which vertically overlaps nothing else in the region, is a full-width
    /// horizontal divider (title, figure, caption). When such a block sits at the
    /// top extreme of a region that is about to be column-split, it is peeled into
    /// the reading stream first — a divider straddles the gutter, so the column
    /// split would otherwise assign it to whichever column its centre falls in
    /// (e.g. a figure caption landing mid-stream between the two columns instead
    /// of with the figure above them).
    /// </summary>
    public const float DividerWidthFraction = 0.55f;

    private readonly ReadingDirection _direction;
    private readonly bool _mergeAdjacent;

    public XYCutPlusPlusResolver(
        ReadingDirection direction = ReadingDirection.LeftToRightTopToBottom,
        bool mergeAdjacent = true)
    {
        _mergeAdjacent = mergeAdjacent;
        if (direction != ReadingDirection.LeftToRightTopToBottom)
        {
            // TODO: implement RTL and CJK vertical when needed. The geometric
            // core is direction-agnostic; only the order in which we recurse
            // left/right (and the role of vertical-vs-horizontal cuts in CJK)
            // changes.
            throw new NotSupportedException(
                $"ReadingDirection.{direction} is not implemented. Only LeftToRightTopToBottom is supported.");
        }
        _direction = direction;
    }

    public void AssignOrder(IList<LayoutBlock> blocks, double pageWidth, double pageHeight,
        IReadOnlyList<CharBox>? charBoxes = null)
    {
        if (blocks.Count == 0) return;
        if (blocks.Count == 1)
        {
            blocks[0].Order = 0;
            return;
        }

        // Pre-mask medians are computed from the ORIGINAL blocks (not the
        // super-block unions), so margin-note / spanner thresholds aren't skewed
        // by tall merged runs.
        var all = blocks.ToList();
        float medW = Median(all.Select(b => b.BBox.W));
        float medH = Median(all.Select(b => b.BBox.H));

        var ordered = _mergeAdjacent
            ? OrderViaSuperBlocks(all, pageWidth, pageHeight, charBoxes, medW, medH)
            : OrderBlocks(all, charBoxes, medW, medH);

        blocks.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
            blocks.Add(ordered[i]);
        }
    }

    /// <summary>
    /// Core reading-order: pre-mask → density-aware recursive cut → re-insert
    /// masked → heading attachment. Returns the blocks in reading order (does not
    /// set <see cref="LayoutBlock.Order"/>, does not mutate the input list).
    /// </summary>
    private List<LayoutBlock> OrderBlocks(List<LayoutBlock> input, IReadOnlyList<CharBox>? charBoxes,
        float medianWidth, float medianHeight)
    {
        var masked = new List<LayoutBlock>();
        var body = new List<LayoutBlock>(input.Count);
        PreMask(input, masked, body, medianWidth, medianHeight);

        var ordered = new List<LayoutBlock>(input.Count);
        if (body.Count > 0)
            Cut(body, ordered, charBoxes);

        ReinsertMasked(ordered, masked);
        AttachHeadings(ordered);
        return ordered;
    }

    /// <summary>
    /// Phase 0 (merge): group vertically-adjacent, horizontally-overlapping
    /// same-class blocks into column-run "super-blocks", order the super-blocks
    /// with <see cref="OrderBlocks"/> on their union bounds, then expand each
    /// super-block top-to-bottom. The super-blocks are a transient scaffold —
    /// the returned list is the original elements, reordered. Tall full-height
    /// runs make column structure unambiguous and make within-column order
    /// trivially correct, which is where most reading-order errors lived.
    /// </summary>
    private List<LayoutBlock> OrderViaSuperBlocks(List<LayoutBlock> all, double pageWidth, double pageHeight,
        IReadOnlyList<CharBox>? charBoxes, float medianWidth, float medianHeight)
    {
        var groups = MergeGroups(all, pageWidth);

        // Build one super-block per group: singletons reuse the original block
        // (preserving its role); multi-member runs get a synthetic body block
        // spanning the union. The run is deliberately given Role=Text rather than
        // its leading member's role: a column run's internal order (including a
        // leading heading) is already correct via top-down expansion, and
        // carrying a Heading role would make AttachHeadings act on the whole run —
        // which corpus validation showed shifts common-case ordering for no gain.
        var supers = new List<LayoutBlock>(groups.Count);
        var groupOf = new Dictionary<LayoutBlock, List<LayoutBlock>>(ReferenceEqualityComparer.Instance);
        foreach (var g in groups)
        {
            LayoutBlock super;
            if (g.Count == 1)
            {
                super = g[0];
            }
            else
            {
                float minx = g.Min(b => b.BBox.X), miny = g.Min(b => b.BBox.Y);
                float maxx = g.Max(b => b.BBox.X + b.BBox.W), maxy = g.Max(b => b.BBox.Y + b.BBox.H);
                super = new LayoutBlock { BBox = new BBox(minx, miny, maxx - minx, maxy - miny), Role = BlockRole.Text };
            }
            supers.Add(super);
            groupOf[super] = g;
        }

        var orderedSupers = OrderBlocks(supers, charBoxes, medianWidth, medianHeight);

        var result = new List<LayoutBlock>(all.Count);
        foreach (var s in orderedSupers)
        {
            var g = groupOf[s];
            if (g.Count == 1) result.Add(g[0]);
            // Expand via the same leaf ordering used elsewhere, so a same-row pair
            // inside a run uses the content-stream tie-break, not bare left-to-right.
            else result.AddRange(OrderLeaf(g, charBoxes));
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // Phase 0: super-block merge
    // ---------------------------------------------------------------------

    /// <summary>Reading-thread class; blocks only merge with same-class neighbours.</summary>
    private static int MergeClass(BlockRole r) =>
        r is BlockRole.Footnote or BlockRole.Reference ? 2
        : r is BlockRole.DisplayMath or BlockRole.Algorithm or BlockRole.InlineMath ? 3
        : 1;

    /// <summary>A block that must stay its own super-block (never absorbed into a run).</summary>
    private static bool IsMergeBarrier(LayoutBlock b, double pageWidth) =>
        b.Role is BlockRole.Figure or BlockRole.Table or BlockRole.Chart
            or BlockRole.Header or BlockRole.Footer or BlockRole.PageNumber
        || (pageWidth > 0 && b.BBox.W >= MergeBarrierWidthFraction * pageWidth);

    /// <summary>
    /// Greedily groups blocks into column runs. Sorted top-down, each block joins
    /// the existing run it overlaps most (≥50% of the narrower width, same merge
    /// class, vertical gap ≤ <see cref="MergeGapPoints"/>); otherwise it starts a
    /// new run. Barriers are always singletons. Two side-by-side columns never
    /// merge (no horizontal overlap).
    /// </summary>
    private static List<List<LayoutBlock>> MergeGroups(List<LayoutBlock> blocks, double pageWidth)
    {
        var sorted = blocks.OrderBy(b => b.BBox.Y).ThenBy(b => b.BBox.X).ToList();
        var groups = new List<List<LayoutBlock>>();
        var info = new List<(float L, float R, float Bottom, int Cls)>();

        foreach (var b in sorted)
        {
            float bl = b.BBox.X, br = b.BBox.X + b.BBox.W, bb = b.BBox.Y + b.BBox.H;
            if (IsMergeBarrier(b, pageWidth)) { groups.Add([b]); info.Add((bl, br, bb, -1)); continue; }

            int cls = MergeClass(b.Role);
            int best = -1; float bestFrac = -1f;
            for (int g = 0; g < groups.Count; g++)
            {
                var (gl, gr, gbot, gcls) = info[g];
                if (gcls != cls) continue;
                float ov = Math.Min(gr, br) - Math.Max(gl, bl);
                float minW = Math.Min(gr - gl, b.BBox.W);
                float frac = minW > 0 ? ov / minW : 0f;
                float gap = b.BBox.Y - gbot;
                if (frac >= 0.5f && gap <= MergeGapPoints && gap >= -0.5f * b.BBox.H && frac > bestFrac)
                { bestFrac = frac; best = g; }
            }

            if (best >= 0)
            {
                groups[best].Add(b);
                var (gl, gr, gbot, gcls) = info[best];
                info[best] = (Math.Min(gl, bl), Math.Max(gr, br), Math.Max(gbot, bb), gcls);
            }
            else { groups.Add([b]); info.Add((bl, br, bb, cls)); }
        }
        return groups;
    }

    // ---------------------------------------------------------------------
    // Phase 1: pre-mask
    // ---------------------------------------------------------------------

    /// <summary>
    /// Partitions <paramref name="all"/> into <paramref name="masked"/> (floating
    /// blocks lifted out before cutting) and <paramref name="body"/> (everything
    /// the recursive cut sees). A block is masked when it is page furniture
    /// (running header / footer / page number), a margin note (narrow, sitting
    /// beside a much wider block), or a clipping spanner (a large block that
    /// 2D-overlaps another block rather than cleanly dividing the page).
    /// Full-width dividers — which y-overlap nothing — are left in the body.
    ///
    /// <para>
    /// Furniture is masked because a header/footer/page number positioned across
    /// the column boundary bridges the two columns in the XY-cut sweep and so
    /// suppresses the column gutter entirely — defeating the split for the whole
    /// page. Lifting it out (and re-inserting it at the page extremes) lets the
    /// columns be found.
    /// </para>
    /// </summary>
    private static void PreMask(List<LayoutBlock> all, List<LayoutBlock> masked, List<LayoutBlock> body,
        float medianWidth, float medianHeight)
    {
        foreach (var b in all)
        {
            if (IsFurniture(b.Role)
                || IsMarginNote(b, all, medianWidth)
                || IsClippingSpanner(b, all, medianWidth, medianHeight))
                masked.Add(b);
            else
                body.Add(b);
        }

        // Guard: never mask everything (e.g. a page that is entirely asides).
        if (body.Count == 0)
        {
            body.AddRange(masked);
            masked.Clear();
        }
    }

    private static bool IsMarginNote(LayoutBlock b, List<LayoutBlock> all, float medianWidth)
    {
        bool narrow = b.Role == BlockRole.Aside || b.BBox.W <= medianWidth * MarginNoteWidthFraction;
        if (!narrow) return false;

        // Must sit beside (y-overlap, x-disjoint) a clearly wider block — that is
        // what makes it create a spurious column gutter for the cut.
        foreach (var c in all)
        {
            if (ReferenceEquals(b, c)) continue;
            if (BlockGeom.IsSideBySide(b.BBox, c.BBox) && c.BBox.W > b.BBox.W * 1.5f)
                return true;
        }
        return false;
    }

    private static bool IsClippingSpanner(LayoutBlock b, List<LayoutBlock> all, float medianWidth, float medianHeight)
    {
        bool large = b.BBox.W >= medianWidth * SpannerSizeFactor || b.BBox.H >= medianHeight * SpannerSizeFactor;
        if (!large) return false;

        // A clean divider y-overlaps nothing. A clipping spanner 2D-overlaps at
        // least one other block (it cuts into a column instead of dividing the
        // page), so the recursive cut cannot place it correctly.
        foreach (var c in all)
        {
            if (ReferenceEquals(b, c)) continue;
            if (BlockGeom.XOverlap(b.BBox, c.BBox) && BlockGeom.YOverlap(b.BBox, c.BBox))
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------
    // Phase 2: density-aware recursive cut
    // ---------------------------------------------------------------------

    /// <summary>
    /// Recursive cut. Appends blocks in reading order to <paramref name="output"/>.
    /// </summary>
    private static void Cut(List<LayoutBlock> blocks, List<LayoutBlock> output, IReadOnlyList<CharBox>? charBoxes)
    {
        if (blocks.Count == 0) return;
        if (blocks.Count == 1)
        {
            output.Add(blocks[0]);
            return;
        }

        // Prefer the widest *validated* vertical (column) gutter. A candidate
        // cut at X must (a) be straddled by no block, (b) have content flanking
        // it on both sides over most of the region height, and (c) leave both
        // sides wide enough to be real columns — guarding against phantom
        // gutters from ragged short paragraphs / narrow headings.
        var splitX = FindColumnSplit(blocks);
        if (splitX is float sx)
        {
            // A full-width horizontal divider at the TOP extreme (title / figure /
            // caption) straddles the gutter, so the centre-partition below would
            // assign it to whichever column its centre falls in — stranding e.g. a
            // figure caption between the two columns instead of keeping it with
            // the figure above. Peel it into the stream by its Y position first,
            // then column-split the rest. Scoped to the column-split branch so
            // single-column and figure-grid pages (which never reach here) are
            // untouched. Only the top is peeled: a bottom divider (footnote band)
            // already reads last via the large horizontal gap above it, and
            // peeling it can remove that gap and destabilise the sub-region.
            float regWidth = blocks.Max(b => b.BBox.X + b.BBox.W) - blocks.Min(b => b.BBox.X);
            var topMost = blocks[0];
            foreach (var b in blocks)
                if (b.BBox.Y < topMost.BBox.Y) topMost = b;
            if (IsFullWidthDivider(topMost, blocks, regWidth))
            {
                output.Add(topMost);
                Cut(Except(blocks, topMost), output, charBoxes);
                return;
            }

            // Partition by block centre: for a clean (straddler-validated) split
            // no block crosses sx so this matches edge-based partition exactly;
            // for a projection split a crosser is assigned to its majority side.
            var left = new List<LayoutBlock>(blocks.Count);
            var right = new List<LayoutBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                if (b.BBox.X + b.BBox.W * 0.5f < sx) left.Add(b);
                else right.Add(b);
            }
            // Safety: if centre-partition somehow leaves one side empty (e.g. all
            // centres on one side of a projection split), don't recurse infinitely.
            if (left.Count == 0 || right.Count == 0)
            {
                foreach (var b in OrderLeaf(blocks, charBoxes)) output.Add(b);
                return;
            }
            Cut(left, output, charBoxes);
            Cut(right, output, charBoxes);
            return;
        }

        // No column gutter: try a horizontal cut. Density guard — if the region
        // is a single dense column (its content fills most of its vertical
        // extent) we avoid splitting it into spurious horizontal slivers and
        // fall through to the ordered leaf instead.
        var hCut = FindWidestHorizontalGap(blocks);
        if (hCut is { Height: >= MinHorizontalGapPoints } h && !IsDenseSingleColumn(blocks, h))
        {
            float splitY = h.Mid;
            var top = new List<LayoutBlock>(blocks.Count);
            var bottom = new List<LayoutBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                if (b.BBox.Y + b.BBox.H <= splitY) top.Add(b);
                else bottom.Add(b);
            }
            Cut(top, output, charBoxes);
            Cut(bottom, output, charBoxes);
            return;
        }

        // Leaf: no further valid cuts. Order by text-layer index when available,
        // else top-down then left-to-right.
        foreach (var b in OrderLeaf(blocks, charBoxes))
            output.Add(b);
    }

    /// <summary>
    /// True when the candidate horizontal gap is small relative to the region's
    /// vertical content density — i.e. the region reads as one continuous column
    /// and the gap is just inter-paragraph leading, not a structural break.
    ///
    /// <para>
    /// The guard only applies to an <b>actual single column</b>. If any two blocks
    /// sit side by side (vertically overlapping but horizontally disjoint) the
    /// region is multi-column, and the summed-height density below would
    /// double-count the stacked columns — for two full-height columns it is always
    /// ≳1.3, so the guard would fire on every dense two-column page. Suppressing
    /// the horizontal cut there is harmful: when the column gutter could not be
    /// found (e.g. a full-width figure + caption straddling it at the top of the
    /// page), the only way to recover the columns is to peel that top matter off
    /// with a horizontal cut first and let the sub-regions find their gutter.
    /// Without this guard the region falls through to <see cref="OrderLeaf"/>'s
    /// row-banding, which interleaves the columns. Splitting an honest single
    /// column horizontally is harmless (top-to-bottom order is preserved either
    /// way), so this exclusion only ever helps.
    /// </para>
    /// </summary>
    private static bool IsDenseSingleColumn(List<LayoutBlock> blocks, HGap gap)
    {
        float top = blocks.Min(b => b.BBox.Y);
        float bottom = blocks.Max(b => b.BBox.Y + b.BBox.H);
        float extent = bottom - top;
        if (extent <= 0) return false;

        if (BlockGeom.AnySideBySide(blocks)) return false;

        float covered = blocks.Sum(b => b.BBox.H);
        float density = covered / extent;
        // Dense column (little whitespace) AND the gap is a minor fraction of the
        // extent → treat as paragraph leading, not a cut.
        return density > 0.8f && gap.Height < extent * 0.1f;
    }

    /// <summary>
    /// True when <paramref name="b"/> spans at least <see cref="DividerWidthFraction"/>
    /// of the region width and vertically overlaps no other block in the region —
    /// i.e. it occupies its own horizontal band and therefore divides the region
    /// rather than belonging to a column. See <see cref="DividerWidthFraction"/>.
    /// </summary>
    private static bool IsFullWidthDivider(LayoutBlock b, List<LayoutBlock> blocks, float regWidth)
    {
        if (regWidth <= 0 || b.BBox.W < DividerWidthFraction * regWidth) return false;
        foreach (var c in blocks)
        {
            if (ReferenceEquals(c, b)) continue;
            if (BlockGeom.YOverlap(b.BBox, c.BBox)) return false;
        }
        return true;
    }

    /// <summary>All blocks except <paramref name="exclude"/> (reference equality), order preserved.</summary>
    private static List<LayoutBlock> Except(List<LayoutBlock> blocks, LayoutBlock exclude)
    {
        var rest = new List<LayoutBlock>(blocks.Count - 1);
        foreach (var b in blocks)
            if (!ReferenceEquals(b, exclude)) rest.Add(b);
        return rest;
    }

    // ---------------------------------------------------------------------
    // Phase 3: cross-modal re-insertion
    // ---------------------------------------------------------------------

    /// <summary>
    /// Inserts each masked block into the already-ordered <paramref name="ordered"/>
    /// body. Anchor = the nearest body block by centroid distance; the masked
    /// block is placed immediately after its anchor, except role-leading blocks
    /// (titles/headings) go before their anchor and role-trailing blocks
    /// (footnotes/references/footers) sink to the end of the contiguous run that
    /// shares their anchor's neighbourhood.
    /// </summary>
    private static void ReinsertMasked(List<LayoutBlock> ordered, List<LayoutBlock> masked)
    {
        if (masked.Count == 0) return;

        // All masked blocks — furniture (header/footer/page number) and floating
        // content (margin notes, clipping spanners) alike — are re-anchored next
        // to their nearest already-ordered body block, preserving their spatial
        // position in the reading stream. Role-leading blocks (titles, headings,
        // running headers) go just before that anchor; everything else just
        // after. Furniture is masked only so it cannot bridge the columns during
        // the cut; it is *not* forced to the page extremes, which would diverge
        // from the natural position it occupies on the page.
        foreach (var m in masked.OrderBy(b => b.BBox.Y).ThenBy(b => b.BBox.X))
        {
            if (ordered.Count == 0) { ordered.Add(m); continue; }
            int anchor = NearestBlockIndex(ordered, m);
            int insertAt = LeadsInReadingOrder(m.Role) ? anchor : anchor + 1;
            ordered.Insert(insertAt, m);
        }
    }

    private static bool IsFurniture(BlockRole role) =>
        role is BlockRole.Header or BlockRole.Footer or BlockRole.PageNumber;

    private static int NearestBlockIndex(List<LayoutBlock> ordered, LayoutBlock m)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < ordered.Count; i++)
        {
            float d = CentroidDistanceSq(ordered[i].BBox, m.BBox);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private static bool LeadsInReadingOrder(BlockRole role) =>
        role is BlockRole.Title or BlockRole.Heading or BlockRole.Header;

    /// <summary>
    /// Ensures every heading sits immediately before its body — the block in the
    /// same column directly beneath it within <see cref="HeadingAttachGapPoints"/>.
    /// Guards against a heading being ordered ahead of a paragraph that visually
    /// precedes it (or stranded apart from the text it introduces) when the
    /// content stream or a cut boundary disagrees with the layout.
    /// </summary>
    private static void AttachHeadings(List<LayoutBlock> ordered)
    {
        if (ordered.Count < 2) return;

        // Snapshot the headings up front; we mutate the list as we relocate them.
        var headings = ordered
            .Where(b => b.Role is BlockRole.Heading or BlockRole.Title)
            .ToList();

        foreach (var h in headings)
        {
            var body = FindHeadingBody(ordered, h);
            if (body is null) continue;

            int hIdx = ordered.IndexOf(h);
            int bodyIdx = ordered.IndexOf(body);
            if (hIdx < 0 || bodyIdx < 0 || bodyIdx == hIdx + 1) continue; // already adjacent
            // Only a short local nudge: a far-away geometric match (a reinserted
            // block, or content the cut placed in another region) must not be
            // pulled across many intervening blocks and reorder them.
            if (Math.Abs(bodyIdx - hIdx) > HeadingAttachMaxShift) continue;

            ordered.RemoveAt(hIdx);
            bodyIdx = ordered.IndexOf(body); // recompute after removal
            ordered.Insert(bodyIdx, h);
        }
    }

    /// <summary>
    /// The body block a heading introduces: the nearest block directly below the
    /// heading, in the same column (x-overlapping, similar left edge) and within
    /// <see cref="HeadingAttachGapPoints"/>. Null when no such block exists (e.g.
    /// a trailing heading or a heading above a figure).
    /// </summary>
    private static LayoutBlock? FindHeadingBody(List<LayoutBlock> ordered, LayoutBlock h)
    {
        float hBottom = h.BBox.Y + h.BBox.H;
        LayoutBlock? best = null;
        float bestGap = float.MaxValue;

        foreach (var b in ordered)
        {
            if (ReferenceEquals(b, h)) continue;
            if (b.Role is BlockRole.Heading or BlockRole.Title) continue; // body is not another heading
            if (b.BBox.Y < h.BBox.Y) continue;                            // must be below the heading's top
            if (!BlockGeom.XOverlap(h.BBox, b.BBox)) continue;
            if (Math.Abs(b.BBox.X - h.BBox.X) > h.BBox.W) continue;       // roughly the same column start

            float gap = b.BBox.Y - hBottom;
            if (gap < -h.BBox.H || gap > HeadingAttachGapPoints) continue; // allow slight overlap
            if (gap < bestGap) { bestGap = gap; best = b; }
        }
        return best;
    }

    // ---------------------------------------------------------------------
    // Phase 4: leaf ordering (with optional text-layer tie-break)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Orders a leaf region. <b>Y-primary</b>: blocks are grouped top-down into
    /// rows (a "row" is a run of blocks whose vertical bands overlap), and only
    /// <i>within</i> a row is the order decided by the text layer's content-stream
    /// index (when available) or left-to-right geometry.
    ///
    /// <para>
    /// The text-stream index is deliberately <b>not</b> used as the primary key:
    /// a PDF's content stream is not guaranteed to follow visual order (headings,
    /// drop caps and floats are routinely drawn out of sequence), so sorting a
    /// whole column by stream index can lift a heading above the paragraph that
    /// visually precedes it. Geometry decides vertical order; the text layer only
    /// disambiguates blocks that share a row.
    /// </para>
    /// </summary>
    private static IEnumerable<LayoutBlock> OrderLeaf(List<LayoutBlock> blocks, IReadOnlyList<CharBox>? charBoxes)
    {
        if (blocks.Count <= 1) return blocks;

        var sorted = blocks.OrderBy(b => b.BBox.Y).ToList();
        var rows = new List<List<LayoutBlock>>();
        float rowTop = 0, rowBottom = 0;
        foreach (var b in sorted)
        {
            float top = b.BBox.Y, bottom = b.BBox.Y + b.BBox.H;
            if (rows.Count > 0)
            {
                float overlap = Math.Min(rowBottom, bottom) - Math.Max(rowTop, top);
                float minH = Math.Min(bottom - top, rowBottom - rowTop);
                if (overlap > 0.5f * minH)
                {
                    rows[^1].Add(b);
                    rowTop = Math.Min(rowTop, top);
                    rowBottom = Math.Max(rowBottom, bottom);
                    continue;
                }
            }
            rows.Add([b]);
            rowTop = top;
            rowBottom = bottom;
        }

        var result = new List<LayoutBlock>(blocks.Count);
        foreach (var row in rows)
            result.AddRange(OrderRow(row, charBoxes));
        return result;
    }

    /// <summary>
    /// Orders blocks that share a row: by content-stream index when the text
    /// layer covers every block in the row, else left-to-right.
    /// </summary>
    private static IEnumerable<LayoutBlock> OrderRow(List<LayoutBlock> row, IReadOnlyList<CharBox>? charBoxes)
    {
        if (row.Count <= 1) return row;

        if (charBoxes is { Count: > 0 })
        {
            // Restrict the (page-wide) char list to this row's Y band once, so the
            // per-block median lookup scans a small slice rather than re-scanning
            // every char box for each block in the row.
            float rowTop = row.Min(b => b.BBox.Y);
            float rowBottom = row.Max(b => b.BBox.Y + b.BBox.H);
            var rowChars = new List<CharBox>();
            foreach (var cb in charBoxes)
            {
                float midY = (cb.Top + cb.Bottom) * 0.5f;
                if (midY >= rowTop && midY <= rowBottom) rowChars.Add(cb);
            }

            var keyed = new List<(LayoutBlock Block, float Index)>(row.Count);
            bool allHaveText = true;
            foreach (var b in row)
            {
                float idx = MedianCharIndex(b.BBox, rowChars);
                if (float.IsNaN(idx)) { allHaveText = false; break; }
                keyed.Add((b, idx));
            }
            if (allHaveText)
                return keyed.OrderBy(k => k.Index).Select(k => k.Block);
        }

        return row.OrderBy(b => b.BBox.X);
    }

    /// <summary>
    /// Median content-stream index of the characters whose midpoints fall inside
    /// <paramref name="bbox"/>. Returns NaN when no character matches (e.g. an
    /// image block, or a scanned region with no text layer).
    /// </summary>
    private static float MedianCharIndex(BBox bbox, IReadOnlyList<CharBox> charBoxes)
    {
        float left = bbox.X, top = bbox.Y, right = bbox.X + bbox.W, bottom = bbox.Y + bbox.H;
        var indices = new List<int>();
        foreach (var cb in charBoxes)
        {
            float midX = (cb.Left + cb.Right) * 0.5f;
            float midY = (cb.Top + cb.Bottom) * 0.5f;
            if (midX >= left && midX <= right && midY >= top && midY <= bottom)
                indices.Add(cb.Index);
        }
        if (indices.Count == 0) return float.NaN;
        indices.Sort();
        return indices[indices.Count / 2];
    }

    // ---------------------------------------------------------------------
    // Geometry helpers
    // ---------------------------------------------------------------------

    private readonly record struct VGap(float Min, float Max)
    {
        public float Width => Max - Min;
        public float Mid => (Min + Max) * 0.5f;
    }

    private readonly record struct HGap(float Min, float Max)
    {
        public float Height => Max - Min;
        public float Mid => (Min + Max) * 0.5f;
    }

    private static float CentroidDistanceSq(BBox a, BBox b)
    {
        float dx = (a.X + a.W * 0.5f) - (b.X + b.W * 0.5f);
        float dy = (a.Y + a.H * 0.5f) - (b.Y + b.H * 0.5f);
        return dx * dx + dy * dy;
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0f;
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Finds the X coordinate of the widest valid column split, or null if none.
    /// Candidate gaps are the non-straddled vertical strips (found by the xmin
    /// sweep); the widest one that also passes coverage (<see cref="MinColumnCoverageFraction"/>)
    /// and width (<see cref="MinColumnWidthFraction"/>) validation is returned.
    /// </summary>
    private static float? FindColumnSplit(List<LayoutBlock> blocks)
    {
        if (blocks.Count < 2) return null;

        float regTop = blocks.Min(b => b.BBox.Y);
        float regBottom = blocks.Max(b => b.BBox.Y + b.BBox.H);
        float regLeft = blocks.Min(b => b.BBox.X);
        float regRight = blocks.Max(b => b.BBox.X + b.BBox.W);
        float regH = regBottom - regTop;
        float regW = regRight - regLeft;

        // NonStraddledGutters sorts the box list by X itself, so pass it unsorted.
        var boxes = blocks.Select(b => b.BBox).ToList();
        var gaps = BlockGeom.NonStraddledGutters(boxes).Select(g => new VGap(g.Start, g.End));

        foreach (var g in gaps.OrderByDescending(g => g.Width))
        {
            if (g.Width < MinColumnGutterPoints) break;
            if (IsValidColumnSplit(blocks, g.Mid, regH, regW))
                return g.Mid;
        }

        // Fallback: the straddler sweep found no clean gutter (something crosses
        // the column boundary). Try a vertical-coverage projection profile, which
        // tolerates thin/short crossers.
        return FindProjectionSplit(blocks, regTop, regBottom, regLeft, regRight, regH, regW);
    }

    /// <summary>
    /// Projection-profile column finder. Samples the fraction of the region
    /// height covered by content across X, then looks in the interior for the
    /// lowest-coverage position (a candidate gutter) that is (a) at or below
    /// <see cref="ProjectionValleyCoverage"/>, (b) flanked on both sides by a band
    /// reaching <see cref="ProjectionFlankCoverage"/>, and (c) validated for
    /// balance/width by <see cref="IsValidCenterSplit"/>. Unlike the straddler
    /// sweep this survives a single block crossing the gutter, because one short
    /// crosser contributes little coverage at the gutter X. Returns the split X
    /// or null.
    /// </summary>
    private static float? FindProjectionSplit(List<LayoutBlock> blocks,
        float regTop, float regBottom, float regLeft, float regRight, float regH, float regW)
    {
        if (regH <= 0 || regW <= 0) return null;

        int bins = Math.Clamp((int)regW, 32, 512);
        float binW = regW / bins;
        var cov = new float[bins];
        for (int i = 0; i < bins; i++)
        {
            float x = regLeft + (i + 0.5f) * binW;
            cov[i] = CoverageAtX(blocks, x) / regH;
        }

        // Restrict the search to the interior so we never split off a sliver.
        int margin = Math.Max(1, (int)(bins * MinColumnWidthFraction));
        int lo = margin, hi = bins - margin;
        if (hi - lo < 1) return null;

        int valley = -1;
        float min = float.MaxValue;
        for (int i = lo; i < hi; i++)
            if (cov[i] < min) { min = cov[i]; valley = i; }

        if (valley < 0 || min > ProjectionValleyCoverage) return null;

        // Both flanks must contain a substantial column *within the interior*
        // (the same [lo,hi) band the valley was chosen from). Scanning into the
        // excluded margins could let dense edge content masquerade as a column
        // flanking the gutter when it does not actually sit beside it.
        float leftMax = 0f, rightMax = 0f;
        for (int i = lo; i < valley; i++) leftMax = Math.Max(leftMax, cov[i]);
        for (int i = valley + 1; i < hi; i++) rightMax = Math.Max(rightMax, cov[i]);
        if (leftMax < ProjectionFlankCoverage || rightMax < ProjectionFlankCoverage) return null;

        float splitX = regLeft + (valley + 0.5f) * binW;
        return IsValidCenterSplit(blocks, splitX, regH, regW) ? splitX : (float?)null;
    }

    /// <summary>Union vertical extent (page points) covered by blocks spanning column X.</summary>
    private static float CoverageAtX(List<LayoutBlock> blocks, float x)
    {
        var intervals = new List<(float Start, float End)>();
        foreach (var b in blocks)
            if (b.BBox.X <= x && x <= b.BBox.X + b.BBox.W)
                intervals.Add((b.BBox.Y, b.BBox.Y + b.BBox.H));
        return BlockGeom.UnionLength(intervals);
    }

    /// <summary>
    /// Like <see cref="IsValidColumnSplit"/> but partitions by block centre (so a
    /// block crossing <paramref name="splitX"/> is assigned to the side it mostly
    /// sits on) — used for projection splits where a crosser may exist.
    /// </summary>
    private static bool IsValidCenterSplit(List<LayoutBlock> blocks, float splitX, float regH, float regW)
    {
        var left = blocks.Where(b => b.BBox.X + b.BBox.W * 0.5f < splitX).ToList();
        var right = blocks.Where(b => b.BBox.X + b.BBox.W * 0.5f >= splitX).ToList();
        // A column is a *stack* of blocks; require ≥2 each side so a single
        // side-by-side pair (e.g. two cells of one line) isn't read as columns.
        // The precise straddler path stays permissive; only this fallback does.
        if (left.Count < 2 || right.Count < 2) return false;
        if (regH > 0 && (UnionHeight(left) < MinColumnCoverageFraction * regH
                      || UnionHeight(right) < MinColumnCoverageFraction * regH)) return false;
        if (regW > 0)
        {
            float leftW = left.Max(b => b.BBox.X + b.BBox.W) - left.Min(b => b.BBox.X);
            float rightW = right.Max(b => b.BBox.X + b.BBox.W) - right.Min(b => b.BBox.X);
            if (Math.Min(leftW, rightW) < MinColumnWidthFraction * regW) return false;
        }
        return true;
    }

    /// <summary>
    /// Validates a candidate column split at <paramref name="splitX"/>: both sides
    /// must carry content over at least <see cref="MinColumnCoverageFraction"/> of
    /// the region height (a real gutter is flanked top-to-bottom; a ragged-text
    /// gap is not) and both sides must be at least <see cref="MinColumnWidthFraction"/>
    /// of the region width (rejecting sliver phantom columns).
    /// </summary>
    private static bool IsValidColumnSplit(List<LayoutBlock> blocks, float splitX, float regH, float regW)
    {
        var left = blocks.Where(b => b.BBox.X + b.BBox.W <= splitX).ToList();
        var right = blocks.Where(b => b.BBox.X >= splitX).ToList();
        if (left.Count == 0 || right.Count == 0) return false;

        if (regH > 0)
        {
            if (UnionHeight(left) < MinColumnCoverageFraction * regH) return false;
            if (UnionHeight(right) < MinColumnCoverageFraction * regH) return false;
        }

        if (regW > 0)
        {
            float leftW = left.Max(b => b.BBox.X + b.BBox.W) - left.Min(b => b.BBox.X);
            float rightW = right.Max(b => b.BBox.X + b.BBox.W) - right.Min(b => b.BBox.X);
            if (Math.Min(leftW, rightW) < MinColumnWidthFraction * regW) return false;
        }

        return true;
    }

    /// <summary>Total vertical extent covered by the union of the blocks' Y intervals.</summary>
    private static float UnionHeight(List<LayoutBlock> blocks)
    {
        var intervals = new List<(float Start, float End)>(blocks.Count);
        foreach (var b in blocks) intervals.Add((b.BBox.Y, b.BBox.Y + b.BBox.H));
        return BlockGeom.UnionLength(intervals);
    }

    /// <summary>
    /// Mirror of <see cref="FindWidestVerticalGap"/> on the Y axis.
    /// </summary>
    private static HGap? FindWidestHorizontalGap(List<LayoutBlock> blocks)
    {
        if (blocks.Count < 2) return null;

        var sorted = blocks.OrderBy(b => b.BBox.Y).ToList();
        float runningMaxBottom = sorted[0].BBox.Y + sorted[0].BBox.H;
        HGap? best = null;

        for (int i = 1; i < sorted.Count; i++)
        {
            float ymin = sorted[i].BBox.Y;
            if (ymin > runningMaxBottom)
            {
                var gap = new HGap(runningMaxBottom, ymin);
                if (best is null || gap.Height > best.Value.Height)
                    best = gap;
            }
            float ymax = sorted[i].BBox.Y + sorted[i].BBox.H;
            if (ymax > runningMaxBottom) runningMaxBottom = ymax;
        }

        return best;
    }
}
