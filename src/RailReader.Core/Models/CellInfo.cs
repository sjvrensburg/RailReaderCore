namespace RailReader.Core.Models;

/// <summary>
/// A horizontal cell within a table row — produced when a <see cref="BlockRole.Table"/>
/// block is processed with cell navigation enabled, and exposed via
/// <see cref="LineInfo.Cells"/>. Lets rail mode step a row column-by-column so the reader
/// can follow "Account label …… $1,234" across the whitespace gap at high magnification.
///
/// <para><see cref="X"/>/<see cref="Width"/> are the cell's horizontal extent in page
/// points and are the source of truth for framing/snapping; <see cref="CenterX"/> is the
/// snap target. <see cref="ColumnTrack"/> is a best-effort column identity (cells sharing
/// a visual column across rows get the same track) — advisory metadata only, not consumed
/// by cell stepping itself. <see cref="CharStart"/>/<see cref="CharCount"/> are a coarse
/// locator hint into the page's text (smallest glyph index in the cell and the glyph
/// count, not a guaranteed contiguous slice); for exact cell text prefer extracting over
/// the cell's X/Width and the row's Y-band.</para>
/// </summary>
public readonly record struct CellInfo(
    float X, float Width, int ColumnTrack, int CharStart, int CharCount)
{
    /// <summary>Horizontal centre of the cell, in page points — the cell snap target.</summary>
    public float CenterX => X + Width / 2f;
}
