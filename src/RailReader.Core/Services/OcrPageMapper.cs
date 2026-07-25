using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Converts an <see cref="OcrPage"/> from the pixmap's pixel space into page-point space
/// and, when the engine transcribed the page, assembles a <see cref="PageText"/> that is
/// indistinguishable to downstream consumers from one produced by a real text layer.
///
/// <para>
/// That equivalence is the point of the whole OCR path: everything that reasons about text
/// — char-clustering line detection, the reading-order text tie-break, table row and cell
/// detection, search, Markdown export, VLM prompt assembly — consumes
/// <see cref="PageText"/>, so synthesising one upgrades all of them at once with no
/// changes of their own.
/// </para>
/// </summary>
internal static class OcrPageMapper
{
    /// <summary>
    /// Maps <paramref name="page"/> into page points. <paramref name="scaleX"/> and
    /// <paramref name="scaleY"/> are points-per-pixel (page size ÷ pixmap size), the same
    /// factors the worker uses for layout blocks.
    /// </summary>
    /// <returns>
    /// The line boxes (always), and a page text assembled from the transcribed lines joined
    /// by newlines — null when the engine reported no text (detection-only, or a page that
    /// turned out to be blank).
    /// </returns>
    internal static (PageText? Text, List<BBox> Lines) ToPageSpace(OcrPage page, float scaleX, float scaleY)
    {
        var lines = new List<BBox>(page.Lines.Count);
        foreach (var line in page.Lines)
            lines.Add(Scale(line.Box, scaleX, scaleY));

        if (!page.HasText) return (null, lines);

        var sb = new System.Text.StringBuilder();
        var charBoxes = new List<CharBox>();

        foreach (var line in page.Lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;

            // Offset this line's char indices into the page-level string being built.
            int baseIndex = sb.Length;
            if (line.Chars is not null)
            {
                foreach (var c in line.Chars)
                {
                    // A char index outside its own line's text would produce a CharBox
                    // pointing at the wrong character (or out of range) once offset, so
                    // drop it rather than corrupt extraction. Geometry without a valid
                    // index is useless to every consumer.
                    if (c.Index < 0 || c.Index >= line.Text.Length) continue;
                    charBoxes.Add(new CharBox(
                        baseIndex + c.Index,
                        c.Left * scaleX, c.Top * scaleY,
                        c.Right * scaleX, c.Bottom * scaleY,
                        c.Angle));
                }
            }

            sb.Append(line.Text);
            sb.Append('\n');
        }

        return (new PageText(sb.ToString(), charBoxes), lines);
    }

    private static BBox Scale(BBox b, float scaleX, float scaleY)
        => new(b.X * scaleX, b.Y * scaleY, b.W * scaleX, b.H * scaleY);
}
