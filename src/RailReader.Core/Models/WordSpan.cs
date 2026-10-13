namespace RailReader.Core.Models;

/// <summary>
/// One word of a page's text layer as the reading guide sees it: a maximal run of
/// non-whitespace glyphs on one line, with its page-point bounds. <see cref="Start"/> and
/// <see cref="Length"/> span the word's characters in <see cref="PageText.Text"/> — the run
/// from the first glyph's <see cref="CharBox.Index"/> through the last's, so a glyph that
/// covers several characters (a ligature) is included whole. Built by
/// <see cref="Services.WordIndex"/>.
/// </summary>
public record struct WordSpan(int Start, int Length, RectF Rect);
