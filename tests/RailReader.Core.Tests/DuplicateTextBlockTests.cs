using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Issue #216: two prose boxes over the same text that overlap heavily without nesting survive
/// NMS, nested-block suppression and <c>ResolveVerticalOverlaps</c>, so rail stepped the shared
/// lines twice and every per-block text consumer saw them twice.
/// </summary>
public class DuplicateTextBlockTests
{
    // One row of text per entry, 20pt apart starting at y=100; each character 8pt wide, 16pt tall.
    private static List<CharBox> Rows(params string[] rows) => RowsAt(50, rows);

    private static List<CharBox> RowsAt(float left, params string[] rows)
    {
        var chars = new List<CharBox>();
        int index = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            float top = 100 + r * 20;
            for (int c = 0; c < rows[r].Length; c++)
                chars.Add(new CharBox(index + c, left + c * 8, top, left + 8 + c * 8, top + 16));
            index += rows[r].Length + 1;
        }
        return chars;
    }

    private static LayoutBlock Block(float x, float y, float w, float h, BlockRole role = BlockRole.Text)
        => new() { Role = role, BBox = new BBox(x, y, w, h), Confidence = 0.9f };

    private static List<LayoutBlock> InOrder(params LayoutBlock[] blocks)
    {
        for (int i = 0; i < blocks.Length; i++) blocks[i].Order = i;
        return [.. blocks];
    }

    private static readonly List<CharBox> List3 = Rows("first item", "second item", "last item with maths");

    [Fact]
    public void OverhangingBoxAroundTheLastItem_IsDropped_ListKept()
    {
        var list = Block(45, 95, 200, 62);
        var item = Block(48, 137, 220, 24); // sticks out right and below; not nested
        var blocks = InOrder(list, item);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, List3);

        Assert.Same(list, Assert.Single(blocks));
        Assert.Equal(new BBox(45, 95, 200, 62), list.BBox); // nothing orphaned, so no growth
    }

    [Fact]
    public void SmallBoxReadFirst_StillLosesToTheBigOne()
    {
        var item = Block(48, 137, 220, 24);
        var list = Block(45, 95, 200, 62);
        var blocks = InOrder(item, list);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, List3);

        Assert.Same(list, Assert.Single(blocks));
        Assert.Equal(0, list.Order); // renumbered: blocks[i].Order == i
    }

    [Fact]
    public void Renumbers_TheBlocksAfterADroppedOne()
    {
        var chars = Rows("first item", "second item", "last item with maths", "", "", "after");
        var list = Block(45, 95, 200, 62);
        var item = Block(48, 137, 220, 24);
        var after = Block(45, 195, 200, 22);
        var blocks = InOrder(list, item, after);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, chars);

        Assert.Equal([list, after], blocks);
        Assert.Equal([0, 1], blocks.Select(b => b.Order));
    }

    [Fact]
    public void CharactersOnlyTheDroppedBoxHeld_AreKept_ByGrowingTheSurvivor()
    {
        // 10 rows; the item box holds rows 8-9, of which row 9's single char is outside the list.
        var rows = Enumerable.Range(0, 9).Select(i => $"row {i} of the list text").Append("x").ToArray();
        var chars = Rows(rows);
        var list = Block(45, 95, 220, 180);   // rows 0-8 (bottom 275)
        var item = Block(45, 255, 220, 40);   // rows 8-9
        var blocks = InOrder(list, item);
        int itemChars = chars.Count(c => Mid(c, item.BBox));
        Assert.True(chars.Count(c => Mid(c, item.BBox) && Mid(c, list.BBox)) >= 0.9f * itemChars);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, chars);

        var survivor = Assert.Single(blocks);
        Assert.All(chars, c => Assert.True(Mid(c, survivor.BBox)));
        Assert.Null(survivor.Quad);
    }

    [Fact]
    public void KeepsBoth_WhenGrowingWouldTakeAThirdBlocksText()
    {
        var rows = Enumerable.Range(0, 9).Select(i => $"row {i} of the list text").Append("x").ToArray();
        var chars = Rows(rows);
        chars.Add(new CharBox(9_000, 280, 100, 288, 116)); // a margin note right of the list
        var list = Block(45, 95, 220, 180);
        var item = Block(45, 255, 250, 40);   // reaches x=295, so the union covers the note
        var note = Block(275, 95, 20, 22, BlockRole.Aside);
        var blocks = InOrder(list, item, note);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, chars);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(new BBox(45, 95, 220, 180), list.BBox);
    }

    [Fact]
    public void FigureOverlappingItsCaption_KeepsBoth()
    {
        var chars = Rows("axis label", "Figure 1: a caption");
        var figure = Block(45, 95, 250, 45, BlockRole.Figure); // takes in the caption row
        var caption = Block(45, 118, 250, 22, BlockRole.Caption);
        var blocks = InOrder(figure, caption);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, chars);

        Assert.Equal(2, blocks.Count);
    }

    [Theory]
    [InlineData(BlockRole.Heading)]
    [InlineData(BlockRole.DisplayMath)]
    [InlineData(BlockRole.Caption)]
    public void MeaningfulRolesInsideProse_AreNeverDropped(BlockRole role)
    {
        var chars = Rows("A run-in heading or equation", "then the paragraph goes on");
        var prose = Block(45, 95, 250, 42);
        var inner = Block(40, 97, 240, 20, role); // overhangs left, so not nested
        var blocks = InOrder(prose, inner);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, chars);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void AdjacentParagraphsThatTouch_AreBothKept()
    {
        var chars = Rows("paragraph one", "paragraph one ends", "paragraph two", "paragraph two ends");
        var one = Block(45, 95, 200, 45);
        var two = Block(45, 133, 200, 45); // overlaps one's last row band by a few points only
        var blocks = InOrder(one, two);

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, chars);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void WithoutCharacters_NothingChanges()
    {
        var blocks = InOrder(Block(45, 95, 200, 62), Block(48, 137, 220, 24));

        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, null);
        BlockPostProcessor.SuppressDuplicateTextBlocks(blocks, []);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void PostProcess_RailNoLongerSeesTheSharedLinesTwice()
    {
        var list = Block(45, 95, 200, 62);
        var item = Block(48, 137, 220, 24);
        var blocks = InOrder(list, item);
        const int px = 64;

        BlockPostProcessor.PostProcess(blocks, new byte[px * px * 3], px, px, 500f / px, 500f / px, List3);

        var lines = blocks.SelectMany(b => b.Lines).Select(l => MathF.Round(l.Y)).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(lines.Distinct().Count(), lines.Count);
    }

    private static bool Mid(CharBox c, BBox b)
    {
        float x = (c.Left + c.Right) / 2f, y = (c.Top + c.Bottom) / 2f;
        return x >= b.X && x <= b.X + b.W && y >= b.Y && y <= b.Y + b.H;
    }
}
