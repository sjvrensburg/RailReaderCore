using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Pure-geometry merging of overlapping text-markup annotations of the same style: two
/// highlights the reader dragged over the same passage (or an extension of an earlier one)
/// become one annotation with one QuadPoints list. No document state — the undoable
/// operation that applies the result lives on <c>DocumentModel.MergeOverlappingTextMarkup</c>.
/// </summary>
public static class TextMarkupMerge
{
    /// <summary>
    /// Opacity tolerance for "same style" (matches <c>AnnotationEquivalence</c>).
    /// </summary>
    private const float OpacityTolerance = 0.01f;

    /// <summary>Why <see cref="TryBuildMerged"/> declined to merge a cluster.</summary>
    public enum Refusal
    {
        None,
        /// <summary>Fewer than two sources — nothing to merge.</summary>
        NothingToMerge,
        /// <summary>Two or more sources carry different, non-empty comments. Merging would have
        /// to pick one or concatenate; the caller should leave that to the user.</summary>
        ConflictingComments,
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are the same markup subtype
    /// (Highlight with Highlight, never Highlight with Underline) in the same colour and
    /// opacity. Geometry is not considered — see <see cref="Overlaps"/>.
    /// </summary>
    public static bool SameStyle(TextMarkupAnnotation a, TextMarkupAnnotation b)
        => a.GetType() == b.GetType()
           && string.Equals(a.Color, b.Color, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(a.Opacity - b.Opacity) <= OpacityTolerance;

    /// <summary>True when any rect of <paramref name="a"/> intersects any rect of <paramref name="b"/>
    /// (strict interior overlap — edge-touching rects do not count, so adjacent lines of two
    /// separate highlights are not glued together).</summary>
    public static bool Overlaps(TextMarkupAnnotation a, TextMarkupAnnotation b)
    {
        foreach (var r in a.Rects)
            foreach (var s in b.Rects)
                if (Intersects(r, s)) return true;
        return false;
    }

    private static bool Intersects(HighlightRect r, HighlightRect s)
        => r.X < s.X + s.W && s.X < r.X + r.W && r.Y < s.Y + s.H && s.Y < r.Y + r.H;

    /// <summary>True when the two could be merged: same style and overlapping.</summary>
    public static bool CanMerge(TextMarkupAnnotation a, TextMarkupAnnotation b)
        => !ReferenceEquals(a, b) && SameStyle(a, b) && Overlaps(a, b);

    /// <summary>
    /// The transitive cluster of same-style markups on <paramref name="page"/> reachable from
    /// <paramref name="seed"/> through pairwise overlap, in page-list order, including the seed
    /// itself (which need not be in the list — a not-yet-added preview qualifies). A seed with
    /// no overlapping partner yields a single-element list.
    /// </summary>
    public static List<TextMarkupAnnotation> FindCluster(IReadOnlyList<Annotation> page, TextMarkupAnnotation seed)
    {
        var cluster = new List<TextMarkupAnnotation> { seed };
        var frontier = new Queue<TextMarkupAnnotation>();
        frontier.Enqueue(seed);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var ann in page)
            {
                if (ann is not TextMarkupAnnotation m || cluster.Contains(m)) continue;
                if (!CanMerge(current, m)) continue;
                cluster.Add(m);
                frontier.Enqueue(m);
            }
        }
        // Page-list order (seed first if absent from the page) keeps the result deterministic.
        cluster.Sort((x, y) => IndexOrFirst(page, x).CompareTo(IndexOrFirst(page, y)));
        return cluster;
    }

    private static int IndexOrFirst(IReadOnlyList<Annotation> page, Annotation a)
    {
        for (int i = 0; i < page.Count; i++) if (ReferenceEquals(page[i], a)) return i;
        return -1;
    }

    /// <summary>
    /// Unions a set of QuadPoint rects into the minimal non-overlapping set, one rect per line band:
    /// rects are grouped by vertical overlap (the same band test the drag path uses to build one
    /// rect per visual line), each band takes the union vertical extent, and horizontally
    /// overlapping intervals within a band are joined. Disjoint intervals on one line stay
    /// separate rects. Output is ordered top-to-bottom, then left-to-right.
    /// </summary>
    public static List<HighlightRect> UnionRects(IEnumerable<HighlightRect> rects)
    {
        var sorted = rects.Where(r => r.W > 0 && r.H > 0).OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
        var result = new List<HighlightRect>();
        if (sorted.Count == 0) return result;

        // 1. Line bands by vertical overlap.
        var bands = new List<List<HighlightRect>>();
        float bandTop = 0, bandBottom = 0;
        foreach (var r in sorted)
        {
            if (bands.Count > 0 && r.Y < bandBottom && r.Y + r.H > bandTop)
            {
                bands[^1].Add(r);
                bandTop = Math.Min(bandTop, r.Y);
                bandBottom = Math.Max(bandBottom, r.Y + r.H);
            }
            else
            {
                bands.Add([r]);
                bandTop = r.Y;
                bandBottom = r.Y + r.H;
            }
        }

        // 2. Within a band: union vertical extent, join horizontally overlapping intervals.
        foreach (var band in bands)
        {
            float top = band.Min(r => r.Y);
            float bottom = band.Max(r => r.Y + r.H);
            band.Sort((a, b) => a.X.CompareTo(b.X));
            float left = band[0].X, right = band[0].X + band[0].W;
            for (int i = 1; i < band.Count; i++)
            {
                var r = band[i];
                if (r.X <= right) // overlapping or touching horizontally → extend
                {
                    right = Math.Max(right, r.X + r.W);
                }
                else
                {
                    result.Add(new HighlightRect(left, top, right - left, bottom - top));
                    left = r.X; right = r.X + r.W;
                }
            }
            result.Add(new HighlightRect(left, top, right - left, bottom - top));
        }
        return result;
    }

    /// <summary>
    /// Builds the merged annotation for a cluster (see <see cref="FindCluster"/>): a brand-new
    /// instance of the shared subtype with the unioned rects, the shared colour/opacity, the single
    /// comment the sources agree on (or none), and the earliest creation date. Identity is fresh
    /// (<see cref="Annotation.NativeId"/> null, <see cref="AnnotationSource.RailReader"/>): the
    /// reconciling PDF writer deletes the sources by their /NM and creates the merged one. Refuses
    /// when the cluster has fewer than two members or carries conflicting comments.
    /// </summary>
    public static Refusal TryBuildMerged(IReadOnlyList<TextMarkupAnnotation> cluster, out TextMarkupAnnotation? merged)
    {
        merged = null;
        if (cluster.Count < 2) return Refusal.NothingToMerge;

        string? comment = null;
        foreach (var m in cluster)
        {
            var body = m.EffectiveContents;
            if (string.IsNullOrWhiteSpace(body)) continue;
            if (comment is null) comment = body;
            else if (!string.Equals(comment, body, StringComparison.Ordinal)) return Refusal.ConflictingComments;
        }

        var seed = cluster[0];
        var result = (TextMarkupAnnotation)Activator.CreateInstance(seed.GetType())!;
        result.Rects = UnionRects(cluster.SelectMany(m => m.Rects));
        result.Color = seed.Color;
        result.Opacity = seed.Opacity;
        result.Contents = comment;
        result.Author = cluster.Select(m => m.Author).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        result.Subject = cluster.Select(m => m.Subject).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        result.Flags = seed.Flags;
        result.State = seed.State;
        result.ColorComponents = seed.ColorComponents;
        result.CreatedUtc = cluster.Min(m => m.CreatedUtc) ?? DateTimeOffset.UtcNow;
        result.ModifiedUtc = DateTimeOffset.UtcNow;
        merged = result;
        return Refusal.None;
    }
}
