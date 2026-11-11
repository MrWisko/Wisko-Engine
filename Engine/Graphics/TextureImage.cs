using StbImageSharp;

namespace Wisko.Engine.Graphics;

/// <summary>
/// A decoded texture image: RGBA8, top row first (the convention Wisko's UVs use), with no renderer types. Decoding
/// never throws: a missing, unreadable, corrupt or oversized image gives null and a short reason, and the renderer
/// draws its neutral fallback instead (diagnosed once per texture).
/// <para>Formats: PNG, JPEG, BMP, TGA, GIF (first frame), PSD (composite), and the others StbImageSharp reads.</para>
/// </summary>
public sealed class TextureImage
{
    /// <summary>Largest width or height kept; larger images are halved until they fit.</summary>
    public const int MaxSize = 4096;

    /// <summary>Largest image file read (bytes).</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    private TextureImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGBA8, top row first.</summary>
    public byte[] Pixels { get; }

    /// <summary>Whether any pixel is not fully opaque.</summary>
    public bool HasTransparency
    {
        get
        {
            for (var i = 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 255)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static TextureImage? FromRgba(int width, int height, byte[] rgba, out string? error)
    {
        error = null;
        if (width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length)
        {
            error = "the image data does not match its size";
            return null;
        }

        return Fit(new TextureImage(width, height, rgba));
    }

    public static TextureImage? Decode(byte[] encoded, out string? error)
    {
        error = null;
        if (encoded.Length == 0)
        {
            error = "the image is empty";
            return null;
        }

        try
        {
            var image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
            if (image is null || image.Width <= 0 || image.Height <= 0 || image.Data is null
                || image.Data.Length != (long)image.Width * image.Height * 4)
            {
                error = "the image could not be decoded";
                return null;
            }

            return Fit(new TextureImage(image.Width, image.Height, image.Data));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException or NotSupportedException)
        {
            error = $"the image could not be decoded ({ex.Message})";
            return null;
        }
    }

    public static TextureImage? LoadFile(string path, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "the file does not exist";
                return null;
            }

            if (info.Length > MaxFileBytes)
            {
                error = $"the file is too large ({info.Length / (1024 * 1024)} MB)";
                return null;
            }

            return Decode(File.ReadAllBytes(path), out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"the file could not be read ({ex.Message})";
            return null;
        }
    }

    // Halves (2x2 box filter) until both sides fit MaxSize.
    private static TextureImage Fit(TextureImage image)
    {
        while (image.Width > MaxSize || image.Height > MaxSize)
        {
            var w = global::System.Math.Max(1, image.Width / 2);
            var h = global::System.Math.Max(1, image.Height / 2);
            var src = image.Pixels;
            var dst = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        var sum = 0;
                        for (var dy = 0; dy < 2; dy++)
                        {
                            for (var dx = 0; dx < 2; dx++)
                            {
                                var sx = global::System.Math.Min(x * 2 + dx, image.Width - 1);
                                var sy = global::System.Math.Min(y * 2 + dy, image.Height - 1);
                                sum += src[(sy * image.Width + sx) * 4 + c];
                            }
                        }

                        dst[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                    }
                }
            }

            image = new TextureImage(w, h, dst);
        }

        return image;
    }
}
