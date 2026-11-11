using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// An OpenGL mesh in the Universal renderer's vertex layout: position (location 0), normal (1), UV (2), tangent with
/// handedness (3), linear RGBA colour (4), and for skinned meshes four bone slots (5) and weights (6). Indexed,
/// 32-bit. Immutable after creation; shared by every object that draws it.
/// </summary>
internal sealed class UniversalMesh : IDisposable
{
    public const int FloatsPerVertex = 16;
    public const int SkinnedFloatsPerVertex = 24;

    private int _vao;
    private int _vbo;
    private int _ebo;

    private UniversalMesh(string name, int indexCount, bool skinned, Vector3 boundsMin, Vector3 boundsMax, long bytes)
    {
        Name = name;
        IndexCount = indexCount;
        Skinned = skinned;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Bytes = bytes;
        LiveCount++;
        TotalBytes += bytes;
    }

    public string Name { get; }

    public int IndexCount { get; }

    public bool Skinned { get; }

    /// <summary>Local bounds (rest pose for skinned meshes).</summary>
    public Vector3 BoundsMin { get; }

    public Vector3 BoundsMax { get; }

    public long Bytes { get; }

    internal static int LiveCount { get; private set; }

    internal static long TotalBytes { get; private set; }

    /// <summary>Uploads <paramref name="vertices"/> (16 floats per vertex, 24 when skinned) and the indices.</summary>
    public static UniversalMesh Create(string name, float[] vertices, uint[] indices, bool skinned)
    {
        var stride = skinned ? SkinnedFloatsPerVertex : FloatsPerVertex;
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (var i = 0; i + 2 < vertices.Length; i += stride)
        {
            min = new Vector3(MathF.Min(min.X, vertices[i]), MathF.Min(min.Y, vertices[i + 1]), MathF.Min(min.Z, vertices[i + 2]));
            max = new Vector3(MathF.Max(max.X, vertices[i]), MathF.Max(max.Y, vertices[i + 1]), MathF.Max(max.Z, vertices[i + 2]));
        }

        if (vertices.Length == 0)
        {
            min = max = Vector3.Zero;
        }

        var mesh = new UniversalMesh(name, indices.Length, skinned, min, max, vertices.Length * sizeof(float) + indices.Length * sizeof(uint))
        {
            _vao = GL.GenVertexArray(),
            _vbo = GL.GenBuffer(),
            _ebo = GL.GenBuffer()
        };

        GL.BindVertexArray(mesh._vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, mesh._vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, mesh._ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
        var bytes = stride * sizeof(float);
        Attribute(0, 3, bytes, 0);
        Attribute(1, 3, bytes, 3);
        Attribute(2, 2, bytes, 6);
        Attribute(3, 4, bytes, 8);
        Attribute(4, 4, bytes, 12);
        if (skinned)
        {
            Attribute(5, 4, bytes, 16);
            Attribute(6, 4, bytes, 20);
        }

        GL.BindVertexArray(0);
        return mesh;
    }

    private static void Attribute(int location, int size, int stride, int offsetFloats)
    {
        GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, stride, offsetFloats * sizeof(float));
        GL.EnableVertexAttribArray(location);
    }

    public void Draw()
    {
        if (_vao == 0)
        {
            return;
        }

        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, 0);
    }

    public void Dispose()
    {
        if (_vao == 0)
        {
            return;
        }

        GL.DeleteBuffer(_ebo);
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        _vao = _vbo = _ebo = 0;
        LiveCount--;
        TotalBytes -= Bytes;
    }
}

/// <summary>Builds Universal vertex arrays: interleaving, missing normals, UVs and tangents.</summary>
internal static class MeshBuilder
{
    /// <summary>
    /// Interleaves streams into 16 (or 24 with <paramref name="joints"/>) floats per vertex. Missing UVs are zero,
    /// missing colours white, missing tangents computed from the UVs (handedness so that bitangent = w x cross(n, t)
    /// points "up" in the image, as glTF defines).
    /// </summary>
    public static float[] Interleave(
        Vector3[] positions, Vector3[] normals, (float U, float V)[]? uvs, float[]? tangents, (float R, float G, float B, float A)[]? colors,
        uint[] indices, float[]? joints = null, float[]? weights = null)
    {
        var count = positions.Length;
        tangents ??= ComputeTangents(positions, normals, uvs, indices);
        var stride = joints is null ? UniversalMesh.FloatsPerVertex : UniversalMesh.SkinnedFloatsPerVertex;
        var data = new float[count * stride];
        for (var v = 0; v < count; v++)
        {
            var o = v * stride;
            data[o] = positions[v].X;
            data[o + 1] = positions[v].Y;
            data[o + 2] = positions[v].Z;
            data[o + 3] = normals[v].X;
            data[o + 4] = normals[v].Y;
            data[o + 5] = normals[v].Z;
            if (uvs is not null)
            {
                data[o + 6] = uvs[v].U;
                data[o + 7] = uvs[v].V;
            }

            data[o + 8] = tangents[v * 4];
            data[o + 9] = tangents[v * 4 + 1];
            data[o + 10] = tangents[v * 4 + 2];
            data[o + 11] = tangents[v * 4 + 3];
            var c = colors?[v] ?? (1f, 1f, 1f, 1f);
            data[o + 12] = c.R;
            data[o + 13] = c.G;
            data[o + 14] = c.B;
            data[o + 15] = c.A;
            if (joints is not null && weights is not null)
            {
                for (var i = 0; i < 4; i++)
                {
                    data[o + 16 + i] = joints[v * 4 + i];
                    data[o + 20 + i] = weights[v * 4 + i];
                }
            }
        }

        return data;
    }

    /// <summary>Per-vertex tangents (x, y, z, handedness) accumulated over the triangles that use each vertex.</summary>
    public static float[] ComputeTangents(Vector3[] positions, Vector3[] normals, (float U, float V)[]? uvs, uint[] indices)
    {
        var count = positions.Length;
        var result = new float[count * 4];
        var tan = new Vector3[count];
        var bit = new Vector3[count];
        if (uvs is not null)
        {
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var i0 = (int)indices[i];
                var i1 = (int)indices[i + 1];
                var i2 = (int)indices[i + 2];
                if (i0 >= count || i1 >= count || i2 >= count)
                {
                    continue;
                }

                var e1 = positions[i1] - positions[i0];
                var e2 = positions[i2] - positions[i0];
                float du1 = uvs[i1].U - uvs[i0].U, dv1 = uvs[i1].V - uvs[i0].V;
                float du2 = uvs[i2].U - uvs[i0].U, dv2 = uvs[i2].V - uvs[i0].V;
                var det = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(det) < 1e-12f)
                {
                    continue;
                }

                var r = 1f / det;
                var t = (e1 * dv2 - e2 * dv1) * r;   // dP/du
                var b = (e2 * du1 - e1 * du2) * r;   // dP/dv
                tan[i0] += t; tan[i1] += t; tan[i2] += t;
                bit[i0] += b; bit[i1] += b; bit[i2] += b;
            }
        }

        for (var v = 0; v < count; v++)
        {
            var n = normals[v];
            var t = tan[v] - n * Vector3.Dot(n, tan[v]);
            if (t.Length < 1e-8f)
            {
                // No UV gradient: any direction perpendicular to the normal.
                var axis = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
                t = Vector3.Cross(axis, n);
            }

            t = t.Normalized();
            var imageUp = new Vector3(-bit[v].X, -bit[v].Y, -bit[v].Z); // UVs have their origin at the top-left: up = -dP/dv
            var w = Vector3.Dot(Vector3.Cross(n, t), imageUp) < 0f ? -1f : 1f;
            result[v * 4] = t.X;
            result[v * 4 + 1] = t.Y;
            result[v * 4 + 2] = t.Z;
            result[v * 4 + 3] = w;
        }

        return result;
    }
}
