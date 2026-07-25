using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for <see cref="LineDetector.MergeRowsByRulings"/> — using a ruled table's horizontal
/// rules to recover its real rows.
///
/// <para>
/// Char clustering finds text lines, which is right for prose and wrong for a table whose cells
/// wrap: a two-line cell becomes two rail rows, the second looking like a row with content in
/// one column only. Where the table draws a rule between rows, the page has already answered
/// the question. Where it does not — a booktabs table with three rules for twenty rows — the
/// rules must be left alone, because merging on them would collapse the body into one row.
/// </para>
/// </summary>
public class RowRulingTests
{
    private const int ImgW = 600, ImgH = 400;

    private static byte[] Blank()
    {
        var px = new byte[ImgW * ImgH * 3];
        Array.Fill(px, (byte)255);
        return px;
    }

    /// <summary>A rule spanning the whole table width at the given y.</summary>
    private static RulingSegment Rule(float y, float left = 100f, float right = 400f)
        => new(y, left, right);

    /// <summary>
    /// Builds a table block whose text lines sit at the given y positions, each with content in
    /// three columns.
    /// </summary>
    private static (LayoutBlock Block, List<CharBox> Chars) Table(float[] lineTops,
        float blockTop = 100f, float blockHeight = 200f)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        foreach (float top in lineTops)
            foreach (float col in new[] { 110f, 210f, 310f })
                for (int g = 0; g < 3; g++)
                {
                    float x = col + g * 8f;
                    chars.Add(new CharBox(idx++, x, top, x + 7f, top + 10f));
                }

        var block = new LayoutBlock
        {
            BBox = new BBox(100f, blockTop, 300f, blockHeight),
            Role = BlockRole.Table,
        };
        return (block, chars);
    }

    private static List<LineInfo> Detect(LayoutBlock block, List<CharBox> chars, PageRulings? rulings)
        => LineDetector.DetectLines(block, chars, Blank(), ImgW, ImgH, 1f, 1f,
            tableRowReading: true, cellNavigation: false, ocrLines: null, rulings: rulings);

    [Fact]
    public void WrappedCells_BecomeOneRowPerRuledBand()
    {
        // Six text lines, paired into three ruled rows: each row's cell wraps to two lines.
        var (block, chars) = Table([110f, 124f, 150f, 164f, 190f, 204f]);
        var rulings = new PageRulings([], [Rule(140f), Rule(180f)]);

        var rows = Detect(block, chars, rulings);

        Assert.Equal(3, rows.Count);
        // Each merged row spans both of its text lines.
        Assert.All(rows, r => Assert.True(r.Height > 20f, $"row height {r.Height} should span two lines"));
    }

    [Fact]
    public void OneLinePerRuledRow_IsLeftUnchanged()
    {
        var (block, chars) = Table([110f, 150f, 190f]);
        var rulings = new PageRulings([], [Rule(140f), Rule(180f)]);

        var withRules = Detect(block, chars, rulings);
        var without = Detect(block, chars, null);

        Assert.Equal(3, withRules.Count);
        Assert.Equal(without.Count, withRules.Count);
        for (int i = 0; i < withRules.Count; i++)
            Assert.Equal(without[i].Y, withRules[i].Y, 1f);
    }

    [Fact]
    public void BooktabsStyle_DoesNotCollapseTheBody()
    {
        // Ten text lines with a single interior rule under the header — the classic academic
        // table. Merging on these rules would give two rail rows for the whole table.
        var tops = Enumerable.Range(0, 10).Select(i => 110f + i * 18f).ToArray();
        var (block, chars) = Table(tops, blockHeight: 200f);
        var rulings = new PageRulings([], [Rule(126f)]);

        var rows = Detect(block, chars, rulings);

        Assert.Equal(10, rows.Count);
    }

    [Fact]
    public void SparseSectionRules_DoNotCollapseTheBody()
    {
        // Two interior rules over twelve lines: section separators, not row separators. The
        // density guard must decline them.
        var tops = Enumerable.Range(0, 12).Select(i => 110f + i * 15f).ToArray();
        var (block, chars) = Table(tops, blockHeight: 200f);
        var rulings = new PageRulings([], [Rule(170f), Rule(230f)]);

        Assert.Equal(12, Detect(block, chars, rulings).Count);
    }

    [Fact]
    public void BooktabsWithTopAndBottomRules_DoesNotCollapseTheBody()
    {
        // The full booktabs table: a rule above the header, one under it, one below the body.
        // Three interior cuts make four bands, two of which are empty — so an average over
        // bands stays under the limit while the one band holding the body fuses five data rows
        // into a single rail row. The band that is over-full is the one that must decline.
        var (block, chars) = Table([112f, 140f, 156f, 172f, 188f, 204f],
            blockTop: 100f, blockHeight: 140f);
        var rulings = new PageRulings([], [Rule(105f), Rule(130f), Rule(235f)]);

        Assert.Equal(6, Detect(block, chars, rulings).Count);
    }

    [Fact]
    public void AnOverFullBand_DoesNotForfeitTheRestOfTheTable()
    {
        // One ruled band holds a wrapped two-line cell (merge it) and the next holds five data
        // rows with no rule between them (leave them alone). Declining band by band keeps both
        // right; declining for the whole table would give back seven text lines.
        var (block, chars) = Table([110f, 124f, 150f, 164f, 178f, 192f, 206f]);
        var rulings = new PageRulings([], [Rule(105f), Rule(140f), Rule(295f)]);

        var rows = Detect(block, chars, rulings);

        Assert.Equal(6, rows.Count);
        Assert.True(rows[0].Height > 20f, $"the wrapped row ({rows[0].Height}) should span two lines");
        Assert.All(rows.Skip(1), r => Assert.True(r.Height < 20f, "data rows stay one line each"));
    }

    [Fact]
    public void ShortRulesThatDoNotCrossTheTable_AreIgnored()
    {
        // A rule underlining one header cell spans a fraction of the width and must not cut a row.
        var (block, chars) = Table([110f, 124f, 150f, 164f, 190f, 204f]);
        var narrow = new PageRulings([], [Rule(140f, 100f, 150f), Rule(180f, 100f, 150f)]);

        Assert.Equal(6, Detect(block, chars, narrow).Count);
    }

    [Fact]
    public void RulesOutsideTheBlock_AreIgnored()
    {
        var (block, chars) = Table([110f, 124f, 150f, 164f, 190f, 204f]);
        // Both rules sit above and below the block, so there are no interior cuts.
        var outside = new PageRulings([], [Rule(50f), Rule(350f)]);

        Assert.Equal(6, Detect(block, chars, outside).Count);
    }

    [Fact]
    public void MergedRowsStayInsideTheBlockAndInOrder()
    {
        var (block, chars) = Table([110f, 124f, 150f, 164f, 190f, 204f]);
        var rulings = new PageRulings([], [Rule(140f), Rule(180f)]);

        var rows = Detect(block, chars, rulings);

        float top = block.BBox.Y, bottom = block.BBox.Y + block.BBox.H;
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.InRange(rows[i].Y - rows[i].Height / 2f, top - 0.5f, bottom);
            Assert.InRange(rows[i].Y + rows[i].Height / 2f, top, bottom + 0.5f);
            if (i > 0) Assert.True(rows[i].Y > rows[i - 1].Y, "rows must stay ordered top to bottom");
        }
    }

    [Fact]
    public void MergedRowsKeepCellGeometry()
    {
        // With cell navigation on, a merged row must still carry the table's columns — the
        // wrapped second line must not leave the row without cells.
        var (block, chars) = Table([110f, 124f, 150f, 164f, 190f, 204f]);
        var rulings = new PageRulings(
            [new RulingSegment(200f, 100f, 300f), new RulingSegment(300f, 100f, 300f)],
            [Rule(140f), Rule(180f)]);

        var rows = LineDetector.DetectLines(block, chars, Blank(), ImgW, ImgH, 1f, 1f,
            tableRowReading: true, cellNavigation: true, ocrLines: null, rulings: rulings);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(3, r.Cells!.Count));
    }

    [Fact]
    public void NonTableBlocks_AreNeverMergedByRules()
    {
        // Prose under a page rule must keep its per-line stepping: a paragraph's lines are the
        // reading unit, whatever is drawn around them.
        var (_, chars) = Table([110f, 124f, 150f, 164f, 190f, 204f]);
        var prose = new LayoutBlock { BBox = new BBox(100f, 100f, 300f, 200f), Role = BlockRole.Text };
        var rulings = new PageRulings([], [Rule(140f), Rule(180f)]);

        Assert.Equal(6, Detect(prose, chars, rulings).Count);
    }

    [Fact]
    public void DirectCall_WithoutRulings_IsIdentity()
    {
        List<LineInfo> rows = [new(110f, 10f, 100f, 300f), new(130f, 10f, 100f, 300f)];
        var block = new BBox(100f, 100f, 300f, 200f);

        Assert.Same(rows, LineDetector.MergeRowsByRulings(rows, null, block));
        Assert.Same(rows, LineDetector.MergeRowsByRulings(rows, PageRulings.Empty, block));
    }
}
