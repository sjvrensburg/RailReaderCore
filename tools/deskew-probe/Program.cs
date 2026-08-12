using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Measures OCR deskew (CoreSettings.DeskewOcrLines) on real scanned PDFs: the skew angle
// recovered from the detector's own line quads, the raw ungated evidence behind it, and the
// line count grouping recovers with and without the shear correction.
//
//   DeskewProbe <pdf|dir> [rasterSize=1920]
//
// Env:
//   DESKEWPROBE_MAXPAGES   pages per document (default 8)
//
// Reading the output: a page whose reported skew is 0.00 with a tight raw spread is genuinely
// square and correctly left alone; one reported as 0.00 with a WIDE raw spread was rejected by
// the confidence gate, which is a different thing and worth investigating. That is why the raw
// median/quartiles are printed next to the gated estimate rather than only the estimate.
//
// Expect the corrected and uncorrected counts to agree on mildly skewed pages. Merging is not a
// function of angle alone: a line only reaches its neighbour's band once its drift across the
// column (width × tan θ) exceeds the median glyph height that sets the split threshold. Small
// glyphs in a wide column cross over below 1°; large text needs several degrees.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: DeskewProbe <pdf|dir> [rasterSize]");
    return 1;
}

string target = args[0];
int rasterSize = args.Length > 1 ? int.Parse(args[1]) : 1920;
int maxPages = int.TryParse(Environment.GetEnvironmentVariable("DESKEWPROBE_MAXPAGES"), out var mp) ? mp : 8;

RailReaderLogging.Logger = NullLogger.Instance;
var factory = new SkiaPdfServiceFactory();
var textSvc = factory.CreatePdfTextService();
using var ocr = new RapidOcrService();

var pdfs = Directory.Exists(target)
    ? Directory.GetFiles(target, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(p => p).ToArray()
    : [target];

int totalCorrected = 0, totalUncorrected = 0, skewedPages = 0, regressions = 0;

foreach (var pdf in pdfs)
{
    IPdfService svc;
    try { svc = factory.CreatePdfService(pdf); }
    catch (Exception ex) { Console.Error.WriteLine($"{Path.GetFileName(pdf)}: {ex.Message}"); continue; }

    Console.WriteLine($"\n{Path.GetFileName(pdf)}  {svc.PageCount} page(s)  raster {rasterSize}px");
    Console.WriteLine($"{"page",4} {"textlyr",7} {"skew°",7} {"ocrlns",6} {"deskewed",8} {"plain",6}  verdict");

    for (int page = 0; page < Math.Min(svc.PageCount, maxPages); page++)
    {
        var (pw, ph) = svc.GetPageSize(page);
        bool hasTextLayer = textSvc.ExtractPageText(svc.PdfBytes, page).CharBoxes.Count > 0;

        var (rgb, pxW, pxH) = svc.RenderPagePixmap(page, rasterSize);
        var ocrPage = ocr.Recognize(rgb, pxW, pxH, OcrMode.Full);

        float sx = pxW > 0 ? (float)(pw / pxW) : 1f, sy = pxH > 0 ? (float)(ph / pxH) : 1f;
        var (pageText, _, skew) = OcrPageMapper.ToPageSpace(ocrPage, sx, sy);

        if (pageText is null)
        {
            Console.WriteLine($"{page,4} {hasTextLayer,7} {"-",7} {ocrPage.Lines.Count,6} {"-",8} {"-",6}  no text recovered");
            continue;
        }

        // The whole page as one block. The symptom this feature exists to fix is a paragraph's
        // worth of printed lines collapsing into one or two rail lines, which is a property of
        // the grouping alone — running it without a layout model keeps the measurement about
        // grouping rather than about which detector found which block.
        var bbox = new BBox(0, 0, (float)pw, (float)ph);
        var chars = pageText.DedupedCharBoxes;

        int deskewed = LineDetector.DetectLinesFromChars(
            bbox, chars, skewTan: MathF.Tan(skew), pivotX: bbox.X + bbox.W / 2f).Count;
        int plain = LineDetector.DetectLinesFromChars(bbox, chars).Count;

        string verdict = deskewed > plain ? $"+{deskewed - plain} recovered"
                       : deskewed == plain ? "no change"
                       : $"REGRESSION {deskewed - plain}";

        totalCorrected += deskewed;
        totalUncorrected += plain;
        if (skew != 0f) skewedPages++;
        if (deskewed < plain) regressions++;

        Console.WriteLine($"{page,4} {hasTextLayer,7} {skew * 180f / MathF.PI,7:F2} {ocrPage.Lines.Count,6} " +
                          $"{deskewed,8} {plain,6}  {verdict}");

        var angles = ocrPage.Lines
            .Where(l => l.TrueHeight > 0f && l.Box.W >= 40f)
            .Select(l => l.Angle * 180f / MathF.PI)
            .OrderBy(a => a).ToList();
        Console.WriteLine(angles.Count > 0
            ? $"       measured={angles.Count,3}  raw median={angles[angles.Count / 2],6:F2}°  " +
              $"p25={angles[angles.Count / 4],6:F2}°  p75={angles[angles.Count * 3 / 4],6:F2}°  " +
              $"min={angles[0]:F2}° max={angles[^1]:F2}°"
            : "       measured=  0  (no quad carried a usable direction)");
    }

    (svc as IDisposable)?.Dispose();
}

Console.WriteLine($"\ntotal: {totalCorrected} lines deskewed vs {totalUncorrected} plain " +
                  $"(+{totalCorrected - totalUncorrected}), {skewedPages} page(s) carried an estimate, " +
                  $"{regressions} regression(s)");
return regressions > 0 ? 2 : 0;
