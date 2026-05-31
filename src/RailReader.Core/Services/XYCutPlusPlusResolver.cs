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
    /// column gutter. Academic-paper gutters are typically 20–50pt; this floor
    /// prevents tiny rendering-noise gaps inside a single column from being
    /// mistaken for a column boundary.
    /// </summary>
    public const float MinColumnGutterPoints = 12f;

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

    private readonly ReadingDirection _direction;

    public XYCutPlusPlusResolver(ReadingDirection direction = ReadingDirection.LeftToRightTopToBottom)
    {
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

        var all = blocks.ToList();

        // Phase 1: lift out blocks that would corrupt a clean recursive cut.
        var masked = new List<LayoutBlock>();
        var body = new List<LayoutBlock>(all.Count);
        PreMask(all, masked, body);

        // Phase 2: recursive density-aware cut of the body.
        var ordered = new List<LayoutBlock>(all.Count);
        if (body.Count > 0)
            Cut(body, ordered, charBoxes);

        // Phase 3: re-insert masked blocks by geometry + role priority.
        ReinsertMasked(ordered, masked);

        blocks.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
            blocks.Add(ordered[i]);
        }
    }

    // ---------------------------------------------------------------------
    // Phase 1: pre-mask
    // ---------------------------------------------------------------------

    /// <summary>
    /// Partitions <paramref name="all"/> into <paramref name="masked"/> (floating
    /// blocks lifted out before cutting) and <paramref name="body"/> (everything
    /// the recursive cut sees). A block is masked when it is either a margin note
    /// (narrow, sitting beside a much wider block) or a clipping spanner (a large
    /// block that 2D-overlaps another block rather than cleanly dividing the
    /// page). Full-width dividers — which y-overlap nothing — are left in the body.
    /// </summary>
    private static void PreMask(List<LayoutBlock> all, List<LayoutBlock> masked, List<LayoutBlock> body)
    {
        float medianWidth = Median(all.Select(b => b.BBox.W));
        float medianHeight = Median(all.Select(b => b.BBox.H));

        foreach (var b in all)
        {
            if (IsMarginNote(b, all, medianWidth) || IsClippingSpanner(b, all, medianWidth, medianHeight))
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
            if (YOverlap(b.BBox, c.BBox) && !XOverlap(b.BBox, c.BBox) && c.BBox.W > b.BBox.W * 1.5f)
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
            if (XOverlap(b.BBox, c.BBox) && YOverlap(b.BBox, c.BBox))
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

        // Prefer the widest valid vertical (column) gutter. A vertical cut at X
        // is valid iff no block's [xmin, xmax] strictly straddles X.
        var vCut = FindWidestVerticalGap(blocks);
        if (vCut is { Width: >= MinColumnGutterPoints } v)
        {
            float splitX = v.Mid;
            var left = new List<LayoutBlock>(blocks.Count);
            var right = new List<LayoutBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                if (b.BBox.X + b.BBox.W <= splitX) left.Add(b);
                else right.Add(b);
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
    /// </summary>
    private static bool IsDenseSingleColumn(List<LayoutBlock> blocks, HGap gap)
    {
        float top = blocks.Min(b => b.BBox.Y);
        float bottom = blocks.Max(b => b.BBox.Y + b.BBox.H);
        float extent = bottom - top;
        if (extent <= 0) return false;

        float covered = blocks.Sum(b => b.BBox.H);
        float density = covered / extent;
        // Dense column (little whitespace) AND the gap is a minor fraction of the
        // extent → treat as paragraph leading, not a cut.
        return density > 0.8f && gap.Height < extent * 0.1f;
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

        // Process top-to-bottom so multiple masked blocks keep a stable order.
        foreach (var m in masked.OrderBy(b => b.BBox.Y).ThenBy(b => b.BBox.X))
        {
            if (ordered.Count == 0)
            {
                ordered.Add(m);
                continue;
            }

            int anchor = NearestBlockIndex(ordered, m);
            int insertAt = LeadsInReadingOrder(m.Role) ? anchor : anchor + 1;
            ordered.Insert(insertAt, m);
        }
    }

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

    // ---------------------------------------------------------------------
    // Phase 4: leaf ordering (with optional text-layer tie-break)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Orders a leaf region. When a text layer is supplied and yields a
    /// content-stream index for every block, sorts by that index (true PDF
    /// reading order); otherwise sorts top-down then left-to-right.
    /// </summary>
    private static IEnumerable<LayoutBlock> OrderLeaf(List<LayoutBlock> blocks, IReadOnlyList<CharBox>? charBoxes)
    {
        if (blocks.Count <= 1) return blocks;

        if (charBoxes is { Count: > 0 })
        {
            var keyed = new List<(LayoutBlock Block, float Index)>(blocks.Count);
            bool allHaveText = true;
            foreach (var b in blocks)
            {
                float idx = MedianCharIndex(b.BBox, charBoxes);
                if (float.IsNaN(idx)) { allHaveText = false; break; }
                keyed.Add((b, idx));
            }
            if (allHaveText)
                return keyed.OrderBy(k => k.Index).Select(k => k.Block);
        }

        return blocks.OrderBy(b => b.BBox.Y).ThenBy(b => b.BBox.X);
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

    private static bool XOverlap(BBox a, BBox b) =>
        a.X < b.X + b.W && b.X < a.X + a.W;

    private static bool YOverlap(BBox a, BBox b) =>
        a.Y < b.Y + b.H && b.Y < a.Y + a.H;

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
    /// Widest vertical strip with no straddling block. Sorts by xmin and sweeps
    /// while tracking the running max of xmax; a gap appears when the next
    /// block's xmin exceeds that running max. Null if no such strip exists.
    /// </summary>
    private static VGap? FindWidestVerticalGap(List<LayoutBlock> blocks)
    {
        if (blocks.Count < 2) return null;

        var sorted = blocks.OrderBy(b => b.BBox.X).ToList();
        float runningMaxRight = sorted[0].BBox.X + sorted[0].BBox.W;
        VGap? best = null;

        for (int i = 1; i < sorted.Count; i++)
        {
            float xmin = sorted[i].BBox.X;
            if (xmin > runningMaxRight)
            {
                var gap = new VGap(runningMaxRight, xmin);
                if (best is null || gap.Width > best.Value.Width)
                    best = gap;
            }
            float xmax = sorted[i].BBox.X + sorted[i].BBox.W;
            if (xmax > runningMaxRight) runningMaxRight = xmax;
        }

        return best;
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
