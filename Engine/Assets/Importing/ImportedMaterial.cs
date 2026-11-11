namespace Wisko.Engine.Assets.Importing;

/// <summary>How a texture repeats outside 0..1 along one axis (glTF sampler wrap modes).</summary>
public enum TextureWrap
{
    Repeat,
    Clamp,
    Mirror
}

/// <summary>How an imported material uses its alpha (glTF alphaMode; other formats: Opaque, or Blend below full opacity).</summary>
public enum ImportedAlphaMode
{
    Opaque,
    Mask,
    Blend
}

/// <summary>
/// A material as the source file describes it, in glTF 2.0 metallic-roughness terms where the format has them. Values a
/// format does not carry are null (never invented): the renderer then uses Wisko's documented defaults. Colours are as
/// stored in the file (glTF: linear; OBJ/FBX: as authored, treated as display colours).
/// </summary>
public sealed class ImportedMaterial
{
    public required string Name { get; init; }

    /// <summary>glTF baseColorFactor, otherwise the diffuse colour; white when the source specifies none.</summary>
    public required ColorRgba BaseColor { get; init; }

    public ImportedTextureReference? BaseColorTexture { get; init; }

    /// <summary>glTF metallicFactor (or the format's metalness); null when the file has none.</summary>
    public float? Metallic { get; init; }

    /// <summary>glTF roughnessFactor (or the format's roughness); null when the file has none.</summary>
    public float? Roughness { get; init; }

    /// <summary>Phong/Blinn shininess (OBJ Ns, FBX); used to estimate roughness when the file has no roughness.</summary>
    public float? Shininess { get; init; }

    /// <summary>glTF packing: green = roughness, blue = metallic.</summary>
    public ImportedTextureReference? MetallicRoughnessTexture { get; init; }

    public ImportedTextureReference? NormalTexture { get; init; }

    /// <summary>glTF normalTexture.scale (1 when absent).</summary>
    public float NormalScale { get; init; } = 1f;

    /// <summary>glTF occlusionTexture: red = ambient occlusion.</summary>
    public ImportedTextureReference? OcclusionTexture { get; init; }

    /// <summary>glTF occlusionTexture.strength (1 when absent).</summary>
    public float OcclusionStrength { get; init; } = 1f;

    /// <summary>glTF emissiveFactor / the format's emissive colour; black when none.</summary>
    public ColorRgba EmissiveColor { get; init; } = new(0f, 0f, 0f, 1f);

    public ImportedTextureReference? EmissiveTexture { get; init; }

    /// <summary>KHR_materials_emissive_strength (1 when absent).</summary>
    public float EmissiveStrength { get; init; } = 1f;

    public ImportedAlphaMode AlphaMode { get; init; } = ImportedAlphaMode.Opaque;

    /// <summary>glTF alphaCutoff (0.5 when absent).</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>OBJ "d" / FBX opacity (1 = solid); glTF opacity is the base colour's alpha.</summary>
    public float Opacity { get; init; } = 1f;

    public bool DoubleSided { get; init; }

    /// <summary>KHR_materials_unlit, or an unlit shading model in the file.</summary>
    public bool Unlit { get; init; }

    /// <summary>A texture uses a UV set other than the first (only the first is imported; reported as a warning).</summary>
    public bool UsesSecondUvSet { get; init; }

    /// <summary>Every texture reference of this material (null slots skipped).</summary>
    public IEnumerable<ImportedTextureReference> Textures
    {
        get
        {
            if (BaseColorTexture is not null) yield return BaseColorTexture;
            if (MetallicRoughnessTexture is not null) yield return MetallicRoughnessTexture;
            if (NormalTexture is not null) yield return NormalTexture;
            if (OcclusionTexture is not null) yield return OcclusionTexture;
            if (EmissiveTexture is not null) yield return EmissiveTexture;
        }
    }

    /// <summary>A copy with every texture reference passed through <paramref name="map"/> (re-homing after a copy).</summary>
    public ImportedMaterial WithTextures(Func<ImportedTextureReference, ImportedTextureReference> map) => new()
    {
        Name = Name,
        BaseColor = BaseColor,
        BaseColorTexture = BaseColorTexture is null ? null : map(BaseColorTexture),
        Metallic = Metallic,
        Roughness = Roughness,
        Shininess = Shininess,
        MetallicRoughnessTexture = MetallicRoughnessTexture is null ? null : map(MetallicRoughnessTexture),
        NormalTexture = NormalTexture is null ? null : map(NormalTexture),
        NormalScale = NormalScale,
        OcclusionTexture = OcclusionTexture is null ? null : map(OcclusionTexture),
        OcclusionStrength = OcclusionStrength,
        EmissiveColor = EmissiveColor,
        EmissiveTexture = EmissiveTexture is null ? null : map(EmissiveTexture),
        EmissiveStrength = EmissiveStrength,
        AlphaMode = AlphaMode,
        AlphaCutoff = AlphaCutoff,
        Opacity = Opacity,
        DoubleSided = DoubleSided,
        Unlit = Unlit,
        UsesSecondUvSet = UsesSecondUvSet
    };
}

/// <summary>
/// Where a material's texture comes from: an image inside the model file, or an external file. The path is
/// kept exactly as written in the source so a later project-copy step can carry the file along.
/// </summary>
public sealed class ImportedTextureReference
{
    /// <summary>Reference as stored in the model file (relative, absolute, or "*N" for embedded images).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Index into <see cref="ImportedModel.EmbeddedTextures"/> for images stored in the model file.</summary>
    public int? EmbeddedIndex { get; init; }

    /// <summary>
    /// External file resolved at import time (relative references resolve against the model's folder).
    /// Only valid for the import itself; not to be persisted.
    /// </summary>
    public string? ResolvedPath { get; init; }

    public bool ResolvedFileExists { get; init; }

    /// <summary>How the texture repeats across U (glTF sampler wrapS; other formats: Repeat).</summary>
    public TextureWrap WrapU { get; init; }

    /// <summary>How the texture repeats across V (glTF sampler wrapT; other formats: Repeat).</summary>
    public TextureWrap WrapV { get; init; }

    /// <summary>Which UV set the texture is read with (0 = the first; only the first is imported).</summary>
    public int UvIndex { get; init; }
}

/// <summary>Image bytes copied out of the model file.</summary>
public sealed class ImportedEmbeddedTexture
{
    public required string Name { get; init; }

    /// <summary>
    /// When true, <see cref="Data"/> is an encoded image file (PNG, JPEG, ...) described by <see cref="FormatHint"/>;
    /// otherwise it is raw RGBA8 pixels of <see cref="Width"/> x <see cref="Height"/>.
    /// </summary>
    public required bool IsEncoded { get; init; }

    /// <summary>File-extension hint such as "png" or "jpg" for encoded data.</summary>
    public required string FormatHint { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required byte[] Data { get; init; }
}
