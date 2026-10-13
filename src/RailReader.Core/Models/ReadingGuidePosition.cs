namespace RailReader.Core.Models;

/// <summary>
/// Where the pointer-driven reading guide sits on a page: a line (and, when the pointer is
/// over text, a word) resolved by <see cref="Services.ReadingGuideHitTester"/>. Indices are
/// page-level — <see cref="BlockIndex"/> into <see cref="PageAnalysis.Blocks"/> (the same
/// space as <see cref="Services.RailNav.CurrentNavigableArrayIndex"/>), <see cref="LineIndex"/>
/// into that block's <see cref="LayoutBlock.Lines"/> (0 for a block with no detected lines,
/// which is treated as one line spanning the block), <see cref="WordIndex"/> into
/// <see cref="Services.WordIndex.WordsOf"/> for that line. <see cref="WordIndex"/> and
/// <see cref="WordRect"/> are null when the pointer is outside the block's horizontal extent
/// (hand resting in the margin) or the page has no text layer. Rects are page points in the
/// displayed frame, matching the analysis.
/// </summary>
public record struct ReadingGuidePosition(
    int BlockIndex,
    int LineIndex,
    int? WordIndex,
    RectF LineRect,
    RectF? WordRect);
