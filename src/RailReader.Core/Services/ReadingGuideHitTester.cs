using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Resolves a page point (the pointer) to the line — and, over text, the word — a pointer-driven
/// reading guide should highlight. One instance per view: it remembers the line it last resolved
/// so the guide behaves like a ruler, not a jitter.
///
/// <para><b>Rules.</b></para>
/// <list type="number">
///   <item><b>Candidate blocks</b> are the eligible blocks (a role in <see cref="NavigableRoles"/>,
///   so page furniture such as headers, footers and page numbers never captures the guide) whose
///   horizontal extent, widened by <see cref="ColumnToleranceFraction"/> of the block's width,
///   contains the point's X. When none does — the pointer is in a margin or a gutter — every
///   eligible block is a candidate. This keeps a two-column page from snapping to the other
///   column's line at the same Y.</item>
///   <item><b>Line with hysteresis.</b> The current line is kept while the point's Y stays inside
///   its band widened by <see cref="DeadZoneFraction"/> of the line's height on each side and its
///   block is still a candidate; otherwise the candidate line whose centre is nearest the Y wins.
///   Moving along a line with an unsteady hand never flickers to a neighbour.</item>
///   <item><b>Word.</b> Inside the block's (widened) horizontal extent, the word of the resolved
///   line that contains the X, else the nearest word on that line — so past a ragged line end the
///   last word stays lit. Outside the block's extent, no word: the hand is resting in the margin
///   and only the line is marked. No text layer (<c>words == null</c>) also means no word.</item>
///   <item><b>Carriage return.</b> When the guide was resting on the last word of its line and the point
///   moves to the line reading order reaches next, it lands on that line's <i>first</i> word rather than
///   the word under the (still far-right) pointer — the natural end-of-line hop, so a reader in point
///   mode doesn't have to sweep back before the next line lights up correctly.</item>
/// </list>
///
/// <para>A block with no detected lines is treated as one line spanning the block (index 0), as
/// <see cref="RailNav.CurrentLineInfo"/> does. Hysteresis state is tied to the analysis instance:
/// a different <see cref="PageAnalysis"/> (another page, a re-analysis) starts fresh. Cost per
/// call is linear in the page's blocks and the candidate blocks' lines plus a binary search over
/// one line's words.</para>
/// </summary>
public sealed class ReadingGuideHitTester
{
    /// <summary>Vertical dead zone around the current line, as a fraction of the line's height
    /// on each side, within which the line is kept. 0 = always nearest line.</summary>
    public float DeadZoneFraction { get; set; } = 0.6f;

    /// <summary>How far outside a block's horizontal extent, as a fraction of its width, the point
    /// still counts as inside it (for candidate selection and for word-vs-margin).</summary>
    public float ColumnToleranceFraction { get; set; } = 0.05f;

    /// <summary>Roles a block must have to carry the guide. Defaults to the rail's navigable set.</summary>
    public IReadOnlySet<BlockRole> NavigableRoles { get; set; } = DefaultRoleSets.Navigable;

    /// <summary>The last resolved position, or null after <see cref="Clear"/> / a miss.</summary>
    public ReadingGuidePosition? Current { get; private set; }

    private PageAnalysis? _analysis;

    /// <summary>
    /// Resolves <paramref name="pageX"/>/<paramref name="pageY"/> (page points, displayed frame)
    /// against <paramref name="analysis"/>. <paramref name="words"/> may be null for a page with
    /// no text layer, or when the host wants line-only behaviour (e.g. while the view is rotated,
    /// since the text layer is in the page's own frame). Returns null — and clears
    /// <see cref="Current"/> — when there is no analysis or no eligible block.
    /// </summary>
    public ReadingGuidePosition? Resolve(float pageX, float pageY, PageAnalysis? analysis, WordIndex? words)
    {
        if (analysis is null || analysis.Blocks.Count == 0) { Clear(); return null; }
        if (!ReferenceEquals(_analysis, analysis)) { Current = null; _analysis = analysis; }

        var blocks = analysis.Blocks;

        // 1. Candidate blocks: eligible + column contains X; else all eligible.
        bool anyInColumn = false;
        for (int b = 0; b < blocks.Count; b++)
            if (Eligible(blocks[b]) && InColumn(blocks[b], pageX)) { anyInColumn = true; break; }

        // 2. Hysteresis: keep the current line while inside its dead-zone band and its block is a candidate.
        if (Current is { } cur && (uint)cur.BlockIndex < (uint)blocks.Count)
        {
            var block = blocks[cur.BlockIndex];
            bool candidate = Eligible(block) && (!anyInColumn || InColumn(block, pageX));
            if (candidate && (uint)cur.LineIndex < (uint)Math.Max(1, block.Lines.Count))
            {
                var line = LineAt(block, cur.LineIndex);
                float half = line.Height / 2f + DeadZoneFraction * line.Height;
                if (pageY >= line.Y - half && pageY <= line.Y + half)
                {
                    Current = Compose(cur.BlockIndex, cur.LineIndex, block, line, pageX, words);
                    return Current;
                }
            }
        }

        // 3. Nearest candidate line by centre.
        int bestBlock = -1, bestLine = -1;
        float bestDist = float.MaxValue;
        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            if (!Eligible(block)) continue;
            if (anyInColumn && !InColumn(block, pageX)) continue;
            int lineCount = Math.Max(1, block.Lines.Count);
            for (int l = 0; l < lineCount; l++)
            {
                var line = LineAt(block, l);
                float d = Math.Abs(pageY - line.Y);
                // Ties (e.g. two blocks whose synthetic lines share a centre): prefer the block
                // whose own extent contains X, then the earlier one in reading order.
                if (d < bestDist || (d == bestDist && bestBlock >= 0
                        && !InColumn(blocks[bestBlock], pageX) && InColumn(block, pageX)))
                {
                    bestDist = d; bestBlock = b; bestLine = l;
                }
            }
        }

        if (bestBlock < 0) { Current = null; return null; }
        bool carriageReturn = Current is { } prev && words is not null
            && prev.WordIndex is { } pw && pw == words.WordsOf(prev.BlockIndex, prev.LineIndex).Count - 1
            && ReadingGuideFlow.IsNextLine(prev, bestBlock, bestLine, analysis, NavigableRoles);
        Current = Compose(bestBlock, bestLine, blocks[bestBlock], LineAt(blocks[bestBlock], bestLine), pageX, words,
            landOnFirstWord: carriageReturn);
        return Current;
    }

    /// <summary>Forgets the current line (and the analysis it belonged to).</summary>
    public void Clear()
    {
        Current = null;
        _analysis = null;
    }

    private bool Eligible(LayoutBlock block) => NavigableRoles.Contains(block.Role);

    private bool InColumn(LayoutBlock block, float x)
    {
        var bb = block.BBox;
        float tol = Math.Max(0f, ColumnToleranceFraction) * bb.W;
        return x >= bb.X - tol && x <= bb.X + bb.W + tol;
    }

    private static LineInfo LineAt(LayoutBlock block, int index)
    {
        if (block.Lines.Count == 0)
            return new LineInfo(block.BBox.Y + block.BBox.H / 2f, block.BBox.H, block.BBox.X, block.BBox.W);
        return block.Lines[Math.Clamp(index, 0, block.Lines.Count - 1)];
    }

    private ReadingGuidePosition Compose(int blockIndex, int lineIndex, LayoutBlock block, LineInfo line,
        float pageX, WordIndex? words, bool landOnFirstWord = false)
    {
        var lineRect = new RectF(line.X, line.Y - line.Height / 2f, line.X + line.Width, line.Y + line.Height / 2f);
        int? wordIndex = null;
        RectF? wordRect = null;
        if (words is not null && InColumn(block, pageX))
        {
            var list = words.WordsOf(blockIndex, lineIndex);
            int w = landOnFirstWord && list.Count > 0 ? 0 : NearestWord(list, pageX);
            if (w >= 0) { wordIndex = w; wordRect = list[w].Rect; }
        }
        return new ReadingGuidePosition(blockIndex, lineIndex, wordIndex, lineRect, wordRect);
    }

    // Index of the word containing x, else the nearest by horizontal distance; -1 when empty.
    private static int NearestWord(IReadOnlyList<WordSpan> words, float x)
    {
        int n = words.Count;
        if (n == 0) return -1;
        // Binary search for the first word whose Left is > x; the candidate is it or its predecessor.
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (words[mid].Rect.Left <= x) lo = mid + 1; else hi = mid;
        }
        int after = lo;            // first word starting right of x (or n)
        int before = lo - 1;       // last word starting at/left of x (or -1)
        if (before >= 0 && x <= words[before].Rect.Right) return before;
        if (before < 0) return after < n ? after : -1;
        if (after >= n) return before;
        float dBefore = x - words[before].Rect.Right;
        float dAfter = words[after].Rect.Left - x;
        return dAfter < dBefore ? after : before;
    }
}
