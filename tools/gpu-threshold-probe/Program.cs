using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Analysis.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Corpus-driven confidence-threshold calibration for a GPU (FP16) layout model
// against its CPU production counterpart — issue #109. Same idea as
// tools/quant-probe/compare_accuracy.py (greedy-IoU agreement vs a CPU
// reference over a held-out corpus), but entirely in .NET: both the CPU and
// GPU models run through the real analyzers via the same ONNX Runtime, so
// there's no Python round-trip and no risk of a preprocessing mismatch
// between the calibration harness and production.
//
// GPU inference runs ONCE PER THRESHOLD VALUE actually needed (one for
// --diagnose, one per swept threshold otherwise) — each at that threshold's
// real production tuning, exactly like a real CPU-backed AnalysisWorker would
// produce. An earlier version of this tool ran inference once at a low
// confidence floor and re-filtered the block list by score afterward, on the
// assumption that "NMS only ever lets a higher-scoring box suppress a lower
// one, so admitting extra low-score candidates can't change what survives at
// a later, higher threshold." That's true of Nms itself, but LayoutAnalyzer's
// SuppressNestedBlocks (which runs after Nms) is NOT confidence-aware — it
// suppresses the geometrically SMALLER of any two overlapping boxes regardless
// of which one has higher confidence. Admitting a sea of low-confidence noise
// boxes at threshold 0.01 reliably produced one or more large, low-confidence,
// page-spanning boxes that geometrically contained every real detection, and
// SuppressNestedBlocks then deleted the real (smaller, higher-confidence)
// blocks outright — a deletion no later score-based re-filter could undo. On
// this tool's own corpus, that inflated "CPU-only misses" from 0 to 15 for
// the *unmodified* FP16 model: the severe under-detection this tool has been
// reporting for issue #109 was overwhelmingly a measurement artifact of that
// trick, not genuine CPU-vs-GPU precision divergence. See project memory
// project-webgpu-gridsample-bug for the full story (multiple superseded
// diagnoses) and re-run this tool's numbers before trusting any older ones.
//
// Usage:
//   GpuThresholdProbe <pdfDir> <heron|v3> <cpuModelPath> <gpuModelPath> [pagesPerPdf=2] [minThr=0.10] [maxThr=0.60] [step=0.05] [--diagnose[=thr]]
//
// --diagnose[=thr] switches to a per-page box-level breakdown at a fixed
// threshold (default: production LayoutConstants.ConfidenceThreshold),
// classifying every unmatched box instead of just an aggregate recall
// number — see issue #109: the aggregate sweep alone couldn't distinguish
// "GPU box moved/shrank" from "GPU box never detected" from "GPU produced a
// duplicate that ate the match slot".
bool diagnose = args.Any(a => a.StartsWith("--diagnose", StringComparison.Ordinal));
float diagnoseThr = LayoutConstants.ConfidenceThreshold;
{
    var diagArg = args.FirstOrDefault(a => a.StartsWith("--diagnose=", StringComparison.Ordinal));
    if (diagArg is not null) diagnoseThr = float.Parse(diagArg["--diagnose=".Length..]);
}
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length < 4)
{
    Console.Error.WriteLine("usage: GpuThresholdProbe <pdfDir> <heron|v3> <cpuModelPath> <gpuModelPath> [pagesPerPdf] [minThr] [maxThr] [step] [--diagnose[=thr]]");
    return 1;
}

string pdfDir = positional[0], archArg = positional[1].ToLowerInvariant();
string cpuModelPath = positional[2], gpuModelPath = positional[3];
int pagesPerPdf = positional.Length > 4 ? int.Parse(positional[4]) : 2;
float minThr = positional.Length > 5 ? float.Parse(positional[5]) : 0.10f;
float maxThr = positional.Length > 6 ? float.Parse(positional[6]) : 0.60f;
float step = positional.Length > 7 ? float.Parse(positional[7]) : 0.05f;

var arch = archArg switch
{
    "heron" => LayoutModelArchitecture.Heron,
    "v3" => LayoutModelArchitecture.PPDocLayoutV3,
    _ => throw new ArgumentException($"unknown arch '{archArg}' (heron|v3)"),
};
var caps = LayoutAnalyzerFactory.CapabilitiesFor(arch);

RailReaderLogging.Logger = NullLogger.Instance;

if (!WebGpuAccelerator.IsAvailable)
{
    Console.Error.WriteLine("No WebGPU device found. Aborting.");
    return 1;
}
Console.Error.WriteLine($"WebGPU device: {WebGpuAccelerator.DeviceDescription}");

// CPU reference: production tuning (LayoutDetectionTuning.Default), exactly
// what a real CPU-backed AnalysisWorker would produce.
ILayoutAnalyzer cpuAnalyzer = arch switch
{
    LayoutModelArchitecture.Heron => new HeronLayoutAnalyzer(cpuModelPath),
    LayoutModelArchitecture.PPDocLayoutV3 => new LayoutAnalyzer(cpuModelPath),
    _ => throw new ArgumentOutOfRangeException(),
};

ILayoutAnalyzer MakeGpuAnalyzer(float confidenceThreshold)
{
    var tuning = LayoutDetectionTuning.Default with { ConfidenceThreshold = confidenceThreshold };
    WebGpuAccelerator.TryEnable(arch);
    ILayoutAnalyzer a = arch switch
    {
        LayoutModelArchitecture.Heron => new HeronLayoutAnalyzer(gpuModelPath, tuning: tuning),
        LayoutModelArchitecture.PPDocLayoutV3 => new LayoutAnalyzer(gpuModelPath, tuning: tuning),
        _ => throw new ArgumentOutOfRangeException(),
    };
    WebGpuAccelerator.Disable(arch); // don't leak the hook past construction
    return a;
}

var factory = new SkiaPdfServiceFactory();
var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToArray();
Console.Error.WriteLine($"Corpus: {pdfs.Length} PDFs from {pdfDir}, <= {pagesPerPdf} pages each");

// Cache the rendered pixmap (the expensive-to-reproduce, PDFium/Skia-derived
// input) and the CPU reference per page. GPU inference is re-run per
// threshold actually needed directly against this cache — correct, and the
// only part that costs anything extra is the GPU forward pass itself, not
// PDF rendering or the CPU reference.
var pages = new List<(string pdf, int page, byte[] rgb, int pxW, int pxH, double pw, double ph, List<LayoutBlock> cpu)>();
foreach (var pdf in pdfs)
{
    IPdfService svc;
    try { svc = factory.CreatePdfService(pdf); }
    catch (Exception e) { Console.Error.WriteLine($"  SKIP {Path.GetFileName(pdf)}: {e.GetType().Name}"); continue; }

    int n = Math.Min(pagesPerPdf, svc.PageCount);
    for (int p = 0; p < n; p++)
    {
        try
        {
            var (pw, ph) = svc.GetPageSize(p);
            var (rgb, pxW, pxH) = svc.RenderPagePixmap(p, caps.InputSize);
            var cpuResult = cpuAnalyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, null, default);
            pages.Add((Path.GetFileName(pdf), p, rgb, pxW, pxH, pw, ph, cpuResult.Blocks));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  page fail {Path.GetFileName(pdf)} p{p}: {e.GetType().Name}: {e.Message}");
        }
    }
    (svc as IDisposable)?.Dispose();
}
Console.Error.WriteLine($"Evaluated {pages.Count} pages.\n");

static float Iou(BBox a, BBox b)
{
    float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
    float x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
    float inter = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
    if (inter <= 0f) return 0f;
    float union = a.W * a.H + b.W * b.H - inter;
    return union > 0f ? inter / union : 0f;
}

// Greedy IoU match, class-agnostic (mirrors compare_accuracy.py's primary
// metric): each GPU (hypothesis) box claims its best still-unused CPU
// (reference) box above the IoU floor, highest-confidence hypothesis first.
// refUsed/hypUsed are returned so the diagnostic pass can classify leftovers.
static (int matched, double sumIou, bool[] refUsed, bool[] hypUsed) Match(
    List<LayoutBlock> refBlocks, List<LayoutBlock> hypBlocksOrdered, float iouFloor = 0.5f)
{
    var refUsed = new bool[refBlocks.Count];
    var hypUsed = new bool[hypBlocksOrdered.Count];
    int matched = 0;
    double sumIou = 0;
    for (int hi = 0; hi < hypBlocksOrdered.Count; hi++)
    {
        var h = hypBlocksOrdered[hi];
        int bestJ = -1;
        float best = 0f;
        for (int j = 0; j < refBlocks.Count; j++)
        {
            if (refUsed[j]) continue;
            float v = Iou(h.BBox, refBlocks[j].BBox);
            if (v > best) { best = v; bestJ = j; }
        }
        if (bestJ >= 0 && best >= iouFloor)
        {
            refUsed[bestJ] = true;
            hypUsed[hi] = true;
            matched++;
            sumIou += best;
        }
    }
    return (matched, sumIou, refUsed, hypUsed);
}

if (diagnose)
{
    Console.WriteLine($"\n=== Diagnose @ threshold={diagnoseThr:F2} ===");
    using var gpuAnalyzer = MakeGpuAnalyzer(diagnoseThr);
    int totalMiss = 0, totalExtra = 0, missPartial = 0, missFull = 0, extraDuplicate = 0, extraSpurious = 0;
    var worstPages = new List<(string pdf, int page, int miss, int extra)>();

    foreach (var (pdf, page, rgb, pxW, pxH, pw, ph, cpu) in pages)
    {
        var gpuAtT = gpuAnalyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, null, default).Blocks
            .OrderByDescending(b => b.Confidence).ToList();
        var (_, _, refUsed, hypUsed) = Match(cpu, gpuAtT);

        var misses = Enumerable.Range(0, cpu.Count).Where(j => !refUsed[j]).Select(j => cpu[j]).ToList();
        var extras = Enumerable.Range(0, gpuAtT.Count).Where(i => !hypUsed[i]).Select(i => gpuAtT[i]).ToList();

        foreach (var miss in misses)
        {
            // Was it detected at all (any GPU box, even a leftover extra, with
            // meaningful overlap) but just didn't survive as a clean match —
            // or did the GPU model never propose anything there?
            bool anyOverlap = gpuAtT.Any(g => Iou(g.BBox, miss.BBox) >= 0.1f);
            if (anyOverlap) missPartial++; else missFull++;
        }
        foreach (var extra in extras)
        {
            // Does this leftover GPU box sit on top of ANOTHER GPU box that
            // already has (or could have) a match — i.e. a near-duplicate the
            // model's own NMS should have suppressed?
            bool duplicate = gpuAtT.Any(g => !ReferenceEquals(g, extra) && Iou(g.BBox, extra.BBox) >= 0.3f);
            if (duplicate) extraDuplicate++; else extraSpurious++;
        }

        totalMiss += misses.Count;
        totalExtra += extras.Count;
        worstPages.Add((pdf, page, misses.Count, extras.Count));
    }

    Console.WriteLine($"pages={pages.Count}  total CPU-only misses={totalMiss}  total GPU-only extras={totalExtra}");
    Console.WriteLine($"  misses: fully-undetected(no GPU box within IoU>=0.1)={missFull}  partially-detected-but-unmatched={missPartial}");
    Console.WriteLine($"  extras: near-duplicate-of-another-GPU-box(IoU>=0.3)={extraDuplicate}  spurious(no overlap)={extraSpurious}");

    Console.WriteLine("\nWorst 10 pages by (miss+extra):");
    foreach (var wp in worstPages.OrderByDescending(w => w.miss + w.extra).Take(10))
        Console.WriteLine($"  {wp.pdf} p{wp.page}: miss={wp.miss} extra={wp.extra}");
}
else
{
    Console.WriteLine($"arch={arch} cpuModel={Path.GetFileName(cpuModelPath)} gpuModel={Path.GetFileName(gpuModelPath)} pages={pages.Count}");
    Console.WriteLine($"{"thr",6} {"recall",8} {"prec",8} {"meanIoU",8} {"cpuBlk",8} {"gpuBlk",8}");

    for (float t = minThr; t <= maxThr + 1e-6f; t += step)
    {
        using var gpuAnalyzer = MakeGpuAnalyzer(t);
        int refTotal = 0, hypTotal = 0, matchedTotal = 0;
        double sumIou = 0;
        foreach (var (_, _, rgb, pxW, pxH, pw, ph, cpu) in pages)
        {
            var gpuAtT = gpuAnalyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, null, default).Blocks
                .OrderByDescending(b => b.Confidence).ToList();
            var (matched, iouSum, _, _) = Match(cpu, gpuAtT);
            refTotal += cpu.Count;
            hypTotal += gpuAtT.Count;
            matchedTotal += matched;
            sumIou += iouSum;
        }
        double recall = refTotal > 0 ? (double)matchedTotal / refTotal : 0;
        double precision = hypTotal > 0 ? (double)matchedTotal / hypTotal : 0;
        double meanIou = matchedTotal > 0 ? sumIou / matchedTotal : 0;
        double avgCpuBlk = pages.Count > 0 ? (double)refTotal / pages.Count : 0;
        double avgGpuBlk = pages.Count > 0 ? (double)hypTotal / pages.Count : 0;
        Console.WriteLine($"{t,6:F2} {recall,8:F3} {precision,8:F3} {meanIou,8:F3} {avgCpuBlk,8:F2} {avgGpuBlk,8:F2}");
    }
}

return 0;
