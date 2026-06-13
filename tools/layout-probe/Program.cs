using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Diagnostic probe for the drop-cap layout bugs.
//   - symptom 1: a drop cap collapsing the lines it spans (line detection)
//   - symptom 2: a full-width block dragging a column's rail frame across the gutter
//
// Runs the REAL pipeline on a page: rasterise -> char boxes -> analyzer ->
// reading order -> BlockPostProcessor -> RailNav chunk build.
//
// Single-PDF (detailed dump):
//   LayoutProbe <pdf>  <modelPath> <heron|v3|pps> [page=0]
// Corpus (one compact diffable summary line per page; for regression diffs):
//   LayoutProbe <dir>  <modelPath> <heron|v3|pps>     (env LAYOUTPROBE_MAXPAGES, default 4)
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: LayoutProbe <pdf|dir> <modelPath> <heron|v3|pps> [page]");
    return 1;
}

string target = args[0], modelPath = args[1], archArg = args[2].ToLowerInvariant();
var arch = archArg switch
{
    "heron" => LayoutModelArchitecture.Heron,
    "v3" => LayoutModelArchitecture.PPDocLayoutV3,
    "pps" => LayoutModelArchitecture.PPDocLayoutS,
    _ => throw new ArgumentException($"unknown arch '{archArg}'"),
};
var caps = LayoutAnalyzerFactory.CapabilitiesFor(arch);

RailReaderLogging.Logger = NullLogger.Instance;
var factory = new SkiaPdfServiceFactory();
var textSvc = factory.CreatePdfTextService();
using var analyzer = LayoutAnalyzerFactory.Create(arch, modelPath);

// (analysis, pageWidth, pageHeight) for one page through the full pipeline.
(PageAnalysis Analysis, double Pw, double Ph) Analyze(IPdfService svc, int page)
{
    var (pw, ph) = svc.GetPageSize(page);
    var pageText = textSvc.ExtractPageText(svc.PdfBytes, page);
    var (rgb, pxW, pxH) = svc.RenderPagePixmap(page, caps.InputSize);
    var analysis = analyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, pageText.CharBoxes, default);
    IReadingOrderResolver resolver = caps.ProvidesReadingOrder
        ? new ModelOrderResolver() : new XYCutPlusPlusResolver();
    resolver.AssignOrder(analysis.Blocks, pw, ph, pageText.CharBoxes);
    float sx = pxW > 0 ? (float)(pw / pxW) : 1f, sy = pxH > 0 ? (float)(ph / pxH) : 1f;
    BlockPostProcessor.PostProcess(analysis.Blocks, rgb, pxW, pxH, sx, sy, pageText.CharBoxes);
    return (analysis, pw, ph);
}

// Reconstruct chunk membership (page-block indices) via RailNav, as the runtime builds it.
List<List<int>> Chunks(PageAnalysis analysis)
{
    var rail = new RailNav(new CoreSettings());
    rail.SetAnalysis(analysis, DefaultRoleSets.Navigable);
    var byChunk = new SortedDictionary<int, List<int>>();
    for (int i = 0; i < rail.NavigableCount; i++)
    {
        rail.CurrentBlock = i;
        (byChunk.TryGetValue(rail.CurrentChunk, out var l) ? l : byChunk[rail.CurrentChunk] = [])
            .Add(rail.CurrentNavigableArrayIndex);
    }
    return byChunk.Values.ToList();
}

if (Directory.Exists(target))
{
    int maxPages = int.TryParse(Environment.GetEnvironmentVariable("LAYOUTPROBE_MAXPAGES"), out var mp) ? mp : 4;
    var pdfs = Directory.EnumerateFiles(target, "*.pdf", SearchOption.AllDirectories).OrderBy(x => x).ToList();
    Console.Error.WriteLine($"corpus: {pdfs.Count} PDFs, maxPages/doc={maxPages}, arch={arch}");
    foreach (var pdfPath in pdfs)
    {
        string rel = Path.GetRelativePath(target, pdfPath);
        IPdfService svc;
        try { svc = factory.CreatePdfService(pdfPath); }
        catch (Exception ex) { Console.Error.WriteLine($"OPEN FAIL {rel}: {ex.Message}"); continue; }
        int n = Math.Min(svc.PageCount, maxPages);
        for (int p = 0; p < n; p++)
        {
            try
            {
                var (analysis, pw, _) = Analyze(svc, p);
                var lines = string.Join(",", analysis.Blocks.Select(b => b.Lines.Count));
                var cgroups = Chunks(analysis);
                var chunks = string.Join(";", cgroups.Select(g => string.Join("+", g)));
                // Count chunks whose union X-extent straddles the page centre band — a
                // chunk framing the camera across the column gutter (the symptom-2 bug).
                double lo = pw * 0.45, hi = pw * 0.55;
                int spanBoth = cgroups.Count(g =>
                {
                    float left = g.Min(m => analysis.Blocks[m].BBox.X);
                    float right = g.Max(m => analysis.Blocks[m].BBox.X + analysis.Blocks[m].BBox.W);
                    return left < lo && right > hi;
                });
                Console.WriteLine($"{rel}|p{p}|nb={analysis.Blocks.Count}|L={lines}|SB={spanBoth}|C={chunks}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"{rel} p{p} ERR: {ex.Message}"); }
        }
    }
    return 0;
}

// ---- single-PDF detailed dump ----
int page = args.Length > 3 && int.TryParse(args[3], out var pg) ? pg : 0;
var svc1 = factory.CreatePdfService(target);
var (an, pw1, _) = Analyze(svc1, page);
var blocks = an.Blocks;
double loBand = pw1 * 0.45, hiBand = pw1 * 0.55;
bool Straddles(BBox b) => b.X < loBand && b.X + b.W > hiBand;

Console.WriteLine($"== {Path.GetFileName(target)} p{page} == arch={arch} page={pw1:F1} centre={pw1 / 2:F1} blocks={blocks.Count}");
Console.WriteLine($"{"#",-3} {"role",-12} {"ord",-4} {"x",6} {"y",6} {"w",6} {"h",6} {"lines",5}  tallestLine  span");
foreach (var (b, i) in blocks.Select((b, i) => (b, i)))
{
    float tallest = b.Lines.Count == 0 ? 0 : b.Lines.Max(l => l.Height);
    Console.WriteLine($"{i,-3} {b.Role,-12} {b.Order,-4} {b.BBox.X,6:F0} {b.BBox.Y,6:F0} {b.BBox.W,6:F0} {b.BBox.H,6:F0} {b.Lines.Count,5}  {tallest,10:F1}{(Straddles(b.BBox) ? "  <-- BOTH COLS" : "")}");
}
Console.WriteLine();
var cs = Chunks(an);
Console.WriteLine($"chunks: {cs.Count}");
Console.WriteLine($"{"chunk",-5} {"members(page#)",-26} {"unionX",7} {"unionR",7} {"unionW",7}  span");
for (int c = 0; c < cs.Count; c++)
{
    var mem = cs[c];
    float left = mem.Min(m => blocks[m].BBox.X), right = mem.Max(m => blocks[m].BBox.X + blocks[m].BBox.W);
    string roles = string.Join(",", mem.Select(m => $"{m}:{blocks[m].Role}"));
    Console.WriteLine($"{c,-5} {roles,-26} {left,7:F0} {right,7:F0} {right - left,7:F0}{(Straddles(new BBox(left, 0, right - left, 1)) ? "  <-- CHUNK SPANS BOTH COLS" : "")}");
}
return 0;
