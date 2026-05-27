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
/// Column-aware recursive XY-cut reading-order resolver. Pure geometry — no
/// model, no IO. Designed for the common academic-paper layouts that a
/// naive top-down Y-then-X sort mis-orders: two- and three-column pages,
/// full-width titles and figures interleaved with column text, and
/// page-bottom footnotes.
///
/// <para>
/// Inspired by Liu, Li &amp; Wei (2025), "XY-Cut++: Advanced Layout Ordering via
/// Hierarchical Mask Mechanism on a Novel Benchmark" (arXiv:2504.10258). The
/// paper's full pipeline combines geometric pre-masking, multi-granularity
/// segmentation, and cross-modal (label-aware) matching; this implementation
/// adopts the geometric kernel only — the two refinements over the classical
/// Nagy &amp; Seth (1984) XY-cut that buy most of the accuracy on column layouts:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Vertical cuts are preferred over horizontal cuts</b> whenever a valid
/// column gutter exists (width ≥ <see cref="MinColumnGutterPoints"/>). Classical
/// XY-cut picks whichever projection-gap is widest, which on dense academic
/// pages can pick a paragraph break inside a column ahead of the column
/// gutter and produce a Z-pattern read order across columns.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Full-width spanning blocks are handled implicitly via the straddler
/// check</b>: a candidate vertical cut at X is only valid if no block's
/// <c>[xmin, xmax]</c> straddles X. So a full-width title or figure naturally
/// blocks the page-level vertical cut, forcing a horizontal cut above or
/// below the spanner first. The columns are then recovered inside each
/// resulting sub-region. This produces the same observable output as the
/// paper's explicit "pre-mask" stage without the additional bookkeeping —
/// the trade-off is that we cannot lift a spanner that geometrically
/// overlaps a column (e.g. a figure inset that mid-page-clips into a column);
/// such cases fall through to the top-down leaf and may mis-order locally.
/// Acceptable for the academic-paper layouts this codebase primarily targets.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// Operates on <see cref="LayoutBlock.BBox"/> coordinates as-is — these are
/// in PDF page points with origin top-left (per the Core convention).
/// </para>
/// </summary>
public sealed class XYCutPlusPlusResolver : IReadingOrderResolver
{
    /// <summary>
    /// Minimum width (in page points) for a vertical gap to qualify as a
    /// column gutter. Academic-paper gutters are typically 20–50pt; setting
    /// this floor prevents tiny rendering-noise gaps inside a single column
    /// from being mistaken for a column boundary.
    /// </summary>
    public const float MinColumnGutterPoints = 12f;

    /// <summary>
    /// Minimum height (in page points) for a horizontal gap to qualify as a
    /// cut. Smaller gaps usually correspond to leading between adjacent text
    /// lines inside a paragraph and should not be cut at this level.
    /// </summary>
    public const float MinHorizontalGapPoints = 4f;

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

    public void AssignOrder(IList<LayoutBlock> blocks, double pageWidth, double pageHeight)
    {
        if (blocks.Count == 0) return;
        if (blocks.Count == 1)
        {
            blocks[0].Order = 0;
            return;
        }

        var working = blocks.ToList();
        var ordered = new List<LayoutBlock>(working.Count);
        Cut(working, ordered);

        blocks.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
            blocks.Add(ordered[i]);
        }
    }

    /// <summary>
    /// Recursive cut. Appends blocks in reading order to <paramref name="output"/>.
    /// </summary>
    private static void Cut(List<LayoutBlock> blocks, List<LayoutBlock> output)
    {
        if (blocks.Count == 0) return;
        if (blocks.Count == 1)
        {
            output.Add(blocks[0]);
            return;
        }

        // Find the widest valid vertical (column) gutter. A vertical cut at X
        // is valid iff no block's [xmin, xmax] strictly straddles X.
        var vCut = FindWidestVerticalGap(blocks);

        if (vCut is { Width: >= MinColumnGutterPoints } v)
        {
            float splitX = v.Mid;
            var left = new List<LayoutBlock>(blocks.Count);
            var right = new List<LayoutBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                // A block whose right edge is <= splitX is in left half; otherwise right.
                // (No block can straddle by construction.)
                if (b.BBox.X + b.BBox.W <= splitX) left.Add(b);
                else right.Add(b);
            }
            Cut(left, output);
            Cut(right, output);
            return;
        }

        // No column gutter: try a horizontal cut. The widest valid horizontal
        // gap that no block straddles in Y separates the page into top/bottom
        // sub-regions (e.g. title-above-columns, columns-above-footer).
        var hCut = FindWidestHorizontalGap(blocks);
        if (hCut is { Height: >= MinHorizontalGapPoints } h)
        {
            float splitY = h.Mid;
            var top = new List<LayoutBlock>(blocks.Count);
            var bottom = new List<LayoutBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                if (b.BBox.Y + b.BBox.H <= splitY) top.Add(b);
                else bottom.Add(b);
            }
            Cut(top, output);
            Cut(bottom, output);
            return;
        }

        // Base case: no further valid cuts. Fall back to top-down within this
        // region. This is the leaf of the recursion — typically a single
        // column of paragraphs.
        foreach (var b in blocks.OrderBy(b => b.BBox.Y).ThenBy(b => b.BBox.X))
            output.Add(b);
    }

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

    /// <summary>
    /// Returns the widest vertical strip <c>[Xa, Xb]</c> such that no block
    /// in <paramref name="blocks"/> has <c>xmin &lt; Xa</c> AND <c>xmax &gt; Xa</c>
    /// for any X in the strip (i.e. no block straddles the strip). Implemented
    /// by sorting blocks by xmin and sweeping while tracking the running max
    /// of xmax seen so far; a gap appears when the next block's xmin exceeds
    /// that running max. Returns null if no such strip exists.
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
    /// Mirror of <see cref="FindWidestVerticalGap"/> on the Y axis. Returns the
    /// widest horizontal strip with no straddling block.
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
