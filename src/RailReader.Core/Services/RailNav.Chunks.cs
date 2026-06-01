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

        int start = 0;
        for (int i = 1; i < _navigableIndices.Count; i++)
        {
            var prev = _analysis!.Blocks[_navigableIndices[i - 1]].BBox;
            var cur = _analysis!.Blocks[_navigableIndices[i]].BBox;
            if (!SameChunk(prev, cur))
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
    /// Two consecutive (reading-order) blocks share a chunk when they overlap
    /// horizontally (same column) and the vertical gap between them is small.
    /// </summary>
    private static bool SameChunk(BBox prev, BBox cur)
    {
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
