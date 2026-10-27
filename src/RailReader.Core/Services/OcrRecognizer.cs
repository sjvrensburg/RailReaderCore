using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Whole-page / whole-document OCR entry points shared by hosts (issue #155):
/// render a page through <see cref="IPdfService"/>, recognise it with an
/// <see cref="IOcrService"/>, and map the result into displayed page points —
/// the exact input <c>PdfTextLayerWriter</c> (in <c>RailReader.Core.Pdfium</c>)
/// consumes, and the same mapping the analysis worker applies internally.
/// Keeping this in Core means a CLI batch job and the desktop's background job
/// cannot drift apart.
///
/// <para>The supplied <see cref="IOcrService"/> is used exactly as given and
/// implementations need not be thread-safe: concurrent
/// <see cref="RecognizePage"/> calls must not share one instance
/// (<see cref="RecognizeDocument"/> is sequential).</para>
/// </summary>
public static class OcrRecognizer
{
    /// <summary>Default rasterisation resolution for <see cref="RecognizePage"/>.</summary>
    public const int DefaultDpi = 300;

    /// <summary>
    /// Recognises one page and returns its lines in page-point space
    /// (top-left origin, Y-down, displayed orientation): line and character
    /// boxes are scaled from the pixmap, line heights are deflated to the
    /// detector's true height, and the skew estimate is carried across.
    /// Returns <see cref="OcrPage.Empty"/> for a blank/unreadable page.
    /// </summary>
    /// <param name="dpi">
    /// Rasterisation resolution. Higher helps small print; the pixmap's longest
    /// edge is <c>max(pageWidth, pageHeight) / 72 × dpi</c> pixels.
    /// </param>
    public static OcrPage RecognizePage(IPdfService pdf, IOcrService ocr, int page,
        OcrMode mode = OcrMode.Full, int dpi = DefaultDpi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(ocr);
        if (dpi <= 0) dpi = DefaultDpi;
        if (mode == OcrMode.Off || page < 0 || page >= pdf.PageCount) return OcrPage.Empty;

        var (pageW, pageH) = pdf.GetPageSize(page);
        if (pageW <= 0 || pageH <= 0) return OcrPage.Empty;
        int targetSize = (int)Math.Clamp(Math.Round(Math.Max(pageW, pageH) / 72.0 * dpi), 1, 8192);

        var (rgb, pxW, pxH) = pdf.RenderPagePixmap(page, targetSize);
        if (pxW <= 0 || pxH <= 0 || rgb.Length == 0) return OcrPage.Empty;

        ct.ThrowIfCancellationRequested();
        var raw = ocr.Recognize(rgb, pxW, pxH, mode, ct);
        return ToPagePoints(raw, (float)(pageW / pxW), (float)(pageH / pxH));
    }

    /// <summary>
    /// Recognises every page of the document, reporting progress as
    /// <c>(completed, total)</c> and honouring cancellation between pages.
    /// Pages with no recognised lines are omitted from the result. In
    /// <see cref="OcrMode.Full"/> mode the returned dictionary can be handed
    /// straight to the text-layer writer (it skips native-text pages itself);
    /// <see cref="OcrMode.Lines"/> pages carry geometry only and are included
    /// for line-detection consumers — the writer has no text to write for them.
    /// </summary>
    public static Dictionary<int, OcrPage> RecognizeDocument(IPdfService pdf, IOcrService ocr,
        OcrMode mode = OcrMode.Full, int dpi = DefaultDpi,
        IProgress<(int Page, int Total)>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(ocr);
        var result = new Dictionary<int, OcrPage>();
        int total = pdf.PageCount;
        for (int page = 0; page < total; page++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((page, total));
            var ocrPage = RecognizePage(pdf, ocr, page, mode, dpi, ct);
            if (ocrPage.Lines.Count > 0) result[page] = ocrPage;
        }
        progress?.Report((total, total));
        return result;
    }

    /// <summary>Scales a pixmap-space OCR page into page points (see <see cref="OcrPageMapper"/> for the geometry rationale).</summary>
    internal static OcrPage ToPagePoints(OcrPage page, float scaleX, float scaleY)
    {
        float skew = page.SkewAngle == 0f || scaleX <= 0f || scaleY <= 0f
            ? 0f
            : MathF.Atan(MathF.Tan(page.SkewAngle) * (scaleY / scaleX));

        var lines = new List<OcrLine>(page.Lines.Count);
        foreach (var line in page.Lines)
        {
            var b = line.Box;
            float centreY = (b.Y + b.H / 2f) * scaleY;
            float h = line.EffectiveHeight * scaleY;
            var box = new BBox(b.X * scaleX, centreY - h / 2f, b.W * scaleX, h);

            List<CharBox>? chars = null;
            if (line.Chars is not null)
            {
                chars = new List<CharBox>(line.Chars.Count);
                foreach (var c in line.Chars)
                    chars.Add(new CharBox(c.Index, c.Left * scaleX, c.Top * scaleY,
                        c.Right * scaleX, c.Bottom * scaleY, c.Angle));
            }

            lines.Add(new OcrLine(box, line.Text, chars, line.Confidence, line.Angle,
                line.TrueHeight > 0f ? line.TrueHeight * scaleY : 0f));
        }
        return new OcrPage(lines, skew);
    }
}
