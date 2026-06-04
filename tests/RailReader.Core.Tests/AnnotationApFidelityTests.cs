using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace RailReader.Core.Tests;

/// <summary>
/// Appearance (/AP) fidelity: annotations RailReader writes via PDFium carry no /AP
/// stream, so external viewers must synthesise an appearance from the annotation's
/// properties. These tests render the written PDF with engines <b>independent of
/// PDFium</b> — Poppler (pdftoppm) and MuPDF (mutool draw) — and assert the annotation
/// visibly renders in the right place. If neither tool is installed the test soft-skips.
/// </summary>
public class AnnotationApFidelityTests(ITestOutputHelper output)
{
    private enum Engine { Poppler, MuPDF }

    // A blank region of page 1 used as a "should stay white" control.
    private static readonly (int X0, int Y0, int X1, int Y1) Control = (72, 550, 272, 580);

    [Fact]
    public void Highlight_RendersInIndependentEngines()
        => AssertRenders(
            new HighlightAnnotation { Rects = [new HighlightRect(72, 250, 200, 16)], Color = "#FFEE00" },
            bbox: (72, 250, 272, 266), minNonWhite: 1000);

    [Fact]
    public void Underline_RendersInIndependentEngines()
        => AssertRenders(
            new UnderlineAnnotation { Rects = [new HighlightRect(72, 300, 200, 12)], Color = "#E00000" },
            bbox: (72, 300, 272, 312), minNonWhite: 25);

    [Fact]
    public void StrikeOut_RendersInIndependentEngines()
        => AssertRenders(
            new StrikeOutAnnotation { Rects = [new HighlightRect(72, 330, 200, 12)], Color = "#E00000" },
            bbox: (72, 330, 272, 342), minNonWhite: 25);

    [Fact]
    public void Squiggly_RendersInIndependentEngines()
        => AssertRenders(
            new SquigglyAnnotation { Rects = [new HighlightRect(72, 360, 200, 12)], Color = "#E00000" },
            bbox: (72, 360, 272, 372), minNonWhite: 25);

    [Fact]
    public void Ink_RendersInIndependentEngines()
        => AssertRenders(
            new FreehandAnnotation
            {
                Points = [new PointF(72, 400), new PointF(160, 412), new PointF(272, 418)],
                Color = "#E00000", StrokeWidth = 2,
            },
            bbox: (70, 396, 274, 422), minNonWhite: 25);

    [Fact]
    public void Square_RendersInIndependentEngines()
        => AssertRenders(
            new RectAnnotation { X = 72, Y = 440, W = 150, H = 30, Color = "#E00000", StrokeWidth = 2 },
            bbox: (70, 438, 224, 472), minNonWhite: 25);

    private void AssertRenders(Annotation ann, (int X0, int Y0, int X1, int Y1) bbox, int minNonWhite)
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"rr-ap-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(pdfPath, File.ReadAllBytes(TestFixtures.GetTestPdfPath()));

        var file = new AnnotationFile();
        file.Pages[0] = [ann];
        Assert.True(new PdfAnnotationStore().Save(pdfPath, file));

        try
        {
            int enginesChecked = 0;
            foreach (var engine in Enum.GetValues<Engine>())
            {
                var ppm = pdfPath + $".{engine}.ppm";
                if (!TryRender(engine, pdfPath, page: 0, ppm)) continue; // tool not installed
                enginesChecked++;
                try
                {
                    var (w, h, rgb) = ParsePpm(ppm);
                    int inAnnot = CountNonWhite(w, h, rgb, bbox);
                    int inControl = CountNonWhite(w, h, rgb, Control);
                    output.WriteLine($"{engine}: annot non-white={inAnnot}, control non-white={inControl}");

                    Assert.True(inAnnot >= minNonWhite,
                        $"{engine}: expected the annotation to render (non-white px={inAnnot}, need >= {minNonWhite})");
                    Assert.True(inControl < 20,
                        $"{engine}: control region should be blank (non-white px={inControl})");
                }
                finally
                {
                    File.Delete(ppm);
                }
            }

            if (enginesChecked == 0)
                output.WriteLine("No independent PDF renderer (pdftoppm/mutool) installed — skipped.");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    // --- external rendering (independent of PDFium) ---

    private static bool TryRender(Engine engine, string pdfPath, int page, string outPpm)
    {
        try
        {
            if (engine == Engine.Poppler)
            {
                var prefix = outPpm + ".pp";
                Run("pdftoppm", $"-r 72 -f {page + 1} -l {page + 1} \"{pdfPath}\" \"{prefix}\"");
                var produced = Directory.GetFiles(
                    Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + "-*.ppm");
                if (produced.Length == 0) return false;
                File.Move(produced[0], outPpm, overwrite: true);
            }
            else
            {
                Run("mutool", $"draw -r 72 -o \"{outPpm}\" \"{pdfPath}\" {page + 1}");
            }
            return File.Exists(outPpm) && new FileInfo(outPpm).Length > 0;
        }
        catch (Win32Exception)
        {
            return false; // executable not found → treat as not installed
        }
    }

    private static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!; // throws Win32Exception when exe is missing
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
    }

    // --- P6 PPM parsing (no image library needed) ---

    private static (int W, int H, byte[] Rgb) ParsePpm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 0;

        string Token()
        {
            while (pos < bytes.Length)
            {
                if (bytes[pos] == (byte)'#') { while (pos < bytes.Length && bytes[pos] != (byte)'\n') pos++; }
                else if (char.IsWhiteSpace((char)bytes[pos])) pos++;
                else break;
            }
            int start = pos;
            while (pos < bytes.Length && !char.IsWhiteSpace((char)bytes[pos])) pos++;
            return Encoding.ASCII.GetString(bytes, start, pos - start);
        }

        var magic = Token();
        if (magic != "P6") throw new InvalidDataException($"Not a P6 PPM: '{magic}'");
        int w = int.Parse(Token()), h = int.Parse(Token());
        _ = Token(); // maxval
        pos++;       // single whitespace separator before binary data

        var rgb = new byte[w * h * 3];
        Array.Copy(bytes, pos, rgb, 0, Math.Min(rgb.Length, bytes.Length - pos));
        return (w, h, rgb);
    }

    private static int CountNonWhite(int w, int h, byte[] rgb, (int X0, int Y0, int X1, int Y1) r)
    {
        int x0 = Math.Max(0, r.X0), y0 = Math.Max(0, r.Y0);
        int x1 = Math.Min(w, r.X1), y1 = Math.Min(h, r.Y1);
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 3;
                if (!(rgb[i] > 245 && rgb[i + 1] > 245 && rgb[i + 2] > 245)) n++;
            }
        }
        return n;
    }
}
