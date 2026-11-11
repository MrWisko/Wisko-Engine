using Wisko.Engine.Math;

namespace Wisko.Engine.Graphics;

/// <summary>
/// sRGB and linear colour conversions (IEC 61966-2-1 transfer function), and the 0-255 grid Wisko stores colours on.
/// Every colour a person picks (Part.Color, material and light colours, "#RRGGBB" from Lua, Atmosphere colours) is an
/// sRGB display value; the Universal renderer lights in linear space, so it converts once on the CPU before upload.
/// </summary>
public static class ColorSpace
{
    public static float SrgbToLinear(float c)
    {
        if (!float.IsFinite(c))
        {
            return 0f;
        }

        c = global::System.Math.Clamp(c, 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    public static float LinearToSrgb(float c)
    {
        if (!float.IsFinite(c))
        {
            return 0f;
        }

        c = global::System.Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    public static Vector3 SrgbToLinear(Vector3 c) => new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z));

    public static Vector3 LinearToSrgb(Vector3 c) => new(LinearToSrgb(c.X), LinearToSrgb(c.Y), LinearToSrgb(c.Z));

    /// <summary>A colour channel on the 0-255 grid (non-finite: 0), so colours save and compare exactly.</summary>
    public static float Snap(float channel) =>
        float.IsFinite(channel) ? MathF.Round(global::System.Math.Clamp(channel, 0f, 1f) * 255f) / 255f : 0f;

    public static Vector3 Snap(Vector3 color) => new(Snap(color.X), Snap(color.Y), Snap(color.Z));

    public static (int R, int G, int B) ToBytes(Vector3 color) =>
        ((int)MathF.Round(Snap(color.X) * 255f), (int)MathF.Round(Snap(color.Y) * 255f), (int)MathF.Round(Snap(color.Z) * 255f));

    public static Vector3 FromBytes(int r, int g, int b) =>
        new(global::System.Math.Clamp(r, 0, 255) / 255f, global::System.Math.Clamp(g, 0, 255) / 255f, global::System.Math.Clamp(b, 0, 255) / 255f);

    /// <summary>"#RRGGBB" for a display colour.</summary>
    public static string ToHex(Vector3 color)
    {
        var (r, g, b) = ToBytes(color);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Parses "#RRGGBB" (or "RRGGBB"); false for anything else.</summary>
    public static bool TryParseHex(string? text, out Vector3 color)
    {
        color = Vector3.Zero;
        if (text is null)
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length != 6 || !int.TryParse(s, global::System.Globalization.NumberStyles.HexNumber, global::System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = FromBytes((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        return true;
    }
}
