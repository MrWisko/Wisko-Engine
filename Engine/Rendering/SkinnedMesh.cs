using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Assets.Importing;

namespace Wisko.Engine.Rendering;

/// <summary>
/// OpenGL buffers of one skinned mesh: the <see cref="ColoredMesh"/> layout (position, colour, normal at locations 0-2)
/// plus four bone slots (location 3, as floats) and their weights (location 4). The skinned shaders move each vertex by
/// its bones' matrices (uploaded per draw); the buffers themselves never change, so every instance of an asset shares
/// them. OpenGL only: bgfx skins on the CPU (see PartRenderer.Bgfx).
/// </summary>
internal sealed class SkinnedMesh : IDisposable
{
    private const int FloatsPerVertex = 17; // position 3, colour 3, normal 3, joints 4, weights 4

    private int _vao;
    private int _vbo;
    private int _ebo;

    private SkinnedMesh(string name, int indexCount)
    {
        DebugName = name;
        IndexCount = indexCount;
    }

    public string DebugName { get; }

    public int IndexCount { get; }

    /// <summary>Live skinned meshes (leak checks across project changes).</summary>
    internal static int LiveCount { get; private set; }

    public static SkinnedMesh Create(string name, Vertex[] vertices, ImportedSkin skin, uint[] indices)
    {
        const int influences = ImportedSkin.MaxInfluences;
        var data = new float[vertices.Length * FloatsPerVertex];
        for (var v = 0; v < vertices.Length; v++)
        {
            var o = v * FloatsPerVertex;
            data[o] = vertices[v].Position.X;
            data[o + 1] = vertices[v].Position.Y;
            data[o + 2] = vertices[v].Position.Z;
            data[o + 3] = vertices[v].Color.X;
            data[o + 4] = vertices[v].Color.Y;
            data[o + 5] = vertices[v].Color.Z;
            data[o + 6] = vertices[v].Normal.X;
            data[o + 7] = vertices[v].Normal.Y;
            data[o + 8] = vertices[v].Normal.Z;
            for (var i = 0; i < influences; i++)
            {
                data[o + 9 + i] = skin.Joints[v * influences + i];
                data[o + 13 + i] = skin.Weights[v * influences + i];
            }
        }

        var mesh = new SkinnedMesh(name, indices.Length)
        {
            _vao = GL.GenVertexArray(),
            _vbo = GL.GenBuffer(),
            _ebo = GL.GenBuffer()
        };

        GL.BindVertexArray(mesh._vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, mesh._vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, mesh._ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        const int stride = FloatsPerVertex * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, 13 * sizeof(float));
        GL.EnableVertexAttribArray(4);
        GL.BindVertexArray(0);
        LiveCount++;
        return mesh;
    }

    public void Draw()
    {
        if (_vao == 0)
        {
            return;
        }

        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
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
    }
}

/// <summary>
/// The bone-matrix uniform block every skinned draw reads (<c>uniform BoneBlock { mat4 uBones[256]; }</c>, std140,
/// binding point <see cref="BindingPoint"/>): one OpenGL buffer, refilled with a mesh's matrices before its draw.
/// </summary>
internal sealed class BoneUniformBuffer : IDisposable
{
    public const int BindingPoint = 0;
    public const string BlockName = "BoneBlock";

    private int _buffer;

    /// <summary>Bytes the block occupies: 256 column-major mat4.</summary>
    public const int SizeInBytes = Wisko.Engine.Animation.SkinningLimits.MaxPaletteSize * 64;

    public void Initialize()
    {
        _buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, _buffer);
        GL.BufferData(BufferTarget.UniformBuffer, SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, BindingPoint, _buffer);
    }

    /// <summary>Uploads a palette (16 column-major floats per matrix, at most 256 matrices) for the next draws.</summary>
    public void Upload(float[] paletteFloats)
    {
        if (_buffer == 0 || paletteFloats.Length == 0)
        {
            return;
        }

        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, BindingPoint, _buffer);
        GL.BindBuffer(BufferTarget.UniformBuffer, _buffer);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, global::System.Math.Min(paletteFloats.Length * sizeof(float), SizeInBytes), paletteFloats);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
    }

    public void Dispose()
    {
        if (_buffer != 0)
        {
            GL.DeleteBuffer(_buffer);
            _buffer = 0;
        }
    }
}
