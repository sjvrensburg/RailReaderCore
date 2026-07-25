using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for vector ruling extraction (<see cref="IPdfRulingService"/>) and the exact table
/// column grid it enables.
///
/// <para>
/// The raster path infers column separators from long dark pixel runs, which works but is
/// bounded by the analysis pixmap's resolution and can be fooled by anything else that is dark
/// and tall. Where a backend can read the page's drawing operators, the separators are simply
/// known. Approach adapted from tabula-java's object extractor.
/// </para>
/// <para>
/// These use PdfPig only, never mixing with PDFium services in one test method (see
/// <see cref="PdfPigServiceTests"/>).
/// </para>
/// </summary>
public class VectorRulingTests
{
    /// <summary>
    /// Writes a one-page PDF with a ruled grid: vertical rules at the given x positions and
    /// horizontal rules at the given y positions, drawn as strokes.
    /// </summary>
    private static string RuledPdf(float[] verticalXs, float[] horizontalYs,
        float strokeWidth = 0.5f, bool asFilledRects = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_rules_{Guid.NewGuid():N}.pdf");
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);
        using var canvas = doc.BeginPage(612, 792);

        using var stroke = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = false,
            Style = asFilledRects ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
        };

        foreach (float x in verticalXs)
        {
            if (asFilledRects) canvas.DrawRect(x - strokeWidth / 2f, 100f, strokeWidth, 500f, stroke);
            else canvas.DrawLine(x, 100f, x, 600f, stroke);
        }
        foreach (float y in horizontalYs)
        {
            if (asFilledRects) canvas.DrawRect(80f, y - strokeWidth / 2f, 450f, strokeWidth, stroke);
            else canvas.DrawLine(80f, y, 530f, y, stroke);
        }

        doc.EndPage();
        doc.Close();
        return path;
    }

    private static PageRulings Extract(string pdfPath, int viewRotation = 0)
    {
        var bytes = File.ReadAllBytes(pdfPath);
        return new RailReader.Core.PdfPig.PdfTextService().ExtractRulings(bytes, 0, viewRotation);
    }

    [Fact]
    public void StrokedRules_AreFoundWithTheirPositionsAndExtents()
    {
        var path = RuledPdf([150f, 300f, 450f], [100f, 600f]);
        try
        {
            var rulings = Extract(path);

            Assert.Equal(3, rulings.Vertical.Count);
            Assert.Equal([150f, 300f, 450f], rulings.Vertical.Select(r => (float)Math.Round(r.Position)));
            // Top-left origin, Y-down: the rules run from y=100 to y=600 as drawn.
            Assert.All(rulings.Vertical, r =>
            {
                Assert.Equal(100f, r.Start, 1f);
                Assert.Equal(600f, r.End, 1f);
            });
            Assert.Equal(2, rulings.Horizontal.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HairlinesDrawnAsFilledRectangles_CollapseToOneRulingEach()
    {
        // A filled rectangle contributes both of its long edges. Without merging, every
        // hairline would present as two column separators a fraction of a point apart.
        var path = RuledPdf([150f, 300f, 450f], [], strokeWidth: 0.4f, asFilledRects: true);
        try
        {
            var rulings = Extract(path);

            Assert.Equal(3, rulings.Vertical.Count);
            Assert.Equal([150f, 300f, 450f], rulings.Vertical.Select(r => (float)Math.Round(r.Position)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DiagonalsAndCurves_AreNotRulings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_diag_{Guid.NewGuid():N}.pdf");
        using (var stream = File.Create(path))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var canvas = doc.BeginPage(612, 792);
            using var stroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
            canvas.DrawLine(100f, 100f, 400f, 500f, stroke);         // diagonal
            using var curve = new SKPath();
            curve.MoveTo(100f, 600f);
            curve.CubicTo(200f, 650f, 300f, 550f, 400f, 600f);       // Bézier
            canvas.DrawPath(curve, stroke);
            doc.EndPage();
            doc.Close();
        }

        try
        {
            var rulings = Extract(path);
            Assert.True(rulings.IsEmpty, "a trend line and a curve are not table rules");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PageWithNoVectorContent_YieldsNothing()
    {
        var path = TestFixtures.GetTestPdfPath();   // text only
        Assert.True(Extract(path).IsEmpty);
    }

    [Fact]
    public void ViewRotation_SwapsTheAxes()
    {
        // Under a quarter-turn the page's vertical rules read as horizontal, in the same
        // displayed frame the char boxes and layout blocks use.
        var path = RuledPdf([150f, 300f, 450f], []);
        try
        {
            var upright = Extract(path);
            var turned = Extract(path, viewRotation: 1);

            Assert.Equal(3, upright.Vertical.Count);
            Assert.Empty(upright.Horizontal);
            Assert.Empty(turned.Vertical);
            Assert.Equal(3, turned.Horizontal.Count);
        }
        finally { File.Delete(path); }
    }

    // --- Consumption: rulings drive the column grid ---

    private static (LayoutBlock Block, List<CharBox> Chars) TableBlock(float[] columnLefts)
    {
        var chars = new List<CharBox>();
        int idx = 0;
        for (int r = 0; r < 3; r++)
        {
            float top = 120f + r * 20f;
            foreach (float col in columnLefts)
                for (int g = 0; g < 3; g++)
                {
                    float left = col + g * 8f;
                    chars.Add(new CharBox(idx++, left, top, left + 7f, top + 10f));
                }
        }
        return (new LayoutBlock { BBox = new BBox(100f, 110f, 400f, 70f), Role = BlockRole.Table }, chars);
    }

    [Fact]
    public void RulingsProduceColumnBoundariesAtTheRules()
    {
        var rulings = new PageRulings(
            [new RulingSegment(200f, 100f, 600f), new RulingSegment(350f, 100f, 600f)],
            []);

        var bounds = LineDetector.DetectColumnGridFromRulings(rulings, new BBox(100f, 110f, 400f, 70f));

        Assert.NotNull(bounds);
        Assert.Equal([100f, 200f, 350f, 500f], bounds!);
    }

    [Fact]
    public void ShortRulesThatDoNotSpanTheBlock_AreNotColumnSeparators()
    {
        // A rule underlining one header cell separates nothing below it.
        var rulings = new PageRulings(
            [new RulingSegment(200f, 110f, 120f), new RulingSegment(350f, 110f, 120f)],
            []);

        Assert.Null(LineDetector.DetectColumnGridFromRulings(rulings, new BBox(100f, 110f, 400f, 70f)));
    }

    [Fact]
    public void RulingsBeatTheRasterScan()
    {
        // The pixmap is blank, so the raster path would find no grid at all — any grid in the
        // result can only have come from the vector rules.
        var (block, chars) = TableBlock([110f, 210f, 360f]);
        var blank = new byte[600 * 300 * 3];
        Array.Fill(blank, (byte)255);

        var rulings = new PageRulings(
            [new RulingSegment(200f, 100f, 600f), new RulingSegment(350f, 100f, 600f)],
            []);

        var rows = LineDetector.DetectLines(block, chars, blank, 600, 300, 1f, 1f,
            tableRowReading: true, cellNavigation: true, ocrLines: null, rulings: rulings);

        Assert.All(rows, r => Assert.Equal(3, r.Cells!.Count));
        var cells = rows[0].Cells!;
        Assert.Equal(200f, cells[0].X + cells[0].Width, 1f);
        Assert.Equal(350f, cells[1].X + cells[1].Width, 1f);
    }

    [Fact]
    public void RulingsThatContentContradicts_AreStillRejected()
    {
        // The guard applies to vector rules exactly as it does to raster ones: rules the
        // content does not populate are not this table's columns.
        var (block, chars) = TableBlock([110f]);
        var blank = new byte[600 * 300 * 3];
        Array.Fill(blank, (byte)255);

        var rulings = new PageRulings(
            [new RulingSegment(200f, 100f, 600f), new RulingSegment(350f, 100f, 600f)],
            []);

        var rows = LineDetector.DetectLines(block, chars, blank, 600, 300, 1f, 1f,
            tableRowReading: true, cellNavigation: true, ocrLines: null, rulings: rulings);

        Assert.All(rows, r => Assert.Single(r.Cells!));
    }

    [Fact]
    public void NullOrEmptyRulings_FallBackWithoutComplaint()
    {
        var block = new BBox(100f, 110f, 400f, 70f);

        Assert.Null(LineDetector.DetectColumnGridFromRulings(null, block));
        Assert.Null(LineDetector.DetectColumnGridFromRulings(PageRulings.Empty, block));
        Assert.Null(LineDetector.DetectColumnGridFromRulings(
            new PageRulings([new RulingSegment(200f, 100f, 600f)], []), block));   // one rule is not a grid
    }

    [Fact]
    public void PdfPigFactorysTextService_AdvertisesRulingSupportThroughTheGate()
    {
        // The gated wrapper must forward IPdfRulingService — Core discovers the capability by
        // casting, so a wrapper that only implements IPdfTextService hides it entirely.
        var textService = new RailReader.Renderer.PdfPigSkia.PdfPigSkiaPdfServiceFactory()
            .CreatePdfTextService();

        Assert.IsAssignableFrom<IPdfRulingService>(textService);
    }

    [Fact]
    public void EndToEnd_RuledTablePdfYieldsItsOwnColumnBoundaries()
    {
        // Rules on the page, content between them, and the column boundaries the reader ends
        // up navigating are the drawn rules — no pixmap involved anywhere in the chain.
        var path = RuledPdf([200f, 350f], []);
        try
        {
            var rulings = Extract(path);
            var (block, chars) = TableBlock([110f, 210f, 360f]);
            var blank = new byte[600 * 300 * 3];
            Array.Fill(blank, (byte)255);

            var rows = LineDetector.DetectLines(block, chars, blank, 600, 300, 1f, 1f,
                tableRowReading: true, cellNavigation: true, ocrLines: null, rulings: rulings);

            Assert.All(rows, r => Assert.Equal(3, r.Cells!.Count));
            Assert.Equal(200f, rows[0].Cells![0].X + rows[0].Cells![0].Width, 1f);
        }
        finally { File.Delete(path); }
    }
}
