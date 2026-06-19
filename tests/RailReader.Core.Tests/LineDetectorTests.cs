using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for the char-box-based clustering path and atomic-class handling in
/// <see cref="LineDetector"/>. The pixel-projection path is covered by
/// <see cref="LineDetectionTests"/>.
/// </summary>
public class LineDetectorTests
{
    /// <summary>
    /// Builds a list of CharBoxes for a fake page with <paramref name="lineCount"/>
    /// lines of <paramref name="charsPerLine"/> characters. Each char is approximately
    /// <paramref name="charHeight"/> tall, separated by <paramref name="lineGap"/>
    /// vertical units. The block bbox is set to fully enclose them.
    /// </summary>
    private static (LayoutBlock Block, List<CharBox> Chars) MakeLines(
        int lineCount, int charsPerLine, float charHeight = 10f, float lineGap = 4f,
        float left = 100f, float top = 100f, BlockRole role = BlockRole.Text)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        for (int line = 0; line < lineCount; line++)
        {
            float lineTop = top + line * (charHeight + lineGap);
            float lineBottom = lineTop + charHeight;
            for (int c = 0; c < charsPerLine; c++)
            {
                float charLeft = left + c * 8f;
                chars.Add(new CharBox(idx++, charLeft, lineTop, charLeft + 7f, lineBottom));
            }
        }

        float width = charsPerLine * 8f + 4f;
        float height = lineCount * charHeight + (lineCount - 1) * lineGap;
        var block = new LayoutBlock
        {
            BBox = new BBox(left - 2f, top - 2f, width, height + 4f),
            Role = role,
            Confidence = 0.9f,
        };
        return (block, chars);
    }

    [Fact]
    public void CharClustering_SingleLine()
    {
        var (block, chars) = MakeLines(lineCount: 1, charsPerLine: 8);
        var lines = LineDetector.DetectLinesFromChars(block.BBox, chars);
        Assert.Single(lines);
        Assert.InRange(lines[0].Height, 9f, 12f);
    }

    [Fact]
    public void CharClustering_FiveLines()
    {
        var (block, chars) = MakeLines(lineCount: 5, charsPerLine: 8);
        var lines = LineDetector.DetectLinesFromChars(block.BBox, chars);
        Assert.Equal(5, lines.Count);
    }

    [Fact]
    public void CharClustering_DenseFourteenLines()
    {
        // Same configuration as the pixel-projection 14-line test
        var (block, chars) = MakeLines(lineCount: 14, charsPerLine: 12, charHeight: 10f, lineGap: 4f);
        var lines = LineDetector.DetectLinesFromChars(block.BBox, chars);
        Assert.Equal(14, lines.Count);
    }

    [Fact]
    public void CharClustering_SubscriptsDoNotFragmentLine()
    {
        // Three base chars on a single line, with one subscript shifted down by
        // 3 px (typical subscript displacement)
        var chars = new List<CharBox>
        {
            new(0, 100, 100, 107, 110), // H
            new(1, 108, 103, 112, 113), // ₂ subscript: 3px lower
            new(2, 113, 100, 120, 110), // O
        };
        var bbox = new BBox(98, 98, 25, 18);
        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_SuperscriptsDoNotFragmentLine()
    {
        // x² + y² on one line: superscripts shifted up by 4 px
        var chars = new List<CharBox>
        {
            new(0, 100, 100, 107, 110), // x
            new(1, 108, 96,  112, 105), // ² (shifted up)
            new(2, 115, 100, 122, 110), // +
            new(3, 125, 100, 132, 110), // y
            new(4, 133, 96,  137, 105), // ²
        };
        var bbox = new BBox(98, 94, 42, 18);
        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_DropCap_DoesNotCollapseSpannedLines()
    {
        // REGRESSION (symptom 1 of the drop-cap bug): a drop cap is a single glyph
        // 2-3 text-lines tall, and PDFium reports it as ONE CharBox with a huge
        // height. It must not swallow the body lines it visually overlaps.
        //
        // Layout: 6 body lines, pitch 14 (10px char height + 4px leading). A
        // 3-line drop cap spans lines 0-2 at the left margin; the body text on
        // those lines is indented to its right (as in print). Lines 3-5 are
        // ordinary full-width body text.
        const float charH = 10f, pitch = 14f, top = 100f, left = 100f;
        float LineTop(int i) => top + i * pitch;

        var chars = new List<CharBox>();
        int idx = 0;

        // The drop cap: one tall glyph from the top of line 0 to the bottom of line 2.
        float capTop = LineTop(0);          // 100
        float capBottom = LineTop(2) + charH; // 138
        chars.Add(new CharBox(idx++, left, capTop, left + 28f, capBottom));

        // Body text for all 6 lines. Lines 0-2 begin to the right of the drop cap.
        for (int line = 0; line < 6; line++)
        {
            float lineTop = LineTop(line);
            float lineBottom = lineTop + charH;
            float startX = line <= 2 ? left + 34f : left; // indent past the drop cap
            for (int c = 0; c < 8; c++)
            {
                float cl = startX + c * 8f;
                chars.Add(new CharBox(idx++, cl, lineTop, cl + 7f, lineBottom));
            }
        }

        float blockBottom = LineTop(5) + charH;        // 180
        float blockRight = left + 34f + 8 * 8f + 7f;   // past the indented body
        var block = new LayoutBlock
        {
            BBox = new BBox(left - 2f, top - 2f, blockRight - (left - 2f), blockBottom - (top - 2f) + 2f),
            Role = BlockRole.Text,
            Confidence = 0.9f,
        };

        var lines = LineDetector.DetectLines(block, chars, rgbBytes: [], imgW: 0, imgH: 0, scaleX: 1, scaleY: 1);

        // Expected: 6 distinct lines.
        //
        // Actual (bug): the drop cap's mid-Y clusters with line 1, and MakeLine's
        // min-top/max-bottom inflates that line's band to the full glyph height
        // [100,138]. NormalizeLines' >50%-overlap merge then cascades, swallowing
        // lines 0-2 into a single line — leaving 4.
        Assert.True(lines.Count == 6,
            $"drop cap collapsed spanned lines: expected 6, got {lines.Count} " +
            $"(heights=[{string.Join(", ", lines.Select(l => l.Height.ToString("0")))}])");

        // The mega-line is the visible artifact: no real text line is taller than
        // ~1.5 pitches, so a band spanning multiple lines is the smoking gun.
        Assert.All(lines, l => Assert.True(l.Height <= pitch * 1.5f,
            $"line height {l.Height:0} spans multiple text lines (pitch {pitch:0})"));
    }

    [Fact]
    public void CharClustering_MultipleOversizeGlyphs_OrderIndependent()
    {
        // REGRESSION: with two oversize glyphs near a line boundary, each glyph's
        // span and target line must be decided against the ORIGINAL clustered bands,
        // so the result cannot depend on the order PDFium emitted the glyphs. (The
        // earlier loop mutated bands mid-iteration and indexed by list position, so
        // swapping the two glyphs' emission order changed the detected line bands.)
        var line0 = new List<CharBox>();
        var line1 = new List<CharBox>();
        for (int c = 0; c < 6; c++)
        {
            float x = 140 + c * 8;
            line0.Add(new CharBox(100 + c, x, 100, x + 7, 110)); // line 0: y[100,110]
            line1.Add(new CharBox(200 + c, x, 130, x + 7, 140)); // line 1: y[130,140]
        }
        var g1 = new CharBox(1, 100, 100, 128, 128); // oversize; overlaps only line 0 in the originals
        var g2 = new CharBox(2, 100, 120, 125, 145); // oversize; overlaps only line 1 in the originals

        var block = new LayoutBlock { BBox = new BBox(98, 98, 200, 50), Role = BlockRole.Text, Confidence = 0.9f };

        List<CharBox> WithCapOrder(CharBox first, CharBox second)
        {
            var cs = new List<CharBox> { first, second };
            cs.AddRange(line0);
            cs.AddRange(line1);
            return cs;
        }

        var a = LineDetector.DetectLines(block, WithCapOrder(g1, g2), [], 0, 0, 1, 1);
        var b = LineDetector.DetectLines(block, WithCapOrder(g2, g1), [], 0, 0, 1, 1);

        Assert.Equal(a, b); // LineInfo is a record struct → value equality of the whole list
    }

    [Fact]
    public void CharClustering_FiltersCharsOutsideBlock()
    {
        // Chars from two lines, but block only covers the first
        var (_, chars) = MakeLines(lineCount: 2, charsPerLine: 5);
        var narrowBbox = new BBox(98, 98, 50, 14); // only encloses line 1
        var lines = LineDetector.DetectLinesFromChars(narrowBbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_SkipsDegenerateBoxes()
    {
        // Whitespace chars often have zero-width or zero-height boxes
        var chars = new List<CharBox>
        {
            new(0, 100, 100, 107, 110), // visible
            new(1, 108, 0,   108, 0),   // degenerate (space)
            new(2, 110, 100, 117, 110), // visible
        };
        var bbox = new BBox(98, 98, 25, 14);
        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
    }

    [Fact]
    public void CharClustering_EmptyInputReturnsEmpty()
    {
        var bbox = new BBox(0, 0, 100, 100);
        var lines = LineDetector.DetectLinesFromChars(bbox, []);
        Assert.Empty(lines);
    }

    [Fact]
    public void DisplayFormula_KeepsPerLineStructure()
    {
        // Stepwise derivations (γ₁ = Cov(…) / = Cov(…) / = -θσ²) are common in
        // math content and read line-by-line in rail mode. Display formulas
        // therefore must NOT collapse to one atomic line — char clustering
        // should expose the individual lines.
        var (block, chars) = MakeLines(lineCount: 4, charsPerLine: 8);
        block.Role = BlockRole.DisplayMath;

        var lines = LineDetector.DetectLines(block, chars, rgbBytes: [], imgW: 0, imgH: 0, scaleX: 1, scaleY: 1);
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void Table_RowReading_SplitsIntoRows()
    {
        // With table-row reading on (the default), a table is detected row-by-row
        // so rail mode can step through it — essential for reading financial
        // statements at high magnification.
        var (block, chars) = MakeLines(lineCount: 6, charsPerLine: 10);
        block.Role = BlockRole.Table;

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);
        Assert.Equal(6, lines.Count);
    }

    [Fact]
    public void Table_RowReadingDisabled_CollapsesToOneLine()
    {
        // With the flag off, a table stays atomic (legacy whole-table-as-one-line).
        var (block, chars) = MakeLines(lineCount: 6, charsPerLine: 10);
        block.Role = BlockRole.Table;

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1, tableRowReading: false);
        Assert.Single(lines);
    }

    // --- Cell navigation (table rows split into cells) ---

    /// <summary>
    /// Builds a Table block: <paramref name="rowCount"/> rows, each row laying down
    /// <paramref name="charsPerCell"/>-glyph runs at each X in <paramref name="colStarts"/>.
    /// Columns are separated by wide whitespace (the gaps in <paramref name="colStarts"/>),
    /// glyphs within a run by ~1px — exactly the whitespace-aligned layout of a financial
    /// statement.
    /// </summary>
    private static (LayoutBlock Block, List<CharBox> Chars) MakeTable(
        float[] colStarts, int rowCount = 3, int charsPerCell = 4,
        float charHeight = 10f, float rowPitch = 20f, float top = 100f)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        float maxRight = 0f;
        for (int r = 0; r < rowCount; r++)
        {
            float rowTop = top + r * rowPitch;
            float rowBottom = rowTop + charHeight;
            foreach (var colStart in colStarts)
                for (int c = 0; c < charsPerCell; c++)
                {
                    float cl = colStart + c * 8f;
                    chars.Add(new CharBox(idx++, cl, rowTop, cl + 7f, rowBottom));
                    maxRight = Math.Max(maxRight, cl + 7f);
                }
        }

        float left = colStarts[0] - 2f;
        float height = (rowCount - 1) * rowPitch + charHeight;
        var block = new LayoutBlock
        {
            BBox = new BBox(left, top - 2f, maxRight - left + 2f, height + 4f),
            Role = BlockRole.Table,
            Confidence = 0.9f,
        };
        return (block, chars);
    }

    [Fact]
    public void Table_CellNavigation_SplitsRowIntoCells()
    {
        // Three columns separated by wide gaps → three cells per row.
        var (block, chars) = MakeTable([100f, 300f, 450f]);

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1,
            tableRowReading: true, cellNavigation: true);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.NotNull(l.Cells);
            Assert.Equal(3, l.Cells!.Count);
        });
    }

    [Fact]
    public void Table_CellNavigation_DisabledByDefault_NoCells()
    {
        // Row reading on, cell navigation off (the default) → rows, but no cells.
        var (block, chars) = MakeTable([100f, 300f, 450f]);

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Null(l.Cells));
    }

    [Fact]
    public void Table_CellNavigation_RequiresRowReading()
    {
        // With row reading off the table is atomic; cells are never computed even
        // when cellNavigation is requested.
        var (block, chars) = MakeTable([100f, 300f, 450f]);

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1,
            tableRowReading: false, cellNavigation: true);

        Assert.Single(lines);
        Assert.Null(lines[0].Cells);
    }

    [Fact]
    public void Table_CellNavigation_CellsAreOrderedAndColumnAlignedAcrossRows()
    {
        var (block, chars) = MakeTable([100f, 300f, 450f]);

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1,
            tableRowReading: true, cellNavigation: true);

        foreach (var l in lines)
        {
            // Cells are listed left-to-right with strictly increasing centres.
            Assert.True(l.Cells![0].CenterX < l.Cells[1].CenterX);
            Assert.True(l.Cells[1].CenterX < l.Cells[2].CenterX);
        }
        // The Nth cell starts at the Nth column's left edge, identical across every row.
        Assert.All(lines, l =>
        {
            Assert.Equal(100f, l.Cells![0].X, 1f);
            Assert.Equal(300f, l.Cells[1].X, 1f);
            Assert.Equal(450f, l.Cells[2].X, 1f);
        });
    }

    [Fact]
    public void Table_CellNavigation_TightlySpacedGlyphsStayOneCell()
    {
        // A single column (no wide gaps) → one cell spanning the whole run, never
        // fragmented by the ordinary ~1px inter-glyph spacing.
        var (block, chars) = MakeTable([100f], charsPerCell: 8);

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1,
            tableRowReading: true, cellNavigation: true);

        Assert.All(lines, l => Assert.Single(l.Cells!));
    }

    [Fact]
    public void NonTable_CellNavigation_ProducesNoCells()
    {
        // cellNavigation only affects Table blocks; a Text block never gets cells.
        var (block, chars) = MakeTable([100f, 300f]);
        block.Role = BlockRole.Text;

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1,
            tableRowReading: true, cellNavigation: true);

        Assert.All(lines, l => Assert.Null(l.Cells));
    }

    [Fact]
    public void AtomicClass_Image_CollapsesToOneLine()
    {
        var (block, chars) = MakeLines(lineCount: 3, charsPerLine: 5);
        block.Role = BlockRole.Figure;

        var lines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);
        Assert.Single(lines);
    }

    [Fact]
    public void DetectLines_PrefersCharBoxesOverPixels()
    {
        // Char boxes describe two clear lines. Pixel bytes are empty (would
        // produce zero lines via pixel projection). The char path should win.
        var (block, chars) = MakeLines(lineCount: 2, charsPerLine: 6);

        var lines = LineDetector.DetectLines(block, chars, rgbBytes: [], imgW: 0, imgH: 0, scaleX: 1, scaleY: 1);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void DetectLines_FallsBackToPixelsWhenNoChars()
    {
        // No char boxes — the pixel path runs. Build a tiny 20×30 RGB image
        // with three dark bands separated by white gaps; expect three lines.
        const int imgW = 20;
        const int imgH = 30;
        var rgb = new byte[imgW * imgH * 3];
        Array.Fill(rgb, (byte)255); // white background

        void DrawBand(int yStart, int yEnd)
        {
            for (int y = yStart; y < yEnd; y++)
                for (int x = 0; x < imgW; x++)
                {
                    int idx = (y * imgW + x) * 3;
                    rgb[idx] = rgb[idx + 1] = rgb[idx + 2] = 0;
                }
        }

        DrawBand(2, 7);
        DrawBand(12, 17);
        DrawBand(22, 27);

        var block = new LayoutBlock
        {
            BBox = new BBox(0, 0, imgW, imgH),
            Role = BlockRole.Text,
        };

        var lines = LineDetector.DetectLines(block, null, rgb, imgW, imgH, scaleX: 1f, scaleY: 1f);
        Assert.Equal(3, lines.Count);
    }

    // --- P0: horizontal extent, normalization, math merge ---

    [Fact]
    public void CharClustering_PopulatesHorizontalExtent()
    {
        // One line of chars from x=100 (left of first) to x=157 (right of last).
        var chars = new List<CharBox>();
        for (int c = 0; c < 8; c++)
            chars.Add(new CharBox(c, 100 + c * 8, 100, 100 + c * 8 + 7, 110));
        var bbox = new BBox(90, 98, 90, 14);

        var lines = LineDetector.DetectLinesFromChars(bbox, chars);
        Assert.Single(lines);
        Assert.Equal(100f, lines[0].X, 1f);              // min char left (c=0)
        Assert.Equal(63f, lines[0].Width, 1f);           // max right (156+7=163) - 100
    }

    [Fact]
    public void NormalizeLines_SortsClampsAndMergesOverlaps()
    {
        var block = new BBox(0, 0, 100, 100);
        var input = new List<LineInfo>
        {
            new(60, 10, 0, 100),
            new(20, 10, 0, 100),
            new(22, 10, 0, 100),   // overlaps the y=20 line -> merge
            new(5, 0, 0, 100),     // zero height -> dropped
            new(200, 10, 0, 100),  // outside block -> clamped away / dropped
        };
        var outp = LineDetector.NormalizeLines(input, block);

        for (int i = 1; i < outp.Count; i++)
            Assert.True(outp[i - 1].Y <= outp[i].Y, "lines must be sorted by Y");
        foreach (var l in outp)
        {
            Assert.True(l.Height > 0);
            Assert.True(l.Y - l.Height / 2 >= -0.01f && l.Y + l.Height / 2 <= 100.01f, "within block");
        }
        // y=20 and y=22 merged -> two distinct lines remain (merged-20, 60).
        Assert.Equal(2, outp.Count);
    }

    [Fact]
    public void NormalizeLines_HorizontalExtentOutsideBlock_StaysInsideBlock()
    {
        // A line whose X/Width lies entirely to the right of the block must not
        // be emitted outside the block (renderers draw highlight/crop from X/Width).
        var block = new BBox(40, 50, 200, 100);
        var input = new List<LineInfo> { new(80, 20, 400, 50) }; // extent [400,450], block right = 240

        var outp = LineDetector.NormalizeLines(input, block);

        Assert.Single(outp);
        Assert.True(outp[0].X >= 40 - 0.01f, "line left inside block");
        Assert.True(outp[0].X + outp[0].Width <= 240 + 0.01f, "line right inside block");
        Assert.True(outp[0].Width > 0);
    }

    [Fact]
    public void DisplayMath_MergesTightRowsThatTextWouldSplit()
    {
        // Rows spaced 1.1x char height: Text splits them, DisplayMath (1.3x
        // threshold) merges — without collapsing to atomic.
        var (block, chars) = MakeLines(lineCount: 4, charsPerLine: 6, charHeight: 10f, lineGap: 1f);

        block.Role = BlockRole.Text;
        var textLines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);

        block.Role = BlockRole.DisplayMath;
        var mathLines = LineDetector.DetectLines(block, chars, [], 0, 0, 1, 1);

        Assert.True(mathLines.Count < textLines.Count,
            $"math({mathLines.Count}) should merge tighter than text({textLines.Count})");
    }
}
