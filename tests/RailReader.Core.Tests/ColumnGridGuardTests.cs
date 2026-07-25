using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for <see cref="LineDetector.GridMatchesGlyphs"/> — the check that a column grid
/// recovered from vertical rules is corroborated by the table's own content.
///
/// <para>
/// The rule detector reads long dark pixel runs, which a figure border, a shaded code block or
/// a tall bracket can produce just as well as a column separator. Accepting those unchecked
/// hands the reader a grid whose columns cut nothing, and cell navigation then steps through
/// boundaries that do not exist. Adapted from tabula-java's <c>isTabular</c>, which likewise
/// validates ruled structure against the structure the text alone implies.
/// </para>
/// </summary>
public class ColumnGridGuardTests
{
    private const int ImgW = 400, ImgH = 200;

    private static List<LineDetector.GlyphRef> Run(float left, float right) => [new(left, right)];

    private static List<LineDetector.GlyphRef> Runs(params (float Left, float Right)[] runs)
    {
        var list = new List<LineDetector.GlyphRef>();
        foreach (var (l, r) in runs) list.Add(new LineDetector.GlyphRef(l, r));
        return list;
    }

    [Fact]
    public void GridWhoseColumnsTheContentPopulates_IsAccepted()
    {
        List<float> bounds = [100f, 200f, 300f, 400f];
        List<LineDetector.GlyphRef>?[] rows =
        [
            Runs((110f, 150f), (210f, 250f), (310f, 350f)),
            Runs((110f, 150f), (210f, 250f), (310f, 350f)),
        ];

        Assert.True(LineDetector.GridMatchesGlyphs(bounds, rows, 10f));
    }

    [Fact]
    public void GridNoRowSpans_IsRejected()
    {
        // Every row's content sits in the first column: the boundaries separate nothing.
        List<float> bounds = [100f, 200f, 300f, 400f];
        List<LineDetector.GlyphRef>?[] rows = [Run(110f, 150f), Run(110f, 160f), Run(110f, 140f)];

        Assert.False(LineDetector.GridMatchesGlyphs(bounds, rows, 10f));
    }

    [Fact]
    public void GridClaimingFarMoreColumnsThanAnyRowUses_IsRejected()
    {
        // Six columns, but no row ever populates more than two — the signature of stray
        // vertical strokes rather than a table's rules.
        List<float> bounds = [0f, 50f, 100f, 150f, 200f, 250f, 300f];
        List<LineDetector.GlyphRef>?[] rows =
        [
            Runs((10f, 40f), (60f, 90f)),
            Runs((10f, 40f), (60f, 90f)),
        ];

        Assert.False(LineDetector.GridMatchesGlyphs(bounds, rows, 10f));
    }

    [Fact]
    public void GridThatSwallowsASplitRowInOneColumn_IsRejected()
    {
        // The row clearly has two cells (a wide gap the glyph split would cut), yet the grid
        // puts both inside one column: the boundaries are in the wrong places.
        List<float> bounds = [100f, 300f, 400f, 500f];
        List<LineDetector.GlyphRef>?[] rows =
        [
            Runs((110f, 150f), (240f, 280f)),
            Runs((110f, 150f), (240f, 280f)),
        ];

        Assert.False(LineDetector.GridMatchesGlyphs(bounds, rows, 10f));
    }

    [Fact]
    public void EmptyOrDegenerateInput_IsRejected()
    {
        Assert.False(LineDetector.GridMatchesGlyphs([100f, 200f], [null, null], 10f));
        Assert.False(LineDetector.GridMatchesGlyphs([100f], [Run(110f, 150f)], 10f));
    }

    // --- End to end, through the pixel-rule detector ---

    /// <summary>
    /// A white pixmap with full-height dark columns at the given x positions — what a ruled
    /// table looks like to <see cref="LineDetector.DetectColumnGrid"/>, and equally what a
    /// figure border looks like.
    /// </summary>
    private static byte[] PixmapWithVerticalLines(params int[] xs)
    {
        var px = new byte[ImgW * ImgH * 3];
        Array.Fill(px, (byte)255);
        foreach (int x in xs)
            for (int y = 0; y < ImgH; y++)
            {
                int i = (y * ImgW + x) * 3;
                px[i] = px[i + 1] = px[i + 2] = 0;
            }
        return px;
    }

    private static (LayoutBlock Block, List<CharBox> Chars) TableWithContentAt(float[] columnLefts)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        for (int r = 0; r < 3; r++)
        {
            float top = 60f + r * 16f;
            foreach (float col in columnLefts)
                for (int g = 0; g < 3; g++)
                {
                    float left = col + g * 8f;
                    chars.Add(new CharBox(idx++, left, top, left + 7f, top + 10f));
                }
        }

        var block = new LayoutBlock
        {
            BBox = new BBox(100f, 55f, 240f, 55f),
            Role = BlockRole.Table,
        };
        return (block, chars);
    }

    [Fact]
    public void RulesWithNoMatchingContent_DoNotBecomeColumns()
    {
        // Two full-height dark columns inside the block, but every row's content is in one
        // narrow strip — e.g. a bordered figure sharing the block with a caption.
        var (block, chars) = TableWithContentAt([110f]);

        var rows = LineDetector.DetectLines(block, chars, PixmapWithVerticalLines(180, 260),
            ImgW, ImgH, 1f, 1f, tableRowReading: true, cellNavigation: true);

        // Guard rejects the grid; with a single run per row the band path declines too, so the
        // per-row split runs and each row gets exactly its own content as one cell.
        Assert.All(rows, r =>
        {
            Assert.Single(r.Cells!);
            Assert.InRange(r.Cells![0].X, 109f, 111f);
        });
    }

    [Fact]
    public void RulesWithMatchingContent_StillBecomeColumns()
    {
        // The same rules, now with content in the three regions they define: this is a real
        // ruled table and must keep working exactly as before the guard was added.
        var (block, chars) = TableWithContentAt([110f, 200f, 280f]);

        var rows = LineDetector.DetectLines(block, chars, PixmapWithVerticalLines(180, 260),
            ImgW, ImgH, 1f, 1f, tableRowReading: true, cellNavigation: true);

        Assert.All(rows, r => Assert.Equal(3, r.Cells!.Count));
        // Boundaries come from the rules (180/260), not from the glyph-derived band midpoints.
        var cells = rows[0].Cells!;
        Assert.Equal(180f, cells[0].X + cells[0].Width, 1f);
        Assert.Equal(260f, cells[1].X + cells[1].Width, 1f);
    }
}
