using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// A linked GLSL program with cached uniform locations and allocation-free uploads (the Universal renderer sets
/// uniforms per draw). Created only through <see cref="ShaderLibrary"/>, which reports compile and link failures
/// instead of throwing.
/// </summary>
internal sealed unsafe class GlProgram : IDisposable
{
    private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);

    private GlProgram(string name, int id)
    {
        Name = name;
        Id = id;
    }

    public string Name { get; }

    public int Id { get; private set; }

    /// <summary>Live programs (resource accounting and leak checks).</summary>
    internal static int LiveCount { get; private set; }

    /// <summary>Compiles and links; null with <paramref name="error"/> (the driver's log) on failure. Never throws.</summary>
    public static GlProgram? TryCreate(string name, string vertexSource, string fragmentSource, out string? error)
    {
        error = null;
        var vertex = Compile(ShaderType.VertexShader, vertexSource, out var vertexError);
        if (vertex == 0)
        {
            error = $"{name}: vertex shader: {vertexError}";
            return null;
        }

        var fragment = Compile(ShaderType.FragmentShader, fragmentSource, out var fragmentError);
        if (fragment == 0)
        {
            GL.DeleteShader(vertex);
            error = $"{name}: fragment shader: {fragmentError}";
            return null;
        }

        var id = GL.CreateProgram();
        GL.AttachShader(id, vertex);
        GL.AttachShader(id, fragment);
        GL.LinkProgram(id);
        GL.DetachShader(id, vertex);
        GL.DetachShader(id, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        GL.GetProgram(id, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0)
        {
            error = $"{name}: link: {GL.GetProgramInfoLog(id)}";
            GL.DeleteProgram(id);
            return null;
        }

        LiveCount++;
        return new GlProgram(name, id);
    }

    private static int Compile(ShaderType type, string source, out string? error)
    {
        error = null;
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
        if (status == 0)
        {
            error = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            return 0;
        }

        return shader;
    }

    public void Use() => GL.UseProgram(Id);

    public int Location(string name)
    {
        if (!_locations.TryGetValue(name, out var location))
        {
            location = GL.GetUniformLocation(Id, name);
            _locations[name] = location;
        }

        return location;
    }

    public bool Has(string name) => Location(name) >= 0;

    public void Set(string name, int value)
    {
        var l = Location(name);
        if (l >= 0)
        {
            GL.Uniform1(l, value);
        }
    }

    public void Set(string name, float value)
    {
        var l = Location(name);
        if (l >= 0)
        {
            GL.Uniform1(l, value);
        }
    }

    public void Set(string name, float x, float y)
    {
        var l = Location(name);
        if (l >= 0)
        {
            GL.Uniform2(l, x, y);
        }
    }

    public void Set(string name, Vector3 v)
    {
        var l = Location(name);
        if (l >= 0)
        {
            GL.Uniform3(l, v.X, v.Y, v.Z);
        }
    }

    public void Set(string name, float x, float y, float z, float w)
    {
        var l = Location(name);
        if (l >= 0)
        {
            GL.Uniform4(l, x, y, z, w);
        }
    }

    public void Set(string name, in Matrix4 m)
    {
        var l = Location(name);
        if (l < 0)
        {
            return;
        }

        var f = stackalloc float[16];
        Write(m, f);
        GL.UniformMatrix4(l, 1, false, f);
    }

    /// <summary>A mat3 uniform from a column-major span of 9 floats.</summary>
    public void SetMatrix3(string name, ReadOnlySpan<float> columnMajor)
    {
        var l = Location(name);
        if (l < 0)
        {
            return;
        }

        fixed (float* p = columnMajor)
        {
            GL.UniformMatrix3(l, 1, false, p);
        }
    }

    public void SetMatrices(string name, ReadOnlySpan<Matrix4> matrices)
    {
        var l = Location(name + "[0]");
        if (l < 0 || matrices.Length == 0)
        {
            return;
        }

        var f = stackalloc float[16 * matrices.Length];
        for (var i = 0; i < matrices.Length; i++)
        {
            Write(matrices[i], f + i * 16);
        }

        GL.UniformMatrix4(l, matrices.Length, false, f);
    }

    public void SetInts(string name, ReadOnlySpan<int> values)
    {
        var l = Location(name + "[0]");
        if (l < 0 || values.Length == 0)
        {
            return;
        }

        fixed (int* p = values)
        {
            GL.Uniform1(l, values.Length, p);
        }
    }

    /// <summary>A vec4 array uniform: 4 floats per element.</summary>
    public void SetVec4s(string name, ReadOnlySpan<float> values)
    {
        var l = Location(name);
        if (l < 0)
        {
            l = Location(name + "[0]");
        }

        if (l < 0 || values.Length < 4)
        {
            return;
        }

        fixed (float* p = values)
        {
            GL.Uniform4(l, values.Length / 4, p);
        }
    }

    /// <summary>A vec3 array uniform: 3 floats per element.</summary>
    public void SetVec3s(string name, ReadOnlySpan<float> values)
    {
        var l = Location(name + "[0]");
        if (l < 0 || values.Length < 3)
        {
            return;
        }

        fixed (float* p = values)
        {
            GL.Uniform3(l, values.Length / 3, p);
        }
    }

    /// <summary>
    /// Points a sampler uniform at a texture unit (once per program; units never change). Binds the program (GL 3.3
    /// has no glProgramUniform), so call it while setting the program up.
    /// </summary>
    public void Sampler(string name, int unit)
    {
        var l = Location(name);
        if (l >= 0)
        {
            GL.UseProgram(Id);
            GL.Uniform1(l, unit);
        }
    }

    public void BindBlock(string blockName, int bindingPoint)
    {
        var index = GL.GetUniformBlockIndex(Id, blockName);
        if (index >= 0 && index != unchecked((int)0xFFFFFFFF))
        {
            GL.UniformBlockBinding(Id, index, bindingPoint);
        }
    }

    public void Dispose()
    {
        if (Id != 0)
        {
            GL.DeleteProgram(Id);
            Id = 0;
            LiveCount--;
        }
    }

    // Engine matrices are row-major with column vectors; OpenGL wants column-major.
    private static void Write(in Matrix4 m, float* f)
    {
        f[0] = m.M11; f[1] = m.M21; f[2] = m.M31; f[3] = m.M41;
        f[4] = m.M12; f[5] = m.M22; f[6] = m.M32; f[7] = m.M42;
        f[8] = m.M13; f[9] = m.M23; f[10] = m.M33; f[11] = m.M43;
        f[12] = m.M14; f[13] = m.M24; f[14] = m.M34; f[15] = m.M44;
    }
}
