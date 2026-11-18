using System.Globalization;

namespace RailReader.Core.Services;

/// <summary>
/// The canonical form of an annotation colour used as a lookup key — for example by
/// <see cref="MarkdownExportOptions.ColourCategories"/>. Hosts that key their own
/// colour-name tables should normalise with the same rule so their keys match Core's.
/// </summary>
public static class ColourKey
{
    /// <summary>
    /// Normalises a hex colour to upper-case <c>#RRGGBB</c>: trims, upper-cases, adds a
    /// missing leading <c>#</c>, expands <c>#RGB</c>, and drops the alpha of an
    /// <c>#AARRGGBB</c> colour (Core's annotation colour format, see
    /// <see cref="ColorUtils.ParseHexColor"/>). Returns null for anything that is not a
    /// 3-, 6- or 8-digit hex colour, so an unparseable colour never matches a key.
    /// </summary>
    public static string? Normalise(string? colour)
    {
        if (colour is null) return null;
        var s = colour.Trim();
        if (s.StartsWith('#')) s = s[1..];

        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }

        s = s.ToUpper(CultureInfo.InvariantCulture);
        return s.Length switch
        {
            3 => $"#{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}",
            6 => "#" + s,
            8 => "#" + s[2..],
            _ => null,
        };
    }
}
