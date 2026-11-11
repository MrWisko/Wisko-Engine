using Wisko.Engine.Math;

namespace Wisko.Engine.Atmosphere;

/// <summary>The camera as the sky and distance effects need it (world space).</summary>
public readonly record struct AtmosphereView(Vector3 CameraPosition, Vector3 Forward, Vector3 Right, Vector3 Up, float VerticalFovRadians, float Aspect);

/// <summary>
/// Packs the environment into the 13 vec4 shader parameters (<c>u_atmo</c>) that the OpenGL and bgfx world shaders
/// share through atmosphere.sh; that file documents the same layout. With Atmosphere disabled, the plain environment
/// is packed: exactly the constants of the renderer before Atmosphere (flat sky, the same light and ambient), and
/// the shaders then reproduce that renderer's output.
/// </summary>
public static class AtmosphereUniforms
{
    public const int Vec4Count = 13;
    public const int FloatCount = Vec4Count * 4;

    /// <summary>Plain environment: the direction light travels (as before Atmosphere).</summary>
    public static readonly Vector3 PlainLightDirection = new Vector3(-0.45f, -0.8f, -0.4f).Normalized();

    public static readonly Vector3 PlainLightColor = new(1.0f, 0.97f, 0.92f);

    public const float PlainAmbient = 0.32f;

    /// <summary>Plain environment sky (the former clear colour).</summary>
    public static readonly Vector3 PlainSkyColor = new(0.58f, 0.78f, 0.96f);

    // Angular radius of the sun disc + glow at Disc Size 1: the former billboard (radius 90 at distance 500).
    private const float SunDiscRadius = 0.17809f;
    private const float HazeDistance = 600f;
    private const float HaloStrength = 0.006f;
    private const float ShoulderKnee = 0.8f;

    public static float[] Create() => new float[FloatCount];

    public static void Write(AtmosphereSettings settings, in AtmosphereView view, Span<float> u)
    {
        if (u.Length < FloatCount)
        {
            throw new ArgumentException($"Need {FloatCount} floats.", nameof(u));
        }

        var tanY = MathF.Tan(view.VerticalFovRadians * 0.5f);
        var tanX = tanY * view.Aspect;
        Set(u, 7, view.CameraPosition, settings.Enabled ? 1f : 0f);
        Set(u, 9, view.Right * tanX, 0f);
        Set(u, 10, view.Up * tanY, 0f);
        Set(u, 11, view.Forward, 0f);

        if (!settings.Enabled)
        {
            var plainSun = new Vector3(-PlainLightDirection.X, -PlainLightDirection.Y, -PlainLightDirection.Z);
            Set(u, 0, plainSun, SunDiscRadius);
            Set(u, 1, PlainLightColor, 0f);
            Set(u, 2, new Vector3(PlainAmbient, PlainAmbient, PlainAmbient), 1f);
            Set(u, 3, PlainSkyColor, 1f);
            Set(u, 4, PlainSkyColor, 1f);
            Set(u, 5, Vector3.Zero, 0f);
            u[24] = 0f;
            u[25] = 1f;
            u[26] = 0f;
            u[27] = HazeDistance;
            Set(u, 8, new Vector3(1f, 1f, 1f), 1f);
            Set(u, 12, new Vector3(1f, 1f, 1f), 1f);
            return;
        }

        var s = settings;
        var sun = s.SunDirectionToSun();
        var sunLight = s.SunColor.ToVector3() * s.SunIntensity;
        var ambient = s.AmbientColor.ToVector3() * s.AmbientIntensity;
        Set(u, 0, sun, SunDiscRadius * s.SunDiscSize);
        Set(u, 1, sunLight, HaloStrength);
        Set(u, 2, ambient, MathF.Pow(2f, s.Exposure));
        Set(u, 3, s.SkyColor.ToVector3() * s.SkyIntensity, s.Saturation);
        Set(u, 4, s.HorizonColor.ToVector3() * s.SkyIntensity, s.Contrast);
        Set(u, 5, s.FogColor.ToVector3(), s.FogEnabled ? s.FogDensity : 0f);
        u[24] = s.FogStart;
        u[25] = global::System.Math.Max(s.FogEnd, s.FogStart + 1f);
        u[26] = s.Haze;
        u[27] = HazeDistance;
        Set(u, 8, GroundLight(ambient, sunLight, sun), ShoulderKnee);

        // Disc: the sun colour at full brightness (its brightest channel 1), dimmer for a weak sun, hidden at 0.
        var c = s.SunColor;
        var peak = MathF.Max(c.R, MathF.Max(c.G, c.B));
        var disc = peak > 1e-3f ? c.ToVector3() / peak : new Vector3(1f, 1f, 1f);
        Set(u, 12, disc * global::System.Math.Clamp(0.55f + 0.45f * s.SunIntensity, 0f, 1.6f), global::System.Math.Clamp(s.SunIntensity * 2f, 0f, 1f));
    }

    /// <summary>
    /// How bright the ground is now relative to the plain environment, per channel: the baked environment colours
    /// (the baseplate) are multiplied by it, so they darken at night and take the light's tint (1 = unchanged).
    /// </summary>
    public static Vector3 GroundLight(Vector3 ambient, Vector3 sunLight, Vector3 towardSun)
    {
        var plainUp = -PlainLightDirection.Y;
        var plain = new Vector3(PlainAmbient, PlainAmbient, PlainAmbient) + PlainLightColor * plainUp;
        var lit = ambient + sunLight * MathF.Max(towardSun.Y, 0f);
        return new Vector3(lit.X / plain.X, lit.Y / plain.Y, lit.Z / plain.Z);
    }

    private static void Set(Span<float> u, int index, Vector3 xyz, float w)
    {
        u[index * 4] = xyz.X;
        u[index * 4 + 1] = xyz.Y;
        u[index * 4 + 2] = xyz.Z;
        u[index * 4 + 3] = w;
    }
}
