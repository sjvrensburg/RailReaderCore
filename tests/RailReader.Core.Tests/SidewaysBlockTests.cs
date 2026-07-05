using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Phase-2 sideways-text tests against the LaTeX fixtures: displayed glyph
/// angles (CharBox.Angle), sideways-block detection (OrientationDetector),
/// the atomic line collapse for rotated blocks, upright VLM crops, and the
/// controller's rotate-to-read.
/// </summary>
public class SidewaysBlockTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "rotation", name);

    private static PageText Extract(string fixture, int page, int viewRotation = 0)
        => new PdfTextService().ExtractPageText(
            File.ReadAllBytes(FixturePath(fixture)), page, viewRotation);

    private static List<CharBox> MarkerChars(PageText pageText, string marker)
    {
        int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(mi >= 0, $"'{marker}' not found");
        return pageText.CharBoxes.Where(c => c.Index >= mi && c.Index < mi + marker.Length).ToList();
    }

    // --- CharBox.Angle (displayed clockwise degrees) --------------------------------

    [Fact]
    public void Rotated_content_reports_270_upright_content_reports_0()
    {
        var pageText = Extract("sideways-table.pdf", 0);
        Assert.All(MarkerChars(pageText, "Quarter"), c => Assert.Equal(270f, c.Angle));
        Assert.All(MarkerChars(pageText, "upright"), c => Assert.Equal(0f, c.Angle));
    }

    [Fact]
    public void Page_rotate_attribute_composes_into_displayed_angle()
    {
        // Upright content on a /Rotate 90 page displays turned 90° clockwise.
        var pageText = Extract("rotate-suite.pdf", 1);
        Assert.All(MarkerChars(pageText, "MARKER"), c => Assert.Equal(90f, c.Angle));
    }

    [Fact]
    public void View_rotation_composes_into_displayed_angle()
    {
        // The sideways scan (glyphs at 270°) reads upright after one clockwise turn.
        var pageText = Extract("landscape-scan.pdf", 0, viewRotation: 1);
        Assert.All(MarkerChars(pageText, "SCANMARK"), c => Assert.Equal(0f, c.Angle));
    }

    // --- OrientationDetector ---------------------------------------------------------

    private static CharBox Box(int i, float x, float y, float angle)
        => new(i, x, y, x + 5, y + 8, angle);

    [Fact]
    public void DetectFromChars_flags_majority_rotated_block()
    {
        var chars = Enumerable.Range(0, 10).Select(i => Box(i, 10 + i * 6, 20, 270f)).ToList();
        Assert.Equal(1, OrientationDetector.DetectFromChars(new BBox(0, 0, 100, 50), chars));
    }

    [Fact]
    public void DetectFromChars_returns_zero_for_upright_and_null_for_thin_or_mixed_evidence()
    {
        var upright = Enumerable.Range(0, 10).Select(i => Box(i, 10 + i * 6, 20, 0f)).ToList();
        Assert.Equal(0, OrientationDetector.DetectFromChars(new BBox(0, 0, 100, 50), upright));

        var few = upright.Take(3).ToList();
        Assert.Null(OrientationDetector.DetectFromChars(new BBox(0, 0, 100, 50), few));

        var mixed = Enumerable.Range(0, 10)
            .Select(i => Box(i, 10 + i * 6, 20, i % 2 == 0 ? 0f : 270f)).ToList();
        Assert.Null(OrientationDetector.DetectFromChars(new BBox(0, 0, 100, 50), mixed));
    }

    [Fact]
    public void DetectFromChars_ignores_chars_outside_the_block()
    {
        // Rotated glyphs inside the block, plenty of upright ones outside.
        var chars = Enumerable.Range(0, 6).Select(i => Box(i, 10 + i * 6, 20, 270f))
            .Concat(Enumerable.Range(6, 30).Select(i => Box(i, 300 + i * 6, 400, 0f)))
            .ToList();
        Assert.Equal(1, OrientationDetector.DetectFromChars(new BBox(0, 0, 100, 50), chars));
    }

    [Fact]
    public void DetectFromChars_finds_the_fixtures_sideways_table()
    {
        var pageText = Extract("sideways-table.pdf", 0);
        // Bound the rotated region from the glyphs themselves (the union of all
        // 270° chars), as the layout model would around the table.
        var rotated = pageText.CharBoxes.Where(c => c.Angle == 270f && c.Right > c.Left).ToList();
        Assert.True(rotated.Count > 20);
        var bbox = new BBox(
            rotated.Min(c => c.Left) - 2, rotated.Min(c => c.Top) - 2,
            rotated.Max(c => c.Right) - rotated.Min(c => c.Left) + 4,
            rotated.Max(c => c.Bottom) - rotated.Min(c => c.Top) + 4);

        Assert.Equal(1, OrientationDetector.DetectFromChars(bbox, pageText.CharBoxes));
    }

    [Fact]
    public void Pixel_fallback_distinguishes_horizontal_from_vertical_banding()
    {
        const int w = 100, h = 100;
        var horizontal = new byte[w * h * 3];
        var vertical = new byte[w * h * 3];
        Array.Fill(horizontal, (byte)255);
        Array.Fill(vertical, (byte)255);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (y % 10 < 4) // horizontal text-line bands
                    horizontal[(y * w + x) * 3] = horizontal[(y * w + x) * 3 + 1] = horizontal[(y * w + x) * 3 + 2] = 0;
                if (x % 10 < 4) // the 90°-rotated transpose
                    vertical[(y * w + x) * 3] = vertical[(y * w + x) * 3 + 1] = vertical[(y * w + x) * 3 + 2] = 0;
            }

        var bbox = new BBox(0, 0, w, h);
        Assert.False(OrientationDetector.DetectSidewaysFromPixels(bbox, horizontal, w, h, 1f, 1f));
        Assert.True(OrientationDetector.DetectSidewaysFromPixels(bbox, vertical, w, h, 1f, 1f));
    }

    // --- Line detection --------------------------------------------------------------

    [Fact]
    public void Sideways_block_collapses_to_single_atomic_line()
    {
        var pageText = Extract("sideways-table.pdf", 0);
        var rotated = pageText.CharBoxes.Where(c => c.Angle == 270f && c.Right > c.Left).ToList();
        var bbox = new BBox(
            rotated.Min(c => c.Left) - 2, rotated.Min(c => c.Top) - 2,
            rotated.Max(c => c.Right) - rotated.Min(c => c.Left) + 4,
            rotated.Max(c => c.Bottom) - rotated.Min(c => c.Top) + 4);

        var block = new LayoutBlock { BBox = bbox, Role = BlockRole.Text, UprightTurns = 1 };
        var lines = LineDetector.DetectLines(block, pageText.CharBoxes, [], 0, 0, 1f, 1f);

        var line = Assert.Single(lines);
        Assert.Equal(bbox.H, line.Height, 0.5);

        // Sanity: WITHOUT the flag the same block shatters into per-glyph noise.
        var unflagged = new LayoutBlock { BBox = bbox, Role = BlockRole.Text };
        var shattered = LineDetector.DetectLines(unflagged, pageText.CharBoxes, [], 0, 0, 1f, 1f);
        Assert.True(shattered.Count > 3, $"expected shattering without the flag, got {shattered.Count}");
    }

    [Fact]
    public void PostProcess_sets_UprightTurns_from_glyph_angles()
    {
        var pageText = Extract("sideways-table.pdf", 0);
        var rotated = pageText.CharBoxes.Where(c => c.Angle == 270f && c.Right > c.Left).ToList();
        float tableTop = rotated.Min(c => c.Top);
        // First paragraph only (above the table) — a full-page prose box would
        // vertically overlap the table block and overlap resolution would rewrite it.
        var upright = pageText.CharBoxes
            .Where(c => c.Angle == 0f && c.Right > c.Left && c.Bottom < tableTop).ToList();

        var tableBlock = new LayoutBlock
        {
            BBox = new BBox(
                rotated.Min(c => c.Left) - 2, tableTop - 2,
                rotated.Max(c => c.Right) - rotated.Min(c => c.Left) + 4,
                rotated.Max(c => c.Bottom) - tableTop + 4),
            Role = BlockRole.Text,
        };
        var proseBlock = new LayoutBlock
        {
            BBox = new BBox(
                upright.Min(c => c.Left) - 2, upright.Min(c => c.Top) - 2,
                upright.Max(c => c.Right) - upright.Min(c => c.Left) + 4,
                upright.Max(c => c.Bottom) - upright.Min(c => c.Top) + 4),
            Role = BlockRole.Text,
        };

        var blocks = new List<LayoutBlock> { proseBlock, tableBlock };
        BlockPostProcessor.PostProcess(blocks, [], 0, 0, 1f, 1f, pageText.CharBoxes);

        // Assert via the list — overlap resolution may replace instances.
        Assert.Equal(0, blocks[0].UprightTurns);
        Assert.Equal(1, blocks[1].UprightTurns);
        Assert.Single(blocks[1].Lines); // atomic
    }

    // --- VLM crop rotation -------------------------------------------------------------

    [Fact]
    public void Block_crop_is_rotated_upright_for_sideways_blocks()
    {
        var pdf = new SkiaPdfService(FixturePath("sideways-table.pdf"));
        var (pageW, pageH) = pdf.GetPageSize(0);

        var pageText = new PdfTextService().ExtractPageText(pdf.PdfBytes, 0);
        var rotated = pageText.CharBoxes.Where(c => c.Angle == 270f && c.Right > c.Left).ToList();
        var bbox = new BBox(
            rotated.Min(c => c.Left), rotated.Min(c => c.Top),
            rotated.Max(c => c.Right) - rotated.Min(c => c.Left),
            rotated.Max(c => c.Bottom) - rotated.Min(c => c.Top));
        Assert.True(bbox.H > bbox.W, "sideways table region is taller than wide");

        var flat = BlockCropRenderer.RenderBlockAsPng(pdf, 0, bbox, pageW, pageH);
        var uprighted = BlockCropRenderer.RenderBlockAsPng(pdf, 0, bbox, pageW, pageH, uprightTurns: 1);
        Assert.NotNull(flat);
        Assert.NotNull(uprighted);

        using var flatBmp = SKBitmap.Decode(flat);
        using var uprightBmp = SKBitmap.Decode(uprighted);
        Assert.True(flatBmp.Height > flatBmp.Width);
        Assert.True(uprightBmp.Width > uprightBmp.Height, "crop should be landscape after upright turn");
        Assert.Equal(flatBmp.Width, uprightBmp.Height);
        Assert.Equal(flatBmp.Height, uprightBmp.Width);
    }

    // --- Rotate-to-read ------------------------------------------------------------------

    [Fact]
    public void RotateViewToReadBlock_rotates_by_the_blocks_upright_turns()
    {
        var config = new AppConfig();
        using var controller = new DocumentController(config.ToCoreSettings(), config,
            AnnotationService.Default, new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        var doc = controller.CreateDocument(FixturePath("sideways-table.pdf"));
        doc.LoadPageBitmap();
        controller.AddDocument(doc);

        // Seat an analysis whose only block is sideways (UprightTurns = 1).
        var analysis = new PageAnalysis
        {
            Blocks =
            [
                new LayoutBlock
                {
                    BBox = new BBox(100, 100, 200, 400),
                    Role = BlockRole.Text,
                    UprightTurns = 1,
                    Lines = [new LineInfo(300, 400, 100, 200)],
                },
            ],
            PageWidth = doc.PageWidth,
            PageHeight = doc.PageHeight,
        };
        var vp = doc.Primary;
        vp.Rail.SetAnalysis(analysis, new HashSet<BlockRole> { BlockRole.Text });
        vp.Rail.CurrentBlock = 0;

        Assert.Equal(1, controller.CurrentBlockUprightTurns);
        Assert.True(controller.RotateViewToReadBlock());
        Assert.Equal(1, doc.ViewRotation);
    }

    [Fact]
    public void RotateViewToReadBlock_is_a_noop_for_upright_blocks()
    {
        var config = new AppConfig();
        using var controller = new DocumentController(config.ToCoreSettings(), config,
            AnnotationService.Default, new SynchronousThreadMarshaller(), TestFixtures.CreatePdfFactory());
        var doc = controller.CreateDocument(FixturePath("sideways-table.pdf"));
        doc.LoadPageBitmap();
        controller.AddDocument(doc);

        Assert.Equal(0, controller.CurrentBlockUprightTurns);
        Assert.False(controller.RotateViewToReadBlock());
        Assert.Equal(0, doc.ViewRotation);
    }
}
