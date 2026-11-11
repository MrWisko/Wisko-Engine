using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

/// <summary>
/// OpenGL side of the shared Atmosphere shader source (atmosphere.sh, embedded): prepends it to GLSL 330 bodies, and
/// draws the Atmosphere sky as one full-screen triangle behind the world (no depth test or write).
/// </summary>
internal sealed class AtmosphereSkyRenderer : IDisposable
{
    private const string VertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec2 aCorner;

        out vec2 vCorner;

        void main()
        {
            vCorner = aCorner;
            gl_Position = vec4(aCorner, 0.5, 1.0);
        }
        """;

    private const string FragmentShaderBody = """
        in vec2 vCorner;
        out vec4 FragColor;

        void main()
        {
            vec3 dir = normalize(u_atmo[11].xyz + vCorner.x * u_atmo[9].xyz + vCorner.y * u_atmo[10].xyz);
            vec3 sky = atmoSky(dir);
            vec4 disc = atmoSunDisc(dir);
            sky = mix(sky, disc.rgb, disc.a);
            if (atmoOn())
            {
                sky = mix(sky, u_atmo[5].rgb, atmoSkyFog(dir));
            }

            FragColor = vec4(atmoGrade(sky), 1.0);
        }
        """;

    private static string? _atmosphereSource;
    private ShaderProgram? _shader;
    private int _vao;
    private int _vbo;

    /// <summary>A fragment shader: GLSL 330 header, the shared Atmosphere functions, then <paramref name="body"/>.</summary>
    public static string WithAtmosphere(string body) => "#version 330 core\n\n" + AtmosphereSource + "\n" + body;

    private static string AtmosphereSource => _atmosphereSource ??= LoadAtmosphereSource();

    /// <summary>The shared Atmosphere functions (atmosphere.sh) without a version line (the Universal renderer's shaders include them).</summary>
    internal static string AtmosphereFunctions => AtmosphereSource;

    public void Initialize()
    {
        _shader = new ShaderProgram(VertexShaderSource, WithAtmosphere(FragmentShaderBody));
        float[] corners = [-1f, -1f, 3f, -1f, -1f, 3f];
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * sizeof(float), corners, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    /// <summary>Draws the sky over the whole viewport (call first, right after clearing).</summary>
    public void Render(float[] atmosphere)
    {
        if (_shader is null)
        {
            return;
        }

        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        _shader.Use();
        _shader.SetVec4Array("u_atmo", atmosphere);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
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
    }

    private static string LoadAtmosphereSource()
    {
        const string resource = "Engine.Shaders.atmosphere.sh";
        using var stream = typeof(AtmosphereSkyRenderer).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded shader source not found: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
