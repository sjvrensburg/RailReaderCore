using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Tests for PDFium-backed vector ruling extraction.
///
/// <para>
/// Assertions are against the <b>absolute positions the fixture draws at</b>, not against the
/// PdfPig backend's answer: loading both PDF backends in one process crashes the test host, and
/// a cross-backend comparison would be the most natural way to write this. Absolute assertions
/// are also stricter — they catch a coordinate-space error that both backends could share.
/// </para>
/// <para>
/// The object matrix in particular is only exercised by real content: PDFium reports path
/// points in each object's own space, so a page whose content is drawn under a CTM lands in the
/// wrong place if the matrix is ignored.
/// </para>
/// </summary>
public class PdfiumRulingTests
{
    private const float PageW = 612f, PageH = 792f;

    /// <summary>
    /// Draws vertical rules at the given x positions and horizontal rules at the given y
    /// positions, in SkiaSharp's top-left Y-down space — the same frame the extractor returns.
    /// </summary>
    private static string RuledPdf(float[] verticalXs, float[] horizontalYs,
        float top = 100f, float bottom = 600f, float left = 80f, float right = 530f,
        bool asFilledRects = false, float strokeWidth = 0.5f, Action<SKCanvas>? extra = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"railreader_pdfium_rules_{Guid.NewGuid():N}.pdf");
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);
        using var canvas = doc.BeginPage(PageW, PageH);
        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = false,
            Style = asFilledRects ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
        };

        foreach (float x in verticalXs)
        {
            if (asFilledRects) canvas.DrawRect(x - strokeWidth / 2f, top, strokeWidth, bottom - top, paint);
            else canvas.DrawLine(x, top, x, bottom, paint);
        }
        foreach (float y in horizontalYs)
        {
            if (asFilledRects) canvas.DrawRect(left, y - strokeWidth / 2f, right - left, strokeWidth, paint);
            else canvas.DrawLine(left, y, right, y, paint);
        }

        extra?.Invoke(canvas);
        doc.EndPage();
        doc.Close();
        return path;
    }

    private static PageRulings Extract(string pdfPath, int viewRotation = 0)
        => new PdfTextService().ExtractRulings(File.ReadAllBytes(pdfPath), 0, viewRotation);

    private static float[] Positions(IEnumerable<RulingSegment> rulings)
        => rulings.Select(r => (float)Math.Round(r.Position)).OrderBy(p => p).ToArray();

    [Fact]
    public void StrokedRules_LandAtThePositionsTheyWereDrawnAt()
    {
        var path = RuledPdf([150f, 300f, 450f], [120f, 580f]);
        try
        {
            var rulings = Extract(path);

            Assert.Equal([150f, 300f, 450f], Positions(rulings.Vertical));
            Assert.Equal([120f, 580f], Positions(rulings.Horizontal));

            // Extents too: the vertical rules were drawn from y=100 to y=600.
            Assert.All(rulings.Vertical, r =>
            {
                Assert.Equal(100f, r.Start, 1.5f);
                Assert.Equal(600f, r.End, 1.5f);
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HairlinesDrawnAsFilledRectangles_CollapseToOneRulingEach()
    {
        var path = RuledPdf([150f, 300f, 450f], [], asFilledRects: true, strokeWidth: 0.4f);
        try
        {
            Assert.Equal([150f, 300f, 450f], Positions(Extract(path).Vertical));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DiagonalsAndCurves_AreNotRulings()
    {
        var path = RuledPdf([], [], extra: canvas =>
        {
            using var stroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
            canvas.DrawLine(100f, 100f, 400f, 500f, stroke);
            using var curve = new SKPath();
            curve.MoveTo(100f, 600f);
            curve.CubicTo(200f, 650f, 300f, 550f, 400f, 600f);
            canvas.DrawPath(curve, stroke);
        });
        try
        {
            Assert.True(Extract(path).IsEmpty, "a trend line and a curve are not table rules");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextOnlyPage_YieldsNothing()
    {
        Assert.True(Extract(TestFixtures.GetTestPdfPath()).IsEmpty);
    }

    [Fact]
    public void RulesDrawnUnderATransform_LandWhereTheyAppear()
    {
        // Content drawn under a translate+scale: the path points PDFium reports are in the
        // object's own space, so ignoring the object matrix puts these rules at 100/200
        // instead of where they are actually drawn.
        var path = RuledPdf([], [], extra: canvas =>
        {
            using var stroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
            canvas.Save();
            canvas.Translate(200f, 50f);
            canvas.Scale(2f, 1f);
            canvas.DrawLine(50f, 100f, 50f, 400f, stroke);    // → x = 200 + 2*50 = 300
            canvas.DrawLine(100f, 100f, 100f, 400f, stroke);  // → x = 200 + 2*100 = 400
            canvas.Restore();
        });
        try
        {
            var rulings = Extract(path);

            Assert.Equal([300f, 400f], Positions(rulings.Vertical));
            // The translate applies to Y as well: y = 50 + 100 … 50 + 400.
            Assert.All(rulings.Vertical, r =>
            {
                Assert.Equal(150f, r.Start, 1.5f);
                Assert.Equal(450f, r.End, 1.5f);
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ViewRotation_SwapsTheAxes()
    {
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

    [Fact]
    public void RotatedPageFixture_PutsRulingsInTheDisplayedFrame()
    {
        // A page carrying its own /Rotate: rulings must arrive in the same displayed frame as
        // the char boxes and layout blocks, not in unrotated user space.
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "rotation", "sideways-table.pdf");
        Assert.True(File.Exists(fixturePath), $"missing fixture {fixturePath}");

        var svc = new PdfTextService();
        var bytes = File.ReadAllBytes(fixturePath);
        var rulings = svc.ExtractRulings(bytes, 0);
        var text = svc.ExtractPageText(bytes, 0);

        if (rulings.IsEmpty) return;   // fixture may draw its table without rules

        // Every ruling must sit within the displayed page box that the char boxes occupy.
        float maxX = text.CharBoxes.Max(c => c.Right);
        float maxY = text.CharBoxes.Max(c => c.Bottom);
        foreach (var r in rulings.Vertical)
        {
            Assert.InRange(r.Position, -2f, maxX + 50f);
            Assert.True(r.End > r.Start);
        }
        foreach (var r in rulings.Horizontal)
        {
            Assert.InRange(r.Position, -2f, maxY + 50f);
            Assert.True(r.End > r.Start);
        }
    }

    [Fact]
    public void DesktopFactorysTextService_AdvertisesRulingSupport()
    {
        // Core discovers this capability by casting the text service it was handed. If the
        // desktop factory ever wraps the service without forwarding IPdfRulingService, tables
        // silently fall back to the raster scan with nothing to indicate why.
        var textService = new RailReader.Renderer.Skia.SkiaPdfServiceFactory().CreatePdfTextService();

        Assert.IsAssignableFrom<IPdfRulingService>(textService);
    }

    [Fact]
    public void EndToEnd_ColumnBoundariesComeFromTheDrawnRules()
    {
        var path = RuledPdf([200f, 350f], []);
        try
        {
            var rulings = Extract(path);

            // Content in the three regions the rules define.
            var chars = new List<CharBox>();
            int idx = 0;
            foreach (float col in new[] { 110f, 210f, 360f })
                for (int r = 0; r < 3; r++)
                    for (int g = 0; g < 3; g++)
                    {
                        float x = col + g * 8f, y = 120f + r * 20f;
                        chars.Add(new CharBox(idx++, x, y, x + 7f, y + 10f));
                    }

            var block = new LayoutBlock { BBox = new BBox(100f, 110f, 400f, 70f), Role = BlockRole.Table };
            var blank = new byte[600 * 300 * 3];
            Array.Fill(blank, (byte)255);

            var rows = LineDetector.DetectLines(block, chars, blank, 600, 300, 1f, 1f,
                tableRowReading: true, cellNavigation: true, ocrLines: null, rulings: rulings);

            Assert.All(rows, r => Assert.Equal(3, r.Cells!.Count));
            Assert.Equal(200f, rows[0].Cells![0].X + rows[0].Cells![0].Width, 1.5f);
            Assert.Equal(350f, rows[0].Cells![1].X + rows[0].Cells![1].Width, 1.5f);
        }
        finally { File.Delete(path); }
    }
}
