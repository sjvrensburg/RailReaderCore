namespace RailReader.Core.Models;

/// <summary>
/// Immutable, pure-geometry layout of a document's pages stacked vertically with a fixed
/// gap between them and centred horizontally in a column as wide as the widest page.
/// Used by continuous-scroll mode (<see cref="CoreSettings.ContinuousScroll"/>) to derive
/// every page's transform from the anchor page's camera. See docs/continuous-scroll-plan.md §1.
/// </summary>
public sealed class PageLayout
{
    private readonly double[] _top;
    private readonly double[] _left;
    private readonly double[] _width;
    private readonly double[] _height;

    public PageLayout(IReadOnlyList<(double W, double H)> sizes, double gap)
    {
        if (sizes.Count == 0)
            throw new ArgumentException("PageLayout requires at least one page.", nameof(sizes));

        Count = sizes.Count;
        Gap = gap;

        _top = new double[Count];
        _left = new double[Count];
        _width = new double[Count];
        _height = new double[Count];

        double maxW = 0;
        for (int i = 0; i < Count; i++)
        {
            _width[i] = sizes[i].W;
            _height[i] = sizes[i].H;
            if (sizes[i].W > maxW) maxW = sizes[i].W;
        }
        MaxWidth = maxW;

        double y = 0;
        for (int i = 0; i < Count; i++)
        {
            _top[i] = y;
            _left[i] = (maxW - _width[i]) / 2.0;
            y += _height[i] + gap;
        }
        TotalHeight = Count > 0 ? _top[Count - 1] + _height[Count - 1] : 0;
    }

    public int Count { get; }
    public double Gap { get; }
    public double MaxWidth { get; }
    public double TotalHeight { get; }

    public double Top(int page) => _top[page];
    public double Left(int page) => _left[page];
    public double Width(int page) => _width[page];
    public double Height(int page) => _height[page];

    /// <summary>
    /// Finds the page whose vertical extent contains <paramref name="docY"/>. A point in the
    /// gap between two pages resolves to the nearer page; a point beyond either end clamps
    /// to the first/last page.
    /// </summary>
    public int PageAtY(double docY)
    {
        if (docY <= _top[0]) return 0;
        int last = Count - 1;
        if (docY >= _top[last] + _height[last]) return last;

        // Binary search for the first page whose top exceeds docY, then step back one.
        int lo = 0, hi = last;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_top[mid] <= docY) lo = mid; else hi = mid - 1;
        }

        int p = lo;
        double pageBottom = _top[p] + _height[p];
        if (docY <= pageBottom) return p;

        // In the gap between p and p+1 — pick the nearer page.
        int next = Math.Min(p + 1, last);
        double distToP = docY - pageBottom;
        double distToNext = _top[next] - docY;
        return distToNext < distToP ? next : p;
    }

    /// <summary>Inclusive range of page indices whose vertical extent overlaps [docY0, docY1].</summary>
    public (int First, int Last) PagesIntersecting(double docY0, double docY1)
    {
        if (docY0 > docY1) (docY0, docY1) = (docY1, docY0);

        int first = PageAtY(docY0);
        while (first > 0 && _top[first - 1] + _height[first - 1] >= docY0) first--;

        int last = PageAtY(docY1);
        while (last < Count - 1 && _top[last + 1] <= docY1) last++;

        return (first, last);
    }
}
