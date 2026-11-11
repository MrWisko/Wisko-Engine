using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

/// <summary>
/// An OpenGL vertex buffer refilled every frame (scripts' debug lines and particles): the same interleaved layout as
/// <see cref="ColoredMesh"/> (position, colour, normal: 9 floats per vertex), so the unlit and environment shaders draw
/// it. The buffer grows as needed and is orphaned on every refill (no stall on the previous frame's draw).
/// </summary>
internal sealed class DynamicColoredMesh : IDisposable
{
    public const int FloatsPerVertex = 9;

    private int _vao;
    private int _vbo;
    private int _capacityFloats;
    private int _vertexCount;

    public int VertexCount => _vertexCount;

    /// <summary>Replaces the contents with <paramref name="vertexCount"/> vertices from <paramref name="packed"/>.</summary>
    public void Update(float[] packed, int vertexCount)
    {
        _vertexCount = vertexCount;
        if (vertexCount == 0)
        {
            return;
        }

        if (_vao == 0)
        {
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            const int stride = FloatsPerVertex * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.BindVertexArray(0);
        }

        var floats = vertexCount * FloatsPerVertex;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        if (floats > _capacityFloats)
        {
            _capacityFloats = global::System.Math.Max(floats, _capacityFloats * 2);
        }

        GL.BufferData(BufferTarget.ArrayBuffer, _capacityFloats * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, floats * sizeof(float), packed);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public void Draw(PrimitiveType primitive)
    {
        if (_vao == 0 || _vertexCount == 0)
        {
            return;
        }

        GL.BindVertexArray(_vao);
        GL.DrawArrays(primitive, 0, _vertexCount);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_vbo != 0)
        {
            GL.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_vao != 0)
        {
            GL.DeleteVertexArray(_vao);
            _vao = 0;
        }

        _capacityFloats = 0;
        _vertexCount = 0;
    }
}
