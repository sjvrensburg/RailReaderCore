using System.Diagnostics;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// RailReaderCore perf harness (tools/ only, not shipped; IsPackable=false,
// outside the solution). Wall-clock + managed allocation per pipeline stage
// on a real PDF. Native (ONNX/PDFium) cost shows in wall-clock only.
//
// IMPORTANT measurement note: this box is intel_pstate "active" — cores idle
// at 400 MHz and ramp under load. Trust absolute ms/page only on a quiet box
// (check /proc/loadavg) after warm-up; use --analyzer duel for A/B comparisons,
// which interleaves and cancels frequency/contention noise.
//
// Modes:
//   --analyzer v3|pps|heron   full-pipeline stage timing (default v3)
//   --analyzer duel           paired V3-vs-Heron A/B (noise-robust ratio)
//   --analyzer sweep[-heron]  ONNX thread-config sweep for V3 / Heron

string pdf = "/home/stefan/Downloads/energies-12-03569.pdf";
string model = "/home/stefan/railreader2/models/PP-DocLayoutV3.onnx";
int maxPages = 8;
string kind = "v3";
int viewTarget = 1500;

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--pdf": pdf = args[++i]; break;
        case "--model": model = args[++i]; break;
        case "--pages": maxPages = int.Parse(args[++i]); break;
        case "--analyzer": kind = args[++i]; break;
        case "--view": viewTarget = int.Parse(args[++i]); break;
    }
}
if (kind == "pps" && model.Contains("DocLayoutV3"))
    model = "/home/stefan/railreader2/experiments/pp-doclayout-s/pp_doclayout_s.onnx";
if (kind == "heron" && model.Contains("DocLayoutV3"))
    model = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx";

if (kind == "dump-rasters")
{
    PerfHarness.DumpRasters.Run(
        pdfDir: "/home/stefan/RailDLA/pdfs",
        outDir: "/home/stefan/RailReaderCore/tools/quant-probe/rasters",
        pagesPerPdf: 4,
        rasterEdge: 1024);
    return;
}

if (kind == "duel")
{
    string v3M = "/home/stefan/railreader2/models/PP-DocLayoutV3.onnx";
    string hrM = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx";
    PerfHarness.Duel.Run(pdf, v3M, hrM, maxPages, rounds: 5);
    return;
}

if (kind == "sweep" || kind == "sweep-heron")
{
    string sweepKind = kind == "sweep-heron" ? "heron" : "v3";
    if (sweepKind == "heron" && model.Contains("DocLayoutV3"))
        model = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx";
    PerfHarness.ThreadSweep.Run(pdf, model, maxPages, sweepKind);
    return;
}

var factory = new SkiaPdfServiceFactory();
var pdfService = factory.CreatePdfService(pdf);
var textService = factory.CreatePdfTextService();
byte[] pdfBytes = pdfService.PdfBytes;

ILayoutAnalyzer analyzer = kind switch
{
    "pps" => new PPDocLayoutSLayoutAnalyzer(model),
    "heron" => new HeronLayoutAnalyzer(model),
    _ => new LayoutAnalyzer(model),
};

int inputSize = analyzer.Capabilities.InputSize;
int pages = Math.Min(maxPages, pdfService.PageCount);
Console.WriteLine($"pdf={Path.GetFileName(pdf)} analyzer={kind} inputSize={inputSize} view={viewTarget} pages={pages}/{pdfService.PageCount} nproc={Environment.ProcessorCount}");

var ms = new Dictionary<string, double>();
var kb = new Dictionary<string, double>();
var order = new[] { "text-extract", "rasterize@analysis", "RunAnalysis", "rasterize@view" };
foreach (var s in order) { ms[s] = 0; kb[s] = 0; }

int blockTotal = 0, charTotal = 0;

(double el, long alloc) M(Action a)
{
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    long t0 = Stopwatch.GetTimestamp();
    a();
    double el = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    return (el, GC.GetAllocatedBytesForCurrentThread() - a0);
}

void RunPage(int p, bool record)
{
    var (pw, ph) = pdfService.GetPageSize(p);

    PageText? text = null;
    var rt = M(() => text = textService.ExtractPageText(pdfBytes, p));

    (byte[] Rgb, int W, int H) apix = default;
    var ra = M(() => apix = pdfService.RenderPagePixmap(p, inputSize));

    PageAnalysis? pa = null;
    var rn = M(() => pa = analyzer.RunAnalysis(apix.Rgb, apix.W, apix.H, pw, ph, text!.CharBoxes));

    (byte[] Rgb, int W, int H) vpix = default;
    var rv = M(() => vpix = pdfService.RenderPagePixmap(p, viewTarget));

    if (!record) return;
    ms["text-extract"] += rt.el; kb["text-extract"] += rt.alloc / 1024.0;
    ms["rasterize@analysis"] += ra.el; kb["rasterize@analysis"] += ra.alloc / 1024.0;
    ms["RunAnalysis"] += rn.el; kb["RunAnalysis"] += rn.alloc / 1024.0;
    ms["rasterize@view"] += rv.el; kb["rasterize@view"] += rv.alloc / 1024.0;
    blockTotal += pa!.Blocks.Count; charTotal += text!.CharBoxes.Count;
}

RunPage(0, record: false);              // warm up JIT / ORT / PDFium

int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
long tot0 = GC.GetTotalAllocatedBytes();
var sw = Stopwatch.StartNew();
for (int p = 0; p < pages; p++) RunPage(p, record: true);
sw.Stop();

double totMB = (GC.GetTotalAllocatedBytes() - tot0) / 1048576.0;

Console.WriteLine($"blocks/pg={blockTotal / (double)pages:F1} chars/pg={charTotal / (double)pages:F0}");
Console.WriteLine($"{"stage",-20}{"ms/pg",9}{"KB/pg",10}");
foreach (var s in order)
    Console.WriteLine($"{s,-20}{ms[s] / pages,9:F2}{kb[s] / pages,10:F1}");
Console.WriteLine($"WALL {sw.Elapsed.TotalMilliseconds / pages:F2} ms/pg | alloc {totMB:F1} MB ({totMB / pages:F2}/pg) | GC g0={GC.CollectionCount(0) - g0} g1={GC.CollectionCount(1) - g1} g2={GC.CollectionCount(2) - g2}");
