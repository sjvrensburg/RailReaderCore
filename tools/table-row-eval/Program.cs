using System.Text.Json;
using RailReader.Core.Models;
using RailReader.Core.Services;

// Scores LineDetector's table-ROW detection against the SynFinTabs ground truth.
//
// For each table example we synthesise CharBoxes from the dataset's word boxes,
// run the production LineDetector on a Table-role block, and compare the detected
// line Y-bands against the dataset's ground-truth semantic rows (1-D Y-IoU).
//
// Over-segmentation is expected and reported separately: a multi-line wrapped
// cell legitimately produces >1 visual line for a single semantic row. For a
// rail reader what matters most is COVERAGE (every row reachable) — reported as
// coverage-recall and the mean lines-per-row split ratio.
//
// args[0] = input json (from fetch_synfintabs.py)
// env: TABLEEVAL_IOU (match threshold, default 0.5)

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: TableRowEval <synfintabs.json>");
    return 1;
}

float iouThresh = float.TryParse(Environment.GetEnvironmentVariable("TABLEEVAL_IOU"), out var t) ? t : 0.5f;

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var examples = doc.RootElement.GetProperty("examples");

static float[] Bbox(JsonElement e) =>
    [(float)e[0].GetDouble(), (float)e[1].GetDouble(), (float)e[2].GetDouble(), (float)e[3].GetDouble()];

// 1-D interval IoU along Y.
static float YIoU((float lo, float hi) a, (float lo, float hi) b)
{
    float inter = MathF.Min(a.hi, b.hi) - MathF.Max(a.lo, b.lo);
    if (inter <= 0) return 0;
    float union = MathF.Max(a.hi, b.hi) - MathF.Min(a.lo, b.lo);
    return union <= 0 ? 0 : inter / union;
}

int nExamples = 0, wordEmptyExamples = 0;
long tp = 0, fp = 0, fn = 0;
int exactCount = 0, overSeg = 0, underSeg = 0;
double coverageSum = 0, splitSum = 0, matchedIoUSum = 0;
long matchedPairs = 0;
int wordEmptyRowsTotal = 0;
// Centroid metrics — robust to the dataset's row-box vertical padding (GT rows
// are grid cells ~1.75x taller than the glyphs, so Y-IoU understates a correct
// one-line-per-row result). A line is "in" a row when its centre falls in the band.
long centroidHits = 0, detectedTotal = 0, rowHit = 0, rowExactlyOne = 0, gtTotal = 0;

foreach (var exWrap in examples.EnumerateArray())
{
    var ex = exWrap;
    var tb = Bbox(ex.GetProperty("table_bbox"));
    var block = new LayoutBlock
    {
        BBox = new BBox(tb[0], tb[1], tb[2] - tb[0], tb[3] - tb[1]),
        Role = BlockRole.Table,
        Confidence = 1f,
    };

    // Synthesise CharBoxes from word boxes ([l,t,r,b] -> CharBox(idx, l,t,r,b)).
    var words = new List<(float lo, float hi, float mid)>();
    var charBoxes = new List<CharBox>();
    int idx = 0;
    foreach (var w in ex.GetProperty("words").EnumerateArray())
    {
        var b = Bbox(w);
        charBoxes.Add(new CharBox(idx++, b[0], b[1], b[2], b[3]));
        words.Add((b[1], b[3], (b[1] + b[3]) * 0.5f));
    }

    // Ground-truth rows, restricted to those that actually contain a word
    // (spacer / rule rows the detector cannot see are excluded but counted).
    var gtRows = new List<(float lo, float hi)>();
    int wordEmptyRows = 0;
    foreach (var r in ex.GetProperty("rows").EnumerateArray())
    {
        var b = Bbox(r);
        var band = (b[1], b[3]);
        bool hasWord = words.Any(wd => wd.mid >= band.Item1 && wd.mid <= band.Item2);
        if (hasWord) gtRows.Add(band);
        else wordEmptyRows++;
    }
    wordEmptyRowsTotal += wordEmptyRows;

    if (gtRows.Count == 0 || charBoxes.Count == 0) { wordEmptyExamples++; continue; }
    nExamples++;

    var lines = LineDetector.DetectLines(block, charBoxes, [], 0, 0, 1, 1);
    var det = lines.Select(l => (l.Y - l.Height / 2f, l.Y + l.Height / 2f)).ToList();
    var centers = lines.Select(l => l.Y).ToList();

    // Centroid containment: assign each detected line centre to the GT row whose
    // band contains it; tally per-row hits.
    detectedTotal += det.Count;
    gtTotal += gtRows.Count;
    var rowCenterCount = new int[gtRows.Count];
    foreach (var cy in centers)
    {
        int hitRow = -1;
        for (int g = 0; g < gtRows.Count; g++)
            if (cy >= gtRows[g].Item1 && cy <= gtRows[g].Item2) { hitRow = g; break; }
        if (hitRow >= 0) { centroidHits++; rowCenterCount[hitRow]++; }
    }
    foreach (var c in rowCenterCount)
    {
        if (c >= 1) rowHit++;
        if (c == 1) rowExactlyOne++;
    }

    // Greedy 1-D Y-IoU matching (detected <-> gt), highest IoU first.
    var pairs = new List<(float iou, int d, int g)>();
    for (int d = 0; d < det.Count; d++)
        for (int g = 0; g < gtRows.Count; g++)
        {
            float iou = YIoU(det[d], gtRows[g]);
            if (iou >= iouThresh) pairs.Add((iou, d, g));
        }
    pairs.Sort((x, y) => y.iou.CompareTo(x.iou));
    var dUsed = new bool[det.Count];
    var gUsed = new bool[gtRows.Count];
    int exTp = 0;
    foreach (var (iou, d, g) in pairs)
    {
        if (dUsed[d] || gUsed[g]) continue;
        dUsed[d] = gUsed[g] = true;
        exTp++;
        matchedIoUSum += iou;
        matchedPairs++;
    }

    int exFp = det.Count - exTp;
    int exFn = gtRows.Count - exTp;
    tp += exTp; fp += exFp; fn += exFn;

    // Coverage: a GT row is "covered" if ANY detected line meets the threshold
    // (lenient to over-segmentation — what matters is the row is reachable).
    int covered = 0;
    for (int g = 0; g < gtRows.Count; g++)
        if (det.Any(dd => YIoU(dd, gtRows[g]) >= iouThresh)) covered++;
    coverageSum += (double)covered / gtRows.Count;
    splitSum += (double)det.Count / gtRows.Count;

    if (det.Count == gtRows.Count) exactCount++;
    else if (det.Count > gtRows.Count) overSeg++;
    else underSeg++;
}

double precision = tp + fp > 0 ? (double)tp / (tp + fp) : 0;
double recall = tp + fn > 0 ? (double)tp / (tp + fn) : 0;
double f1 = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;

double cPrecision = detectedTotal > 0 ? (double)centroidHits / detectedTotal : 0;
double cRecall = gtTotal > 0 ? (double)rowHit / gtTotal : 0;
double oneToOne = gtTotal > 0 ? (double)rowExactlyOne / gtTotal : 0;

Console.WriteLine($"SynFinTabs table-row eval  ({nExamples} tables, {gtTotal} rows, IoU>={iouThresh:0.00})");
Console.WriteLine($"  skipped word-empty   : {wordEmptyExamples} examples, {wordEmptyRowsTotal} GT rows");
Console.WriteLine();
Console.WriteLine($"  CENTROID (one steppable line per row — the rail-nav metric):");
Console.WriteLine($"    row coverage       : {cRecall:0.000}   <- fraction of rows with >=1 detected line centre inside");
Console.WriteLine($"    one-line-per-row   : {oneToOne:0.000}   <- fraction of rows with exactly one");
Console.WriteLine($"    centre-in-a-row    : {cPrecision:0.000}   <- fraction of detected lines landing in some row");
Console.WriteLine($"    lines/row split    : {(nExamples > 0 ? splitSum / nExamples : 0):0.000}   <- 1.0 = perfect, >1 = over-seg");
Console.WriteLine($"    exact row count    : {exactCount}/{nExamples} ({(nExamples > 0 ? 100.0 * exactCount / nExamples : 0):0.0}%),  over/under-seg {overSeg}/{underSeg}");
Console.WriteLine();
Console.WriteLine($"  Y-IoU (strict band overlap — understated by GT row-box padding ~1.75x glyph height):");
Console.WriteLine($"    precision / recall : {precision:0.000} / {recall:0.000},  F1 {f1:0.000}");
Console.WriteLine($"    matched mean Y-IoU : {(matchedPairs > 0 ? matchedIoUSum / matchedPairs : 0):0.000}");
Console.WriteLine($"    coverage-recall    : {(nExamples > 0 ? coverageSum / nExamples : 0):0.000}");
return 0;
