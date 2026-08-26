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
// Inference runs ONCE per page per backend, at a low confidence floor
// (LowThreshold below) — NOT once per swept threshold. This is valid because
// NMS only ever lets a HIGHER-scoring box suppress a lower one, so admitting
// extra low-score candidates into the NMS pass cannot change whether any box
// scoring >= a later, higher threshold survives. Sweeping is then just a
// Confidence-based re-filter of the one low-threshold detection set, which is
// why this tool doesn't need the FP16 GPU EP re-run per threshold value.
//
// Usage:
//   GpuThresholdProbe <pdfDir> <heron|v3> <cpuModelPath> <gpuModelPath> [pagesPerPdf=2] [minThr=0.10] [maxThr=0.60] [step=0.05]
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: GpuThresholdProbe <pdfDir> <heron|v3> <cpuModelPath> <gpuModelPath> [pagesPerPdf] [minThr] [maxThr] [step]");
    return 1;
}

string pdfDir = args[0], archArg = args[1].ToLowerInvariant();
string cpuModelPath = args[2], gpuModelPath = args[3];
int pagesPerPdf = args.Length > 4 ? int.Parse(args[4]) : 2;
float minThr = args.Length > 5 ? float.Parse(args[5]) : 0.10f;
float maxThr = args.Length > 6 ? float.Parse(args[6]) : 0.60f;
float step = args.Length > 7 ? float.Parse(args[7]) : 0.05f;
const float LowThreshold = 0.01f;

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

// GPU candidate set: same NMS/min-size, but a low confidence floor so the
// sweep below can re-filter without re-running inference (see header note).
var lowTuning = LayoutDetectionTuning.Default with { ConfidenceThreshold = LowThreshold };
WebGpuAccelerator.TryEnable(arch);
ILayoutAnalyzer gpuAnalyzer = arch switch
{
    LayoutModelArchitecture.Heron => new HeronLayoutAnalyzer(gpuModelPath, tuning: lowTuning),
    LayoutModelArchitecture.PPDocLayoutV3 => new LayoutAnalyzer(gpuModelPath, tuning: lowTuning),
    _ => throw new ArgumentOutOfRangeException(),
};
WebGpuAccelerator.Disable(arch); // don't leak the hook past construction

var factory = new SkiaPdfServiceFactory();
var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToArray();
Console.Error.WriteLine($"Corpus: {pdfs.Length} PDFs from {pdfDir}, <= {pagesPerPdf} pages each");

var pages = new List<(List<LayoutBlock> cpu, List<LayoutBlock> gpuLow)>();
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
            var gpuResult = gpuAnalyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, null, default);
            pages.Add((cpuResult.Blocks, gpuResult.Blocks));
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
static (int matched, double sumIou) Match(List<LayoutBlock> refBlocks, List<LayoutBlock> hypBlocks, float iouFloor = 0.5f)
{
    var used = new bool[refBlocks.Count];
    int matched = 0;
    double sumIou = 0;
    foreach (var h in hypBlocks.OrderByDescending(b => b.Confidence))
    {
        int bestJ = -1;
        float best = 0f;
        for (int j = 0; j < refBlocks.Count; j++)
        {
            if (used[j]) continue;
            float v = Iou(h.BBox, refBlocks[j].BBox);
            if (v > best) { best = v; bestJ = j; }
        }
        if (bestJ >= 0 && best >= iouFloor)
        {
            used[bestJ] = true;
            matched++;
            sumIou += best;
        }
    }
    return (matched, sumIou);
}

Console.WriteLine($"arch={arch} cpuModel={Path.GetFileName(cpuModelPath)} gpuModel={Path.GetFileName(gpuModelPath)} pages={pages.Count}");
Console.WriteLine($"{"thr",6} {"recall",8} {"prec",8} {"meanIoU",8} {"cpuBlk",8} {"gpuBlk",8}");

for (float t = minThr; t <= maxThr + 1e-6f; t += step)
{
    int refTotal = 0, hypTotal = 0, matchedTotal = 0;
    double sumIou = 0;
    foreach (var (cpu, gpuLow) in pages)
    {
        var gpuAtT = gpuLow.Where(b => b.Confidence >= t).ToList();
        var (matched, iouSum) = Match(cpu, gpuAtT);
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

(cpuAnalyzer as IDisposable)?.Dispose();
(gpuAnalyzer as IDisposable)?.Dispose();
return 0;
