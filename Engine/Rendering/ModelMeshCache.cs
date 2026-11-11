using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering;

/// <summary>
/// GPU meshes of model assets, created on first draw and shared by every instance of an asset that has the same tint
/// (an instance adds only its transform). Built as <see cref="ColoredMesh"/>, so the same code yields OpenGL or bgfx
/// buffers depending on the active backend; indices stay 32-bit end to end. Owned by <see cref="PartRenderer"/> and
/// released with its meshes (project change, shutdown).
/// <para>Rendering rule (no textures yet): vertex colour = material base colour x vertex colour when the mesh has one
/// x instance tint (<see cref="Engine.World.Part.Color"/>, white by default). glTF/GLB colours are linear by
/// specification and are encoded to sRGB, the space part colours are shown in, before the tint (itself a display
/// colour, like every part colour) is applied; other formats' colours are used as stored.</para>
/// <para>The tint is baked into the vertex colours, so the existing lit path renders it on both backends without a
/// shader change. Each tint in use gets its own copy of the asset's surface meshes (keyed by the 8-bit colour the
/// scene file stores); a copy no instance drew in the last pass is freed, so recolouring never accumulates buffers.</para>
/// <para>Skinned meshes (Animation) get an OpenGL <see cref="SkinnedMesh"/> per tint as well (their bone weights added);
/// every instance shares it and supplies only its bone matrices per draw.</para>
/// </summary>
internal sealed class ModelMeshCache : IDisposable
{
    private readonly Dictionary<ModelAsset, Entry> _entries = new();

    /// <summary>Lit surface mesh of a draw in one tint; mirrored node transforms get a reversed-winding copy.</summary>
    public ColoredMesh GetSurface(ModelAsset asset, ModelDraw draw, Vector3 tint)
    {
        var entry = GetEntry(asset);
        var key = TintKey(tint);
        if (!entry.Tints.TryGetValue(key, out var variant))
        {
            variant = new TintVariant(asset.Model.Meshes.Count);
            entry.Tints.Add(key, variant);
        }

        variant.Used = true;
        var meshes = draw.Mirrored ? variant.Mirrored : variant.Surface;
        return meshes[draw.MeshIndex] ??= BuildSurface(asset, draw.MeshIndex, draw.Mirrored, TintFromKey(key));
    }

    /// <summary>
    /// Frees the tint copies that were not drawn since the previous call. Called after each pass that draws every
    /// part, so a copy survives exactly as long as some instance still uses that tint.
    /// </summary>
    public void ReleaseUnusedTints()
    {
        foreach (var entry in _entries.Values)
        {
            List<uint>? unused = null;
            foreach (var (key, variant) in entry.Tints)
            {
                if (variant.Used)
                {
                    variant.Used = false;
                }
                else
                {
                    (unused ??= []).Add(key);
                }
            }

            foreach (var key in unused ?? [])
            {
                entry.Tints[key].Dispose();
                entry.Tints.Remove(key);
            }
        }
    }

    /// <summary>
    /// Final vertex colour: material base colour x vertex colour (sRGB-encoded for linear glTF/GLB sources), x tint.
    /// A white tint multiplies by exactly 1.
    /// </summary>
    internal static Vector3 EffectiveColor(ColorRgba material, ColorRgba vertexColor, bool linearSource, Vector3 tint)
    {
        var color = new Vector3(material.R * vertexColor.R, material.G * vertexColor.G, material.B * vertexColor.B);
        var display = linearSource ? LinearToSrgb(color) : Saturate(color);
        return new Vector3(display.X * tint.X, display.Y * tint.Y, display.Z * tint.Z);
    }

    /// <summary>Single-colour copy of a mesh for the selection rim (unlit, so winding does not matter).</summary>
    public ColoredMesh GetOutline(ModelAsset asset, int meshIndex, Vector3 color)
    {
        var entry = GetEntry(asset);
        return entry.Outline[meshIndex] ??= BuildOutline(asset, meshIndex, color);
    }

    /// <summary>
    /// OpenGL skinned surface of a mesh in one tint (the same colours as <see cref="GetSurface"/>, plus its bone weights);
    /// the skinned lit shader deforms it with the instance's bone matrices.
    /// </summary>
    public SkinnedMesh GetSkinnedSurface(ModelAsset asset, int meshIndex, Vector3 tint)
    {
        var entry = GetEntry(asset);
        var key = TintKey(tint);
        if (!entry.Tints.TryGetValue(key, out var variant))
        {
            variant = new TintVariant(asset.Model.Meshes.Count);
            entry.Tints.Add(key, variant);
        }

        variant.Used = true;
        var mesh = asset.Model.Meshes[meshIndex];
        return variant.Skinned[meshIndex] ??= SkinnedMesh.Create($"{asset.AssetPath}#{meshIndex}_Skinned", BuildVertices(asset, meshIndex, TintFromKey(key)), mesh.Skin!, mesh.Indices);
    }

    /// <summary>OpenGL skinned single-colour copy for the selection rim.</summary>
    public SkinnedMesh GetSkinnedOutline(ModelAsset asset, int meshIndex, Vector3 color)
    {
        var entry = GetEntry(asset);
        var mesh = asset.Model.Meshes[meshIndex];
        if (entry.SkinnedOutline[meshIndex] is { } existing)
        {
            return existing;
        }

        var vertices = new Vertex[mesh.VertexCount];
        for (var v = 0; v < vertices.Length; v++)
        {
            vertices[v] = new Vertex(mesh.Positions[v], color);
        }

        return entry.SkinnedOutline[meshIndex] = SkinnedMesh.Create($"{asset.AssetPath}#{meshIndex}_SkinnedOutline", vertices, mesh.Skin!, mesh.Indices);
    }

    /// <summary>
    /// The lit vertices of a mesh in one tint (what its surface meshes are built from): position, effective colour and
    /// normal. bgfx's CPU skinning starts from these.
    /// </summary>
    internal static Vertex[] BuildVertices(ModelAsset asset, int meshIndex, Vector3 tint)
    {
        var model = asset.Model;
        var mesh = model.Meshes[meshIndex];
        var material = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < model.Materials.Count
            ? model.Materials[mesh.MaterialIndex].BaseColor
            : ColorRgba.White;
        var linearColors = model.SourceFormat is "gltf" or "glb";

        var vertices = new Vertex[mesh.VertexCount];
        for (var v = 0; v < vertices.Length; v++)
        {
            var color = EffectiveColor(material, mesh.Colors?[v] ?? ColorRgba.White, linearColors, tint);
            vertices[v] = new Vertex(mesh.Positions[v], color, mesh.Normals[v]);
        }

        return vertices;
    }

    /// <summary>The key a tint's meshes are cached under (the scene file's 8-bit colour).</summary>
    internal static uint TintKeyOf(Vector3 tint) => TintKey(tint);

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    public void Dispose() => Clear();

    private Entry GetEntry(ModelAsset asset)
    {
        if (!_entries.TryGetValue(asset, out var entry))
        {
            entry = new Entry(asset.Model.Meshes.Count);
            _entries.Add(asset, entry);
        }

        return entry;
    }

    private static ColoredMesh BuildSurface(ModelAsset asset, int meshIndex, bool reverseWinding, Vector3 tint)
    {
        var mesh = asset.Model.Meshes[meshIndex];
        var vertices = BuildVertices(asset, meshIndex, tint);
        var indices = mesh.Indices;
        if (reverseWinding)
        {
            indices = (uint[])indices.Clone();
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        return ColoredMesh.FromIndexedVertices($"{asset.AssetPath}#{meshIndex}", vertices, indices);
    }

    private static ColoredMesh BuildOutline(ModelAsset asset, int meshIndex, Vector3 color)
    {
        var mesh = asset.Model.Meshes[meshIndex];
        var vertices = new Vertex[mesh.VertexCount];
        for (var v = 0; v < vertices.Length; v++)
        {
            vertices[v] = new Vertex(mesh.Positions[v], color);
        }

        return ColoredMesh.FromIndexedVertices($"{asset.AssetPath}#{meshIndex}_Outline", vertices, mesh.Indices);
    }

    // The scene file stores colours as 0-255 per channel; tints are keyed (and baked) at that precision.
    private static uint TintKey(Vector3 tint) => (TintByte(tint.X) << 16) | (TintByte(tint.Y) << 8) | TintByte(tint.Z);

    private static uint TintByte(float channel) => (uint)global::System.Math.Clamp(MathF.Round(channel * 255f), 0f, 255f);

    private static Vector3 TintFromKey(uint key) =>
        new(((key >> 16) & 0xFF) / 255f, ((key >> 8) & 0xFF) / 255f, (key & 0xFF) / 255f);

    private static Vector3 Saturate(Vector3 c) =>
        new(global::System.Math.Clamp(c.X, 0f, 1f), global::System.Math.Clamp(c.Y, 0f, 1f), global::System.Math.Clamp(c.Z, 0f, 1f));

    private static Vector3 LinearToSrgb(Vector3 c) => new(Encode(c.X), Encode(c.Y), Encode(c.Z));

    private static float Encode(float linear)
    {
        var x = global::System.Math.Clamp(linear, 0f, 1f);
        return x <= 0.0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    }

    private static void DisposeAll(ColoredMesh?[] meshes)
    {
        for (var i = 0; i < meshes.Length; i++)
        {
            meshes[i]?.Dispose();
            meshes[i] = null;
        }
    }

    private static void DisposeAll(SkinnedMesh?[] meshes)
    {
        for (var i = 0; i < meshes.Length; i++)
        {
            meshes[i]?.Dispose();
            meshes[i] = null;
        }
    }

    // Per asset: one surface copy per tint in use, plus one single-colour outline set (the rim ignores the tint).
    private sealed class Entry(int meshCount) : IDisposable
    {
        public Dictionary<uint, TintVariant> Tints { get; } = new();

        public ColoredMesh?[] Outline { get; } = new ColoredMesh?[meshCount];

        public SkinnedMesh?[] SkinnedOutline { get; } = new SkinnedMesh?[meshCount];

        public void Dispose()
        {
            foreach (var variant in Tints.Values)
            {
                variant.Dispose();
            }

            Tints.Clear();
            DisposeAll(Outline);
            DisposeAll(SkinnedOutline);
        }
    }

    private sealed class TintVariant(int meshCount) : IDisposable
    {
        public ColoredMesh?[] Surface { get; } = new ColoredMesh?[meshCount];

        public ColoredMesh?[] Mirrored { get; } = new ColoredMesh?[meshCount];

        public SkinnedMesh?[] Skinned { get; } = new SkinnedMesh?[meshCount];

        public bool Used { get; set; }

        public void Dispose()
        {
            DisposeAll(Surface);
            DisposeAll(Mirrored);
            DisposeAll(Skinned);
        }
    }
}
