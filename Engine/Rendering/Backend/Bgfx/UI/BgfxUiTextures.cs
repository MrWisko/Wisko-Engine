using Wisko.Engine.Text;
using Wisko.Engine.UI;
using BgfxNative = global::Bgfx.bgfx;

namespace Wisko.Engine.Rendering.Backend.Bgfx.UI;

/// <summary>R8 glyph atlas as a bgfx texture; glyph blocks are copied into bgfx memory on upload.</summary>
internal sealed unsafe class BgfxGlyphAtlasSurface : IGlyphAtlasSurface
{
    private BgfxNative.TextureHandle _texture = new() { idx = ushort.MaxValue };

    public int Handle => _texture.idx;

    public void Create(int size)
    {
        // Created without initial memory: bgfx makes textures created *with* memory immutable, and the
        // atlas is updated glyph by glyph. Then cleared to zero so bilinear sampling of the padding
        // gutters never reads garbage.
        _texture = BgfxNative.create_texture_2d(
            (ushort)size, (ushort)size, false, 1, BgfxNative.TextureFormat.R8,
            BgfxUiBackend.TextureSamplerFlags, null, 0);
        if (!_texture.Valid)
        {
            throw new InvalidOperationException("bgfx glyph atlas texture could not be created.");
        }

        Upload(0, 0, size, size, new byte[size * size]);
    }

    public void Upload(int x, int y, int width, int height, byte[] coverage)
    {
        fixed (byte* data = coverage)
        {
            BgfxNative.update_texture_2d(
                _texture, 0, 0, (ushort)x, (ushort)y, (ushort)width, (ushort)height,
                BgfxNative.copy(data, (uint)(width * height)), (ushort)width);
        }
    }

    public void Dispose()
    {
        if (_texture.Valid)
        {
            BgfxNative.destroy_texture(_texture);
            _texture = new BgfxNative.TextureHandle { idx = ushort.MaxValue };
        }
    }
}

/// <summary>RGBA8 bgfx textures for project previews (owned and destroyed via TextureFileCache).</summary>
internal sealed unsafe class BgfxUiTextureFactory : IUiTextureFactory
{
    public int LiveTextures { get; private set; }

    public UiTexture Create(byte[] rgba, int width, int height)
    {
        BgfxNative.TextureHandle handle;
        fixed (byte* data = rgba)
        {
            handle = BgfxNative.create_texture_2d(
                (ushort)width, (ushort)height, false, 1, BgfxNative.TextureFormat.RGBA8,
                BgfxUiBackend.TextureSamplerFlags, BgfxNative.copy(data, (uint)(width * height * 4)), 0);
        }

        if (!handle.Valid)
        {
            throw new InvalidOperationException($"bgfx image texture {width}x{height} could not be created.");
        }

        LiveTextures++;
        return new UiTexture(handle.idx);
    }

    public void Destroy(UiTexture texture)
    {
        BgfxNative.destroy_texture(new BgfxNative.TextureHandle { idx = (ushort)texture.Id });
        LiveTextures--;
    }
}

/// <summary>Loads the precompiled Metal shaders embedded in this assembly.</summary>
internal static unsafe class BgfxShaders
{
    public static BgfxNative.ShaderHandle Load(string name)
    {
        var resource = $"Engine.Bgfx.Shaders.metal.{name}.bin";
        using var stream = typeof(BgfxShaders).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded bgfx shader not found: {resource}");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        // Same convention as bgfx's examples: bgfx-owned copy plus a trailing NUL.
        var memory = BgfxNative.alloc((uint)bytes.Length + 1);
        fixed (byte* source = bytes)
        {
            Buffer.MemoryCopy(source, memory->data, bytes.Length, bytes.Length);
        }

        memory->data[bytes.Length] = 0;
        var handle = BgfxNative.create_shader(memory);
        if (!handle.Valid)
        {
            throw new InvalidOperationException($"bgfx shader {name} could not be created.");
        }

        return handle;
    }
}
