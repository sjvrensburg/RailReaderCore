namespace RailReader.Core.Analysis.LightGbm;

/// <summary>
/// One visually rendered text line on a page. Direct analogue of the
/// <c>&lt;text&gt;</c> elements that <c>pdftohtml -xml</c> emits and that
/// huridocs's LightGBM features were trained on.
///
/// <para>Coordinates are in PDF points with origin at the page's top-left
/// (Y-down) — matches the rest of Core (e.g. <c>CharBox</c>). The
/// per-letter Y-up emitted by PdfPig is flipped inside <see cref="LineTokenizer"/>.</para>
/// </summary>
/// <param name="Content">Concatenated letter values in left-to-right order.</param>
/// <param name="Left">Left edge in page-point space.</param>
/// <param name="Top">Top edge (smaller Y).</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge (larger Y).</param>
/// <param name="FontName">Dominant font name across the letters in the line. Empty if unknown.</param>
/// <param name="FontSize">Dominant font point size; 0 if unknown.</param>
internal sealed record LineToken(
    string Content,
    float Left,
    float Top,
    float Right,
    float Bottom,
    string FontName,
    float FontSize)
{
    public float Width  => Right - Left;
    public float Height => Bottom - Top;
}
