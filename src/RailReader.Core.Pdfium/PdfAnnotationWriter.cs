using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Writes Core annotations into a PDF as native PDF annotation objects.
///
/// <para><b>Coordinates:</b> Core annotations are in page-point space (top-left
/// origin, Y-down); these helpers convert to PDF user space (bottom-left, Y-up)
/// via <see cref="PdfiumNative.PagePointToPdf"/>, the inverse of
/// <see cref="PdfAnnotationReader"/>.</para>
///
/// <para><see cref="AddAuthoredAnnotations"/> performs an <b>in-place</b>
/// write: it loads the existing document, appends the RailReader-authored
/// annotations, and saves via <c>FPDF_SaveAsCopy</c> — preserving
/// the document's existing /Annots, AcroForm, and any digital signatures. This is
/// the opposite of <see cref="AnnotationExportService"/>, which bakes a flattened
/// copy into a new document.</para>
///
/// <para>The per-annotation writer (<see cref="WriteAnnotationToPage"/>) is shared
/// with the export path so there is a single source of truth for the geometry and
/// metadata mapping.</para>
/// </summary>
public sealed class PdfAnnotationWriter
{
    private const float StickyNoteSize = 24f;

    /// <summary>
    /// Appends every RailReader-authored annotation (<see cref="AnnotationSource.RailReader"/>)
    /// from <paramref name="annotations"/> into the existing PDF and returns the updated
    /// bytes (a full rewrite that preserves the document's existing content). In-PDF annotations are left untouched.
    /// </summary>
    /// <remarks>
    /// PR 2 step 1 scope: this only <i>adds</i> authored annotations; it does not yet
    /// reconcile edits/deletes of existing in-PDF annotations, and does not mark the
    /// written annotations as persisted — so it is not idempotent across repeated calls.
    /// Edit/delete-by-/NM and idempotent save are the next step.
    /// </remarks>
    public byte[] AddAuthoredAnnotations(byte[] pdfBytes, AnnotationFile annotations)
    {
        lock (PdfiumGate.Lock)
        {
            PdfiumResolver.EnsureLibraryInitialized();

            var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            try
            {
                var doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
                if (doc == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to load PDF via PDFium");

                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    foreach (var (pageIndex, list) in annotations.Pages)
                    {
                        if (pageIndex < 0 || pageIndex >= pageCount) continue;

                        var authored = list.Where(a => a.Source != AnnotationSource.InPdf).ToList();
                        if (authored.Count == 0) continue;

                        var page = FPDF_LoadPage(doc, pageIndex);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            var (cropLeft, cropBottom, visibleHeight) = GetCropBoxTransform(page);
                            foreach (var ann in authored)
                                WriteAnnotationToPage(page, ann, cropLeft, cropBottom, visibleHeight);
                        }
                        finally
                        {
                            FPDF_ClosePage(page);
                        }
                    }

                    return SaveDocumentBytes(doc);
                }
                finally
                {
                    FPDF_CloseDocument(doc);
                }
            }
            finally
            {
                pinned.Free();
            }
        }
    }

    /// <summary>
    /// Reconciles the PDF's managed annotations to match <paramref name="file"/>,
    /// keyed by <see cref="Annotation.NativeId"/> (/NM), and returns the updated bytes. Idempotent and lossless for unchanged annotations:
    /// <list type="bullet">
    /// <item><b>Add</b> — annotations without a /NM are created; a fresh /NM is minted and
    /// written back into <paramref name="file"/> (<see cref="AnnotationSource.InPdf"/>), so
    /// a subsequent save recognises them and does not duplicate.</item>
    /// <item><b>Delete</b> — managed PDF annotations whose /NM is no longer present in
    /// <paramref name="file"/> are removed.</item>
    /// <item><b>Edit</b> — matched annotations are compared by value; unchanged ones are left
    /// completely untouched (preserving Acrobat /RC, /AP, etc.); changed rect-based ones are
    /// updated in place; changed text-markup/ink ones are deleted and recreated with the
    /// same /NM.</item>
    /// </list>
    /// <b>Mutates</b> <paramref name="file"/> by back-filling /NM and Source on newly persisted
    /// annotations. Caret annotations can be edited in place but never created
    /// (<see cref="CaretAnnotation"/>). Only pages present in <paramref name="file"/> are
    /// visited.
    /// </summary>
    public byte[] WriteReconciled(byte[] pdfBytes, AnnotationFile file)
    {
        lock (PdfiumGate.Lock)
        {
            PdfiumResolver.EnsureLibraryInitialized();

            var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            try
            {
                var doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
                if (doc == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to load PDF via PDFium");

                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    foreach (var pageIndex in file.Pages.Keys.Where(k => k >= 0 && k < pageCount).OrderBy(k => k))
                    {
                        var page = FPDF_LoadPage(doc, pageIndex);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            ReconcilePage(page, file.Pages[pageIndex]);
                        }
                        finally
                        {
                            FPDF_ClosePage(page);
                        }
                    }

                    return SaveDocumentBytes(doc);
                }
                finally
                {
                    FPDF_CloseDocument(doc);
                }
            }
            finally
            {
                pinned.Free();
            }
        }
    }

    private static void ReconcilePage(IntPtr page, List<Annotation> desired)
    {
        var (cl, cb, vh) = GetCropBoxTransform(page);

        var desiredByNm = new Dictionary<string, Annotation>(StringComparer.Ordinal);
        var desiredNew = new List<Annotation>();
        foreach (var a in desired)
        {
            if (string.IsNullOrEmpty(a.NativeId)) desiredNew.Add(a);
            else desiredByNm[a.NativeId!] = a;
        }

        var toDelete = new List<int>();
        var recreate = new List<Annotation>();
        var inPlace = new List<(int Index, Annotation Ann)>();
        var matched = new HashSet<string>(StringComparer.Ordinal);

        int count = FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            var annot = FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                if (!IsManagedSubtype(FPDFAnnot_GetSubtype(annot))) continue;

                var nm = ReadAnnotString(annot, "NM");
                if (string.IsNullOrEmpty(nm)) continue; // unidentifiable — never touch

                if (!desiredByNm.TryGetValue(nm!, out var want))
                {
                    toDelete.Add(i); // removed from the model → delete from the PDF
                    continue;
                }

                matched.Add(nm!);
                var current = PdfAnnotationReader.ReadSingle(annot, cl, cb, vh);
                if (current is not null && AnnotationEquivalence.ContentEquivalent(current, want))
                    continue; // unchanged → leave the original annotation untouched

                if (want is TextMarkupAnnotation or FreehandAnnotation or RectAnnotation)
                {
                    // Quad/ink geometry, and Rect fill — which can't be cleared in place since
                    // PDFium can't remove /IC — are handled by delete + recreate (same /NM).
                    toDelete.Add(i);
                    recreate.Add(want);
                }
                else
                {
                    inPlace.Add((i, want)); // rect-based → modify in place, preserving other keys
                }
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }

        // Desired annotations with a /NM that isn't in the PDF (e.g. migrated) → create with that /NM.
        foreach (var (nm, want) in desiredByNm)
            if (!matched.Contains(nm)) recreate.Add(want);

        // In-place edits first (indices are still valid)…
        foreach (var (index, ann) in inPlace)
        {
            var annot = FPDFPage_GetAnnot(page, index);
            if (annot == IntPtr.Zero) continue;
            try { EditRectBasedInPlace(annot, ann, cl, cb, vh); }
            finally { FPDFPage_CloseAnnot(annot); }
        }

        // …then deletions, descending so earlier indices stay valid…
        foreach (var index in toDelete.OrderByDescending(x => x))
            FPDFPage_RemoveAnnot(page, index);

        // …then additions: new authored (mint /NM, mark persisted) and recreated edits.
        foreach (var ann in desiredNew)
        {
            // An in-PDF annotation with no /NM is already in the document but unidentifiable;
            // re-creating it would duplicate it. Leave it untouched.
            if (ann.Source == AnnotationSource.InPdf) continue;

            ann.NativeId = Guid.NewGuid().ToString();
            // Only mark it persisted if an annotation was actually written — empty-geometry
            // annots (0 rects / <2 ink points) and Carets write nothing, and must NOT be
            // flagged InPdf or they'd be lost from both the PDF and the sidecar.
            if (WriteAnnotationToPage(page, ann, cl, cb, vh))
                ann.Source = AnnotationSource.InPdf;
            else
                ann.NativeId = null;
        }
        foreach (var ann in recreate)
            WriteAnnotationToPage(page, ann, cl, cb, vh);
    }

    private static bool IsManagedSubtype(int subtype) => subtype is
        FPDF_ANNOT_TEXT or FPDF_ANNOT_FREETEXT or FPDF_ANNOT_SQUARE or
        FPDF_ANNOT_HIGHLIGHT or FPDF_ANNOT_UNDERLINE or FPDF_ANNOT_SQUIGGLY or
        FPDF_ANNOT_STRIKEOUT or FPDF_ANNOT_CARET or FPDF_ANNOT_INK;

    /// <summary>
    /// In-place edit for rect-based annotations that PDFium can't (or shouldn't) recreate —
    /// Text (sticky), Caret (uncreatable), and FreeText — preserving their other keys.
    /// Rect/markup/ink changes go through delete+recreate instead.
    /// </summary>
    private static void EditRectBasedInPlace(IntPtr annot, Annotation ann,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        FsRectF rect = ann switch
        {
            TextNoteAnnotation tn => StickyRect(tn.X, tn.Y, cropLeft, cropBottom, visibleHeight),
            CaretAnnotation c => BoxRect(c.X, c.Y, c.W, c.H, cropLeft, cropBottom, visibleHeight),
            FreeTextAnnotation ft => BoxRect(ft.X, ft.Y, ft.W, ft.H, cropLeft, cropBottom, visibleHeight),
            _ => default,
        };
        FPDFAnnot_SetRect(annot, ref rect);

        // Re-apply colour so a colour edit isn't dropped on an in-place edit.
        var color = ResolveColor(ann);
        FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);

        SetString(annot, "Contents", AnnotationEquivalence.EffectiveContents(ann));
        SetString(annot, "M", FormatPdfDate(DateTimeOffset.Now));
        WriteReviewState(annot, ann.State);
    }

    private static FsRectF StickyRect(float x, float y, float cl, float cb, double vh)
    {
        var (px, py) = PagePointToPdf(x, y, cl, cb, vh);
        return new FsRectF { Left = px, Bottom = py - StickyNoteSize, Right = px + StickyNoteSize, Top = py };
    }

    private static FsRectF BoxRect(float x, float y, float w, float h, float cl, float cb, double vh)
    {
        var (lx, by) = PagePointToPdf(x, y + h, cl, cb, vh);
        var (rx, ty) = PagePointToPdf(x + w, y, cl, cb, vh);
        return new FsRectF { Left = lx, Bottom = by, Right = rx, Top = ty };
    }

    /// <summary>
    /// Writes a single annotation onto an already-loaded PDFium page. Shared by the
    /// in-place writer and <see cref="AnnotationExportService"/>. Caller holds
    /// <see cref="PdfiumGate.Lock"/>.
    /// </summary>
    /// <summary>Returns true iff a PDF annotation was actually created (false for an
    /// unsupported subtype or empty geometry, so the caller can avoid marking it persisted).</summary>
    internal static bool WriteAnnotationToPage(IntPtr page, Annotation ann,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        switch (ann)
        {
            case TextMarkupAnnotation m:
                return WriteTextMarkup(page, m, MarkupSubtype(m), cropLeft, cropBottom, visibleHeight);
            case FreehandAnnotation f:
                return WriteInk(page, f, cropLeft, cropBottom, visibleHeight);
            case RectAnnotation r:
                return WriteRect(page, r, cropLeft, cropBottom, visibleHeight);
            case TextNoteAnnotation tn:
                return WriteTextNote(page, tn, cropLeft, cropBottom, visibleHeight);
            case CaretAnnotation:
                // PDFium's FPDFPage_CreateAnnot cannot create Caret annotations (not in its
                // supported-subtype whitelist). Carets are read-only: we surface existing ones
                // (PdfAnnotationReader) and preserve them on save, but never author new ones.
                // RailReader has no caret-authoring tool, so this only matters defensively.
                RailReaderLogging.Logger.Debug("[PdfAnnotationWriter] Skipping Caret: PDFium cannot create caret annotations");
                return false;
            case FreeTextAnnotation ft:
                return WriteRectShaped(page, ft, FPDF_ANNOT_FREETEXT, ft.X, ft.Y, ft.W, ft.H, cropLeft, cropBottom, visibleHeight);
            default:
                return false;
        }
    }

    private static int MarkupSubtype(TextMarkupAnnotation m) => m switch
    {
        UnderlineAnnotation => FPDF_ANNOT_UNDERLINE,
        SquigglyAnnotation => FPDF_ANNOT_SQUIGGLY,
        StrikeOutAnnotation => FPDF_ANNOT_STRIKEOUT,
        _ => FPDF_ANNOT_HIGHLIGHT,
    };

    private static bool WriteTextMarkup(IntPtr page, TextMarkupAnnotation m, int subtype,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (m.Rects.Count == 0) return false;

        var annot = FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return false;
        try
        {
            var color = ResolveColor(m);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var rect in m.Rects)
            {
                var (lx, by) = PagePointToPdf(rect.X, rect.Y + rect.H, cropLeft, cropBottom, visibleHeight);
                var (rx, ty) = PagePointToPdf(rect.X + rect.W, rect.Y, cropLeft, cropBottom, visibleHeight);
                var quad = new FsQuadPointsF { X1 = lx, Y1 = by, X2 = rx, Y2 = by, X3 = lx, Y3 = ty, X4 = rx, Y4 = ty };
                FPDFAnnot_AppendAttachmentPoints(annot, ref quad);
                minX = Math.Min(minX, lx); minY = Math.Min(minY, by);
                maxX = Math.Max(maxX, rx); maxY = Math.Max(maxY, ty);
            }

            var bounding = new FsRectF { Left = minX, Bottom = minY, Right = maxX, Top = maxY };
            FPDFAnnot_SetRect(annot, ref bounding);
            ApplyCommonMetadata(annot, m);
            return true;
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static bool WriteInk(IntPtr page, FreehandAnnotation f,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (f.Points.Count < 2) return false;

        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_INK);
        if (annot == IntPtr.Zero) return false;
        try
        {
            var color = ResolveColor(f);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);
            FPDFAnnot_SetBorder(annot, 0, 0, f.StrokeWidth);

            var pts = new FsPointF[f.Points.Count];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < f.Points.Count; i++)
            {
                var (px, py) = PagePointToPdf(f.Points[i].X, f.Points[i].Y, cropLeft, cropBottom, visibleHeight);
                pts[i] = new FsPointF { X = px, Y = py };
                minX = Math.Min(minX, px); minY = Math.Min(minY, py);
                maxX = Math.Max(maxX, px); maxY = Math.Max(maxY, py);
            }
            FPDFAnnot_AddInkStroke(annot, pts, (nuint)pts.Length);

            float pad = f.StrokeWidth / 2f;
            var bounding = new FsRectF { Left = minX - pad, Bottom = minY - pad, Right = maxX + pad, Top = maxY + pad };
            FPDFAnnot_SetRect(annot, ref bounding);
            ApplyCommonMetadata(annot, f);
            return true;
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static bool WriteRect(IntPtr page, RectAnnotation ra,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_SQUARE);
        if (annot == IntPtr.Zero) return false;
        try
        {
            ApplyRectStyle(annot, ra);

            var (lx, by) = PagePointToPdf(ra.X, ra.Y + ra.H, cropLeft, cropBottom, visibleHeight);
            var (rx, ty) = PagePointToPdf(ra.X + ra.W, ra.Y, cropLeft, cropBottom, visibleHeight);
            var rect = new FsRectF { Left = lx, Bottom = by, Right = rx, Top = ty };
            FPDFAnnot_SetRect(annot, ref rect);
            ApplyCommonMetadata(annot, ra);
            return true;
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static bool WriteTextNote(IntPtr page, TextNoteAnnotation tn,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_TEXT);
        if (annot == IntPtr.Zero) return false;
        try
        {
            var color = ResolveColor(tn);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);

            var (px, py) = PagePointToPdf(tn.X, tn.Y, cropLeft, cropBottom, visibleHeight);
            var rect = new FsRectF { Left = px, Bottom = py - StickyNoteSize, Right = px + StickyNoteSize, Top = py };
            FPDFAnnot_SetRect(annot, ref rect);
            ApplyCommonMetadata(annot, tn);
            return true;
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Writes a simple rect-bounded annotation (FreeText).</summary>
    private static bool WriteRectShaped(IntPtr page, Annotation ann, int subtype,
        float x, float y, float w, float h, float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return false;
        try
        {
            var color = ResolveColor(ann);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);

            var (lx, by) = PagePointToPdf(x, y + h, cropLeft, cropBottom, visibleHeight);
            var (rx, ty) = PagePointToPdf(x + w, y, cropLeft, cropBottom, visibleHeight);
            var rect = new FsRectF { Left = lx, Bottom = by, Right = rx, Top = ty };
            FPDFAnnot_SetRect(annot, ref rect);
            ApplyCommonMetadata(annot, ann);
            return true;
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Applies a rect annotation's colour, fill, and border — shared by create and in-place edit.</summary>
    private static void ApplyRectStyle(IntPtr annot, RectAnnotation ra)
    {
        var color = ResolveColor(ra);
        if (ra.Filled)
        {
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_INTERIOR, color.R, color.G, color.B, color.A);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);
        }
        else
        {
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, 255);
        }
        FPDFAnnot_SetBorder(annot, 0, 0, ra.StrokeWidth);
    }

    /// <summary>Stamps the shared PDF metadata keys (/NM, /Contents, /T, /Subj, dates, flags).</summary>
    private static void ApplyCommonMetadata(IntPtr annot, Annotation ann)
    {
        FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);

        // Stable id: reuse the existing /NM when known, otherwise mint one. The PDF is
        // the record; the in-memory model is not mutated here.
        SetString(annot, "NM", string.IsNullOrEmpty(ann.NativeId) ? Guid.NewGuid().ToString() : ann.NativeId!);

        var contents = !string.IsNullOrEmpty(ann.Contents)
            ? ann.Contents
            : (ann as TextNoteAnnotation)?.Text;
        if (!string.IsNullOrEmpty(contents)) SetString(annot, "Contents", contents!);
        if (!string.IsNullOrEmpty(ann.Author)) SetString(annot, "T", ann.Author!);
        if (!string.IsNullOrEmpty(ann.Subject)) SetString(annot, "Subj", ann.Subject!);

        var now = DateTimeOffset.Now;
        SetString(annot, "CreationDate", FormatPdfDate(ann.CreatedUtc ?? now));
        SetString(annot, "M", FormatPdfDate(ann.ModifiedUtc ?? now));

        WriteReviewState(annot, ann.State);
        // Note: /IRT (reply linkage) is an indirect object reference; PDFium has no API to set
        // it, so reply threading is read-only and not re-emitted on write.
    }

    /// <summary>Writes the Acrobat review state (/State + /StateModel "Review"); no-op for None.</summary>
    private static void WriteReviewState(IntPtr annot, ReviewState state)
    {
        if (state == ReviewState.None) return;
        SetString(annot, "StateModel", "Review");
        SetString(annot, "State", state.ToString()); // Accepted/Rejected/Cancelled/Completed
    }

    private static ColorRGBA ResolveColor(Annotation ann)
    {
        // The hex Color is the write source of truth: ColorComponents (faithful floats from
        // the reader) only matter for displaying unchanged annotations, which are never
        // rewritten. Preferring ColorComponents here would ignore a user's colour edit, which
        // updates Color but leaves the stale ColorComponents in place.
        byte a = (byte)Math.Clamp(ann.Opacity * 255f, 0f, 255f);
        return ColorUtils.ParseHexColor(ann.Color, a);
    }

    /// <summary>Formats a PDF date string: D:YYYYMMDDHHmmSS±HH'mm'.</summary>
    internal static string FormatPdfDate(DateTimeOffset dt)
    {
        var o = dt.Offset;
        char sign = o < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{dt:yyyyMMddHHmmss}{sign}{Math.Abs(o.Hours):D2}'{Math.Abs(o.Minutes):D2}'");
    }

    private static void SetString(IntPtr annot, string key, string value)
    {
        var utf16 = Encoding.Unicode.GetBytes(value + "\0");
        var pin = GCHandle.Alloc(utf16, GCHandleType.Pinned);
        try { FPDFAnnot_SetStringValue(annot, key, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    /// <summary>
    /// Serialises the (already-modified) loaded document to bytes. Uses a full
    /// <c>FPDF_SaveAsCopy</c> rewrite (flag 0), not an incremental update: PDFium's
    /// incremental save appends a fresh xref section that strict validators (qpdf)
    /// flag as damaged on linearised / xref-stream source PDFs, whereas a full rewrite
    /// of the loaded document emits one clean xref while still preserving the document's
    /// existing /Annots and AcroForm (this is the loaded-doc path, not the
    /// new-doc/ImportPages flatten). Signed PDFs never reach here — they are routed to
    /// the sidecar by <see cref="CompositeAnnotationStore"/> — so incremental's
    /// signature-ByteRange preservation is not needed.
    /// </summary>
    private static byte[] SaveDocumentBytes(IntPtr document)
    {
        using var ms = new MemoryStream();
        byte[] buffer = [];
        WriteBlockDelegate writeBlock = (IntPtr self, IntPtr data, uint size) =>
        {
            int n = (int)size;
            if (buffer.Length < n) buffer = new byte[n];
            Marshal.Copy(data, buffer, 0, n);
            ms.Write(buffer, 0, n);
            return 1;
        };

        var fileWrite = new FpdfFileWrite
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(writeBlock),
        };

        bool ok = FPDF_SaveAsCopy(document, ref fileWrite, 0);
        GC.KeepAlive(writeBlock);
        if (!ok) throw new InvalidOperationException("FPDF_SaveAsCopy failed");
        return ms.ToArray();
    }
}
