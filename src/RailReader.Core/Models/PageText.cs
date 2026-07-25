using System.Threading;

namespace RailReader.Core.Models;

public record PageText(string Text, List<CharBox> CharBoxes)
{
    // Lazily-built spatial index: the CharBoxes sorted by vertical midpoint, with a
    // parallel array of those midpoints for binary search. Built once per PageText
    // (which is created once per page and cached), then reused by every geometric
    // query so a rect lookup only scans the chars in its Y-band rather than the
    // whole page. Storing the boxes themselves (not just an index permutation) keeps
    // the band scan sequential/cache-friendly. This turns GetPageDescription's
    // per-block extraction from O(blocks × chars) into roughly O(blocks × band) and
    // the per-line reading-position query from O(chars) into O(log chars + band).
    private CharBox[]? _yBoxes;
    private float[]? _yMid;
    private readonly object _ySync = new();

    // Lazily-built deduplicated view of CharBoxes — see DedupedCharBoxes.
    private List<CharBox>? _deduped;
    private readonly object _dedupSync = new();

    /// <summary>
    /// Fraction of a glyph's own size within which a same-valued glyph counts as an
    /// overlapping duplicate. A fake-bold glyph pair is offset by a fraction of a
    /// stroke width — far below a third of the glyph box — while two legitimately
    /// distinct instances of the same character are at least an advance apart.
    ///
    /// <para>
    /// Applied <b>per axis</b>, against that axis's extent. A single tolerance taken from
    /// the larger extent is wrong for narrow glyphs: the tight box of an 11pt Helvetica
    /// 'l' is about 1.1pt wide and 7.9pt tall, so a height-derived tolerance of 2.6pt
    /// exceeds the character's own 2.4pt advance and the second 'l' of "all" reads as a
    /// duplicate of the first.
    /// </para>
    /// </summary>
    private const float DuplicateToleranceFraction = 1f / 3f;

    /// <summary>
    /// Height in page points of the vertical bands the duplicate search is bucketed into, so
    /// a glyph is only ever compared against same-valued glyphs near it rather than against
    /// every occurrence on the page. Roughly a line's worth: small enough to prune hard on a
    /// dense page, large enough that a typical tolerance spans one band either side.
    /// </summary>
    private const float DuplicateBandHeight = 4f;

    /// <summary>
    /// <see cref="CharBoxes"/> with overlapping duplicates removed: where the same
    /// character is drawn two or more times at (nearly) the same place, only the
    /// first occurrence is kept.
    ///
    /// <para>
    /// PDF producers fake bold by stroking a glyph several times at sub-pixel
    /// offsets (and fake shadows the same way). Those repeats are real entries in
    /// the text layer, so they inflate the glyph population that
    /// <see cref="Services.LineDetector"/> reasons over — biasing the median char
    /// height that sets the line-split threshold, and the 90th-percentile anchor
    /// behind the table cell-gap threshold. Filtering them costs one pass and
    /// leaves genuinely distinct glyphs untouched.
    /// </para>
    /// <para>
    /// <see cref="Text"/> and the <see cref="CharBox.Index"/> values are unchanged —
    /// this only drops boxes, so text extraction (which indexes into
    /// <see cref="Text"/>) is unaffected and every surviving index stays valid.
    /// Zero-area boxes (whitespace and other non-marking glyphs) are always kept:
    /// they carry no geometry to duplicate and consumers already skip them.
    /// </para>
    /// <para>Computed once and cached; safe to call from any thread.</para>
    /// </summary>
    public List<CharBox> DedupedCharBoxes
    {
        get
        {
            var cached = Volatile.Read(ref _deduped);
            if (cached is not null) return cached;
            lock (_dedupSync)
            {
                if (_deduped is not null) return _deduped;
                var result = BuildDeduped();
                Volatile.Write(ref _deduped, result);
                return result;
            }
        }
    }

    private List<CharBox> BuildDeduped()
    {
        var kept = new List<CharBox>(CharBoxes.Count);
        // Candidate duplicates are looked up by character value AND vertical band, so the
        // geometric test only ever runs against glyphs that could actually be duplicates —
        // the other 1500 'e's on the page are never visited. Keying on the value alone left
        // the pass quadratic in each character's page-wide frequency, on the UI thread.
        // Values are indices into `kept`, so the boxes stay in one contiguous list.
        var byCharBand = new Dictionary<(char Value, int Band), List<int>>();

        foreach (var cb in CharBoxes)
        {
            float w = cb.Right - cb.Left, h = cb.Bottom - cb.Top;
            if (w <= 0 || h <= 0 || cb.Index < 0 || cb.Index >= Text.Length)
            {
                kept.Add(cb);
                continue;
            }

            char value = Text[cb.Index];
            float tolX = w * DuplicateToleranceFraction;
            float tolY = h * DuplicateToleranceFraction;

            // A glyph within tolY of this one sits at most ceil(tolY / band) bands away, so
            // that span covers every candidate the un-bucketed scan would have found.
            int band = (int)MathF.Floor(cb.Top / DuplicateBandHeight);
            int span = (int)MathF.Ceiling(tolY / DuplicateBandHeight);

            bool duplicate = false;
            for (int b = band - span; b <= band + span && !duplicate; b++)
            {
                if (!byCharBand.TryGetValue((value, b), out var candidates)) continue;
                foreach (int i in candidates)
                {
                    var other = kept[i];
                    // Same glyph, same orientation, same place (within tolerance).
                    if (other.Angle == cb.Angle
                        && Math.Abs(other.Left - cb.Left) <= tolX
                        && Math.Abs(other.Top - cb.Top) <= tolY)
                    {
                        duplicate = true;
                        break;
                    }
                }
            }
            if (duplicate) continue;

            var key = (value, band);
            if (byCharBand.TryGetValue(key, out var bucket)) bucket.Add(kept.Count);
            else byCharBand[key] = [kept.Count];

            kept.Add(cb);
        }

        // No duplicates found — hand back the original list so the common case
        // costs nothing beyond the scan and callers can compare by reference.
        return kept.Count == CharBoxes.Count ? CharBoxes : kept;
    }

    private (CharBox[] Boxes, float[] Mid) YIndex()
    {
        var boxes = Volatile.Read(ref _yBoxes);
        if (boxes is not null) return (boxes, _yMid!);
        lock (_ySync)
        {
            if (_yBoxes is not null) return (_yBoxes, _yMid!);
            int n = CharBoxes.Count;
            var sorted = new CharBox[n];
            var mid = new float[n];
            for (int i = 0; i < n; i++)
            {
                sorted[i] = CharBoxes[i];
                mid[i] = (CharBoxes[i].Top + CharBoxes[i].Bottom) / 2f;
            }
            Array.Sort(mid, sorted);
            _yMid = mid;
            Volatile.Write(ref _yBoxes, sorted);
            return (sorted, mid);
        }
    }

    /// <summary>
    /// Collects the text-indices of every CharBox whose midpoint falls within the
    /// rectangle, using the Y-sorted index to skip boxes outside the vertical band.
    /// Indices are returned in spatial (Y-then-input) order; callers that need
    /// reading order sort by index. Same membership test as a full linear scan.
    /// </summary>
    private List<int> IndicesInRect(float left, float top, float right, float bottom)
    {
        var result = new List<int>();
        int n = CharBoxes.Count;
        if (n == 0) return result;

        var (boxes, mid) = YIndex();
        // Band = [lo, hiB): the boxes whose midY lies in [top, bottom].
        int lo = LowerBound(mid, top);
        int hiB = UpperBound(mid, bottom);
        int bandCount = hiB - lo;

        // When the band covers most of the page the index prunes nothing, and its
        // per-element cost (two-array reads plus a post-sort over Y-scrambled
        // indices) loses to a plain index-order scan. Fall back to a linear pass:
        // it collects in ascending index order, so the caller's Sort is near-free.
        if (bandCount * 4 >= n * 3)
        {
            var linear = new List<int>(bandCount);
            foreach (var cb in CharBoxes)
            {
                float mx = (cb.Left + cb.Right) / 2f;
                float my = (cb.Top + cb.Bottom) / 2f;
                if (mx >= left && mx <= right && my >= top && my <= bottom
                    && cb.Index >= 0 && cb.Index < Text.Length)
                {
                    linear.Add(cb.Index);
                }
            }
            return linear;
        }

        result.Capacity = bandCount;
        for (int k = lo; k < hiB; k++)
        {
            var cb = boxes[k];
            float midX = (cb.Left + cb.Right) / 2f;
            if (midX >= left && midX <= right
                && cb.Index >= 0 && cb.Index < Text.Length)
            {
                result.Add(cb.Index);
            }
        }
        return result;
    }

    // First index whose value is >= key (lower bound).
    private static int LowerBound(float[] a, float key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] < key) lo = m + 1;
            else hi = m;
        }
        return lo;
    }

    // First index whose value is > key (upper bound).
    private static int UpperBound(float[] a, float key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] <= key) lo = m + 1;
            else hi = m;
        }
        return lo;
    }

    /// <summary>
    /// Extracts text whose character midpoints fall within the given rectangle.
    /// Returns null if no characters match.
    /// </summary>
    public string? ExtractTextInRect(float left, float top, float right, float bottom)
    {
        var indices = IndicesInRect(left, top, right, bottom);
        if (indices.Count == 0) return null;
        indices.Sort();
        var buf = new char[indices.Count];
        for (int i = 0; i < indices.Count; i++) buf[i] = Text[indices[i]];
        return new string(buf).Trim();
    }

    /// <summary>
    /// Extracts text within a layout block's bounding box.
    /// </summary>
    public string ExtractBlockText(LayoutBlock block)
    {
        var bbox = block.BBox;
        return ExtractTextInRect(bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H) ?? "";
    }

    /// <summary>
    /// Extracts up to <paramref name="maxChars"/> characters of a block's text in
    /// reading order (trimmed for display). Sets <paramref name="truncated"/> to
    /// true when the block contains more matched characters than were returned, so
    /// callers can decide whether to append an ellipsis based on the real content
    /// length rather than the trimmed preview length (trailing whitespace removed
    /// by the trim must not hide that the text was cut). Avoids allocating a
    /// full-length string when only a preview is needed.
    /// </summary>
    public string ExtractBlockText(LayoutBlock block, int maxChars, out bool truncated)
    {
        truncated = false;
        if (maxChars <= 0) return "";
        var bbox = block.BBox;
        var indices = IndicesInRect(bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H);
        if (indices.Count == 0) return "";
        indices.Sort();
        // The preview is the lowest-indexed maxChars characters; anything beyond
        // that means the block was truncated.
        truncated = indices.Count > maxChars;
        int take = Math.Min(indices.Count, maxChars);
        // Don't slice through a surrogate pair at the cut.
        if (truncated && take > 0 && char.IsHighSurrogate(Text[indices[take - 1]])) take--;
        var buf = new char[take];
        for (int i = 0; i < take; i++) buf[i] = Text[indices[i]];
        return new string(buf).Trim();
    }
}

/// <summary>
/// One character's axis-aligned bounding box in page-point space (top-left
/// origin, Y-down, displayed orientation). <paramref name="Angle"/> is the
/// glyph's rotation in the DISPLAYED frame, in clockwise degrees normalised to
/// {0, 90, 180, 270} (0 for ordinary upright text; free-angle glyphs are
/// rounded to the nearest quarter-turn). It composes the content-stream glyph
/// angle, the page /Rotate, and any view rotation — so 0 always means "reads
/// left-to-right upright as displayed". Providers that cannot report glyph
/// angles leave it 0.
/// </summary>
public record struct CharBox(int Index, float Left, float Top, float Right, float Bottom, float Angle = 0f);
