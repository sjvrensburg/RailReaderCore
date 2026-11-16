using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Analysis.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RapidOcrNet;

// Issue #193: layout inference and OCR both on WebGPU, run concurrently the way AnalysisWorker's
// two stage threads run them. Answers three questions on a corpus:
//
//   1. Parity   — with both engines on the GPU and running at the same time, do layout blocks and
//                 OCR text match a CPU reference?
//   2. Throughput — wall time for the corpus through both stages, for each placement:
//                 cpu+cpu, gpu layout + cpu ocr, cpu layout + gpu ocr, gpu+gpu.
//   3. Stability — sustained concurrent gpu+gpu load for --stress seconds; a native crash kills the
//                 process, so a clean "stress: ok" line is the evidence.
//
//   GpuPipelineProbe <pdf|dir> <heron|v3> <layoutModel> [pagesPerPdf=2]
//                    [--ocr=v5-latin|v6-tiny|v6-small|v6-medium] [--device=N] [--stress=60]
//                    [--construct-race]
//
// --construct-race: build the GPU layout analyzer and GPU OCR service on two threads at once, as
// AnalysisWorker's stage threads do at startup, run one inference each and exit. The plugin
// initialises its device context on the first session in a process, so loop this from a shell
// (one process per attempt) to exercise that first-session race.
//
// One GPU per process (#182): --device=N pins both engines to AvailableDevices[N]; without it Dawn
// chooses (its high-performance pick). OCR rasterises at 1920 px (the PP-DocLayout-S pairing the
// docs recommend for OcrMode.Full); layout at the model's own input size.

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
string? Opt(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];
if (positional.Length < 3)
{
    Console.Error.WriteLine("usage: GpuPipelineProbe <pdf|dir> <heron|v3> <layoutModel> [pagesPerPdf=2] [--ocr=tier] [--device=N] [--stress=60]");
    return 1;
}

string input = positional[0];
var arch = positional[1].ToLowerInvariant() switch
{
    "heron" => LayoutModelArchitecture.Heron,
    "v3" => LayoutModelArchitecture.PPDocLayoutV3,
    var a => throw new ArgumentException($"unknown arch '{a}'"),
};
string layoutModel = positional[2];
int pagesPerPdf = positional.Length > 3 ? int.Parse(positional[3]) : 2;
string ocrTier = Opt("ocr") ?? "v5-latin";
int? device = Opt("device") is { } d ? int.Parse(d) : null;
int stressSeconds = Opt("stress") is { } s ? int.Parse(s) : 60;
const int OcrRaster = 1920;

RapidOcrModelSet? ocrSet = ocrTier switch
{
    "v5-latin" => null,
    "v6-tiny" => OcrModelRegistry.PPOCRv6Tiny.ModelSet,
    "v6-small" => OcrModelRegistry.PPOCRv6Small.ModelSet,
    "v6-medium" => OcrModelRegistry.PPOCRv6Medium.ModelSet,
    _ => throw new ArgumentException($"unknown OCR tier '{ocrTier}'"),
};

RailReaderLogging.Logger = NullLogger.Instance;
if (!WebGpuAccelerator.IsAvailable) { Console.Error.WriteLine("No WebGPU device."); return 1; }

// ---- corpus: render every page once, for both engines ----
var caps = LayoutAnalyzerFactory.CapabilitiesFor(arch);
var pdfs = Directory.Exists(input)
    ? Directory.GetFiles(input, "*.pdf").OrderBy(p => p, StringComparer.Ordinal).ToArray()
    : [input];
var factory = new SkiaPdfServiceFactory();
var pages = new List<Page>();
foreach (var pdf in pdfs)
{
    try
    {
        var svc = factory.CreatePdfService(pdf);
        for (int p = 0; p < Math.Min(pagesPerPdf, svc.PageCount); p++)
        {
            var (pw, ph) = svc.GetPageSize(p);
            var (lr, lw, lh) = svc.RenderPagePixmap(p, caps.InputSize);
            var (or, ow, oh) = svc.RenderPagePixmap(p, OcrRaster);
            pages.Add(new Page($"{Path.GetFileName(pdf)}#{p}", lr, lw, lh, pw, ph, or, ow, oh));
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"skip {Path.GetFileName(pdf)}: {ex.Message}"); }
}
Console.WriteLine($"corpus: {pages.Count} pages from {pdfs.Length} PDFs; layout={arch} ({Path.GetFileName(layoutModel)}), ocr={ocrTier}");

// ---- engines ----
ILayoutAnalyzer Layout(bool gpu)
{
    lock (WebGpuAccelerator.ConstructionLock)
    {
        if (gpu && !(device is int di ? WebGpuAccelerator.TryEnable(arch, di) : WebGpuAccelerator.TryEnable(arch)))
            throw new InvalidOperationException("layout: WebGPU device unavailable");
        try { return LayoutAnalyzerFactory.Create(arch, layoutModel); }
        finally { WebGpuAccelerator.Disable(arch); }
    }
}
IOcrService Ocr(bool gpu)
{
    Action<SessionOptions>? hook = null;
    if (gpu)
        hook = (device is int di ? WebGpuAccelerator.TryBuildSessionHook(di) : WebGpuAccelerator.TryBuildSessionHook())
            ?? throw new InvalidOperationException("ocr: WebGPU device unavailable");
    return new RapidOcrService(ocrSet, configureSession: hook);
}

if (args.Contains("--construct-race"))
{
    using var go = new Barrier(2);
    Exception? err = null;
    var p0 = pages[0];
    var a = new Thread(() =>
    {
        go.SignalAndWait();
        try { using var l = Layout(gpu: true); l.RunAnalysis(p0.LRgb, p0.LW, p0.LH, p0.PW, p0.PH, null, default); }
        catch (Exception ex) { err ??= ex; }
    });
    var b = new Thread(() =>
    {
        go.SignalAndWait();
        try { using var o = Ocr(gpu: true); o.Recognize(p0.ORgb, p0.OW, p0.OH, OcrMode.Full); }
        catch (Exception ex) { err ??= ex; }
    });
    a.Start(); b.Start(); a.Join(); b.Join();
    Console.WriteLine(err is null
        ? $"construct-race: ok on {WebGpuAccelerator.DeviceDescription}"
        : $"construct-race: FAILED {err.GetType().Name}: {err.Message}");
    return err is null ? 0 : 2;
}

using var cpuLayout = Layout(gpu: false);
using var cpuOcr = Ocr(gpu: false);
using var gpuLayout = Layout(gpu: true);
using var gpuOcr = Ocr(gpu: true);
Console.WriteLine($"gpu device: {WebGpuAccelerator.DeviceDescription} (ActiveDeviceIndex={WebGpuAccelerator.ActiveDeviceIndex})");

// Warm-up: first runs pay JIT / shader compilation, which would otherwise land in one config's timing.
foreach (var (l, o) in new[] { (cpuLayout, cpuOcr), (gpuLayout, gpuOcr) })
{
    l.RunAnalysis(pages[0].LRgb, pages[0].LW, pages[0].LH, pages[0].PW, pages[0].PH, null, default);
    o.Recognize(pages[0].ORgb, pages[0].OW, pages[0].OH, OcrMode.Full);
}

// ---- 1+2: each placement, both stages concurrently over the corpus ----
var configs = new (string Name, ILayoutAnalyzer L, IOcrService O)[]
{
    ("cpu+cpu", cpuLayout, cpuOcr),
    ("gpuL+cpuO", gpuLayout, cpuOcr),
    ("cpuL+gpuO", cpuLayout, gpuOcr),
    ("gpu+gpu", gpuLayout, gpuOcr),
};
var results = configs.Select(c => (c.Name, Result: RunPair(c.L, c.O, pages))).ToList();
var reference = results[0].Result;

Console.WriteLine();
Console.WriteLine($"{"config",-10} {"wall s",7} {"layout s",9} {"ocr s",7} {"blk recall",10} {"blk prec",9} {"ocr pages =",11} {"ocr char sim",12}");
foreach (var (name, r) in results)
{
    var (recall, precision) = LayoutAgreement(reference.Blocks, r.Blocks);
    int same = 0; double sim = 0;
    for (int i = 0; i < pages.Count; i++)
    {
        if (reference.Texts[i] == r.Texts[i]) same++;
        sim += Similarity(reference.Texts[i], r.Texts[i]);
    }
    Console.WriteLine($"{name,-10} {r.Wall.TotalSeconds,7:F1} {r.LayoutWall.TotalSeconds,9:F1} {r.OcrWall.TotalSeconds,7:F1} {recall,10:F3} {precision,9:F3} {same,5}/{pages.Count,-5} {sim / pages.Count,12:F4}");
}
var gg = results[^1].Result;
for (int i = 0; i < pages.Count; i++)
    if (reference.Texts[i] != gg.Texts[i])
        Console.WriteLine($"  ocr diff {pages[i].Name}: cpu {reference.Texts[i].Length} chars, gpu+gpu {gg.Texts[i].Length} chars, sim {Similarity(reference.Texts[i], gg.Texts[i]):F4}");

// ---- 3: sustained concurrent gpu+gpu load ----
long layoutRuns = 0, ocrRuns = 0;
Exception? failure = null;
var deadline = Stopwatch.StartNew();
void Loop(Action<Page> work, ref long count)
{
    try
    {
        for (int i = 0; deadline.Elapsed.TotalSeconds < stressSeconds; i++)
        {
            work(pages[i % pages.Count]);
            count++;
        }
    }
    catch (Exception ex) { failure ??= ex; }
}
var lt = new Thread(() => Loop(p => gpuLayout.RunAnalysis(p.LRgb, p.LW, p.LH, p.PW, p.PH, null, default), ref layoutRuns));
var ot = new Thread(() => Loop(p => gpuOcr.Recognize(p.ORgb, p.OW, p.OH, OcrMode.Full), ref ocrRuns));
lt.Start(); ot.Start(); lt.Join(); ot.Join();
Console.WriteLine();
Console.WriteLine(failure is null
    ? $"stress: ok — {stressSeconds}s concurrent gpu+gpu, {layoutRuns} layout runs, {ocrRuns} ocr runs, no exception, no crash"
    : $"stress: FAILED after {layoutRuns} layout / {ocrRuns} ocr runs: {failure.GetType().Name}: {failure.Message}");
return failure is null ? 0 : 2;

// ---- helpers ----
static PairResult RunPair(ILayoutAnalyzer layout, IOcrService ocr, List<Page> pages)
{
    var blocks = new List<LayoutBlock>[pages.Count];
    var texts = new string[pages.Count];
    TimeSpan lWall = default, oWall = default;
    using var start = new Barrier(2);
    var total = Stopwatch.StartNew();
    var lt = new Thread(() =>
    {
        start.SignalAndWait();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            blocks[i] = layout.RunAnalysis(p.LRgb, p.LW, p.LH, p.PW, p.PH, null, default).Blocks;
        }
        lWall = sw.Elapsed;
    });
    var ot = new Thread(() =>
    {
        start.SignalAndWait();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            texts[i] = string.Join("\n", ocr.Recognize(p.ORgb, p.OW, p.OH, OcrMode.Full).Lines.Select(l => l.Text ?? ""));
        }
        oWall = sw.Elapsed;
    });
    lt.Start(); ot.Start(); lt.Join(); ot.Join();
    return new PairResult(blocks, texts, total.Elapsed, lWall, oWall);
}

// Class-agnostic greedy IoU ≥ 0.5 agreement against the reference, pooled over the corpus.
static (double Recall, double Precision) LayoutAgreement(List<LayoutBlock>[] reference, List<LayoutBlock>[] hyp)
{
    int matched = 0, refTotal = 0, hypTotal = 0;
    for (int i = 0; i < reference.Length; i++)
    {
        var used = new bool[reference[i].Count];
        refTotal += reference[i].Count;
        hypTotal += hyp[i].Count;
        foreach (var h in hyp[i].OrderByDescending(b => b.Confidence))
        {
            int best = -1; float bestIou = 0.5f;
            for (int j = 0; j < reference[i].Count; j++)
            {
                if (used[j]) continue;
                float v = Iou(h.BBox, reference[i][j].BBox);
                if (v >= bestIou) { bestIou = v; best = j; }
            }
            if (best >= 0) { used[best] = true; matched++; }
        }
    }
    return (refTotal == 0 ? 1 : (double)matched / refTotal, hypTotal == 0 ? 1 : (double)matched / hypTotal);
}

static float Iou(BBox a, BBox b)
{
    float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
    float x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
    float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
    float union = a.W * a.H + b.W * b.H - inter;
    return union <= 0 ? 0 : inter / union;
}

// 1 − normalised Levenshtein distance.
static double Similarity(string a, string b)
{
    if (a == b) return 1;
    int n = a.Length, m = b.Length;
    if (n == 0 || m == 0) return 0;
    var prev = new int[m + 1];
    var cur = new int[m + 1];
    for (int j = 0; j <= m; j++) prev[j] = j;
    for (int i = 1; i <= n; i++)
    {
        cur[0] = i;
        for (int j = 1; j <= m; j++)
            cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        (prev, cur) = (cur, prev);
    }
    return 1 - (double)prev[m] / Math.Max(n, m);
}

record Page(string Name, byte[] LRgb, int LW, int LH, double PW, double PH, byte[] ORgb, int OW, int OH);
record PairResult(List<LayoutBlock>[] Blocks, string[] Texts, TimeSpan Wall, TimeSpan LayoutWall, TimeSpan OcrWall);
