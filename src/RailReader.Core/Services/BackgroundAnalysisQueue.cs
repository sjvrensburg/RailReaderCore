using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Lazily generates page indices for background analysis, scanning outward
/// from the current page in alternating forward/backward directions.
/// Skips pages already in the analysis cache or in-flight.
///
/// <para>
/// The scan is bounded to <see cref="WindowPages"/> on either side of the
/// reset origin so opening a document doesn't eagerly analyse every page up
/// front. The window re-centres on each <see cref="Reset"/> (i.e. on
/// navigation), so coverage follows the reader through the document.
/// </para>
/// </summary>
internal sealed class BackgroundAnalysisQueue
{
    private readonly int _pageCount;
    private int _nextForward;
    private int _nextBackward;
    private int _forwardLimit;   // exclusive upper bound for the forward scan
    private int _backwardLimit;  // inclusive lower bound for the backward scan

    /// <summary>
    /// Max pages scanned on either side of the reset origin. <c>&lt;= 0</c>
    /// means the whole document (no window). Takes effect on the next
    /// <see cref="Reset"/>.
    /// </summary>
    public int WindowPages { get; set; }

    public BackgroundAnalysisQueue(int pageCount, int windowPages = 0)
    {
        _pageCount = pageCount;
        WindowPages = windowPages;
        // Both cursors out-of-range until Reset is called → IsExhausted true.
        _nextForward = pageCount;
        _forwardLimit = pageCount;
        _nextBackward = -1;
        _backwardLimit = 0;
    }

    /// <summary>Re-centre the scan origin (and window) on the current page.</summary>
    public void Reset(int currentPage)
    {
        // Start forward scan from the current page itself so it's included
        // if it was never analysed (e.g. worker wasn't ready on first load).
        _nextForward = currentPage;
        _nextBackward = currentPage - 1;
        // WindowPages <= 0 means the whole document: a window of _pageCount
        // makes both clamps collapse to the full [0, _pageCount) range.
        int window = WindowPages > 0 ? WindowPages : _pageCount;
        _forwardLimit = Math.Min(_pageCount, currentPage + window + 1);
        _backwardLimit = Math.Max(0, currentPage - window);
    }

    public bool IsExhausted => _nextForward >= _forwardLimit && _nextBackward < _backwardLimit;

    /// <summary>
    /// Returns the next page to analyse, or null if all pages are covered.
    /// Tries forward first, then backward, skipping cached/in-flight pages.
    /// </summary>
    public int? TryGetNext(IReadOnlyDictionary<int, PageAnalysis> cache,
        Func<int, bool> isInFlight)
    {
        while (_nextForward < _forwardLimit)
        {
            int page = _nextForward++;
            if (!cache.ContainsKey(page) && !isInFlight(page))
                return page;
        }

        while (_nextBackward >= _backwardLimit)
        {
            int page = _nextBackward--;
            if (!cache.ContainsKey(page) && !isInFlight(page))
                return page;
        }

        return null;
    }
}
