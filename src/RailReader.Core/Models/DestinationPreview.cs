namespace RailReader.Core.Models;

/// <summary>
/// What a link destination would show, for a host-drawn preview ("peek") of a link before following it.
/// Returned by <c>DocumentController.ResolveDestinationPreview</c>.
/// </summary>
/// <param name="Page">Destination page index.</param>
/// <param name="Rect">Region to preview, in the destination page's page-point space (origin top-left, Y-down).</param>
/// <param name="BlockIndex">Page-level index of the layout block <paramref name="Rect"/> was taken from, or
/// null when the page had no cached analysis (or no block at the destination) and <paramref name="Rect"/>
/// is a fixed-height band.</param>
public readonly record struct DestinationPreview(int Page, RectF Rect, int? BlockIndex);
