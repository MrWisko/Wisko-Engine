using Wisko.Engine.Atmosphere;
using Wisko.Engine.Math;

namespace Wisko.Engine.Scripting;

/// <summary>
/// Camera writes from scripts, host side: Position (finite), Rotation (pitch kept within ±89, yaw any angle, no roll)
/// and FieldOfView (kept within <see cref="MinFieldOfView"/>..<see cref="MaxFieldOfView"/>). A refusal is a short
/// message for the script (null means done).
/// </summary>
internal static class ScriptCamera
{
    public const float MaxPitch = 89f;

    public const float MinFieldOfView = 10f;

    public const float MaxFieldOfView = 120f;

    public static string? Set(IScriptCameraHost camera, string key, double a, double b, double c)
    {
        if (!Fits(a) || !Fits(b) || !Fits(c))
        {
            return $"Camera.{key} must be finite";
        }

        switch (key)
        {
            case "Position":
                camera.Position = new Vector3((float)a, (float)b, (float)c);
                return null;
            case "Rotation":
                if (c != 0d)
                {
                    return "Camera.Rotation.z must be 0: the camera does not roll";
                }

                camera.SetRotation((float)global::System.Math.Clamp(a, -MaxPitch, MaxPitch), NormalizeDegrees((float)b));
                return null;
            case "FieldOfView":
                camera.FieldOfView = (float)global::System.Math.Clamp(a, MinFieldOfView, MaxFieldOfView);
                return null;
            case "Forward":
                // Looks along a direction (any length but zero): the same as looking at the point that far ahead.
                var direction = new Vector3((float)a, (float)b, (float)c);
                var length = direction.Length;
                return length > 1e-6f && float.IsFinite(length)
                    ? LookAt(camera, camera.Position + direction / length)
                    : "Camera.Forward expects a direction that is not zero";
            default:
                return $"Camera.{key} is read-only";
        }
    }

    /// <summary>Turns the camera to look at a world point (pitch kept within ±89).</summary>
    public static string? LookAt(IScriptCameraHost camera, Vector3 target)
    {
        var offset = target - camera.Position;
        var length = offset.Length;
        if (!(length > 1e-6f) || !float.IsFinite(length))
        {
            return "Camera.LookAt expects a point away from the camera";
        }

        var pitch = MathF.Asin(global::System.Math.Clamp(offset.Y / length, -1f, 1f)) * 180f / MathF.PI;
        var horizontal = MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z);
        var yaw = horizontal > 1e-6f ? MathF.Atan2(offset.Z, offset.X) * 180f / MathF.PI : camera.Yaw;
        camera.SetRotation(global::System.Math.Clamp(pitch, -MaxPitch, MaxPitch), NormalizeDegrees(yaw));
        return null;
    }

    private static bool Fits(double value) => double.IsFinite(value) && global::System.Math.Abs(value) <= float.MaxValue;

    // The same angle in (−180, 180].
    private static float NormalizeDegrees(float degrees)
    {
        var wrapped = degrees % 360f;
        if (wrapped <= -180f)
        {
            wrapped += 360f;
        }
        else if (wrapped > 180f)
        {
            wrapped -= 360f;
        }

        return wrapped == 0f ? 0f : wrapped;
    }
}

/// <summary>
/// The Atmosphere settings as scripts name them (the editor's Atmosphere settings): Enabled and FogEnabled (booleans), the colours
/// SkyColor, HorizonColor, SunColor, AmbientColor and FogColor ("#RRGGBB"), and the numbers SkyIntensity, SunIntensity,
/// SunElevation, SunHeading, SunDiscSize, AmbientIntensity, FogDensity, FogStart, FogEnd, Haze, Exposure, Saturation and
/// Contrast. Numbers are kept in the panel's ranges and steps (<see cref="AtmosphereSettings.With(AtmosphereValue, float)"/>).
/// </summary>
internal static class ScriptAtmosphere
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "Enabled", "FogEnabled", "SkyColor", "HorizonColor", "SunColor", "AmbientColor", "FogColor", "GroundColor", "AmbientMode",
        .. AtmosphereSettings.Values.Select(value => value.ToString())
    ];

    public static object? Get(AtmosphereSettings settings, string key)
    {
        if (key == "Enabled")
        {
            return settings.Enabled;
        }

        if (key == "AmbientMode")
        {
            return settings.AmbientMode.ToString();
        }

        if (key == "FogEnabled")
        {
            return settings.FogEnabled;
        }

        if (TryColorSlot(key, out var slot))
        {
            var (r, g, b) = settings.Get(slot).ToBytes();
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        return TryValue(key, out var value) ? (double)settings.Get(value) : null;
    }

    public static string? Set(IScriptAtmosphereHost atmosphere, string key, object? value)
    {
        var settings = atmosphere.Settings;
        if (key is "Enabled" or "FogEnabled")
        {
            if (value is not bool flag)
            {
                return $"Atmosphere.{key} must be true or false";
            }

            atmosphere.Settings = key == "Enabled" ? settings with { Enabled = flag } : settings with { FogEnabled = flag };
            return null;
        }

        if (key == "AmbientMode")
        {
            if (value is not string text || !Enum.TryParse<AtmosphereAmbientMode>(text, ignoreCase: true, out var mode) || !Enum.IsDefined(mode)
                || int.TryParse(text, out _))
            {
                return "Atmosphere.AmbientMode must be \"Flat\" or \"Sky\"";
            }

            atmosphere.Settings = (settings with { AmbientMode = mode }).Sanitized();
            return null;
        }

        if (TryColorSlot(key, out var slot))
        {
            if (value is not string hex || !ScriptObjectAccess.TryParseHex(hex, out var color))
            {
                return $"Atmosphere.{key} must be \"#RRGGBB\" (six hex digits)";
            }

            atmosphere.Settings = settings.With(slot, AtmosphereColor.FromRgb(color.X, color.Y, color.Z));
            return null;
        }

        if (TryValue(key, out var setting))
        {
            if (value is not double number || !double.IsFinite(number))
            {
                var range = AtmosphereSettings.RangeOf(setting);
                return $"Atmosphere.{key} must be a finite number ({range.Min:0.##} to {range.Max:0.##})";
            }

            atmosphere.Settings = settings.With(setting, (float)global::System.Math.Clamp(number, -float.MaxValue, float.MaxValue));
            return null;
        }

        return $"{key} is not a valid member of Atmosphere";
    }

    private static bool TryColorSlot(string key, out AtmosphereColorSlot slot)
    {
        slot = default;
        return key.EndsWith("Color", StringComparison.Ordinal) && Enum.TryParse(key[..^5], ignoreCase: false, out slot) && Enum.IsDefined(slot)
            && slot.ToString() + "Color" == key;
    }

    private static bool TryValue(string key, out AtmosphereValue value) =>
        Enum.TryParse(key, ignoreCase: false, out value) && Enum.IsDefined(value) && value.ToString() == key;
}
