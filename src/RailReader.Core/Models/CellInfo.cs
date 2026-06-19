namespace RailReader.Core.Models;

/// <summary>
/// A horizontal cell within a table row — produced when a <see cref="BlockRole.Table"/>
/// block is processed with cell navigation enabled, and exposed via
/// <see cref="LineInfo.Cells"/>. Lets rail mode step a row column-by-column so the reader
/// can follow "Account label …… $1,234" across the whitespace gap at high magnification.
///
/// <para><see cref="X"/>/<see cref="Width"/> are the cell's horizontal extent in page
/// points and are the source of truth for framing/snapping; <see cref="CenterX"/> is the
/// snap target. Cells of a row are listed left-to-right. (Column identity and per-cell text
/// are deliberately not modelled yet — when a consumer needs them they can be added without
/// breaking the read-only contract; for cell text, extract over the cell's X/Width and the
/// row's Y-band.)</para>
/// </summary>
public readonly record struct CellInfo(float X, float Width)
{
    /// <summary>Horizontal centre of the cell, in page points — the cell snap target.</summary>
    public float CenterX => X + Width / 2f;
}
