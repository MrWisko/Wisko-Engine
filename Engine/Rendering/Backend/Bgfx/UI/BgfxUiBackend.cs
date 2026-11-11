using System.Runtime.InteropServices;
using Wisko.Engine.Text;
using Wisko.Engine.UI;
using BgfxNative = global::Bgfx.bgfx;

namespace Wisko.Engine.Rendering.Backend.Bgfx.UI;

/// <summary>One UI vertex: logical-px position, texcoord, straight RGBA colour (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct BgfxUiVertex(float x, float y, float u, float v, float r, float g, float b, float a)
{
    public readonly float X = x;
    public readonly float Y = y;
    public readonly float U = u;
    public readonly float V = v;
    public readonly float R = r;
    public readonly float G = g;
    public readonly float B = b;
    public readonly float A = a;
}

/// <summary>
/// bgfx implementation of the UI seam. Owns the shared UI GPU resources (vertex layout, solid/text/image
/// programs, sampler uniform, glyph atlas texture via its FontLibrary) and submits UI geometry to one
/// sequential view (after the 3D world view) in logical px, projected onto the framebuffer's pixels.
/// </summary>
internal sealed unsafe class BgfxUiBackend : IUiBackend, IDisposable
{
    public const ushort ViewId = 1; // after the editor's world view (0); no clear, draws on top

    // Straight-alpha blending, no depth, matching the GL UI path (SrcAlpha, OneMinusSrcAlpha).
    private const ulong UiState =
        (ulong)BgfxNative.StateFlags.WriteRgb
        | (ulong)BgfxNative.StateFlags.WriteA
        | ((ulong)BgfxNative.StateFlags.BlendSrcAlpha | ((ulong)BgfxNative.StateFlags.BlendInvSrcAlpha << 4))
        | (((ulong)BgfxNative.StateFlags.BlendSrcAlpha | ((ulong)BgfxNative.StateFlags.BlendInvSrcAlpha << 4)) << 8);

    internal const ulong TextureSamplerFlags = (ulong)(BgfxNative.SamplerFlags.UClamp | BgfxNative.SamplerFlags.VClamp);

    private readonly float[] _projection = new float[16];
    private BgfxNative.VertexLayout _layout;
    private BgfxNative.ShaderHandle _vertexShader;
    private BgfxNative.ShaderHandle _solidShader;
    private BgfxNative.ShaderHandle _textShader;
    private BgfxNative.ShaderHandle _imageShader;
    private BgfxNative.ProgramHandle _solidProgram;
    private BgfxNative.ProgramHandle _textProgram;
    private BgfxNative.ProgramHandle _imageProgram;
    private BgfxNative.UniformHandle _sampler;
    private FontLibrary? _fonts;
    private bool _transientWarned;
    private bool _initialized;

    public FontLibrary Fonts => _fonts ?? throw new InvalidOperationException("bgfx UI is not initialized.");

    public BgfxUiTextureFactory Textures { get; } = new();

    /// <summary>Creates shaders, programs, sampler and the glyph atlas. bgfx must be initialized.</summary>
    public void Initialize(float dpiScale)
    {
        fixed (BgfxNative.VertexLayout* layout = &_layout)
        {
            BgfxNative.vertex_layout_begin(layout, BgfxNative.RendererType.Noop);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Position, 2, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.TexCoord0, 2, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Color0, 4, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_end(layout);
        }

        if (_layout.stride != sizeof(BgfxUiVertex))
        {
            throw new InvalidOperationException($"bgfx UI vertex layout stride {_layout.stride} != {sizeof(BgfxUiVertex)}.");
        }

        _vertexShader = BgfxShaders.Load("vs_ui");
        _solidShader = BgfxShaders.Load("fs_ui_solid");
        _textShader = BgfxShaders.Load("fs_ui_text");
        _imageShader = BgfxShaders.Load("fs_ui_image");
        // destroyShaders: false -> the vertex shader is shared by all three programs; shaders are destroyed
        // explicitly after the programs in Dispose.
        _solidProgram = CreateProgram(_solidShader, "solid");
        _textProgram = CreateProgram(_textShader, "text");
        _imageProgram = CreateProgram(_imageShader, "image");

        _sampler = BgfxNative.create_uniform("s_tex", BgfxNative.UniformType.Sampler, 1);
        if (!_sampler.Valid)
        {
            throw new InvalidOperationException("bgfx UI sampler uniform could not be created.");
        }

        BgfxNative.set_view_mode(ViewId, BgfxNative.ViewMode.Sequential); // draw strictly in submit order

        _fonts = new FontLibrary(new BgfxGlyphAtlasSurface());
        _fonts.Initialize();
        _fonts.PrewarmAscii(PrewarmBuckets(dpiScale));
        _initialized = true;
    }

    /// <summary>Sets the UI view: whole framebuffer, projection from logical px (top-left origin).</summary>
    public void BeginFrame(int logicalWidth, int logicalHeight, int framebufferWidth, int framebufferHeight)
    {
        BgfxNative.set_view_rect(ViewId, 0, 0, (ushort)global::System.Math.Max(framebufferWidth, 1),
            (ushort)global::System.Math.Max(framebufferHeight, 1), 0f, 1f);
        var width = global::System.Math.Max(logicalWidth, 1);
        var height = global::System.Math.Max(logicalHeight, 1);
        // Column-major orthographic, y down, depth 0..1 (Metal: homogeneousDepth == false).
        Array.Clear(_projection);
        _projection[0] = 2f / width;
        _projection[5] = -2f / height;
        _projection[10] = 1f;
        _projection[12] = -1f;
        _projection[13] = 1f;
        _projection[15] = 1f;
        fixed (float* projection = _projection)
        {
            BgfxNative.set_view_transform(ViewId, null, projection);
        }
    }

    public IUiRenderer CreateRenderer() => new BgfxUiRenderer(this, Fonts);

    public IUiRenderer CreateRenderer(FontLibrary fonts) => new BgfxUiRenderer(this, fonts);

    public FontLibrary CreateFontLibrary()
    {
        var library = new FontLibrary(new BgfxGlyphAtlasSurface());
        library.Initialize();
        return library;
    }

    public IUiImageRenderer CreateImageRenderer() => new BgfxUiImageRenderer(this);

    public IUiTextureCache CreateTextureCache() => new TextureFileCache(Textures);

    public UiTexture CreateTexture(byte[] rgba, int width, int height) => Textures.Create(rgba, width, height);

    public void DestroyTexture(UiTexture texture) => Textures.Destroy(texture);

    internal void SubmitSolid(List<BgfxUiVertex> vertices) => Submit(vertices, _solidProgram, null);

    internal void SubmitText(List<BgfxUiVertex> vertices, FontLibrary fonts) =>
        Submit(vertices, _textProgram, new BgfxNative.TextureHandle { idx = (ushort)fonts.AtlasTexture });

    internal void SubmitImage(List<BgfxUiVertex> vertices, UiTexture texture) =>
        Submit(vertices, _imageProgram, new BgfxNative.TextureHandle { idx = (ushort)texture.Id });

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        BgfxNative.destroy_program(_solidProgram);
        BgfxNative.destroy_program(_textProgram);
        BgfxNative.destroy_program(_imageProgram);
        BgfxNative.destroy_shader(_vertexShader);
        BgfxNative.destroy_shader(_solidShader);
        BgfxNative.destroy_shader(_textShader);
        BgfxNative.destroy_shader(_imageShader);
        BgfxNative.destroy_uniform(_sampler);
        _fonts?.Dispose(); // destroys the glyph atlas texture
        _fonts = null;
    }

    // Text scales used by the Project Hub and name prompt; other sizes rasterize on demand.
    private static readonly float[] PrewarmScales = { 1.7f, 1.75f, 1.8f, 2.0f, 2.1f, 2.3f, 2.8f };

    private static int[] PrewarmBuckets(float dpiScale) =>
        PrewarmScales.Select(scale => TextLayout.DeviceSizeBucket(scale, dpiScale)).Distinct().Order().ToArray();

        private BgfxNative.ProgramHandle CreateProgram(BgfxNative.ShaderHandle fragmentShader, string name)
    {
        var program = BgfxNative.create_program(_vertexShader, fragmentShader, false);
        if (!program.Valid)
        {
            throw new InvalidOperationException($"bgfx UI {name} program could not be created.");
        }

        return program;
    }

    private void Submit(List<BgfxUiVertex> vertices, BgfxNative.ProgramHandle program, BgfxNative.TextureHandle? texture)
    {
        var count = (uint)vertices.Count;
        if (count == 0)
        {
            return;
        }

        fixed (BgfxNative.VertexLayout* layout = &_layout)
        {
            if (BgfxNative.get_avail_transient_vertex_buffer(count, layout) < count)
            {
                if (!_transientWarned)
                {
                    _transientWarned = true;
                    Console.WriteLine($"[Renderer] bgfx UI: transient vertex space exhausted ({count} vertices); batch skipped");
                }

                return;
            }

            BgfxNative.TransientVertexBuffer buffer;
            BgfxNative.alloc_transient_vertex_buffer(&buffer, count, layout);
            var source = CollectionsMarshal.AsSpan(vertices);
            fixed (BgfxUiVertex* data = source)
            {
                Buffer.MemoryCopy(data, buffer.data, buffer.size, count * (long)sizeof(BgfxUiVertex));
            }

            BgfxNative.set_transient_vertex_buffer(0, &buffer, 0, count);
        }

        if (texture is { } handle)
        {
            BgfxNative.set_texture(0, _sampler, handle, uint.MaxValue); // uint.MaxValue: use the texture's own sampler flags
        }

        BgfxNative.set_state(UiState, 0);
        BgfxNative.submit(ViewId, program, 0, (byte)BgfxNative.DiscardFlags.All);
    }
}
