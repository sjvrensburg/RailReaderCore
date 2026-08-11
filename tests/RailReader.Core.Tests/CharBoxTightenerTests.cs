using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Deterministic, model-free tests for <see cref="CharBoxTightener"/> — synthetic RGB pixmaps
/// with known ink placement, so the expected tightened extent is exact rather than
/// eyeballed. The real-scan visual check for this feature (railreader2#209) lives in
/// <c>RapidOcrServiceTests</c>/manual verification against the reporter's attached PDF.
/// </summary>
public class CharBoxTightenerTests
{
    private const int W = 40, H = 20;

    // A single black column-run [inkLeft, inkRight] on an otherwise white pixmap.
    private static byte[] WhiteWithBlackColumns(int inkLeft, int inkRight, int inkTop = 0, int inkBottom = H)
    {
        var rgb = new byte[W * H * 3];
        Array.Fill(rgb, (byte)255);
        for (int y = inkTop; y < inkBottom; y++)
            for (int x = inkLeft; x < inkRight; x++)
            {
                int idx = (y * W + x) * 3;
                rgb[idx] = rgb[idx + 1] = rgb[idx + 2] = 0;
            }
        return rgb;
    }

    [Fact]
    public void Tighten_ShrinksAnOversizedBoxToItsRealInk()
    {
        // Ink only spans [15,20); the estimated box is far wider, [5,35).
        var rgb = WhiteWithBlackColumns(inkLeft: 15, inkRight: 20);
        var box = new CharBox(0, 5, 0, 35, H);

        var result = CharBoxTightener.Tighten([box], rgb, W, H);

        Assert.Single(result);
        var r = result[0];
        Assert.True(r.Left > box.Left, "tightened box should have moved its left edge inward");
        Assert.True(r.Right < box.Right, "tightened box should have moved its right edge inward");
        // Within padding of the true ink run.
        Assert.InRange(r.Left, 13, 16);
        Assert.InRange(r.Right, 19, 22);
    }

    [Fact]
    public void Tighten_NeverWidensPastTheOriginalEstimate()
    {
        // Ink fills the whole box and beyond conceptually (simulate by filling exactly the box
        // range) — tightening must never report a box wider than what was handed in.
        var rgb = WhiteWithBlackColumns(inkLeft: 0, inkRight: W);
        var box = new CharBox(0, 10, 0, 20, H);

        var result = CharBoxTightener.Tighten([box], rgb, W, H);

        Assert.True(result[0].Left >= box.Left);
        Assert.True(result[0].Right <= box.Right);
    }

    [Fact]
    public void Tighten_LeavesABoxWithNoInkUnchanged()
    {
        var rgb = new byte[W * H * 3];
        Array.Fill(rgb, (byte)255); // all white — no ink anywhere
        var box = new CharBox(0, 10, 0, 20, H);

        var result = CharBoxTightener.Tighten([box], rgb, W, H);

        Assert.Equal(box, result[0]);
    }

    [Fact]
    public void Tighten_DiscardsAnImplausiblyThinResult()
    {
        // A single stray dark pixel (below the min-ink-row-fraction and, even if it counted,
        // would produce a near-zero-width result) must not collapse the box.
        var rgb = WhiteWithBlackColumns(inkLeft: 20, inkRight: 21, inkTop: 0, inkBottom: 1);
        var box = new CharBox(0, 10, 0, 20, H);

        var result = CharBoxTightener.Tighten([box], rgb, W, H);

        Assert.Equal(box, result[0]);
    }

    [Fact]
    public void Tighten_LeavesTopAndBottomUnchanged()
    {
        var rgb = WhiteWithBlackColumns(inkLeft: 15, inkRight: 20);
        var box = new CharBox(0, 5, 3, 35, 17);

        var result = CharBoxTightener.Tighten([box], rgb, W, H);

        Assert.Equal(box.Top, result[0].Top);
        Assert.Equal(box.Bottom, result[0].Bottom);
    }

    [Fact]
    public void Tighten_ProcessesEachBoxIndependently()
    {
        var rgb = WhiteWithBlackColumns(inkLeft: 5, inkRight: 8);
        // Overlay a second ink run for the second box's own window.
        for (int y = 0; y < H; y++)
            for (int x = 25; x < 30; x++)
            {
                int idx = (y * W + x) * 3;
                rgb[idx] = rgb[idx + 1] = rgb[idx + 2] = 0;
            }
        var boxes = new List<CharBox>
        {
            new(0, 0, 0, 10, H),
            new(1, 20, 0, 35, H),
        };

        var result = CharBoxTightener.Tighten(boxes, rgb, W, H);

        Assert.Equal(2, result.Count);
        Assert.InRange(result[0].Left, 3, 6);
        Assert.InRange(result[0].Right, 7, 10);
        Assert.InRange(result[1].Left, 23, 26);
        Assert.InRange(result[1].Right, 29, 32);
    }

    [Fact]
    public void Tighten_EmptyListReturnsEmptyList()
    {
        var result = CharBoxTightener.Tighten([], [], W, H);
        Assert.Empty(result);
    }
}
