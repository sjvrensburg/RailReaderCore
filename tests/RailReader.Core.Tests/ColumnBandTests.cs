using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for unruled-table column bands (<see cref="LineDetector.DetectColumnBands"/>).
///
/// <para>
/// Splitting each row's glyphs independently is right for that row but not across rows: a
/// blank cell or a short entry changes the cell count and the spans, so column <c>k</c> means
/// something different on every row and cell navigation drifts sideways as it steps down.
/// Pooling every row's runs recovers the shared columns — the same outcome
/// <see cref="LineDetector.DetectColumnGrid"/> gets from ruling lines when a table has them.
/// </para>
/// </summary>
public class ColumnBandTests
{
    private const float ColHeight = 10f;
    private const int ImgW = 400, ImgH = 200;

    /// <summary>An all-white pixmap: no vertical rules, so the ruled-grid path declines and
    /// the glyph-geometry path under test is the one that runs.</summary>
    private static byte[] BlankPixmap()
    {
        var px = new byte[ImgW * ImgH * 3];
        Array.Fill(px, (byte)255);
        return px;
    }

    /// <summary>
    /// Builds a table block whose rows are given as column-start offsets: each entry is the
    /// set of columns that row actually has content in, so a row can skip columns entirely.
    /// </summary>
    private static (LayoutBlock Block, List<CharBox> Chars) MakeTable(
        float[] columnLefts, IReadOnlyList<int[]> rowsColumns,
        float blockLeft = 100f, float blockWidth = 240f, float top = 50f, float rowGap = 6f)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        for (int r = 0; r < rowsColumns.Count; r++)
        {
            float rowTop = top + r * (ColHeight + rowGap);
            foreach (int col in rowsColumns[r])
            {
                // Three glyphs per cell, 8pt apart — well inside the cell-gap threshold
                // (~1× the 10pt glyph height), so a cell stays one run.
                for (int g = 0; g < 3; g++)
                {
                    float left = columnLefts[col] + g * 8f;
                    chars.Add(new CharBox(idx++, left, rowTop, left + 7f, rowTop + ColHeight));
                }
            }
        }

        float height = rowsColumns.Count * ColHeight + (rowsColumns.Count - 1) * rowGap;
        var block = new LayoutBlock
        {
            BBox = new BBox(blockLeft, top, blockWidth, height),
            Role = BlockRole.Table,
        };
        return (block, chars);
    }

    private static List<LineInfo> Detect(LayoutBlock block, List<CharBox> chars) =>
        LineDetector.DetectLines(block, chars, BlankPixmap(), ImgW, ImgH, 1f, 1f,
            tableRowReading: true, cellNavigation: true);

    [Fact]
    public void RaggedTable_GivesEveryRowTheSameColumns()
    {
        // Three columns; the middle row has no middle-column entry — exactly the case that
        // used to yield 3/2/3 cells with three different geometries.
        var (block, chars) = MakeTable(
            [100f, 200f, 300f],
            [[0, 1, 2], [0, 2], [0, 1, 2]]);

        var rows = Detect(block, chars);

        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.NotNull(row.Cells);
            Assert.Equal(3, row.Cells!.Count);
        }

        // Identical spans on every row: that is what makes a column index stable, and it
        // gives the blank an empty navigable cell at the right place rather than no cell.
        var first = rows[0].Cells!;
        foreach (var row in rows)
            Assert.Equal(first, row.Cells);
    }

    [Fact]
    public void BandsCoverTheBlockContiguously()
    {
        var (block, chars) = MakeTable(
            [100f, 200f, 300f],
            [[0, 1, 2], [0, 1, 2]]);

        var cells = Detect(block, chars)[0].Cells!;

        // Boundaries run block edge to block edge with no gaps, so every x inside the table
        // lands in exactly one cell.
        Assert.Equal(block.BBox.X, cells[0].X, 3);
        Assert.Equal(block.BBox.X + block.BBox.W, cells[^1].X + cells[^1].Width, 3);
        for (int i = 1; i < cells.Count; i++)
            Assert.Equal(cells[i - 1].X + cells[i - 1].Width, cells[i].X, 3);
    }

    [Fact]
    public void EachColumnsContentFallsInItsOwnCell()
    {
        float[] lefts = [100f, 200f, 300f];
        var (block, chars) = MakeTable(lefts, [[0, 1, 2], [0, 1, 2]]);

        var cells = Detect(block, chars)[0].Cells!;

        for (int c = 0; c < lefts.Length; c++)
        {
            var cell = cells[c];
            Assert.InRange(lefts[c], cell.X, cell.X + cell.Width);
            // ...and so does the end of that column's content (3 glyphs, last ends at +23).
            Assert.InRange(lefts[c] + 23f, cell.X, cell.X + cell.Width);
        }
    }

    [Fact]
    public void SpanningHeadingRow_DoesNotCollapseTheColumns()
    {
        // A section heading set inside the table body overlaps every column at once. Pooling
        // it would merge all the runs into one band and destroy the grid, so it is excluded
        // from band construction — while still receiving the resulting bands.
        var (block, chars) = MakeTable(
            [100f, 200f, 300f],
            [[0, 1, 2], [0, 1, 2], [0, 1, 2]]);

        // Add a wide heading row spanning 100..330 as one run (glyphs 8pt apart).
        float headingTop = block.BBox.Y + block.BBox.H + 6f;
        int idx = chars.Count;
        for (float x = 100f; x <= 323f; x += 8f)
            chars.Add(new CharBox(idx++, x, headingTop, x + 7f, headingTop + ColHeight));
        block = new LayoutBlock
        {
            BBox = block.BBox with { H = block.BBox.H + 6f + ColHeight },
            Role = BlockRole.Table,
        };

        var rows = Detect(block, chars);

        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
            Assert.Equal(3, row.Cells!.Count);
    }

    [Fact]
    public void SingleColumnBlock_FallsBackToPerRowSplitting()
    {
        // One run per row is a list, not a grid. Bands must decline so the per-row split
        // keeps its existing behaviour rather than inventing columns.
        var (block, chars) = MakeTable([100f], [[0], [0], [0]], blockWidth: 40f);

        var rows = Detect(block, chars);

        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
            Assert.Single(row.Cells!);
    }

    [Fact]
    public void TwoColumnTable_KeepsPerRowSplitting()
    {
        // Deliberately conservative: the same ≥3-column confidence bar the ruled-grid path
        // applies, so a two-column layout is not mistaken for a table grid.
        var (block, chars) = MakeTable([100f, 200f], [[0, 1], [0, 1]], blockWidth: 140f);

        var rows = Detect(block, chars);

        foreach (var row in rows)
            Assert.Equal(2, row.Cells!.Count);  // from the per-row split, not from bands
    }

    [Fact]
    public void DetectColumnBands_ReturnsNullWithoutMultiCellEvidence()
    {
        // Direct unit check of the guard: rows that never show more than one run cannot
        // establish columns however many of them there are.
        List<LineDetector.GlyphRef>?[] rowGlyphs =
        [
            [new LineDetector.GlyphRef(100f, 130f)],
            [new LineDetector.GlyphRef(100f, 128f)],
            [new LineDetector.GlyphRef(100f, 131f)],
        ];

        Assert.Null(LineDetector.DetectColumnBands(rowGlyphs, new BBox(100f, 0f, 240f, 50f), 10f));
    }

    [Fact]
    public void CellNavigationOff_LeavesRowsWithoutCells()
    {
        var (block, chars) = MakeTable([100f, 200f, 300f], [[0, 1, 2], [0, 2]]);

        var rows = LineDetector.DetectLines(block, chars, BlankPixmap(), ImgW, ImgH, 1f, 1f,
            tableRowReading: true, cellNavigation: false);

        Assert.All(rows, r => Assert.Null(r.Cells));
    }
}
