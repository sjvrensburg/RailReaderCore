using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;
using static RailReader.Core.Tests.GuidePageBuilder;

namespace RailReader.Core.Tests;

public class WordIndexTests
{
    [Fact]
    public void SplitsOnWhitespace_AndUnionsGlyphRects()
    {
        var text = new GuidePageBuilder().Line("The quick fox", 10, 100).Build();
        var analysis = MakeAnalysis(Block(10, 95, 200, 20, BlockRole.Text, 100));

        var idx = WordIndex.Build(analysis, text);
        var words = idx.WordsOf(0, 0);

        Assert.Equal(3, words.Count);
        Assert.Equal("The", idx.TextOf(words[0]));
        Assert.Equal("quick", idx.TextOf(words[1]));
        Assert.Equal("fox", idx.TextOf(words[2]));
        Assert.Equal(new RectF(10, 100, 25, 110), words[0].Rect);
        Assert.Equal(new RectF(30, 100, 55, 110), words[1].Rect);
        Assert.Equal(0, words[0].Start);
        Assert.Equal(3, words[0].Length);
    }

    [Fact]
    public void SpacesWithBoxes_StillBreakWords_AndAreNotWords()
    {
        var text = new GuidePageBuilder().Line("a   b", 10, 100, spaceBoxes: true).Build();
        var analysis = MakeAnalysis(Block(10, 95, 200, 20, BlockRole.Text, 100));

        var words = WordIndex.Build(analysis, text).WordsOf(0, 0);

        Assert.Equal(2, words.Count);
        Assert.Equal(new RectF(10, 100, 15, 110), words[0].Rect);
        Assert.Equal(new RectF(30, 100, 35, 110), words[1].Rect);
    }

    [Fact]
    public void Ligature_OneBoxForSeveralChars_StaysInsideItsWord()
    {
        // "first" with an "fi" ligature: one box at index 0 covers two characters; index 1 has no box.
        var text = new GuidePageBuilder().Raw("first",
            new CharBox(0, 10, 100, 20, 110),
            new CharBox(2, 20, 100, 25, 110),
            new CharBox(3, 25, 100, 30, 110),
            new CharBox(4, 30, 100, 35, 110)).Build();
        var analysis = MakeAnalysis(Block(10, 95, 200, 20, BlockRole.Text, 100));

        var idx = WordIndex.Build(analysis, text);
        var words = idx.WordsOf(0, 0);

        var w = Assert.Single(words);
        Assert.Equal(0, w.Start);
        Assert.Equal(5, w.Length);
        Assert.Equal("first", idx.TextOf(w));
        Assert.Equal(new RectF(10, 100, 35, 110), w.Rect);
    }

    [Fact]
    public void LineBreakWithoutWhitespace_SplitsAtTheBand()
    {
        // A producer that emits no newline between a hyphenated break: "exam-" then "ple" on the next line.
        var boxes = new List<CharBox>();
        const string s = "exam-ple";
        for (int i = 0; i < 5; i++) boxes.Add(new CharBox(i, 10 + i * CharW, 100, 15 + i * CharW, 110));
        for (int i = 5; i < 8; i++) boxes.Add(new CharBox(i, 10 + (i - 5) * CharW, 115, 15 + (i - 5) * CharW, 125));
        var text = new PageText(s, boxes);
        var analysis = MakeAnalysis(Block(10, 95, 200, 40, BlockRole.Text, 100, 115));

        var idx = WordIndex.Build(analysis, text);

        var first = Assert.Single(idx.WordsOf(0, 0));
        var second = Assert.Single(idx.WordsOf(0, 1));
        Assert.Equal("exam-", idx.TextOf(first));
        Assert.Equal("ple", idx.TextOf(second));
    }

    [Fact]
    public void WordsLandOnTheRightLine_WithMixedLineHeights()
    {
        var text = new GuidePageBuilder()
            .Line("Heading", 10, 100)
            .Line("body text", 10, 130)
            .Build();
        var block = new LayoutBlock { BBox = new BBox(10, 95, 200, 60), Role = BlockRole.Text };
        block.Lines.Add(new LineInfo(105, 10, 10, 200));   // tight band around the first line
        block.Lines.Add(new LineInfo(136, 20, 10, 200));   // taller band, centre off the glyph centre
        var analysis = MakeAnalysis(block);

        var idx = WordIndex.Build(analysis, text);

        Assert.Equal("Heading", idx.TextOf(Assert.Single(idx.WordsOf(0, 0))));
        Assert.Equal(2, idx.WordsOf(0, 1).Count);
    }

    [Fact]
    public void WordWhoseCentreIsInNoLineBand_GoesToNearestLine()
    {
        // Glyphs at top=118 (centre 123) fall between line bands [100,110] and [130,140]; nearest is line 1 (135).
        var text = new GuidePageBuilder().Line("stray", 10, 118).Build();
        var analysis = MakeAnalysis(Block(10, 95, 200, 60, BlockRole.Text, 100, 130));

        var idx = WordIndex.Build(analysis, text);

        Assert.Empty(idx.WordsOf(0, 0));
        Assert.Single(idx.WordsOf(0, 1));
    }

    [Fact]
    public void WordOutsideEveryBlock_IsDropped()
    {
        var text = new GuidePageBuilder().Line("inside", 10, 100).Line("outside", 300, 100).Build();
        var analysis = MakeAnalysis(Block(10, 95, 100, 20, BlockRole.Text, 100));

        var idx = WordIndex.Build(analysis, text);

        Assert.Equal("inside", idx.TextOf(Assert.Single(idx.WordsOf(0, 0))));
    }

    [Fact]
    public void OverlappingBlocks_SmallestContainingBlockWins()
    {
        var text = new GuidePageBuilder().Line("word", 20, 100).Build();
        var big = Block(0, 0, 500, 700, BlockRole.Text, 100);
        var small = Block(10, 95, 100, 20, BlockRole.Text, 100);
        var analysis = MakeAnalysis(big, small);

        var idx = WordIndex.Build(analysis, text);

        Assert.Empty(idx.WordsOf(0, 0));
        Assert.Single(idx.WordsOf(1, 0));
    }

    [Fact]
    public void BlockWithoutDetectedLines_UsesLineZero()
    {
        var text = new GuidePageBuilder().Line("lonely", 10, 100).Build();
        var analysis = MakeAnalysis(Block(10, 95, 200, 20));

        var idx = WordIndex.Build(analysis, text);

        Assert.Single(idx.WordsOf(0, 0));
        Assert.Empty(idx.WordsOf(0, 1));
        Assert.Empty(idx.WordsOf(7, 0));
    }

    [Fact]
    public void For_CachesPerAnalysis_AndRebuildsWhenTextChanges()
    {
        var text = new GuidePageBuilder().Line("one", 10, 100).Build();
        var analysis = MakeAnalysis(Block(10, 95, 200, 20, BlockRole.Text, 100));

        var a = WordIndex.For(analysis, text);
        var b = WordIndex.For(analysis, text);
        Assert.Same(a, b);

        var text2 = new GuidePageBuilder().Line("two words", 10, 100).Build();
        var c = WordIndex.For(analysis, text2);
        Assert.NotSame(a, c);
        Assert.Equal(2, c.WordsOf(0, 0).Count);
        Assert.Same(c, WordIndex.For(analysis, text2));
    }

    [Fact]
    public void EmptyTextLayer_YieldsEmptyLines()
    {
        var text = new PageText("", []);
        var analysis = MakeAnalysis(Block(10, 95, 200, 20, BlockRole.Text, 100));

        Assert.Empty(WordIndex.Build(analysis, text).WordsOf(0, 0));
    }
}
