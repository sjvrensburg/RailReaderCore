using System.Diagnostics;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RapidOcrNet;

// Measures what a page of OCR actually costs, per model tier — the number OcrModelRegistry's
// descriptors do not carry (they advertise download size only, so "Medium, 138 MB" reads as a
// bandwidth decision rather than a minutes-per-page one; issue #100).
//
//   OcrCostProbe <pdf> [pages=0] [rasterSize=1920]
//
// `pages` is a single index or an inclusive range (`0`, `0-3`). Detection and recognition are
// timed separately because they scale differently and only detection runs in OcrMode.Lines —
// a tier can be affordable for rail geometry and unaffordable for transcription.
//
// Env:
//   OCRCOST_TIERS     comma-separated subset of v5-latin,v6-tiny,v6-small,v6-medium
//   OCRCOST_THREADS   comma-separated intra-op thread caps to sweep (default: the shipping cap)
//   OCRCOST_REPEATS   timed passes per (tier, thread cap); the best is reported (default 1)
//
// Reading the output: `det` is one detector pass over the whole page (what OcrMode.Lines pays);
// `rec` is everything OcrMode.Full adds on top, which is per-line and therefore scales with how
// much text the page carries. The worker runs OCR ahead of layout inference for the same page,
// so `full` is the stall a scanned page imposes before its ~1s of layout analysis can start.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: OcrCostProbe <pdf> [page|first-last] [rasterSize]");
    return 1;
}

string pdf = args[0];
var (firstPage, lastPage) = ParsePages(args.Length > 1 ? args[1] : "0");
int rasterSize = args.Length > 2 ? int.Parse(args[2]) : 1920;
int repeats = int.TryParse(Environment.GetEnvironmentVariable("OCRCOST_REPEATS"), out var r) ? Math.Max(1, r) : 1;

RailReaderLogging.Logger = NullLogger.Instance;

var allTiers = new (string Name, RapidOcrModelSet? Set)[]
{
    ("v5-latin", null),   // the bundled Latin-only default; null = RapidOcrService's own default
    ("v6-tiny", OcrModelRegistry.PPOCRv6Tiny.ModelSet),
    ("v6-small", OcrModelRegistry.PPOCRv6Small.ModelSet),
    ("v6-medium", OcrModelRegistry.PPOCRv6Medium.ModelSet),
};
var wanted = Environment.GetEnvironmentVariable("OCRCOST_TIERS") is { Length: > 0 } t
    ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : null;
var tiers = wanted is null
    ? allTiers
    : allTiers.Where(x => wanted.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

// null means "leave RapidOcrService's own conservative default alone", which is what ships.
int?[] threadCaps = Environment.GetEnvironmentVariable("OCRCOST_THREADS") is { Length: > 0 } th
    ? th.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => (int?)int.Parse(s)).ToArray()
    : [null];

var factory = new SkiaPdfServiceFactory();
var svc = factory.CreatePdfService(pdf);
var textSvc = factory.CreatePdfTextService();

lastPage = Math.Min(lastPage, svc.PageCount - 1);
if (firstPage > lastPage)
{
    Console.Error.WriteLine($"{Path.GetFileName(pdf)} has {svc.PageCount} page(s); requested range is empty");
    return 1;
}

Console.WriteLine($"{Path.GetFileName(pdf)}  pages {firstPage}-{lastPage}  raster {rasterSize}px  " +
                  $"cores={Environment.ProcessorCount}  repeats={repeats}");

// Rasterise once per page and reuse across tiers, exactly as the worker does — it hands OCR the
// pixmap it already rendered for the layout model, so rasterisation is not part of OCR's cost.
var pages = new List<(int Index, byte[] Rgb, int PxW, int PxH, bool HasTextLayer)>();
for (int page = firstPage; page <= lastPage; page++)
{
    var (rgb, pxW, pxH) = svc.RenderPagePixmap(page, rasterSize);
    bool hasText = textSvc.ExtractPageText(svc.PdfBytes, page).CharBoxes.Count > 0;
    pages.Add((page, rgb, pxW, pxH, hasText));
    if (hasText)
        Console.WriteLine($"  note: page {page} has a text layer — the worker would never OCR it");
}

Console.WriteLine();
Console.WriteLine($"{"tier",-10} {"threads",7} {"page",4} {"lines",5} {"chars",6} " +
                  $"{"det ms",8} {"rec ms",9} {"full ms",9}  {"ms/line",8}");

foreach (var (name, set) in tiers)
{
    if (set is not null && OcrModelLocator.Locate(set) is null)
    {
        Console.WriteLine($"{name,-10} models not installed — see scripts/download-ocr-model.sh");
        continue;
    }

    foreach (int? threads in threadCaps)
    {
        RapidOcrService ocr;
        try
        {
            ocr = new RapidOcrService(set,
                configureSession: threads is { } n ? o => o.IntraOpNumThreads = n : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{name,-10} load failed: {ex.Message}");
            break;
        }

        using (ocr)
        {
            string threadLabel = threads?.ToString() ?? "default";
            double tierDet = 0, tierRec = 0;

            foreach (var page in pages)
            {
                double det = double.MaxValue, full = double.MaxValue;
                OcrPage detPage = OcrPage.Empty, fullPage = OcrPage.Empty;

                // Best-of-N rather than a mean: the interesting quantity is the cost with no
                // competing work, and a shared machine only ever adds time.
                for (int i = 0; i < repeats; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var d = ocr.Recognize(page.Rgb, page.PxW, page.PxH, OcrMode.Lines);
                    sw.Stop();
                    if (sw.Elapsed.TotalMilliseconds < det) { det = sw.Elapsed.TotalMilliseconds; detPage = d; }

                    sw.Restart();
                    var f = ocr.Recognize(page.Rgb, page.PxW, page.PxH, OcrMode.Full);
                    sw.Stop();
                    if (sw.Elapsed.TotalMilliseconds < full) { full = sw.Elapsed.TotalMilliseconds; fullPage = f; }
                }

                // Recognition is not timed on its own: OcrMode.Full re-runs detection, so the
                // per-line cost is what Full adds over Lines on the same page.
                double rec = Math.Max(0, full - det);
                int chars = fullPage.Lines.Sum(l => l.Text?.Length ?? 0);
                double perLine = fullPage.Lines.Count > 0 ? rec / fullPage.Lines.Count : 0;
                tierDet += det;
                tierRec += rec;

                Console.WriteLine($"{name,-10} {threadLabel,7} {page.Index,4} {detPage.Lines.Count,5} {chars,6} " +
                                  $"{det,8:F0} {rec,9:F0} {full,9:F0}  {perLine,8:F0}");
            }

            if (pages.Count > 1)
                Console.WriteLine($"{name,-10} {threadLabel,7} {"all",4} {"",5} {"",6} " +
                                  $"{tierDet,8:F0} {tierRec,9:F0} {tierDet + tierRec,9:F0}");
        }
    }
}

return 0;

static (int First, int Last) ParsePages(string spec)
{
    int dash = spec.IndexOf('-');
    if (dash < 0) { int only = int.Parse(spec); return (only, only); }
    return (int.Parse(spec[..dash]), int.Parse(spec[(dash + 1)..]));
}
