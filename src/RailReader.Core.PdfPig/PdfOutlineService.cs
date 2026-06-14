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
    public List<OutlineEntry> Extract(byte[] pdfBytes, string? password = null)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes, PdfPigOpen.Options(password));
            return Extract(doc);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[PdfPig.Outline] Failed to extract", ex);
            return [];
        }
    }

    /// <summary>
    /// Extracts the outline from an already-opened <see cref="PdfDocument"/>.
    /// Lets callers that keep a long-lived document instance (e.g. a renderer
    /// caching the parsed tree) avoid the round-trip of re-opening just for
    /// outline lookup.
    /// </summary>
    public List<OutlineEntry> Extract(PdfDocument doc)
    {
        var result = new List<OutlineEntry>();
        if (!doc.TryGetBookmarks(out var bookmarks) || bookmarks is null)
            return result;

        foreach (var root in bookmarks.Roots)
            result.Add(ConvertNode(root));

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
