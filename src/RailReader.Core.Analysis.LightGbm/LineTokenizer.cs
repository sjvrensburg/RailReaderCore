using System.Text;
using UglyToad.PdfPig.Content;

namespace RailReader.Core.Analysis.LightGbm;

/// <summary>
/// Clusters a PdfPig <see cref="Page"/>'s letters into baseline lines —
/// the analogue of what <c>pdftohtml -xml</c> emits as one <c>&lt;text&gt;</c>
/// element per visual line. The huridocs LightGBM features are trained
/// against that shape, so the analyzer needs the same tokenisation
/// upstream of feature engineering.
///
/// <para>
/// Algorithm: flip each letter into page-point (Y-down) space, group by
/// mid-Y using a threshold of one median letter height, sort each
/// group's letters by X, then emit one <see cref="LineToken"/> per
/// group with concatenated text, union bbox, and the dominant font.
/// Final lines are sorted top-to-bottom.
/// </para>
///
/// <para>
/// Known limitation: a side-by-side two-column layout whose left and
/// right columns share a Y band gets merged into a single token. The
/// huridocs pipeline inherits this limitation from <c>pdftohtml</c> and
/// the model is robust to it in practice on academic content. If/when
/// column-aware tokenisation matters, the right place is here.
/// </para>
/// </summary>
internal static class LineTokenizer
{
    /// <summary>
    /// Y-band tolerance as a multiple of the median per-letter height
    /// — letters within this fraction of each other's midpoint are
    /// considered to share a baseline. Matches the threshold the
    /// existing in-Core <c>LineDetector</c> uses for the same problem.
    /// </summary>
    private const float YBandFraction = 1.0f;

    public static List<LineToken> Tokenize(Page page)
    {
        var letters = page.Letters;
        if (letters.Count == 0) return [];

        double pageH = page.Height;

        // Flip + record per-letter geometry once.
        var prepared = new (float Left, float Top, float Right, float Bottom,
                            float MidY, float Height, string Value, string FontName, float FontSize)[letters.Count];
        for (int i = 0; i < letters.Count; i++)
        {
            var l = letters[i];
            var r = l.BoundingBox;
            float left   = (float)r.Left;
            float right  = (float)r.Right;
            float top    = (float)(pageH - r.Top);
            float bottom = (float)(pageH - r.Bottom);
            prepared[i] = (
                left, top, right, bottom,
                (top + bottom) / 2f, Math.Max(1f, bottom - top),
                l.Value ?? "", l.FontName ?? "", (float)l.PointSize);
        }

        // Median letter height → band tolerance.
        var heightsSorted = prepared.Select(p => p.Height).OrderBy(h => h).ToArray();
        float medianHeight = heightsSorted[heightsSorted.Length / 2];
        float yTolerance = medianHeight * YBandFraction;

        // Sort by mid-Y so adjacent items in the list are vertically near.
        // Cluster greedily — within each cluster the centroid is the
        // average mid-Y of accumulated letters.
        var byMidY = prepared.OrderBy(p => p.MidY).ToArray();
        var clusters = new List<List<int>>(); // indices into byMidY
        var centroids = new List<float>();

        for (int i = 0; i < byMidY.Length; i++)
        {
            float midY = byMidY[i].MidY;
            bool placed = false;
            for (int c = clusters.Count - 1; c >= 0; c--)
            {
                if (Math.Abs(centroids[c] - midY) <= yTolerance)
                {
                    clusters[c].Add(i);
                    // Update centroid as running average — cheap and
                    // stable enough for tight Y bands.
                    centroids[c] = (centroids[c] * (clusters[c].Count - 1) + midY) / clusters[c].Count;
                    placed = true;
                    break;
                }
                // Sorted by midY — once centroids are below midY by more
                // than the tolerance, no earlier cluster will match.
                if (midY - centroids[c] > yTolerance) break;
            }
            if (!placed)
            {
                clusters.Add(new List<int> { i });
                centroids.Add(midY);
            }
        }

        var lines = new List<LineToken>(clusters.Count);
        foreach (var cluster in clusters)
        {
            cluster.Sort((a, b) => byMidY[a].Left.CompareTo(byMidY[b].Left));

            var sb = new StringBuilder();
            float left = float.PositiveInfinity, top = float.PositiveInfinity;
            float right = float.NegativeInfinity, bottom = float.NegativeInfinity;

            // Tally fonts to pick the dominant one (by character count).
            var fontTally = new Dictionary<(string Name, float Size), int>();

            foreach (int idx in cluster)
            {
                var p = byMidY[idx];
                sb.Append(p.Value);
                if (p.Left   < left)   left   = p.Left;
                if (p.Top    < top)    top    = p.Top;
                if (p.Right  > right)  right  = p.Right;
                if (p.Bottom > bottom) bottom = p.Bottom;

                int chars = Math.Max(1, p.Value.Length);
                var key = (p.FontName, p.FontSize);
                fontTally.TryGetValue(key, out int count);
                fontTally[key] = count + chars;
            }

            var (domName, domSize) = fontTally.OrderByDescending(kv => kv.Value).First().Key;
            lines.Add(new LineToken(sb.ToString(), left, top, right, bottom, domName, domSize));
        }

        // Final reading-direction sort (top-down, then left-to-right).
        lines.Sort((a, b) =>
        {
            int byTop = a.Top.CompareTo(b.Top);
            return byTop != 0 ? byTop : a.Left.CompareTo(b.Left);
        });
        return lines;
    }
}
