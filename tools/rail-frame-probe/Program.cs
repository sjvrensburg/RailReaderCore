using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Diagnostic probe for rail-mode camera stranding (the v0.51.0 KeepLineInFrame fix).
//
// For every navigable line on a page it computes where that line's own bounds land on
// screen when rail snaps to it, under two formulas:
//   BASE — the pre-0.51.0 target: frame on the chunk (or the block, for a self-framed
//          display unit), left-align/centre, hard-clamp into the scroll range.
//   NOW  — RailNav.ComputeSnapTarget, i.e. BASE plus KeepLineInFrame's nudge.
// A line is STRANDED when its own bounds land entirely outside the viewport, and FAR
// when its near edge lands past the window's midpoint (half the screen empty).
//
//   RailFrameProbe <pdf> <modelPath> <heron|v3|pps> [firstPage] [lastPage] [zoom] [winW] [winH]
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: RailFrameProbe <pdf> <modelPath> <heron|v3|pps> [first] [last] [zoom] [winW] [winH]");
    return 1;
}

string pdfPath = args[0], modelPath = args[1];
var arch = args[2].ToLowerInvariant() switch
{
    "heron" => LayoutModelArchitecture.Heron,
    "v3" => LayoutModelArchitecture.PPDocLayoutV3,
    "pps" => LayoutModelArchitecture.PPDocLayoutS,
    _ => throw new ArgumentException($"unknown arch '{args[2]}'"),
};
int first = args.Length > 3 ? int.Parse(args[3]) : 0;
int last = args.Length > 4 ? int.Parse(args[4]) : first;

// Each page is analysed once and then framed under every (zoom, window) combination —
// stranding is a function of how much wider the chunk is than the viewport, so the
// sweep is where the bug lives, not the page.
(double Zoom, double W, double H)[] configs = args.Length > 5
    ? [(double.Parse(args[5]), args.Length > 6 ? double.Parse(args[6]) : 1600, args.Length > 7 ? double.Parse(args[7]) : 1000)]
    : [(4, 1600, 1000), (6, 1600, 1000), (8, 1600, 1000), (4, 1000, 800), (6, 1000, 800), (10, 1000, 800)];

const double CenterBlockThreshold = 0.75; // mirrors CoreTuning.CenterBlockThreshold (internal)
var caps = LayoutAnalyzerFactory.CapabilitiesFor(arch);
RailReaderLogging.Logger = NullLogger.Instance;
var factory = new SkiaPdfServiceFactory();
var textSvc = factory.CreatePdfTextService();
using var analyzer = LayoutAnalyzerFactory.Create(arch, modelPath);
var svc = factory.CreatePdfService(pdfPath);
var settings = new CoreSettings();

Console.WriteLine($"== {Path.GetFileName(pdfPath)} pages {first}..{last} arch={arch}");
Console.WriteLine($"   centre-threshold={CenterBlockThreshold} rail-threshold={settings.RailZoomThreshold}");

// Per-config totals, indexed alongside `configs`.
var totLinesC = new int[configs.Length];
var totBaseStrandC = new int[configs.Length];
var totNowStrandC = new int[configs.Length];
var totBaseRightC = new int[configs.Length];
var totNowRightC = new int[configs.Length];
var totNudgedC = new int[configs.Length];
var totSlackC = new double[configs.Length];
var totMaxSlackC = new double[configs.Length];

for (int page = first; page <= last && page < svc.PageCount; page++)
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

    var rail = new RailNav(settings);
    rail.SetAnalysis(analysis, DefaultRoleSets.Navigable);
    if (!rail.HasAnalysis) { Console.WriteLine($"p{page}: no navigable blocks"); continue; }

    // Chunk membership (array indices), rebuilt exactly as RailNav builds it.
    var chunkMembers = new Dictionary<int, List<int>>();
    for (int i = 0; i < rail.NavigableCount; i++)
    {
        rail.CurrentBlock = i;
        (chunkMembers.TryGetValue(rail.CurrentChunk, out var l) ? l : chunkMembers[rail.CurrentChunk] = [])
            .Add(rail.CurrentNavigableArrayIndex);
    }

    for (int c = 0; c < configs.Length; c++)
    {
        var (zoom, winW, winH) = configs[c];
        var rows = new List<string>();
        int baseStranded = 0, nowStranded = 0, baseRight = 0, nowRight = 0, lineCount = 0;
        int nudged = 0; double slackSum = 0, maxSlack = 0, maxSlackW = winW;

        for (int i = 0; i < rail.NavigableCount; i++)
        {
            rail.CurrentBlock = i;
            var block = rail.CurrentNavigableBlock;
            int chunk = rail.CurrentChunk;
            var mem = chunkMembers[chunk];

            // --- framing unit, mirroring RailNav.GetFramingBounds ---
            bool centringRole = settings.CenteringRoles.Contains(block.Role);
            bool selfFramed = (block.Role == BlockRole.DisplayMath || block.Role == BlockRole.Algorithm) && centringRole;
            double uL, uR;
            if (selfFramed)
            {
                double m = block.BBox.W * 0.05;
                uL = block.BBox.X - m; uR = block.BBox.X + block.BBox.W + m;
            }
            else
            {
                float l0 = mem.Min(m => analysis.Blocks[m].BBox.X);
                float r0 = mem.Max(m => analysis.Blocks[m].BBox.X + analysis.Blocks[m].BBox.W);
                double m = (r0 - l0) * 0.05;
                uL = l0 - m; uR = r0 + m;
            }
            double uW = (uR - uL) * zoom;
            bool centred = uW < winW * CenterBlockThreshold && centringRole;

            for (int j = 0; j < block.Lines.Count; j++)
            {
                rail.CurrentLine = j;
                var line = rail.CurrentLineInfo;
                double lm = line.Width * 0.05;
                double lL = line.X - lm, lR = line.X + line.Width + lm;

                // BASE: pre-fix ComputeTargetCamera.
                double baseX;
                if (centred)
                    baseX = winW / 2.0 - (uL + uR) / 2.0 * zoom;
                else
                    baseX = winW * 0.05 - uL * zoom;
                if (uW > winW)
                    baseX = Math.Clamp(baseX, winW - uR * zoom, -uL * zoom);

                double nowX = rail.ComputeSnapTarget(zoom, winW, winH).X;

                double bL = lL * zoom + baseX, bR = lR * zoom + baseX;
                double nL = lL * zoom + nowX, nR = lR * zoom + nowX;

                // Stranded: the line's own bounds land entirely off-screen.
                bool bStrand = bR < 0 || bL > winW, nStrand = nR < 0 || nL > winW;
                // Right-far: the line's START is past the window midpoint — the reported
                // symptom (near half of the screen empty, long scroll right to find the text).
                bool bRight = bL > winW / 2.0, nRight = nL > winW / 2.0;

                // Residual after the fix: KeepLineInFrame nudges only the SNAP TARGET, while
                // ClampX / IsAtHardEdge / ComputeHorizontalFraction still bound the camera by the
                // chunk. `slack` is how far left of the nudged landing the camera may still travel
                // before the backward hard-edge fires — i.e. how far the reader can drift off the
                // line the snap just framed, expressed in windows.
                if (uW > winW && Math.Abs(nowX - baseX) > 1)
                {
                    nudged++;
                    double slack = -uL * zoom - nowX;
                    if (slack > maxSlack) { maxSlack = slack; maxSlackW = winW; }
                    slackSum += slack;
                }

                lineCount++;
                if (bStrand) baseStranded++;
                if (nStrand) nowStranded++;
                if (bRight) baseRight++;
                if (nRight) nowRight++;

                if (bStrand || nStrand || bRight || nRight)
                    rows.Add($"    blk{rail.CurrentNavigableArrayIndex,-3} {block.Role,-11} ln{j,-3} " +
                             $"chunk{chunk,-3} chunkW={(uR - uL),6:F0}pt blkW={block.BBox.W,6:F0}pt lineX={line.X,6:F0} lineW={line.Width,6:F0} | " +
                             $"BASE[{bL,8:F0}..{bR,8:F0}]{(bStrand ? " STRANDED" : bRight ? " far-right" : "          ")} " +
                             $"NOW[{nL,8:F0}..{nR,8:F0}]{(nStrand ? " STRANDED" : nRight ? " far-right" : "")}");
            }
        }

        Console.WriteLine($"p{page} z={zoom} win={winW}x{winH}: lines={lineCount} chunks={chunkMembers.Count} | " +
                          $"stranded base={baseStranded} now={nowStranded} | far-right base={baseRight} now={nowRight} | " +
                          $"nudged={nudged} slack avg={(nudged > 0 ? slackSum / nudged : 0):F0}px max={maxSlack:F0}px ({maxSlack / maxSlackW:F2} windows)");
        foreach (var r in rows) Console.WriteLine(r);

        totLinesC[c] += lineCount; totBaseStrandC[c] += baseStranded; totNowStrandC[c] += nowStranded;
        totBaseRightC[c] += baseRight; totNowRightC[c] += nowRight;
        totNudgedC[c] += nudged; totSlackC[c] += slackSum; if (maxSlack > totMaxSlackC[c]) totMaxSlackC[c] = maxSlack;
    }
}

Console.WriteLine();
for (int c = 0; c < configs.Length; c++)
    Console.WriteLine($"TOTAL z={configs[c].Zoom} win={configs[c].W}x{configs[c].H}: lines={totLinesC[c]} | " +
                      $"stranded base={totBaseStrandC[c]} now={totNowStrandC[c]} | " +
                      $"far-right base={totBaseRightC[c]} now={totNowRightC[c]} | " +
                      $"nudged={totNudgedC[c]} slack avg={(totNudgedC[c] > 0 ? totSlackC[c] / totNudgedC[c] : 0):F0}px max={totMaxSlackC[c]:F0}px ({totMaxSlackC[c] / configs[c].W:F2} windows)");
return 0;
