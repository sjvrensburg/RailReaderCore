using RailReader.Core.Models;
using RailReader.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;

namespace RailReader.Core.PdfPig;

/// <summary>
/// Pure-managed link extraction via UglyToad.PdfPig. Walks the page's
/// link-type annotations and translates each into a <see cref="PdfLink"/>
/// in page-point space (origin top-left, Y-down).
/// </summary>
public sealed class PdfLinkService : IPdfLinkService
{
    private static readonly List<PdfLink> s_empty = [];

    public List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, string? password = null)
        => ExtractPageLinks(pdfBytes, pageIndex, 0, password);

    public List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex, int viewRotation, string? password = null)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes, PdfPigOpen.Options(password));
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return s_empty;

            var page = doc.GetPage(pageIndex + 1);
            double pageH = page.Height;
            var links = new List<PdfLink>();

            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Link) continue;

                var dest = ResolveDestination(ann.Action);
                if (dest is null) continue;

                // Corner-normalise: like letter boxes, annotation rects on
                // /Rotate pages are oriented and can come back Left>Right.
                var r = ann.Rectangle;
                double minX = Math.Min(Math.Min(r.TopLeft.X, r.TopRight.X), Math.Min(r.BottomLeft.X, r.BottomRight.X));
                double maxX = Math.Max(Math.Max(r.TopLeft.X, r.TopRight.X), Math.Max(r.BottomLeft.X, r.BottomRight.X));
                double minY = Math.Min(Math.Min(r.TopLeft.Y, r.TopRight.Y), Math.Min(r.BottomLeft.Y, r.BottomRight.Y));
                double maxY = Math.Max(Math.Max(r.TopLeft.Y, r.TopRight.Y), Math.Max(r.BottomLeft.Y, r.BottomRight.Y));
                var rect = new RectF((float)minX, (float)(pageH - maxY), (float)maxX, (float)(pageH - minY));
                rect = ViewRotationMath.RotateRect(rect, page.Width, pageH, viewRotation);

                links.Add(new PdfLink { Rect = rect, Destination = dest });
            }

            return links;
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"[PdfPig.Link] Failed to extract links for page {pageIndex}", ex);
            return s_empty;
        }
    }

    private static PdfLinkDestination? ResolveDestination(PdfAction? action)
    {
        switch (action)
        {
            case UriAction uri when !string.IsNullOrWhiteSpace(uri.Uri):
                return new UriDestination { Uri = uri.Uri };

            case GoToAction go when go.Destination is { PageNumber: > 0 } d:
                float? x = null, y = null;
                if (d.Coordinates is { } c)
                {
                    if (c.Left.HasValue) x = (float)c.Left.Value;
                    if (c.Top.HasValue)  y = (float)c.Top.Value;
                }
                return new PageDestination { PageIndex = d.PageNumber - 1, PdfX = x, PdfY = y };

            default:
                return null;
        }
    }
}
