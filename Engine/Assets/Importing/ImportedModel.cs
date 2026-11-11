using Wisko.Engine.Math;

namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// A decoded model in Wisko's own CPU representation and coordinate convention (right-handed, Y up,
/// counter-clockwise front faces, UV origin at the top-left of the image). Independent of the importer that
/// produced it and of any renderer; holds no native memory.
/// </summary>
public sealed class ImportedModel
{
    /// <summary>File the model was decoded from. Informational only; nothing should depend on it later.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Source format as reported by the file extension (e.g. "gltf", "glb", "obj", "fbx").</summary>
    public required string SourceFormat { get; init; }

    public required IReadOnlyList<ImportedMesh> Meshes { get; init; }

    public required IReadOnlyList<ImportedMaterial> Materials { get; init; }

    /// <summary>Images stored inside the model file (e.g. GLB); referenced by <see cref="ImportedTextureReference.EmbeddedIndex"/>.</summary>
    public required IReadOnlyList<ImportedEmbeddedTexture> EmbeddedTextures { get; init; }

    public required ImportedNode RootNode { get; init; }

    /// <summary>Bounds of every mesh instance with node transforms applied (model space).</summary>
    public required ImportedBounds Bounds { get; init; }

    /// <summary>Non-fatal notes from conversion (e.g. skipped point/line primitives).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Animation clips in file order (none for a static model).</summary>
    public IReadOnlyList<ImportedAnimation> Animations { get; init; } = [];
}

/// <summary>Scene-graph node: local transform relative to its parent, plus the meshes it instances.</summary>
public sealed class ImportedNode
{
    public required string Name { get; init; }

    /// <summary>Local transform in the engine's <see cref="Matrix4"/> layout (column vectors, translation in M14/M24/M34).</summary>
    public required Matrix4 LocalTransform { get; init; }

    /// <summary>Indices into <see cref="ImportedModel.Meshes"/>.</summary>
    public required IReadOnlyList<int> MeshIndices { get; init; }

    public required IReadOnlyList<ImportedNode> Children { get; init; }
}

public readonly record struct ImportedBounds(Vector3 Min, Vector3 Max)
{
    public static ImportedBounds Empty => new(
        new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
        new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public ImportedBounds Include(Vector3 point) => new(
        new Vector3(MathF.Min(Min.X, point.X), MathF.Min(Min.Y, point.Y), MathF.Min(Min.Z, point.Z)),
        new Vector3(MathF.Max(Max.X, point.X), MathF.Max(Max.Y, point.Y), MathF.Max(Max.Z, point.Z)));
}
