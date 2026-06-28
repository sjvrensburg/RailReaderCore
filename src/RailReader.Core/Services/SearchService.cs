using System.Text.RegularExpressions;
using RailReader.Core.Commands;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Full-text search over the active document.
///
/// <b>Thread-safety:</b> single-threaded. All public members (ExecuteSearch,
/// CloseSearch, NextMatch, PreviousMatch, SearchMatches, CurrentPageSearchMatches,
/// ActiveMatchIndex) must be called from the UI thread. Internal state
/// (<c>SearchMatches</c>, <c>_searchMatchesByPage</c>, <c>CurrentPageSearchMatches</c>)
/// is mutated without synchronisation; concurrent access from background threads
/// will race. Background work (e.g. a future async search) must marshal back to
/// the UI thread before touching this service.
/// </summary>
public sealed class SearchService
{
    private readonly Func<Viewport?> _getFocusedViewport;
    private readonly Action<int> _goToPage;

    public SearchService(
        Func<Viewport?> getFocusedViewport,
        Action<int> goToPage)
    {
        _getFocusedViewport = getFocusedViewport;
        _goToPage = goToPage;
    }

    /// <summary>The view search resolves against (issue #74): "current page", rail framing, and
    /// match-centring all use the focused viewport's page/camera/size, so a focused non-primary pane
    /// searches and highlights ITS page — not the document's primary. The owning document is reached
    /// via <see cref="Viewport.Owner"/>. Single-viewport: this is the document's primary.</summary>
    private Viewport? FocusedView => _getFocusedViewport();

    /// <summary>The document being searched, derived from <see cref="FocusedView"/>.</summary>
    private DocumentModel? ActiveDoc => _getFocusedViewport()?.Owner;

    public List<SearchMatch> SearchMatches { get; private set; } = [];
    private Dictionary<int, List<SearchMatch>> _searchMatchesByPage = [];
    public List<SearchMatch>? CurrentPageSearchMatches { get; private set; }
    public int ActiveMatchIndex { get; set; }
    private DocumentModel? _searchedDocument;

    /// <summary>
    /// Search matches on a specific page (issue #74). A multi-viewport host renders each pane's own
    /// search highlights by passing that pane's <see cref="Viewport.CurrentPage"/> — whereas
    /// <see cref="CurrentPageSearchMatches"/> tracks only the focused view's page. Null when the page
    /// has no matches.
    /// </summary>
    public IReadOnlyList<SearchMatch>? MatchesForPage(int page)
        => _searchMatchesByPage.TryGetValue(page, out var matches) ? matches : null;

    /// <summary>
    /// Clears search state when the active document has changed since the last search.
    /// Prevents stale results from a previous document appearing on a newly-opened one.
    /// </summary>
    private void ClearIfDocumentChanged()
    {
        var current = ActiveDoc;
        if (!ReferenceEquals(current, _searchedDocument))
            CloseSearch();
    }

    public void CloseSearch()
    {
        SearchMatches = [];
        _searchMatchesByPage = [];
        CurrentPageSearchMatches = null;
        ActiveMatchIndex = 0;
        _searchedDocument = null;
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        CloseSearch();

        if (string.IsNullOrEmpty(query) || ActiveDoc is not { } doc)
            return;

        var (regex, comparison, _) = PrepareSearchParams(query, caseSensitive, useRegex);
        if (useRegex && regex is null) return; // invalid regex — caller shows error via RegexError

        var allMatches = new List<SearchMatch>();
        for (int page = 0; page < doc.PageCount; page++)
            SearchPage(doc, page, query, regex, comparison, allMatches);

        FinalizeSearch(doc, allMatches);
    }

    /// <summary>
    /// Prepares search parameters. Returns null regex and an error message for invalid regex patterns.
    /// </summary>
    public static (Regex? Regex, StringComparison Comparison, string? RegexError) PrepareSearchParams(
        string query, bool caseSensitive, bool useRegex)
    {
        Regex? regex = null;
        string? regexError = null;
        if (useRegex)
        {
            try
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options);
            }
            catch (RegexParseException ex) { regexError = ex.Message; }
        }
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return (regex, comparison, regexError);
    }

    /// <summary>
    /// Searches a single page and appends matches to the list.
    /// Uses PDFium's FPDFText_CountRects/GetRect for accurate highlight positioning.
    /// </summary>
    public static void SearchPage(DocumentModel doc, int page, string query,
        Regex? regex, StringComparison comparison, List<SearchMatch> results)
    {
        var pageText = doc.GetOrExtractText(page);
        if (string.IsNullOrEmpty(pageText.Text)) return;

        IEnumerable<(int Index, int Length)> hits;
        if (regex is not null)
            hits = regex.Matches(pageText.Text).Select(m => (m.Index, m.Length));
        else
            hits = FindAllOccurrences(pageText.Text, query, comparison);

        // Collect all hits first so we can batch the PDFium rect query
        var hitList = hits.ToList();
        if (hitList.Count == 0) return;

        var allRects = doc.PdfText.GetTextRangeRects(doc.Pdf.PdfBytes, page, hitList, doc.Pdf.Password);

        for (int i = 0; i < hitList.Count; i++)
        {
            var rects = allRects[i];
            if (rects.Count > 0)
                results.Add(new SearchMatch(page, hitList[i].Index, hitList[i].Length, rects));
        }
    }

    /// <summary>
    /// Finalizes search results: sets active match, navigates, updates current page matches.
    /// </summary>
    public void FinalizeSearch(DocumentModel doc, List<SearchMatch> allMatches)
    {
        _searchedDocument = doc;
        SearchMatches = allMatches;
        var byPage = new Dictionary<int, List<SearchMatch>>();
        foreach (var m in allMatches)
        {
            if (!byPage.TryGetValue(m.PageIndex, out var list))
            {
                list = [];
                byPage[m.PageIndex] = list;
            }
            list.Add(m);
        }
        _searchMatchesByPage = byPage;
        if (allMatches.Count > 0)
        {
            // Seed the active match at/after the FOCUSED view's page (issue #74), not the document's
            // primary — so a search from a focused non-primary pane jumps relative to that pane. Only
            // honour the focused view's page when it is actually viewing THIS document; otherwise (a
            // caller finalising results for a non-focused doc) fall back to the doc's own current page.
            int fromPage = FocusedView is { } fv && ReferenceEquals(fv.Owner, doc)
                ? fv.CurrentPage
                : doc.CurrentPage;
            int firstOnCurrentOrAfter = allMatches.FindIndex(m => m.PageIndex >= fromPage);
            int seed = firstOnCurrentOrAfter >= 0 ? firstOnCurrentOrAfter : 0;
            // Confined (portal) view: seed on the first reachable (in-block) match so the initial jump lands
            // on something the block clamp can show. If there are none, fall back to the page-order seed —
            // NavigateToActiveMatch then bails (no jiggle) until the user steps to a reachable match.
            if (FocusedView is { } cfv && ReferenceEquals(cfv.Owner, doc) && cfv.CurrentFocusBlockIndex is not null)
            {
                int firstInBlock = allMatches.FindIndex(m => MatchWithinFocus(cfv, m));
                if (firstInBlock >= 0) seed = firstInBlock;
            }
            ActiveMatchIndex = seed;
            NavigateToActiveMatch();
        }
        UpdateCurrentPageMatches();
    }

    public void NextMatch()
    {
        ClearIfDocumentChanged();
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = AdvanceActiveIndex(+1);
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public void PreviousMatch()
    {
        ClearIfDocumentChanged();
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = AdvanceActiveIndex(-1);
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    /// <summary>
    /// Steps <see cref="ActiveMatchIndex"/> one match in cycle direction <paramref name="dir"/> (+1 next,
    /// −1 previous). For an unconfined view this is a plain wrap-around ±1 (byte-identical to the original
    /// behaviour). For a confined (portal) view it skips matches outside the focus block — they can never
    /// be shown under the block clamp — and returns the next reachable one; if none are reachable it leaves
    /// the index unchanged (a clean no-op rather than jumping to a match the clamp would suppress).
    /// </summary>
    private int AdvanceActiveIndex(int dir)
    {
        int n = SearchMatches.Count;
        int plain = ((ActiveMatchIndex + dir) % n + n) % n;
        if (FocusedView is not { } vp || vp.CurrentFocusBlockIndex is null)
            return plain;
        for (int i = 1; i <= n; i++)
        {
            int cand = ((ActiveMatchIndex + dir * i) % n + n) % n;
            if (MatchWithinFocus(vp, SearchMatches[cand])) return cand;
        }
        return ActiveMatchIndex;
    }

    /// <summary>
    /// True when <paramref name="match"/> can actually be shown in <paramref name="vp"/>: always for an
    /// unconfined view; for a confined (portal) view only when the match is on the view's current page AND
    /// the centre of its bounding rect falls within the focus block's bounds (an off-block match would be
    /// centred then immediately yanked back by <c>ClampCameraToBlock</c>, so it is unreachable).
    /// </summary>
    private static bool MatchWithinFocus(Viewport vp, SearchMatch match)
    {
        if (vp.CurrentFocusBlockIndex is null) return true;
        if (match.PageIndex != vp.CurrentPage || vp.Focus is not { } f || match.Rects.Count == 0)
            return false;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var r in match.Rects)
        {
            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }
        double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
        var b = f.Bounds;
        return cx >= b.X && cx <= b.X + b.W && cy >= b.Y && cy <= b.Y + b.H;
    }

    public void GoToMatch(int matchIndex)
    {
        ClearIfDocumentChanged();
        if (matchIndex < 0 || matchIndex >= SearchMatches.Count) return;
        ActiveMatchIndex = matchIndex;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public (string Pre, string Match, string Post) GetMatchSnippet(SearchMatch match, int contextChars = 40)
    {
        ClearIfDocumentChanged();
        var text = ActiveDoc?.GetOrExtractText(match.PageIndex).Text;
        if (text is null) return ("", "", "");

        int start = Math.Max(0, match.CharStart - contextChars);
        int end = Math.Min(text.Length, match.CharStart + match.CharLength + contextChars);
        int matchEnd = Math.Min(match.CharStart + match.CharLength, text.Length);

        static string Flatten(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

        string pre = Flatten((start > 0 ? "\u2026" : "") + text[start..match.CharStart]);
        string matchStr = Flatten(text[match.CharStart..matchEnd]);
        string post = Flatten(text[matchEnd..end] + (end < text.Length ? "\u2026" : ""));

        return (pre, matchStr, post);
    }

    private void NavigateToActiveMatch()
    {
        if (FocusedView is not { } vp) return;
        if (ActiveMatchIndex < 0 || ActiveMatchIndex >= SearchMatches.Count) return;
        var match = SearchMatches[ActiveMatchIndex];
        // Confined (portal) view: only navigate to a match the block clamp can actually show — on this page
        // AND within the focus block. An OFF-page match the controller's GoToPage would refuse anyway; an
        // off-block ON-page match would be centred and then yanked back by ClampCameraToBlock (a jiggle that
        // never shows the term). NextMatch/PreviousMatch already skip these in the cycle so the active match
        // is reachable; this is the safety net for any other entry point (FinalizeSearch / GoToMatch).
        if (!MatchWithinFocus(vp, match)) return;
        // _goToPage routes through the focused view (controller.GoToPage), so this moves THIS view.
        if (match.PageIndex != vp.CurrentPage)
            _goToPage(match.PageIndex);

        if (vp.Rail.Active && vp.Rail.HasAnalysis && match.Rects.Count > 0)
        {
            // Set rail to the block/line containing the match, then snap
            // horizontally to center the match rather than the block start
            var rect = match.Rects[0];
            double matchCenterX = (rect.Left + rect.Right) / 2.0;
            double matchCenterY = (rect.Top + rect.Bottom) / 2.0;
            vp.Rail.FindBlockNearPoint(matchCenterX, matchCenterY);
            vp.Rail.StartSnapToPoint(vp.Camera.OffsetX, vp.Camera.OffsetY,
                vp.Camera.Zoom, vp.Width, vp.Height, matchCenterX);
        }
        else
        {
            ScrollToMatchRect(vp, match);
        }
    }

    private static void ScrollToMatchRect(Viewport vp, SearchMatch match)
    {
        if (match.Rects.Count == 0) return;
        double ww = vp.Width, wh = vp.Height;

        // Compute bounding box of all match rects
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var r in match.Rects)
        {
            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }

        // Center the match bounding box in the viewport
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;
        vp.Camera.OffsetX = ww / 2.0 - centerX * vp.Camera.Zoom;
        vp.Camera.OffsetY = wh / 2.0 - centerY * vp.Camera.Zoom;
        vp.ClampCamera(ww, wh);
    }

    public void UpdateCurrentPageMatches()
    {
        if (FocusedView is not { } vp)
        {
            CurrentPageSearchMatches = null;
            return;
        }
        _searchMatchesByPage.TryGetValue(vp.CurrentPage, out var matches);
        CurrentPageSearchMatches = matches;
    }

    public static IEnumerable<(int Index, int Length)> FindAllOccurrences(string text, string query, StringComparison comparison)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(query, pos, comparison);
            if (idx < 0) break;
            yield return (idx, query.Length);
            pos = idx + 1;
        }
    }

    public SearchResult GetSearchState()
    {
        var perPage = _searchMatchesByPage.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        return new SearchResult(SearchMatches.Count, ActiveMatchIndex, perPage);
    }
}
