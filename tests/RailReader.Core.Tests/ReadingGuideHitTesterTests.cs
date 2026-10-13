using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;
using static RailReader.Core.Tests.GuidePageBuilder;

namespace RailReader.Core.Tests;

/// <summary>
/// A two-column page: left block x 10–200, right block x 220–410, each with three 10-pt lines at
/// tops 100/115/130 (centres 105/120/135), plus a page header the guide must ignore. Line text is
/// laid out so the left column's lines are "The quick brown" / "fox jumps" / "over it".
/// </summary>
public class ReadingGuideHitTesterTests
{
    private static (PageAnalysis analysis, WordIndex words) Page()
    {
        var text = new GuidePageBuilder()
            .Line("Running head", 10, 20)
            .Line("The quick brown", 10, 100)
            .Line("fox jumps", 10, 115)
            .Line("over it", 10, 130)
            .Line("Right column one", 220, 100)
            .Line("two", 220, 115)
            .Line("three", 220, 130)
            .Build();
        var analysis = MakeAnalysis(
            Block(10, 15, 400, 20, BlockRole.Header, 20),
            Block(10, 95, 190, 50, BlockRole.Text, 100, 115, 130),
            Block(220, 95, 190, 50, BlockRole.Text, 100, 115, 130));
        return (analysis, WordIndex.Build(analysis, text));
    }

    private const int Left = 1, Right = 2;

    [Fact]
    public void ResolvesLineAndWordUnderPointer()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        var p = t.Resolve(12, 105, analysis, words);

        Assert.NotNull(p);
        Assert.Equal(Left, p!.Value.BlockIndex);
        Assert.Equal(0, p.Value.LineIndex);
        Assert.Equal(0, p.Value.WordIndex);
        Assert.Equal(new RectF(10, 100, 200, 110), p.Value.LineRect);
        Assert.Equal(new RectF(10, 100, 25, 110), p.Value.WordRect);

        var q = t.Resolve(32, 105, analysis, words)!.Value;
        Assert.Equal(1, q.WordIndex);                       // "quick"
        Assert.Equal(new RectF(30, 100, 55, 110), q.WordRect);
        Assert.Equal(q, t.Current);
    }

    [Fact]
    public void Hysteresis_WobbleInsideDeadZone_KeepsLine_ThenSwitchesOnce()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();              // dead zone 0.6 × 10 → band 94–116 for line 0

        Assert.Equal(0, t.Resolve(12, 105, analysis, words)!.Value.LineIndex);
        Assert.Equal(0, t.Resolve(12, 112, analysis, words)!.Value.LineIndex);   // nearer line 1's centre, kept
        Assert.Equal(0, t.Resolve(12, 116, analysis, words)!.Value.LineIndex);   // band edge, kept
        Assert.Equal(1, t.Resolve(12, 118, analysis, words)!.Value.LineIndex);   // left the band → nearest (120)
        Assert.Equal(1, t.Resolve(12, 114, analysis, words)!.Value.LineIndex);   // inside line 1's band (109–131), kept
    }

    [Fact]
    public void DeadZoneZero_IsPlainNearestLine()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester { DeadZoneFraction = 0f };

        Assert.Equal(0, t.Resolve(12, 105, analysis, words)!.Value.LineIndex);
        Assert.Equal(1, t.Resolve(12, 113, analysis, words)!.Value.LineIndex);
    }

    [Fact]
    public void ColumnAwareness_SameY_OtherColumnWins_EvenWhileHysteresisWouldHold()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        Assert.Equal(Left, t.Resolve(12, 105, analysis, words)!.Value.BlockIndex);
        var p = t.Resolve(300, 105, analysis, words)!.Value;
        Assert.Equal(Right, p.BlockIndex);
        Assert.Equal(0, p.LineIndex);
        Assert.NotNull(p.WordIndex);
    }

    [Fact]
    public void Margin_ResolvesLine_WithNoWord()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        var p = t.Resolve(-20, 120, analysis, words);   // left of every block

        Assert.NotNull(p);
        Assert.Equal(1, p!.Value.LineIndex);
        Assert.Null(p.Value.WordIndex);
        Assert.Null(p.Value.WordRect);
        Assert.Equal(new RectF(10, 115, 200, 125), p.Value.LineRect);
    }

    [Fact]
    public void Gutter_KeepsCurrentLine_DropsWord()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        Assert.NotNull(t.Resolve(60, 120, analysis, words)!.Value.WordIndex);
        var p = t.Resolve(210, 120, analysis, words)!.Value;   // between the columns (tolerance 9.5 each side)

        Assert.Equal(Left, p.BlockIndex);
        Assert.Equal(1, p.LineIndex);
        Assert.Null(p.WordIndex);
    }

    [Fact]
    public void PastARaggedLineEnd_LastWordStaysLit()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        var p = t.Resolve(150, 120, analysis, words)!.Value;   // "fox jumps" ends at x=55; block runs to 200

        Assert.Equal(1, p.LineIndex);
        Assert.Equal(1, p.WordIndex);
        Assert.Equal(new RectF(30, 115, 55, 125), p.WordRect);
    }

    [Fact]
    public void CarriageReturn_FromLastWord_LandsOnNextLinesFirstWord()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        // Rest on "brown" (last word of line 0, ends at x=85), pointer far right of it, then drop a line.
        var p0 = t.Resolve(150, 105, analysis, words)!.Value;
        Assert.Equal(2, p0.WordIndex);
        var p1 = t.Resolve(150, 120, analysis, words)!.Value;
        Assert.Equal(1, p1.LineIndex);
        Assert.Equal(0, p1.WordIndex);                       // "fox", not the nearest word under x=150

        // Not from a mid-line word: the ordinary nearest-word rule applies.
        t.Clear();
        Assert.Equal(1, t.Resolve(40, 105, analysis, words)!.Value.WordIndex);   // "quick"
        Assert.Equal(1, t.Resolve(40, 120, analysis, words)!.Value.WordIndex);   // "jumps" (nearest to x=40)
    }

    [Fact]
    public void CarriageReturn_OnlyIntoTheNextLineInReadingOrder()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        Assert.Equal(2, t.Resolve(150, 105, analysis, words)!.Value.WordIndex);  // last word, line 0
        var p = t.Resolve(150, 135, analysis, words)!.Value;                      // skipped a line
        Assert.Equal(2, p.LineIndex);
        Assert.Equal(1, p.WordIndex);                                             // nearest ("it"), no return
    }

    [Fact]
    public void BetweenTwoWords_NearerWordWins()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        // "The" ends at 25, "quick" starts at 30: 26 → The, 29 → quick.
        Assert.Equal(0, t.Resolve(26, 105, analysis, words)!.Value.WordIndex);
        Assert.Equal(1, t.Resolve(29, 105, analysis, words)!.Value.WordIndex);
    }

    [Fact]
    public void NoTextLayer_IsLineOnly()
    {
        var (analysis, _) = Page();
        var t = new ReadingGuideHitTester();

        var p = t.Resolve(12, 105, analysis, words: null)!.Value;

        Assert.Equal(Left, p.BlockIndex);
        Assert.Null(p.WordIndex);
        Assert.Equal(new RectF(10, 100, 200, 110), p.LineRect);
    }

    [Fact]
    public void PageFurniture_NeverCapturesTheGuide()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        var p = t.Resolve(100, 25, analysis, words)!.Value;   // dead centre of the running head

        Assert.Equal(Left, p.BlockIndex);
        Assert.Equal(0, p.LineIndex);
    }

    [Fact]
    public void NoAnalysis_ReturnsNull_AndClearsCurrent()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        Assert.NotNull(t.Resolve(12, 105, analysis, words));
        Assert.Null(t.Resolve(12, 105, null, words));
        Assert.Null(t.Current);
        Assert.Null(t.Resolve(12, 105, new PageAnalysis(), words));
    }

    [Fact]
    public void NoEligibleBlock_ReturnsNull()
    {
        var analysis = MakeAnalysis(Block(10, 15, 400, 20, BlockRole.Header, 20));
        var t = new ReadingGuideHitTester();

        Assert.Null(t.Resolve(100, 25, analysis, null));
        Assert.Null(t.Current);
    }

    [Fact]
    public void NewAnalysisInstance_ResetsHysteresis()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        Assert.Equal(0, t.Resolve(12, 105, analysis, words)!.Value.LineIndex);
        Assert.Equal(0, t.Resolve(12, 114, analysis, words)!.Value.LineIndex);   // held by the dead zone

        var (fresh, freshWords) = Page();
        Assert.Equal(1, t.Resolve(12, 114, fresh, freshWords)!.Value.LineIndex); // nearest: 120 is 6 away, 105 is 9
    }

    [Fact]
    public void Clear_ForgetsTheLine()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester();

        t.Resolve(12, 105, analysis, words);
        t.Resolve(12, 114, analysis, words);
        t.Clear();
        Assert.Null(t.Current);
        Assert.Equal(1, t.Resolve(12, 114, analysis, words)!.Value.LineIndex);
    }

    [Fact]
    public void BlockWithoutLines_IsOneSyntheticLine()
    {
        var text = new GuidePageBuilder().Line("alpha beta", 10, 110).Build();
        var analysis = MakeAnalysis(Block(10, 100, 200, 40));
        var t = new ReadingGuideHitTester();

        var p = t.Resolve(40, 112, analysis, WordIndex.Build(analysis, text))!.Value;

        Assert.Equal(0, p.LineIndex);
        Assert.Equal(new RectF(10, 100, 210, 140), p.LineRect);
        Assert.Equal(1, p.WordIndex);
    }

    [Fact]
    public void CustomNavigableRoles_AreHonoured()
    {
        var (analysis, words) = Page();
        var t = new ReadingGuideHitTester { NavigableRoles = new HashSet<BlockRole> { BlockRole.Header } };

        var p = t.Resolve(100, 105, analysis, words)!.Value;

        Assert.Equal(0, p.BlockIndex);   // only the header is eligible now
    }
}
