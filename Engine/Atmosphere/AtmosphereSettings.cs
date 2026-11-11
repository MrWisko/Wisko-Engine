namespace Wisko.Engine.Atmosphere;

/// <summary>An Atmosphere colour on the 0-255 grid (stored as 0-1 floats), so it saves and loads exactly.</summary>
public readonly record struct AtmosphereColor(float R, float G, float B)
{
    public static AtmosphereColor FromBytes(int r, int g, int b) => new(Channel(r), Channel(g), Channel(b));

    /// <summary>Any floats (NaN and infinities included) snapped onto the grid; non-finite channels become 0.</summary>
    public static AtmosphereColor FromRgb(float r, float g, float b) => new(Snap(r), Snap(g), Snap(b));

    public (int R, int G, int B) ToBytes() => (ToByte(R), ToByte(G), ToByte(B));

    public Engine.Math.Vector3 ToVector3() => new(R, G, B);

    public AtmosphereColor Sanitized() => FromRgb(R, G, B);

    private static float Channel(int value) => global::System.Math.Clamp(value, 0, 255) / 255f;

    private static float Snap(float value) => float.IsFinite(value) ? Channel(ToByte(value)) : 0f;

    private static int ToByte(float value) => float.IsFinite(value) ? (int)MathF.Round(global::System.Math.Clamp(value, 0f, 1f) * 255f) : 0;
}

/// <summary>The numeric Atmosphere settings (each one slider in the Environment & Graphics panel).</summary>
public enum AtmosphereValue
{
    SkyIntensity,
    SunIntensity,
    SunElevation,
    SunHeading,
    SunDiscSize,
    AmbientIntensity,
    FogDensity,
    FogStart,
    FogEnd,
    Haze,
    Exposure,
    Saturation,
    Contrast,

    /// <summary>Universal renderer: how strongly the sky is reflected by smooth and metallic surfaces.</summary>
    ReflectionIntensity
}

/// <summary>The Atmosphere colours.</summary>
public enum AtmosphereColorSlot
{
    Sky,
    Horizon,
    Sun,
    Ambient,
    Fog,

    /// <summary>Universal renderer, Sky ambient: the ambient light coming from below.</summary>
    Ground
}

/// <summary>Where ambient light (light not from the sun or a light) comes from; the Universal renderer only.</summary>
public enum AtmosphereAmbientMode
{
    /// <summary>The same Ambient colour from every direction (the Classic renderer's model).</summary>
    Flat,

    /// <summary>Ambient colour from above blending to the Ground colour from below, so undersides are darker.</summary>
    Sky
}

/// <summary>Allowed range and step of one <see cref="AtmosphereValue"/>; values are clamped and snapped to the step.</summary>
public readonly record struct AtmosphereRange(float Min, float Max, float Step)
{
    public float Clamp(float value, float fallback)
    {
        if (!float.IsFinite(value))
        {
            value = fallback;
        }

        var steps = MathF.Round(1f / Step);
        var snapped = MathF.Round(global::System.Math.Clamp(value, Min, Max) * steps) / steps;
        return global::System.Math.Clamp(snapped, Min, Max);
    }
}

/// <summary>
/// A project's environment look: sky, sun, ambient light, fog, distance haze and simple image controls. Plain data
/// with no renderer types. Every edit goes through <see cref="With(AtmosphereValue, float)"/> /
/// <see cref="With(AtmosphereColorSlot, AtmosphereColor)"/> or <see cref="Sanitized"/>, so values always stay in range,
/// on their step and finite (NaN and infinities fall back to the default). A preset is only a starting point: it
/// produces ordinary settings that stay fully editable.
/// </summary>
public sealed record AtmosphereSettings
{
    /// <summary>Disabled: the renderer uses Wisko's plain environment; these values are kept for re-enabling.</summary>
    public bool Enabled { get; init; } = true;

    public AtmosphereColor SkyColor { get; init; } = AtmosphereColor.FromBytes(78, 138, 226);

    public AtmosphereColor HorizonColor { get; init; } = AtmosphereColor.FromBytes(176, 208, 240);

    public float SkyIntensity { get; init; } = 1f;

    public AtmosphereColor SunColor { get; init; } = AtmosphereColor.FromBytes(255, 245, 228);

    public float SunIntensity { get; init; } = 1f;

    /// <summary>Degrees above the horizon (negative: just below it).</summary>
    public float SunElevation { get; init; } = 53f;

    /// <summary>Compass degrees around the vertical axis (0 = +Z, 90 = +X).</summary>
    public float SunHeading { get; init; } = 48f;

    /// <summary>Size of the visible sun disc and its glow (0 = no disc; the light stays).</summary>
    public float SunDiscSize { get; init; } = 1f;

    public AtmosphereColor AmbientColor { get; init; } = AtmosphereColor.FromBytes(204, 219, 255);

    public float AmbientIntensity { get; init; } = 0.36f;

    public bool FogEnabled { get; init; }

    public AtmosphereColor FogColor { get; init; } = AtmosphereColor.FromBytes(199, 216, 232);

    /// <summary>How much fog there is at and beyond <see cref="FogEnd"/> (0 = none, 1 = only fog colour).</summary>
    public float FogDensity { get; init; } = 0.5f;

    /// <summary>Distance (world units from the camera) where fog begins.</summary>
    public float FogStart { get; init; } = 120f;

    /// <summary>Distance where fog reaches <see cref="FogDensity"/>; always beyond <see cref="FogStart"/>.</summary>
    public float FogEnd { get; init; } = 800f;

    /// <summary>Distance haze: far geometry blends into the sky colour behind it (0 = off).</summary>
    public float Haze { get; init; } = 0.25f;

    /// <summary>Brightness in stops (EV): +1 doubles, -1 halves.</summary>
    public float Exposure { get; init; }

    public float Saturation { get; init; } = 1f;

    public float Contrast { get; init; } = 1f;

    /// <summary>Universal renderer (Classic ignores it): flat ambient, or sky above and ground below.</summary>
    public AtmosphereAmbientMode AmbientMode { get; init; } = AtmosphereAmbientMode.Sky;

    /// <summary>The ambient light from below in Sky ambient mode.</summary>
    public AtmosphereColor GroundColor { get; init; } = AtmosphereColor.FromBytes(120, 112, 104);

    /// <summary>Strength of sky reflections on smooth and metallic surfaces (0 none, 1 natural).</summary>
    public float ReflectionIntensity { get; init; } = 1f;

    /// <summary>Wisko's default look for a new project (also the Neutral preset and Reset to Default).</summary>
    public static AtmosphereSettings Default { get; } = new();

    public static IReadOnlyList<AtmosphereValue> Values { get; } = Enum.GetValues<AtmosphereValue>();

    public static IReadOnlyList<AtmosphereColorSlot> ColorSlots { get; } = Enum.GetValues<AtmosphereColorSlot>();

    public static AtmosphereRange RangeOf(AtmosphereValue value) => value switch
    {
        AtmosphereValue.SkyIntensity => new(0f, 3f, 0.01f),
        AtmosphereValue.SunIntensity => new(0f, 4f, 0.01f),
        AtmosphereValue.SunElevation => new(-10f, 90f, 1f),
        AtmosphereValue.SunHeading => new(0f, 359f, 1f),
        AtmosphereValue.SunDiscSize => new(0f, 3f, 0.01f),
        AtmosphereValue.AmbientIntensity => new(0f, 2f, 0.01f),
        AtmosphereValue.FogDensity => new(0f, 1f, 0.01f),
        AtmosphereValue.FogStart => new(0f, 1999f, 1f),
        AtmosphereValue.FogEnd => new(1f, 2000f, 1f),
        AtmosphereValue.Haze => new(0f, 1f, 0.01f),
        AtmosphereValue.Exposure => new(-3f, 3f, 0.05f),
        AtmosphereValue.Saturation => new(0f, 2f, 0.01f),
        AtmosphereValue.Contrast => new(0.5f, 1.5f, 0.01f),
        AtmosphereValue.ReflectionIntensity => new(0f, 2f, 0.01f),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public float Get(AtmosphereValue value) => value switch
    {
        AtmosphereValue.SkyIntensity => SkyIntensity,
        AtmosphereValue.SunIntensity => SunIntensity,
        AtmosphereValue.SunElevation => SunElevation,
        AtmosphereValue.SunHeading => SunHeading,
        AtmosphereValue.SunDiscSize => SunDiscSize,
        AtmosphereValue.AmbientIntensity => AmbientIntensity,
        AtmosphereValue.FogDensity => FogDensity,
        AtmosphereValue.FogStart => FogStart,
        AtmosphereValue.FogEnd => FogEnd,
        AtmosphereValue.Haze => Haze,
        AtmosphereValue.Exposure => Exposure,
        AtmosphereValue.Saturation => Saturation,
        AtmosphereValue.Contrast => Contrast,
        AtmosphereValue.ReflectionIntensity => ReflectionIntensity,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public AtmosphereColor Get(AtmosphereColorSlot slot) => slot switch
    {
        AtmosphereColorSlot.Sky => SkyColor,
        AtmosphereColorSlot.Horizon => HorizonColor,
        AtmosphereColorSlot.Sun => SunColor,
        AtmosphereColorSlot.Ambient => AmbientColor,
        AtmosphereColorSlot.Fog => FogColor,
        AtmosphereColorSlot.Ground => GroundColor,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
    };

    /// <summary>A copy with one value changed (clamped and snapped; Fog Start/End keep their order).</summary>
    public AtmosphereSettings With(AtmosphereValue value, float amount)
    {
        var changed = value switch
        {
            AtmosphereValue.SkyIntensity => this with { SkyIntensity = amount },
            AtmosphereValue.SunIntensity => this with { SunIntensity = amount },
            AtmosphereValue.SunElevation => this with { SunElevation = amount },
            AtmosphereValue.SunHeading => this with { SunHeading = amount },
            AtmosphereValue.SunDiscSize => this with { SunDiscSize = amount },
            AtmosphereValue.AmbientIntensity => this with { AmbientIntensity = amount },
            AtmosphereValue.FogDensity => this with { FogDensity = amount },
            AtmosphereValue.FogStart => this with { FogStart = amount },
            AtmosphereValue.FogEnd => this with { FogEnd = amount },
            AtmosphereValue.Haze => this with { Haze = amount },
            AtmosphereValue.Exposure => this with { Exposure = amount },
            AtmosphereValue.Saturation => this with { Saturation = amount },
            AtmosphereValue.Contrast => this with { Contrast = amount },
            AtmosphereValue.ReflectionIntensity => this with { ReflectionIntensity = amount },
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        return changed.Sanitized(movedEnd: value == AtmosphereValue.FogEnd);
    }

    public AtmosphereSettings With(AtmosphereColorSlot slot, AtmosphereColor color) => (slot switch
    {
        AtmosphereColorSlot.Sky => this with { SkyColor = color },
        AtmosphereColorSlot.Horizon => this with { HorizonColor = color },
        AtmosphereColorSlot.Sun => this with { SunColor = color },
        AtmosphereColorSlot.Ambient => this with { AmbientColor = color },
        AtmosphereColorSlot.Fog => this with { FogColor = color },
        AtmosphereColorSlot.Ground => this with { GroundColor = color },
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
    }).Sanitized();

    /// <summary>
    /// Every value in range, on its step and finite (non-finite numbers take the default), colours on the 0-255 grid,
    /// heading wrapped to 0-359 and Fog End beyond Fog Start. Idempotent.
    /// </summary>
    public AtmosphereSettings Sanitized() => Sanitized(movedEnd: false);

    private AtmosphereSettings Sanitized(bool movedEnd)
    {
        var defaults = Default;
        float Clean(AtmosphereValue value, float amount) => RangeOf(value).Clamp(amount, defaults.Get(value));

        var heading = float.IsFinite(SunHeading) ? SunHeading % 360f : defaults.SunHeading;
        if (heading < 0f)
        {
            heading += 360f;
        }

        heading = MathF.Round(heading) % 360f;
        var start = Clean(AtmosphereValue.FogStart, FogStart);
        var end = Clean(AtmosphereValue.FogEnd, FogEnd);
        if (end <= start)
        {
            // Dragging End below Start pushes Start down; anything else moves End past Start.
            if (movedEnd)
            {
                start = global::System.Math.Max(0f, end - 1f);
            }
            else
            {
                end = global::System.Math.Min(start + 1f, RangeOf(AtmosphereValue.FogEnd).Max);
            }
        }

        return new AtmosphereSettings
        {
            Enabled = Enabled,
            SkyColor = SkyColor.Sanitized(),
            HorizonColor = HorizonColor.Sanitized(),
            SkyIntensity = Clean(AtmosphereValue.SkyIntensity, SkyIntensity),
            SunColor = SunColor.Sanitized(),
            SunIntensity = Clean(AtmosphereValue.SunIntensity, SunIntensity),
            SunElevation = Clean(AtmosphereValue.SunElevation, SunElevation),
            SunHeading = Clean(AtmosphereValue.SunHeading, heading),
            SunDiscSize = Clean(AtmosphereValue.SunDiscSize, SunDiscSize),
            AmbientColor = AmbientColor.Sanitized(),
            AmbientIntensity = Clean(AtmosphereValue.AmbientIntensity, AmbientIntensity),
            FogEnabled = FogEnabled,
            FogColor = FogColor.Sanitized(),
            FogDensity = Clean(AtmosphereValue.FogDensity, FogDensity),
            FogStart = start,
            FogEnd = end,
            Haze = Clean(AtmosphereValue.Haze, Haze),
            Exposure = Clean(AtmosphereValue.Exposure, Exposure),
            Saturation = Clean(AtmosphereValue.Saturation, Saturation),
            Contrast = Clean(AtmosphereValue.Contrast, Contrast),
            AmbientMode = Enum.IsDefined(AmbientMode) ? AmbientMode : defaults.AmbientMode,
            GroundColor = GroundColor.Sanitized(),
            ReflectionIntensity = Clean(AtmosphereValue.ReflectionIntensity, ReflectionIntensity)
        };
    }

    /// <summary>Unit vector from the scene toward the sun (elevation and heading); light travels the opposite way.</summary>
    public Engine.Math.Vector3 SunDirectionToSun()
    {
        var elevation = SunElevation * MathF.PI / 180f;
        var heading = SunHeading * MathF.PI / 180f;
        return new Engine.Math.Vector3(
            MathF.Cos(elevation) * MathF.Sin(heading),
            MathF.Sin(elevation),
            MathF.Cos(elevation) * MathF.Cos(heading)).Normalized();
    }
}
