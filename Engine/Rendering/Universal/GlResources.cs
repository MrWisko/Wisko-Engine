using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// OpenGL render-target objects with live counts, so leak checks can see that targets, shadow maps and post-processing
/// buffers are released when a project closes, the window resizes or the renderer shuts down.
/// </summary>
internal static class GlResources
{
    public static int LiveTextures { get; private set; }

    public static int LiveRenderbuffers { get; private set; }

    public static int LiveFramebuffers { get; private set; }

    public static int Texture2D(PixelInternalFormat internalFormat, int width, int height, PixelFormat format, PixelType type,
        TextureMinFilter min = TextureMinFilter.Linear, TextureMagFilter mag = TextureMagFilter.Linear, int levels = 1)
    {
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        var w = width;
        var h = height;
        for (var level = 0; level < levels; level++)
        {
            GL.TexImage2D(TextureTarget.Texture2D, level, internalFormat, w, h, 0, format, type, IntPtr.Zero);
            w = global::System.Math.Max(1, w / 2);
            h = global::System.Math.Max(1, h / 2);
        }

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)mag);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, levels - 1);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        LiveTextures++;
        return id;
    }

    /// <summary>A depth (or depth-stencil) texture usable as a shadow sampler (comparison on) or a plain sampler.</summary>
    public static int DepthTexture2D(int width, int height, bool stencil, bool compare)
    {
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        if (stencil)
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Depth24Stencil8, width, height, 0, PixelFormat.DepthStencil,
                PixelType.UnsignedInt248, IntPtr.Zero);
        }
        else
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, width, height, 0, PixelFormat.DepthComponent,
                PixelType.UnsignedInt, IntPtr.Zero);
        }

        var filter = compare ? (int)TextureMinFilter.Linear : (int)TextureMinFilter.Nearest;
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        if (compare)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        LiveTextures++;
        return id;
    }

    public static int Renderbuffer(int samples, RenderbufferStorage format, int width, int height)
    {
        var id = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, id);
        if (samples > 1)
        {
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, format, width, height);
        }
        else
        {
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, format, width, height);
        }

        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        LiveRenderbuffers++;
        return id;
    }

    public static int Framebuffer()
    {
        LiveFramebuffers++;
        return GL.GenFramebuffer();
    }

    /// <summary>Whether the bound draw framebuffer is complete (the reason logged once per label when not).</summary>
    public static bool Complete(string label)
    {
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status == FramebufferErrorCode.FramebufferComplete)
        {
            return true;
        }

        Console.WriteLine($"[Graphics] Render target '{label}' incomplete: {status}");
        return false;
    }

    public static void DeleteTexture(ref int id)
    {
        if (id != 0)
        {
            GL.DeleteTexture(id);
            id = 0;
            LiveTextures--;
        }
    }

    public static void DeleteRenderbuffer(ref int id)
    {
        if (id != 0)
        {
            GL.DeleteRenderbuffer(id);
            id = 0;
            LiveRenderbuffers--;
        }
    }

    public static void DeleteFramebuffer(ref int id)
    {
        if (id != 0)
        {
            GL.DeleteFramebuffer(id);
            id = 0;
            LiveFramebuffers--;
        }
    }
}
