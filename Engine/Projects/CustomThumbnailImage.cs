using Wisko.Engine.Rendering;
using StbImageSharp;

namespace Wisko.Engine.Projects;

/// <summary>
/// Turns a user-chosen PNG/JPEG into a custom card preview: decode, apply JPEG EXIF orientation
/// (phone photos), center-crop to the preview aspect ratio, resample. The source file is only read.
/// </summary>
internal static class CustomThumbnailImage
{
    private const long MaxSourceBytes = 64L * 1024 * 1024;

    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg"];

    public static bool TryCreate(string sourcePath, int width, int height, out byte[] rgba, out string? error)
    {
        rgba = [];
        error = null;

        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            error = "Choose a PNG or JPEG image";
            return false;
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                error = "Image file not found";
                return false;
            }

            if (info.Length > MaxSourceBytes)
            {
                error = "Image is too large (max 64 MB)";
                return false;
            }

            bytes = File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex)
        {
            error = $"Could not read image: {ex.Message}";
            return false;
        }

        if (!TryDecode(bytes, out var pixels, out var imageWidth, out var imageHeight))
        {
            error = "Could not decode that image";
            return false;
        }

        var orientation = ReadJpegExifOrientation(bytes);
        if (orientation is >= 2 and <= 8)
        {
            (pixels, imageWidth, imageHeight) = ApplyOrientation(pixels, imageWidth, imageHeight, orientation);
        }

        rgba = ImageResample.CropToAspectAndResize(pixels, imageWidth, imageHeight, width, height);
        return true;
    }

    /// <summary>Decodes PNG/JPEG bytes to top-down RGBA; false (never throws) for corrupt data.</summary>
    public static bool TryDecode(byte[] bytes, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = 0;
        height = 0;
        try
        {
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            if (image is null || image.Width <= 0 || image.Height <= 0 || image.Data.Length < image.Width * image.Height * 4)
            {
                return false;
            }

            rgba = image.Data;
            width = image.Width;
            height = image.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // EXIF orientation (tag 0x0112) from a JPEG's APP1 segment; 1 (as stored) when absent or unreadable.
    private static int ReadJpegExifOrientation(byte[] data)
    {
        try
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            {
                return 1;
            }

            var pos = 2;
            while (pos + 4 <= data.Length && data[pos] == 0xFF)
            {
                var marker = data[pos + 1];
                if (marker is 0xD9 or 0xDA)
                {
                    break; // end of image / start of scan: no EXIF before pixel data
                }

                var length = (data[pos + 2] << 8) | data[pos + 3];
                var segment = pos + 4;
                if (marker == 0xE1 && length >= 8 && segment + 6 <= data.Length
                    && data[segment] == 'E' && data[segment + 1] == 'x' && data[segment + 2] == 'i' && data[segment + 3] == 'f')
                {
                    return ReadTiffOrientation(data, segment + 6, pos + 2 + length);
                }

                pos += 2 + length;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // Malformed EXIF: treat as unrotated.
        }

        return 1;
    }

    private static int ReadTiffOrientation(byte[] data, int tiff, int end)
    {
        if (tiff + 8 > end)
        {
            return 1;
        }

        var littleEndian = data[tiff] == 'I' && data[tiff + 1] == 'I';
        int U16(int at) => littleEndian ? data[at] | (data[at + 1] << 8) : (data[at] << 8) | data[at + 1];
        int U32(int at) => littleEndian
            ? data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24)
            : (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

        var ifd = tiff + U32(tiff + 4);
        if (ifd + 2 > end || ifd < tiff)
        {
            return 1;
        }

        var count = U16(ifd);
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + i * 12;
            if (entry + 12 > end)
            {
                break;
            }

            if (U16(entry) == 0x0112)
            {
                return U16(entry + 8);
            }
        }

        return 1;
    }

    // Maps each output pixel back to its stored source pixel for EXIF orientations 2-8.
    private static (byte[] Pixels, int Width, int Height) ApplyOrientation(byte[] source, int width, int height, int orientation)
    {
        var swap = orientation >= 5;
        var outWidth = swap ? height : width;
        var outHeight = swap ? width : height;
        var result = new byte[outWidth * outHeight * 4];

        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (y, height - 1 - x),
                    7 => (width - 1 - y, height - 1 - x),
                    _ => (width - 1 - y, x) // 8
                };
                Buffer.BlockCopy(source, (sy * width + sx) * 4, result, (y * outWidth + x) * 4, 4);
            }
        }

        return (result, outWidth, outHeight);
    }
}
