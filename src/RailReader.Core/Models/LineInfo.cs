namespace RailReader.Core.Models;

/// <summary>
/// A detected text line within a block. <see cref="Y"/> is the line centre and
/// <see cref="Height"/> its full vertical span (ascenders→descenders), both in
/// page points. <see cref="X"/>/<see cref="Width"/> give the line's horizontal
/// extent so each line is self-describing — required when a navigation chunk
/// concatenates lines from blocks of differing widths, and lets the renderers
/// focus/highlight the line's true extent rather than the whole parent block.
/// </summary>
public record struct LineInfo(float Y, float Height, float X, float Width);
