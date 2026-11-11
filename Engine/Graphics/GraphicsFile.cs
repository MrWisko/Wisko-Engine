using System.Text;
using System.Text.Json;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Graphics;

/// <summary>How <see cref="GraphicsFile.Load"/> went.</summary>
public enum GraphicsLoadStatus
{
    Loaded,

    /// <summary>No Graphics.json (an older project): <see cref="GraphicsSettings.LegacyDefault"/>, Classic.</summary>
    Missing,

    /// <summary>Unreadable or not JSON: Classic (the safe look), and the problem is reported.</summary>
    Invalid
}

/// <summary>
/// A project's Graphics.json, next to Scene.json. Written deterministically (fixed field order, round-trip numbers)
/// and atomically (<see cref="AtomicFile"/>). Reading never throws: a missing or unreadable file gives the Classic
/// renderer, and a missing, mistyped or out-of-range field falls back to its default on its own.
/// </summary>
public static class GraphicsFile
{
    public const string FileName = "Graphics.json";
    public const int FormatVersion = 1;

    public static GraphicsSettings Load(string path, out GraphicsLoadStatus status, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            status = GraphicsLoadStatus.Missing;
            return GraphicsSettings.LegacyDefault;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                status = GraphicsLoadStatus.Invalid;
                problem = "not a JSON object";
                return GraphicsSettings.LegacyDefault;
            }

            status = GraphicsLoadStatus.Loaded;
            return FromJson(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            status = GraphicsLoadStatus.Invalid;
            problem = ex.Message;
            return GraphicsSettings.LegacyDefault;
        }
    }

    public static bool TrySave(string path, GraphicsSettings settings, out string? error) =>
        AtomicFile.TryWriteAllText(path, ToJson(settings), out error);

    /// <summary>The exact file text for these settings (sanitized first); equal settings give identical text.</summary>
    public static string ToJson(GraphicsSettings settings)
    {
        settings = settings.Sanitized();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("FormatVersion", FormatVersion);
            writer.WriteString("Renderer", settings.Renderer.ToString());
            writer.WriteString("Quality", settings.Quality.ToString());
            writer.WriteString("AntiAliasing", settings.AntiAliasing.ToString());

            writer.WriteStartObject("ToneMapping");
            writer.WriteString("Mode", settings.ToneMapping.ToString());
            writer.WriteNumber("Temperature", settings.Temperature);
            writer.WriteNumber("Tint", settings.Tint);
            writer.WriteEndObject();

            writer.WriteStartObject("Bloom");
            writer.WriteBoolean("Enabled", settings.BloomEnabled);
            writer.WriteNumber("Threshold", settings.BloomThreshold);
            writer.WriteNumber("Intensity", settings.BloomIntensity);
            writer.WriteNumber("Radius", settings.BloomRadius);
            writer.WriteEndObject();

            writer.WriteStartObject("Vignette");
            writer.WriteBoolean("Enabled", settings.VignetteEnabled);
            writer.WriteNumber("Intensity", settings.VignetteIntensity);
            writer.WriteEndObject();

            writer.WriteStartObject("AmbientOcclusion");
            writer.WriteBoolean("Enabled", settings.AmbientOcclusionEnabled);
            writer.WriteNumber("Radius", settings.AmbientOcclusionRadius);
            writer.WriteNumber("Intensity", settings.AmbientOcclusionIntensity);
            writer.WriteEndObject();

            writer.WriteStartObject("Shadows");
            writer.WriteBoolean("Enabled", settings.ShadowsEnabled);
            writer.WriteNumber("Distance", settings.ShadowDistance);
            writer.WriteNumber("Bias", settings.ShadowBias);
            writer.WriteNumber("NormalBias", settings.ShadowNormalBias);
            writer.WriteBoolean("PointLights", settings.PointLightShadows);
            writer.WriteEndObject();

            var q = settings.Custom;
            writer.WriteStartObject("Custom");
            writer.WriteBoolean("PostProcessing", q.PostProcessing);
            writer.WriteNumber("ShadowResolution", q.ShadowResolution);
            writer.WriteNumber("ShadowCascades", q.ShadowCascades);
            writer.WriteNumber("ShadowFilterSamples", q.ShadowFilterSamples);
            writer.WriteNumber("ShadowDistanceScale", q.ShadowDistanceScale);
            writer.WriteNumber("MaxSpotShadows", q.MaxSpotShadows);
            writer.WriteNumber("SpotShadowResolution", q.SpotShadowResolution);
            writer.WriteNumber("MaxPointShadows", q.MaxPointShadows);
            writer.WriteNumber("PointShadowResolution", q.PointShadowResolution);
            writer.WriteNumber("AmbientOcclusionSamples", q.AmbientOcclusionSamples);
            writer.WriteBoolean("AmbientOcclusionHalfResolution", q.AmbientOcclusionHalfResolution);
            writer.WriteNumber("BloomLevels", q.BloomLevels);
            writer.WriteNumber("ReflectionResolution", q.ReflectionResolution);
            writer.WriteNumber("MaxLightsPerObject", q.MaxLightsPerObject);
            writer.WriteNumber("RenderScale", q.RenderScale);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Settings from a parsed Graphics object; anything missing or invalid keeps its default.</summary>
    public static GraphicsSettings FromJson(JsonElement root)
    {
        var d = GraphicsSettings.Default;
        var tone = Child(root, "ToneMapping");
        var bloom = Child(root, "Bloom");
        var vignette = Child(root, "Vignette");
        var ao = Child(root, "AmbientOcclusion");
        var shadows = Child(root, "Shadows");
        var custom = Child(root, "Custom");
        var q = QualitySettings.Medium;
        return new GraphicsSettings
        {
            Renderer = EnumValue(root, "Renderer", RendererKind.Classic),
            Quality = EnumValue(root, "Quality", d.Quality),
            AntiAliasing = EnumValue(root, "AntiAliasing", d.AntiAliasing),
            ToneMapping = EnumValue(tone, "Mode", d.ToneMapping),
            Temperature = Number(tone, "Temperature", d.Temperature),
            Tint = Number(tone, "Tint", d.Tint),
            BloomEnabled = Bool(bloom, "Enabled", d.BloomEnabled),
            BloomThreshold = Number(bloom, "Threshold", d.BloomThreshold),
            BloomIntensity = Number(bloom, "Intensity", d.BloomIntensity),
            BloomRadius = Number(bloom, "Radius", d.BloomRadius),
            VignetteEnabled = Bool(vignette, "Enabled", d.VignetteEnabled),
            VignetteIntensity = Number(vignette, "Intensity", d.VignetteIntensity),
            AmbientOcclusionEnabled = Bool(ao, "Enabled", d.AmbientOcclusionEnabled),
            AmbientOcclusionRadius = Number(ao, "Radius", d.AmbientOcclusionRadius),
            AmbientOcclusionIntensity = Number(ao, "Intensity", d.AmbientOcclusionIntensity),
            ShadowsEnabled = Bool(shadows, "Enabled", d.ShadowsEnabled),
            ShadowDistance = Number(shadows, "Distance", d.ShadowDistance),
            ShadowBias = Number(shadows, "Bias", d.ShadowBias),
            ShadowNormalBias = Number(shadows, "NormalBias", d.ShadowNormalBias),
            PointLightShadows = Bool(shadows, "PointLights", d.PointLightShadows),
            Custom = new QualitySettings
            {
                PostProcessing = Bool(custom, "PostProcessing", q.PostProcessing),
                ShadowResolution = Int(custom, "ShadowResolution", q.ShadowResolution),
                ShadowCascades = Int(custom, "ShadowCascades", q.ShadowCascades),
                ShadowFilterSamples = Int(custom, "ShadowFilterSamples", q.ShadowFilterSamples),
                ShadowDistanceScale = Number(custom, "ShadowDistanceScale", q.ShadowDistanceScale),
                MaxSpotShadows = Int(custom, "MaxSpotShadows", q.MaxSpotShadows),
                SpotShadowResolution = Int(custom, "SpotShadowResolution", q.SpotShadowResolution),
                MaxPointShadows = Int(custom, "MaxPointShadows", q.MaxPointShadows),
                PointShadowResolution = Int(custom, "PointShadowResolution", q.PointShadowResolution),
                AmbientOcclusionSamples = Int(custom, "AmbientOcclusionSamples", q.AmbientOcclusionSamples),
                AmbientOcclusionHalfResolution = Bool(custom, "AmbientOcclusionHalfResolution", q.AmbientOcclusionHalfResolution),
                BloomLevels = Int(custom, "BloomLevels", q.BloomLevels),
                ReflectionResolution = Int(custom, "ReflectionResolution", q.ReflectionResolution),
                MaxLightsPerObject = Int(custom, "MaxLightsPerObject", q.MaxLightsPerObject),
                RenderScale = Number(custom, "RenderScale", q.RenderScale)
            }
        }.Sanitized();
    }

    internal static JsonElement? Child(JsonElement? parent, string name) =>
        parent is { } p && p.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : null;

    internal static bool Bool(JsonElement? parent, string name, bool fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    internal static float Number(JsonElement? parent, string name, float fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? (float)global::System.Math.Clamp(number, -1e6, 1e6)
            : fallback;

    internal static int Int(JsonElement? parent, string name, int fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? (int)global::System.Math.Clamp(global::System.Math.Round(number), -1e6, 1e6)
            : fallback;

    internal static T EnumValue<T>(JsonElement? parent, string name, T fallback) where T : struct, Enum =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && Enum.TryParse<T>(value.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}
