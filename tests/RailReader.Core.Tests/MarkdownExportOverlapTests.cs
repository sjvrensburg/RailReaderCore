using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #214: the Markdown export wrote some regions of a page twice. Layout boxes can overlap
/// heavily without one containing the other, so neither NMS (IoU) nor nested-block suppression
/// removes the second; and <c>ResolveVerticalOverlaps</c> leaves a block untouched when trimming
/// it would leave under 5pt. Per-block text extraction then gave the shared characters to both.
/// </summary>
public class MarkdownExportOverlapTests
{
    // One row of text per entry, 20pt apart; each character 8pt wide, 16pt tall.
    private static PageText MakePage(params string[] rows)
    {
        var text = string.Join("\n", rows);
        var chars = new List<CharBox>();
        int index = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            float top = 100 + r * 20;
            for (int c = 0; c < rows[r].Length; c++)
                chars.Add(new CharBox(index + c, 50 + c * 8, top, 58 + c * 8, top + 16));
            index += rows[r].Length + 1; // the newline has no box
        }
        return new PageText(text, chars);
    }

    private static LayoutBlock Block(float x, float y, float w, float h, BlockRole role = BlockRole.Text)
        => new() { Role = role, BBox = new BBox(x, y, w, h) };

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    [Fact]
    public void ListBox_PlusAnOverhangingBoxAroundItsLastItem_EmitsTheItemOnce()
    {
        var page = MakePage("first item", "second item", "last item with maths");
        var blocks = new List<LayoutBlock>
        {
            Block(45, 95, 200, 62),   // the whole list: rows 0-2 (bottom 157)
            Block(48, 137, 220, 24),  // the last item, overhanging the list on the right and below
        };
        // The geometry the pipeline lets through: not nested, and too little left to trim.
        BlockPostProcessor.ResolveVerticalOverlaps(blocks);
        Assert.Equal(137, blocks[1].BBox.Y);
        // Per-block extraction (the old export) gives the item to both boxes.
        Assert.All(blocks, b => Assert.EndsWith("last item with maths", page.ExtractBlockText(b)));

        var texts = MarkdownExportService.BlockTexts(page, blocks);
        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), texts, null, null);

        Assert.Equal(1, Count(md, "last item with maths"));
        Assert.Equal(1, Count(md, "first item"));
        Assert.False(texts.ContainsKey(1)); // nothing of its own: dropped, not emitted empty
    }

    [Fact]
    public void BoxReadLast_KeepsOnlyTheTextNoEarlierBoxHas()
    {
        // A reference entry also detected as its own box, ordered at the end of the page.
        var page = MakePage("[1] A. Author, incremental", "learning, 2024.", "Body text after.");
        var blocks = new List<LayoutBlock>
        {
            Block(45, 95, 230, 42, BlockRole.Reference), // the entry (rows 0-1)
            Block(45, 135, 200, 22),                     // body text (row 2)
            Block(40, 97, 220, 38, BlockRole.Reference), // the entry again, read last
        };

        var texts = MarkdownExportService.BlockTexts(page, blocks);
        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), texts, null, null);

        Assert.Equal(1, Count(md, "incremental"));
        Assert.Equal(1, Count(md, "learning, 2024."));
        Assert.EndsWith("Body text after.", md.TrimEnd());
    }

    [Fact]
    public void ExtractBlockTexts_KeepsTextOnlyTheLaterBoxCovers()
    {
        var page = MakePage("alpha", "beta", "gamma");
        var blocks = new List<LayoutBlock>
        {
            Block(45, 95, 100, 42),  // alpha, beta
            Block(45, 115, 100, 42), // beta (taken), gamma
        };

        var texts = page.ExtractBlockTexts(blocks);

        Assert.Equal(page.ExtractBlockText(blocks[0]), texts[0]); // first box: unchanged
        Assert.Contains("beta", texts[0]);
        Assert.Equal("gamma", texts[1]);
    }

    [Fact]
    public void ExtractBlockTexts_MatchesPerBlockExtraction_WhenBoxesDoNotOverlap()
    {
        var page = MakePage("alpha", "beta", "gamma");
        var blocks = new List<LayoutBlock> { Block(45, 95, 100, 22), Block(45, 115, 100, 42) };

        var texts = page.ExtractBlockTexts(blocks);

        Assert.Equal(blocks.Select(page.ExtractBlockText), texts);
    }

    [Fact]
    public void BoxWhoseTextIsNotEmitted_DoesNotSwallowTheTextItOverlaps()
    {
        // A figure (or running header) read first whose box runs into the caption below it: the
        // figure writes no text, so the caption must keep every character.
        var page = MakePage("axis label", "Figure 1: the caption");
        var blocks = new List<LayoutBlock>
        {
            Block(45, 95, 200, 42, BlockRole.Figure),  // covers both rows
            Block(45, 117, 200, 22, BlockRole.Caption),
        };

        var texts = MarkdownExportService.BlockTexts(page, blocks);
        var md = PageMarkdownBuilder.Build(blocks, new Dictionary<int, int>(), texts, null, null);

        Assert.Equal("Figure 1: the caption", texts[1]);
        Assert.Equal(1, Count(md, "Figure 1: the caption"));
    }
}
