using Wisko.Engine.Math;

namespace Wisko.Engine.Graphics;

/// <summary>How a surface is lit.</summary>
public enum ShadingMode
{
    /// <summary>Physically based (metallic-roughness, glTF conventions): lights, shadows, ambient and reflections.</summary>
    Lit,

    /// <summary>Lambert diffuse plus a Blinn-Phong highlight: cheaper, not physically based, no reflections.</summary>
    SimpleLit,

    /// <summary>The colour (and texture) as is: no lighting or shadows. Fog and emission still apply.</summary>
    Unlit,

    /// <summary>Cel shading: light in a few hard bands, with an optional outline.</summary>
    Toon
}

/// <summary>How a surface's alpha (opacity x the base-colour texture's alpha) is used.</summary>
public enum AlphaMode
{
    /// <summary>Alpha ignored: fully solid.</summary>
    Opaque,

    /// <summary>Cut-out: pixels with alpha below <see cref="SurfaceMaterial.AlphaCutoff"/> are not drawn (leaves, fences).</summary>
    Mask,

    /// <summary>See-through: blended over what is behind, drawn after solid surfaces, back to front.</summary>
    Blend
}

/// <summary>Where texture coordinates come from.</summary>
public enum UvMode
{
    /// <summary>The mesh's own UVs (a primitive's faces 0-1 each), repeated <see cref="SurfaceMaterial.UvScaleX"/> x <see cref="SurfaceMaterial.UvScaleY"/> times.</summary>
    Mesh,

    /// <summary>
    /// Projected from the Part's own axes in studs, so a texture keeps its size when the Part is resized: one repeat
    /// every <see cref="SurfaceMaterial.UvScaleX"/> x <see cref="SurfaceMaterial.UvScaleY"/> studs.
    /// </summary>
    Studs
}

/// <summary>The textures a material can use.</summary>
public enum TextureSlot
{
    /// <summary>Colour (sRGB) and alpha.</summary>
    BaseColor,

    /// <summary>Data (linear): green = roughness, blue = metallic (glTF packing).</summary>
    MetallicRoughness,

    /// <summary>Data (linear): tangent-space normal map (OpenGL convention, +Y up).</summary>
    Normal,

    /// <summary>Data (linear): red = ambient occlusion.</summary>
    Occlusion,

    /// <summary>Colour (sRGB): emitted light.</summary>
    Emissive
}

/// <summary>
/// A surface's look: Wisko's one material model (metallic-roughness PBR, as glTF 2.0 defines it), plus the alternative
/// shading modes. Plain immutable data with no renderer types, shared by both backends and saved in Scene.json (only
/// fields that differ from <see cref="Default"/>).
/// <para>Colours are sRGB display values on the 0-255 grid, as everywhere in Wisko; the renderer converts them to
/// linear. A primitive Part's colour is <see cref="World.Part.Color"/> (its material's <see cref="BaseColor"/> stays
/// white); an imported model's material keeps its own base colour, and the Part's colour tints it.</para>
/// <para>Texture paths are relative to the project folder ("Assets/Textures/Bricks.png"), or "@model:N" for the Nth
/// image stored inside the Part's model file.</para>
/// </summary>
public sealed record SurfaceMaterial
{
    /// <summary>Prefix of a texture reference to an image stored inside the Part's model file ("@model:2").</summary>
    public const string ModelTexturePrefix = "@model:";

    public static SurfaceMaterial Default { get; } = new();

    public ShadingMode Mode { get; init; } = ShadingMode.Lit;

    /// <summary>Base colour multiplier (sRGB); white for primitives, whose colour is the Part's.</summary>
    public Vector3 BaseColor { get; init; } = new(1f, 1f, 1f);

    /// <summary>1 solid, 0 invisible; used by <see cref="AlphaMode.Mask"/> and <see cref="AlphaMode.Blend"/>.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>0 dielectric (plastic, wood, stone), 1 metal.</summary>
    public float Metallic { get; init; }

    /// <summary>0 mirror-smooth, 1 fully rough.</summary>
    public float Roughness { get; init; } = 0.55f;

    /// <summary>Emitted light colour (sRGB); black emits nothing.</summary>
    public Vector3 EmissiveColor { get; init; } = Vector3.Zero;

    /// <summary>Multiplier on the emitted light (above 1 it can bloom).</summary>
    public float EmissiveIntensity { get; init; } = 1f;

    public float NormalStrength { get; init; } = 1f;

    public float OcclusionStrength { get; init; } = 1f;

    public AlphaMode AlphaMode { get; init; } = AlphaMode.Opaque;

    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Back faces are drawn (and lit) too.</summary>
    public bool DoubleSided { get; init; }

    /// <summary>Faceted look: each triangle lit with its own flat normal instead of smooth vertex normals.</summary>
    public bool FlatShading { get; init; }

    /// <summary>Toon: how many light bands (2-8).</summary>
    public int ToonBands { get; init; } = 3;

    /// <summary>A gameplay outline around the object (any shading mode; separate from the editor's selection outline).</summary>
    public bool Outline { get; init; }

    public Vector3 OutlineColor { get; init; } = Vector3.Zero;

    /// <summary>Outline width in studs.</summary>
    public float OutlineThickness { get; init; } = 0.05f;

    public bool CastShadows { get; init; } = true;

    public bool ReceiveShadows { get; init; } = true;

    public string? BaseColorTexture { get; init; }

    public string? MetallicRoughnessTexture { get; init; }

    public string? NormalTexture { get; init; }

    public string? OcclusionTexture { get; init; }

    public string? EmissiveTexture { get; init; }

    public UvMode UvMode { get; init; } = UvMode.Mesh;

    public float UvScaleX { get; init; } = 1f;

    public float UvScaleY { get; init; } = 1f;

    public string? GetTexture(TextureSlot slot) => slot switch
    {
        TextureSlot.BaseColor => BaseColorTexture,
        TextureSlot.MetallicRoughness => MetallicRoughnessTexture,
        TextureSlot.Normal => NormalTexture,
        TextureSlot.Occlusion => OcclusionTexture,
        TextureSlot.Emissive => EmissiveTexture,
        _ => null
    };

    public SurfaceMaterial WithTexture(TextureSlot slot, string? path) => (slot switch
    {
        TextureSlot.BaseColor => this with { BaseColorTexture = path },
        TextureSlot.MetallicRoughness => this with { MetallicRoughnessTexture = path },
        TextureSlot.Normal => this with { NormalTexture = path },
        TextureSlot.Occlusion => this with { OcclusionTexture = path },
        TextureSlot.Emissive => this with { EmissiveTexture = path },
        _ => this
    }).Sanitized();

    /// <summary>Whether the texture holds colour (decoded from sRGB) rather than data (used as stored).</summary>
    public static bool IsColorTexture(TextureSlot slot) => slot is TextureSlot.BaseColor or TextureSlot.Emissive;

    public bool HasAnyTexture =>
        BaseColorTexture is not null || MetallicRoughnessTexture is not null || NormalTexture is not null
        || OcclusionTexture is not null || EmissiveTexture is not null;

    /// <summary>Whether it emits any light.</summary>
    public bool IsEmissive => (EmissiveColor.X > 0f || EmissiveColor.Y > 0f || EmissiveColor.Z > 0f || EmissiveTexture is not null) && EmissiveIntensity > 0f;

    /// <summary>
    /// Fully see-through and not glowing: nothing of it is drawn, in either renderer (Add Light's parts: the light shows,
    /// the part does not; the editor marks it with an icon). Its shape still collides (if CanCollide) and can be selected.
    /// </summary>
    public bool IsInvisible => AlphaMode == AlphaMode.Blend && Opacity <= 0f && !IsEmissive;

    /// <summary>
    /// Every value in range and finite (non-finite numbers take the default), colours on the 0-255 grid, texture
    /// paths that are usable project-relative paths (others dropped). Idempotent.
    /// </summary>
    public SurfaceMaterial Sanitized()
    {
        var d = Default;
        return new SurfaceMaterial
        {
            Mode = Enum.IsDefined(Mode) ? Mode : d.Mode,
            BaseColor = ColorSpace.Snap(BaseColor),
            Opacity = MathUtil.ClampFinite(Opacity, 0f, 1f, d.Opacity),
            Metallic = MathUtil.ClampFinite(Metallic, 0f, 1f, d.Metallic),
            Roughness = MathUtil.ClampFinite(Roughness, 0f, 1f, d.Roughness),
            EmissiveColor = ColorSpace.Snap(EmissiveColor),
            EmissiveIntensity = MathUtil.ClampFinite(EmissiveIntensity, 0f, 100f, d.EmissiveIntensity),
            NormalStrength = MathUtil.ClampFinite(NormalStrength, 0f, 4f, d.NormalStrength),
            OcclusionStrength = MathUtil.ClampFinite(OcclusionStrength, 0f, 1f, d.OcclusionStrength),
            AlphaMode = Enum.IsDefined(AlphaMode) ? AlphaMode : d.AlphaMode,
            AlphaCutoff = MathUtil.ClampFinite(AlphaCutoff, 0f, 1f, d.AlphaCutoff),
            DoubleSided = DoubleSided,
            FlatShading = FlatShading,
            ToonBands = global::System.Math.Clamp(ToonBands, 2, 8),
            Outline = Outline,
            OutlineColor = ColorSpace.Snap(OutlineColor),
            OutlineThickness = MathUtil.ClampFinite(OutlineThickness, 0.005f, 2f, d.OutlineThickness),
            CastShadows = CastShadows,
            ReceiveShadows = ReceiveShadows,
            BaseColorTexture = CleanTexturePath(BaseColorTexture),
            MetallicRoughnessTexture = CleanTexturePath(MetallicRoughnessTexture),
            NormalTexture = CleanTexturePath(NormalTexture),
            OcclusionTexture = CleanTexturePath(OcclusionTexture),
            EmissiveTexture = CleanTexturePath(EmissiveTexture),
            UvMode = Enum.IsDefined(UvMode) ? UvMode : d.UvMode,
            UvScaleX = MathUtil.ClampFinite(UvScaleX, 0.01f, 1000f, d.UvScaleX),
            UvScaleY = MathUtil.ClampFinite(UvScaleY, 0.01f, 1000f, d.UvScaleY)
        };
    }

    /// <summary>
    /// A usable texture reference, or null: "@model:N" (N &gt;= 0), or a relative path inside the project (forward
    /// slashes, no "..", no drive or root, no control characters).
    /// </summary>
    public static string? CleanTexturePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith(ModelTexturePrefix, StringComparison.Ordinal))
        {
            return int.TryParse(p.AsSpan(ModelTexturePrefix.Length), global::System.Globalization.NumberStyles.None,
                global::System.Globalization.CultureInfo.InvariantCulture, out var index) && index >= 0
                ? ModelTexturePrefix + index.ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }

        if (p.Length > 260 || p.StartsWith('/') || p.Contains(':') || p.Any(char.IsControl))
        {
            return null;
        }

        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        return string.Join('/', segments);
    }
}

/// <summary>
/// A named starting point for a material. <c>Apply(material, color)</c> keeps the colour, textures and UVs; color is
/// the object's display colour (a primitive's Part.Color), which some presets use (Neon glows in it).
/// </summary>
public sealed record MaterialPreset(string Name, string Description, Func<SurfaceMaterial, Vector3, SurfaceMaterial> Apply);

/// <summary>Wisko's built-in material presets (editor and Lua: <c>object.Material:ApplyPreset("Metal")</c>).</summary>
public static class MaterialPresets
{
    public static IReadOnlyList<MaterialPreset> All { get; } =
    [
        new("Plastic", "Matte plastic (the default).", (m, _) => Reset(m) with { Roughness = 0.55f }),
        new("SmoothPlastic", "Glossy plastic.", (m, _) => Reset(m) with { Roughness = 0.25f }),
        new("Matte", "Chalky, almost no highlight.", (m, _) => Reset(m) with { Roughness = 0.95f }),
        new("Metal", "Polished metal.", (m, _) => Reset(m) with { Metallic = 1f, Roughness = 0.25f }),
        new("BrushedMetal", "Rough metal.", (m, _) => Reset(m) with { Metallic = 1f, Roughness = 0.55f }),
        new("Glass", "Smooth and see-through.", (m, _) => Reset(m) with { Roughness = 0.05f, Opacity = 0.3f, AlphaMode = AlphaMode.Blend, CastShadows = false }),
        new("Neon", "Glows in its own colour (blooms when Bloom is on).", (m, c) => Reset(m) with { EmissiveColor = ColorSpace.Snap(c), EmissiveIntensity = 2.5f, Roughness = 0.4f }),
        new("Toon", "Cel shading in three bands with an outline.", (m, _) => Reset(m) with { Mode = ShadingMode.Toon, ToonBands = 3, Outline = true }),
        new("Unlit", "Flat colour, no lighting.", (m, _) => Reset(m) with { Mode = ShadingMode.Unlit }),
        new("Simple", "Classic Lambert lighting with a soft highlight.", (m, _) => Reset(m) with { Mode = ShadingMode.SimpleLit, Roughness = 0.5f })
    ];

    public static MaterialPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // The look-related values back to their defaults; colour, textures, UVs and shadow switches are kept.
    private static SurfaceMaterial Reset(SurfaceMaterial m) => SurfaceMaterial.Default with
    {
        BaseColor = m.BaseColor,
        BaseColorTexture = m.BaseColorTexture,
        MetallicRoughnessTexture = m.MetallicRoughnessTexture,
        NormalTexture = m.NormalTexture,
        OcclusionTexture = m.OcclusionTexture,
        EmissiveTexture = m.EmissiveTexture,
        NormalStrength = m.NormalStrength,
        OcclusionStrength = m.OcclusionStrength,
        UvMode = m.UvMode,
        UvScaleX = m.UvScaleX,
        UvScaleY = m.UvScaleY,
        CastShadows = m.CastShadows,
        ReceiveShadows = m.ReceiveShadows
    };
}
