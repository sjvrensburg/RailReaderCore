using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class SearchServiceTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentModel _state;
    private readonly SearchService _search;
    private int _lastGoToPage = -1;

    public SearchServiceTests()
    {
        var config = new AppConfig();
        var factory = TestFixtures.CreatePdfFactory();
        _pdfPath = TestFixtures.GetTestPdfPath();
        _state = new DocumentModel(_pdfPath, factory.CreatePdfService(_pdfPath),
            factory.CreatePdfTextService(), factory.CreatePdfLinkService(), config.ToCoreSettings(), new SynchronousThreadMarshaller());
        _state.LoadPageBitmap();

        _search = new SearchService(
            () => _state.Primary,
            page => _lastGoToPage = page);
    }

    public void Dispose() => _state.Dispose();

    // ---------------------------------------------------------------
    // PrepareSearchParams
    // ---------------------------------------------------------------

    [Fact]
    public void PrepareSearchParams_PlainText_NullRegex()
    {
        var (regex, comparison, error) = SearchService.PrepareSearchParams("hello", caseSensitive: true, useRegex: false);
        Assert.Null(regex);
        Assert.Null(error);
        Assert.Equal(StringComparison.Ordinal, comparison);
    }

    [Fact]
    public void PrepareSearchParams_ValidRegex_Compiles()
    {
        var (regex, _, error) = SearchService.PrepareSearchParams(@"test\d+", caseSensitive: true, useRegex: true);
        Assert.NotNull(regex);
        Assert.Null(error);
        Assert.Matches(regex, "test123");
        Assert.DoesNotMatch(regex, "hello");
    }

    [Fact]
    public void PrepareSearchParams_InvalidRegex_ReturnsError()
    {
        var (regex, _, error) = SearchService.PrepareSearchParams("[", caseSensitive: true, useRegex: true);
        Assert.Null(regex);
        Assert.NotNull(error);
    }

    [Fact]
    public void PrepareSearchParams_CaseInsensitive_SetsComparison()
    {
        var (_, comparison, _) = SearchService.PrepareSearchParams("test", caseSensitive: false, useRegex: false);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, comparison);
    }

    // ---------------------------------------------------------------
    // FindAllOccurrences
    // ---------------------------------------------------------------

    [Fact]
    public void FindAllOccurrences_MultipleHits()
    {
        var hits = SearchService.FindAllOccurrences("ababab", "ab", StringComparison.Ordinal).ToList();
        Assert.Equal(3, hits.Count);
        Assert.Equal(0, hits[0].Index);
        Assert.Equal(2, hits[1].Index);
        Assert.Equal(4, hits[2].Index);
        Assert.All(hits, h => Assert.Equal(2, h.Length));
    }

    [Fact]
    public void FindAllOccurrences_Overlapping()
    {
        var hits = SearchService.FindAllOccurrences("aaa", "aa", StringComparison.Ordinal).ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0].Index);
        Assert.Equal(1, hits[1].Index);
    }

    [Fact]
    public void FindAllOccurrences_CaseInsensitive()
    {
        var hits = SearchService.FindAllOccurrences("ABAB", "ab", StringComparison.OrdinalIgnoreCase).ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0].Index);
        Assert.Equal(2, hits[1].Index);
    }

    [Fact]
    public void FindAllOccurrences_NoMatch_Empty()
    {
        var hits = SearchService.FindAllOccurrences("abc", "xyz", StringComparison.Ordinal).ToList();
        Assert.Empty(hits);
    }

    // ---------------------------------------------------------------
    // Navigation via FinalizeSearch
    // ---------------------------------------------------------------

    private static List<SearchMatch> MakeMatches(params int[] pages)
    {
        var matches = new List<SearchMatch>();
        foreach (var page in pages)
        {
            matches.Add(new SearchMatch(page, 0, 4, [new RectF(10, 10, 50, 20)]));
        }
        return matches;
    }

    [Fact]
    public void NextMatch_WrapsAround()
    {
        var matches = MakeMatches(0, 0, 0);
        _search.FinalizeSearch(_state, matches);

        // Move to the last match
        _search.ActiveMatchIndex = 2;
        _search.NextMatch();

        Assert.Equal(0, _search.ActiveMatchIndex);
    }

    [Fact]
    public void PreviousMatch_WrapsAround()
    {
        var matches = MakeMatches(0, 0, 0);
        _search.FinalizeSearch(_state, matches);

        _search.ActiveMatchIndex = 0;
        _search.PreviousMatch();

        Assert.Equal(2, _search.ActiveMatchIndex);
    }

    [Fact]
    public void CloseSearch_ClearsState()
    {
        var matches = MakeMatches(0, 1, 2);
        _search.FinalizeSearch(_state, matches);

        Assert.Equal(3, _search.SearchMatches.Count);

        _search.CloseSearch();

        Assert.Empty(_search.SearchMatches);
        Assert.Null(_search.CurrentPageSearchMatches);
        Assert.Equal(0, _search.ActiveMatchIndex);
    }

    [Fact]
    public void DocumentChange_ClearsStaleSearchMatches()
    {
        // Simulate: search ran against _state (document A), results cached.
        var matches = MakeMatches(0, 1, 2);
        _search.FinalizeSearch(_state, matches);
        Assert.Equal(3, _search.SearchMatches.Count);

        // Now the "active document" switches to a new DocumentModel (document B).
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();
        DocumentModel? docB = new DocumentModel(pdfPath,
            factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(),
            factory.CreatePdfLinkService(),
            new AppConfig().ToCoreSettings(),
            new SynchronousThreadMarshaller());
        try
        {
            DocumentModel? activeDoc = _state;
            var search = new SearchService(
                () => activeDoc?.Primary,
                _ => { });

            // Establish search results against docA
            search.FinalizeSearch(_state, matches);
            Assert.Equal(3, search.SearchMatches.Count);

            // Switch active document to docB (simulates file open)
            activeDoc = docB;

            // Navigation entry-points must detect the change and clear stale results
            search.NextMatch();
            Assert.Empty(search.SearchMatches);
        }
        finally
        {
            docB.Dispose();
        }
    }

    [Fact]
    public void GoToMatch_OutOfRange_NoOp()
    {
        var matches = MakeMatches(0, 0);
        _search.FinalizeSearch(_state, matches);

        int indexBefore = _search.ActiveMatchIndex;

        // Negative index: should not crash or change state
        _search.GoToMatch(-1);
        Assert.Equal(indexBefore, _search.ActiveMatchIndex);

        // Way out of range: should not crash or change state
        _search.GoToMatch(999);
        Assert.Equal(indexBefore, _search.ActiveMatchIndex);
    }

    // ---------------------------------------------------------------
    // Confined (portal) view search — skip off-block matches in the cycle
    // ---------------------------------------------------------------

    [Fact]
    public void ConfinedSearch_SeedsAndStepsOnlyInBlockMatches()
    {
        // Confine the focused view to a block over the top-left of page 0. No analysis is seated, so a
        // page-matching focus defaults to CONFINED (CurrentFocusBlockIndex non-null).
        _state.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));

        // Three matches on page 0: idx0 in-block (centre 30,15), idx1 OFF-block (centre 530,415),
        // idx2 in-block (centre 30,55).
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(10, 10, 50, 20)]),
            new(0, 4, 4, [new RectF(500, 400, 560, 430)]),
            new(0, 8, 4, [new RectF(10, 50, 50, 60)]),
        };
        _search.FinalizeSearch(_state, matches);

        // Seed lands on the first in-block match, not the (earlier-or-equal) off-block one.
        Assert.Equal(0, _search.ActiveMatchIndex);

        _search.NextMatch();                 // skips the off-block idx1
        Assert.Equal(2, _search.ActiveMatchIndex);

        _search.NextMatch();                 // wraps, still skipping idx1
        Assert.Equal(0, _search.ActiveMatchIndex);

        _search.PreviousMatch();             // backwards also skips idx1
        Assert.Equal(2, _search.ActiveMatchIndex);
    }

    [Fact]
    public void ConfinedSearch_NoInBlockMatches_StepIsNoOp()
    {
        _state.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(500, 400, 560, 430)]),  // off block
            new(0, 4, 4, [new RectF(520, 420, 560, 440)]),  // off block
        };
        _search.FinalizeSearch(_state, matches);

        int before = _search.ActiveMatchIndex;
        _search.NextMatch();                 // nothing reachable inside the block → stay put
        Assert.Equal(before, _search.ActiveMatchIndex);
    }

    // ---------------------------------------------------------------
    // Issue #81 item D: seed / direct-jump never desync the counter from the camera
    // ---------------------------------------------------------------

    [Fact]
    public void ConfinedSearch_NoReachableMatches_SeedsNoActiveMatch()
    {
        // With no in-block match the seed is −1 ("no active match"), not a page-order match the camera
        // would never scroll to — so the counter can't read "match X of N" while the view stays put.
        _state.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(500, 400, 560, 430)]),  // off block
            new(0, 4, 4, [new RectF(520, 420, 560, 440)]),  // off block
        };
        _search.FinalizeSearch(_state, matches);
        Assert.Equal(-1, _search.ActiveMatchIndex);
    }

    [Fact]
    public void ConfinedSearch_GoToMatch_OffBlockJumpsToNearestReachable()
    {
        _state.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(10, 10, 50, 20)]),     // idx0 in-block
            new(0, 4, 4, [new RectF(500, 400, 560, 430)]), // idx1 off-block (direct-jump target)
            new(0, 8, 4, [new RectF(10, 50, 50, 60)]),     // idx2 in-block
        };
        _search.FinalizeSearch(_state, matches);

        // A direct jump to the off-block idx1 resolves to the nearest reachable match (forward bias → idx2),
        // never advancing the counter to a match the block clamp can't show.
        _search.GoToMatch(1);
        Assert.Equal(2, _search.ActiveMatchIndex);
    }

    [Fact]
    public void ConfinedSearch_GoToMatch_NoReachable_IsNoOp()
    {
        _state.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(500, 400, 560, 430)]),  // off block
            new(0, 4, 4, [new RectF(520, 420, 560, 440)]),  // off block
        };
        _search.FinalizeSearch(_state, matches);   // seed = −1 (nothing reachable)

        _search.GoToMatch(0);                       // off-block direct jump → no-op
        Assert.Equal(-1, _search.ActiveMatchIndex);
    }

    [Fact]
    public void GoToMatch_Unconfined_JumpsExactly()
    {
        // Unconfined view: ResolveReachableTarget returns the requested index unchanged (no regression).
        var matches = MakeMatches(0, 0, 0);
        _search.FinalizeSearch(_state, matches);
        _search.GoToMatch(2);
        Assert.Equal(2, _search.ActiveMatchIndex);
    }

    // ---------------------------------------------------------------
    // Issue #81 item E: reachability is rect-vs-block intersection, not centre containment
    // ---------------------------------------------------------------

    [Fact]
    public void ConfinedSearch_StraddlingMatch_IsReachableByIntersection()
    {
        // A match whose rect straddles the block's top edge (centre 85,−7.5 is ABOVE the block) overlaps
        // the block and the camera clamp can surface it — so it counts as reachable under intersection,
        // though the old centre-containment test skipped it.
        _state.Primary.Focus = new FocusBlock(0, 0, new BBox(0, 0, 100, 100));
        var matches = new List<SearchMatch>
        {
            new(0, 0, 4, [new RectF(50, -20, 120, 5)]),    // straddles the top edge (reachable)
            new(0, 4, 4, [new RectF(500, 400, 560, 430)]), // fully off-block (unreachable)
        };
        _search.FinalizeSearch(_state, matches);

        Assert.Equal(0, _search.ActiveMatchIndex);  // seed lands on the straddling match
        _search.NextMatch();                        // the off-block idx1 is still skipped → wrap to 0
        Assert.Equal(0, _search.ActiveMatchIndex);
    }

    // ---------------------------------------------------------------
    // GetMatchSnippet
    // ---------------------------------------------------------------

    [Fact]
    public void GetMatchSnippet_MidText_HasEllipsis()
    {
        // Inject known text into TextCache for page 0
        string longText = new string('x', 60) + "MATCH" + new string('y', 60);
        _state.SetText(0, new PageText(longText, []));

        var match = new SearchMatch(0, 60, 5, [new RectF(10, 10, 50, 20)]);
        var (pre, matchStr, post) = _search.GetMatchSnippet(match, contextChars: 40);

        // Match text should be extracted correctly
        Assert.Equal("MATCH", matchStr);

        // Pre should start with ellipsis since match is well into the text
        Assert.StartsWith("\u2026", pre);

        // Post should end with ellipsis since there is text remaining
        Assert.EndsWith("\u2026", post);
    }

    [Fact]
    public void GetMatchSnippet_ReplacesNewlines()
    {
        string text = "before\nthe\nmatch\nHERE\nafter\nthe\nmatch";
        _state.SetText(0, new PageText(text, []));

        // "HERE" starts at index 18, length 4
        int matchStart = text.IndexOf("HERE");
        var match = new SearchMatch(0, matchStart, 4, [new RectF(10, 10, 50, 20)]);
        var (pre, matchStr, post) = _search.GetMatchSnippet(match, contextChars: 40);

        Assert.Equal("HERE", matchStr);

        // All newlines should be replaced with spaces
        Assert.DoesNotContain("\n", pre);
        Assert.DoesNotContain("\n", matchStr);
        Assert.DoesNotContain("\n", post);

        // Verify the newlines were replaced (not stripped) — spaces should be present
        Assert.Contains(" ", pre);
        Assert.Contains(" ", post);
    }
}
