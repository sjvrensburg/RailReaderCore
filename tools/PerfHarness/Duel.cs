using System.Diagnostics;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace PerfHarness;

/// <summary>
/// Paired, interleaved A/B of V3 vs Heron inference, robust to the CPU-steal
/// variance of this shared/VM box. For each page we time V3 then Heron
/// back-to-back (so any contention spike hits both equally), loop several
/// rounds, and report min / median / max per analyzer plus the paired
/// median ratio. min ≈ true uncontended cost; the paired ratio cancels
/// time-varying noise far better than running all-V3 then all-Heron.
/// </summary>
internal static class Duel
{
    public static void Run(string pdf, string v3Model, string heronModel, int pages, int rounds)
    {
        var factory = new SkiaPdfServiceFactory();
        var pdfService = factory.CreatePdfService(pdf);
        var textService = factory.CreatePdfTextService();
        byte[] pdfBytes = pdfService.PdfBytes;

        using var v3 = new LayoutAnalyzer(v3Model);
        using var heron = new HeronLayoutAnalyzer(heronModel);
        int v3In = v3.Capabilities.InputSize;       // 800
        int hrIn = heron.Capabilities.InputSize;    // 640 advertised; resized internally

        int n = Math.Min(pages, pdfService.PageCount);

        // Pre-rasterise both input sizes per page once.
        var v3Raster = new List<(byte[] Rgb, int W, int H, double PW, double PH, IReadOnlyList<CharBox> C)>();
        var hrRaster = new List<(byte[] Rgb, int W, int H, double PW, double PH, IReadOnlyList<CharBox> C)>();
        for (int p = 0; p < n; p++)
        {
            var (pw, ph) = pdfService.GetPageSize(p);
            var text = textService.ExtractPageText(pdfBytes, p);
            var (r1, w1, h1) = pdfService.RenderPagePixmap(p, v3In);
            v3Raster.Add((r1, w1, h1, pw, ph, text.CharBoxes));
            var (r2, w2, h2) = pdfService.RenderPagePixmap(p, hrIn);
            hrRaster.Add((r2, w2, h2, pw, ph, text.CharBoxes));
        }

        // Warm up both (JIT + ORT arenas).
        v3.RunAnalysis(v3Raster[0].Rgb, v3Raster[0].W, v3Raster[0].H, v3Raster[0].PW, v3Raster[0].PH, v3Raster[0].C);
        heron.RunAnalysis(hrRaster[0].Rgb, hrRaster[0].W, hrRaster[0].H, hrRaster[0].PW, hrRaster[0].PH, hrRaster[0].C);

        var v3Times = new List<double>();
        var hrTimes = new List<double>();
        var ratios = new List<double>();

        for (int round = 0; round < rounds; round++)
        {
            for (int p = 0; p < n; p++)
            {
                double tv = Time(() => { var a = v3Raster[p]; v3.RunAnalysis(a.Rgb, a.W, a.H, a.PW, a.PH, a.C); });
                double th = Time(() => { var b = hrRaster[p]; heron.RunAnalysis(b.Rgb, b.W, b.H, b.PW, b.PH, b.C); });
                v3Times.Add(tv);
                hrTimes.Add(th);
                ratios.Add(tv / th);
            }
        }

        Console.WriteLine($"DUEL  pdf={Path.GetFileName(pdf)} pages={n} rounds={rounds} samples={v3Times.Count} nproc={Environment.ProcessorCount}");
        Console.WriteLine($"{"analyzer",-10}{"min",10}{"median",10}{"max",10}  (ms/pg, RunAnalysis wall-clock)");
        Report("V3", v3Times);
        Report("Heron", hrTimes);
        ratios.Sort();
        Console.WriteLine($"paired V3/Heron ratio: min={ratios[0]:F2} median={Median(ratios):F2} max={ratios[^1]:F2}");
        Console.WriteLine($"  (ratio>1 ⇒ Heron faster on that page; median is the noise-robust headline)");
    }

    private static double Time(Action a)
    {
        long t0 = Stopwatch.GetTimestamp();
        a();
        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    private static void Report(string label, List<double> times)
    {
        var s = new List<double>(times); s.Sort();
        Console.WriteLine($"{label,-10}{s[0],10:F1}{Median(s),10:F1}{s[^1],10:F1}");
    }

    private static double Median(List<double> sorted)
    {
        int c = sorted.Count;
        return c % 2 == 1 ? sorted[c / 2] : (sorted[c / 2 - 1] + sorted[c / 2]) / 2.0;
    }
}
