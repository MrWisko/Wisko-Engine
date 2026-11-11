using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// The off-screen images one view needs with post-processing on: the scene (HDR RGBA16F colour + depth-stencil,
/// multisampled when MSAA is on), its single-sample resolves (HDR colour, depth for ambient occlusion and the depth
/// copy), the ambient occlusion images, the bloom chain and an 8-bit image for FXAA. Re-created only when the size,
/// sample count or quality changes; released on <see cref="Dispose"/>.
/// </summary>
internal sealed class ViewTargets : IDisposable
{
    private int _sceneFbo;
    private int _sceneColorRb;
    private int _sceneDepthRb;
    private int _resolveFbo;
    private int _hdrTexture;
    private int _depthResolveFbo;
    private int _depthTexture;
    private int _aoFbo;
    private int _aoTexture;
    private int _aoBlurFbo;
    private int _aoBlurTexture;
    private int _ldrFbo;
    private int _ldrTexture;
    private readonly List<(int Fbo, int Texture, int Width, int Height)> _bloom = [];

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Samples { get; private set; }

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    public bool AoHalf { get; private set; }

    public int AoWidth { get; private set; }

    public int AoHeight { get; private set; }

    public bool Valid { get; private set; }

    public int SceneFbo => _sceneFbo;

    public int HdrTexture => _hdrTexture;

    public int DepthTexture => _depthTexture;

    public int AoFbo => _aoFbo;

    public int AoTexture => _aoTexture;

    public int AoBlurFbo => _aoBlurFbo;

    public int AoBlurTexture => _aoBlurTexture;

    public int LdrFbo => _ldrFbo;

    public int LdrTexture => _ldrTexture;

    public IReadOnlyList<(int Fbo, int Texture, int Width, int Height)> Bloom => _bloom;

    /// <summary>Bytes of GPU memory the targets take (estimate).</summary>
    public long Bytes { get; private set; }

    public bool Matches(int width, int height, int samples, bool aoHalf, int bloomLevels, int outputWidth, int outputHeight) =>
        Valid && Width == width && Height == height && Samples == samples && AoHalf == aoHalf && _bloom.Count == bloomLevels
        && OutputWidth == outputWidth && OutputHeight == outputHeight;

    public bool Create(int width, int height, int samples, bool aoHalf, int bloomLevels, int outputWidth, int outputHeight)
    {
        Dispose();
        Width = global::System.Math.Max(1, width);
        Height = global::System.Math.Max(1, height);
        Samples = global::System.Math.Max(1, samples);
        AoHalf = aoHalf;
        OutputWidth = global::System.Math.Max(1, outputWidth);
        OutputHeight = global::System.Math.Max(1, outputHeight);
        long bytes = 0;

        // Single-sample HDR colour and depth-stencil (sampled by bloom, composite, ambient occlusion, depth copy).
        _hdrTexture = GlResources.Texture2D(PixelInternalFormat.Rgba16f, Width, Height, PixelFormat.Rgba, PixelType.HalfFloat);
        _depthTexture = GlResources.DepthTexture2D(Width, Height, stencil: true, compare: false);
        bytes += (long)Width * Height * (8 + 4);

        _sceneFbo = GlResources.Framebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        if (Samples > 1)
        {
            _sceneColorRb = GlResources.Renderbuffer(Samples, RenderbufferStorage.Rgba16f, Width, Height);
            _sceneDepthRb = GlResources.Renderbuffer(Samples, RenderbufferStorage.Depth24Stencil8, Width, Height);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _sceneColorRb);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _sceneDepthRb);
            bytes += (long)Width * Height * Samples * (8 + 4);
        }
        else
        {
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _hdrTexture, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, TextureTarget.Texture2D, _depthTexture, 0);
        }

        var ok = GlResources.Complete("scene");
        if (Samples > 1)
        {
            _resolveFbo = GlResources.Framebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _hdrTexture, 0);
            ok &= GlResources.Complete("resolve");
            _depthResolveFbo = GlResources.Framebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _depthResolveFbo);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, TextureTarget.Texture2D, _depthTexture, 0);
            ok &= GlResources.Complete("depth resolve");
        }

        // Ambient occlusion (R8; half or full scene resolution) and its blurred copy at full resolution (edge-aware
        // upsampling: the surfaces read it pixel for pixel, so it never bleeds across silhouettes).
        AoWidth = aoHalf ? global::System.Math.Max(1, Width / 2) : Width;
        AoHeight = aoHalf ? global::System.Math.Max(1, Height / 2) : Height;
        _aoTexture = GlResources.Texture2D(PixelInternalFormat.R8, AoWidth, AoHeight, PixelFormat.Red, PixelType.UnsignedByte);
        _aoBlurTexture = GlResources.Texture2D(PixelInternalFormat.R8, Width, Height, PixelFormat.Red, PixelType.UnsignedByte);
        _aoFbo = ColorFbo(_aoTexture, "ambient occlusion", ref ok);
        _aoBlurFbo = ColorFbo(_aoBlurTexture, "ambient occlusion blur", ref ok);
        bytes += (long)AoWidth * AoHeight + (long)Width * Height;

        // Bloom chain: half resolution, then halving per level (R11F_G11F_B10F).
        var w = global::System.Math.Max(1, Width / 2);
        var h = global::System.Math.Max(1, Height / 2);
        for (var level = 0; level < bloomLevels; level++)
        {
            var texture = GlResources.Texture2D(PixelInternalFormat.R11fG11fB10f, w, h, PixelFormat.Rgb, PixelType.Float);
            _bloom.Add((ColorFbo(texture, $"bloom {level}", ref ok), texture, w, h));
            bytes += (long)w * h * 4;
            w = global::System.Math.Max(1, w / 2);
            h = global::System.Math.Max(1, h / 2);
        }

        // 8-bit image of the output size (FXAA reads it).
        _ldrTexture = GlResources.Texture2D(PixelInternalFormat.Rgba8, OutputWidth, OutputHeight, PixelFormat.Rgba, PixelType.UnsignedByte);
        _ldrFbo = ColorFbo(_ldrTexture, "fxaa input", ref ok);
        bytes += (long)OutputWidth * OutputHeight * 4;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Bytes = bytes;
        Valid = ok;
        if (!ok)
        {
            Dispose();
        }

        return ok;
    }

    /// <summary>Copies the multisampled scene colour (and depth when <paramref name="depth"/>) to the single-sample images.</summary>
    public void Resolve(bool color, bool depth)
    {
        if (Samples <= 1)
        {
            return;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _sceneFbo);
        if (color)
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
            GL.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        }

        if (depth)
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _depthResolveFbo);
            GL.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
    }

    private static int ColorFbo(int texture, string label, ref bool ok)
    {
        var fbo = GlResources.Framebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
        ok &= GlResources.Complete(label);
        return fbo;
    }

    public void Dispose()
    {
        GlResources.DeleteFramebuffer(ref _sceneFbo);
        GlResources.DeleteRenderbuffer(ref _sceneColorRb);
        GlResources.DeleteRenderbuffer(ref _sceneDepthRb);
        GlResources.DeleteFramebuffer(ref _resolveFbo);
        GlResources.DeleteFramebuffer(ref _depthResolveFbo);
        GlResources.DeleteTexture(ref _hdrTexture);
        GlResources.DeleteTexture(ref _depthTexture);
        GlResources.DeleteFramebuffer(ref _aoFbo);
        GlResources.DeleteFramebuffer(ref _aoBlurFbo);
        GlResources.DeleteTexture(ref _aoTexture);
        GlResources.DeleteTexture(ref _aoBlurTexture);
        GlResources.DeleteFramebuffer(ref _ldrFbo);
        GlResources.DeleteTexture(ref _ldrTexture);
        for (var i = 0; i < _bloom.Count; i++)
        {
            var (fbo, texture, _, _) = _bloom[i];
            GlResources.DeleteFramebuffer(ref fbo);
            GlResources.DeleteTexture(ref texture);
        }

        _bloom.Clear();
        Valid = false;
        Bytes = 0;
    }
}
