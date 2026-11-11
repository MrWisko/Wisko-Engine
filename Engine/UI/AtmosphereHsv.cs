using Wisko.Engine.Atmosphere;

namespace Wisko.Engine.UI;

/// <summary>Hue/saturation/brightness of the colour being edited in a property sheet (all 0-1).</summary>
internal readonly record struct AtmosphereHsv(float H, float S, float V)
{
    public static AtmosphereHsv FromColor(AtmosphereColor color)
    {
        var max = MathF.Max(color.R, MathF.Max(color.G, color.B));
        var min = MathF.Min(color.R, MathF.Min(color.G, color.B));
        var delta = max - min;
        var hue = 0f;
        if (delta > 1e-6f)
        {
            hue = max == color.R ? (color.G - color.B) / delta % 6f
                : max == color.G ? (color.B - color.R) / delta + 2f
                : (color.R - color.G) / delta + 4f;
            hue /= 6f;
            if (hue < 0f)
            {
                hue += 1f;
            }
        }

        return new AtmosphereHsv(hue, max > 1e-6f ? delta / max : 0f, max);
    }

    public AtmosphereColor ToColor()
    {
        var (r, g, b) = Rgb(H, S, V);
        return AtmosphereColor.FromRgb(r, g, b);
    }

    public static (float R, float G, float B) Rgb(float h, float s, float v)
    {
        h = (h - MathF.Floor(h)) * 6f;
        var c = v * s;
        var x = c * (1f - MathF.Abs(h % 2f - 1f));
        var m = v - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        return (r + m, g + m, b + m);
    }
}
