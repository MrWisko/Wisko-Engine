using Wisko.Engine.Math;

namespace Wisko.Engine.Editor;

public readonly struct PaletteColor(string name, Vector3 rgb)
{
    public string Name { get; } = name;
    public Vector3 Rgb { get; } = rgb;
}

public static class PartColorPalette
{
    public static readonly PaletteColor[] Colors =
    [
        new("White", new Vector3(0.95f, 0.95f, 0.95f)),
        new("Black", new Vector3(0.12f, 0.12f, 0.12f)),
        new("Gray", new Vector3(0.55f, 0.55f, 0.58f)),
        new("Red", new Vector3(0.85f, 0.25f, 0.25f)),
        new("Green", new Vector3(0.30f, 0.78f, 0.35f)),
        new("Blue", new Vector3(0.28f, 0.45f, 0.92f)),
        new("Yellow", new Vector3(0.95f, 0.85f, 0.25f)),
        new("Brown", new Vector3(0.52f, 0.34f, 0.20f))
    ];
}
