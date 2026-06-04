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
        if (hex.Length == 7 && hex[0] == '#')
        {
            byte r = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);
            return new ColorRGBA(r, g, b, alpha);
        }
        if (hex.Length == 9 && hex[0] == '#')
        {
            byte hexA = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber);
            byte r = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.AsSpan(7, 2), NumberStyles.HexNumber);
            byte a = (byte)(hexA * alpha / 255);
            return new ColorRGBA(r, g, b, a);
        }
        return new ColorRGBA(255, 255, 0, alpha); // fallback yellow
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
