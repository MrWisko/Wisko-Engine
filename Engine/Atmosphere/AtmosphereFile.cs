using System.Text;
using System.Text.Json;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Atmosphere;

/// <summary>How <see cref="AtmosphereFile.Load"/> went.</summary>
public enum AtmosphereLoadStatus
{
    Loaded,

    /// <summary>No Atmosphere.json (a project from before Atmosphere): the defaults are used.</summary>
    Missing,

    /// <summary>Unreadable or not JSON: the defaults are used and the problem is reported.</summary>
    Invalid
}

/// <summary>
/// A project's Atmosphere.json, next to Scene.json. Written deterministically (fixed field order, colours as 0-255
/// like Scene.json, round-trip numbers) and atomically (<see cref="AtomicFile"/>). Reading never throws: a missing
/// file gives the defaults, and a missing, mistyped or out-of-range field falls back to its default on its own.
/// </summary>
public static class AtmosphereFile
{
    public const string FileName = "Atmosphere.json";
    public const int FormatVersion = 1;

    public static AtmosphereSettings Load(string path, out AtmosphereLoadStatus status, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            status = AtmosphereLoadStatus.Missing;
            return AtmosphereSettings.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                status = AtmosphereLoadStatus.Invalid;
                problem = "not a JSON object";
                return AtmosphereSettings.Default;
            }

            status = AtmosphereLoadStatus.Loaded;
            return FromJson(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            status = AtmosphereLoadStatus.Invalid;
            problem = ex.Message;
            return AtmosphereSettings.Default;
        }
    }

    public static bool TrySave(string path, AtmosphereSettings settings, out string? error) =>
        AtomicFile.TryWriteAllText(path, ToJson(settings), out error);

    /// <summary>The exact file text for these settings (sanitized first); equal settings give identical text.</summary>
    public static string ToJson(AtmosphereSettings settings)
    {
        settings = settings.Sanitized();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("FormatVersion", FormatVersion);
            writer.WriteBoolean("Enabled", settings.Enabled);

            writer.WriteStartObject("Sky");
            WriteColor(writer, "Color", settings.SkyColor);
            WriteColor(writer, "HorizonColor", settings.HorizonColor);
            writer.WriteNumber("Intensity", settings.SkyIntensity);
            writer.WriteEndObject();

            writer.WriteStartObject("Sun");
            WriteColor(writer, "Color", settings.SunColor);
            writer.WriteNumber("Intensity", settings.SunIntensity);
            writer.WriteNumber("Elevation", settings.SunElevation);
            writer.WriteNumber("Heading", settings.SunHeading);
            writer.WriteNumber("DiscSize", settings.SunDiscSize);
            writer.WriteEndObject();

            writer.WriteStartObject("Ambient");
            WriteColor(writer, "Color", settings.AmbientColor);
            writer.WriteNumber("Intensity", settings.AmbientIntensity);
            writer.WriteString("Mode", settings.AmbientMode.ToString());
            WriteColor(writer, "GroundColor", settings.GroundColor);
            writer.WriteEndObject();

            writer.WriteStartObject("Fog");
            writer.WriteBoolean("Enabled", settings.FogEnabled);
            WriteColor(writer, "Color", settings.FogColor);
            writer.WriteNumber("Density", settings.FogDensity);
            writer.WriteNumber("Start", settings.FogStart);
            writer.WriteNumber("End", settings.FogEnd);
            writer.WriteEndObject();

            writer.WriteNumber("Haze", settings.Haze);
            writer.WriteNumber("Exposure", settings.Exposure);
            writer.WriteNumber("Saturation", settings.Saturation);
            writer.WriteNumber("Contrast", settings.Contrast);
            writer.WriteNumber("ReflectionIntensity", settings.ReflectionIntensity);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Settings from a parsed Atmosphere object; anything missing or invalid keeps its default.</summary>
    public static AtmosphereSettings FromJson(JsonElement root)
    {
        var d = AtmosphereSettings.Default;
        var sky = Child(root, "Sky");
        var sun = Child(root, "Sun");
        var ambient = Child(root, "Ambient");
        var fog = Child(root, "Fog");
        return new AtmosphereSettings
        {
            Enabled = Bool(root, "Enabled", d.Enabled),
            SkyColor = Color(sky, "Color", d.SkyColor),
            HorizonColor = Color(sky, "HorizonColor", d.HorizonColor),
            SkyIntensity = Number(sky, "Intensity", d.SkyIntensity),
            SunColor = Color(sun, "Color", d.SunColor),
            SunIntensity = Number(sun, "Intensity", d.SunIntensity),
            SunElevation = Number(sun, "Elevation", d.SunElevation),
            SunHeading = Number(sun, "Heading", d.SunHeading),
            SunDiscSize = Number(sun, "DiscSize", d.SunDiscSize),
            AmbientColor = Color(ambient, "Color", d.AmbientColor),
            AmbientIntensity = Number(ambient, "Intensity", d.AmbientIntensity),
            FogEnabled = Bool(fog, "Enabled", d.FogEnabled),
            FogColor = Color(fog, "Color", d.FogColor),
            FogDensity = Number(fog, "Density", d.FogDensity),
            FogStart = Number(fog, "Start", d.FogStart),
            FogEnd = Number(fog, "End", d.FogEnd),
            Haze = Number(root, "Haze", d.Haze),
            Exposure = Number(root, "Exposure", d.Exposure),
            Saturation = Number(root, "Saturation", d.Saturation),
            Contrast = Number(root, "Contrast", d.Contrast),
            AmbientMode = ambient is { } a && a.TryGetProperty("Mode", out var mode) && mode.ValueKind == JsonValueKind.String
                && Enum.TryParse<AtmosphereAmbientMode>(mode.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : d.AmbientMode,
            GroundColor = Color(ambient, "GroundColor", d.GroundColor),
            ReflectionIntensity = Number(root, "ReflectionIntensity", d.ReflectionIntensity)
        }.Sanitized();
    }

    private static void WriteColor(Utf8JsonWriter writer, string name, AtmosphereColor color)
    {
        var (r, g, b) = color.ToBytes();
        writer.WriteStartArray(name);
        writer.WriteNumberValue(r);
        writer.WriteNumberValue(g);
        writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : null;

    private static bool Bool(JsonElement? parent, string name, bool fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static float Number(JsonElement? parent, string name, float fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? (float)global::System.Math.Clamp(number, -1e6, 1e6)
            : fallback;

    private static AtmosphereColor Color(JsonElement? parent, string name, AtmosphereColor fallback)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 3)
        {
            return fallback;
        }

        var channels = new int[3];
        var i = 0;
        foreach (var channel in value.EnumerateArray())
        {
            if (channel.ValueKind != JsonValueKind.Number || !channel.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                return fallback;
            }

            channels[i++] = (int)global::System.Math.Round(global::System.Math.Clamp(number, 0, 255));
        }

        return AtmosphereColor.FromBytes(channels[0], channels[1], channels[2]);
    }
}
