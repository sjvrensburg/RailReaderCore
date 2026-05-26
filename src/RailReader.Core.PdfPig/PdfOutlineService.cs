using RailReader.Core.Models;
using RailReader.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace RailReader.Core.PdfPig;

/// <summary>
/// Pure-managed bookmark/outline extraction via UglyToad.PdfPig.
/// Container bookmarks (group-only, no destination) are emitted with a
/// null page; PDFium's outline extraction does the same thing.
/// </summary>
public sealed class PdfOutlineService : IPdfOutlineService
{
    public List<OutlineEntry> Extract(byte[] pdfBytes)
    {
        var result = new List<OutlineEntry>();

        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            if (!doc.TryGetBookmarks(out var bookmarks) || bookmarks is null)
                return result;

            foreach (var root in bookmarks.Roots)
                result.Add(ConvertNode(root));
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[PdfPig.Outline] Failed to extract", ex);
        }

        return result;
    }

    private static OutlineEntry ConvertNode(BookmarkNode node)
    {
        var entry = new OutlineEntry
        {
            Title = node.Title ?? "",
            Page = (node is DocumentBookmarkNode doc && doc.PageNumber > 0)
                ? doc.PageNumber - 1   // PdfPig is 1-indexed; Core is 0-indexed.
                : null,
        };

        foreach (var child in node.Children)
            entry.Children.Add(ConvertNode(child));

        return entry;
    }
}
