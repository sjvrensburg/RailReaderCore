using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Built-in defaults for which <see cref="BlockRole"/> values rail-mode
/// navigates by and which are horizontally centered when narrower than the
/// viewport. Consumers can override via <see cref="Models.CoreSettings"/>.
/// </summary>
public static class DefaultRoleSets
{
    public static IReadOnlySet<BlockRole> Navigable { get; } = new HashSet<BlockRole>
    {
        BlockRole.Text,
        BlockRole.Heading,
        BlockRole.Title,
        BlockRole.Caption,
        BlockRole.DisplayMath,
        BlockRole.Algorithm,
        BlockRole.Footnote,
    };

    /// <summary>
    /// Block roles that are horizontally centred when narrower than the
    /// viewport. Heading-like roles are excluded — they look better
    /// left-aligned.
    /// </summary>
    public static IReadOnlySet<BlockRole> Centering { get; } = new HashSet<BlockRole>
    {
        BlockRole.Text,
        BlockRole.Caption,
        BlockRole.DisplayMath,
        BlockRole.Algorithm,
        BlockRole.Footnote,
    };
}
