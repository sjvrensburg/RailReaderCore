using System.Runtime.CompilerServices;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Words of a page's text layer, grouped by the layout line they sit on, for the pointer-driven
/// reading guide (<see cref="ReadingGuideHitTester"/>). Built once per
/// (<see cref="PageAnalysis"/>, <see cref="PageText"/>) pair via <see cref="For"/> and cached
/// against the analysis, so a host asks for it on every pointer move without paying for a rebuild.
///
/// <para><b>Segmentation.</b> A word is a maximal run of glyphs, walked in
/// <see cref="CharBox.Index"/> order, that is broken by (a) a whitespace character anywhere in
/// <see cref="PageText.Text"/> between one glyph and the next — including characters that have no
/// box of their own, which is how most producers emit the space and newline between words —
/// (b) a glyph whose vertical band does not overlap the previous one's (a new line without an
/// intervening whitespace character, e.g. a soft hyphen break), or (c) a glyph that starts to the
/// left of the previous one by more than a glyph height (same thing, for a line break the
/// producer emitted without any whitespace). A glyph covering several characters (a ligature)
/// is one box and stays inside its word. Duplicate glyphs (fake bold) are dropped first via
/// <see cref="PageText.DedupedCharBoxes"/>.</para>
///
/// <para><b>Assignment.</b> Each word goes to the block whose bbox contains the word's centre
/// (smallest such block when they overlap), then to the line of that block whose vertical band
/// contains the centre, else the nearest line by centre. A block with no detected lines is
/// treated as a single line (index 0) spanning the block, as <see cref="RailNav.CurrentLineInfo"/>
/// does. A word whose centre lies in no block is dropped — the guide never highlights text the
/// layout did not claim. Per line, words are sorted by <see cref="RectF.Left"/>.</para>
///
/// <para>Inline maths is just glyphs to this index: a run of operator glyphs with no whitespace
/// becomes one word, a spaced equation several. Acceptable for a place-keeper; a maximum-gap
/// split is a possible refinement.</para>
/// </summary>
public sealed class WordIndex
{
    private static readonly ConditionalWeakTable<PageAnalysis, WordIndex> s_cache = new();
    private static readonly IReadOnlyList<WordSpan> s_empty = Array.Empty<WordSpan>();

    private readonly PageText _text;
    // [block][line] → words sorted by Left. A block with no lines has one entry (the synthetic line).
    private readonly IReadOnlyList<WordSpan>[][] _words;

    private WordIndex(PageText text, IReadOnlyList<WordSpan>[][] words)
    {
        _text = text;
        _words = words;
    }

    /// <summary>The text layer this index was built from.</summary>
    public PageText Text => _text;

    /// <summary>
    /// The index for <paramref name="analysis"/> over <paramref name="text"/>, built on first
    /// request and cached against the analysis instance. A later call with a different
    /// <see cref="PageText"/> instance (the page's text was re-extracted, e.g. OCR landed)
    /// rebuilds and replaces the cached index.
    /// </summary>
    public static WordIndex For(PageAnalysis analysis, PageText text)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(text);
        if (s_cache.TryGetValue(analysis, out var cached) && ReferenceEquals(cached._text, text))
            return cached;
        var built = Build(analysis, text);
        s_cache.AddOrUpdate(analysis, built);
        return built;
    }

    /// <summary>Builds an index without touching the cache (tests, one-off tooling).</summary>
    public static WordIndex Build(PageAnalysis analysis, PageText text)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(text);

        var blocks = analysis.Blocks;
        var perBlock = new List<WordSpan>[blocks.Count][];
        for (int b = 0; b < blocks.Count; b++)
            perBlock[b] = new List<WordSpan>[Math.Max(1, blocks[b].Lines.Count)];

        foreach (var word in Segment(text))
        {
            int b = ContainingBlock(blocks, word.Rect.MidX, word.Rect.MidY);
            if (b < 0) continue;
            int l = LineFor(blocks[b], word.Rect.MidY);
            (perBlock[b][l] ??= []).Add(word);
        }

        var result = new IReadOnlyList<WordSpan>[blocks.Count][];
        for (int b = 0; b < blocks.Count; b++)
        {
            var lines = perBlock[b];
            var ro = new IReadOnlyList<WordSpan>[lines.Length];
            for (int l = 0; l < lines.Length; l++)
            {
                var list = lines[l];
                if (list is null) { ro[l] = s_empty; continue; }
                list.Sort(static (a, c) => a.Rect.Left.CompareTo(c.Rect.Left));
                ro[l] = list;
            }
            result[b] = ro;
        }
        return new WordIndex(text, result);
    }

    /// <summary>
    /// The words on line <paramref name="lineIndex"/> of block <paramref name="blockIndex"/>
    /// (page-level indices), left to right. Empty for an unknown index, a line with no glyphs,
    /// or a block with no text. Line 0 of a block with no detected lines is the whole block.
    /// </summary>
    public IReadOnlyList<WordSpan> WordsOf(int blockIndex, int lineIndex)
    {
        if ((uint)blockIndex >= (uint)_words.Length) return s_empty;
        var lines = _words[blockIndex];
        if ((uint)lineIndex >= (uint)lines.Length) return s_empty;
        return lines[lineIndex];
    }

    /// <summary>The text of a word, straight from the page string.</summary>
    public string TextOf(in WordSpan word)
    {
        int end = Math.Min(_text.Text.Length, word.Start + word.Length);
        int start = Math.Clamp(word.Start, 0, end);
        return _text.Text[start..end];
    }

    // --- segmentation ---

    private static IEnumerable<WordSpan> Segment(PageText text)
    {
        var boxes = text.DedupedCharBoxes;
        if (boxes.Count == 0) yield break;
        var ordered = new List<CharBox>(boxes);
        ordered.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        string s = text.Text;
        int runStart = -1, runEnd = -1;
        float left = 0, top = 0, right = 0, bottom = 0;
        CharBox prev = default;
        bool open = false;

        foreach (var box in ordered)
        {
            if ((uint)box.Index >= (uint)s.Length) continue;
            bool glyphIsSpace = char.IsWhiteSpace(s[box.Index]);

            if (open)
            {
                bool breakHere = glyphIsSpace
                    || HasWhitespaceBetween(s, prev.Index, box.Index)
                    || !SameBand(prev, box)
                    || box.Left < prev.Left - Math.Max(prev.Bottom - prev.Top, box.Bottom - box.Top);
                if (breakHere)
                {
                    yield return new WordSpan(runStart, runEnd - runStart + 1, new RectF(left, top, right, bottom));
                    open = false;
                }
            }

            if (glyphIsSpace) { prev = box; continue; }

            if (!open)
            {
                open = true;
                runStart = box.Index;
                left = box.Left; top = box.Top; right = box.Right; bottom = box.Bottom;
            }
            else
            {
                left = Math.Min(left, box.Left); top = Math.Min(top, box.Top);
                right = Math.Max(right, box.Right); bottom = Math.Max(bottom, box.Bottom);
            }
            runEnd = box.Index;
            prev = box;
        }

        if (open)
            yield return new WordSpan(runStart, runEnd - runStart + 1, new RectF(left, top, right, bottom));
    }

    // True when any character strictly between the two glyph indices is whitespace — the
    // space/newline most producers emit without a box of its own.
    private static bool HasWhitespaceBetween(string s, int prevIndex, int index)
    {
        for (int i = prevIndex + 1; i < index && i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) return true;
        return false;
    }

    // Two glyphs share a line when their vertical extents overlap by at least half the shorter one.
    private static bool SameBand(in CharBox a, in CharBox b)
    {
        float overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        float shorter = Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
        return overlap >= 0.5f * shorter;
    }

    // --- assignment ---

    private static int ContainingBlock(List<LayoutBlock> blocks, float x, float y)
    {
        int best = -1;
        float bestArea = float.MaxValue;
        for (int i = 0; i < blocks.Count; i++)
        {
            var bb = blocks[i].BBox;
            if (x < bb.X || x > bb.X + bb.W || y < bb.Y || y > bb.Y + bb.H) continue;
            float area = bb.W * bb.H;
            if (area < bestArea) { bestArea = area; best = i; }
        }
        return best;
    }

    private static int LineFor(LayoutBlock block, float y)
    {
        var lines = block.Lines;
        if (lines.Count == 0) return 0;
        int nearest = 0;
        float nearestDist = float.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            float half = l.Height / 2f;
            if (y >= l.Y - half && y <= l.Y + half) return i;
            float d = Math.Abs(y - l.Y);
            if (d < nearestDist) { nearestDist = d; nearest = i; }
        }
        return nearest;
    }
}
