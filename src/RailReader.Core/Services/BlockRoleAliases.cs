using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Single source of truth for the friendly role vocabulary used by user-facing entry points
/// (CLI <c>--classes</c>, the demo control bus, etc.). A token like "figure" or "equation" maps
/// to the set of detector <see cref="BlockRole"/>s it covers, so the CLI and the GUI can't drift
/// (e.g. "figure" must include <see cref="BlockRole.Chart"/> everywhere, not just in one tool).
/// </summary>
public static class BlockRoleAliases
{
    /// <summary>
    /// Resolve a friendly token to the detector roles it covers, most-specific first (e.g.
    /// "equation" → DisplayMath, InlineMath, Algorithm). Unknown tokens fall back to a
    /// case-insensitive <see cref="BlockRole"/> enum-name parse, so raw role names work too;
    /// an unrecognised token yields an empty list.
    /// </summary>
    public static IReadOnlyList<BlockRole> Resolve(string? token) =>
        token?.Trim().ToLowerInvariant() switch
        {
            "figure" => [BlockRole.Figure, BlockRole.Chart],
            "equation" or "math" => [BlockRole.DisplayMath, BlockRole.InlineMath, BlockRole.Algorithm],
            "table" => [BlockRole.Table],
            "heading" or "section" => [BlockRole.Heading],
            "title" or "doctitle" or "doc-title" => [BlockRole.Title],
            "caption" => [BlockRole.Caption],
            "text" or "paragraph" => [BlockRole.Text],
            _ => Enum.TryParse<BlockRole>(token, ignoreCase: true, out var role) ? [role] : [],
        };
}
