using Wisko.Engine.Math;

namespace Wisko.Engine.Graphics;

// Scene.json shapes of the material and light data. Every value is written only when it differs from its default, so a
// default material or light writes nothing and scenes without material or light data keep their exact JSON. Reading takes
// each value on its own (missing, mistyped or out of range: the default) and sanitizes the result.

/// <summary>"Material": { "Mode": "Toon", "Roughness": 0.2, "Textures": { "BaseColor": "Assets/Textures/A.png" } ... }</summary>
internal sealed class SurfaceMaterialDto
{
    public string? Mode { get; set; }
    public int[]? Color { get; set; }
    public float? Opacity { get; set; }
    public float? Metallic { get; set; }
    public float? Roughness { get; set; }
    public int[]? Emissive { get; set; }
    public float? EmissiveIntensity { get; set; }
    public float? NormalStrength { get; set; }
    public float? OcclusionStrength { get; set; }
    public string? AlphaMode { get; set; }
    public float? AlphaCutoff { get; set; }
    public bool? DoubleSided { get; set; }
    public bool? FlatShading { get; set; }
    public int? ToonBands { get; set; }
    public bool? Outline { get; set; }
    public int[]? OutlineColor { get; set; }
    public float? OutlineThickness { get; set; }
    public bool? CastShadows { get; set; }
    public bool? ReceiveShadows { get; set; }
    public MaterialTexturesDto? Textures { get; set; }
    public string? UvMode { get; set; }
    public float[]? UvScale { get; set; }
}

internal sealed class MaterialTexturesDto
{
    public string? BaseColor { get; set; }
    public string? MetallicRoughness { get; set; }
    public string? Normal { get; set; }
    public string? Occlusion { get; set; }
    public string? Emissive { get; set; }
}

/// <summary>A model instance's override of one imported material: "Materials": [ { "Index": 0, "Material": {...} } ].</summary>
internal sealed class ModelMaterialDto
{
    public int Index { get; set; }
    public SurfaceMaterialDto? Material { get; set; }
}

/// <summary>"Light": { "Type": "Spot", "Intensity": 80, "Range": 30, "Angle": 60, "InnerAngle": 40, "CastShadows": true }</summary>
internal sealed class PartLightDto
{
    public string? Type { get; set; }
    public bool? Enabled { get; set; }
    public int[]? Color { get; set; }
    public float? Intensity { get; set; }
    public float? Range { get; set; }
    public float? Angle { get; set; }
    public float? InnerAngle { get; set; }
    public bool? CastShadows { get; set; }
}

internal static class GraphicsDto
{
    public static SurfaceMaterialDto? ToDto(SurfaceMaterial? material)
    {
        if (material is null)
        {
            return null;
        }

        var m = material.Sanitized();
        var d = SurfaceMaterial.Default;
        if (m == d)
        {
            return null;
        }

        var textures = new MaterialTexturesDto
        {
            BaseColor = m.BaseColorTexture,
            MetallicRoughness = m.MetallicRoughnessTexture,
            Normal = m.NormalTexture,
            Occlusion = m.OcclusionTexture,
            Emissive = m.EmissiveTexture
        };
        return new SurfaceMaterialDto
        {
            Mode = m.Mode == d.Mode ? null : m.Mode.ToString(),
            Color = m.BaseColor.Equals(d.BaseColor) ? null : Bytes(m.BaseColor),
            Opacity = m.Opacity.Equals(d.Opacity) ? null : m.Opacity,
            Metallic = m.Metallic.Equals(d.Metallic) ? null : m.Metallic,
            Roughness = m.Roughness.Equals(d.Roughness) ? null : m.Roughness,
            Emissive = m.EmissiveColor.Equals(d.EmissiveColor) ? null : Bytes(m.EmissiveColor),
            EmissiveIntensity = m.EmissiveIntensity.Equals(d.EmissiveIntensity) ? null : m.EmissiveIntensity,
            NormalStrength = m.NormalStrength.Equals(d.NormalStrength) ? null : m.NormalStrength,
            OcclusionStrength = m.OcclusionStrength.Equals(d.OcclusionStrength) ? null : m.OcclusionStrength,
            AlphaMode = m.AlphaMode == d.AlphaMode ? null : m.AlphaMode.ToString(),
            AlphaCutoff = m.AlphaCutoff.Equals(d.AlphaCutoff) ? null : m.AlphaCutoff,
            DoubleSided = m.DoubleSided ? true : null,
            FlatShading = m.FlatShading ? true : null,
            ToonBands = m.ToonBands == d.ToonBands ? null : m.ToonBands,
            Outline = m.Outline ? true : null,
            OutlineColor = m.OutlineColor.Equals(d.OutlineColor) ? null : Bytes(m.OutlineColor),
            OutlineThickness = m.OutlineThickness.Equals(d.OutlineThickness) ? null : m.OutlineThickness,
            CastShadows = m.CastShadows ? null : false,
            ReceiveShadows = m.ReceiveShadows ? null : false,
            Textures = m.HasAnyTexture ? textures : null,
            UvMode = m.UvMode == d.UvMode ? null : m.UvMode.ToString(),
            UvScale = m.UvScaleX.Equals(d.UvScaleX) && m.UvScaleY.Equals(d.UvScaleY) ? null : [m.UvScaleX, m.UvScaleY]
        };
    }

    public static SurfaceMaterial? FromDto(SurfaceMaterialDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var d = SurfaceMaterial.Default;
        var material = new SurfaceMaterial
        {
            Mode = ParseEnum(dto.Mode, d.Mode),
            BaseColor = Color(dto.Color, d.BaseColor),
            Opacity = dto.Opacity ?? d.Opacity,
            Metallic = dto.Metallic ?? d.Metallic,
            Roughness = dto.Roughness ?? d.Roughness,
            EmissiveColor = Color(dto.Emissive, d.EmissiveColor),
            EmissiveIntensity = dto.EmissiveIntensity ?? d.EmissiveIntensity,
            NormalStrength = dto.NormalStrength ?? d.NormalStrength,
            OcclusionStrength = dto.OcclusionStrength ?? d.OcclusionStrength,
            AlphaMode = ParseEnum(dto.AlphaMode, d.AlphaMode),
            AlphaCutoff = dto.AlphaCutoff ?? d.AlphaCutoff,
            DoubleSided = dto.DoubleSided == true,
            FlatShading = dto.FlatShading == true,
            ToonBands = dto.ToonBands ?? d.ToonBands,
            Outline = dto.Outline == true,
            OutlineColor = Color(dto.OutlineColor, d.OutlineColor),
            OutlineThickness = dto.OutlineThickness ?? d.OutlineThickness,
            CastShadows = dto.CastShadows != false,
            ReceiveShadows = dto.ReceiveShadows != false,
            BaseColorTexture = dto.Textures?.BaseColor,
            MetallicRoughnessTexture = dto.Textures?.MetallicRoughness,
            NormalTexture = dto.Textures?.Normal,
            OcclusionTexture = dto.Textures?.Occlusion,
            EmissiveTexture = dto.Textures?.Emissive,
            UvMode = ParseEnum(dto.UvMode, d.UvMode),
            UvScaleX = dto.UvScale is { Length: 2 } ? dto.UvScale[0] : d.UvScaleX,
            UvScaleY = dto.UvScale is { Length: 2 } ? dto.UvScale[1] : d.UvScaleY
        }.Sanitized();
        return material == d ? null : material;
    }

    public static ModelMaterialDto[]? ToDto(MaterialOverrideSet? overrides) =>
        overrides is null || overrides.Count == 0
            ? null
            : overrides.Entries.Select(e => new ModelMaterialDto { Index = e.Index, Material = ToDto(e.Material) ?? new SurfaceMaterialDto() }).ToArray();

    public static MaterialOverrideSet? FromDto(ModelMaterialDto[]? dtos)
    {
        if (dtos is null || dtos.Length == 0)
        {
            return null;
        }

        var set = MaterialOverrideSet.From(dtos
            .Where(dto => dto.Index >= 0 && dto.Index < MaterialOverrideSet.MaxMaterialIndex)
            .Select(dto => (dto.Index, FromDto(dto.Material) ?? SurfaceMaterial.Default)));
        return set.Count == 0 ? null : set;
    }

    public static PartLightDto? ToDto(PartLight? light)
    {
        if (light is null)
        {
            return null;
        }

        var l = light.Sanitized();
        var d = PartLight.Create(l.Type);
        return new PartLightDto
        {
            Type = l.Type.ToString(),
            Enabled = l.Enabled ? null : false,
            Color = l.Color.Equals(d.Color) ? null : Bytes(l.Color),
            Intensity = l.Intensity.Equals(d.Intensity) ? null : l.Intensity,
            Range = l.Range.Equals(d.Range) ? null : l.Range,
            Angle = l.Angle.Equals(d.Angle) ? null : l.Angle,
            InnerAngle = l.InnerAngle.Equals(d.InnerAngle) ? null : l.InnerAngle,
            CastShadows = l.CastShadows == d.CastShadows ? null : l.CastShadows
        };
    }

    public static PartLight? FromDto(PartLightDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var type = ParseEnum(dto.Type, LightType.Point);
        var d = PartLight.Create(type);
        return new PartLight
        {
            Type = type,
            Enabled = dto.Enabled != false,
            Color = Color(dto.Color, d.Color),
            Intensity = dto.Intensity ?? d.Intensity,
            Range = dto.Range ?? d.Range,
            Angle = dto.Angle ?? d.Angle,
            InnerAngle = dto.InnerAngle ?? d.InnerAngle,
            CastShadows = dto.CastShadows ?? d.CastShadows
        }.Sanitized();
    }

    private static int[] Bytes(Vector3 color)
    {
        var (r, g, b) = ColorSpace.ToBytes(color);
        return [r, g, b];
    }

    private static Vector3 Color(int[]? bytes, Vector3 fallback) =>
        bytes is { Length: 3 } ? ColorSpace.FromBytes(bytes[0], bytes[1], bytes[2]) : fallback;

    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        text is not null && Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : fallback;
}
