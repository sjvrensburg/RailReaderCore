namespace RailReader.Core.Models;

/// <summary>
/// One page's OCR output, in the <b>pixel space of the pixmap that was submitted</b>
/// (top-left origin, Y-down). Mapping into page-point space is the caller's job — the
/// analysis worker does it with the same scale factors it uses for layout blocks.
/// </summary>
/// <param name="Lines">
/// Detected text lines, in the engine's own order. Line geometry is always present;
/// <see cref="OcrLine.Text"/> and <see cref="OcrLine.Chars"/> are only populated when the
/// engine ran in <see cref="Services.OcrMode.Full"/>.
/// </param>
public sealed record OcrPage(List<OcrLine> Lines)
{
    /// <summary>No text found (or OCR disabled).</summary>
    public static readonly OcrPage Empty = new([]);

    /// <summary>True when at least one line carries transcribed text.</summary>
    public bool HasText
    {
        get
        {
            foreach (var l in Lines)
                if (!string.IsNullOrEmpty(l.Text)) return true;
            return false;
        }
    }
}

/// <summary>
/// One detected text line. <paramref name="Box"/> is the line's axis-aligned bounds in
/// pixmap pixel space.
/// </summary>
/// <param name="Text">
/// The transcribed line, or null when only detection ran.
/// </param>
/// <param name="Chars">
/// Per-character boxes whose <see cref="CharBox.Index"/> is an offset into
/// <paramref name="Text"/> (not into any page-level string). Null when only detection
/// ran, or when the engine could not localise individual characters.
/// </param>
/// <param name="Confidence">Engine confidence in [0,1]; 0 when not reported.</param>
public sealed record OcrLine(BBox Box, string? Text = null, List<CharBox>? Chars = null, float Confidence = 0f);
