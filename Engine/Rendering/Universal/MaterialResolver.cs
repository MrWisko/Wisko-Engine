using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// Turns Wisko materials into <see cref="GpuMaterial"/>s (linear colours, textures from the cache). Rules
/// (docs/graphics.md, "Materials"):
/// <list type="bullet">
/// <item>Primitive: <c>Part.Color</c> (sRGB) x the material's base colour, its values and project textures.</item>
/// <item>Model material without an override: as imported. glTF/GLB colours are linear and its metallic-roughness
/// values are used; OBJ/FBX colours are display colours (converted to linear), metallic is 0 unless the file has it,
/// roughness comes from the file, else is estimated from Phong shininess, else 0.6. glTF single-sided materials cull
/// back faces; OBJ/FBX draw both sides (those formats rarely say). The instance's Part.Color tints it.</item>
/// <item>Model material with an override: the override's values, its textures ("@model:N" = the file's own images);
/// the tint still applies.</item>
/// </list>
/// Missing or failed textures resolve to null (the slot is simply not used), reported once by the cache.
/// </summary>
internal sealed class MaterialResolver
{
    private readonly TextureCache _textures;

    public MaterialResolver(TextureCache textures) => _textures = textures;

    public string? ProjectRoot { get; set; }

    public GpuMaterial ForPrimitive(Part part)
    {
        var material = part.Material ?? SurfaceMaterial.Default;
        var color = ColorSpace.SrgbToLinear(part.Color);
        var tint = ColorSpace.SrgbToLinear(material.BaseColor);
        return FromSurface(material, new Vector3(color.X * tint.X, color.Y * tint.Y, color.Z * tint.Z), asset: null, cull: !material.DoubleSided, vertexSrgb: false);
    }

    public GpuMaterial ForModel(Part part, ModelAsset asset, int meshIndex)
    {
        var mesh = asset.Model.Meshes[meshIndex];
        var tint = ColorSpace.SrgbToLinear(part.Color);
        var gltf = asset.Model.SourceFormat is "gltf" or "glb";
        var index = mesh.MaterialIndex;
        if (part.ModelMaterials?.Get(index) is { } surface)
        {
            var baseColor = ColorSpace.SrgbToLinear(surface.BaseColor);
            var imported = index >= 0 && index < asset.Model.Materials.Count ? asset.Model.Materials[index] : null;
            var material = FromSurface(surface, new Vector3(baseColor.X * tint.X, baseColor.Y * tint.Y, baseColor.Z * tint.Z), asset,
                cull: gltf && !surface.DoubleSided, vertexSrgb: !gltf);
            material.Wrap = imported is null ? 0 : ImportedWrap(imported, surface, ProjectRoot);
            return material;
        }

        if (index < 0 || index >= asset.Model.Materials.Count)
        {
            return FromSurface(SurfaceMaterial.Default with { Roughness = 0.6f }, tint, asset, cull: false, vertexSrgb: !gltf);
        }

        return FromImported(asset.Model.Materials[index], asset, gltf, tint);
    }

    /// <summary>
    /// The surface material a model material starts from when it is first edited (its imported look). Without a project
    /// folder, the model's own (its file's full path less its project-relative path) is used for its external images.
    /// </summary>
    public static SurfaceMaterial ImportedAsSurface(ModelAsset asset, int materialIndex, string? projectRoot)
    {
        projectRoot ??= ProjectRootOf(asset);
        var gltf = asset.Model.SourceFormat is "gltf" or "glb";
        if (materialIndex < 0 || materialIndex >= asset.Model.Materials.Count)
        {
            return SurfaceMaterial.Default;
        }

        var m = asset.Model.Materials[materialIndex];
        var linear = new Vector3(m.BaseColor.R, m.BaseColor.G, m.BaseColor.B);
        var display = gltf ? ColorSpace.LinearToSrgb(linear) : linear;
        var emissiveLinear = new Vector3(m.EmissiveColor.R, m.EmissiveColor.G, m.EmissiveColor.B);
        return new SurfaceMaterial
        {
            Mode = m.Unlit ? ShadingMode.Unlit : ShadingMode.Lit,
            BaseColor = display,
            Opacity = global::System.Math.Clamp(m.BaseColor.A * m.Opacity, 0f, 1f),
            Metallic = m.Metallic ?? 0f,
            Roughness = RoughnessOf(m),
            EmissiveColor = gltf ? ColorSpace.LinearToSrgb(emissiveLinear) : emissiveLinear,
            EmissiveIntensity = m.EmissiveStrength,
            NormalStrength = m.NormalScale,
            OcclusionStrength = m.OcclusionStrength,
            AlphaMode = m.AlphaMode switch { ImportedAlphaMode.Mask => AlphaMode.Mask, ImportedAlphaMode.Blend => AlphaMode.Blend, _ => AlphaMode.Opaque },
            AlphaCutoff = m.AlphaCutoff,
            DoubleSided = m.DoubleSided || !gltf,
            BaseColorTexture = ModelReference(m.BaseColorTexture, projectRoot),
            MetallicRoughnessTexture = ModelReference(m.MetallicRoughnessTexture, projectRoot),
            NormalTexture = ModelReference(m.NormalTexture, projectRoot),
            OcclusionTexture = ModelReference(m.OcclusionTexture, projectRoot),
            EmissiveTexture = ModelReference(m.EmissiveTexture, projectRoot)
        }.Sanitized();
    }

    public GpuMaterial ForEnvironment() => new()
    {
        BaseR = 1f, BaseG = 1f, BaseB = 1f, Opacity = 1f,
        Emissive = Vector3.Zero,
        Metallic = 0f, Roughness = 0.85f, NormalStrength = 1f, OcclusionStrength = 1f, AlphaCutoff = 0.5f,
        Mode = ShadingMode.Lit, AlphaMode = AlphaMode.Opaque, ToonBands = 3,
        ReceiveShadows = true, CastShadows = true, VertexColorsSrgb = true, Cull = false, DoubleSided = false,
        UvMode = UvMode.Mesh, UvScaleX = 1f, UvScaleY = 1f
    };

    private GpuMaterial FromSurface(SurfaceMaterial m, Vector3 baseLinear, ModelAsset? asset, bool cull, bool vertexSrgb)
    {
        var emissive = ColorSpace.SrgbToLinear(m.EmissiveColor) * m.EmissiveIntensity;
        return new GpuMaterial
        {
            BaseR = baseLinear.X, BaseG = baseLinear.Y, BaseB = baseLinear.Z, Opacity = m.Opacity,
            Emissive = emissive,
            Metallic = m.Metallic,
            Roughness = m.Roughness,
            NormalStrength = m.NormalStrength,
            OcclusionStrength = m.OcclusionStrength,
            AlphaCutoff = m.AlphaCutoff,
            Mode = m.Mode,
            AlphaMode = m.AlphaMode,
            ToonBands = m.ToonBands,
            FlatShading = m.FlatShading,
            ReceiveShadows = m.ReceiveShadows && m.Mode != ShadingMode.Unlit,
            CastShadows = m.CastShadows,
            DoubleSided = m.DoubleSided,
            VertexColorsSrgb = vertexSrgb,
            Cull = cull && m.AlphaMode != AlphaMode.Blend,
            Outline = m.Outline,
            OutlineColor = ColorSpace.SrgbToLinear(m.OutlineColor),
            OutlineThickness = m.OutlineThickness,
            UvMode = asset is null ? m.UvMode : UvMode.Mesh,
            UvScaleX = m.UvScaleX,
            UvScaleY = m.UvScaleY,
            BaseTexture = Texture(m.BaseColorTexture, asset, srgb: true),
            MetalRoughTexture = Texture(m.MetallicRoughnessTexture, asset, srgb: false),
            NormalTexture = Texture(m.NormalTexture, asset, srgb: false),
            OcclusionTexture = Texture(m.OcclusionTexture, asset, srgb: false),
            EmissiveTexture = Texture(m.EmissiveTexture, asset, srgb: true)
        };
    }

    private GpuMaterial FromImported(ImportedMaterial m, ModelAsset asset, bool gltf, Vector3 tint)
    {
        var baseLinear = gltf
            ? new Vector3(m.BaseColor.R, m.BaseColor.G, m.BaseColor.B)
            : ColorSpace.SrgbToLinear(new Vector3(m.BaseColor.R, m.BaseColor.G, m.BaseColor.B));
        var emissiveLinear = gltf
            ? new Vector3(m.EmissiveColor.R, m.EmissiveColor.G, m.EmissiveColor.B)
            : ColorSpace.SrgbToLinear(new Vector3(m.EmissiveColor.R, m.EmissiveColor.G, m.EmissiveColor.B));
        var alphaMode = m.AlphaMode switch { ImportedAlphaMode.Mask => AlphaMode.Mask, ImportedAlphaMode.Blend => AlphaMode.Blend, _ => AlphaMode.Opaque };
        return new GpuMaterial
        {
            BaseR = baseLinear.X * tint.X, BaseG = baseLinear.Y * tint.Y, BaseB = baseLinear.Z * tint.Z,
            Opacity = global::System.Math.Clamp(m.BaseColor.A * m.Opacity, 0f, 1f),
            Emissive = emissiveLinear * m.EmissiveStrength,
            Metallic = m.Metallic ?? 0f,
            Roughness = RoughnessOf(m),
            NormalStrength = m.NormalScale,
            OcclusionStrength = m.OcclusionStrength,
            AlphaCutoff = m.AlphaCutoff,
            Mode = m.Unlit ? ShadingMode.Unlit : ShadingMode.Lit,
            AlphaMode = alphaMode,
            ToonBands = 3,
            ReceiveShadows = !m.Unlit,
            CastShadows = true,
            DoubleSided = m.DoubleSided || !gltf,
            VertexColorsSrgb = !gltf,
            Cull = gltf && !m.DoubleSided && alphaMode != AlphaMode.Blend,
            UvMode = UvMode.Mesh,
            UvScaleX = 1f,
            UvScaleY = 1f,
            BaseTexture = Imported(m.BaseColorTexture, asset, srgb: true),
            MetalRoughTexture = Imported(m.MetallicRoughnessTexture, asset, srgb: false),
            NormalTexture = Imported(m.NormalTexture, asset, srgb: false),
            OcclusionTexture = Imported(m.OcclusionTexture, asset, srgb: false),
            EmissiveTexture = Imported(m.EmissiveTexture, asset, srgb: true),
            Wrap = ImportedWrap(m, surface: null, projectRoot: null)
        };
    }

    // glTF roughness, else an estimate from Phong shininess (Blinn-Phong exponent to GGX: sqrt(2 / (n + 2))), else 0.6.
    private static float RoughnessOf(ImportedMaterial m)
    {
        if (m.Roughness is { } roughness)
        {
            return roughness;
        }

        if (m.Shininess is { } shininess && shininess > 0f)
        {
            return global::System.Math.Clamp(MathF.Sqrt(2f / (shininess + 2f)), 0.05f, 1f);
        }

        return 0.6f;
    }

    // An imported texture as a material texture reference: "@model:N" for an image inside the model file, else the
    // project-relative path of the file copied next to the model at import (null when it is not inside the project).
    private static string? ModelReference(ImportedTextureReference? reference, string? projectRoot)
    {
        if (reference is null)
        {
            return null;
        }

        if (reference.EmbeddedIndex is { } index)
        {
            return SurfaceMaterial.ModelTexturePrefix + index;
        }

        if (projectRoot is null || reference.ResolvedPath is not { } path)
        {
            return null;
        }

        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(path));
            return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
                ? null
                : SurfaceMaterial.CleanTexturePath(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ProjectRootOf(ModelAsset asset)
    {
        try
        {
            var source = Path.GetFullPath(asset.Model.SourcePath);
            var relative = asset.AssetPath.Replace('/', Path.DirectorySeparatorChar);
            return source.EndsWith(relative, StringComparison.OrdinalIgnoreCase) ? source[..^relative.Length] : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static ImportedTextureReference? ImportedSlot(ImportedMaterial m, int slot) => slot switch
    {
        0 => m.BaseColorTexture,
        1 => m.MetallicRoughnessTexture,
        2 => m.NormalTexture,
        3 => m.OcclusionTexture,
        4 => m.EmissiveTexture,
        _ => null
    };

    // The imported wrap modes per slot; an override keeps a slot's modes while it still uses that slot's own image.
    private static int ImportedWrap(ImportedMaterial m, SurfaceMaterial? surface, string? projectRoot)
    {
        var wrap = 0;
        for (var slot = 0; slot < 5; slot++)
        {
            if (ImportedSlot(m, slot) is { } reference
                && (surface is null || surface.GetTexture((TextureSlot)slot) is { } path && path == ModelReference(reference, projectRoot)))
            {
                wrap |= GpuMaterial.PackWrap(slot, reference.WrapU, reference.WrapV);
            }
        }

        return wrap;
    }

    private GlTexture? Imported(ImportedTextureReference? reference, ModelAsset asset, bool srgb)
    {
        if (reference is null)
        {
            return null;
        }

        if (reference.EmbeddedIndex is { } index)
        {
            return _textures.GetEmbedded(asset, index, srgb);
        }

        return reference.ResolvedFileExists && reference.ResolvedPath is { } path
            ? _textures.GetModelFile(path, $"{asset.AssetPath}: {reference.SourcePath}", srgb)
            : _textures.GetModelFile(reference.ResolvedPath ?? reference.SourcePath, $"{asset.AssetPath}: {reference.SourcePath}", srgb);
    }

    private GlTexture? Texture(string? path, ModelAsset? asset, bool srgb)
    {
        if (path is null)
        {
            return null;
        }

        if (path.StartsWith(SurfaceMaterial.ModelTexturePrefix, StringComparison.Ordinal))
        {
            return asset is not null && int.TryParse(path.AsSpan(SurfaceMaterial.ModelTexturePrefix.Length), out var index)
                ? _textures.GetEmbedded(asset, index, srgb)
                : null;
        }

        return _textures.GetProjectFile(ProjectRoot, path, srgb);
    }
}
