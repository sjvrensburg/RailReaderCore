using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Pure-geometry annotation operations (hit testing, bounds computation).
/// No rendering-library dependency — used by AnnotationInteractionHandler in Core.
/// </summary>
public static class AnnotationGeometry
{
    public const float NoteIconSize = 16f;

    public static RectF? GetAnnotationBounds(Annotation annotation)
    {
        switch (annotation)
        {
            // Highlight + Underline/StrikeOut/Squiggly all share QuadPoint rects.
            case TextMarkupAnnotation m when m.Rects.Count > 0:
                return ComputeRectsBounds(m.Rects);
            case FreehandAnnotation f when f.Points.Count > 0:
                return ComputePointsBounds(f.Points);
            case TextNoteAnnotation t:
                float half = NoteIconSize / 2;
                return new RectF(t.X - half, t.Y - half, t.X + half, t.Y + half);
            case RectAnnotation r:
                return new RectF(r.X, r.Y, r.X + r.W, r.Y + r.H);
            case CaretAnnotation c:
                return new RectF(c.X, c.Y, c.X + c.W, c.Y + c.H);
            case FreeTextAnnotation ft:
                return new RectF(ft.X, ft.Y, ft.X + ft.W, ft.Y + ft.H);
            default:
                return null;
        }
    }

    public static RectF ComputeRectsBounds(List<HighlightRect> rects)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var r in rects)
        {
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X + r.W > maxX) maxX = r.X + r.W;
            if (r.Y + r.H > maxY) maxY = r.Y + r.H;
        }
        return new RectF(minX, minY, maxX, maxY);
    }

    public static RectF ComputePointsBounds(List<PointF> points)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new RectF(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Whether (<paramref name="pageX"/>, <paramref name="pageY"/>) lies on the visible part of
    /// <paramref name="annotation"/>, within <paramref name="tolerance"/> page units: a markup's own
    /// line rects (not their union, which for a highlight wrapping onto the next line covers both whole
    /// rows), a freehand stroke's path, an unfilled rectangle's outline. Notes, free text, carets and
    /// filled rectangles are solid, so their bounds.
    /// </summary>
    public static bool HitTest(Annotation annotation, float pageX, float pageY, float tolerance = 4f)
    {
        switch (annotation)
        {
            case TextMarkupAnnotation m:
                foreach (var r in m.Rects)
                    if (new RectF(r.X, r.Y, r.X + r.W, r.Y + r.H).Inflated(tolerance, tolerance).Contains(pageX, pageY))
                        return true;
                return false;
            case FreehandAnnotation f when f.Points.Count > 0:
                return DistanceToPolyline(f.Points, pageX, pageY) <= f.StrokeWidth / 2 + tolerance;
            case RectAnnotation { Filled: false } r:
                var box = new RectF(Math.Min(r.X, r.X + r.W), Math.Min(r.Y, r.Y + r.H),
                    Math.Max(r.X, r.X + r.W), Math.Max(r.Y, r.Y + r.H));
                float reach = r.StrokeWidth / 2 + tolerance;
                if (!box.Inflated(reach, reach).Contains(pageX, pageY)) return false;
                var hollow = box.Inflated(-reach, -reach);
                return hollow.Width <= 0 || hollow.Height <= 0 || !hollow.Contains(pageX, pageY);
        }
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return false;
        return rect.Inflated(tolerance, tolerance).Contains(pageX, pageY);
    }

    /// <summary>Distance from a point to the polyline through <paramref name="points"/> (to the point
    /// itself when there is only one).</summary>
    public static float DistanceToPolyline(List<PointF> points, float x, float y)
    {
        if (points.Count == 1)
            return MathF.Sqrt((x - points[0].X) * (x - points[0].X) + (y - points[0].Y) * (y - points[0].Y));
        float best = float.MaxValue;
        for (int i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            float t = lengthSq > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSq, 0f, 1f) : 0f;
            float px = a.X + t * dx - x, py = a.Y + t * dy - y;
            best = Math.Min(best, px * px + py * py);
        }
        return MathF.Sqrt(best);
    }

    public static ResizeHandle HitTestResizeHandle(FreehandAnnotation annotation, float pageX, float pageY, float tolerance = 8f)
    {
        var bounds = GetAnnotationBounds(annotation);
        if (bounds is not { } rect) return ResizeHandle.None;

        var selRect = new RectF(rect.Left - 3, rect.Top - 3, rect.Right + 3, rect.Bottom + 3);
        var positions = GetHandlePositions(selRect);

        for (int i = 0; i < positions.Length; i++)
        {
            float dx = pageX - positions[i].X;
            float dy = pageY - positions[i].Y;
            if (dx * dx + dy * dy <= tolerance * tolerance)
                return (ResizeHandle)(i + 1);
        }
        return ResizeHandle.None;
    }

    public static (float X, float Y)[] GetHandlePositions(RectF bounds)
    {
        float cx = bounds.MidX, cy = bounds.MidY;
        return
        [
            (bounds.Left, bounds.Top),
            (cx, bounds.Top),
            (bounds.Right, bounds.Top),
            (bounds.Right, cy),
            (bounds.Right, bounds.Bottom),
            (cx, bounds.Bottom),
            (bounds.Left, bounds.Bottom),
            (bounds.Left, cy),
        ];
    }

    public static RectF ComputeNewBounds(RectF old, ResizeHandle handle, float px, float py, float startX, float startY)
    {
        float dx = px - startX;
        float dy = py - startY;
        float l = old.Left, t = old.Top, r = old.Right, b = old.Bottom;

        switch (handle)
        {
            case ResizeHandle.TopLeft:     l += dx; t += dy; break;
            case ResizeHandle.Top:         t += dy; break;
            case ResizeHandle.TopRight:    r += dx; t += dy; break;
            case ResizeHandle.Right:       r += dx; break;
            case ResizeHandle.BottomRight: r += dx; b += dy; break;
            case ResizeHandle.Bottom:      b += dy; break;
            case ResizeHandle.BottomLeft:  l += dx; b += dy; break;
            case ResizeHandle.Left:        l += dx; break;
        }

        return new RectF(l, t, r, b).Normalized();
    }
}
