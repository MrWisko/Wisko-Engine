using Wisko.Engine.Math;

namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// One triangle mesh. Vertex streams are parallel arrays of equal length; optional streams are null when the
/// source had none. Triangles are counter-clockwise when seen from the front.
/// </summary>
public sealed class ImportedMesh
{
    public required string Name { get; init; }

    public required Vector3[] Positions { get; init; }

    /// <summary>Unit normals; generated (smooth) by the importer when the source had none.</summary>
    public required Vector3[] Normals { get; init; }

    /// <summary>First UV set, origin at the top-left of the image (V grows downwards), or null.</summary>
    public TexCoord[]? Uv0 { get; init; }

    /// <summary>
    /// Tangents from the file (x, y, z and handedness w per vertex: bitangent = w * cross(normal, tangent)), or null; the
    /// renderer computes them from the UVs when a normal map needs them and the file has none.
    /// </summary>
    public float[]? Tangents { get; init; }

    /// <summary>First vertex-colour set (linear RGBA as stored in the file), or null.</summary>
    public ColorRgba[]? Colors { get; init; }

    /// <summary>Triangle list: three indices per triangle into the vertex streams.</summary>
    public required uint[] Indices { get; init; }

    /// <summary>Index into <see cref="ImportedModel.Materials"/>.</summary>
    public required int MaterialIndex { get; init; }

    /// <summary>Bounds of <see cref="Positions"/> in mesh space.</summary>
    public required ImportedBounds Bounds { get; init; }

    /// <summary>Bone weights when the mesh is skinned (deformed by a skeleton); null for a rigid mesh.</summary>
    public ImportedSkin? Skin { get; init; }

    /// <summary>Morph targets (blend shapes) in the file; Wisko draws the base shape only.</summary>
    public int MorphTargetCount { get; init; }

    public int VertexCount => Positions.Length;

    public int TriangleCount => Indices.Length / 3;
}

public readonly record struct TexCoord(float U, float V);

public readonly record struct ColorRgba(float R, float G, float B, float A)
{
    public static ColorRgba White => new(1f, 1f, 1f, 1f);
}
