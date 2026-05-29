using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace PerfHarness;

/// <summary>
/// ONNX inference thread-config sweep for V3 or Heron. Answers: is the per-page
/// CPU inference thread-starved (a free SessionOptions win) or already saturated,
/// and where is the optimum IntraOpNumThreads on this box.
///
/// Times full RunAnalysis wall-clock per page (preprocess + inference + post),
/// which is the same metric for both analyzers — apples-to-apples. A fresh
/// InferenceSession is built per config via the analyzer's ConfigureSession seam.
/// </summary>
internal static class ThreadSweep
{
    public static void Run(string pdf, string model, int pages, string kind)
    {
        Func<ILayoutAnalyzer> make = kind == "heron"
            ? () => new HeronLayoutAnalyzer(model)
            : () => new LayoutAnalyzer(model);

        Action<Action<SessionOptions>?> setConfig = kind == "heron"
            ? cfg => HeronLayoutAnalyzer.ConfigureSession = cfg
            : cfg => LayoutAnalyzer.ConfigureSession = cfg;

        var factory = new SkiaPdfServiceFactory();
        var pdfService = factory.CreatePdfService(pdf);
        var textService = factory.CreatePdfTextService();
        byte[] pdfBytes = pdfService.PdfBytes;

        // Read InputSize from a throwaway analyzer's public Capabilities.
        int probeInput;
        using (var probe = make())
            probeInput = probe.Capabilities.InputSize;

        // Pre-rasterise the pages once so the sweep times pure model cost.
        var rasters = new List<(byte[] Rgb, int W, int H, double PW, double PH, IReadOnlyList<CharBox> Chars)>();
        int n = Math.Min(pages, pdfService.PageCount);
        for (int p = 0; p < n; p++)
        {
            var (pw, ph) = pdfService.GetPageSize(p);
            var (rgb, w, h) = pdfService.RenderPagePixmap(p, probeInput);
            var text = textService.ExtractPageText(pdfBytes, p);
            rasters.Add((rgb, w, h, pw, ph, text.CharBoxes));
        }

        Console.WriteLine($"THREAD SWEEP  analyzer={kind} pdf={Path.GetFileName(pdf)} inputSize={probeInput} pages={n} nproc={Environment.ProcessorCount}");
        Console.WriteLine($"{"config",-28}{"ms/pg",10}");

        (int? intra, int? inter, ExecutionMode mode, string label)[] configs =
        {
            (null, null, ExecutionMode.ORT_SEQUENTIAL, "default"),
            (4,    null, ExecutionMode.ORT_SEQUENTIAL, "intra=4"),
            (6,    null, ExecutionMode.ORT_SEQUENTIAL, "intra=6"),
            (8,    null, ExecutionMode.ORT_SEQUENTIAL, "intra=8"),
            (10,   null, ExecutionMode.ORT_SEQUENTIAL, "intra=10"),
            (16,   null, ExecutionMode.ORT_SEQUENTIAL, "intra=16"),
            (20,   null, ExecutionMode.ORT_SEQUENTIAL, "intra=20"),
            (10,   2,    ExecutionMode.ORT_PARALLEL,   "intra=10,inter=2,par"),
        };

        foreach (var c in configs)
        {
            setConfig(opts =>
            {
                if (c.intra is int ia) opts.IntraOpNumThreads = ia;
                if (c.inter is int ie) opts.InterOpNumThreads = ie;
                opts.ExecutionMode = c.mode;
            });

            using var analyzer = make();

            // Warm up (first run JITs + allocates ORT arenas).
            var r0 = rasters[0];
            analyzer.RunAnalysis(r0.Rgb!, r0.W, r0.H, r0.PW, r0.PH, r0.Chars);

            var sw = Stopwatch.StartNew();
            foreach (var r in rasters)
                analyzer.RunAnalysis(r.Rgb!, r.W, r.H, r.PW, r.PH, r.Chars);
            sw.Stop();

            Console.WriteLine($"{c.label,-28}{sw.Elapsed.TotalMilliseconds / rasters.Count,10:F1}");
        }

        setConfig(null);
    }
}
