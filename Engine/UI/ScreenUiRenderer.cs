using Wisko.Engine.Math;
using Wisko.Engine.Text;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.UI;

internal sealed class ScreenUiRenderer : IUiRenderer
{
    // Rollback switch (Stage 2): when false, text falls back to the legacy SimpleFont quad path.
    // Kept as a mutable static (not const) so the legacy path stays reachable / toggleable until
    // the Inter path is visually verified. SimpleFont itself is intentionally left untouched.
    internal static bool UseInterFont = true;

    private const string VertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec3 aColor;
        layout (location = 2) in float aAlpha;

        uniform mat4 uProjection;

        out vec3 vColor;
        out float vAlpha;

        void main()
        {
            vColor = aColor;
            vAlpha = aAlpha;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core

        in vec3 vColor;
        in float vAlpha;
        out vec4 FragColor;

        void main()
        {
            FragColor = vec4(vColor, vAlpha);
        }
        """;

    // Textured glyph path: samples the shared R8 atlas as coverage (alpha).
    private const string TextVertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aUv;
        layout (location = 2) in vec3 aColor;
        layout (location = 3) in float aAlpha;

        uniform mat4 uProjection;

        out vec2 vUv;
        out vec3 vColor;
        out float vAlpha;

        void main()
        {
            vUv = aUv;
            vColor = aColor;
            vAlpha = aAlpha;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
        """;

    private const string TextFragmentShaderSource = """
        #version 330 core

        in vec2 vUv;
        in vec3 vColor;
        in float vAlpha;
        out vec4 FragColor;

        uniform sampler2D uAtlas;

        void main()
        {
            float coverage = texture(uAtlas, vUv).r;
            FragColor = vec4(vColor, vAlpha * coverage);
        }
        """;

    private readonly List<float> _vertices = new();       // solid: pos2 + color3 + alpha (6)
    private readonly List<float> _glyphVertices = new();   // text:  pos2 + uv2 + color3 + alpha (8)
    private float[] _solidUpload = Array.Empty<float>();   // reused upload buffers (no per-frame alloc)
    private float[] _glyphUpload = Array.Empty<float>();

    private Rendering.ShaderProgram? _shader;
    private Rendering.ShaderProgram? _textShader;
    private int _vao;
    private int _vbo;
    private int _textVao;
    private int _textVbo;
    private FontLibrary? _fonts;
    private float _dpiScale = 1f;
    private UiClip? _clip;

    public void Initialize(FontLibrary fonts)
    {
        _fonts = fonts;

        _shader = new Rendering.ShaderProgram(VertexShaderSource, FragmentShaderSource);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        _textShader = new Rendering.ShaderProgram(TextVertexShaderSource, TextFragmentShaderSource);
        _textVao = GL.GenVertexArray();
        _textVbo = GL.GenBuffer();

        // Configure the text VAO attributes once (they persist in VAO state).
        GL.BindVertexArray(_textVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _textVbo);
        const int textStride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, textStride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, textStride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, textStride, 4 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, textStride, 7 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.BindVertexArray(0);
    }

    public void BeginBatch(float dpiScale)
    {
        _vertices.Clear();
        _glyphVertices.Clear();
        _dpiScale = dpiScale > 0f ? dpiScale : 1f;
        _clip = null;
    }

    public void SetClip(UiClip? clip) => _clip = clip;

    public void DrawRect(float x, float y, float width, float height, Vector3 color, float alpha = 1f)
    {
        AddQuad(x, y, width, height, color, alpha);
    }

    public void DrawBorderRect(float x, float y, float width, float height, Vector3 color, float thickness, float alpha = 1f)
    {
        DrawRect(x, y, width, thickness, color, alpha);
        DrawRect(x, y + height - thickness, width, thickness, color, alpha);
        DrawRect(x, y, thickness, height, color, alpha);
        DrawRect(x + width - thickness, y, thickness, height, color, alpha);
    }

    public void DrawString(float x, float y, string text, Vector3 color, float pixelScale = 1.4f, FontWeight weight = FontWeight.Regular)
    {
        if (UseInterFont && _fonts is not null)
        {
            DrawStringInter(x, y, text, color, pixelScale, weight);
        }
        else
        {
            DrawStringLegacy(x, y, text, color, pixelScale);
        }
    }

    /// <summary>Width of the run in logical px, using the exact metrics/weight <see cref="DrawString"/> uses.</summary>
    public float MeasureWidth(string text, float pixelScale, FontWeight weight = FontWeight.Regular)
    {
        if (UseInterFont && _fonts is not null)
        {
            return TextLayout.Measure(_fonts, weight, text, pixelScale, _dpiScale).Width;
        }

        return SimpleFont.MeasureWidth(text, pixelScale);
    }

    /// <summary>Line-box height in logical px (weight/text independent), for consistent vertical centering.</summary>
    public float MeasureLineHeight(float pixelScale, FontWeight weight = FontWeight.Regular)
    {
        if (UseInterFont && _fonts is not null)
        {
            return TextLayout.Measure(_fonts, weight, string.Empty, pixelScale, _dpiScale).Height;
        }

        return 7f * pixelScale;
    }

    public void Flush(int screenWidth, int screenHeight)
    {
        if (_shader is null)
        {
            return;
        }

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        var projection = Matrix4.CreateOrthographicOffCenter(0f, screenWidth, screenHeight, 0f, -1f, 1f);

        if (_vertices.Count > 0)
        {
            _shader.Use();
            _shader.SetMatrix4("uProjection", projection);
            DrawSolidBatch();
        }

        if (UseInterFont && _textShader is not null && _fonts is not null && _glyphVertices.Count > 0)
        {
            _textShader.Use();
            _textShader.SetMatrix4("uProjection", projection);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _fonts.AtlasTexture); // sampler uAtlas defaults to unit 0
            DrawTextBatch();
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        _textShader?.Dispose();
        _textShader = null;

        if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
        if (_vbo != 0) { GL.DeleteBuffer(_vbo); _vbo = 0; }
        if (_textVao != 0) { GL.DeleteVertexArray(_textVao); _textVao = 0; }
        if (_textVbo != 0) { GL.DeleteBuffer(_textVbo); _textVbo = 0; }
        // Note: the shared FontLibrary/atlas is owned by Renderer and is NOT disposed here.
    }

    private void DrawSolidBatch()
    {
        var count = _vertices.Count;
        if (_solidUpload.Length < count)
        {
            _solidUpload = new float[global::System.Math.Max(count, _solidUpload.Length * 2)];
        }

        _vertices.CopyTo(_solidUpload);

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, count * sizeof(float), _solidUpload, BufferUsageHint.DynamicDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 6 * sizeof(float), 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.DrawArrays(PrimitiveType.Triangles, 0, count / 6);
        GL.BindVertexArray(0);
    }

    private void DrawTextBatch()
    {
        var count = _glyphVertices.Count;
        if (_glyphUpload.Length < count)
        {
            _glyphUpload = new float[global::System.Math.Max(count, _glyphUpload.Length * 2)];
        }

        _glyphVertices.CopyTo(_glyphUpload);

        GL.BindVertexArray(_textVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _textVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, count * sizeof(float), _glyphUpload, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, count / 8);
        GL.BindVertexArray(0);
    }

    private void DrawStringInter(float x, float y, string text, Vector3 color, float scale, FontWeight weight)
    {
        var quads = TextLayout.BuildQuads(_fonts!, weight, text, scale, _dpiScale, x, y);
        foreach (var quad in quads)
        {
            AddGlyphQuad(quad.X, quad.Y, quad.Width, quad.Height, quad.Uv, color);
        }
    }

    private void AddGlyphQuad(float x, float y, float width, float height, AtlasUv uv, Vector3 color)
    {
        // Screen ortho is y-down (top-left origin); atlas is top-down (V0 = glyph top), so the
        // top screen edge (y) maps to V0 and the bottom (y+height) to V1.
        float u0 = uv.U0, v0 = uv.V0, u1 = uv.U1, v1 = uv.V1;
        if (_clip is { } clip && !clip.Apply(ref x, ref y, ref width, ref height, ref u0, ref v0, ref u1, ref v1))
        {
            return;
        }

        AddGlyphVertex(x, y, u0, v0, color);
        AddGlyphVertex(x + width, y, u1, v0, color);
        AddGlyphVertex(x + width, y + height, u1, v1, color);

        AddGlyphVertex(x, y, u0, v0, color);
        AddGlyphVertex(x + width, y + height, u1, v1, color);
        AddGlyphVertex(x, y + height, u0, v1, color);
    }

    private void AddGlyphVertex(float x, float y, float u, float v, Vector3 color)
    {
        _glyphVertices.Add(x);
        _glyphVertices.Add(y);
        _glyphVertices.Add(u);
        _glyphVertices.Add(v);
        _glyphVertices.Add(color.X);
        _glyphVertices.Add(color.Y);
        _glyphVertices.Add(color.Z);
        _glyphVertices.Add(1f);
    }

    // ---- Legacy SimpleFont path (kept intact for rollback until the Inter path is verified) ----
    private void DrawStringLegacy(float x, float y, string text, Vector3 color, float pixelScale)
    {
        var cursorX = x;
        foreach (var ch in text)
        {
            if (ch == ' ')
            {
                cursorX += 4f * pixelScale;
                continue;
            }

            if (!SimpleFont.TryGetGlyph(ch, out var glyph))
            {
                continue;
            }

            for (var row = 0; row < glyph.Length; row++)
            {
                var bits = glyph[row];
                for (var col = 0; col < 5; col++)
                {
                    if ((bits & (1 << (4 - col))) != 0)
                    {
                        AddQuad(
                            cursorX + col * pixelScale,
                            y + row * pixelScale,
                            pixelScale,
                            pixelScale,
                            color,
                            1f);
                    }
                }
            }

            cursorX += 6f * pixelScale;
        }
    }

    private void AddQuad(float x, float y, float width, float height, Vector3 color, float alpha)
    {
        float u0 = 0f, v0 = 0f, u1 = 0f, v1 = 0f;
        if (_clip is { } clip && !clip.Apply(ref x, ref y, ref width, ref height, ref u0, ref v0, ref u1, ref v1))
        {
            return;
        }

        AddVertex(x, y, color, alpha);
        AddVertex(x + width, y, color, alpha);
        AddVertex(x + width, y + height, color, alpha);

        AddVertex(x, y, color, alpha);
        AddVertex(x + width, y + height, color, alpha);
        AddVertex(x, y + height, color, alpha);
    }

    private void AddVertex(float x, float y, Vector3 color, float alpha)
    {
        _vertices.Add(x);
        _vertices.Add(y);
        _vertices.Add(color.X);
        _vertices.Add(color.Y);
        _vertices.Add(color.Z);
        _vertices.Add(alpha);
    }
}

internal static class SimpleFont
{
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['S'] = [0x0E, 0x11, 0x10, 0x0E, 0x01, 0x11, 0x0E],
        ['e'] = [0x00, 0x0E, 0x11, 0x1F, 0x10, 0x11, 0x0E],
        ['l'] = [0x06, 0x02, 0x02, 0x02, 0x02, 0x02, 0x07],
        ['c'] = [0x00, 0x0E, 0x11, 0x10, 0x10, 0x11, 0x0E],
        ['t'] = [0x04, 0x0E, 0x04, 0x04, 0x04, 0x04, 0x06],
        ['M'] = [0x11, 0x1B, 0x15, 0x11, 0x11, 0x11, 0x11],
        ['o'] = [0x00, 0x0E, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['v'] = [0x00, 0x00, 0x11, 0x11, 0x11, 0x0A, 0x04],
        ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
        ['A'] = [0x04, 0x0A, 0x11, 0x1F, 0x11, 0x11, 0x11],
        ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
        ['p'] = [0x00, 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10],
        ['W'] = [0x11, 0x11, 0x11, 0x15, 0x1B, 0x11, 0x11],
        ['D'] = [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
        ['a'] = [0x00, 0x00, 0x0E, 0x01, 0x0F, 0x11, 0x0F],
        ['d'] = [0x02, 0x02, 0x0E, 0x12, 0x12, 0x12, 0x0E],
        ['i'] = [0x04, 0x00, 0x0C, 0x04, 0x04, 0x04, 0x0E],
        ['z'] = [0x00, 0x1F, 0x01, 0x02, 0x04, 0x08, 0x1F],
        ['B'] = [0x1F, 0x10, 0x1E, 0x11, 0x11, 0x11, 0x1E],
        ['k'] = [0x10, 0x10, 0x12, 0x14, 0x18, 0x14, 0x12],
        ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        ['y'] = [0x00, 0x11, 0x11, 0x0F, 0x01, 0x0E, 0x04],
        ['n'] = [0x00, 0x1C, 0x12, 0x12, 0x12, 0x12, 0x12],
        ['r'] = [0x00, 0x1C, 0x12, 0x10, 0x10, 0x10, 0x10],
        ['u'] = [0x00, 0x12, 0x12, 0x12, 0x12, 0x16, 0x0A],
        ['0'] = [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        ['2'] = [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
        ['3'] = [0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E],
        ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x04, 0x04, 0x04],
        ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
        ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x06],
        ['E'] = [0x1F, 0x10, 0x1E, 0x10, 0x10, 0x10, 0x1F],
        ['F'] = [0x1F, 0x10, 0x1E, 0x10, 0x10, 0x10, 0x10],
        ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0E],
        ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
        ['J'] = [0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C],
        ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
        ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
        ['N'] = [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
        ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['Q'] = [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
        ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
        ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['V'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
        ['X'] = [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
        ['Y'] = [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
        ['Z'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
        ['b'] = [0x10, 0x10, 0x16, 0x19, 0x11, 0x11, 0x1E],
        ['f'] = [0x06, 0x09, 0x08, 0x1C, 0x08, 0x08, 0x08],
        ['g'] = [0x00, 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x0E],
        ['h'] = [0x10, 0x10, 0x16, 0x19, 0x11, 0x11, 0x11],
        ['j'] = [0x02, 0x00, 0x06, 0x02, 0x02, 0x12, 0x0C],
        ['m'] = [0x00, 0x00, 0x1A, 0x15, 0x15, 0x11, 0x11],
        ['q'] = [0x00, 0x1E, 0x11, 0x11, 0x1E, 0x01, 0x01],
        ['s'] = [0x00, 0x0E, 0x10, 0x0E, 0x01, 0x11, 0x0E],
        ['w'] = [0x00, 0x00, 0x11, 0x11, 0x15, 0x15, 0x0A],
        ['x'] = [0x00, 0x00, 0x11, 0x0A, 0x04, 0x0A, 0x11],
        ['_'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F],
        ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
    };

    public static float MeasureWidth(string text, float pixelScale)
    {
        var width = 0f;
        foreach (var ch in text)
        {
            width += ch == ' ' ? 4f * pixelScale : 6f * pixelScale;
        }

        return width;
    }

    public static bool TryGetGlyph(char ch, out byte[] glyph)
    {
        if (Glyphs.TryGetValue(ch, out glyph!))
        {
            return true;
        }

        var upper = char.ToUpperInvariant(ch);
        return Glyphs.TryGetValue(upper, out glyph!);
    }
}
