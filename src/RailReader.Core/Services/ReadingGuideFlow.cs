using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Reading-order stepping for the reading guide: the next / previous word (or line) on a page,
/// crossing line and block boundaries, over the eligible (<paramref name="roles"/>) blocks in
/// <see cref="PageAnalysis.Blocks"/> order — which is the page's reading order. The end of a line is
/// simply the next word, so a steered guide flows through a paragraph, into the next block and up to
/// the page end (where every step returns null and the host turns the page).
///
/// <para>A block with no detected lines is one line (index 0) spanning the block, as everywhere else
/// in the guide. A line with no words (no text layer, or nothing under it) is a line-only position
/// (<see cref="ReadingGuidePosition.WordIndex"/> null) and a word step over it moves by line.</para>
/// </summary>
public static class ReadingGuideFlow
{
    /// <summary>The first word of the first eligible block, or null when the page has none.</summary>
    public static ReadingGuidePosition? First(PageAnalysis analysis, WordIndex? words, IReadOnlySet<BlockRole> roles)
    {
        for (int b = 0; b < analysis.Blocks.Count; b++)
            if (roles.Contains(analysis.Blocks[b].Role))
                return At(analysis, words, b, 0, FirstWordOrNull(words, b, 0));
        return null;
    }

    /// <summary>The last word of the last eligible block, or null when the page has none.</summary>
    public static ReadingGuidePosition? Last(PageAnalysis analysis, WordIndex? words, IReadOnlySet<BlockRole> roles)
    {
        for (int b = analysis.Blocks.Count - 1; b >= 0; b--)
            if (roles.Contains(analysis.Blocks[b].Role))
            {
                int line = LineCount(analysis.Blocks[b]) - 1;
                return At(analysis, words, b, line, LastWordOrNull(words, b, line));
            }
        return null;
    }

    /// <summary>
    /// The position for page-level indices, with rects rebuilt from <paramref name="analysis"/> and
    /// <paramref name="words"/>: null when the block or line index no longer exists; a word index
    /// beyond the line's words degrades to a line-only position. The shell uses this to re-read a parked
    /// guide against a replaced analysis instance.
    /// </summary>
    public static ReadingGuidePosition? At(PageAnalysis analysis, WordIndex? words, int block, int line, int? word)
    {
        if ((uint)block >= (uint)analysis.Blocks.Count) return null;
        var b = analysis.Blocks[block];
        if ((uint)line >= (uint)LineCount(b)) return null;
        var li = LineAt(b, line);
        var lineRect = new RectF(li.X, li.Y - li.Height / 2f, li.X + li.Width, li.Y + li.Height / 2f);
        RectF? wordRect = null;
        int? wordIndex = null;
        if (word is { } w && words is not null)
        {
            var list = words.WordsOf(block, line);
            if ((uint)w < (uint)list.Count) { wordIndex = w; wordRect = list[w].Rect; }
        }
        return new ReadingGuidePosition(block, line, wordIndex, lineRect, wordRect);
    }

    /// <summary>
    /// The next (<paramref name="forward"/>) or previous word in reading order. Past the last word of a
    /// line this is the first word of the next line (next block when the line was its block's last);
    /// before the first word it is the previous line's last word. A line-only position (no word) steps
    /// onto the line's first / last word when it has words, else by line. Null at the page end / start.
    /// </summary>
    public static ReadingGuidePosition? StepWord(in ReadingGuidePosition pos, bool forward, PageAnalysis analysis,
        WordIndex? words, IReadOnlySet<BlockRole> roles)
    {
        int count = words?.WordsOf(pos.BlockIndex, pos.LineIndex).Count ?? 0;
        if (count > 0)
        {
            int w = pos.WordIndex ?? (forward ? -1 : count);
            int next = w + (forward ? 1 : -1);
            if (next >= 0 && next < count)
                return At(analysis, words, pos.BlockIndex, pos.LineIndex, next);
        }
        var (nb, nl) = NextLine(pos.BlockIndex, pos.LineIndex, forward, analysis, roles);
        if (nb < 0) return null;
        return At(analysis, words, nb, nl, forward ? FirstWordOrNull(words, nb, nl) : LastWordOrNull(words, nb, nl));
    }

    /// <summary>The first word of the next / previous line in reading order (crossing blocks), or null
    /// at the page end / start.</summary>
    public static ReadingGuidePosition? StepLine(in ReadingGuidePosition pos, bool forward, PageAnalysis analysis,
        WordIndex? words, IReadOnlySet<BlockRole> roles)
    {
        var (nb, nl) = NextLine(pos.BlockIndex, pos.LineIndex, forward, analysis, roles);
        if (nb < 0) return null;
        return At(analysis, words, nb, nl, FirstWordOrNull(words, nb, nl));
    }

    /// <summary>True when <paramref name="next"/> is the line that reading order reaches right after
    /// <paramref name="pos"/>'s line (same block, next line — or the first line of the next eligible block).</summary>
    public static bool IsNextLine(in ReadingGuidePosition pos, int nextBlock, int nextLine, PageAnalysis analysis,
        IReadOnlySet<BlockRole> roles)
    {
        var (nb, nl) = NextLine(pos.BlockIndex, pos.LineIndex, forward: true, analysis, roles);
        return nb == nextBlock && nl == nextLine;
    }

    // (block, line) of the next/previous line in reading order; (-1, -1) at the page boundary.
    private static (int Block, int Line) NextLine(int block, int line, bool forward, PageAnalysis analysis,
        IReadOnlySet<BlockRole> roles)
    {
        var blocks = analysis.Blocks;
        if ((uint)block >= (uint)blocks.Count) return (-1, -1);
        if (forward)
        {
            if (line + 1 < LineCount(blocks[block])) return (block, line + 1);
            for (int b = block + 1; b < blocks.Count; b++)
                if (roles.Contains(blocks[b].Role)) return (b, 0);
        }
        else
        {
            if (line > 0) return (block, line - 1);
            for (int b = block - 1; b >= 0; b--)
                if (roles.Contains(blocks[b].Role)) return (b, LineCount(blocks[b]) - 1);
        }
        return (-1, -1);
    }

    private static int LineCount(LayoutBlock block) => Math.Max(1, block.Lines.Count);

    private static LineInfo LineAt(LayoutBlock block, int index)
        => block.Lines.Count == 0
            ? new LineInfo(block.BBox.Y + block.BBox.H / 2f, block.BBox.H, block.BBox.X, block.BBox.W)
            : block.Lines[index];

    private static int? FirstWordOrNull(WordIndex? words, int block, int line)
        => words is not null && words.WordsOf(block, line).Count > 0 ? 0 : null;

    private static int? LastWordOrNull(WordIndex? words, int block, int line)
    {
        int n = words?.WordsOf(block, line).Count ?? 0;
        return n > 0 ? n - 1 : null;
    }
}
