using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// Environment reflections (docs/graphics.md, "Reflections"): the Atmosphere sky rendered into a small HDR cube
/// map and prefiltered for roughness (each mip level a wider GGX lobe), sampled by Lit materials along their
/// reflection direction, plus the split-sum BRDF table (computed once). It reflects the sky only, not the scene: a
/// static, resource-light approximation, not real-time reflections of objects. Refreshed only when the sky changes
/// (at most every <see cref="MinFramesBetweenUpdates"/> frames, so a day/night cycle stays cheap).
/// </summary>
internal sealed class EnvironmentLighting : IDisposable
{
    public const int MinFramesBetweenUpdates = 10;
    private const int LutSize = 128;

    private int _skyCube;
    private int _prefiltered;
    private int _brdfLut;
    private int _fbo;
    private int _size;
    private int _levels;
    private readonly float[] _lastSky = new float[Atmosphere.AtmosphereUniforms.FloatCount];
    private bool _hasSky;
    private int _framesSinceUpdate = int.MaxValue / 2;

    public int PrefilteredCube => _prefiltered;

    public int BrdfLut => _brdfLut;

    public float MaxLod => global::System.Math.Max(0, _levels - 1);

    /// <summary>Times the cube map was re-rendered (diagnostics).</summary>
    public int Updates { get; private set; }

    public long Bytes => (long)_size * _size * 6 * 8 * 2 * 4 / 3 + LutSize * LutSize * 4;

    public bool Initialize(GlProgram brdf, int fullscreenVao)
    {
        GL.Enable(EnableCap.TextureCubeMapSeamless);
        _fbo = GlResources.Framebuffer();
        _brdfLut = GlResources.Texture2D(PixelInternalFormat.Rg16f, LutSize, LutSize, PixelFormat.Rg, PixelType.HalfFloat);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _brdfLut, 0);
        if (!GlResources.Complete("BRDF table"))
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return false;
        }

        GL.Viewport(0, 0, LutSize, LutSize);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        brdf.Use();
        brdf.Set("uQuadDepth", 0f);
        GL.BindVertexArray(fullscreenVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Enable(EnableCap.DepthTest);
        return true;
    }

    /// <summary>
    /// Re-renders and prefilters the sky cube when the sky (the linear Atmosphere parameters) or the size changed.
    /// </summary>
    public void Update(GlProgram sky, GlProgram prefilter, float[] skyUniforms, int size, int fullscreenVao)
    {
        _framesSinceUpdate++;
        var resized = size != _size || _skyCube == 0;
        if (resized)
        {
            Allocate(size);
        }

        // Sky, sun, fog and ground parameters only: u_atmo[7] (camera position) and [9]-[11] (camera basis) change
        // with every camera move and do not affect the sky's colours.
        var changed = !_hasSky || !skyUniforms.AsSpan(0, 28).SequenceEqual(_lastSky.AsSpan(0, 28))
            || !skyUniforms.AsSpan(32, 4).SequenceEqual(_lastSky.AsSpan(32, 4))
            || !skyUniforms.AsSpan(48, 4).SequenceEqual(_lastSky.AsSpan(48, 4));
        if (!resized && (!changed || _framesSinceUpdate < MinFramesBetweenUpdates))
        {
            return;
        }

        skyUniforms.CopyTo(_lastSky, 0);
        _hasSky = true;
        _framesSinceUpdate = 0;
        Updates++;

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.BindVertexArray(fullscreenVao);

        // 1. The sky into each face of the source cube (top level), then its mip chain.
        sky.Use();
        sky.SetVec4s("u_atmo", skyUniforms);
        sky.Set("uSunDiscOn", 0);
        sky.Set("uQuadDepth", 0f);
        GL.Viewport(0, 0, _size, _size);
        for (var face = 0; face < 6; face++)
        {
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.TextureCubeMapPositiveX + face, _skyCube, 0);
            var (forward, right, up) = FaceBasis(face);
            sky.Set("uRayForward", forward);
            sky.Set("uRayRight", right);
            sky.Set("uRayUp", up);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        GL.BindTexture(TextureTarget.TextureCubeMap, _skyCube);
        GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);

        // 2. Prefilter: level l holds roughness l / (levels - 1).
        prefilter.Use();
        prefilter.Sampler("uSource", 0);
        prefilter.Set("uSourceSize", (float)_size);
        prefilter.Set("uQuadDepth", 0f);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.TextureCubeMap, _skyCube);
        for (var level = 0; level < _levels; level++)
        {
            var levelSize = global::System.Math.Max(1, _size >> level);
            GL.Viewport(0, 0, levelSize, levelSize);
            prefilter.Set("uRoughness", _levels > 1 ? level / (float)(_levels - 1) : 0f);
            for (var face = 0; face < 6; face++)
            {
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.TextureCubeMapPositiveX + face, _prefiltered, level);
                var (forward, right, up) = FaceBasis(face);
                prefilter.Set("uFaceForward", forward);
                prefilter.Set("uFaceRight", right);
                prefilter.Set("uFaceUp", up);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
        }

        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
    }

    // The direction through each face: forward + x * right + y * up for x, y in -1..1 (OpenGL's cube-map layout).
    private static (Math.Vector3 Forward, Math.Vector3 Right, Math.Vector3 Up) FaceBasis(int face) => face switch
    {
        0 => (new(1, 0, 0), new(0, 0, -1), new(0, -1, 0)),
        1 => (new(-1, 0, 0), new(0, 0, 1), new(0, -1, 0)),
        2 => (new(0, 1, 0), new(1, 0, 0), new(0, 0, 1)),
        3 => (new(0, -1, 0), new(1, 0, 0), new(0, 0, -1)),
        4 => (new(0, 0, 1), new(1, 0, 0), new(0, -1, 0)),
        _ => (new(0, 0, -1), new(-1, 0, 0), new(0, -1, 0))
    };

    private void Allocate(int size)
    {
        DeleteCubes();
        _size = size;
        _levels = global::System.Math.Clamp((int)MathF.Log2(size) - 1, 1, 6);
        _skyCube = Cube(size, mipmapped: true, levels: (int)MathF.Log2(size) + 1);
        _prefiltered = Cube(size, mipmapped: true, levels: _levels);
        _hasSky = false;
    }

    private static int Cube(int size, bool mipmapped, int levels)
    {
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, id);
        for (var level = 0; level < levels; level++)
        {
            var s = global::System.Math.Max(1, size >> level);
            for (var face = 0; face < 6; face++)
            {
                GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, level, PixelInternalFormat.Rgba16f, s, s, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
            }
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, mipmapped ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, levels - 1);
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
        return id;
    }

    private void DeleteCubes()
    {
        if (_skyCube != 0)
        {
            GL.DeleteTexture(_skyCube);
            _skyCube = 0;
        }

        if (_prefiltered != 0)
        {
            GL.DeleteTexture(_prefiltered);
            _prefiltered = 0;
        }
    }

    public void Dispose()
    {
        DeleteCubes();
        GlResources.DeleteTexture(ref _brdfLut);
        GlResources.DeleteFramebuffer(ref _fbo);
        _size = 0;
    }
}
