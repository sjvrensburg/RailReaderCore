using RailReader.Core.Models;
using RailReader.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using static UglyToad.PdfPig.Core.PdfSubpath;

namespace RailReader.Core.PdfPig;

/// <summary>
/// Vector ruling-line extraction — the <see cref="IPdfRulingService"/> half of the text
/// service. It lives on the same type because the two need exactly the same inputs and both
/// describe a page's structure, and because Core discovers the capability by casting the text
/// service (so no consumer has to change its wiring to gain it).
///
/// <para>
/// Approach adapted from tabula-java's <c>ObjectExtractorStreamEngine</c>: walk the page's
/// paths, keep the subpaths that are drawn (stroked or filled) and consist only of straight
/// segments, and turn each segment into a ruling. Curved subpaths are skipped outright — a
/// Bézier is not a table rule, and its control points would otherwise contribute spurious
/// straight chords.
/// </para>
/// </summary>
public sealed partial class PdfTextService : IPdfRulingService
{
    /// <summary>
    /// Maximum deviation, in points, for a segment to count as axis-aligned. Generous enough
    /// to accept rules that are a hair off square from a rounded transform, tight enough that
    /// a genuinely diagonal line (a strike-through, a chart's trend line) is not mistaken for
    /// a table rule.
    /// </summary>
    private const double AxisAlignedTolerance = 0.25;

    /// <summary>Rules shorter than this are decoration or glyph fragments, not table structure.</summary>
    private const double MinRulingLength = 2.0;

    /// <summary>
    /// Distance within which two parallel rulings at the same position are treated as one.
    /// A hairline drawn as a thin filled rectangle contributes both of its long edges, a
    /// fraction of a point apart, and a table's rules are often re-drawn per row.
    /// </summary>
    private const double MergeTolerance = 1.5;

    public PageRulings ExtractRulings(byte[] pdfBytes, int pageIndex, string? password = null)
        => ExtractRulings(pdfBytes, pageIndex, 0, password);

    public PageRulings ExtractRulings(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes, PdfPigOpen.Options(password));
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return PageRulings.Empty;

            var page = doc.GetPage(pageIndex + 1);
            return BuildRulings(page, viewRotation);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[PdfPig.Rulings] Failed to extract rulings for page {pageIndex}", ex);
            return PageRulings.Empty;
        }
    }

    private static PageRulings BuildRulings(UglyToad.PdfPig.Content.Page page, int viewRotation)
    {
        double pageH = page.Height;
        var vertical = new List<RulingSegment>();
        var horizontal = new List<RulingSegment>();

        foreach (var path in page.Paths)
        {
            // Neither stroked nor filled means the subpath was used for clipping or was
            // discarded — it draws nothing, so it rules nothing.
            if (!path.IsFilled && !path.IsStroked) continue;

            foreach (var subpath in path)
            {
                if (subpath.Commands.Count == 0) continue;
                if (subpath.Commands[0] is not Move first) continue;

                bool curved = false;
                foreach (var c in subpath.Commands)
                    if (c is BezierCurve) { curved = true; break; }
                if (curved) continue;

                PdfPoint start = first.Location, cursor = first.Location, subpathStart = first.Location;

                foreach (var command in subpath.Commands)
                {
                    switch (command)
                    {
                        case Move move:
                            cursor = subpathStart = move.Location;
                            break;

                        case Line line:
                            start = cursor;
                            AddSegment(start, line.To, pageH, page, viewRotation, vertical, horizontal);
                            cursor = line.To;
                            break;

                        // Close draws the implicit segment back to the most recent move —
                        // which for a filled rectangle is one of its four sides.
                        case Close:
                            AddSegment(cursor, subpathStart, pageH, page, viewRotation, vertical, horizontal);
                            cursor = subpathStart;
                            break;
                    }
                }
            }
        }

        return new PageRulings(Merge(vertical), Merge(horizontal));
    }

    /// <summary>
    /// Classifies one straight segment and, if it is axis-aligned and long enough, records it
    /// in the displayed frame. Conversion runs through the same flip-and-rotate the text path
    /// uses, so rulings and char boxes always land in one coordinate space.
    /// </summary>
    private static void AddSegment(PdfPoint a, PdfPoint b, double pageH,
        UglyToad.PdfPig.Content.Page page, int viewRotation,
        List<RulingSegment> vertical, List<RulingSegment> horizontal)
    {
        // PdfPig is bottom-left origin, Y-up; Core is top-left, Y-down.
        float x1 = (float)a.X, y1 = (float)(pageH - a.Y);
        float x2 = (float)b.X, y2 = (float)(pageH - b.Y);

        bool isVertical = Math.Abs(x1 - x2) <= AxisAlignedTolerance;
        bool isHorizontal = Math.Abs(y1 - y2) <= AxisAlignedTolerance;
        // A degenerate segment satisfies both; a diagonal satisfies neither.
        if (isVertical == isHorizontal) return;

        var rect = new RectF(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
        if (viewRotation != 0)
            rect = ViewRotationMath.RotateRect(rect, page.Width, page.Height, viewRotation);

        // Re-classify after rotation: an odd quarter-turn swaps the axes.
        float w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= AxisAlignedTolerance && h >= MinRulingLength)
            vertical.Add(new RulingSegment((rect.Left + rect.Right) * 0.5f, rect.Top, rect.Bottom));
        else if (h <= AxisAlignedTolerance && w >= MinRulingLength)
            horizontal.Add(new RulingSegment((rect.Top + rect.Bottom) * 0.5f, rect.Left, rect.Right));
    }

    /// <summary>
    /// Collapses rulings that describe the same line: same position within
    /// <see cref="MergeTolerance"/>, and extents that touch or overlap. Without this a single
    /// hairline drawn as a filled rectangle would present as two separate column separators a
    /// fraction of a point apart, and a rule redrawn per table row as dozens of stubs.
    /// </summary>
    private static List<RulingSegment> Merge(List<RulingSegment> segments)
    {
        if (segments.Count <= 1) return segments;

        segments.Sort((p, q) => p.Position != q.Position
            ? p.Position.CompareTo(q.Position)
            : p.Start.CompareTo(q.Start));

        var merged = new List<RulingSegment>(segments.Count);
        var current = segments[0];

        foreach (var s in segments.Skip(1))
        {
            bool samePosition = Math.Abs(s.Position - current.Position) <= MergeTolerance;
            bool extentsMeet = s.Start <= current.End + MergeTolerance;

            if (samePosition && extentsMeet)
            {
                // Keep the position of the longer contributor: a long rule's own coordinate is
                // more trustworthy than a stub that happens to sit beside it.
                float newEnd = Math.Max(current.End, s.End);
                float position = (s.End - s.Start) > (current.End - current.Start) ? s.Position : current.Position;
                current = new RulingSegment(position, current.Start, newEnd);
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
