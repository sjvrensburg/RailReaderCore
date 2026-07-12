using System.Globalization;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public static class ColorUtils
{
    /// <summary>
    /// Parses a #RRGGBB or #AARRGGBB hex string to a ColorRGBA.
    /// For #RRGGBB, uses the provided alpha. For #AARRGGBB, combines
    /// the embedded alpha with the provided alpha multiplicatively.
    /// Falls back to yellow if the format is invalid.
    /// </summary>
    public static ColorRGBA ParseHexColor(string hex, byte alpha)
    {
        // TryParse (not Parse) so a right-length string with non-hex digits — e.g. "#GGHHII"
        // from a hand-edited/corrupted annotation sidecar — falls back to yellow as documented
        // instead of throwing FormatException on the render or PDF-save path.
        if (hex is { Length: 7 } && hex[0] == '#'
            && TryHexByte(hex, 1, out byte r6) && TryHexByte(hex, 3, out byte g6) && TryHexByte(hex, 5, out byte b6))
        {
            return new ColorRGBA(r6, g6, b6, alpha);
        }
        if (hex is { Length: 9 } && hex[0] == '#'
            && TryHexByte(hex, 1, out byte hexA) && TryHexByte(hex, 3, out byte r8)
            && TryHexByte(hex, 5, out byte g8) && TryHexByte(hex, 7, out byte b8))
        {
            byte a = (byte)(hexA * alpha / 255);
            return new ColorRGBA(r8, g8, b8, a);
        }
        return new ColorRGBA(255, 255, 0, alpha); // fallback yellow

        static bool TryHexByte(string s, int start, out byte value)
            => byte.TryParse(s.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Formats RGB components (each 0..1) as a <c>#RRGGBB</c> hex string — the inverse of
    /// <see cref="ParseHexColor"/>'s colour channels. Clamps to range and rounds to the
    /// nearest byte so callers don't each re-implement the float→hex conversion.
    /// </summary>
    public static string ToHexColor(float r, float g, float b)
    {
        static int Channel(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
    }
}
