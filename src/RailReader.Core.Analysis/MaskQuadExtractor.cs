using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using RailReader.Core.Models;
using PointF = RailReader.Core.Models.PointF;

namespace RailReader.Core.Analysis;

/// <summary>
/// Turns a detector's per-detection instance mask into a (possibly rotated)
/// minimum-area rectangle — the geometry behind <see cref="LayoutBlock.Quad"/>
/// (issue #125). PP-DocLayoutV3 emits one binary mask per detection at a
/// quarter of its input resolution; the model computes it whether or not we
/// read it, so this costs a crop scan plus a hull per surviving block.
/// </summary>
internal static class MaskQuadExtractor
{
    private static readonly GeometryFactory Factory = new();

    /// <summary>
    /// Fits the minimum-area rectangle around the set pixels of
    /// <paramref name="mask"/> (row-major, <paramref name="maskW"/>×<paramref name="maskH"/>)
    /// that fall inside the detection box <c>[x0,x1)×[y0,y1)</c> given in
    /// canvas pixels, where one mask pixel spans <paramref name="canvasPerMask"/>
    /// canvas pixels. Pixels outside the box are ignored so a stray blob of the
    /// same mask can't drag the rectangle off the block the box describes.
    /// </summary>
    /// <param name="quad">The rectangle in canvas pixels, corners ordered as <see cref="BlockQuad"/> documents.</param>
    /// <param name="axisAligned">
    /// True when the rectangle's tilt moves its long edge by less than one mask
    /// pixel end to end — below what the mask can resolve, so the caller should
    /// prefer the detection box's own (finer) edges.
    /// </param>
    /// <returns>False when the box holds no set pixels or the fit degenerates.</returns>
    internal static bool TryExtract(ReadOnlySpan<int> mask, int maskW, int maskH,
        float x0, float y0, float x1, float y1, float canvasPerMask,
        out BlockQuad quad, out bool axisAligned)
    {
        quad = default;
        axisAligned = false;
        if (canvasPerMask <= 0 || mask.Length < maskW * maskH) return false;

        // One-pixel margin: the mask is quantised to canvasPerMask, so the box
        // edge can fall mid-pixel on either side of the mask's own edge.
        int mx0 = Math.Clamp((int)MathF.Floor(x0 / canvasPerMask) - 1, 0, maskW);
        int mx1 = Math.Clamp((int)MathF.Ceiling(x1 / canvasPerMask) + 1, 0, maskW);
        int my0 = Math.Clamp((int)MathF.Floor(y0 / canvasPerMask) - 1, 0, maskH);
        int my1 = Math.Clamp((int)MathF.Ceiling(y1 / canvasPerMask) + 1, 0, maskH);

        // The hull of a set of pixel squares is the hull of each row run's two
        // outer squares, so four corners per row suffice — no contour tracing.
        var coords = new List<Coordinate>(4 * (my1 - my0));
        for (int my = my0; my < my1; my++)
        {
            var row = mask.Slice(my * maskW + mx0, mx1 - mx0);
            int first = row.IndexOfAnyExcept(0);
            if (first < 0) continue;
            int last = row.LastIndexOfAnyExcept(0);
            double left = (mx0 + first) * canvasPerMask;
            double right = (mx0 + last + 1) * canvasPerMask;
            double top = my * canvasPerMask;
            double bottom = (my + 1) * canvasPerMask;
            coords.Add(new Coordinate(left, top));
            coords.Add(new Coordinate(right, top));
            coords.Add(new Coordinate(right, bottom));
            coords.Add(new Coordinate(left, bottom));
        }
        if (coords.Count == 0) return false;

        // Hull first: MinimumAreaRectangle on the raw point set is ~6x slower
        // than on its hull (measured: 2.8 ms vs 0.44 ms for a 160-row block).
        var hull = new ConvexHull([.. coords], Factory).GetConvexHull();
        var rect = MinimumAreaRectangle.GetMinimumRectangle(hull);
        if (rect is not Polygon poly || poly.ExteriorRing.NumPoints != 5) return false;

        var corners = OrderCorners(poly.ExteriorRing.Coordinates);
        quad = new BlockQuad(corners[0], corners[1], corners[2], corners[3]);

        float longSide = MathF.Max(Distance(corners[0], corners[1]), Distance(corners[1], corners[2]));
        float drift = longSide / canvasPerMask * MathF.Abs(MathF.Sin(quad.AngleDegrees * MathF.PI / 180f));
        axisAligned = drift < 1f;
        return true;
    }

    /// <summary>
    /// Orders a closed rectangle ring (5 coordinates, last repeating the first)
    /// clockwise on screen — ascending angle about the centroid, since y points
    /// down — starting from the corner with the smallest x+y.
    /// </summary>
    internal static PointF[] OrderCorners(Coordinate[] ring)
    {
        double cx = 0, cy = 0;
        for (int i = 0; i < 4; i++) { cx += ring[i].X; cy += ring[i].Y; }
        cx /= 4; cy /= 4;

        var pts = ring.Take(4)
            .OrderBy(c => Math.Atan2(c.Y - cy, c.X - cx))
            .Select(c => new PointF((float)c.X, (float)c.Y))
            .ToArray();

        int start = 0;
        for (int i = 1; i < 4; i++)
            if (pts[i].X + pts[i].Y < pts[start].X + pts[start].Y) start = i;

        return [pts[start], pts[(start + 1) % 4], pts[(start + 2) % 4], pts[(start + 3) % 4]];
    }

    /// <summary>Scales a canvas-pixel quad into page space.</summary>
    internal static BlockQuad Scale(BlockQuad q, float sx, float sy) => new(
        new PointF(q.TopLeft.X * sx, q.TopLeft.Y * sy),
        new PointF(q.TopRight.X * sx, q.TopRight.Y * sy),
        new PointF(q.BottomRight.X * sx, q.BottomRight.Y * sy),
        new PointF(q.BottomLeft.X * sx, q.BottomLeft.Y * sy));

    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
