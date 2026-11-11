using Wisko.Engine.Math;

namespace Wisko.Engine.Graphics;

public enum LightType
{
    /// <summary>Shines in every direction from the Part's position, fading out by <see cref="PartLight.Range"/>.</summary>
    Point,

    /// <summary>A cone along the Part's Forward (local -Z), fading out by Range and towards the cone's edge.</summary>
    Spot,

    /// <summary>Parallel light along the Part's Forward from infinitely far (like the sun); position and range unused.</summary>
    Directional
}

/// <summary>
/// A light carried by a Part: the Part gives its position and direction, so lights are selected,
/// moved, rotated, grouped, saved, undone, copied into Play and scripted like any other object. Plain immutable data
/// shared by both backends; drawn by the Universal renderer (the Classic renderer and bgfx keep it but do not light
/// with it).
/// <para>Units: a white surface facing a light straight on reflects <c>Intensity / distance²</c> (in studs) of
/// it, and a directional light's Intensity is that brightness directly (the Atmosphere sun at intensity 1 lights a
/// white surface to 1). Point and spot light fall off with the square of the distance and are smoothly cut to zero at
/// <see cref="Range"/>, so a light never touches anything beyond its range.</para>
/// </summary>
public sealed record PartLight
{
    public const float MaxIntensity = 100_000f;
    public const float MinRange = 0.1f;
    public const float MaxRange = 1000f;

    public LightType Type { get; init; } = LightType.Point;

    public bool Enabled { get; init; } = true;

    /// <summary>sRGB display colour on the 0-255 grid.</summary>
    public Vector3 Color { get; init; } = new(1f, 1f, 1f);

    public float Intensity { get; init; } = 40f;

    /// <summary>Studs; nothing beyond it is lit (point and spot).</summary>
    public float Range { get; init; } = 20f;

    /// <summary>Spot: the full cone angle in degrees (1-179).</summary>
    public float Angle { get; init; } = 60f;

    /// <summary>Spot: the full angle of the fully lit inner cone (0 to <see cref="Angle"/>); softer towards the edge.</summary>
    public float InnerAngle { get; init; } = 40f;

    /// <summary>Whether it casts shadows (within the Graphics quality's shadow budget; see docs/graphics.md).</summary>
    public bool CastShadows { get; init; }

    /// <summary>A new light of a type, with that type's sensible starting values.</summary>
    public static PartLight Create(LightType type) => type switch
    {
        LightType.Spot => new PartLight { Type = LightType.Spot, Intensity = 80f, Range = 30f, Angle = 60f, InnerAngle = 40f, CastShadows = true },
        LightType.Directional => new PartLight { Type = LightType.Directional, Intensity = 1f },
        _ => new PartLight()
    };

    /// <summary>Every value in range and finite (non-finite: the default), colour on the 0-255 grid. Idempotent.</summary>
    public PartLight Sanitized()
    {
        var d = new PartLight();
        var angle = MathUtil.ClampFinite(Angle, 1f, 179f, d.Angle);
        return new PartLight
        {
            Type = Enum.IsDefined(Type) ? Type : LightType.Point,
            Enabled = Enabled,
            Color = ColorSpace.Snap(Color),
            Intensity = MathUtil.ClampFinite(Intensity, 0f, MaxIntensity, d.Intensity),
            Range = MathUtil.ClampFinite(Range, MinRange, MaxRange, d.Range),
            Angle = angle,
            InnerAngle = global::System.Math.Min(MathUtil.ClampFinite(InnerAngle, 0f, 179f, d.InnerAngle), angle),
            CastShadows = CastShadows
        };
    }
}
