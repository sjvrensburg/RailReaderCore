namespace RailReader.Core.Models;

/// <summary>
/// Pure geometry helpers for the user-requested <b>view rotation</b> — an extra
/// clockwise quarter-turn count (0–3) applied on top of a page's own /Rotate
/// attribute. All coordinates are page-point space (origin top-left, Y-down).
/// Providers use these to map geometry between the unrotated-view frame and the
/// view-rotated frame; hosts can use them to map cached page-space geometry
/// (e.g. sidecar annotations, stored in the rotation-0 frame) for display.
/// </summary>
public static class ViewRotationMath
{
    /// <summary>Normalises any quarter-turn count into 0–3.</summary>
    public static int Normalize(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    /// <summary>Frame dimensions after rotation (axes swap on odd quarter-turns).</summary>
    public static (double Width, double Height) RotateSize(double width, double height, int quarterTurns)
        => (Normalize(quarterTurns) & 1) == 0 ? (width, height) : (height, width);

    /// <summary>
    /// Rotates a point from a Y-down frame of size (<paramref name="width"/>,
    /// <paramref name="height"/>) by <paramref name="quarterTurns"/> clockwise
    /// quarter-turns into the rotated frame.
    /// </summary>
    public static (float X, float Y) RotatePoint(float x, float y, double width, double height, int quarterTurns)
        => Normalize(quarterTurns) switch
        {
            1 => ((float)(height - y), x),
            2 => ((float)(width - x), (float)(height - y)),
            3 => (y, (float)(width - x)),
            _ => (x, y),
        };

    /// <summary>Inverse of <see cref="RotatePoint"/>: maps a point in the rotated frame
    /// back into the source frame of size (<paramref name="width"/>, <paramref name="height"/>).</summary>
    public static (float X, float Y) UnrotatePoint(float x, float y, double width, double height, int quarterTurns)
        => Normalize(quarterTurns) switch
        {
            1 => (y, (float)(height - x)),
            2 => ((float)(width - x), (float)(height - y)),
            3 => ((float)(width - y), x),
            _ => (x, y),
        };

    /// <summary>Rotates an axis-aligned rect (corner-normalised result).</summary>
    public static RectF RotateRect(RectF rect, double width, double height, int quarterTurns)
    {
        var (x1, y1) = RotatePoint(rect.Left, rect.Top, width, height, quarterTurns);
        var (x2, y2) = RotatePoint(rect.Right, rect.Bottom, width, height, quarterTurns);
        return new RectF(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
