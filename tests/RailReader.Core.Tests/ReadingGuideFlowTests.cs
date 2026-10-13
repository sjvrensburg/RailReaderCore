using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;
using static RailReader.Core.Tests.GuidePageBuilder;

namespace RailReader.Core.Tests;

/// <summary>
/// Reading-order stepping over the same two-column page as <see cref="ReadingGuideHitTesterTests"/>:
/// header (ignored) · left block lines "The quick brown" / "fox jumps" / "over it" · right block
/// "Right column one" / "two" / "three".
/// </summary>
public class ReadingGuideFlowTests
{
    private static readonly IReadOnlySet<BlockRole> Roles = DefaultRoleSets.Navigable;
    private const int Left = 1, Right = 2;

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

    private static ReadingGuidePosition Pos(PageAnalysis a, WordIndex w, int b, int l, int? word)
        => ReadingGuideFlow.At(a, w, b, l, word)!.Value;

    [Fact]
    public void First_And_Last_SkipFurniture()
    {
        var (a, w) = Page();
        var first = ReadingGuideFlow.First(a, w, Roles)!.Value;
        Assert.Equal((Left, 0, 0), (first.BlockIndex, first.LineIndex, first.WordIndex));
        var last = ReadingGuideFlow.Last(a, w, Roles)!.Value;
        Assert.Equal((Right, 2, 0), (last.BlockIndex, last.LineIndex, last.WordIndex));
    }

    [Fact]
    public void StepWord_WithinLine_ThenWrapsToNextLinesFirstWord()
    {
        var (a, w) = Page();
        var p = Pos(a, w, Left, 0, 1);                                   // "quick"
        p = ReadingGuideFlow.StepWord(p, true, a, w, Roles)!.Value;     // "brown"
        Assert.Equal((0, 2), (p.LineIndex, p.WordIndex));
        p = ReadingGuideFlow.StepWord(p, true, a, w, Roles)!.Value;     // wrap → "fox"
        Assert.Equal((1, 0), (p.LineIndex, p.WordIndex));
        Assert.Equal(new RectF(10, 115, 25, 125), p.WordRect);
    }

    [Fact]
    public void StepWord_CrossesIntoNextBlock_AndStopsAtPageEnd()
    {
        var (a, w) = Page();
        var p = Pos(a, w, Left, 2, 1);                                   // "it", last word of the left block
        p = ReadingGuideFlow.StepWord(p, true, a, w, Roles)!.Value;
        Assert.Equal((Right, 0, 0), (p.BlockIndex, p.LineIndex, p.WordIndex));
        var end = Pos(a, w, Right, 2, 0);                                // "three"
        Assert.Null(ReadingGuideFlow.StepWord(end, true, a, w, Roles));
    }

    [Fact]
    public void StepWord_Backward_LandsOnPreviousLinesLastWord_AndStopsAtPageStart()
    {
        var (a, w) = Page();
        var p = Pos(a, w, Left, 1, 0);                                   // "fox"
        p = ReadingGuideFlow.StepWord(p, false, a, w, Roles)!.Value;    // "brown"
        Assert.Equal((0, 2), (p.LineIndex, p.WordIndex));
        p = Pos(a, w, Right, 0, 0);                                      // first word of the right block
        p = ReadingGuideFlow.StepWord(p, false, a, w, Roles)!.Value;    // "it"
        Assert.Equal((Left, 2, 1), (p.BlockIndex, p.LineIndex, p.WordIndex));
        Assert.Null(ReadingGuideFlow.StepWord(Pos(a, w, Left, 0, 0), false, a, w, Roles));
    }

    [Fact]
    public void StepWord_FromMarginPosition_EntersTheLine()
    {
        var (a, w) = Page();
        var margin = Pos(a, w, Left, 1, null);
        Assert.Null(margin.WordIndex);
        Assert.Equal(0, ReadingGuideFlow.StepWord(margin, true, a, w, Roles)!.Value.WordIndex);
        Assert.Equal(1, ReadingGuideFlow.StepWord(margin, false, a, w, Roles)!.Value.WordIndex);   // backward from the margin enters at the line's last word ("jumps")
    }

    [Fact]
    public void StepLine_LandsOnFirstWord_AcrossBlocks()
    {
        var (a, w) = Page();
        var p = Pos(a, w, Left, 0, 2);
        p = ReadingGuideFlow.StepLine(p, true, a, w, Roles)!.Value;
        Assert.Equal((Left, 1, 0), (p.BlockIndex, p.LineIndex, p.WordIndex));
        p = Pos(a, w, Left, 2, 1);
        p = ReadingGuideFlow.StepLine(p, true, a, w, Roles)!.Value;
        Assert.Equal((Right, 0, 0), (p.BlockIndex, p.LineIndex, p.WordIndex));
        p = ReadingGuideFlow.StepLine(p, false, a, w, Roles)!.Value;
        Assert.Equal((Left, 2, 0), (p.BlockIndex, p.LineIndex, p.WordIndex));
        Assert.Null(ReadingGuideFlow.StepLine(Pos(a, w, Right, 2, 0), true, a, w, Roles));
    }

    [Fact]
    public void LineOnlyPage_StepsByLine()
    {
        var (a, _) = Page();
        var p = ReadingGuideFlow.First(a, null, Roles)!.Value;
        Assert.Null(p.WordIndex);
        p = ReadingGuideFlow.StepWord(p, true, a, null, Roles)!.Value;
        Assert.Equal((Left, 1), (p.BlockIndex, p.LineIndex));
        Assert.Null(p.WordIndex);
    }

    [Fact]
    public void At_RebuildsRects_AndDegradesGracefully()
    {
        var (a, w) = Page();
        var p = ReadingGuideFlow.At(a, w, Left, 0, 1)!.Value;
        Assert.Equal(new RectF(10, 100, 200, 110), p.LineRect);
        Assert.Equal(new RectF(30, 100, 55, 110), p.WordRect);
        Assert.Null(ReadingGuideFlow.At(a, w, Left, 0, 7)!.Value.WordIndex);  // word gone → line-only
        Assert.Null(ReadingGuideFlow.At(a, w, Left, 9, 0));                    // line gone → null
        Assert.Null(ReadingGuideFlow.At(a, w, 9, 0, 0));                       // block gone → null
    }

    [Fact]
    public void BlockWithoutLines_IsOneLine()
    {
        var text = new GuidePageBuilder().Line("alpha beta", 10, 110).Line("gamma", 10, 210).Build();
        var analysis = MakeAnalysis(Block(10, 100, 200, 40), Block(10, 200, 200, 40));
        var words = WordIndex.Build(analysis, text);
        var p = ReadingGuideFlow.First(analysis, words, Roles)!.Value;
        p = ReadingGuideFlow.StepWord(p, true, analysis, words, Roles)!.Value;   // "beta"
        Assert.Equal((0, 0, 1), (p.BlockIndex, p.LineIndex, p.WordIndex));
        p = ReadingGuideFlow.StepWord(p, true, analysis, words, Roles)!.Value;   // next block's synthetic line
        Assert.Equal((1, 0, 0), (p.BlockIndex, p.LineIndex, p.WordIndex));
    }

    [Fact]
    public void IsNextLine_TrueOnlyForTheImmediateSuccessor()
    {
        var (a, w) = Page();
        var p = Pos(a, w, Left, 0, 2);
        Assert.True(ReadingGuideFlow.IsNextLine(p, Left, 1, a, Roles));
        Assert.False(ReadingGuideFlow.IsNextLine(p, Left, 2, a, Roles));
        Assert.True(ReadingGuideFlow.IsNextLine(Pos(a, w, Left, 2, 1), Right, 0, a, Roles));
    }
}
