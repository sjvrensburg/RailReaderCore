using RailReader.Core;
using RailReader.Core.Models;
using static RailReader.Core.Services.PdfiumNative;

namespace RailReader.Core.Services;

/// <summary>
/// Vector ruling-line extraction for the PDFium backend — the <see cref="IPdfRulingService"/>
/// half of the text service, mirroring what <c>RailReader.Core.PdfPig</c> provides for the
/// managed backend so a table's column grid comes from the lines the producer drew rather than
/// from dark pixel runs in the analysis pixmap.
///
/// <para>
/// PDFium exposes page content as a flat object list, so this walks it directly: every path
/// object that is actually drawn contributes its straight segments, and form XObjects are
/// descended into with their matrices composed (a table drawn inside a form is invisible to
/// <c>FPDFPage_GetObject</c> alone, which is common in LaTeX and Word output).
/// </para>
/// <para>
/// Segment points are in each object's own space, so the object matrix has to be applied
/// before anything else — without it, rules land wherever the identity transform happens to
/// put them, which is right only for content drawn with no CTM in effect.
/// </para>
/// </summary>
public sealed partial class PdfTextService : IPdfRulingService
{
    /// <summary>Maximum deviation, in points, for a segment to count as axis-aligned.</summary>
    private const float AxisAlignedTolerance = 0.25f;

    /// <summary>Rules shorter than this are decoration or glyph fragments, not table structure.</summary>
    private const float MinRulingLength = 2f;

    /// <summary>
    /// Distance within which two parallel rulings at the same position are one rule. A hairline
    /// drawn as a thin filled rectangle contributes both of its long edges, a fraction of a
    /// point apart.
    /// </summary>
    private const float MergeTolerance = 1.5f;

    /// <summary>
    /// Depth limit when descending form XObjects. Forms nest legitimately but shallowly; the
    /// bound stops a malformed or cyclic document from recursing without end.
    /// </summary>
    private const int MaxFormDepth = 8;

    public PageRulings ExtractRulings(byte[] pdfBytes, int pageIndex, string? password = null)
        => ExtractRulings(pdfBytes, pageIndex, 0, password);

    public PageRulings ExtractRulings(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
    {
        lock (PdfiumGate.Lock)
        {
            PdfiumResolver.EnsureLibraryInitialized();

            IntPtr page = IntPtr.Zero;
            try
            {
                IntPtr doc = _docCache.GetOrLoad(pdfBytes, password);
                if (doc == IntPtr.Zero) return PageRulings.Empty;

                page = FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero) return PageRulings.Empty;

                var tx = GetPageTransform(page, viewRotation);
                var vertical = new List<RulingSegment>();
                var horizontal = new List<RulingSegment>();

                int count = FPDFPage_CountObjects(page);
                for (int i = 0; i < count; i++)
                    CollectFromObject(FPDFPage_GetObject(page, i), FsMatrix.Identity, tx,
                        vertical, horizontal, depth: 0);

                return new PageRulings(Merge(vertical), Merge(horizontal));
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Error($"[PdfRulings] Failed to extract rulings for page {pageIndex}", ex);
                return PageRulings.Empty;
            }
            finally
            {
                if (page != IntPtr.Zero) FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Contributes one page object's rulings, descending into form XObjects. <paramref name="parent"/>
    /// is the transform accumulated from enclosing forms; an object's own matrix composes onto it.
    /// </summary>
    private static void CollectFromObject(IntPtr obj, in FsMatrix parent, in PageTransform tx,
        List<RulingSegment> vertical, List<RulingSegment> horizontal, int depth)
    {
        if (obj == IntPtr.Zero) return;

        int type = FPDFPageObj_GetType(obj);
        if (type != FPDF_PAGEOBJ_PATH && type != FPDF_PAGEOBJ_FORM) return;

        var own = FsMatrix.Identity;
        if (!FPDFPageObj_GetMatrix(obj, ref own)) own = FsMatrix.Identity;
        var matrix = own.Concat(parent);

        if (type == FPDF_PAGEOBJ_FORM)
        {
            if (depth >= MaxFormDepth) return;
            int inner = FPDFFormObj_CountObjects(obj);
            for (int i = 0; i < inner; i++)
                CollectFromObject(FPDFFormObj_GetObject(obj, (uint)i), matrix, tx,
                    vertical, horizontal, depth + 1);
            return;
        }

        // A path that is neither filled nor stroked draws nothing — it was used for clipping —
        // so it rules nothing.
        int fillMode = 0, stroke = 0;
        if (FPDFPath_GetDrawMode(obj, ref fillMode, ref stroke) && fillMode == 0 && stroke == 0)
            return;

        CollectFromPath(obj, matrix, tx, vertical, horizontal);
    }

    /// <summary>
    /// Turns one path object's straight segments into rulings. Segments are read into a buffer
    /// first so each subpath can be inspected as a whole: a subpath containing a Bézier is
    /// skipped outright, because a curve is not a table rule and the straight chords between
    /// its control points would otherwise be recorded as if it were.
    /// </summary>
    private static void CollectFromPath(IntPtr path, in FsMatrix matrix, in PageTransform tx,
        List<RulingSegment> vertical, List<RulingSegment> horizontal)
    {
        int segmentCount = FPDFPath_CountSegments(path);
        if (segmentCount < 2) return;

        var types = new int[segmentCount];
        var points = new (float X, float Y)[segmentCount];
        var closes = new bool[segmentCount];

        for (int i = 0; i < segmentCount; i++)
        {
            IntPtr segment = FPDFPath_GetPathSegment(path, i);
            if (segment == IntPtr.Zero) { types[i] = FPDF_SEGMENT_UNKNOWN; continue; }

            types[i] = FPDFPathSegment_GetType(segment);
            float x = 0, y = 0;
            points[i] = FPDFPathSegment_GetPoint(segment, ref x, ref y)
                ? matrix.Apply(x, y)
                : (0f, 0f);
            closes[i] = FPDFPathSegment_GetClose(segment);
        }

        int start = 0;
        while (start < segmentCount)
        {
            // A subpath runs from one MOVETO to just before the next.
            int end = start + 1;
            while (end < segmentCount && types[end] != FPDF_SEGMENT_MOVETO) end++;

            bool curved = false;
            for (int i = start; i < end; i++)
                if (types[i] == FPDF_SEGMENT_BEZIERTO) { curved = true; break; }

            if (!curved && types[start] == FPDF_SEGMENT_MOVETO)
            {
                var subpathStart = points[start];
                var cursor = subpathStart;

                for (int i = start + 1; i < end; i++)
                {
                    if (types[i] != FPDF_SEGMENT_LINETO) continue;
                    AddSegment(cursor, points[i], tx, vertical, horizontal);
                    cursor = points[i];
                    // Close draws the implicit segment back to the subpath's first point —
                    // for a filled rectangle, that is one of its four sides.
                    if (closes[i]) AddSegment(cursor, subpathStart, tx, vertical, horizontal);
                }
            }

            start = end;
        }
    }

    /// <summary>
    /// Classifies one straight segment in the displayed frame and records it if it is
    /// axis-aligned and long enough. Conversion runs through <see cref="PageTransform"/>, the
    /// same path char boxes take, so rulings and glyphs share one coordinate space.
    /// </summary>
    private static void AddSegment((float X, float Y) a, (float X, float Y) b, in PageTransform tx,
        List<RulingSegment> vertical, List<RulingSegment> horizontal)
    {
        var (x1, y1) = tx.PdfToPage(a.X, a.Y);
        var (x2, y2) = tx.PdfToPage(b.X, b.Y);

        float left = Math.Min(x1, x2), right = Math.Max(x1, x2);
        float top = Math.Min(y1, y2), bottom = Math.Max(y1, y2);
        float w = right - left, h = bottom - top;

        if (w <= AxisAlignedTolerance && h >= MinRulingLength)
            vertical.Add(new RulingSegment((left + right) * 0.5f, top, bottom));
        else if (h <= AxisAlignedTolerance && w >= MinRulingLength)
            horizontal.Add(new RulingSegment((top + bottom) * 0.5f, left, right));
    }

    /// <summary>
    /// Collapses rulings describing the same line: same position within
    /// <see cref="MergeTolerance"/> and extents that touch or overlap. Without it a hairline
    /// drawn as a filled rectangle presents as two separate separators, and a rule redrawn per
    /// table row as dozens of stubs.
    /// </summary>
    private static List<RulingSegment> Merge(List<RulingSegment> segments)
    {
        if (segments.Count <= 1) return segments;

        segments.Sort((p, q) => p.Position != q.Position
            ? p.Position.CompareTo(q.Position)
            : p.Start.CompareTo(q.Start));

        var merged = new List<RulingSegment>(segments.Count);
        var current = segments[0];

        for (int i = 1; i < segments.Count; i++)
        {
            var s = segments[i];
            bool samePosition = Math.Abs(s.Position - current.Position) <= MergeTolerance;
            bool extentsMeet = s.Start <= current.End + MergeTolerance;

            if (samePosition && extentsMeet)
            {
                // Keep the longer contributor's position: a full-height rule's own coordinate
                // is more trustworthy than a stub that happens to sit beside it.
                float end = Math.Max(current.End, s.End);
                float position = (s.End - s.Start) > (current.End - current.Start) ? s.Position : current.Position;
                current = new RulingSegment(position, current.Start, end);
            }
            else
            {
                merged.Add(current);
                current = s;
            }
        }
        merged.Add(current);
        return merged;
    }
}
