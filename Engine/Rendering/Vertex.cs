using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering;

public readonly struct Vertex
{
    // Position + color only: Normal defaults to zero, which the lit shader treats as "no vertex normal"
    // and falls back to its derivative flat-normal path. Existing call sites need no change.
    public Vertex(Vector3 position, Vector3 color)
        : this(position, color, Vector3.Zero)
    {
    }

    // Position + color + explicit smooth normal (sphere / cylinder sides / cylinder caps).
    public Vertex(Vector3 position, Vector3 color, Vector3 normal)
    {
        Position = position;
        Color = color;
        Normal = normal;
    }

    public Vector3 Position { get; }
    public Vector3 Color { get; }
    public Vector3 Normal { get; }
}
