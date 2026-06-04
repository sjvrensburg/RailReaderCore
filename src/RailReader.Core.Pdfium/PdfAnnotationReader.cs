using System.Globalization;
using System.Runtime.InteropServices;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Reads native PDF markup annotations (Acrobat review comments and the like)
/// from a document's /Annots dictionaries and maps them to Core's annotation
/// model. Geometry is returned in page-point space (origin top-left, Y-down),
/// the inverse of the transform used by the annotation export/write path.
///
/// Read-only. Link and Popup annotations are skipped — links are served by
/// <see cref="IPdfLinkService"/>, and popups are companion objects whose
/// content is carried by their parent markup annotation.
/// </summary>
public sealed class PdfAnnotationReader
{
    /// <summary>
    /// Reads every supported markup annotation in the document into an
    /// <see cref="AnnotationFile"/> keyed by page index. Marks each result
    /// <see cref="AnnotationSource.InPdf"/>. Never throws — on failure it logs
    /// and returns whatever was read so far (possibly empty).
    /// </summary>
    public AnnotationFile Read(byte[] pdfBytes)
    {
        var file = new AnnotationFile();

        // The annotation store can be queried before any SkiaPdfService is
        // constructed, so PDFium may not have been initialised by PDFtoImage yet.
        PdfiumResolver.EnsureLibraryInitialized();

        lock (PdfiumGate.Lock)
        {
            IntPtr doc = IntPtr.Zero;
            GCHandle pinned = default;
            try
            {
                pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
                doc = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
                if (doc == IntPtr.Zero) return file;

                int pageCount = FPDF_GetPageCount(doc);
                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    IntPtr page = FPDF_LoadPage(doc, pageIndex);
                    if (page == IntPtr.Zero) continue;
                    try
                    {
                        var pageAnnots = ReadPage(page);
                        if (pageAnnots.Count > 0)
                            file.Pages[pageIndex] = pageAnnots;
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }
                }
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Error("[PdfAnnotationReader] Failed to read annotations", ex);
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                if (pinned.IsAllocated) pinned.Free();
            }
        }

        return file;
    }

    private static List<Annotation> ReadPage(IntPtr page)
    {
        var result = new List<Annotation>();
        var (cropLeft, cropBottom, visibleHeight) = GetCropBoxTransform(page);

        int count = FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr annot = FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                var mapped = ReadSingle(annot, cropLeft, cropBottom, visibleHeight);
                if (mapped is not null) result.Add(mapped);
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }

        return result;
    }

    /// <summary>
    /// Maps one already-open PDFium annotation handle to a Core annotation (with
    /// metadata), or null for unsupported subtypes. Shared with the reconciling writer.
    /// Caller owns the handle and holds <see cref="PdfiumGate.Lock"/>.
    /// </summary>
    internal static Annotation? ReadSingle(IntPtr annot, float cropLeft, float cropBottom, double visibleHeight)
    {
        int subtype = FPDFAnnot_GetSubtype(annot);
        var mapped = Map(annot, subtype, cropLeft, cropBottom, visibleHeight);
        if (mapped is null) return null;
        ApplyCommonMetadata(annot, mapped);
        return mapped;
    }

    private static Annotation? Map(IntPtr annot, int subtype,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        switch (subtype)
        {
            case FPDF_ANNOT_HIGHLIGHT:
                return new HighlightAnnotation { Rects = ReadQuads(annot, cropLeft, cropBottom, visibleHeight) };
            case FPDF_ANNOT_UNDERLINE:
                return new UnderlineAnnotation { Rects = ReadQuads(annot, cropLeft, cropBottom, visibleHeight) };
            case FPDF_ANNOT_STRIKEOUT:
                return new StrikeOutAnnotation { Rects = ReadQuads(annot, cropLeft, cropBottom, visibleHeight) };
            case FPDF_ANNOT_SQUIGGLY:
                return new SquigglyAnnotation { Rects = ReadQuads(annot, cropLeft, cropBottom, visibleHeight) };

            case FPDF_ANNOT_TEXT:
            {
                var (x, y, _, _) = ReadRect(annot, cropLeft, cropBottom, visibleHeight);
                return new TextNoteAnnotation { X = x, Y = y };
            }
            case FPDF_ANNOT_CARET:
            {
                var (x, y, w, h) = ReadRect(annot, cropLeft, cropBottom, visibleHeight);
                return new CaretAnnotation { X = x, Y = y, W = w, H = h };
            }
            case FPDF_ANNOT_FREETEXT:
            {
                var (x, y, w, h) = ReadRect(annot, cropLeft, cropBottom, visibleHeight);
                return new FreeTextAnnotation { X = x, Y = y, W = w, H = h };
            }
            case FPDF_ANNOT_SQUARE:
            {
                var (x, y, w, h) = ReadRect(annot, cropLeft, cropBottom, visibleHeight);
                var rect = new RectAnnotation { X = x, Y = y, W = w, H = h };
                if (FPDFAnnot_GetBorder(annot, out _, out _, out float bw)) rect.StrokeWidth = bw;
                // /IC (interior colour) present ⇒ filled. (GetColor(INTERIOR) returns a default
                // even when /IC is absent, so test the key directly.)
                rect.Filled = FPDFAnnot_HasKey(annot, "IC");
                return rect;
            }
            case FPDF_ANNOT_INK:
            {
                var points = ReadInk(annot, cropLeft, cropBottom, visibleHeight);
                if (points.Count < 2) return null;
                var ink = new FreehandAnnotation { Points = points };
                if (FPDFAnnot_GetBorder(annot, out _, out _, out float bw) && bw > 0) ink.StrokeWidth = bw;
                return ink;
            }

            // Link → IPdfLinkService; Popup → companion of its parent; everything else
            // is preserved untouched on save but not surfaced as an editable annotation.
            default:
                return null;
        }
    }

    /// <summary>Reads QuadPoints into page-space rectangles for text-markup annotations.</summary>
    private static List<HighlightRect> ReadQuads(IntPtr annot,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var rects = new List<HighlightRect>();
        nuint quadCount = FPDFAnnot_CountAttachmentPoints(annot);
        for (nuint q = 0; q < quadCount; q++)
        {
            if (!FPDFAnnot_GetAttachmentPoints(annot, q, out FsQuadPointsF quad))
                continue;

            // Quad ordering can vary; derive the bounds defensively.
            float pdfLeft = Math.Min(Math.Min(quad.X1, quad.X2), Math.Min(quad.X3, quad.X4));
            float pdfRight = Math.Max(Math.Max(quad.X1, quad.X2), Math.Max(quad.X3, quad.X4));
            float pdfBottom = Math.Min(Math.Min(quad.Y1, quad.Y2), Math.Min(quad.Y3, quad.Y4));
            float pdfTop = Math.Max(Math.Max(quad.Y1, quad.Y2), Math.Max(quad.Y3, quad.Y4));

            var (x, yTop) = PdfPointToPage(pdfLeft, pdfTop, cropLeft, cropBottom, visibleHeight);
            rects.Add(new HighlightRect(x, yTop, pdfRight - pdfLeft, pdfTop - pdfBottom));
        }
        return rects;
    }

    /// <summary>Reads /Rect into page-space (X,Y top-left, W, H). Normalises inverted rects.</summary>
    private static (float X, float Y, float W, float H) ReadRect(IntPtr annot,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        if (!FPDFAnnot_GetRect(annot, out FsRectF r))
            return (0, 0, 0, 0);

        float pdfLeft = Math.Min(r.Left, r.Right);
        float pdfRight = Math.Max(r.Left, r.Right);
        float pdfBottom = Math.Min(r.Bottom, r.Top);
        float pdfTop = Math.Max(r.Bottom, r.Top);

        var (x, yTop) = PdfPointToPage(pdfLeft, pdfTop, cropLeft, cropBottom, visibleHeight);
        return (x, yTop, pdfRight - pdfLeft, pdfTop - pdfBottom);
    }

    private static List<PointF> ReadInk(IntPtr annot,
        float cropLeft, float cropBottom, double visibleHeight)
    {
        var points = new List<PointF>();
        int pathCount = FPDFAnnot_GetInkListCount(annot);
        for (uint p = 0; p < pathCount; p++)
        {
            uint n = FPDFAnnot_GetInkListPath(annot, p, null, 0);
            if (n == 0) continue;
            var buffer = new FsPointF[n];
            FPDFAnnot_GetInkListPath(annot, p, buffer, n);
            foreach (var pt in buffer)
            {
                var (x, y) = PdfPointToPage(pt.X, pt.Y, cropLeft, cropBottom, visibleHeight);
                points.Add(new PointF(x, y));
            }
        }
        return points;
    }

    private static void ApplyCommonMetadata(IntPtr annot, Annotation a)
    {
        a.Source = AnnotationSource.InPdf;
        a.Author = ReadAnnotString(annot, "T");
        a.Subject = ReadAnnotString(annot, "Subj");
        a.NativeId = ReadAnnotString(annot, "NM");
        a.CreatedUtc = ParsePdfDate(ReadAnnotString(annot, "CreationDate"));
        a.ModifiedUtc = ParsePdfDate(ReadAnnotString(annot, "M"));
        a.State = ParseReviewState(ReadAnnotString(annot, "StateModel"), ReadAnnotString(annot, "State"));

        // /Contents is the note body. Text-markup annotations leave it null when the
        // reviewer attached no comment; FreeText/Text always carry it.
        a.Contents = ReadAnnotString(annot, "Contents");

        // Opacity: /CA (constant alpha). Default opaque when absent.
        a.Opacity = FPDFAnnot_GetNumberValue(annot, "CA", out float ca) ? ca : 1.0f;

        // Colour: /C via PDFium. Returns false when the annotation carries a baked
        // /AP appearance stream (true for most Acrobat output) — in that case leave
        // ColorComponents null and fall back to a per-subtype default colour.
        if (FPDFAnnot_GetColor(annot, FPDFANNOT_COLORTYPE_COLOR,
                out uint r, out uint g, out uint b, out _))
        {
            a.ColorComponents = [r / 255f, g / 255f, b / 255f];
            a.Color = $"#{r:X2}{g:X2}{b:X2}";
        }
        else
        {
            a.Color = DefaultColorFor(a);
        }

        // Reply linkage: /IRT points at the parent annotation; carry its /NM.
        IntPtr parent = FPDFAnnot_GetLinkedAnnot(annot, "IRT");
        if (parent != IntPtr.Zero)
        {
            try { a.InReplyTo = ReadAnnotString(parent, "NM"); }
            finally { FPDFPage_CloseAnnot(parent); }
        }
    }

    private static string DefaultColorFor(Annotation a) => a switch
    {
        HighlightAnnotation => "#FFFF00", // yellow
        UnderlineAnnotation or SquigglyAnnotation => "#00A000", // green
        StrikeOutAnnotation => "#FF0000", // red
        _ => "#FFD400", // Acrobat sticky-note yellow
    };

    private static ReviewState ParseReviewState(string? stateModel, string? state)
    {
        if (!string.Equals(stateModel, "Review", StringComparison.OrdinalIgnoreCase))
            return ReviewState.None;
        return state switch
        {
            "Accepted" => ReviewState.Accepted,
            "Rejected" => ReviewState.Rejected,
            "Cancelled" => ReviewState.Cancelled,
            "Completed" => ReviewState.Completed,
            _ => ReviewState.None,
        };
    }

    /// <summary>
    /// Parses a PDF date string ("D:YYYYMMDDHHmmSS±HH'mm'") into a DateTimeOffset.
    /// Tolerates truncated forms (date-only, no timezone). Returns null on failure.
    /// </summary>
    internal static DateTimeOffset? ParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        if (s.Length < 4) return null;

        try
        {
            int year = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
            int month = s.Length >= 6 ? int.Parse(s.AsSpan(4, 2), CultureInfo.InvariantCulture) : 1;
            int day = s.Length >= 8 ? int.Parse(s.AsSpan(6, 2), CultureInfo.InvariantCulture) : 1;
            int hour = s.Length >= 10 ? int.Parse(s.AsSpan(8, 2), CultureInfo.InvariantCulture) : 0;
            int min = s.Length >= 12 ? int.Parse(s.AsSpan(10, 2), CultureInfo.InvariantCulture) : 0;
            int sec = s.Length >= 14 ? int.Parse(s.AsSpan(12, 2), CultureInfo.InvariantCulture) : 0;

            var offset = TimeSpan.Zero;
            int tzStart = 14;
            if (s.Length > tzStart)
            {
                char sign = s[tzStart];
                if (sign is '+' or '-')
                {
                    // ±HH'mm'
                    var rest = s[(tzStart + 1)..].Replace("'", "");
                    int oh = rest.Length >= 2 ? int.Parse(rest.AsSpan(0, 2), CultureInfo.InvariantCulture) : 0;
                    int om = rest.Length >= 4 ? int.Parse(rest.AsSpan(2, 2), CultureInfo.InvariantCulture) : 0;
                    offset = new TimeSpan(oh, om, 0);
                    if (sign == '-') offset = -offset;
                }
                else if (sign == 'Z')
                {
                    offset = TimeSpan.Zero;
                }
            }

            return new DateTimeOffset(year, month, day, hour, min, sec, offset);
        }
        catch
        {
            return null;
        }
    }
}
