using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Dumps the PRODUCTION line-detection result per page so it can be scored against
// a Surya text-line-detection oracle (see line_oracle.py / compare_lines.py).
//
// args[0] = model path, args[1] = architecture (Heron|PPDocLayoutV3|PPDocLayoutS),
// args[2] = output json, args[3..] = corpus dirs
// env: LINEEVAL_MAXPAGES (default 6)

var modelPath = args[0];
var arch = Enum.Parse<LayoutModelArchitecture>(args[1], ignoreCase: true);
var outPath = args[2];
var dirs = args.Skip(3).ToArray();
int maxPages = int.TryParse(Environment.GetEnvironmentVariable("LINEEVAL_MAXPAGES"), out var mp) ? mp : 6;

RailReaderLogging.Logger = NullLogger.Instance;
var factory = new SkiaPdfServiceFactory();

Console.Error.WriteLine($"Analyzer: {arch} {modelPath}");
using var analyzer = LayoutAnalyzerFactory.Create(arch, modelPath);
int inputSize = analyzer.Capabilities.InputSize;
var resolver = new XYCutPlusPlusResolver();

var pdfs = new List<string>();
foreach (var d in dirs)
{
    if (!Directory.Exists(d)) { Console.Error.WriteLine($"MISSING dir: {d}"); continue; }
    pdfs.AddRange(Directory.EnumerateFiles(d, "*.pdf", SearchOption.AllDirectories));
}
pdfs.Sort();
Console.Error.WriteLine($"{pdfs.Count} PDFs; maxPages/doc={maxPages}");

var textSvc = factory.CreatePdfTextService();
var pageOut = new List<object>();
int docIdx = 0;

foreach (var pdfPath in pdfs)
{
    docIdx++;
    IPdfService pdf;
    try { pdf = factory.CreatePdfService(pdfPath); }
    catch (Exception ex) { Console.Error.WriteLine($"[{docIdx}] OPEN FAIL {Path.GetFileName(pdfPath)}: {ex.Message}"); continue; }

    int nPages = Math.Min(pdf.PageCount, maxPages);
    Console.Error.WriteLine($"[{docIdx}/{pdfs.Count}] {Path.GetFileName(pdfPath)} ({pdf.PageCount}p, dump {nPages})");

    for (int p = 0; p < nPages; p++)
    {
        try
        {
            var (pw, ph) = pdf.GetPageSize(p);
            var pageText = textSvc.ExtractPageText(pdf.PdfBytes, p);
            bool hasText = pageText.CharBoxes is { Count: > 0 };
            var (rgb, pxW, pxH) = pdf.RenderPagePixmap(p, inputSize);

            var analysis = analyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, pageText.CharBoxes, default);
            resolver.AssignOrder(analysis.Blocks, pw, ph, pageText.CharBoxes);
            float msx = pxW > 0 ? (float)(pw / pxW) : 1f;
            float msy = pxH > 0 ? (float)(ph / pxH) : 1f;
            BlockPostProcessor.PostProcess(analysis.Blocks, rgb, pxW, pxH, msx, msy, pageText.CharBoxes);

            var blocks = analysis.Blocks.Select(b => (object)new
            {
                role = b.Role.ToString(),
                x = b.BBox.X, y = b.BBox.Y, w = b.BBox.W, h = b.BBox.H,
                lines = b.Lines.Select(l => new[] { l.Y, l.Height, l.X, l.Width }).ToList()
            }).ToList();

            pageOut.Add(new
            {
                dir = Path.GetFileName(Path.GetDirectoryName(pdfPath) ?? ""),
                pdf = Path.GetFileName(pdfPath),
                page = p, pw, ph, hasText, blocks
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"    page {p} ERR: {ex.Message}"); }
    }
}

File.WriteAllText(outPath, JsonSerializer.Serialize(new { pages = pageOut }));
Console.Error.WriteLine($"Wrote {outPath} ({pageOut.Count} pages)");
