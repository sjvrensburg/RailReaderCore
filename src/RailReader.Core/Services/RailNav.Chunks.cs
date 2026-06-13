using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed partial class RailNav
{
    /// <summary>
    /// Maximum vertical gap (page points) between consecutive navigable blocks
    /// for them to belong to the same navigation chunk. Tight paragraph/heading
    /// runs in a column stay together; a column change (no horizontal overlap)
    /// or a section-sized gap starts a new chunk.
    /// </summary>
    public const float ChunkMaxGapPoints = 24f;

    /// <summary>
    /// A block at or above this fraction of the page width is a near-full-width
    /// spanner (title, abstract, full-width figure/divider/equation). On a
    /// multi-column page such a block must not share a chunk with a single
    /// column: it horizontally contains the column, so the
    /// 50%-of-the-narrower overlap test in <see cref="SameChunk"/> would always
    /// pass and drag the column's camera frame across the gutter onto both
    /// columns. Set a touch above XY-Cut++'s <c>MergeBarrierWidthFraction</c>
    /// (0.55) — deliberately not the same value: a chunk spanner must be
    /// unambiguously full-width before it overrides the overlap test.
    /// </summary>
    public const float ChunkSpannerWidthFraction = 0.6f;

    // Navigation chunks: maximal runs of consecutive navigable blocks (in reading
    // order) that read continuously down one column. Rail mode treats a chunk as
    // one gliding unit — framing the camera on the whole run and pausing only at
    // chunk boundaries, not at every block within a column.
    private readonly List<(int Start, int End)> _chunks = []; // inclusive nav-position ranges
    private int[] _chunkOfNav = [];

    /// <summary>Index of the chunk the current block belongs to.</summary>
    public int CurrentChunk =>
        _chunkOfNav.Length == 0 ? 0 : _chunkOfNav[Math.Min(CurrentBlock, _chunkOfNav.Length - 1)];

    /// <summary>Number of navigation chunks on the current page.</summary>
    public int ChunkCount => _chunks.Count;

    private void BuildChunks()
    {
        _chunks.Clear();
        _chunkOfNav = new int[_navigableIndices.Count];
        if (_navigableIndices.Count == 0) return;

        float pageWidth = (float)(_analysis?.PageWidth ?? 0);
        // A block is a "column block" when another navigable block sits beside it
        // (y-overlapping, x-disjoint) — the signature of belonging to one of two+
        // real columns. The spanner barrier fires only against these, so a wide
        // body still chunks with its narrow heading in a single-column region.
        // Skip the O(n^2) scan entirely when pageWidth is unknown: SameChunk's
        // barrier (the only consumer) is gated on pageWidth > 0, so the flags would
        // never be read.
        bool[] isColumnBlock = pageWidth > 0 ? ComputeColumnBlocks() : new bool[_navigableIndices.Count];

        int start = 0;
        for (int i = 1; i < _navigableIndices.Count; i++)
        {
            var prev = _analysis!.Blocks[_navigableIndices[i - 1]].BBox;
            var cur = _analysis!.Blocks[_navigableIndices[i]].BBox;
            if (!SameChunk(prev, cur, pageWidth, isColumnBlock[i - 1], isColumnBlock[i]))
            {
                _chunks.Add((start, i - 1));
                start = i;
            }
        }
        _chunks.Add((start, _navigableIndices.Count - 1));

        for (int c = 0; c < _chunks.Count; c++)
            for (int p = _chunks[c].Start; p <= _chunks[c].End; p++)
                _chunkOfNav[p] = c;
    }

    /// <summary>
    /// For each navigable block, whether some other navigable block sits beside it
    /// — vertically overlapping but horizontally disjoint. That is the geometric
    /// signature of belonging to a real column (content exists in another column at
    /// the same height), as opposed to a full-width block or a lone narrow heading.
    /// </summary>
    private bool[] ComputeColumnBlocks()
    {
        int n = _navigableIndices.Count;
        var isColumn = new bool[n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var a = _analysis!.Blocks[_navigableIndices[i]].BBox;
                var b = _analysis!.Blocks[_navigableIndices[j]].BBox;
                bool yOverlap = a.Y < b.Y + b.H && b.Y < a.Y + a.H;
                bool xOverlap = a.X < b.X + b.W && b.X < a.X + a.W;
                if (yOverlap && !xOverlap) { isColumn[i] = true; isColumn[j] = true; }
            }
        return isColumn;
    }

    /// <summary>
    /// Two consecutive (reading-order) blocks share a chunk when they overlap
    /// horizontally (same column) and the vertical gap between them is small —
    /// except that a near-full-width spanner (title, abstract, full-width figure)
    /// never joins a block that belongs to a real column. The spanner horizontally
    /// contains the column, so the 50%-of-the-narrower overlap test below would
    /// always pass and frame the column across the gutter onto both columns. The
    /// barrier is gated on the narrower block actually being a column block, so a
    /// full-width body still chunks with its narrow heading in a single-column
    /// region (see <see cref="ChunkSpannerWidthFraction"/>).
    /// </summary>
    private static bool SameChunk(BBox prev, BBox cur, float pageWidth, bool prevIsColumn, bool curIsColumn)
    {
        if (pageWidth > 0)
        {
            float spanW = ChunkSpannerWidthFraction * pageWidth;
            bool prevSpanner = prev.W >= spanW, curSpanner = cur.W >= spanW;
            // Exactly one block is a full-width spanner, and the other belongs to a
            // real column → different structural levels; keep them in separate chunks.
            if (prevSpanner != curSpanner && (prevSpanner ? curIsColumn : prevIsColumn))
                return false;
        }

        float ov = Math.Min(prev.X + prev.W, cur.X + cur.W) - Math.Max(prev.X, cur.X);
        float minW = Math.Min(prev.W, cur.W);
        if (minW <= 0 || ov < 0.5f * minW) return false;       // different column
        float gap = cur.Y - (prev.Y + prev.H);
        return gap <= ChunkMaxGapPoints && gap >= -prev.H;      // tight forward gap (overlap allowed)
    }

    /// <summary>
    /// Union horizontal bounds (page points, with 5% margin) of the current
    /// chunk. Used for stable camera framing so crossing block boundaries within
    /// a column doesn't shift the view. Falls back to the single block when no
    /// chunks are built.
    /// </summary>
    private (double Left, double Right, double WidthPx) GetChunkBounds(double zoom)
    {
        if (_chunks.Count == 0 || _chunkOfNav.Length == 0) return GetBlockBounds(zoom);

        var (s, e) = _chunks[CurrentChunk];
        float left = float.MaxValue, right = float.MinValue;
        for (int p = s; p <= e; p++)
        {
            var b = _analysis!.Blocks[_navigableIndices[p]].BBox;
            if (b.X < left) left = b.X;
            if (b.X + b.W > right) right = b.X + b.W;
        }
        double margin = (right - left) * 0.05;
        double l = left - margin, r = right + margin;
        return (l, r, (r - l) * zoom);
    }
}
