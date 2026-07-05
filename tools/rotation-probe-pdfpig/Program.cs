// PdfPig-side rotation probe: what page.Width/Height and page.Rotation report
// on /Rotate pages, what space letter boxes come back in, and whether the
// char boxes cover the ink of the PdfPig.Rendering.Skia pixmap. Runs as its
// own process — PDFium and PdfPig cannot coexist in-process.
using RailReader.Core.Models;
using RailReader.Renderer.PdfPigSkia;
using UglyToad.PdfPig;

string fixtures = args.Length > 0 ? args[0] : "tests/fixtures/rotation";
ProbePdf(Path.Combine(fixtures, "rotate-suite.pdf"), "MARKER");
ProbePdf(Path.Combine(fixtures, "landscape-scan.pdf"), "SCANMARK");

static void ProbePdf(string path, string marker)
{
    Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
    var bytes = File.ReadAllBytes(path);
    using var service = new PdfPigSkiaPdfService(bytes);
    var textService = new RailReader.Core.PdfPig.PdfTextService();

    using var doc = PdfDocument.Open(bytes);
    for (int p = 0; p < service.PageCount; p++)
    {
        Console.WriteLine($"\n-- page {p} --");
        var page = doc.GetPage(p + 1);
        Console.WriteLine($"PdfPig page.Rotation: {page.Rotation.Value}");
        Console.WriteLine($"PdfPig page.Width/Height: {page.Width:F1} x {page.Height:F1}");
        Console.WriteLine($"PdfPig MediaBox: {page.MediaBox.Bounds.Left:F0},{page.MediaBox.Bounds.Bottom:F0} .. {page.MediaBox.Bounds.Right:F0},{page.MediaBox.Bounds.Top:F0}");

        var (w, h) = service.GetPageSize(p);
        Console.WriteLine($"service GetPageSize: {w:F1} x {h:F1}");

        var pageText = textService.ExtractPageText(bytes, p);
        int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
        if (mi >= 0)
        {
            var boxes = pageText.CharBoxes.Where(c => c.Index >= mi && c.Index < mi + marker.Length).ToList();
            Console.WriteLine($"'{marker}' charbox union: L={boxes.Min(b => b.Left):F1} T={boxes.Min(b => b.Top):F1} R={boxes.Max(b => b.Right):F1} B={boxes.Max(b => b.Bottom):F1}");
        }
        else
        {
            Console.WriteLine($"'{marker}' NOT FOUND");
        }

        MeasureInkCoverage(service, pageText, p, w, h);
    }
}

static void MeasureInkCoverage(PdfPigSkiaPdfService service, PageText pageText,
    int pageIndex, double pageW, double pageH)
{
    var (rgb, pixW, pixH) = service.RenderPagePixmap(pageIndex, 800);
    double scaleX = pixW / pageW, scaleY = pixH / pageH;

    var inBox = new bool[pixW * pixH];
    foreach (var b in pageText.CharBoxes)
    {
        if (b.Right <= b.Left || b.Bottom <= b.Top) continue;
        int x0 = Math.Clamp((int)(b.Left * scaleX) - 1, 0, pixW - 1);
        int x1 = Math.Clamp((int)(b.Right * scaleX) + 1, 0, pixW - 1);
        int y0 = Math.Clamp((int)(b.Top * scaleY) - 1, 0, pixH - 1);
        int y1 = Math.Clamp((int)(b.Bottom * scaleY) + 1, 0, pixH - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                inBox[y * pixW + x] = true;
    }

    long dark = 0, darkInBox = 0;
    for (int i = 0; i < pixW * pixH; i++)
    {
        int sum = rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2];
        if (sum < 384)
        {
            dark++;
            if (inBox[i]) darkInBox++;
        }
    }
    double recall = dark == 0 ? 0 : (double)darkInBox / dark;
    Console.WriteLine($"pixmap {pixW}x{pixH}; dark px: {dark}; ink coverage by char boxes: {recall:F3}");
}
