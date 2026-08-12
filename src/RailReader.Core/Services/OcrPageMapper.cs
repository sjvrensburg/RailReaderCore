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
    /// The line boxes (always); a page text assembled from the transcribed lines joined by
    /// newlines — null when the engine reported no text (detection-only, or a page that turned
    /// out to be blank); and the page's skew in radians, 0 when there is no confident estimate.
    /// </returns>
    internal static (PageText? Text, List<BBox> Lines, float Skew) ToPageSpace(OcrPage page, float scaleX, float scaleY)
    {
        float skew = ToPointSpace(page.SkewAngle, scaleX, scaleY);

        var lines = new List<BBox>(page.Lines.Count);
        foreach (var line in page.Lines)
            lines.Add(Deflate(line, scaleX, scaleY));

        if (!page.HasText) return (null, lines, skew);

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

        return (new PageText(sb.ToString(), charBoxes), lines, skew);
    }

    /// <summary>
    /// Scales a line into page points and, where the detector measured a rotated rectangle,
    /// replaces the axis-aligned height with the true one.
    ///
    /// <para>
    /// The substitution is exact rather than approximate: the axis-aligned bound of a rotated
    /// rectangle is centred on that rectangle's own centre, so shrinking the box about its
    /// centre lands the band precisely over the line's ink. What it removes is the
    /// <c>width × sin(angle)</c> term — on a 400&#160;pt line, 1° of skew inflates the height
    /// by ~7&#160;pt, which is enough for neighbouring lines' bands to overlap past
    /// <c>LineDetector.NormalizeLines</c>' half-height merge threshold and fuse a paragraph
    /// into a single rail unit.
    /// </para>
    /// <para>
    /// Width is deliberately left as the axis-aligned extent: its consumer is a horizontal
    /// overlap test against the block, which wants the span in page-X, not along the baseline.
    /// </para>
    /// </summary>
    private static BBox Deflate(OcrLine line, float scaleX, float scaleY)
    {
        var b = line.Box;
        float x = b.X * scaleX, w = b.W * scaleX;
        float centreY = (b.Y + b.H / 2f) * scaleY;
        float h = line.EffectiveHeight * scaleY;
        return new BBox(x, centreY - h / 2f, w, h);
    }

    /// <summary>
    /// Converts a skew angle from pixmap space into page-point space.
    ///
    /// <para>
    /// An angle is not invariant under a non-uniform scale, so the tangent carries the axis
    /// ratio: <c>tan θ_pt = tan θ_px × (scaleY / scaleX)</c>. Today's rasteriser fits a page
    /// with a single scale on both axes, leaving the ratio at 1 to within the integer
    /// truncation of the pixmap's dimensions (&lt;0.15%), so this is very nearly a no-op — it is
    /// applied anyway because it costs two multiplies and because a future non-uniform raster
    /// path would otherwise corrupt the angle silently rather than visibly.
    /// </para>
    /// </summary>
    private static float ToPointSpace(float skewRadians, float scaleX, float scaleY)
    {
        if (skewRadians == 0f || scaleX <= 0f || scaleY <= 0f) return 0f;
        return MathF.Atan(MathF.Tan(skewRadians) * (scaleY / scaleX));
    }
}
