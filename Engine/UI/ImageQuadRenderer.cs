using Wisko.Engine.Math;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.UI;

/// <summary>
/// Draws RGBA textures as screen-space quads (logical px, top-left origin) and owns texture upload for
/// them. Used for Project Hub thumbnails; drawn after the hub's rect/text batch.
/// </summary>
internal sealed class ImageQuadRenderer : IUiImageRenderer
{
    private const string VertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aUv;

        uniform mat4 uProjection;

        out vec2 vUv;

        void main()
        {
            vUv = aUv;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core

        in vec2 vUv;
        out vec4 FragColor;

        uniform sampler2D uImage;

        void main()
        {
            FragColor = texture(uImage, vUv);
        }
        """;

    private readonly float[] _vertices = new float[6 * 4];
    private Rendering.ShaderProgram? _shader;
    private int _vao;
    private int _vbo;

    public void Initialize()
    {
        _shader = new Rendering.ShaderProgram(VertexShaderSource, FragmentShaderSource);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        const int stride = 4 * sizeof(float);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    /// <summary>Uploads top-down RGBA rows; row 0 maps to the top of drawn quads.</summary>
    public static int CreateTexture(byte[] rgba, int width, int height)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    public static void DeleteTexture(int texture) => GL.DeleteTexture(texture);

    public void Draw(UiTexture texture, float x, float y, float width, float height, int screenWidth, int screenHeight) =>
        Draw(texture.Id, x, y, width, height, screenWidth, screenHeight);

    public void DrawRegion(UiTexture texture, float x, float y, float width, float height, float u0, float v0, float u1, float v1, int screenWidth, int screenHeight) =>
        Draw(texture.Id, x, y, width, height, screenWidth, screenHeight, u0, v0, u1, v1);

    /// <summary>Draws the texture (or its u0..u1 x v0..v1 part) into the given logical-px rect, alpha-blended.</summary>
    public void Draw(int texture, float x, float y, float width, float height, int screenWidth, int screenHeight,
        float u0 = 0f, float v0 = 0f, float u1 = 1f, float v1 = 1f)
    {
        if (_shader is null)
        {
            return;
        }

        float[] quad =
        [
            x, y, u0, v0,
            x + width, y, u1, v0,
            x + width, y + height, u1, v1,
            x, y, u0, v0,
            x + width, y + height, u1, v1,
            x, y + height, u0, v1
        ];
        Array.Copy(quad, _vertices, quad.Length);

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _shader.Use();
        _shader.SetMatrix4("uProjection", Matrix4.CreateOrthographicOffCenter(0f, screenWidth, screenHeight, 0f, -1f, 1f));
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _vertices.Length * sizeof(float), _vertices);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
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
    }
}
