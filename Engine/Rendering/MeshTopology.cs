namespace Wisko.Engine.Rendering;

/// <summary>Primitive topology of a <see cref="ColoredMesh"/>, independent of the graphics backend.</summary>
public enum MeshTopology
{
    Triangles,
    Lines,
    LineStrip,
    LineLoop
}

/// <summary>Backend GPU storage for one <see cref="ColoredMesh"/> (used instead of GL buffers on non-GL backends).</summary>
internal interface IGpuMesh : IDisposable
{
}

/// <summary>Creates <see cref="IGpuMesh"/> objects; installed on <see cref="ColoredMesh"/> for non-GL backends.</summary>
internal interface IGpuMeshFactory
{
    IGpuMesh Create(Vertex[] vertices, uint[]? indices, MeshTopology topology);
}
