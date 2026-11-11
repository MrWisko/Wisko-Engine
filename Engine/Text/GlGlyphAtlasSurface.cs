using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Text;

/// <summary>GPU side of a glyph atlas: a texture the CPU packer uploads glyph coverage into.</summary>
internal interface IGlyphAtlasSurface : IDisposable
{
    /// <summary>Backend texture id (GL texture name or bgfx texture handle index).</summary>
    int Handle { get; }

    void Create(int size);

    /// <summary>Uploads a top-down, tightly packed single-channel coverage block at (x, y).</summary>
    void Upload(int x, int y, int width, int height, byte[] coverage);
}

/// <summary>
/// OpenGL R8 atlas texture (unpack alignment 1, clamp-to-edge, bilinear). Requires an active GL context.
/// </summary>
internal sealed class GlGlyphAtlasSurface : IGlyphAtlasSurface
{
    private int _texture;

    public int Handle => _texture;

    public void Create(int size)
    {
        _texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.R8, size, size, 0,
            PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Upload(int x, int y, int width, int height, byte[] coverage)
    {
        GL.BindTexture(TextureTarget.Texture2D, _texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage2D(
            TextureTarget.Texture2D, 0, x, y, width, height,
            PixelFormat.Red, PixelType.UnsignedByte, coverage);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (_texture != 0)
        {
            GL.DeleteTexture(_texture);
            _texture = 0;
        }
    }
}
