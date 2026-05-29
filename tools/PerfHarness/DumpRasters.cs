using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace PerfHarness;

/// <summary>
/// Dumps production-faithful page rasters for quantization calibration /
/// accuracy validation. Renders pages via the real PDFium/Skia
/// <see cref="IPdfService.RenderPagePixmap"/> at Heron's advertised
/// InputSize (1024 longest edge) — exactly what the analyzer feeds before its
/// internal 640×640 resize — and writes each as a raw RGB (HWC, row-major)
/// blob with dimensions encoded in the filename:
///     {pdfstem}_p{page}_{W}x{H}.rgb
/// Python side reads with np.fromfile(uint8).reshape(H, W, 3).
/// </summary>
internal static class DumpRasters
{
    public static void Run(string pdfDir, string outDir, int pagesPerPdf, int rasterEdge)
    {
        Directory.CreateDirectory(outDir);
        var factory = new SkiaPdfServiceFactory();

        var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToArray();
        Console.WriteLine($"DUMP rasters: {pdfs.Length} PDFs from {pdfDir} -> {outDir} " +
                          $"(<= {pagesPerPdf} pages each @ {rasterEdge}px longest edge)");

        int written = 0;
        foreach (var pdf in pdfs)
        {
            string stem = Path.GetFileNameWithoutExtension(pdf)
                .Replace(' ', '_').Replace(',', '_');
            IPdfService svc;
            try { svc = factory.CreatePdfService(pdf); }
            catch (Exception e) { Console.WriteLine($"  SKIP {stem}: {e.GetType().Name}"); continue; }

            int n = Math.Min(pagesPerPdf, svc.PageCount);
            for (int p = 0; p < n; p++)
            {
                try
                {
                    var (rgb, w, h) = svc.RenderPagePixmap(p, rasterEdge);
                    string path = Path.Combine(outDir, $"{stem}_p{p}_{w}x{h}.rgb");
                    File.WriteAllBytes(path, rgb);
                    written++;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  page fail {stem} p{p}: {e.GetType().Name}");
                }
            }
            (svc as IDisposable)?.Dispose();
        }
        Console.WriteLine($"DUMP done: {written} rasters written");
    }
}
