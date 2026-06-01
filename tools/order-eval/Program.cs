using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// args[0] = V3 model path, args[1] = output json prefix, args[2..] = corpus dirs
// env: ORDEREVAL_MAXPAGES (default 12), MERGE_GAP (default 30)

var v3Path = args[0];
var outPrefix = args[1];
var dirs = args.Skip(2).ToArray();
int maxPages = int.TryParse(Environment.GetEnvironmentVariable("ORDEREVAL_MAXPAGES"), out var mp) ? mp : 12;
float[] gaps = { 6f, 10f, 14f, 20f, 30f };

RailReaderLogging.Logger = NullLogger.Instance;
var factory = new SkiaPdfServiceFactory();

Console.Error.WriteLine($"Loading V3 analyzer: {v3Path}  (gaps={string.Join(",", gaps)})");
using var analyzer = new LayoutAnalyzer(v3Path);
int inputSize = analyzer.Capabilities.InputSize;

var pdfs = new List<string>();
foreach (var d in dirs)
{
    if (!Directory.Exists(d)) { Console.Error.WriteLine($"MISSING dir: {d}"); continue; }
    pdfs.AddRange(Directory.EnumerateFiles(d, "*.pdf", SearchOption.AllDirectories));
}
pdfs.Sort();
Console.Error.WriteLine($"{pdfs.Count} PDFs; maxPages/doc={maxPages}");

var pageRecords = new List<PageRec>();
var textSvc = factory.CreatePdfTextService();
int docIdx = 0;
var impurePerGap = new int[gaps.Length];
var multiPerGap = new int[gaps.Length];

foreach (var pdfPath in pdfs)
{
    docIdx++;
    IPdfService pdf;
    try { pdf = factory.CreatePdfService(pdfPath); }
    catch (Exception ex) { Console.Error.WriteLine($"[{docIdx}] OPEN FAIL {Path.GetFileName(pdfPath)}: {ex.Message}"); continue; }

    int nPages = Math.Min(pdf.PageCount, maxPages);
    Console.Error.WriteLine($"[{docIdx}/{pdfs.Count}] {Path.GetFileName(pdfPath)} ({pdf.PageCount}p, eval {nPages})");

    for (int p = 0; p < nPages; p++)
    {
        try
        {
            var (pw, ph) = pdf.GetPageSize(p);
            var pageText = textSvc.ExtractPageText(pdf.PdfBytes, p);
            var (rgb, pxW, pxH) = pdf.RenderPagePixmap(p, inputSize);
            var analysis = analyzer.RunAnalysis(rgb, pxW, pxH, pw, ph, pageText.CharBoxes, default);
            var blocks = analysis.Blocks;
            if (blocks.Count < 3) continue;

            for (int i = 0; i < blocks.Count; i++) blocks[i].ClassId = i;
            int distinctRaw = blocks.Select(b => b.Order).Distinct().Count();

            var meta = blocks.Select(b => new BlockMeta(
                b.ClassId, b.Role.ToString(), b.BBox.X, b.BBox.Y, b.BBox.W, b.BBox.H, "")).ToList();

            var v3Clone = blocks.Select(Clone).ToList();
            var oursClone = blocks.Select(Clone).ToList();
            new ModelOrderResolver().AssignOrder(v3Clone, pw, ph);
            new XYCutPlusPlusResolver().AssignOrder(oursClone, pw, ph, pageText.CharBoxes);
            var v3Seq = v3Clone.Select(b => b.ClassId).ToList();
            var oursSeq = oursClone.Select(b => b.ClassId).ToList();

            // --- merge-then-order, swept over gap thresholds ---
            var v3RankById = new Dictionary<int, int>();
            for (int i = 0; i < v3Seq.Count; i++) v3RankById[v3Seq[i]] = i;

            var mergeSeqs = new List<List<int>>();
            foreach (var gap in gaps)
            {
                var groups = MergeGroups(blocks.Select(Clone).ToList(), pw, gap);
                var supers = new List<LayoutBlock>();
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    float minx = g.Min(x => x.BBox.X), miny = g.Min(x => x.BBox.Y);
                    float maxx = g.Max(x => x.BBox.X + x.BBox.W), maxy = g.Max(x => x.BBox.Y + x.BBox.H);
                    supers.Add(new LayoutBlock
                    {
                        BBox = new BBox(minx, miny, maxx - minx, maxy - miny),
                        Role = g.Count == 1 ? g[0].Role : BlockRole.Text,
                        ClassId = gi, Confidence = 1f
                    });
                }
                new XYCutPlusPlusResolver().AssignOrder(supers, pw, ph, pageText.CharBoxes);
                var mergeSeq = new List<int>();
                foreach (var s in supers)
                    foreach (var m in groups[s.ClassId].OrderBy(x => x.BBox.Y).ThenBy(x => x.BBox.X))
                        mergeSeq.Add(m.ClassId);
                mergeSeqs.Add(mergeSeq);

                int gi2 = Array.IndexOf(gaps, gap);
                foreach (var g in groups)
                {
                    if (g.Count < 2) continue;
                    multiPerGap[gi2]++;
                    var ranks = g.Select(m => v3RankById[m.ClassId]).ToList();
                    if (ranks.Max() - ranks.Min() + 1 != g.Count) impurePerGap[gi2]++;
                }
            }

            pageRecords.Add(new PageRec(
                Path.GetFileName(Path.GetDirectoryName(pdfPath) ?? ""), Path.GetFileName(pdfPath), p,
                blocks.Count, distinctRaw, KendallTauDistance(v3Seq, oursSeq),
                v3Seq, oursSeq, mergeSeqs, meta));
        }
        catch (Exception ex) { Console.Error.WriteLine($"    page {p} ERR: {ex.Message}"); }
    }
}

var usable = pageRecords.Where(r => r.DistinctRawOrder > 1).ToList();
var furn = new HashSet<string> { "Header", "Footer", "PageNumber" };

double BodyTau(PageRec r, List<int> seq)
{
    var roleById = r.Meta.ToDictionary(m => m.Id, m => m.Role);
    var keep = new HashSet<int>(r.Meta.Where(m => !furn.Contains(m.Role)).Select(m => m.Id));
    var a = r.V3Seq.Where(keep.Contains).ToList();
    var b = seq.Where(keep.Contains).ToList();
    return KendallTauDistance(a, b);
}

Console.Error.WriteLine($"\nPages usable: {usable.Count}");
Console.Error.WriteLine($"RAW   body-tau={usable.Average(r => BodyTau(r, r.OursSeq)):F4}  "
    + $"exact={usable.Count(r => KendallTauDistance(r.V3Seq, r.OursSeq) == 0)}");
Console.Error.WriteLine($"{"gap",4} {"bodyTau",8} {"fullTau",8} {"exact",6} {"purity%",8}");
for (int gi = 0; gi < gaps.Length; gi++)
{
    int g = gi;
    double bt = usable.Average(r => BodyTau(r, r.MergeSeqs[g]));
    double ft = usable.Average(r => KendallTauDistance(r.V3Seq, r.MergeSeqs[g]));
    int ex = usable.Count(r => KendallTauDistance(r.V3Seq, r.MergeSeqs[g]) == 0);
    double pur = 100.0 * (multiPerGap[g] - impurePerGap[g]) / Math.Max(1, multiPerGap[g]);
    Console.Error.WriteLine($"{gaps[g],4} {bt,8:F4} {ft,8:F4} {ex,6} {pur,8:F1}");
}

// Dump per-gap merge files + raw, for the interleaving analyzer (OursSeq = the ordering).
void Dump(string path, Func<PageRec, List<int>> seq) =>
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        pageRecords = pageRecords.Select(r => new
        {
            r.Dir, r.Pdf, r.Page, r.NBlocks, r.DistinctRawOrder, r.Tau,
            r.V3Seq, OursSeq = seq(r), r.Meta
        })
    }));
Dump(outPrefix + "_raw.json", r => r.OursSeq);
for (int gi = 0; gi < gaps.Length; gi++)
{
    int g = gi;
    Dump($"{outPrefix}_merge{gaps[g]:F0}.json", r => r.MergeSeqs[g]);
}
Console.Error.WriteLine($"Wrote {outPrefix}_raw.json and per-gap merge files");

static LayoutBlock Clone(LayoutBlock b) => new()
{ BBox = b.BBox, Role = b.Role, ClassId = b.ClassId, Confidence = b.Confidence, Order = b.Order };

static bool IsBarrier(LayoutBlock b, double pageW) =>
    b.Role is BlockRole.Figure or BlockRole.Table or BlockRole.Chart
        or BlockRole.Header or BlockRole.Footer or BlockRole.PageNumber
    || b.BBox.W >= 0.55 * pageW;

static List<List<LayoutBlock>> MergeGroups(List<LayoutBlock> blocks, double pageW, float gapThresh)
{
    var sorted = blocks.OrderBy(b => b.BBox.Y).ThenBy(b => b.BBox.X).ToList();
    var groups = new List<List<LayoutBlock>>();
    var info = new List<(float L, float R, float Bottom)>();
    foreach (var b in sorted)
    {
        float bl = b.BBox.X, br = b.BBox.X + b.BBox.W, bb = b.BBox.Y + b.BBox.H;
        if (IsBarrier(b, pageW)) { groups.Add([b]); info.Add((bl, br, bb)); continue; }
        int best = -1; float bestFrac = -1;
        for (int g = 0; g < groups.Count; g++)
        {
            if (IsBarrier(groups[g][^1], pageW)) continue;
            var (gl, gr, gbot) = info[g];
            float ov = Math.Min(gr, br) - Math.Max(gl, bl);
            float minW = Math.Min(gr - gl, b.BBox.W);
            float frac = minW > 0 ? ov / minW : 0;
            float gap = b.BBox.Y - gbot;
            if (frac >= 0.5f && gap <= gapThresh && gap >= -0.5f * b.BBox.H && frac > bestFrac)
            { bestFrac = frac; best = g; }
        }
        if (best >= 0)
        {
            groups[best].Add(b);
            var (gl, gr, gbot) = info[best];
            info[best] = (Math.Min(gl, bl), Math.Max(gr, br), Math.Max(gbot, bb));
        }
        else { groups.Add([b]); info.Add((bl, br, bb)); }
    }
    return groups;
}

static double KendallTauDistance(List<int> a, List<int> b)
{
    var rankB = new Dictionary<int, int>();
    for (int i = 0; i < b.Count; i++) rankB[b[i]] = i;
    int n = a.Count, discord = 0, total = 0;
    for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        { total++; if (rankB[a[i]] > rankB[a[j]]) discord++; }
    return total == 0 ? 0 : (double)discord / total;
}

record BlockMeta(int Id, string Role, float X, float Y, float W, float H, string Text);
record PageRec(string Dir, string Pdf, int Page, int NBlocks, int DistinctRawOrder, double Tau,
    List<int> V3Seq, List<int> OursSeq, List<List<int>> MergeSeqs, List<BlockMeta> Meta);
