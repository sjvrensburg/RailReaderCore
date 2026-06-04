using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Value-equality for annotations — same type, same effective content, and same geometry
/// (within a small tolerance). Shared by the reconciling writer (to decide whether a matched
/// annotation actually changed) and the sidecar merge (to dedupe migrated annotations that
/// no longer share a /NM with their in-PDF copy).
/// </summary>
internal static class AnnotationEquivalence
{
    private const float GeometryTolerance = 0.75f;

    /// <summary>The note/body text, treating a <see cref="TextNoteAnnotation"/>'s legacy
    /// <c>Text</c> field as equivalent to <see cref="Annotation.Contents"/>. Delegates to
    /// <see cref="Annotation.EffectiveContents"/> so the writer, equivalence, and renderer
    /// share one definition.</summary>
    internal static string EffectiveContents(Annotation ann) => ann.EffectiveContents;

    /// <summary>True when two annotations share colour, opacity, and (where applicable) fill
    /// and stroke width. Compares the display hex colour (consistent in read and write paths),
    /// not <see cref="Annotation.ColorComponents"/>, which is only populated on read.</summary>
    private static bool StyleEquivalent(Annotation a, Annotation b)
    {
        if (!string.Equals(a.Color, b.Color, StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs(a.Opacity - b.Opacity) > 0.01f) return false;
        if (a is RectAnnotation ra && b is RectAnnotation rb)
            return ra.Filled == rb.Filled && Math.Abs(ra.StrokeWidth - rb.StrokeWidth) <= 0.1f;
        if (a is FreehandAnnotation fa && b is FreehandAnnotation fb)
            return Math.Abs(fa.StrokeWidth - fb.StrokeWidth) <= 0.1f;
        return true;
    }

    /// <summary>True when two annotations of the same type have equal content, style, and geometry.</summary>
    internal static bool ContentEquivalent(Annotation a, Annotation b)
    {
        if (a.GetType() != b.GetType()) return false;
        if (!string.Equals(EffectiveContents(a), EffectiveContents(b), StringComparison.Ordinal)) return false;
        if (!StyleEquivalent(a, b)) return false;

        static bool Close(float x, float y) => Math.Abs(x - y) <= GeometryTolerance;
        static bool RectClose(HighlightRect r, HighlightRect s)
            => Close(r.X, s.X) && Close(r.Y, s.Y) && Close(r.W, s.W) && Close(r.H, s.H);

        switch (a)
        {
            case TextMarkupAnnotation ma when b is TextMarkupAnnotation mb:
                if (ma.Rects.Count != mb.Rects.Count) return false;
                for (int i = 0; i < ma.Rects.Count; i++)
                    if (!RectClose(ma.Rects[i], mb.Rects[i])) return false;
                return true;
            case FreehandAnnotation fa when b is FreehandAnnotation fb:
                if (fa.Points.Count != fb.Points.Count) return false;
                for (int i = 0; i < fa.Points.Count; i++)
                    if (!Close(fa.Points[i].X, fb.Points[i].X) || !Close(fa.Points[i].Y, fb.Points[i].Y)) return false;
                return true;
            case TextNoteAnnotation ta when b is TextNoteAnnotation tb:
                return Close(ta.X, tb.X) && Close(ta.Y, tb.Y);
            case CaretAnnotation ca when b is CaretAnnotation cbx:
                return Close(ca.X, cbx.X) && Close(ca.Y, cbx.Y) && Close(ca.W, cbx.W) && Close(ca.H, cbx.H);
            case FreeTextAnnotation fta when b is FreeTextAnnotation ftb:
                return Close(fta.X, ftb.X) && Close(fta.Y, ftb.Y) && Close(fta.W, ftb.W) && Close(fta.H, ftb.H)
                    && Math.Abs(fta.FontSize - ftb.FontSize) <= 0.1f;
            case RectAnnotation ra when b is RectAnnotation rb:
                return Close(ra.X, rb.X) && Close(ra.Y, rb.Y) && Close(ra.W, rb.W) && Close(ra.H, rb.H);
            default:
                return false;
        }
    }
}
