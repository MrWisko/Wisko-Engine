using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

internal sealed class ShaderProgram : IDisposable
{
    private readonly int _programId;

    public ShaderProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        _programId = GL.CreateProgram();
        GL.AttachShader(_programId, vertexShader);
        GL.AttachShader(_programId, fragmentShader);
        GL.LinkProgram(_programId);

        GL.GetProgram(_programId, GetProgramParameterName.LinkStatus, out var linkStatus);
        var linkLog = GL.GetProgramInfoLog(_programId);
        if (!string.IsNullOrWhiteSpace(linkLog))
        {
            Console.WriteLine($"[Shader] Link log: {linkLog}");
        }

        if (linkStatus == 0)
        {
            throw new InvalidOperationException($"Shader link failed: {linkLog}");
        }

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
    }

    public void Use() => GL.UseProgram(_programId);

    public void SetMatrix4(string name, Math.Matrix4 matrix)
    {
        var location = GL.GetUniformLocation(_programId, name);
        if (location == -1)
        {
            Console.WriteLine($"[Shader] Warning: uniform '{name}' not found.");
            return;
        }

        GL.UniformMatrix4(location, 1, false, matrix.ToOpenGlColumnMajor());
    }

    public void SetVec3(string name, Math.Vector3 value)
    {
        var location = GL.GetUniformLocation(_programId, name);
        if (location == -1)
        {
            return;
        }

        GL.Uniform3(location, value.X, value.Y, value.Z);
    }

    public void SetFloat(string name, float value)
    {
        var location = GL.GetUniformLocation(_programId, name);
        if (location == -1)
        {
            return;
        }

        GL.Uniform1(location, value);
    }

    /// <summary>A vec4 array uniform (e.g. <c>u_atmo</c>): <paramref name="values"/> holds 4 floats per element.</summary>
    public void SetVec4Array(string name, float[] values)
    {
        var location = GL.GetUniformLocation(_programId, name);
        if (location == -1)
        {
            return;
        }

        GL.Uniform4(location, values.Length / 4, values);
    }

    /// <summary>Points a uniform block of this program at a buffer binding point (GLSL 330 has no layout(binding)).</summary>
    public void BindUniformBlock(string blockName, int bindingPoint)
    {
        var index = GL.GetUniformBlockIndex(_programId, blockName);
        if (index == -1 || index == unchecked((int)0xFFFFFFFF))
        {
            Console.WriteLine($"[Shader] Warning: uniform block '{blockName}' not found.");
            return;
        }

        GL.UniformBlockBinding(_programId, index, bindingPoint);
    }

    public void Dispose() => GL.DeleteProgram(_programId);

    private static int CompileShader(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
        var info = GL.GetShaderInfoLog(shader);
        if (status == 0)
        {
            throw new InvalidOperationException($"Shader compile failed ({type}): {info}");
        }

        return shader;
    }
}
