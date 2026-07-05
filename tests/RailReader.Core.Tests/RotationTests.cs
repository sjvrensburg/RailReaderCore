using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Page-/Rotate correctness tests against the LaTeX-generated fixture
/// (tests/fixtures/rotation/rotate-suite.pdf): four pages with a
/// byte-identical portrait content stream and /Rotate 0/90/180/270.
/// The rendered pixmap honours /Rotate (PDFtoImage), so all extracted
/// geometry must come back in the displayed (rotated) frame.
/// </summary>
public class RotationTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "rotation", name);

    private static byte[] SuiteBytes() => File.ReadAllBytes(FixturePath("rotate-suite.pdf"));

    /// <summary>
    /// Fraction of dark (ink) pixels of the rendered pixmap that fall inside
    /// the char boxes when mapped with the app's own convention
    /// (uniform scale pixmap/GetPageSize). ~1.0 = geometry aligned with pixels.
    /// </summary>
    private static double InkCoverage(SkiaPdfService service, PageText pageText, int pageIndex)
    {
        var (pageW, pageH) = service.GetPageSize(pageIndex);
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
            if (rgb[i * 3] + rgb[i * 3 + 1] + rgb[i * 3 + 2] < 384)
            {
                dark++;
                if (inBox[i]) darkInBox++;
            }
        }
        return dark == 0 ? 0 : (double)darkInBox / dark;
    }

    private static (float L, float T, float R, float B) MarkerUnion(PageText pageText, string marker)
    {
        int mi = pageText.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(mi >= 0, $"'{marker}' not found in extracted text");
        var boxes = pageText.CharBoxes
            .Where(c => c.Index >= mi && c.Index < mi + marker.Length).ToList();
        return (boxes.Min(b => b.Left), boxes.Min(b => b.Top),
                boxes.Max(b => b.Right), boxes.Max(b => b.Bottom));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CharBoxes_cover_rendered_ink_on_all_rotations(int pageIndex)
    {
        var service = new SkiaPdfService(FixturePath("rotate-suite.pdf"));
        var pageText = new PdfTextService().ExtractPageText(service.PdfBytes, pageIndex);

        Assert.Contains("MARKER", pageText.Text);
        // Empirically 1.000 on all four rotations; anything below ~0.95 means the
        // char boxes and the rendered pixmap have drifted into different frames.
        Assert.True(InkCoverage(service, pageText, pageIndex) >= 0.95,
            $"char boxes no longer cover the rendered ink on page {pageIndex}");
    }

    [Fact]
    public void CharBoxes_stay_within_displayed_page_bounds()
    {
        var service = new SkiaPdfService(FixturePath("rotate-suite.pdf"));
        var textService = new PdfTextService();

        for (int p = 0; p < 4; p++)
        {
            var (w, h) = service.GetPageSize(p);
            var pageText = textService.ExtractPageText(service.PdfBytes, p);
            foreach (var b in pageText.CharBoxes)
            {
                Assert.InRange(b.Left, -1, w + 1);
                Assert.InRange(b.Right, -1, w + 1);
                Assert.InRange(b.Top, -1, h + 1);
                Assert.InRange(b.Bottom, -1, h + 1);
            }
        }
    }

    [Fact]
    public void GetTextRangeRects_returns_displayed_frame_rects_on_rotated_page()
    {
        var bytes = SuiteBytes();
        var textService = new PdfTextService();
        var pageText = textService.ExtractPageText(bytes, 1); // /Rotate 90

        int mi = pageText.Text.IndexOf("MARKER", StringComparison.Ordinal);
        var rects = textService.GetTextRangeRects(bytes, 1, [(mi, "MARKER".Length)]);

        Assert.Single(rects);
        Assert.NotEmpty(rects[0]);
        var union = MarkerUnion(pageText, "MARKER");
        foreach (var r in rects[0])
        {
            // Range rects must land on the same displayed-frame region as the
            // char boxes (loose tolerance: FPDFText_GetRect merges glyph runs).
            Assert.InRange(r.Left, union.L - 3, union.R + 3);
            Assert.InRange(r.Right, union.L - 3, union.R + 3);
            Assert.InRange(r.Top, union.T - 3, union.B + 3);
            Assert.InRange(r.Bottom, union.T - 3, union.B + 3);
        }
    }

    /// <summary>
    /// The four fixture pages share a byte-identical content stream, so a
    /// highlight authored over 'MARKER' in each page's own displayed frame
    /// must serialise to the <b>same PDF-user-space</b> quad on every page.
    /// This cross-page invariant is independent of the transform maths under
    /// test (an identity transform would fail it on 90/180/270).
    /// </summary>
    [Fact]
    public void Authored_highlight_lands_on_identical_pdf_space_quads_across_rotations()
    {
        var bytes = SuiteBytes();
        var textService = new PdfTextService();

        var file = new AnnotationFile();
        for (int p = 0; p < 4; p++)
        {
            var pageText = textService.ExtractPageText(bytes, p);
            var (l, t, r, b) = MarkerUnion(pageText, "MARKER");
            file.Pages[p] =
            [
                new HighlightAnnotation { Rects = [new HighlightRect(l, t, r - l, b - t)] },
            ];
        }

        var written = new PdfAnnotationWriter().AddAuthoredAnnotations(bytes, file);
        var quads = ReadRawHighlightQuads(written);

        Assert.Equal(4, quads.Count);
        var (x1, y1, x2, y2) = quads[0];
        for (int p = 1; p < 4; p++)
        {
            Assert.Equal(x1, quads[p].MinX, 1.0);
            Assert.Equal(y1, quads[p].MinY, 1.0);
            Assert.Equal(x2, quads[p].MaxX, 1.0);
            Assert.Equal(y2, quads[p].MaxY, 1.0);
        }
    }

    /// <summary>Reads the raw (PDF-user-space) quad bounds of the first highlight on each page.</summary>
    private static List<(float MinX, float MinY, float MaxX, float MaxY)> ReadRawHighlightQuads(byte[] pdfBytes)
    {
        var result = new List<(float, float, float, float)>();
        lock (PdfiumGate.Lock)
        {
            PdfiumResolver.EnsureLibraryInitialized();
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
                pdfBytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero;
            try
            {
                doc = PdfiumNative.FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
                Assert.NotEqual(IntPtr.Zero, doc);
                int pageCount = PdfiumNative.FPDF_GetPageCount(doc);
                for (int p = 0; p < pageCount; p++)
                {
                    IntPtr page = PdfiumNative.FPDF_LoadPage(doc, p);
                    try
                    {
                        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                            try
                            {
                                if (PdfiumNative.FPDFAnnot_GetSubtype(annot) != PdfiumNative.FPDF_ANNOT_HIGHLIGHT)
                                    continue;
                                Assert.True(PdfiumNative.FPDFAnnot_GetAttachmentPoints(annot, 0, out var q));
                                result.Add((
                                    Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4)),
                                    Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4)),
                                    Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4)),
                                    Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4))));
                                break;
                            }
                            finally
                            {
                                PdfiumNative.FPDFPage_CloseAnnot(annot);
                            }
                        }
                    }
                    finally
                    {
                        PdfiumNative.FPDF_ClosePage(page);
                    }
                }
            }
            finally
            {
                if (doc != IntPtr.Zero) PdfiumNative.FPDF_CloseDocument(doc);
                pinned.Free();
            }
        }
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Annotations_roundtrip_on_rotated_pages(int pageIndex)
    {
        var bytes = SuiteBytes();
        var pageText = new PdfTextService().ExtractPageText(bytes, pageIndex);
        var (l, t, r, b) = MarkerUnion(pageText, "MARKER");

        var file = new AnnotationFile();
        file.Pages[pageIndex] =
        [
            new HighlightAnnotation { Rects = [new HighlightRect(l, t, r - l, b - t)] },
            new RectAnnotation { X = l, Y = t, W = r - l, H = b - t },
            new TextNoteAnnotation { X = l, Y = t, Text = "note" },
        ];

        var written = new PdfAnnotationWriter().AddAuthoredAnnotations(bytes, file);
        var readBack = new PdfAnnotationReader().Read(written);

        Assert.True(readBack.Pages.ContainsKey(pageIndex));
        var anns = readBack.Pages[pageIndex];

        var hl = Assert.Single(anns.OfType<HighlightAnnotation>());
        var rect = Assert.Single(hl.Rects);
        Assert.Equal(l, rect.X, 0.5);
        Assert.Equal(t, rect.Y, 0.5);
        Assert.Equal(r - l, rect.W, 0.5);
        Assert.Equal(b - t, rect.H, 0.5);

        var ra = Assert.Single(anns.OfType<RectAnnotation>());
        Assert.Equal(l, ra.X, 0.5);
        Assert.Equal(t, ra.Y, 0.5);

        var tn = Assert.Single(anns.OfType<TextNoteAnnotation>());
        Assert.Equal(l, tn.X, 0.5);
        Assert.Equal(t, tn.Y, 0.5);
    }

    [Fact]
    public void Link_destination_resolves_page_point_position_on_rotated_target_page()
    {
        var bytes = SuiteBytes();
        var links = new PdfLinkService().ExtractPageLinks(bytes, 0);

        var toRotated = links.Select(x => x.Destination).OfType<PageDestination>()
            .Where(d => d.PageIndex == 1).ToList();
        Assert.NotEmpty(toRotated);

        var dest = toRotated[0];
        // Target page is /Rotate 90 → displayed size 792×612. The hypertarget
        // sits at the top-left of the unrotated content, which displays in the
        // right-hand region of the rotated page.
        Assert.NotNull(dest.PageX);
        Assert.InRange(dest.PageX!.Value, 0, 792);
        Assert.True(dest.PageX!.Value > 500,
            $"expected destination in the right-hand display region, got X={dest.PageX}");
        if (dest.PageY is { } py) Assert.InRange(py, 0, 612);
    }
}

/// <summary>Pure unit tests for the PDF↔page-point transform maths.</summary>
public class PageTransformTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PageToPdf_inverts_PdfToPage(int rotation)
    {
        var tx = new PageTransform(10f, 20f, 612f, 792f, rotation);
        foreach (var (px, py) in new[] { (10.0, 20.0), (100.0, 700.0), (622.0, 812.0), (300.0, 400.0) })
        {
            var (x, y) = tx.PdfToPage(px, py);
            var (bx, by) = tx.PageToPdf(x, y);
            Assert.Equal(px, bx, 3);
            Assert.Equal(py, by, 3);
        }
    }

    [Theory]
    [InlineData(0, 612, 792)]
    [InlineData(1, 792, 612)]
    [InlineData(2, 612, 792)]
    [InlineData(3, 792, 612)]
    public void Display_dimensions_swap_on_odd_rotations(int rotation, float expectW, float expectH)
    {
        var tx = new PageTransform(0f, 0f, 612f, 792f, rotation);
        Assert.Equal(expectW, tx.DisplayWidth);
        Assert.Equal(expectH, tx.DisplayHeight);
    }

    [Fact]
    public void Corner_mapping_matches_empirical_probe_results()
    {
        // From tools/rotation-probe on the fixture: the MARKER box at
        // PDF space x∈[72.5,125.7], y∈[707.8,716.6] displays at:
        //   rot 0: L=72.5  T=75.4  (top-left)
        //   rot 1: L=707.8 T=72.5  (top-right)
        //   rot 2: L=486.3 T=707.8 (bottom-right)
        //   rot 3: L=75.4  T=486.3 (bottom-left)
        var expectations = new (int Rot, float L, float T)[]
        {
            (0, 72.5f, 75.4f), (1, 707.8f, 72.5f), (2, 486.3f, 707.8f), (3, 75.4f, 486.3f),
        };
        foreach (var (rot, expL, expT) in expectations)
        {
            var tx = new PageTransform(0f, 0f, 612f, 792f, rot);
            var (l, t, _, _) = tx.PdfRectToPage(72.5, 792 - 84.2, 125.7, 792 - 75.4);
            Assert.Equal(expL, l, 0.5);
            Assert.Equal(expT, t, 0.5);
        }
    }

    [Fact]
    public void PdfRectToPage_normalises_corners_on_all_rotations()
    {
        for (int rot = 0; rot < 4; rot++)
        {
            var tx = new PageTransform(0f, 0f, 612f, 792f, rot);
            var (l, t, r, b) = tx.PdfRectToPage(100, 200, 300, 500);
            Assert.True(l <= r);
            Assert.True(t <= b);
            var (pl, pb2, pr, pt2) = tx.PageRectToPdf(l, t, r - l, b - t);
            Assert.Equal(100, pl, 3);
            Assert.Equal(200, pb2, 3);
            Assert.Equal(300, pr, 3);
            Assert.Equal(500, pt2, 3);
        }
    }
}
