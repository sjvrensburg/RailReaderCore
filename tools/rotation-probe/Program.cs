// Empirical probe for page-rotation behaviour: what PDFium reports for
// /Rotate pages, what coordinate space char boxes come back in, per-char
// angles, and whether char boxes (mapped the way the app maps them) cover
// the ink in the rendered pixmap.
using System.Reflection;
using System.Runtime.InteropServices;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

internal static class Probe
{
    private const string Lib = "pdfium";
    private static readonly object s_gate = new();

    [DllImport(Lib)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] private static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport(Lib)] private static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Lib)] private static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] private static extern bool FPDFPage_GetCropBox(IntPtr page,
        ref float left, ref float bottom, ref float right, ref float top);
    [DllImport(Lib)] private static extern bool FPDFPage_GetMediaBox(IntPtr page,
        ref float left, ref float bottom, ref float right, ref float top);
    [DllImport(Lib)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] private static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] private static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] private static extern float FPDFText_GetCharAngle(IntPtr textPage, int index);

    private static void Main(string[] args)
    {
        NativeLibrary.SetDllImportResolver(typeof(Probe).Assembly, ResolvePdfium);
        PdfiumResolver.EnsureLibraryInitialized();

        string fixtures = args.Length > 0 ? args[0] : "tests/fixtures/rotation";
        ProbePdf(Path.Combine(fixtures, "rotate-suite.pdf"), "MARKER");
        ProbePdf(Path.Combine(fixtures, "landscape-scan.pdf"), "SCANMARK");
        ProbePdf(Path.Combine(fixtures, "sideways-table.pdf"), "Quarter");
        ProbeViewRotation(Path.Combine(fixtures, "landscape-scan.pdf"), "SCANMARK");

        // Optional second arg: write a copy of rotate-suite.pdf with a yellow
        // highlight authored over 'MARKER' on every page (each in its own display
        // frame) for independent-viewer fidelity checks (Poppler/MuPDF).
        if (args.Length > 1)
            WriteAnnotatedSuite(Path.Combine(fixtures, "rotate-suite.pdf"), args[1]);
    }

    private static void WriteAnnotatedSuite(string path, string outPath)
    {
        var bytes = File.ReadAllBytes(path);
        var textService = new PdfTextService();
        var file = new AnnotationFile();
        for (int p = 0; p < 4; p++)
        {
            var pageText = textService.ExtractPageText(bytes, p);
            int mi = pageText.Text.IndexOf("MARKER", StringComparison.Ordinal);
            var boxes = pageText.CharBoxes.Where(c => c.Index >= mi && c.Index < mi + 6).ToList();
            float l = boxes.Min(b => b.Left), t = boxes.Min(b => b.Top);
            float r = boxes.Max(b => b.Right), btm = boxes.Max(b => b.Bottom);
            file.Pages[p] =
            [
                new HighlightAnnotation
                {
                    Rects = [new HighlightRect(l, t, r - l, btm - t)],
                    Color = "#FFFF00",
                },
            ];
        }
        File.WriteAllBytes(outPath, new PdfAnnotationWriter().AddAuthoredAnnotations(bytes, file));
        Console.WriteLine($"\nannotated suite written: {outPath}");
    }

    /// <summary>
    /// Phase-1 check: applying a manual view rotation to the sideways-scan fixture
    /// must keep char boxes aligned with the re-rendered pixmap at every quarter-turn,
    /// and at the correct turn the marker's box must become wider than tall (upright text).
    /// </summary>
    private static void ProbeViewRotation(string path, string marker)
    {
        Console.WriteLine($"\n=== view-rotation: {Path.GetFileName(path)} ===");
        var service = new SkiaPdfService(path);
        var textService = new PdfTextService();
        var bytes = File.ReadAllBytes(path);

        for (int q = 0; q < 4; q++)
        {
            var (w, h) = ((IPdfService)service).GetPageSize(0, q);
            var pageText = ((IPdfTextService)textService).ExtractPageText(bytes, 0, q);

            int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
            var boxes = pageText.CharBoxes.Where(c => c.Index >= mi && c.Index < mi + marker.Length).ToList();
            float bw = boxes.Max(b => b.Right) - boxes.Min(b => b.Left);
            float bh = boxes.Max(b => b.Bottom) - boxes.Min(b => b.Top);

            var (rgb, pixW, pixH) = ((IPdfService)service).RenderPagePixmap(0, 800, q);
            double scaleX = pixW / w, scaleY = pixH / h;
            var inBox = new bool[pixW * pixH];
            foreach (var b in pageText.CharBoxes)
            {
                if (b.Right <= b.Left || b.Bottom <= b.Top) continue;
                int x0 = Math.Clamp((int)(b.Left * scaleX) - 1, 0, pixW - 1);
                int x1 = Math.Clamp((int)(b.Right * scaleX) + 1, 0, pixW - 1);
                int y0 = Math.Clamp((int)(b.Top * scaleY) - 1, 0, pixH - 1);
                int y1 = Math.Clamp((int)(b.Bottom * scaleY) + 1, 0, pixH - 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        inBox[y * pixW + x] = true;
            }
            long dark = 0, darkIn = 0;
            for (int i = 0; i < pixW * pixH; i++)
                if (rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2] < 384)
                {
                    dark++;
                    if (inBox[i]) darkIn++;
                }

            Console.WriteLine($"viewRotation={q}: size {w:F0}x{h:F0}, pixmap {pixW}x{pixH}, " +
                $"coverage {(dark == 0 ? 0 : (double)darkIn / dark):F3}, '{marker}' box {bw:F1}x{bh:F1} " +
                (bw > bh ? "(horizontal)" : "(vertical)"));
        }
    }

    private static void ProbePdf(string path, string marker)
    {
        Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
        var service = new SkiaPdfService(path);
        var textService = new PdfTextService();
        var bytes = File.ReadAllBytes(path);

        for (int p = 0; p < service.PageCount; p++)
        {
            Console.WriteLine($"\n-- page {p} --");
            DumpRawPageInfo(bytes, p);

            var (w, h) = service.GetPageSize(p);
            Console.WriteLine($"PDFtoImage GetPageSize: {w:F1} x {h:F1}");

            var pageText = textService.ExtractPageText(bytes, p);
            Console.WriteLine($"chars: {pageText.Text.Length}");

            int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
            if (mi >= 0)
            {
                var boxes = pageText.CharBoxes.Where(c => c.Index >= mi && c.Index < mi + marker.Length).ToList();
                float l = boxes.Min(b => b.Left), t = boxes.Min(b => b.Top);
                float r = boxes.Max(b => b.Right), btm = boxes.Max(b => b.Bottom);
                Console.WriteLine($"'{marker}' charbox union (page-pt, service space): L={l:F1} T={t:F1} R={r:F1} B={btm:F1}");
            }
            else
            {
                Console.WriteLine($"'{marker}' NOT FOUND in extracted text");
            }

            DumpCharAngles(bytes, p, pageText.Text, marker);
            MeasureInkCoverage(service, pageText, p, w, h);
        }
    }

    private static void DumpRawPageInfo(byte[] bytes, int pageIndex)
    {
        lock (s_gate)
        {
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero, page = IntPtr.Zero;
            try
            {
                doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), bytes.Length, null);
                page = FPDF_LoadPage(doc, pageIndex);
                float ml = 0, mb = 0, mr = 0, mt = 0;
                FPDFPage_GetMediaBox(page, ref ml, ref mb, ref mr, ref mt);
                Console.WriteLine($"FPDFPage_GetRotation: {FPDFPage_GetRotation(page)}");
                Console.WriteLine($"FPDF_GetPageWidth/Height: {FPDF_GetPageWidth(page):F1} x {FPDF_GetPageHeight(page):F1}");
                Console.WriteLine($"MediaBox: {ml:F0},{mb:F0} .. {mr:F0},{mt:F0}");
            }
            finally
            {
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                pinned.Free();
            }
        }
    }

    private static void DumpCharAngles(byte[] bytes, int pageIndex, string text, string marker)
    {
        lock (s_gate)
        {
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero, page = IntPtr.Zero, tp = IntPtr.Zero;
            try
            {
                doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), bytes.Length, null);
                page = FPDF_LoadPage(doc, pageIndex);
                tp = FPDFText_LoadPage(page);
                int n = FPDFText_CountChars(tp);

                // Angle histogram over all chars (degrees, rounded to nearest 90)
                var hist = new Dictionary<int, int>();
                for (int i = 0; i < n; i++)
                {
                    float rad = FPDFText_GetCharAngle(tp, i);
                    int deg = ((int)Math.Round(rad * 180.0 / Math.PI / 90.0) * 90 % 360 + 360) % 360;
                    hist[deg] = hist.GetValueOrDefault(deg) + 1;
                }
                Console.WriteLine("char angle histogram (deg): " +
                    string.Join(", ", hist.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}°×{kv.Value}")));

                int mi = text.IndexOf(marker, StringComparison.Ordinal);
                if (mi >= 0 && mi < n)
                    Console.WriteLine($"'{marker}' first-char angle: {FPDFText_GetCharAngle(tp, mi) * 180.0 / Math.PI:F1}°");
            }
            finally
            {
                if (tp != IntPtr.Zero) FPDFText_ClosePage(tp);
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                pinned.Free();
            }
        }
    }

    /// <summary>
    /// Renders the page pixmap and measures what fraction of dark (ink) pixels
    /// fall inside the char boxes when mapped exactly the way the app maps them
    /// (uniform scale pixmapWidth / GetPageSize width). Near 1.0 = aligned;
    /// low = the char boxes and the pixmap are in different frames.
    /// </summary>
    private static void MeasureInkCoverage(SkiaPdfService service, PageText pageText,
        int pageIndex, double pageW, double pageH)
    {
        var (rgb, pixW, pixH) = service.RenderPagePixmap(pageIndex, 800);
        double scaleX = pixW / pageW, scaleY = pixH / pageH;

        var inBox = new bool[pixW * pixH];
        foreach (var b in pageText.CharBoxes)
        {
            if (b.Right <= b.Left || b.Bottom <= b.Top) continue;
            int x0 = Math.Clamp((int)(b.Left * scaleX) - 1, 0, pixW - 1);
            int x1 = Math.Clamp((int)(b.Right * scaleX) + 1, 0, pixW - 1);
            int y0 = Math.Clamp((int)(b.Top * scaleY) - 1, 0, pixH - 1);
            int y1 = Math.Clamp((int)(b.Bottom * scaleY) + 1, 0, pixH - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    inBox[y * pixW + x] = true;
        }

        long dark = 0, darkInBox = 0;
        for (int i = 0; i < pixW * pixH; i++)
        {
            int sum = rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2];
            if (sum < 384)
            {
                dark++;
                if (inBox[i]) darkInBox++;
            }
        }
        double recall = dark == 0 ? 0 : (double)darkInBox / dark;
        Console.WriteLine($"pixmap {pixW}x{pixH}; dark px: {dark}; ink coverage by char boxes: {recall:F3}");
    }

    private static IntPtr ResolvePdfium(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "pdfium") return IntPtr.Zero;
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle)) return handle;
        string lib = Path.Combine(AppContext.BaseDirectory,
            "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "libpdfium.so");
        return File.Exists(lib) && NativeLibrary.TryLoad(lib, out handle) ? handle : IntPtr.Zero;
    }
}
