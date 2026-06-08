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

public record struct CharBox(int Index, float Left, float Top, float Right, float Bottom);
