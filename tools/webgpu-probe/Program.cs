using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Analysis.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Spike probe for the native WebGPU execution provider (Dawn -> Vulkan/D3D12/Metal),
// distributed as a plugin EP via Microsoft.ML.OnnxRuntime.EP.WebGpu (shipped 2026-08-24).
// See memory: project-onnx-gpu-ep-investigation.
//
// Times CPU-only vs WebGPU-EP inference on the SAME rasterised page, paired and
// interleaved iteration-by-iteration to cancel CPU-frequency/contention drift
// (reference-perf-measurement-caveats) rather than running two separate blocks.
//
// Usage:
//   WebGpuProbe <pdf> <heron|v3|pps> <cpuModelPath> [gpuModelPath] [page=0] [iterations=20]
// gpuModelPath defaults to cpuModelPath (same-model CPU vs GPU comparison); pass a
// separate FP16 export as gpuModelPath to compare a CPU-FP32 baseline against a
// GPU-FP16 candidate in one run.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: WebGpuProbe <pdf> <heron|v3|pps> <cpuModelPath> [gpuModelPath] [page] [iterations]");
    return 1;
}

string pdfPath = args[0], archArg = args[1].ToLowerInvariant(), cpuModelPath = args[2];
string gpuModelPath = args.Length > 3 ? args[3] : cpuModelPath;
int page = args.Length > 4 && int.TryParse(args[4], out var pg) ? pg : 0;
int iterations = args.Length > 5 && int.TryParse(args[5], out var it) ? it : 20;

var arch = archArg switch
{
    "heron" => LayoutModelArchitecture.Heron,
    "v3" => LayoutModelArchitecture.PPDocLayoutV3,
    "pps" => LayoutModelArchitecture.PPDocLayoutS,
    _ => throw new ArgumentException($"unknown arch '{archArg}'"),
};
var caps = LayoutAnalyzerFactory.CapabilitiesFor(arch);

RailReaderLogging.Logger = NullLogger.Instance;

if (!WebGpuAccelerator.IsAvailable)
{
    Console.Error.WriteLine("No WebGPU device found (missing Vulkan loader / no supported GPU?). Aborting.");
    return 1;
}
Console.Error.WriteLine($"WebGPU device: {WebGpuAccelerator.DeviceDescription}");

// ---- rasterise the page once; both analyzers see identical input ----
var factory = new SkiaPdfServiceFactory();
var textSvc = factory.CreatePdfTextService();
var svc = factory.CreatePdfService(pdfPath);
var (pw, ph) = svc.GetPageSize(page);
var pageText = textSvc.ExtractPageText(svc.PdfBytes, page);
var (rgb, pxW, pxH) = svc.RenderPagePixmap(page, caps.InputSize);

PageAnalysis Run(ILayoutAnalyzer analyzer) =>
    analyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, pageText.CharBoxes, default);

// ---- construct both analyzer instances up front (session build cost excluded from timing) ----
WebGpuAccelerator.Disable(arch);
using var cpuAnalyzer = LayoutAnalyzerFactory.Create(arch, cpuModelPath);
WebGpuAccelerator.TryEnable(arch);
using var gpuAnalyzer = LayoutAnalyzerFactory.Create(arch, gpuModelPath);
WebGpuAccelerator.Disable(arch); // don't leak the hook past construction

// warm up (first call on each backend pays one-time JIT/kernel-compile cost)
var cpuWarm = Run(cpuAnalyzer);
var gpuWarm = Run(gpuAnalyzer);
Console.Error.WriteLine($"warmup: cpu blocks={cpuWarm.Blocks.Count} gpu blocks={gpuWarm.Blocks.Count}" +
    (cpuWarm.Blocks.Count != gpuWarm.Blocks.Count ? "  <-- MISMATCH, check correctness before trusting timings" : ""));

var cpuMs = new List<double>(iterations);
var gpuMs = new List<double>(iterations);
var sw = System.Diagnostics.Stopwatch.StartNew();

for (int i = 0; i < iterations; i++)
{
    sw.Restart();
    Run(cpuAnalyzer);
    cpuMs.Add(sw.Elapsed.TotalMilliseconds);

    sw.Restart();
    Run(gpuAnalyzer);
    gpuMs.Add(sw.Elapsed.TotalMilliseconds);
}

double Median(List<double> xs)
{
    var s = xs.OrderBy(x => x).ToList();
    int n = s.Count;
    return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
}

Console.WriteLine($"arch={arch} page={page} pxSize={pxW}x{pxH} iterations={iterations}");
Console.WriteLine($"cpuModel={Path.GetFileName(cpuModelPath)} gpuModel={Path.GetFileName(gpuModelPath)}");
Console.WriteLine($"cpu:  median={Median(cpuMs):F1}ms  mean={cpuMs.Average():F1}ms  min={cpuMs.Min():F1}ms  max={cpuMs.Max():F1}ms");
Console.WriteLine($"gpu:  median={Median(gpuMs):F1}ms  mean={gpuMs.Average():F1}ms  min={gpuMs.Min():F1}ms  max={gpuMs.Max():F1}ms");
Console.WriteLine($"speedup (median cpu/gpu): {Median(cpuMs) / Median(gpuMs):F2}x");

// correctness spot-check: same block count and roughly the same boxes (order can
// legitimately differ, so compare as a multiset of rounded centroids).
var cpuFinal = Run(cpuAnalyzer);
var gpuFinal = Run(gpuAnalyzer);
string Sig(PageAnalysis a) => string.Join(",", a.Blocks
    .Select(b => $"{(int)Math.Round(b.BBox.X + b.BBox.W / 2)}.{(int)Math.Round(b.BBox.Y + b.BBox.H / 2)}")
    .OrderBy(s => s));
bool match = Sig(cpuFinal) == Sig(gpuFinal);
Console.WriteLine($"correctness: cpu blocks={cpuFinal.Blocks.Count} gpu blocks={gpuFinal.Blocks.Count} centroidsMatch={match}");
if (!match)
{
    Console.WriteLine($"  cpu sig: {Sig(cpuFinal)}");
    Console.WriteLine($"  gpu sig: {Sig(gpuFinal)}");
}

return 0;
