using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

public sealed class ColoredMesh : IDisposable
{
    private int _vao;
    private int _vbo;
    private int _ebo;
    private int _vertexCount;
    private int _indexCount;
    private bool _uploaded;
    private bool _indexed;
    private IGpuMesh? _gpu;

    /// <summary>
    /// When set (bgfx), meshes are created through this backend instead of OpenGL buffers. Null = OpenGL.
    /// </summary>
    internal static IGpuMeshFactory? GpuBackend { get; set; }

    /// <summary>Backend mesh when created through <see cref="GpuBackend"/>; null for OpenGL meshes.</summary>
    internal IGpuMesh? Gpu => _gpu;

    public string DebugName { get; init; } = "Mesh";
    public MeshTopology Topology { get; private set; } = MeshTopology.Triangles;
    public int VertexCount => _vertexCount;
    public int IndexCount => _indexCount;

    public static ColoredMesh FromVertices(string name, Vertex[] vertices, MeshTopology topology = MeshTopology.Triangles)
    {
        var mesh = new ColoredMesh { DebugName = name, Topology = topology };
        if (GpuBackend is { } backend)
        {
            mesh.CreateOnBackend(backend, vertices, null);
        }
        else
        {
            mesh.Upload(vertices);
        }

        return mesh;
    }

    public static ColoredMesh FromIndexedVertices(string name, Vertex[] vertices, uint[] indices)
    {
        var mesh = new ColoredMesh { DebugName = name, Topology = MeshTopology.Triangles, _indexed = true };
        if (GpuBackend is { } backend)
        {
            mesh.CreateOnBackend(backend, vertices, indices);
        }
        else
        {
            mesh.UploadIndexed(vertices, indices);
        }

        return mesh;
    }

    /// <summary>OpenGL draw. Backend meshes (bgfx) are submitted by their backend instead.</summary>
    public void Draw()
    {
        if (!_uploaded)
        {
            return;
        }

        if (_gpu is not null)
        {
            throw new InvalidOperationException($"Mesh '{DebugName}' lives on a non-OpenGL backend; submit it through that backend.");
        }

        var primitive = ToGlPrimitive(Topology);
        GL.BindVertexArray(_vao);

        if (_indexed)
        {
            GL.DrawElements(primitive, _indexCount, DrawElementsType.UnsignedInt, 0);
        }
        else
        {
            GL.DrawArrays(primitive, 0, _vertexCount);
        }

        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_gpu is not null)
        {
            _gpu.Dispose();
            _gpu = null;
            _uploaded = false;
            return;
        }

        DisposeGl();
    }

    // Separate method so backend meshes never JIT-compile (or load) the OpenGL bindings.
    private void DisposeGl()
    {
        if (_ebo != 0)
        {
            GL.DeleteBuffer(_ebo);
            _ebo = 0;
        }

        if (_vao != 0)
        {
            GL.DeleteVertexArray(_vao);
            _vao = 0;
        }

        if (_vbo != 0)
        {
            GL.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        _uploaded = false;
    }

    private static PrimitiveType ToGlPrimitive(MeshTopology topology) => topology switch
    {
        MeshTopology.Lines => PrimitiveType.Lines,
        MeshTopology.LineStrip => PrimitiveType.LineStrip,
        MeshTopology.LineLoop => PrimitiveType.LineLoop,
        _ => PrimitiveType.Triangles
    };

    private void CreateOnBackend(IGpuMeshFactory backend, Vertex[] vertices, uint[]? indices)
    {
        _vertexCount = vertices.Length;
        _indexCount = indices?.Length ?? 0;
        _gpu = backend.Create(vertices, indices, Topology);
        _uploaded = true;
    }

    private void Upload(Vertex[] vertices)
    {
        _vertexCount = vertices.Length;
        _indexCount = 0;

        var data = PackVertexData(vertices);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        ConfigureVertexAttributes();

        GL.BindVertexArray(0);
        _uploaded = true;
    }

    private void UploadIndexed(Vertex[] vertices, uint[] indices)
    {
        _vertexCount = vertices.Length;
        _indexCount = indices.Length;

        var data = PackVertexData(vertices);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        ConfigureVertexAttributes();

        GL.BindVertexArray(0);
        _uploaded = true;
    }

    private static float[] PackVertexData(Vertex[] vertices)
    {
        var data = new float[vertices.Length * 9];
        for (var i = 0; i < vertices.Length; i++)
        {
            var offset = i * 9;
            data[offset + 0] = vertices[i].Position.X;
            data[offset + 1] = vertices[i].Position.Y;
            data[offset + 2] = vertices[i].Position.Z;
            data[offset + 3] = vertices[i].Color.X;
            data[offset + 4] = vertices[i].Color.Y;
            data[offset + 5] = vertices[i].Color.Z;
            data[offset + 6] = vertices[i].Normal.X;
            data[offset + 7] = vertices[i].Normal.Y;
            data[offset + 8] = vertices[i].Normal.Z;
        }

        return data;
    }

    private static void ConfigureVertexAttributes()
    {
        // Interleaved layout: position (loc 0), color (loc 1), normal (loc 2). The unlit shader ignores
        // loc 2; only the lit shader reads it. Zero normals mark flat primitives (derivative-normal path).
        const int stride = 9 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
    }
}
