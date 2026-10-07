namespace RailReader.Core.Models;

/// <summary>
/// A layout block's outline as four page-space corners, ordered clockwise on
/// screen (y down) starting from the corner nearest the page's top-left — the
/// order PaddleX uses. Unlike <see cref="BBox"/> the quad need not be
/// axis-aligned, so it follows a block on a rotated page (a photographed or
/// skewed scan) instead of bounding it with a larger upright rectangle.
/// </summary>
public readonly record struct BlockQuad(PointF TopLeft, PointF TopRight, PointF BottomRight, PointF BottomLeft)
{
    /// <summary>
    /// Rotation of the top edge (<see cref="TopLeft"/> → <see cref="TopRight"/>)
    /// in degrees, clockwise-positive on screen, in (-45, 45]. A rectangle alone
    /// cannot tell a quarter-turned block from an upright one — that is
    /// <see cref="LayoutBlock.UprightTurns"/>' job — so this is the residual tilt.
    /// </summary>
    public float AngleDegrees =>
        (float)(Math.Atan2(TopRight.Y - TopLeft.Y, TopRight.X - TopLeft.X) * 180.0 / Math.PI);

    /// <summary>Corners in order: top-left, top-right, bottom-right, bottom-left.</summary>
    public PointF[] ToArray() => [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>The axis-aligned quad covering <paramref name="b"/>.</summary>
    public static BlockQuad FromBBox(BBox b) => new(
        new PointF(b.X, b.Y), new PointF(b.X + b.W, b.Y),
        new PointF(b.X + b.W, b.Y + b.H), new PointF(b.X, b.Y + b.H));
}
