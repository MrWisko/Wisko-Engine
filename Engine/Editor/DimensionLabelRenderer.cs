using System.Globalization;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.Text;
using Wisko.Engine.World;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Editor;

internal sealed class DimensionLabelRenderer : IDisposable
{
    private static readonly Vector3 GlyphColor = new(1f, 1f, 1f);
    private static readonly Vector3 Background = new(0.07f, 0.07f, 0.09f);

    // Background padding, in logical px (scaled to world by WorldUnitsPerLogicalPixel).
    private const float BackgroundPadHLogical = 3f;
    private const float BackgroundPadVLogical = 2f;

    // World units per logical text pixel. Chosen so Inter's digit/cap apparent height matches the
    // old 5x7 label (~0.294 world units): old digits spanned 7 cells x 0.042 = 0.294; Inter caps at
    // the nominal bucket are ~10 logical px tall (0.625 of the 16 px line box, per the FontSpike
    // measurements), so 0.294 / 10 ~= 0.029. Treat as a manual visual-calibration starting point.
    private const float WorldUnitsPerLogicalPixel = 0.029f;

    // SemiBold reads better for these small, transient readout values over arbitrary 3D backgrounds.
    private const FontWeight LabelWeight = FontWeight.SemiBold;

    // Nominal legacy scale -> a pre-warmed device bucket (DeviceSizeBucket(2.0, dpi)); governs atlas
    // crispness only. Apparent world size is set separately by WorldUnitsPerLogicalPixel.
    private const float LabelLegacyScale = 2.0f;

    private const string GlyphVertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec2 aUv;
        layout (location = 2) in vec3 aColor;

        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec2 vUv;
        out vec3 vColor;

        void main()
        {
            vUv = aUv;
            vColor = aColor;
            gl_Position = uProjection * uView * vec4(aPosition, 1.0);
        }
        """;

    private const string GlyphFragmentShaderSource = """
        #version 330 core

        in vec2 vUv;
        in vec3 vColor;
        out vec4 FragColor;

        uniform sampler2D uAtlas;

        void main()
        {
            float coverage = texture(uAtlas, vUv).r;
            FragColor = vec4(vColor, coverage);
        }
        """;

    private readonly List<Vertex> _vertices = new();        // background: pos3 + color3
    private readonly List<float> _glyphVertices = new();    // glyphs: pos3 + uv2 + color3 (8)
    private float[] _bgUpload = Array.Empty<float>();
    private float[] _glyphUpload = Array.Empty<float>();

    private FontLibrary? _fonts;
    private ShaderProgram? _glyphShader;
    private int _vao;
    private int _vbo;
    private int _glyphVao;
    private int _glyphVbo;

    public void Initialize(FontLibrary fonts)
    {
        _fonts = fonts;

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        const int bgStride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, bgStride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, bgStride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        _glyphShader = new ShaderProgram(GlyphVertexShaderSource, GlyphFragmentShaderSource);
        _glyphVao = GL.GenVertexArray();
        _glyphVbo = GL.GenBuffer();
        GL.BindVertexArray(_glyphVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _glyphVbo);
        const int glyphStride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, glyphStride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, glyphStride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, glyphStride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);
    }

    /// <summary>Text layout only (no GPU objects), for backends that draw the label geometry themselves.</summary>
    internal void InitializeLayout(FontLibrary fonts) => _fonts = fonts;

    /// <summary>Background quads (pos + colour) built by <see cref="BuildGeometry"/>; world space.</summary>
    internal IReadOnlyList<Vertex> BackgroundVertices => _vertices;

    /// <summary>Glyph vertices built by <see cref="BuildGeometry"/>: pos3 + uv2 + colour3 per vertex, world space.</summary>
    internal List<float> GlyphVertices => _glyphVertices;

    public void Render(
        ShaderProgram shader,
        Part part,
        EditorCamera camera,
        Matrix4 view,
        Matrix4 projection,
        EditorToolMode tool,
        MoveGizmoController moveController,
        ResizeGizmoController resizeController,
        float dpiScale,
        RotateGizmoController? rotateController = null)
    {
        if (!BuildGeometry(part, camera, tool, moveController, resizeController, dpiScale, rotateController))
        {
            return;
        }

        // Labels always draw on top of the scene. Depth off entering; restored on exit. Blend is
        // enabled only for the glyph pass and disabled again; the atlas is unbound afterward. This
        // renderer restores all GL state it touches rather than relying on later renderers.
        GL.Disable(EnableCap.DepthTest);
        shader.Use();
        shader.SetMatrix4("uView", view);
        shader.SetMatrix4("uProjection", projection);

        if (_vertices.Count > 0)
        {
            DrawBackgroundBatch(shader);
        }

        if (_glyphShader is not null && _fonts is not null && _glyphVertices.Count > 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _glyphShader.Use();
            _glyphShader.SetMatrix4("uView", view);
            _glyphShader.SetMatrix4("uProjection", projection);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _fonts.AtlasTexture); // sampler uAtlas defaults to unit 0
            DrawGlyphBatch();
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.Disable(EnableCap.Blend);
        }

        GL.Enable(EnableCap.DepthTest);
    }

    /// <summary>
    /// Builds this frame's label geometry (backend-independent). Returns false when there is nothing to draw.
    /// </summary>
    internal bool BuildGeometry(
        Part part,
        EditorCamera camera,
        EditorToolMode tool,
        MoveGizmoController moveController,
        ResizeGizmoController resizeController,
        float dpiScale,
        RotateGizmoController? rotateController = null)
    {
        _vertices.Clear();
        _glyphVertices.Clear();

        var moveDragging = moveController.IsDragging;
        var resizeDragging = resizeController.IsDragging;
        var branch = ResolveLabelBranch(tool, moveDragging, resizeDragging);
        if (branch == DimensionLabelBranch.None && tool == EditorToolMode.Rotate && rotateController is { IsDragging: true } rotating)
        {
            // While a ring or arc turns the Part: the angle it is at now (the same numbers the World panel shows).
            var label = RotateAngleLabel(part.Rotation, rotating.ActiveHandle);
            DrawCenteredLabel(part.Position, label, ScreenBillboardBasis.FromCamera(camera), dpiScale);
            return _vertices.Count > 0 || _glyphVertices.Count > 0;
        }

        if (branch == DimensionLabelBranch.None)
        {
            return false;
        }

        var anchor = part.Position;
        var basis = ScreenBillboardBasis.FromCamera(camera);
        if (branch == DimensionLabelBranch.Move)
        {
            AddMovePositionLabel(part, anchor, basis, dpiScale);
        }
        else if (resizeController.ActiveCorner != ResizeCornerHandle.None)
        {
            AddCombinedResizeLabel(part, anchor, basis, dpiScale);
        }
        else
        {
            AddResizeSizeLabel(part, resizeController.ActiveHandle, anchor, basis, dpiScale);
        }

        return _vertices.Count > 0 || _glyphVertices.Count > 0;
    }

    public void Dispose()
    {
        _glyphShader?.Dispose();
        _glyphShader = null;

        if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
        if (_vbo != 0) { GL.DeleteBuffer(_vbo); _vbo = 0; }
        if (_glyphVao != 0) { GL.DeleteVertexArray(_glyphVao); _glyphVao = 0; }
        if (_glyphVbo != 0) { GL.DeleteBuffer(_glyphVbo); _glyphVbo = 0; }
        // The shared FontLibrary/atlas is owned by Renderer and is NOT disposed here.
    }

    private void AddMovePositionLabel(Part part, Vector3 anchor, ScreenBillboardBasis basis, float dpiScale)
    {
        var label = $"Pos {FormatNumber(part.Position.X)} {FormatNumber(part.Position.Y)} {FormatNumber(part.Position.Z)}";
        DrawCenteredLabel(anchor, label, basis, dpiScale);
    }

    private void AddResizeSizeLabel(Part part, ResizeHandle handle, Vector3 anchor, ScreenBillboardBasis basis, float dpiScale)
    {
        var label = $"Size {FormatNumber(GetResizeAxisSize(part, handle))}";
        DrawCenteredLabel(anchor, label, basis, dpiScale);
    }

    // Dual-axis corner resize shows both changing dimensions in one uncluttered label, using axis names.
    private void AddCombinedResizeLabel(Part part, Vector3 anchor, ScreenBillboardBasis basis, float dpiScale)
    {
        var label = $"Size X={FormatNumber(part.Size.X)} Z={FormatNumber(part.Size.Z)}";
        DrawCenteredLabel(anchor, label, basis, dpiScale);
    }

    private void DrawCenteredLabel(Vector3 anchor, string label, ScreenBillboardBasis basis, float dpiScale)
    {
        if (_fonts is null)
        {
            return;
        }

        // Measure via the shared layout (encodes ascent/descent/line-height) so the background and
        // glyph placement share one origin — no assumption that glyphs start at logical y = 0.
        var measurement = TextLayout.Measure(_fonts, LabelWeight, label, LabelLegacyScale, dpiScale);
        var scale = WorldUnitsPerLogicalPixel;

        var bgHalfW = measurement.Width * 0.5f * scale + BackgroundPadHLogical * scale;
        var bgHalfH = measurement.Height * 0.5f * scale + BackgroundPadVLogical * scale;
        AddBackgroundQuad(anchor, basis.Right, basis.Up, bgHalfW, bgHalfH, Background);

        AddWorldGlyphs(anchor, label, basis, dpiScale, measurement, scale);
    }

    /// <summary>
    /// Converts the pure-logical <see cref="TextLayout"/> quads into camera-facing world quads on the
    /// billboard Right/Up plane. Logical y is down; world Up is up, so y maps to -Up (upright). Atlas
    /// UVs are top-down (V0 top), so the top world edge samples V0 and the bottom V1 (never mirrored
    /// or upside-down, from any viewing direction).
    /// </summary>
    private void AddWorldGlyphs(
        Vector3 anchor,
        string label,
        ScreenBillboardBasis basis,
        float dpiScale,
        TextMeasurement measurement,
        float scale)
    {
        var right = basis.Right;
        var up = basis.Up;

        // World position of the block's top-left, centering the [0..W] x [0..H] logical box on anchor.
        var origin = anchor
            - right * (measurement.Width * 0.5f * scale)
            + up * (measurement.Height * 0.5f * scale);

        var quads = TextLayout.BuildQuads(_fonts!, LabelWeight, label, LabelLegacyScale, dpiScale, 0f, 0f);
        foreach (var quad in quads)
        {
            var x0 = quad.X;
            var y0 = quad.Y;
            var x1 = quad.X + quad.Width;
            var y1 = quad.Y + quad.Height;

            var topLeft = origin + right * (x0 * scale) - up * (y0 * scale);
            var topRight = origin + right * (x1 * scale) - up * (y0 * scale);
            var bottomRight = origin + right * (x1 * scale) - up * (y1 * scale);
            var bottomLeft = origin + right * (x0 * scale) - up * (y1 * scale);

            var uv = quad.Uv;
            AddGlyphVertex(topLeft, uv.U0, uv.V0);
            AddGlyphVertex(topRight, uv.U1, uv.V0);
            AddGlyphVertex(bottomRight, uv.U1, uv.V1);

            AddGlyphVertex(topLeft, uv.U0, uv.V0);
            AddGlyphVertex(bottomRight, uv.U1, uv.V1);
            AddGlyphVertex(bottomLeft, uv.U0, uv.V1);
        }
    }

    private void AddGlyphVertex(Vector3 position, float u, float v)
    {
        _glyphVertices.Add(position.X);
        _glyphVertices.Add(position.Y);
        _glyphVertices.Add(position.Z);
        _glyphVertices.Add(u);
        _glyphVertices.Add(v);
        _glyphVertices.Add(GlyphColor.X);
        _glyphVertices.Add(GlyphColor.Y);
        _glyphVertices.Add(GlyphColor.Z);
    }

    private void AddBackgroundQuad(Vector3 center, Vector3 right, Vector3 up, float halfWidth, float halfHeight, Vector3 color)
    {
        var r = right * halfWidth;
        var u = up * halfHeight;

        var bottomLeft = center - r - u;
        var bottomRight = center + r - u;
        var topRight = center + r + u;
        var topLeft = center - r + u;

        _vertices.Add(new Vertex(bottomLeft, color));
        _vertices.Add(new Vertex(bottomRight, color));
        _vertices.Add(new Vertex(topRight, color));

        _vertices.Add(new Vertex(bottomLeft, color));
        _vertices.Add(new Vertex(topRight, color));
        _vertices.Add(new Vertex(topLeft, color));
    }

    private void DrawBackgroundBatch(ShaderProgram shader)
    {
        var floatCount = _vertices.Count * 6;
        if (_bgUpload.Length < floatCount)
        {
            _bgUpload = new float[global::System.Math.Max(floatCount, _bgUpload.Length * 2)];
        }

        for (var i = 0; i < _vertices.Count; i++)
        {
            var v = _vertices[i];
            var o = i * 6;
            _bgUpload[o + 0] = v.Position.X;
            _bgUpload[o + 1] = v.Position.Y;
            _bgUpload[o + 2] = v.Position.Z;
            _bgUpload[o + 3] = v.Color.X;
            _bgUpload[o + 4] = v.Color.Y;
            _bgUpload[o + 5] = v.Color.Z;
        }

        shader.SetMatrix4("uModel", Matrix4.Identity);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, floatCount * sizeof(float), _bgUpload, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertices.Count);
        GL.BindVertexArray(0);
    }

    private void DrawGlyphBatch()
    {
        var count = _glyphVertices.Count;
        if (_glyphUpload.Length < count)
        {
            _glyphUpload = new float[global::System.Math.Max(count, _glyphUpload.Length * 2)];
        }

        _glyphVertices.CopyTo(_glyphUpload);

        GL.BindVertexArray(_glyphVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _glyphVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, count * sizeof(float), _glyphUpload, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, count / 8);
        GL.BindVertexArray(0);
    }

    private static DimensionLabelBranch ResolveLabelBranch(
        EditorToolMode tool,
        bool moveDragging,
        bool resizeDragging)
    {
        if (tool == EditorToolMode.Move && moveDragging)
        {
            return DimensionLabelBranch.Move;
        }

        if (tool == EditorToolMode.Resize && resizeDragging)
        {
            return DimensionLabelBranch.Resize;
        }

        return DimensionLabelBranch.None;
    }

    // "Y 45°" for a ring (its axis), "X 30°  Y 60°" for a diagonal arc (it turns both).
    internal static string RotateAngleLabel(Vector3 rotation, RotateHandle handle) => handle switch
    {
        RotateHandle.X => $"X {RotationInput.Format(rotation.X)}°",
        RotateHandle.Y => $"Y {RotationInput.Format(rotation.Y)}°",
        RotateHandle.Z => $"Z {RotationInput.Format(rotation.Z)}°",
        _ => $"X {RotationInput.Format(rotation.X)}°  Y {RotationInput.Format(rotation.Y)}°"
    };

    private static float GetResizeAxisSize(Part part, ResizeHandle handle) =>
        handle switch
        {
            ResizeHandle.PositiveX or ResizeHandle.NegativeX => part.Size.X,
            ResizeHandle.PositiveY or ResizeHandle.NegativeY => part.Size.Y,
            ResizeHandle.PositiveZ or ResizeHandle.NegativeZ => part.Size.Z,
            _ => 0f
        };

    private static string FormatNumber(float value)
    {
        var rounded = global::System.MathF.Round(value);
        if (global::System.MathF.Abs(value - rounded) < 0.05f)
        {
            return ((int)rounded).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private readonly struct ScreenBillboardBasis
    {
        public Vector3 Right { get; init; }
        public Vector3 Up { get; init; }
        public Vector3 Forward { get; init; }

        public static ScreenBillboardBasis FromCamera(EditorCamera camera) => new()
        {
            Right = camera.Right,
            Up = camera.Up,
            Forward = camera.Forward
        };
    }

    private enum DimensionLabelBranch
    {
        None,
        Move,
        Resize
    }
}
