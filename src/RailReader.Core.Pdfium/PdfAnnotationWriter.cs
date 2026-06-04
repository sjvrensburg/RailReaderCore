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
/// <para><see cref="AddAuthoredAnnotations"/> performs an <b>incremental, in-place</b>
/// write: it loads the existing document, appends the RailReader-authored
/// annotations, and saves via <c>FPDF_SaveAsCopy(FPDF_INCREMENTAL)</c> — preserving
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
    /// bytes via incremental save. In-PDF annotations are left untouched.
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

                    return SaveIncremental(doc);
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
    /// Writes a single annotation onto an already-loaded PDFium page. Shared by the
    /// in-place writer and <see cref="AnnotationExportService"/>. Caller holds
    /// <see cref="PdfiumGate.Lock"/>.
    /// </summary>
    internal static void WriteAnnotationToPage(IntPtr page, Annotation ann,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        switch (ann)
        {
            case TextMarkupAnnotation m:
                WriteTextMarkup(page, m, MarkupSubtype(m), cropLeft, cropBottom, visibleHeight);
                break;
            case FreehandAnnotation f:
                WriteInk(page, f, cropLeft, cropBottom, visibleHeight);
                break;
            case RectAnnotation r:
                WriteRect(page, r, cropLeft, cropBottom, visibleHeight);
                break;
            case TextNoteAnnotation tn:
                WriteTextNote(page, tn, cropLeft, cropBottom, visibleHeight);
                break;
            case CaretAnnotation:
                // PDFium's FPDFPage_CreateAnnot cannot create Caret annotations (not in its
                // supported-subtype whitelist). Carets are read-only: we surface existing ones
                // (PdfAnnotationReader) and preserve them on save, but never author new ones.
                // RailReader has no caret-authoring tool, so this only matters defensively.
                RailReaderLogging.Logger.Debug("[PdfAnnotationWriter] Skipping Caret: PDFium cannot create caret annotations");
                break;
            case FreeTextAnnotation ft:
                WriteRectShaped(page, ft, FPDF_ANNOT_FREETEXT, ft.X, ft.Y, ft.W, ft.H, cropLeft, cropBottom, visibleHeight);
                break;
        }
    }

    private static int MarkupSubtype(TextMarkupAnnotation m) => m switch
    {
        UnderlineAnnotation => FPDF_ANNOT_UNDERLINE,
        SquigglyAnnotation => FPDF_ANNOT_SQUIGGLY,
        StrikeOutAnnotation => FPDF_ANNOT_STRIKEOUT,
        _ => FPDF_ANNOT_HIGHLIGHT,
    };

    private static void WriteTextMarkup(IntPtr page, TextMarkupAnnotation m, int subtype,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (m.Rects.Count == 0) return;

        var annot = FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return;
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
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void WriteInk(IntPtr page, FreehandAnnotation f,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (f.Points.Count < 2) return;

        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_INK);
        if (annot == IntPtr.Zero) return;
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
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void WriteRect(IntPtr page, RectAnnotation ra,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_SQUARE);
        if (annot == IntPtr.Zero) return;
        try
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

            var (lx, by) = PagePointToPdf(ra.X, ra.Y + ra.H, cropLeft, cropBottom, visibleHeight);
            var (rx, ty) = PagePointToPdf(ra.X + ra.W, ra.Y, cropLeft, cropBottom, visibleHeight);
            var rect = new FsRectF { Left = lx, Bottom = by, Right = rx, Top = ty };
            FPDFAnnot_SetRect(annot, ref rect);
            ApplyCommonMetadata(annot, ra);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    private static void WriteTextNote(IntPtr page, TextNoteAnnotation tn,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_TEXT);
        if (annot == IntPtr.Zero) return;
        try
        {
            var color = ResolveColor(tn);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);

            var (px, py) = PagePointToPdf(tn.X, tn.Y, cropLeft, cropBottom, visibleHeight);
            var rect = new FsRectF { Left = px, Bottom = py - StickyNoteSize, Right = px + StickyNoteSize, Top = py };
            FPDFAnnot_SetRect(annot, ref rect);
            ApplyCommonMetadata(annot, tn);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Writes a simple rect-bounded annotation (Caret, FreeText).</summary>
    private static void WriteRectShaped(IntPtr page, Annotation ann, int subtype,
        float x, float y, float w, float h, float cropLeft, float cropBottom, double visibleHeight)
    {
        var annot = FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return;
        try
        {
            var color = ResolveColor(ann);
            FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_COLOR, color.R, color.G, color.B, color.A);

            var (lx, by) = PagePointToPdf(x, y + h, cropLeft, cropBottom, visibleHeight);
            var (rx, ty) = PagePointToPdf(x + w, y, cropLeft, cropBottom, visibleHeight);
            var rect = new FsRectF { Left = lx, Bottom = by, Right = rx, Top = ty };
            FPDFAnnot_SetRect(annot, ref rect);
            ApplyCommonMetadata(annot, ann);
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
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
    }

    private static ColorRGBA ResolveColor(Annotation ann)
    {
        byte a = (byte)Math.Clamp(ann.Opacity * 255f, 0f, 255f);
        if (ann.ColorComponents is { Length: 3 } c)
            return new ColorRGBA(To255(c[0]), To255(c[1]), To255(c[2]), a);
        return ColorUtils.ParseHexColor(ann.Color, a);

        static byte To255(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);
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

    private static byte[] SaveIncremental(IntPtr document)
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

        bool ok = FPDF_SaveAsCopy(document, ref fileWrite, FPDF_INCREMENTAL);
        GC.KeepAlive(writeBlock);
        if (!ok) throw new InvalidOperationException("FPDF_SaveAsCopy (incremental) failed");
        return ms.ToArray();
    }
}
