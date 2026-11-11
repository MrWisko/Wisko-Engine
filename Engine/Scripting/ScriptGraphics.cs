using System.Globalization;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering.Universal;
using Wisko.Engine.World;

namespace Wisko.Engine.Scripting;

/// <summary>A scene object scripts can give a material and a light: a Part, primitive or model.</summary>
internal interface IScriptGraphicsTarget
{
    Part Part { get; }
}

/// <summary>What scripts' Graphics global reads and writes: the Play session's copy of the project's rendering settings.</summary>
public interface IScriptGraphicsHost
{
    GraphicsSettings Settings { get; set; }
}

/// <summary>
/// Graphics for Lua (docs/graphics.md, "Lua"), host side: an object's Material and Light and the Graphics
/// global. Like every Play change they last until Stop (the scene and the project's settings come back). Values are
/// checked and kept in the same ranges as the editor's panels; a value that cannot be used is refused with a short
/// message and nothing changes. Colours are "#RRGGBB" strings, as object.Color. On a model, Material reads its first
/// material and writes every one of its materials (each becomes the instance's own, from its imported look).
/// </summary>
internal static class ScriptGraphics
{
    public static readonly IReadOnlyList<string> MaterialKeys =
    [
        "Preset", "Shading", "Tint", "Metallic", "Roughness", "Opacity", "AlphaMode", "AlphaCutoff", "EmissiveColor", "EmissiveIntensity",
        "NormalStrength", "OcclusionStrength", "ToonBands", "Outline", "OutlineColor", "OutlineThickness", "FlatShading", "DoubleSided",
        "CastShadows", "ReceiveShadows", "BaseColorTexture", "NormalTexture", "MetallicRoughnessTexture", "OcclusionTexture",
        "EmissiveTexture", "UvMode", "UvScaleX", "UvScaleY"
    ];

    public static readonly IReadOnlyList<string> LightKeys = ["Type", "Enabled", "Color", "Intensity", "Range", "Angle", "InnerAngle", "CastShadows"];

    public static readonly IReadOnlyList<string> SettingKeys =
    [
        "Renderer", "Quality", "ToneMapping", "Bloom", "BloomThreshold", "BloomIntensity", "BloomRadius", "Vignette", "VignetteIntensity",
        "AmbientOcclusion", "AmbientOcclusionIntensity", "Temperature", "Tint", "Shadows", "ShadowDistance"
    ];

    // ---------------------------------------------------------------------------------------------- object.Material

    /// <summary>The material scripts see: a primitive's own, else a model's first material (its own or as imported).</summary>
    public static SurfaceMaterial MaterialOf(Part part) =>
        part.Model?.Asset is { } asset
            ? part.ModelMaterials?.Get(0) ?? MaterialResolver.ImportedAsSurface(asset, 0, null)
            : part.Material ?? SurfaceMaterial.Default;

    public static object? GetMaterial(Part part, string key, out string? error)
    {
        error = null;
        var m = MaterialOf(part);
        return key switch
        {
            "Preset" => MaterialPresets.All.FirstOrDefault(p => p.Apply(m, part.Color) == m)?.Name ?? "Custom",
            "Shading" => m.Mode.ToString(),
            "Tint" => ColorSpace.ToHex(m.BaseColor),
            "Metallic" => (double)m.Metallic,
            "Roughness" => (double)m.Roughness,
            "Opacity" => (double)m.Opacity,
            "AlphaMode" => m.AlphaMode.ToString(),
            "AlphaCutoff" => (double)m.AlphaCutoff,
            "EmissiveColor" => ColorSpace.ToHex(m.EmissiveColor),
            "EmissiveIntensity" => (double)m.EmissiveIntensity,
            "NormalStrength" => (double)m.NormalStrength,
            "OcclusionStrength" => (double)m.OcclusionStrength,
            "ToonBands" => (double)m.ToonBands,
            "Outline" => m.Outline,
            "OutlineColor" => ColorSpace.ToHex(m.OutlineColor),
            "OutlineThickness" => (double)m.OutlineThickness,
            "FlatShading" => m.FlatShading,
            "DoubleSided" => m.DoubleSided,
            "CastShadows" => m.CastShadows,
            "ReceiveShadows" => m.ReceiveShadows,
            "BaseColorTexture" => m.BaseColorTexture,
            "NormalTexture" => m.NormalTexture,
            "MetallicRoughnessTexture" => m.MetallicRoughnessTexture,
            "OcclusionTexture" => m.OcclusionTexture,
            "EmissiveTexture" => m.EmissiveTexture,
            "UvMode" => m.UvMode.ToString(),
            "UvScaleX" => (double)m.UvScaleX,
            "UvScaleY" => (double)m.UvScaleY,
            _ => Invalid("Material", key, out error)
        };
    }

    public static string? SetMaterial(Part part, string key, object? value)
    {
        if (key == "Preset")
        {
            return value is string name ? ApplyPreset(part, name) : "Material.Preset must be a preset name (a string)";
        }

        string? error = null;
        Func<SurfaceMaterial, SurfaceMaterial>? change = key switch
        {
            "Shading" => EnumValue<ShadingMode>(value, "Material.Shading", "\"Lit\", \"SimpleLit\", \"Unlit\" or \"Toon\"", ref error) is { } mode ? m => m with { Mode = mode } : null,
            "AlphaMode" => EnumValue<AlphaMode>(value, "Material.AlphaMode", "\"Opaque\", \"Mask\" or \"Blend\"", ref error) is { } alpha ? m => m with { AlphaMode = alpha } : null,
            "UvMode" => EnumValue<UvMode>(value, "Material.UvMode", "\"Mesh\" or \"Studs\"", ref error) is { } uv ? m => m with { UvMode = uv } : null,
            "Tint" => Color(value, "Material.Tint", ref error) is { } tint ? m => m with { BaseColor = tint } : null,
            "EmissiveColor" => Color(value, "Material.EmissiveColor", ref error) is { } glow ? m => m with { EmissiveColor = glow } : null,
            "OutlineColor" => Color(value, "Material.OutlineColor", ref error) is { } outline ? m => m with { OutlineColor = outline } : null,
            "Metallic" => Number(value, "Material.Metallic", 0f, 1f, ref error) is { } v ? m => m with { Metallic = v } : null,
            "Roughness" => Number(value, "Material.Roughness", 0f, 1f, ref error) is { } v ? m => m with { Roughness = v } : null,
            "Opacity" => Number(value, "Material.Opacity", 0f, 1f, ref error) is { } v ? m => m with { Opacity = v } : null,
            "AlphaCutoff" => Number(value, "Material.AlphaCutoff", 0f, 1f, ref error) is { } v ? m => m with { AlphaCutoff = v } : null,
            "EmissiveIntensity" => Number(value, "Material.EmissiveIntensity", 0f, 100f, ref error) is { } v ? m => m with { EmissiveIntensity = v } : null,
            "NormalStrength" => Number(value, "Material.NormalStrength", 0f, 4f, ref error) is { } v ? m => m with { NormalStrength = v } : null,
            "OcclusionStrength" => Number(value, "Material.OcclusionStrength", 0f, 1f, ref error) is { } v ? m => m with { OcclusionStrength = v } : null,
            "ToonBands" => Number(value, "Material.ToonBands", 2f, 8f, ref error) is { } v ? m => m with { ToonBands = (int)MathF.Round(v) } : null,
            "OutlineThickness" => Number(value, "Material.OutlineThickness", 0.005f, 2f, ref error) is { } v ? m => m with { OutlineThickness = v } : null,
            "UvScaleX" => Number(value, "Material.UvScaleX", 0.01f, 1000f, ref error) is { } v ? m => m with { UvScaleX = v } : null,
            "UvScaleY" => Number(value, "Material.UvScaleY", 0.01f, 1000f, ref error) is { } v ? m => m with { UvScaleY = v } : null,
            "Outline" => Flag(value, "Material.Outline", ref error) is { } b ? m => m with { Outline = b } : null,
            "FlatShading" => Flag(value, "Material.FlatShading", ref error) is { } b ? m => m with { FlatShading = b } : null,
            "DoubleSided" => Flag(value, "Material.DoubleSided", ref error) is { } b ? m => m with { DoubleSided = b } : null,
            "CastShadows" => Flag(value, "Material.CastShadows", ref error) is { } b ? m => m with { CastShadows = b } : null,
            "ReceiveShadows" => Flag(value, "Material.ReceiveShadows", ref error) is { } b ? m => m with { ReceiveShadows = b } : null,
            "BaseColorTexture" or "NormalTexture" or "MetallicRoughnessTexture" or "OcclusionTexture" or "EmissiveTexture" =>
                Texture(value, key, ref error) is var (ok, path) && ok ? m => m.WithTexture(SlotOf(key), path) : null,
            _ => null
        };

        if (change is null)
        {
            return error ?? $"{key} is not a valid member of Material";
        }

        EditMaterial(part, change);
        return null;
    }

    /// <summary>Material:ApplyPreset(name): one of the editor's presets (keeps colour, textures and UVs).</summary>
    public static string? ApplyPreset(Part part, string name)
    {
        if (MaterialPresets.Find(name) is not { } preset)
        {
            return $"No material preset \"{name}\" (presets: {string.Join(", ", MaterialPresets.All.Select(p => p.Name))})";
        }

        EditMaterial(part, m => preset.Apply(m, part.Color));
        return null;
    }

    /// <summary>Material:Reset(): a primitive's default material, a model's imported materials.</summary>
    public static void ResetMaterial(Part part)
    {
        part.Material = null;
        part.ModelMaterials = null;
    }

    private static void EditMaterial(Part part, Func<SurfaceMaterial, SurfaceMaterial> change)
    {
        if (part.Model?.Asset is { } asset)
        {
            var overrides = part.ModelMaterials ?? MaterialOverrideSet.Empty;
            for (var i = 0; i < global::System.Math.Max(1, asset.Model.Materials.Count); i++)
            {
                var current = overrides.Get(i) ?? MaterialResolver.ImportedAsSurface(asset, i, null);
                overrides = overrides.With(i, change(current).Sanitized());
            }

            part.ModelMaterials = overrides;
            return;
        }

        var next = change(part.Material ?? SurfaceMaterial.Default).Sanitized();
        part.Material = next == SurfaceMaterial.Default ? null : next;
    }

    private static TextureSlot SlotOf(string key) => key switch
    {
        "NormalTexture" => TextureSlot.Normal,
        "MetallicRoughnessTexture" => TextureSlot.MetallicRoughness,
        "OcclusionTexture" => TextureSlot.Occlusion,
        "EmissiveTexture" => TextureSlot.Emissive,
        _ => TextureSlot.BaseColor
    };

    // ---------------------------------------------------------------------------------------------- object.Light

    public static object? GetLight(Part part, string key, out string? error)
    {
        error = null;
        if (part.Light is not { } light)
        {
            error = $"'{part.Name}' has no light (AddLight gives it one)";
            return null;
        }

        return key switch
        {
            "Type" => light.Type.ToString(),
            "Enabled" => light.Enabled,
            "Color" => ColorSpace.ToHex(light.Color),
            "Intensity" => (double)light.Intensity,
            "Range" => (double)light.Range,
            "Angle" => (double)light.Angle,
            "InnerAngle" => (double)light.InnerAngle,
            "CastShadows" => light.CastShadows,
            _ => Invalid("Light", key, out error)
        };
    }

    public static string? SetLight(Part part, string key, object? value)
    {
        if (part.Light is not { } light)
        {
            return $"'{part.Name}' has no light (AddLight gives it one)";
        }

        string? error = null;
        PartLight? next = key switch
        {
            // A new type starts from that type's defaults, keeping the colour and whether it is on.
            "Type" => EnumValue<LightType>(value, "Light.Type", "\"Point\", \"Spot\" or \"Directional\"", ref error) is { } type
                ? type == light.Type ? light : PartLight.Create(type) with { Color = light.Color, Enabled = light.Enabled }
                : null,
            "Enabled" => Flag(value, "Light.Enabled", ref error) is { } on ? light with { Enabled = on } : null,
            "CastShadows" => Flag(value, "Light.CastShadows", ref error) is { } cast ? light with { CastShadows = cast } : null,
            "Color" => Color(value, "Light.Color", ref error) is { } color ? light with { Color = color } : null,
            "Intensity" => Number(value, "Light.Intensity", 0f, PartLight.MaxIntensity, ref error) is { } v ? light with { Intensity = v } : null,
            "Range" => Number(value, "Light.Range", PartLight.MinRange, PartLight.MaxRange, ref error) is { } v ? light with { Range = v } : null,
            "Angle" => Number(value, "Light.Angle", 1f, 179f, ref error) is { } v ? light with { Angle = v } : null,
            "InnerAngle" => Number(value, "Light.InnerAngle", 0f, 179f, ref error) is { } v ? light with { InnerAngle = v } : null,
            _ => null
        };

        if (next is null)
        {
            return error ?? $"{key} is not a valid member of Light";
        }

        part.Light = next.Sanitized();
        return null;
    }

    /// <summary>object:AddLight([type]): a light of that type (default "Point"), replacing any it had.</summary>
    public static string? AddLight(Part part, object? type)
    {
        string? error = null;
        var lightType = type is null ? LightType.Point : EnumValue<LightType>(type, "AddLight", "\"Point\", \"Spot\" or \"Directional\"", ref error);
        if (lightType is not { } chosen)
        {
            return error;
        }

        part.Light = PartLight.Create(chosen);
        return null;
    }

    // ---------------------------------------------------------------------------------------------- Graphics

    public static object? GetSetting(GraphicsSettings s, string key, out string? error)
    {
        error = null;
        return key switch
        {
            "Renderer" => s.Renderer.ToString(),
            "Quality" => s.Quality.ToString(),
            "ToneMapping" => s.ToneMapping.ToString(),
            "Bloom" => s.BloomEnabled,
            "BloomThreshold" => (double)s.BloomThreshold,
            "BloomIntensity" => (double)s.BloomIntensity,
            "BloomRadius" => (double)s.BloomRadius,
            "Vignette" => s.VignetteEnabled,
            "VignetteIntensity" => (double)s.VignetteIntensity,
            "AmbientOcclusion" => s.AmbientOcclusionEnabled,
            "AmbientOcclusionIntensity" => (double)s.AmbientOcclusionIntensity,
            "Temperature" => (double)s.Temperature,
            "Tint" => (double)s.Tint,
            "Shadows" => s.ShadowsEnabled,
            "ShadowDistance" => (double)s.ShadowDistance,
            _ => Invalid("Graphics", key, out error)
        };
    }

    public static string? SetSetting(IScriptGraphicsHost host, string key, object? value)
    {
        var s = host.Settings;
        string? error = null;
        GraphicsSettings? next = key switch
        {
            "Renderer" => null,
            "Quality" => EnumValue<QualityLevel>(value, "Graphics.Quality", "\"Low\", \"Medium\", \"High\", \"Ultra\" or \"Custom\"", ref error) is { } q
                ? s with { Quality = q } : null,
            "ToneMapping" => EnumValue<ToneMapping>(value, "Graphics.ToneMapping", "\"None\", \"Neutral\" or \"Filmic\"", ref error) is { } t
                ? s with { ToneMapping = t } : null,
            "Bloom" => Flag(value, "Graphics.Bloom", ref error) is { } b ? s with { BloomEnabled = b } : null,
            "Vignette" => Flag(value, "Graphics.Vignette", ref error) is { } b ? s with { VignetteEnabled = b } : null,
            "AmbientOcclusion" => Flag(value, "Graphics.AmbientOcclusion", ref error) is { } b ? s with { AmbientOcclusionEnabled = b } : null,
            "Shadows" => Flag(value, "Graphics.Shadows", ref error) is { } b ? s with { ShadowsEnabled = b } : null,
            "BloomThreshold" => Number(value, "Graphics.BloomThreshold", 0f, 10f, ref error) is { } v ? s with { BloomThreshold = v } : null,
            "BloomIntensity" => Number(value, "Graphics.BloomIntensity", 0f, 5f, ref error) is { } v ? s with { BloomIntensity = v } : null,
            "BloomRadius" => Number(value, "Graphics.BloomRadius", 0f, 1f, ref error) is { } v ? s with { BloomRadius = v } : null,
            "VignetteIntensity" => Number(value, "Graphics.VignetteIntensity", 0f, 1f, ref error) is { } v ? s with { VignetteIntensity = v } : null,
            "AmbientOcclusionIntensity" => Number(value, "Graphics.AmbientOcclusionIntensity", 0f, 4f, ref error) is { } v ? s with { AmbientOcclusionIntensity = v } : null,
            "Temperature" => Number(value, "Graphics.Temperature", -1f, 1f, ref error) is { } v ? s with { Temperature = v } : null,
            "Tint" => Number(value, "Graphics.Tint", -1f, 1f, ref error) is { } v ? s with { Tint = v } : null,
            "ShadowDistance" => Number(value, "Graphics.ShadowDistance", 10f, 2000f, ref error) is { } v ? s with { ShadowDistance = v } : null,
            _ => null
        };

        if (next is null)
        {
            return key == "Renderer" ? "Graphics.Renderer is read-only (the project chooses it)" : error ?? $"{key} is not a valid member of Graphics";
        }

        host.Settings = next.Sanitized();
        return null;
    }

    // ---------------------------------------------------------------------------------------------- values

    private static object? Invalid(string type, string key, out string? error)
    {
        error = $"{key} is not a valid member of {type}";
        return null;
    }

    private static float? Number(object? value, string name, float min, float max, ref string? error)
    {
        if (value is double number && double.IsFinite(number) && number >= min && number <= max)
        {
            return (float)number;
        }

        error = $"{name} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}";
        return null;
    }

    private static bool? Flag(object? value, string name, ref string? error)
    {
        if (value is bool flag)
        {
            return flag;
        }

        error = $"{name} must be true or false";
        return null;
    }

    private static Vector3? Color(object? value, string name, ref string? error)
    {
        if (value is string hex && ScriptObjectAccess.TryParseHex(hex, out var color))
        {
            return color;
        }

        error = $"{name} must be \"#RRGGBB\" (six hex digits)";
        return null;
    }

    // Names only (case-insensitive), never numbers: a script says what it means.
    private static T? EnumValue<T>(object? value, string name, string allowed, ref string? error) where T : struct, Enum
    {
        if (value is string text && !int.TryParse(text, out _) && Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        error = $"{name} must be {allowed}";
        return null;
    }

    // A project texture path ("Assets/Textures/brick.png", "@model:N"), or nil to remove it.
    private static (bool Ok, string? Path) Texture(object? value, string key, ref string? error)
    {
        if (value is null)
        {
            return (true, null);
        }

        if (value is string text && SurfaceMaterial.CleanTexturePath(text) is { } clean)
        {
            return (true, clean);
        }

        error = $"Material.{key} must be a path inside the project (e.g. \"Assets/Textures/brick.png\") or nil";
        return (false, null);
    }
}
