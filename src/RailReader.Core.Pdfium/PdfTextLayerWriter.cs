using System.Runtime.InteropServices;
using System.Text;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>Options for <see cref="PdfTextLayerWriter"/>.</summary>
public sealed record TextLayerOptions
{
    /// <summary>
    /// Skip pages that already carry native (non-OCR) text. Default true: OCR text
    /// must never shadow a real text layer.
    /// </summary>
    public bool SkipPagesWithNativeText { get; init; } = true;

    /// <summary>
    /// When a page already carries a layer written by this writer, remove it and
    /// write the new one instead of stacking a second layer. Default true.
    /// </summary>
    public bool ReplaceExistingOcrLayer { get; init; } = true;

    /// <summary>
    /// Fit the layer to the line's char boxes (<see cref="OcrLine.Chars"/>): each word
    /// spans its own glyphs, and the line's glyph band is the ink the boxes cover.
    /// Words whose characters are not all boxed are placed between their fitted
    /// neighbours. When false — or when a line has no char boxes — words are
    /// apportioned along the line box by character count. Default true.
    ///
    /// <para>Either way the layer is one text object per word on a baseline shared by
    /// the whole line. Per-glyph objects seated on their own ink boxes read back
    /// letter-spaced (<c>H e l l o</c>) and broken at every apostrophe and hyphen in
    /// PDFium, Poppler and MuPDF alike, which defeats search and copy.</para>
    /// </summary>
    public bool UseCharBoxes { get; init; } = true;
}

/// <summary>Outcome of a <see cref="PdfTextLayerWriter"/> run.</summary>
/// <param name="PagesWritten">Pages that received a (new) OCR text layer.</param>
/// <param name="PagesSkipped">
/// Supplied entries that received no layer and were left untouched: pages with
/// native text, an existing OCR layer with replacement disabled, no writable
/// OCR content, or a page index outside the document.
/// <c>PagesWritten + PagesSkipped</c> always equals the supplied entry count.
/// </param>
/// <param name="SourceWasSigned">
/// The source carried at least one digital signature. Saving a copy invalidates
/// signatures, so the host should tell the user; writing still succeeds.
/// </param>
public sealed record TextLayerWriteResult(int PagesWritten, int PagesSkipped, bool SourceWasSigned)
{
    public static readonly TextLayerWriteResult Empty = new(0, 0, false);
}

/// <summary>
/// Writes OCR results into a copy of a PDF as an invisible text layer, the way
/// <c>ocrmypdf</c> does (issue #155): every recognised word becomes a text
/// object in render mode 3 (invisible) on its line's shared baseline, positioned
/// and horizontally scaled over the printed glyphs, so the copy becomes
/// searchable, selectable, copyable and highlightable in any reader.
///
/// <para><b>Copy, never in place.</b> The document is loaded from the source
/// bytes, modified in memory and saved to <c>outputPath</c> via
/// <c>FPDF_SaveAsCopy</c>; annotations, AcroForm and outline are preserved and
/// an encrypted source stays encrypted (same contract as
/// <see cref="PdfAnnotationWriter"/>).</para>
///
/// <para><b>Unicode.</b> A synthesised glyphless CID font
/// (<see cref="GlyphlessFont"/>) with densely assigned CIDs and a <c>/ToUnicode</c> CMap carries the
/// text, so non-Latin scripts round-trip; the standard-14 fallback (Helvetica)
/// is used only if the CID font fails to load, and is WinAnsi-limited.</para>
///
/// <para><b>Provenance.</b> Every written object is tagged with the marked-content
/// name <see cref="OcrMarkName"/> (and uses the recognisable glyphless font), so
/// both text backends can flag the read-back text as OCR-derived via
/// <see cref="PageText.FromOcr"/> instead of the host guessing — by the one rule
/// in <see cref="OcrProvenance"/>.</para>
///
/// <para>All PDFium work runs inside <see cref="PdfiumGate.Lock"/>; the writer
/// holds no state and is safe to call from a background thread.</para>
/// </summary>
public static class PdfTextLayerWriter
{
    /// <summary>Marked-content name stamped on every OCR text object.</summary>
    public const string OcrMarkName = OcrProvenance.MarkName;

    /// <summary>
    /// Writes <paramref name="pages"/> (keyed by zero-based page index, geometry
    /// in displayed page points — top-left origin, Y-down) into a copy of the
    /// source document at <paramref name="outputPath"/>.
    /// </summary>
    public static TextLayerWriteResult Write(IPdfService source,
        IReadOnlyDictionary<int, OcrPage> pages, string outputPath,
        TextLayerOptions? options = null, IProgress<(int Page, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Write(source.PdfBytes, pages, outputPath, source.Password, options, progress, ct);
    }

    /// <summary>Byte-source overload of <see cref="Write(IPdfService, IReadOnlyDictionary{int, OcrPage}, string, TextLayerOptions, IProgress{ValueTuple{int, int}}, CancellationToken)"/>.</summary>
    public static TextLayerWriteResult Write(byte[] pdfBytes,
        IReadOnlyDictionary<int, OcrPage> pages, string outputPath,
        string? password = null, TextLayerOptions? options = null,
        IProgress<(int Page, int Total)>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var (bytes, result) = WriteToBytesCore(pdfBytes, pages, password, options, progress, ct);
        File.WriteAllBytes(outputPath, bytes);
        return result;
    }

    /// <summary>
    /// Same write as <see cref="Write(IPdfService, IReadOnlyDictionary{int, OcrPage}, string, TextLayerOptions, IProgress{ValueTuple{int, int}}, CancellationToken)"/>
    /// but returns the saved document bytes instead of writing a file.
    /// </summary>
    public static (byte[] Bytes, TextLayerWriteResult Result) WriteToBytes(byte[] pdfBytes,
        IReadOnlyDictionary<int, OcrPage> pages, string? password = null,
        TextLayerOptions? options = null, IProgress<(int Page, int Total)>? progress = null,
        CancellationToken ct = default)
        => WriteToBytesCore(pdfBytes, pages, password, options, progress, ct);

    private static (byte[], TextLayerWriteResult) WriteToBytesCore(byte[] pdfBytes,
        IReadOnlyDictionary<int, OcrPage> pages, string? password, TextLayerOptions? options,
        IProgress<(int Page, int Total)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(pages);
        options ??= new TextLayerOptions();

        // The gate is taken per native phase, never across a host callback:
        // a synchronous IProgress.Report that blocks on a thread waiting for
        // the gate would otherwise deadlock the process.
        PdfiumResolver.EnsureLibraryInitialized();
        var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        IntPtr doc = IntPtr.Zero;
        FontHandle? font = null;
        try
        {
            bool signed;
            int pageCount;
            lock (PdfiumGate.Lock)
            {
                doc = LoadDocumentChecked(pinned.AddrOfPinnedObject(), pdfBytes.Length, password);
                if (doc == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to load PDF via PDFium");
                signed = FPDF_GetSignatureCount(doc) > 0;
                pageCount = FPDF_GetPageCount(doc);
            }

            var ordered = pages.Keys.Where(p => p >= 0 && p < pageCount).OrderBy(p => p).ToList();
            // Out-of-range keys are counted as skipped, never silently dropped.
            int total = pages.Count;
            int written = 0, skipped = total - ordered.Count, done = skipped;
            // The font is loaded lazily on the first page that actually
            // writes: an all-skipped or empty write must not embed it.
            // GetFont is only invoked from WritePage, inside the gate.
            FontHandle GetFont() => font ??= new FontHandle(doc, pages.Values);

            foreach (int pageIndex in ordered)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((done, total));
                lock (PdfiumGate.Lock)
                {
                    var page = FPDF_LoadPage(doc, pageIndex);
                    if (page == IntPtr.Zero) { skipped++; }
                    else
                    {
                        try
                        {
                            if (WritePage(doc, page, pages[pageIndex], GetFont, options))
                                written++;
                            else
                                skipped++;
                        }
                        finally { FPDF_ClosePage(page); }
                    }
                }
                done++;
            }
            progress?.Report((total, total));

            byte[] bytes;
            lock (PdfiumGate.Lock)
            {
                font?.Dispose();
                font = null;
                bytes = PdfAnnotationWriter.SaveDocumentBytes(doc);
            }
            return (bytes, new TextLayerWriteResult(written, skipped, signed));
        }
        finally
        {
            lock (PdfiumGate.Lock)
            {
                font?.Dispose();
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            }
            pinned.Free();
        }
    }

    /// <summary>Returns true when the page received a layer, false when skipped.
    /// Every skip decision is made before anything is removed, so a skipped page
    /// is left genuinely untouched — including any OCR layer it already carries.</summary>
    private static bool WritePage(IntPtr doc, IntPtr page, OcrPage ocr,
        Func<FontHandle> getFont, TextLayerOptions options)
    {
        // Materialise the replacement first: an empty or unwritable input must
        // never destroy the layer already on the page.
        var units = new List<(TextUnit Unit, float Angle)>();
        foreach (var line in ocr.Lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            float angle = line.Angle != 0f ? line.Angle : ocr.SkewAngle;
            foreach (var unit in UnitsOf(line, angle, options.UseCharBoxes))
                units.Add((unit, angle));
        }
        if (units.Count == 0) return false;

        bool hadOcr = PageHasOcrLayer(page);
        if (hadOcr && !options.ReplaceExistingOcrLayer) return false;
        if (options.SkipPagesWithNativeText && HasNativeText(page, hadOcr)) return false;

        if (hadOcr) RemoveOcrObjects(page);

        var font = getFont();
        var tx = GetPageTransform(page);
        bool any = false;
        foreach (var (unit, angle) in units)
        {
            if (WriteUnit(doc, page, unit, angle, tx, font)) any = true;
        }
        // Without a successful regeneration the changes are lost on save, so a
        // failed one is not a write. (After a removal, skipping regeneration
        // here also keeps the old layer's stream intact.)
        if (!any || !FPDFPage_GenerateContent(page)) return false;
        return true;
    }

    /// <summary>
    /// Native-text detection that does not require removing anything first:
    /// with no OCR layer the char count decides; with one, a text object that
    /// is not ours is native. Form XObjects are descended into — native text
    /// drawn through a form would otherwise be shadowed by a new layer even
    /// though <see cref="TextLayerOptions.SkipPagesWithNativeText"/> is on.
    /// </summary>
    private static bool HasNativeText(IntPtr page, bool hadOcr)
    {
        if (!hadOcr) return CountChars(page) > 0;
        return AnyNativeTextObject(page, isForm: false, depth: 0);
    }

    private static bool AnyNativeTextObject(IntPtr container, bool isForm, int depth)
    {
        if (depth > 10) return false;
        int count = isForm ? FPDFFormObj_CountObjects(container) : FPDFPage_CountObjects(container);
        for (int i = 0; i < count; i++)
        {
            var obj = isForm ? FPDFFormObj_GetObject(container, (uint)i) : FPDFPage_GetObject(container, i);
            if (obj == IntPtr.Zero) continue;
            int type = FPDFPageObj_GetType(obj);
            if (type == FPDF_PAGEOBJ_TEXT && !IsOcrTextObject(obj)) return true;
            if (type == FPDF_PAGEOBJ_FORM && AnyNativeTextObject(obj, isForm: true, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>A text unit already resolved to displayed-frame geometry:
    /// baseline origin, along-baseline length, and glyph height (all points).</summary>
    private readonly record struct TextUnit(string Text, float OriginX, float OriginY, float Along, float Height);

    /// <summary>
    /// Resolves a line into one unit per word, all on the line's shared baseline.
    ///
    /// <para>The line frame is the baseline direction <c>v = (cos, sin)</c> and the
    /// downward normal <c>n = (−sin, cos)</c> (displayed frame, Y-down) through the
    /// line box's centre, so a slanted line's words descend/rise with it. Without
    /// char boxes the glyph band is the line box centre-deflated to
    /// EffectiveHeight (matching OcrPageMapper/OcrRecognizer) and words are
    /// apportioned by character count. With them, the band is re-seated on the ink
    /// the boxes cover — read-back char heights then match the printed glyphs rather
    /// than the detector's dilated band, which would otherwise exceed the line pitch
    /// and merge neighbouring lines in char-clustering line detection — and each
    /// fully boxed word spans its own glyphs.</para>
    /// </summary>
    private static IEnumerable<TextUnit> UnitsOf(OcrLine line, float angleRad, bool useCharBoxes)
    {
        var text = line.Text!;
        var words = WordSpans(text);
        if (words.Count == 0) yield break;

        double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
        float ac = (float)Math.Abs(cos), asn = (float)Math.Abs(sin);
        var box = line.Box;
        float perp = box.H * ac + box.W * asn;
        float glyphHeight = line.EffectiveHeight > 0f && perp > 0f
            ? Math.Min(line.EffectiveHeight, perp) : perp;
        if (glyphHeight <= 0f) glyphHeight = box.H;
        float totalAlong = ac > 0.05f
            ? Math.Max((box.W - glyphHeight * asn) / ac, 0f)
            : Math.Max((box.H - glyphHeight * ac) / Math.Max(asn, 0.001f), 0f);
        if (totalAlong <= 0f) totalAlong = box.W;

        double cx = box.X + box.W / 2.0, cy = box.Y + box.H / 2.0;
        double height = glyphHeight;

        // Along-baseline extent of each word, measured from the line centre.
        var lo = new double[words.Count];
        var hi = new double[words.Count];
        var fitted = new bool[words.Count];

        if (useCharBoxes && line.Chars is { Count: > 0 })
        {
            var byIndex = BoxesByIndex(line.Chars, text);
            var tops = new List<double>();
            var bottoms = new List<double>();
            foreach (var b in byIndex)
            {
                if (b is not { } cb) continue;
                var (pLo, pHi) = Project(cb, cx, cy, -sin, cos);
                tops.Add(pLo);
                bottoms.Add(pHi);
            }
            if (InkBand(tops, bottoms) is var (bandLo, bandHi))
            {
                height = Math.Min(bandHi - bandLo, glyphHeight);
                double mid = (bandLo + bandHi) / 2.0;
                cx -= mid * sin;
                cy += mid * cos;
            }

            // A word is fitted only when every one of its characters is boxed: a
            // partial box set would shrink the word onto the glyphs it happens to have.
            for (int k = 0; k < words.Count; k++)
            {
                var (start, end) = words[k];
                double wLo = double.PositiveInfinity, wHi = double.NegativeInfinity;
                bool complete = true;
                for (int i = start; i < end && complete; i++)
                {
                    if (char.IsLowSurrogate(text[i])) continue;
                    if (byIndex[i] is not { } cb) { complete = false; continue; }
                    var (aLo, aHi) = Project(cb, cx, cy, cos, sin);
                    wLo = Math.Min(wLo, aLo);
                    wHi = Math.Max(wHi, aHi);
                }
                if (complete && wHi > wLo) (lo[k], hi[k], fitted[k]) = (wLo, wHi, true);
            }
        }

        // Unfitted words are spaced by character count between their fitted
        // neighbours (or the line ends), so a word the engine could not box is still
        // written in its own slot rather than dropped. With nothing fitted this is the
        // plain proportional layout along the line box.
        double half = totalAlong / 2.0;
        for (int k = 0; k < words.Count;)
        {
            if (fitted[k]) { k++; continue; }
            int j = k;
            while (j < words.Count && !fitted[j]) j++;
            double a = k > 0 ? hi[k - 1] : -half, b = j < words.Count ? lo[j] : half;
            int ia = k > 0 ? words[k - 1].End : 0, ib = j < words.Count ? words[j].Start : text.Length;
            // Fitted neighbours that leave no room: fall back to the line's own span.
            if (b <= a) (a, b, ia, ib) = (-half, half, 0, text.Length);
            double scale = (b - a) / (ib - ia);
            for (int m = k; m < j; m++)
            {
                lo[m] = a + (words[m].Start - ia) * scale;
                hi[m] = a + (words[m].End - ia) * scale;
            }
            k = j;
        }

        for (int k = 0; k < words.Count; k++)
        {
            // Origin = centre + lo·v + (height/2)·n: the baseline start of a glyph
            // band centred on the line.
            double ox = cx + lo[k] * cos - (height / 2.0) * sin;
            double oy = cy + lo[k] * sin + (height / 2.0) * cos;
            var (start, end) = words[k];
            // Every word but the line's last carries a real space stretched to the
            // next word's start (as Tesseract's PDF renderer does), so readers find
            // the space instead of inferring it from a gap — MuPDF reads a gap wider
            // than ~0.8 em as a line break.
            bool last = k == words.Count - 1;
            double along = last ? hi[k] - lo[k] : Math.Max(lo[k + 1], hi[k]) - lo[k];
            yield return new TextUnit(last ? text[start..end] : text[start..end] + " ",
                (float)ox, (float)oy, (float)along, (float)height);
        }
    }

    /// <summary>
    /// How far past the median glyph's top/bottom, as a fraction of the median
    /// glyph's height, an extent may reach and still count as this line's ink.
    /// Ascenders, capitals, brackets and descenders reach about half of it.
    /// </summary>
    private const double BandOutlierFraction = 0.75;

    /// <summary>
    /// The line's ink band from its glyphs' perpendicular extents — not their plain
    /// union. On tightly set text a box's vertical tightening window reaches into
    /// the neighbouring line, and one glyph that picked up a descender from the line
    /// above (or an ascender from below) would stretch the whole band past the line
    /// pitch, so that the lines overlap and readers merge them. Null when nothing usable remains.
    /// </summary>
    private static (double Lo, double Hi)? InkBand(List<double> tops, List<double> bottoms)
    {
        if (tops.Count == 0) return null;
        double medTop = Median(tops), medBottom = Median(bottoms);
        double reach = Math.Max(medBottom - medTop, 0.0) * BandOutlierFraction;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (double t in tops) if (t >= medTop - reach) lo = Math.Min(lo, t);
        foreach (double b in bottoms) if (b <= medBottom + reach) hi = Math.Max(hi, b);
        return hi > lo ? (lo, hi) : null;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        int m = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2.0;
    }

    private static List<(int Start, int End)> WordSpans(string text)
    {
        var spans = new List<(int Start, int End)>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > start) spans.Add((start, i));
        }
        return spans;
    }

    /// <summary>
    /// The usable char box for each index of <paramref name="text"/>: in range,
    /// non-degenerate, and not on whitespace or on the low-surrogate half of an
    /// astral glyph (engines box each UTF-16 code unit; the glyph is its high half's).
    /// </summary>
    private static CharBox?[] BoxesByIndex(List<CharBox> chars, string text)
    {
        var byIndex = new CharBox?[text.Length];
        foreach (var cb in chars)
        {
            if (cb.Index < 0 || cb.Index >= text.Length) continue;
            if (cb.Right <= cb.Left || cb.Bottom <= cb.Top) continue;
            char c = text[cb.Index];
            if (char.IsLowSurrogate(c) || char.IsWhiteSpace(c)) continue;
            byIndex[cb.Index] = cb;
        }
        return byIndex;
    }

    /// <summary>
    /// Extent of an axis-aligned box projected onto the direction
    /// (<paramref name="dx"/>, <paramref name="dy"/>), relative to (<paramref name="ox"/>, <paramref name="oy"/>).
    /// On a slanted line the box is the bound of a tilted glyph, so the extent
    /// overshoots by up to the glyph's size × |sin θ| — under a point at the
    /// skew estimator's ±5° gate.
    /// </summary>
    private static (double Lo, double Hi) Project(CharBox b, double ox, double oy, double dx, double dy)
    {
        double l = (b.Left - ox) * dx, r = (b.Right - ox) * dx;
        double t = (b.Top - oy) * dy, u = (b.Bottom - oy) * dy;
        return (Math.Min(l, r) + Math.Min(t, u), Math.Max(l, r) + Math.Max(t, u));
    }

    private static bool WriteUnit(IntPtr doc, IntPtr page, TextUnit unit, float angleRad,
        in PageTransform tx, FontHandle font)
    {
        if (unit.Along <= 0f || unit.Height <= 0f || string.IsNullOrEmpty(unit.Text)) return false;
        float fontSize = Math.Max(unit.Height, 0.5f);

        var obj = font.CreateTextObject(doc, fontSize);
        if (obj == IntPtr.Zero) return false;
        bool inserted = false;
        try
        {
            if (!font.SetText(obj, unit.Text)) return false;
            if (!FPDFTextObj_SetTextRenderMode(obj, FPDF_TEXTRENDERMODE_INVISIBLE)) return false;
            if (FPDFPageObj_AddMark(obj, OcrMarkName) == IntPtr.Zero) return false;

            // Displayed-frame axes (Y-down): baseline direction and glyph-up.
            double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
            var (vx, vy) = Linear(tx, cos, sin);
            var (ux, uy) = Linear(tx, sin, -cos);
            var (ex, ey) = tx.PageToPdf(unit.OriginX, unit.OriginY);

            // Glyphless advance is UnitsPerEm per char, so the natural width of
            // the unit at this font size is glyphCount × fontSize; scale X to fit.
            // For the CID font each codepoint is one glyph; for the fallback the
            // UTF-16 length is the glyph count PDFium lays out.
            int glyphs = font.IsCid ? CodepointCount(unit.Text) : unit.Text.Length;
            double natural = Math.Max(glyphs, 1) * (double)fontSize;
            double sx = unit.Along / natural;

            var matrix = new FsMatrix
            {
                A = (float)(vx * sx), B = (float)(vy * sx),
                C = (float)ux, D = (float)uy,
                E = ex, F = ey,
            };
            if (!FPDFPageObj_SetMatrix(obj, ref matrix)) return false;
            FPDFPage_InsertObject(page, obj);
            inserted = true;
            return true;
        }
        finally
        {
            if (!inserted) FPDFPageObj_Destroy(obj);
        }
    }

    /// <summary>Applies only the linear part of the page transform to a displayed-frame vector.</summary>
    private static (double X, double Y) Linear(in PageTransform tx, double x, double y)
    {
        var (ox, oy) = tx.PageToPdf(0f, 0f);
        var (px, py) = tx.PageToPdf((float)x, (float)y);
        return (px - ox, py - oy);
    }

    private static int CodepointCount(string text)
    {
        int count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return Math.Max(count, 1);
    }

    private static int CountChars(IntPtr page)
    {
        var textPage = FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero) return 0;
        try { return FPDFText_CountChars(textPage); }
        finally { FPDFText_ClosePage(textPage); }
    }

    /// <summary>
    /// True when any text object on the page carries our provenance mark (or our
    /// glyphless font, as a belt-and-braces fallback if a producer strips marks).
    /// Caller holds <see cref="PdfiumGate.Lock"/> and owns <paramref name="page"/>.
    /// </summary>
    internal static bool PageHasOcrLayer(IntPtr page)
    {
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            var obj = FPDFPage_GetObject(page, i);
            if (obj != IntPtr.Zero && IsOcrTextObject(obj)) return true;
        }
        return false;
    }

    internal static bool IsOcrTextObject(IntPtr obj)
    {
        if (FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) return false;
        int marks = FPDFPageObj_CountMarks(obj);
        for (int m = 0; m < marks; m++)
        {
            var mark = FPDFPageObj_GetMark(obj, (nuint)m);
            if (mark != IntPtr.Zero && ReadMarkName(mark) == OcrMarkName) return true;
        }
        // Font-name fallback (mark stripped by another producer): only for
        // invisible objects. Visible native text that merely happens to use a
        // similarly named font must never be classified — and thus removed —
        // as OCR. Genuine layers are always written invisible, marked or not.
        if (FPDFTextObj_GetTextRenderMode(obj) != FPDF_TEXTRENDERMODE_INVISIBLE) return false;
        var name = ReadFontBaseName(FPDFTextObj_GetFont(obj));
        return name is not null && name.Contains(GlyphlessFont.FontName, StringComparison.Ordinal);
    }

    private static void RemoveOcrObjects(IntPtr page)
    {
        var doomed = new List<IntPtr>();
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            var obj = FPDFPage_GetObject(page, i);
            if (obj != IntPtr.Zero && IsOcrTextObject(obj)) doomed.Add(obj);
        }
        foreach (var obj in doomed)
        {
            if (FPDFPage_RemoveObject(page, obj)) FPDFPageObj_Destroy(obj);
        }
    }

    /// <summary>
    /// The document's OCR font: the glyphless CID font when it loads, Helvetica
    /// otherwise. Loaded once per write; closed with the document write.
    /// </summary>
    private sealed class FontHandle : IDisposable
    {
        /// <summary>Replacement for characters a two-byte CID cannot represent (astral planes).</summary>
        internal const int ReplacementCodepoint = 0xFFFD;

        private readonly IntPtr _font;
        private readonly Dictionary<int, int> _cidByCodepoint = new();
        public bool IsCid { get; }

        public FontHandle(IntPtr doc, IEnumerable<OcrPage> pages)
        {
            // Dense CID assignment: collect the distinct BMP codepoints actually
            // used (astral runes collapse to U+FFFD — a two-byte CID cannot
            // address them, and clamping them to U+FFFF would collide distinct
            // characters on a noncharacter outside the sized font).
            var used = new SortedSet<int>();
            foreach (var page in pages)
            foreach (var line in page.Lines)
            {
                if (line.Text is null) continue;
                foreach (var rune in line.Text.EnumerateRunes())
                    used.Add(Sanitize(rune.Value));
            }
            // The writer joins words with U+0020 whatever whitespace separated them.
            used.Add(' ');

            int cid = 1; // CID 0 is .notdef
            var cidToUnicode = new Dictionary<int, int>();
            foreach (int codepoint in used)
            {
                _cidByCodepoint[codepoint] = cid;
                cidToUnicode[cid] = codepoint;
                cid++;
            }
            int numGlyphs = used.Count + 1;

            var ttf = GlyphlessFont.Build(numGlyphs, _cidByCodepoint);
            var cidToGid = GlyphlessFont.BuildCidToGidMap(numGlyphs);
            _font = FPDFText_LoadCidType2Font(doc, ttf, (uint)ttf.Length,
                GlyphlessFont.BuildToUnicodeCmap(cidToUnicode), cidToGid, (uint)cidToGid.Length);
            IsCid = _font != IntPtr.Zero;
            if (!IsCid)
            {
                RailReaderLogging.Logger.Debug("[PdfTextLayerWriter] Glyphless CID font failed to load; falling back to Helvetica (WinAnsi only)");
                _font = FPDFText_LoadStandardFont(doc, "Helvetica");
            }
        }

        internal static int Sanitize(int runeValue)
        {
            // Tab is ordinary word-separating whitespace; every other control,
            // the U+FFFE/U+FFFF noncharacters (which readers do not round-trip),
            // and anything outside the BMP collapses to U+FFFD.
            if (runeValue == '\t') return ' ';
            if (runeValue < 0x20 || runeValue is >= 0x7F and <= 0x9F
                || runeValue is 0xFFFE or 0xFFFF or > 0xFFFF)
                return ReplacementCodepoint;
            return runeValue;
        }

        public IntPtr CreateTextObject(IntPtr doc, float fontSize)
            => IsCid && _font != IntPtr.Zero
                ? FPDFPageObj_CreateTextObj(doc, _font, fontSize)
                : FPDFPageObj_NewTextObj(doc, "Helvetica", fontSize);

        public bool SetText(IntPtr obj, string text)
        {
            if (IsCid)
            {
                var codes = new List<uint>();
                foreach (var rune in text.EnumerateRunes())
                {
                    int codepoint = Sanitize(rune.Value);
                    if (_cidByCodepoint.TryGetValue(codepoint, out int assigned))
                        codes.Add((uint)assigned);
                    else if (_cidByCodepoint.TryGetValue(ReplacementCodepoint, out int fallback))
                        codes.Add((uint)fallback);
                }
                return codes.Count > 0 && FPDFText_SetCharcodes(obj, codes.ToArray(), (nuint)codes.Count);
            }
            var utf16 = Encoding.Unicode.GetBytes(text + "\0");
            var pin = GCHandle.Alloc(utf16, GCHandleType.Pinned);
            try { return FPDFText_SetText(obj, pin.AddrOfPinnedObject()); }
            finally { pin.Free(); }
        }

        public void Dispose()
        {
            if (_font != IntPtr.Zero) FPDFFont_Close(_font);
        }
    }
}
