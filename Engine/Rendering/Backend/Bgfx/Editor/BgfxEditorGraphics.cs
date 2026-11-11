using System.Runtime.InteropServices;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering.Backend.Bgfx.UI;
using BgfxNative = global::Bgfx.bgfx;

namespace Wisko.Engine.Rendering.Backend.Bgfx.Editor;

/// <summary>Static bgfx vertex (+ optional index) buffer backing one <see cref="ColoredMesh"/>.</summary>
internal sealed class BgfxGpuMesh : IGpuMesh
{
    public BgfxNative.VertexBufferHandle Vertices;
    public BgfxNative.IndexBufferHandle Indices;
    public bool Indexed;
    public uint VertexCount;
    public uint IndexCount;
    public ulong PrimitiveState;
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BgfxNative.destroy_vertex_buffer(Vertices);
        if (Indexed)
        {
            BgfxNative.destroy_index_buffer(Indices);
        }

        BgfxEditorGraphics.LiveMeshes--;
    }
}

/// <summary>
/// A vertex buffer the CPU refills (bgfx skinning: one per skinned mesh of one model instance), in the
/// <see cref="ColoredMesh"/> layout so the lit program draws it unchanged.
/// </summary>
internal sealed class BgfxDynamicMesh : IDisposable
{
    public BgfxNative.DynamicVertexBufferHandle Vertices;
    public uint VertexCount;
    private bool _disposed;

    /// <summary>Live dynamic buffers (leak checks).</summary>
    internal static int Live;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BgfxNative.destroy_dynamic_vertex_buffer(Vertices);
        Live--;
    }
}

/// <summary>
/// bgfx GPU side of the 3D editor: mesh buffers for <see cref="ColoredMesh"/>, the unlit / lit / environment /
/// Atmosphere sky / world-text programs ported from the OpenGL shaders, explicit per-draw render state, transient
/// geometry for per-frame shapes, and the asynchronous world-only thumbnail capture. The world programs share the
/// Atmosphere parameters (<c>u_atmo</c>, see atmosphere.sh) set once per frame through <see cref="SetAtmosphere"/>.
/// Editor behaviour stays in <see cref="Renderer"/>; this class only turns its draws into bgfx submits.
/// </summary>
internal sealed unsafe class BgfxEditorGraphics : IGpuMeshFactory, IDisposable
{
    public const ushort WorldView = 0;
    public const ushort CaptureView = 2;
    public const ushort CaptureBlitView = 3;

    /// <summary>The OpenGL editor's clear colour (0.58, 0.78, 0.96) as RGBA8.</summary>
    public const uint SkyClearColor = 0x94C7F5FF;

    private const ulong WriteColor = (ulong)(BgfxNative.StateFlags.WriteRgb | BgfxNative.StateFlags.WriteA);

    // GL BlendFunc(SrcAlpha, OneMinusSrcAlpha) for colour and alpha.
    private const ulong BlendAlpha =
        ((ulong)BgfxNative.StateFlags.BlendSrcAlpha | ((ulong)BgfxNative.StateFlags.BlendInvSrcAlpha << 4))
        | (((ulong)BgfxNative.StateFlags.BlendSrcAlpha | ((ulong)BgfxNative.StateFlags.BlendInvSrcAlpha << 4)) << 8);

    /// <summary>GL baseline: depth test LESS + depth write, no culling (environment, unlit world geometry).</summary>
    public const ulong OpaqueState = WriteColor | (ulong)BgfxNative.StateFlags.WriteZ | (ulong)BgfxNative.StateFlags.DepthTestLess;

    /// <summary>Lit parts: as opaque, with GL's counter-clockwise front face so gl_FrontFacing matches.</summary>
    public const ulong LitState = OpaqueState | (ulong)BgfxNative.StateFlags.FrontCcw;

    /// <summary>Selection outline: depth LEQUAL, no depth write (GL DepthMask(false) + DepthFunc(Lequal)).</summary>
    public const ulong OutlineState = WriteColor | (ulong)BgfxNative.StateFlags.DepthTestLequal;

    /// <summary>GL Disable(DepthTest): no depth test and no depth write (gizmos, label backgrounds).</summary>
    public const ulong OverlayState = WriteColor;

    /// <summary>Depth off + alpha blending (label glyphs).</summary>
    public const ulong OverlayBlendState = WriteColor | BlendAlpha;

    /// <summary>Selected parts mark stencil = 1 from their own fragments (GL: Always 1, Keep/Keep/Replace).</summary>
    public const uint SelectionMaskStencil =
        (uint)BgfxNative.StencilFlags.TestAlways
        | (1u << (int)BgfxNative.StencilFlags.FuncRefShift)
        | (0xFFu << (int)BgfxNative.StencilFlags.FuncRmaskShift)
        | (uint)BgfxNative.StencilFlags.OpFailSKeep
        | (uint)BgfxNative.StencilFlags.OpFailZKeep
        | (uint)BgfxNative.StencilFlags.OpPassZReplace;

    /// <summary>Outline only where stencil != 1, writing nothing (GL: NotEqual 1, Keep/Keep/Keep, mask 0).</summary>
    public const uint OutlineStencil =
        (uint)BgfxNative.StencilFlags.TestNotequal
        | (1u << (int)BgfxNative.StencilFlags.FuncRefShift)
        | (0xFFu << (int)BgfxNative.StencilFlags.FuncRmaskShift)
        | (uint)BgfxNative.StencilFlags.OpFailSKeep
        | (uint)BgfxNative.StencilFlags.OpFailZKeep
        | (uint)BgfxNative.StencilFlags.OpPassZKeep;

    private const int MeshFloatsPerVertex = 9;  // position3 + colour3 + normal3 (ColoredMesh packing)
    private const int TextFloatsPerVertex = 8;  // position3 + uv2 + colour3 (DimensionLabelRenderer packing)
    private const int CaptureFrameLimit = 8;
    private const byte FrameFlush = 0x04; // BGFX_FRAME_FLUSH: execute submitted work without presenting

    /// <summary>Live static mesh buffers (diagnostics for leak checks across Hub/editor cycles).</summary>
    internal static int LiveMeshes;

    private readonly float[] _atmosphere = Engine.Atmosphere.AtmosphereUniforms.Create();
    private BgfxNative.VertexLayout _meshLayout;
    private BgfxNative.VertexLayout _textLayout;
    private BgfxNative.ProgramHandle _unlit;
    private BgfxNative.ProgramHandle _lit;
    private BgfxNative.ProgramHandle _environment;
    private BgfxNative.ProgramHandle _sky;
    private BgfxNative.ProgramHandle _worldText;
    private BgfxNative.UniformHandle _uAtmosphere;
    private BgfxNative.UniformHandle _uNormalMatrix;
    private BgfxNative.UniformHandle _sTex;
    private BgfxNative.VertexBufferHandle _skyTriangle;
    private bool _homogeneousDepth;
    private bool _originBottomLeft;
    private bool _transientWarned;
    private bool _initialized;

    // Thumbnail capture targets (recreated when the framebuffer size changes).
    private BgfxNative.FrameBufferHandle _captureFrameBuffer = new() { idx = ushort.MaxValue };
    private BgfxNative.TextureHandle _captureColor = new() { idx = ushort.MaxValue };
    private BgfxNative.TextureHandle _captureReadback = new() { idx = ushort.MaxValue };
    private int _captureWidth;
    private int _captureHeight;

    public BgfxNative.ProgramHandle UnlitProgram => _unlit;

    public void Initialize()
    {
        fixed (BgfxNative.VertexLayout* layout = &_meshLayout)
        {
            BgfxNative.vertex_layout_begin(layout, BgfxNative.RendererType.Noop);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Position, 3, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Color0, 3, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Normal, 3, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_end(layout);
        }

        fixed (BgfxNative.VertexLayout* layout = &_textLayout)
        {
            BgfxNative.vertex_layout_begin(layout, BgfxNative.RendererType.Noop);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Position, 3, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.TexCoord0, 2, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_add(layout, BgfxNative.Attrib.Color0, 3, BgfxNative.AttribType.Float, false, false);
            BgfxNative.vertex_layout_end(layout);
        }

        if (_meshLayout.stride != MeshFloatsPerVertex * sizeof(float) || _textLayout.stride != TextFloatsPerVertex * sizeof(float))
        {
            throw new InvalidOperationException($"bgfx editor vertex layout strides {_meshLayout.stride}/{_textLayout.stride} do not match the CPU packing.");
        }

        var vsUnlit = BgfxShaders.Load("vs_unlit");
        var fsUnlit = BgfxShaders.Load("fs_unlit");
        var vsLit = BgfxShaders.Load("vs_lit");
        var fsLit = BgfxShaders.Load("fs_lit");
        var vsEnvironment = BgfxShaders.Load("vs_env");
        var fsEnvironment = BgfxShaders.Load("fs_env");
        var vsSky = BgfxShaders.Load("vs_atmo_sky");
        var fsSky = BgfxShaders.Load("fs_atmo_sky");
        var vsText = BgfxShaders.Load("vs_world_text");
        var fsText = BgfxShaders.Load("fs_world_text");
        // destroyShaders: true -> each program owns its two shaders.
        _unlit = CreateProgram(vsUnlit, fsUnlit, "unlit");
        _lit = CreateProgram(vsLit, fsLit, "lit");
        _environment = CreateProgram(vsEnvironment, fsEnvironment, "environment");
        _sky = CreateProgram(vsSky, fsSky, "atmosphere sky");
        _worldText = CreateProgram(vsText, fsText, "world text");

        _uAtmosphere = CreateUniform("u_atmo", BgfxNative.UniformType.Vec4, Engine.Atmosphere.AtmosphereUniforms.Vec4Count);
        _uNormalMatrix = CreateUniform("u_normalMatrix", BgfxNative.UniformType.Mat4);
        _sTex = CreateUniform("s_tex", BgfxNative.UniformType.Sampler);

        // Full-screen sky triangle: clip-space corners in x/y of the mesh layout (the shader passes them through).
        float[] corners = [-1, -1, 3, -1, -1, 3];
        var skyVertices = new float[3 * MeshFloatsPerVertex];
        for (var i = 0; i < 3; i++)
        {
            skyVertices[i * MeshFloatsPerVertex] = corners[i * 2];
            skyVertices[i * MeshFloatsPerVertex + 1] = corners[i * 2 + 1];
        }

        fixed (float* data = skyVertices)
        fixed (BgfxNative.VertexLayout* layout = &_meshLayout)
        {
            _skyTriangle = BgfxNative.create_vertex_buffer(
                BgfxNative.copy(data, (uint)(skyVertices.Length * sizeof(float))), layout, (ushort)BgfxNative.BufferFlags.None);
        }

        var caps = BgfxNative.get_caps();
        _homogeneousDepth = caps->homogeneousDepth != 0;
        _originBottomLeft = caps->originBottomLeft != 0;
        _initialized = true;
    }

    public IGpuMesh Create(Vertex[] vertices, uint[]? indices, MeshTopology topology)
    {
        // LineLoop has no bgfx primitive: expand to a closed line list. (GL line widths above 1 are not
        // supported by the macOS core profile either, so all lines draw 1px on both backends.)
        if (topology == MeshTopology.LineLoop && indices is null)
        {
            var loop = new Vertex[vertices.Length * 2];
            for (var i = 0; i < vertices.Length; i++)
            {
                loop[i * 2] = vertices[i];
                loop[i * 2 + 1] = vertices[(i + 1) % vertices.Length];
            }

            vertices = loop;
            topology = MeshTopology.Lines;
        }

        var data = Pack(vertices);
        var mesh = new BgfxGpuMesh
        {
            VertexCount = (uint)vertices.Length,
            PrimitiveState = topology switch
            {
                MeshTopology.Lines => (ulong)BgfxNative.StateFlags.PtLines,
                MeshTopology.LineStrip => (ulong)BgfxNative.StateFlags.PtLinestrip,
                _ => 0
            }
        };

        fixed (float* source = data)
        fixed (BgfxNative.VertexLayout* layout = &_meshLayout)
        {
            mesh.Vertices = BgfxNative.create_vertex_buffer(
                BgfxNative.copy(source, (uint)(data.Length * sizeof(float))), layout, (ushort)BgfxNative.BufferFlags.None);
        }

        if (indices is { Length: > 0 })
        {
            fixed (uint* source = indices)
            {
                mesh.Indices = BgfxNative.create_index_buffer(
                    BgfxNative.copy(source, (uint)(indices.Length * sizeof(uint))), (ushort)BgfxNative.BufferFlags.Index32);
            }

            mesh.Indexed = true;
            mesh.IndexCount = (uint)indices.Length;
        }

        LiveMeshes++;
        return mesh;
    }

    /// <summary>
    /// Camera for a view. bgfx/Metal uses a 0..1 clip depth (homogeneousDepth == false), so the GL-style
    /// -1..1 projection from EditorCamera is remapped (z' = 0.5 z + 0.5 w); x/y (and picking) are unchanged.
    /// </summary>
    public void SetCamera(ushort view, Matrix4 viewMatrix, Matrix4 glProjection)
    {
        var projection = _homogeneousDepth ? glProjection : RemapDepthToZeroOne(glProjection);
        var v = viewMatrix.ToOpenGlColumnMajor();
        var p = projection.ToOpenGlColumnMajor();
        fixed (float* pv = v)
        fixed (float* pp = p)
        {
            BgfxNative.set_view_transform(view, pv, pp);
        }

        BgfxNative.set_view_mode(view, BgfxNative.ViewMode.Sequential); // keep the OpenGL draw order
    }

    /// <summary>This frame's Atmosphere parameters (<see cref="Engine.Atmosphere.AtmosphereUniforms"/>), used by the
    /// sky, environment and lit submits that follow.</summary>
    public void SetAtmosphere(ReadOnlySpan<float> parameters) => parameters[.._atmosphere.Length].CopyTo(_atmosphere);

    public void SubmitUnlit(ushort view, ColoredMesh mesh, in Matrix4 model, ulong state, uint stencil = 0) =>
        SubmitMesh(view, mesh, _unlit, model, state, stencil);

    /// <summary>Environment geometry (baseplate): unlit colours with the Atmosphere applied.</summary>
    public void SubmitEnvironment(ushort view, ColoredMesh mesh, in Matrix4 model, ulong state)
    {
        SetAtmosphereUniform();
        SubmitMesh(view, mesh, _environment, model, state, 0);
    }

    /// <summary>Lit part surface: model transform, its inverse-transpose normal matrix and the Atmosphere light.</summary>
    public void SubmitLit(ushort view, ColoredMesh mesh, in Matrix4 model, uint stencil)
    {
        var normalMatrix = NormalMatrix(model);
        fixed (float* pn = normalMatrix)
        {
            BgfxNative.set_uniform(_uNormalMatrix, pn, 1);
        }

        SetAtmosphereUniform();
        SubmitMesh(view, mesh, _lit, model, LitState, stencil);
    }

    /// <summary>A CPU-filled vertex buffer for <paramref name="vertexCount"/> vertices (see <see cref="UpdateDynamicMesh"/>).</summary>
    public BgfxDynamicMesh CreateDynamicMesh(int vertexCount)
    {
        var mesh = new BgfxDynamicMesh { VertexCount = (uint)vertexCount };
        fixed (BgfxNative.VertexLayout* layout = &_meshLayout)
        {
            mesh.Vertices = BgfxNative.create_dynamic_vertex_buffer((uint)vertexCount, layout, (ushort)BgfxNative.BufferFlags.None);
        }

        BgfxDynamicMesh.Live++;
        return mesh;
    }

    /// <summary>Replaces a dynamic buffer's vertices (position3 + colour3 + normal3 floats per vertex, as ColoredMesh packs).</summary>
    public void UpdateDynamicMesh(BgfxDynamicMesh mesh, float[] packed)
    {
        fixed (float* source = packed)
        {
            BgfxNative.update_dynamic_vertex_buffer(mesh.Vertices, 0, BgfxNative.copy(source, (uint)(packed.Length * sizeof(float))));
        }
    }

    /// <summary>A lit surface whose vertices come from a dynamic buffer and whose triangles from <paramref name="indexSource"/>.</summary>
    public void SubmitLitDynamic(ushort view, BgfxDynamicMesh vertices, ColoredMesh indexSource, in Matrix4 model, uint stencil)
    {
        var normalMatrix = NormalMatrix(model);
        fixed (float* pn = normalMatrix)
        {
            BgfxNative.set_uniform(_uNormalMatrix, pn, 1);
        }

        SetAtmosphereUniform();
        SubmitDynamic(view, vertices, indexSource, _lit, model, LitState, stencil);
    }

    /// <summary>As <see cref="SubmitUnlit"/> for a dynamic buffer (the selection rim of a skinned mesh).</summary>
    public void SubmitUnlitDynamic(ushort view, BgfxDynamicMesh vertices, ColoredMesh indexSource, in Matrix4 model, ulong state, uint stencil) =>
        SubmitDynamic(view, vertices, indexSource, _unlit, model, state, stencil);

    private static void SubmitDynamic(ushort view, BgfxDynamicMesh vertices, ColoredMesh indexSource, BgfxNative.ProgramHandle program, in Matrix4 model, ulong state, uint stencil)
    {
        if (indexSource.Gpu is not BgfxGpuMesh { Indexed: true } indexed)
        {
            return;
        }

        var m = model.ToOpenGlColumnMajor();
        fixed (float* pm = m)
        {
            BgfxNative.set_transform(pm, 1);
        }

        BgfxNative.set_dynamic_vertex_buffer(0, vertices.Vertices, 0, vertices.VertexCount);
        BgfxNative.set_index_buffer(indexed.Indices, 0, indexed.IndexCount);
        if (stencil != 0)
        {
            BgfxNative.set_stencil(stencil, (uint)BgfxNative.StencilFlags.None);
        }

        BgfxNative.set_state(state, 0);
        BgfxNative.submit(view, program, 0, (byte)BgfxNative.DiscardFlags.All);
    }

    /// <summary>The sky behind everything: submit first in the view; no depth test or write.</summary>
    public void SubmitAtmosphereSky(ushort view)
    {
        SetAtmosphereUniform();
        BgfxNative.set_vertex_buffer(0, _skyTriangle, 0, 3);
        SubmitIdentity(view, _sky, WriteColor);
    }

    /// <summary>
    /// Large per-frame geometry (Lua particles, debug lines), identity model: as many whole primitives
    /// (<paramref name="perPrimitive"/> vertices each) as the frame's transient vertex space holds while
    /// <see cref="TransientBudget.ReserveBytes"/> stay free for the overlays and UI drawn after it, instead of none.
    /// With <paramref name="atmosphere"/> the environment program draws it (haze, fog and image controls, as the OpenGL
    /// editor draws Lua particles); otherwise the unlit one. Returns the vertices drawn.
    /// </summary>
    public uint SubmitTransientBounded(ushort view, IReadOnlyList<Vertex> vertices, ulong state, uint perPrimitive, bool atmosphere)
    {
        var requested = (uint)vertices.Count;
        if (requested == 0)
        {
            return 0;
        }

        var reserve = TransientBudget.ReserveBytes / _meshLayout.stride;
        uint count;
        BgfxNative.TransientVertexBuffer buffer;
        fixed (BgfxNative.VertexLayout* layout = &_meshLayout)
        {
            var available = BgfxNative.get_avail_transient_vertex_buffer(TransientBudget.Query(requested, reserve), layout);
            count = TransientBudget.Affordable(requested, available, reserve, perPrimitive);
            if (count == 0)
            {
                return 0;
            }

            BgfxNative.alloc_transient_vertex_buffer(&buffer, count, layout);
        }

        PackTransient(vertices, count, buffer);
        BgfxNative.set_transient_vertex_buffer(0, &buffer, 0, count);
        if (atmosphere)
        {
            SetAtmosphereUniform();
        }

        SubmitIdentity(view, atmosphere ? _environment : _unlit, state);
        return count;
    }

    /// <summary>Per-frame triangles (pos + colour), identity model, unlit (Rotate arcs, label backgrounds).</summary>
    public void SubmitTransientTriangles(ushort view, IReadOnlyList<Vertex> vertices, ulong state)
    {
        var count = (uint)vertices.Count;
        if (count == 0 || !TryAllocTransient(count, ref _meshLayout, out var buffer))
        {
            return;
        }

        PackTransient(vertices, count, buffer);
        BgfxNative.set_transient_vertex_buffer(0, &buffer, 0, count);
        SubmitIdentity(view, _unlit, state);
    }

    // The first count vertices into a transient buffer, packed as ColoredMesh packs them (position, colour, normal).
    private static void PackTransient(IReadOnlyList<Vertex> vertices, uint count, BgfxNative.TransientVertexBuffer buffer)
    {
        var target = (float*)buffer.data;
        for (var i = 0; i < (int)count; i++)
        {
            var v = vertices[i];
            var o = i * MeshFloatsPerVertex;
            target[o] = v.Position.X;
            target[o + 1] = v.Position.Y;
            target[o + 2] = v.Position.Z;
            target[o + 3] = v.Color.X;
            target[o + 4] = v.Color.Y;
            target[o + 5] = v.Color.Z;
            target[o + 6] = v.Normal.X;
            target[o + 7] = v.Normal.Y;
            target[o + 8] = v.Normal.Z;
        }
    }

    /// <summary>World-space glyph quads (pos3 + uv2 + colour3 floats) sampled from the glyph atlas.</summary>
    public void SubmitWorldText(ushort view, List<float> glyphFloats, int atlasTexture)
    {
        var count = (uint)(glyphFloats.Count / TextFloatsPerVertex);
        if (count == 0 || !TryAllocTransient(count, ref _textLayout, out var buffer))
        {
            return;
        }

        fixed (float* source = CollectionsMarshal.AsSpan(glyphFloats))
        {
            Buffer.MemoryCopy(source, buffer.data, buffer.size, glyphFloats.Count * (long)sizeof(float));
        }

        BgfxNative.set_transient_vertex_buffer(0, &buffer, 0, count);
        BgfxNative.set_texture(0, _sTex, new BgfxNative.TextureHandle { idx = (ushort)atlasTexture }, uint.MaxValue);
        SubmitIdentity(view, _worldText, OverlayBlendState);
    }

    /// <summary>
    /// Renders the world through <paramref name="submitWorld"/> into an offscreen target, then reads it back.
    /// bgfx readback is asynchronous: this flushes frames (without presenting) only until the returned
    /// frame is reached, bounded by <see cref="CaptureFrameLimit"/>. Returns bottom-up RGBA rows like
    /// glReadPixels so the shared OpenGL resampling path can be reused.
    /// </summary>
    public bool TryCaptureWorld(int width, int height, Action<ushort> submitWorld, out byte[] bottomUpRgba)
    {
        bottomUpRgba = [];
        if (!_initialized || width <= 0 || height <= 0)
        {
            return false;
        }

        EnsureCaptureTargets(width, height);
        BgfxNative.set_view_frame_buffer(CaptureView, _captureFrameBuffer);
        BgfxNative.set_view_rect(CaptureView, 0, 0, (ushort)width, (ushort)height, 0f, 1f);
        BgfxNative.set_view_clear(
            CaptureView,
            (ushort)(BgfxNative.ClearFlags.Color | BgfxNative.ClearFlags.Depth | BgfxNative.ClearFlags.Stencil),
            SkyClearColor,
            1f,
            0);
        submitWorld(CaptureView);

        BgfxNative.TextureRegion destination;
        BgfxNative.TextureRegion source;
        BgfxNative.texture_region_init(&destination, _captureReadback, 0, 0, (ushort)width, (ushort)height);
        BgfxNative.texture_region_init(&source, _captureColor, 0, 0, (ushort)width, (ushort)height);
        BgfxNative.blit(CaptureBlitView, &destination, &source);
        BgfxNative.touch(CaptureBlitView);

        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            BgfxNative.TextureRegion readRegion;
            BgfxNative.texture_region_init(&readRegion, _captureReadback, 0, 0, (ushort)width, (ushort)height);
            var readyFrame = BgfxNative.read_texture(&readRegion, (void*)handle.AddrOfPinnedObject());
            var frame = 0u;
            for (var i = 0; i < CaptureFrameLimit; i++)
            {
                frame = BgfxNative.frame(FrameFlush);
                if (frame >= readyFrame)
                {
                    break;
                }
            }

            if (frame < readyFrame)
            {
                Console.WriteLine($"[Renderer] bgfx thumbnail readback not ready after {CaptureFrameLimit} frames; skipped");
                return false;
            }
        }
        finally
        {
            handle.Free();
        }

        bottomUpRgba = _originBottomLeft ? pixels : FlipRows(pixels, width, height);
        return true;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        DestroyCaptureTargets();
        BgfxNative.destroy_vertex_buffer(_skyTriangle);
        BgfxNative.destroy_program(_unlit);
        BgfxNative.destroy_program(_lit);
        BgfxNative.destroy_program(_environment);
        BgfxNative.destroy_program(_sky);
        BgfxNative.destroy_program(_worldText);
        BgfxNative.destroy_uniform(_uAtmosphere);
        BgfxNative.destroy_uniform(_uNormalMatrix);
        BgfxNative.destroy_uniform(_sTex);
    }

    private void SubmitMesh(ushort view, ColoredMesh mesh, BgfxNative.ProgramHandle program, in Matrix4 model, ulong state, uint stencil)
    {
        if (mesh.Gpu is not BgfxGpuMesh gpu)
        {
            return;
        }

        var m = model.ToOpenGlColumnMajor();
        fixed (float* pm = m)
        {
            BgfxNative.set_transform(pm, 1);
        }

        BgfxNative.set_vertex_buffer(0, gpu.Vertices, 0, gpu.VertexCount);
        if (gpu.Indexed)
        {
            BgfxNative.set_index_buffer(gpu.Indices, 0, gpu.IndexCount);
        }

        if (stencil != 0)
        {
            BgfxNative.set_stencil(stencil, (uint)BgfxNative.StencilFlags.None);
        }

        BgfxNative.set_state(state | gpu.PrimitiveState, 0);
        BgfxNative.submit(view, program, 0, (byte)BgfxNative.DiscardFlags.All);
    }

    private void SetAtmosphereUniform()
    {
        fixed (float* parameters = _atmosphere)
        {
            BgfxNative.set_uniform(_uAtmosphere, parameters, Engine.Atmosphere.AtmosphereUniforms.Vec4Count);
        }
    }

    private static void SubmitIdentity(ushort view, BgfxNative.ProgramHandle program, ulong state)
    {
        var identity = Matrix4.Identity.ToOpenGlColumnMajor();
        fixed (float* pm = identity)
        {
            BgfxNative.set_transform(pm, 1);
        }

        BgfxNative.set_state(state, 0);
        BgfxNative.submit(view, program, 0, (byte)BgfxNative.DiscardFlags.All);
    }

    private bool TryAllocTransient(uint count, ref BgfxNative.VertexLayout layout, out BgfxNative.TransientVertexBuffer buffer)
    {
        buffer = default;
        fixed (BgfxNative.VertexLayout* pl = &layout)
        {
            if (BgfxNative.get_avail_transient_vertex_buffer(count, pl) < count)
            {
                if (!_transientWarned)
                {
                    _transientWarned = true;
                    Console.WriteLine($"[Renderer] bgfx editor: transient vertex space exhausted ({count} vertices); draw skipped");
                }

                return false;
            }

            BgfxNative.TransientVertexBuffer allocated;
            BgfxNative.alloc_transient_vertex_buffer(&allocated, count, pl);
            buffer = allocated;
        }

        return true;
    }

    private void EnsureCaptureTargets(int width, int height)
    {
        if (_captureFrameBuffer.Valid && _captureWidth == width && _captureHeight == height)
        {
            return;
        }

        DestroyCaptureTargets();
        const ulong clamp = (ulong)(BgfxNative.SamplerFlags.UClamp | BgfxNative.SamplerFlags.VClamp);
        _captureColor = BgfxNative.create_texture_2d(
            (ushort)width, (ushort)height, false, 1, BgfxNative.TextureFormat.RGBA8,
            (ulong)BgfxNative.TextureFlags.Rt | clamp, null, 0);
        var depth = BgfxNative.create_texture_2d(
            (ushort)width, (ushort)height, false, 1, BgfxNative.TextureFormat.D24S8,
            (ulong)BgfxNative.TextureFlags.RtWriteOnly, null, 0);
        var attachments = stackalloc BgfxNative.TextureHandle[2] { _captureColor, depth };
        _captureFrameBuffer = BgfxNative.create_frame_buffer_from_handles(2, attachments, true); // owns both textures
        _captureReadback = BgfxNative.create_texture_2d(
            (ushort)width, (ushort)height, false, 1, BgfxNative.TextureFormat.RGBA8,
            (ulong)(BgfxNative.TextureFlags.BlitDst | BgfxNative.TextureFlags.ReadBack) | clamp, null, 0);
        _captureWidth = width;
        _captureHeight = height;
    }

    private void DestroyCaptureTargets()
    {
        if (_captureFrameBuffer.Valid)
        {
            BgfxNative.destroy_frame_buffer(_captureFrameBuffer); // also destroys its colour + depth textures
            _captureFrameBuffer = new BgfxNative.FrameBufferHandle { idx = ushort.MaxValue };
            _captureColor = new BgfxNative.TextureHandle { idx = ushort.MaxValue };
        }

        if (_captureReadback.Valid)
        {
            BgfxNative.destroy_texture(_captureReadback);
            _captureReadback = new BgfxNative.TextureHandle { idx = ushort.MaxValue };
        }
    }

    private BgfxNative.ProgramHandle CreateProgram(BgfxNative.ShaderHandle vs, BgfxNative.ShaderHandle fs, string name)
    {
        var program = BgfxNative.create_program(vs, fs, true);
        if (!program.Valid)
        {
            throw new InvalidOperationException($"bgfx {name} program could not be created.");
        }

        return program;
    }

    private static BgfxNative.UniformHandle CreateUniform(string name, BgfxNative.UniformType type, ushort count = 1)
    {
        var handle = BgfxNative.create_uniform(name, type, count);
        if (!handle.Valid)
        {
            throw new InvalidOperationException($"bgfx uniform {name} could not be created.");
        }

        return handle;
    }

    private static float[] Pack(Vertex[] vertices)
    {
        var data = new float[vertices.Length * MeshFloatsPerVertex];
        for (var i = 0; i < vertices.Length; i++)
        {
            var o = i * MeshFloatsPerVertex;
            data[o] = vertices[i].Position.X;
            data[o + 1] = vertices[i].Position.Y;
            data[o + 2] = vertices[i].Position.Z;
            data[o + 3] = vertices[i].Color.X;
            data[o + 4] = vertices[i].Color.Y;
            data[o + 5] = vertices[i].Color.Z;
            data[o + 6] = vertices[i].Normal.X;
            data[o + 7] = vertices[i].Normal.Y;
            data[o + 8] = vertices[i].Normal.Z;
        }

        return data;
    }

    private static Matrix4 RemapDepthToZeroOne(Matrix4 p) => new(
        p.M11, p.M12, p.M13, p.M14,
        p.M21, p.M22, p.M23, p.M24,
        0.5f * (p.M31 + p.M41), 0.5f * (p.M32 + p.M42), 0.5f * (p.M33 + p.M43), 0.5f * (p.M34 + p.M44),
        p.M41, p.M42, p.M43, p.M44);

    /// <summary>
    /// Column-major mat4 holding transpose(inverse(upper-left 3x3 of the model)), the GL lit shader's
    /// normal matrix, computed on the CPU (shaderc's Metal path has no inverse()).
    /// </summary>
    private static float[] NormalMatrix(in Matrix4 m)
    {
        float a = m.M11, b = m.M12, c = m.M13;
        float d = m.M21, e = m.M22, f = m.M23;
        float g = m.M31, h = m.M32, i = m.M33;
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        var result = new float[16];
        result[15] = 1f;
        if (MathF.Abs(det) < 1e-12f)
        {
            return result; // degenerate scale: zero normals -> ambient only (GL inverse would be undefined)
        }

        var s = 1f / det;
        // Cofactor matrix / det == transpose(inverse(M)). Row r, column c of that matrix:
        float n11 = (e * i - f * h) * s, n12 = -(d * i - f * g) * s, n13 = (d * h - e * g) * s;
        float n21 = -(b * i - c * h) * s, n22 = (a * i - c * g) * s, n23 = -(a * h - b * g) * s;
        float n31 = (b * f - c * e) * s, n32 = -(a * f - c * d) * s, n33 = (a * e - b * d) * s;
        // Column-major upload (same convention as ToOpenGlColumnMajor).
        result[0] = n11; result[1] = n21; result[2] = n31;
        result[4] = n12; result[5] = n22; result[6] = n32;
        result[8] = n13; result[9] = n23; result[10] = n33;
        return result;
    }

    private static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        var stride = width * 4;
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
        }

        return flipped;
    }
}
