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

    /// <summary>
    /// Block roles that park semi-automatic auto-scroll on entry (it waits for an explicit
    /// advance keypress). Non-prose units whose "am I done?" decision has high, content-
    /// dependent variance no timer can guess. Prose roles (<see cref="BlockRole.Text"/>,
    /// <see cref="BlockRole.Caption"/>, <see cref="BlockRole.Aside"/>,
    /// <see cref="BlockRole.Reference"/>, <see cref="BlockRole.Footnote"/>) flow through.
    /// </summary>
    public static IReadOnlySet<BlockRole> AutoScrollStop { get; } = new HashSet<BlockRole>
    {
        BlockRole.Heading,
        BlockRole.Title,
        BlockRole.DisplayMath,
        BlockRole.Algorithm,
        BlockRole.Table,
        BlockRole.Figure,
        BlockRole.Chart,
    };
}
