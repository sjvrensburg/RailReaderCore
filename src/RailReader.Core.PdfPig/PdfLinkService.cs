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

    public List<PdfLink> ExtractPageLinks(byte[] pdfBytes, int pageIndex)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return s_empty;

            var page = doc.GetPage(pageIndex + 1);
            double pageH = page.Height;
            var links = new List<PdfLink>();

            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Link) continue;

                var dest = ResolveDestination(ann.Action);
                if (dest is null) continue;

                var r = ann.Rectangle;
                float top    = (float)(pageH - r.Top);
                float bottom = (float)(pageH - r.Bottom);
                var rect = new RectF((float)r.Left, top, (float)r.Right, bottom).Normalized();

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
