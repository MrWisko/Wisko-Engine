using System.Buffers.Binary;
using System.IO.Compression;
using StbImageSharp;

namespace Wisko.BrandingAssets;

/// <summary>An 8-bit RGBA image with straight (not premultiplied) alpha, rows top to bottom.</summary>
internal sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "an image needs at least one pixel");
        }

        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
        {
            throw new ArgumentException("the pixel buffer does not match the size", nameof(pixels));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public int Index(int x, int y) => (y * Width + x) * 4;

    public byte Alpha(int x, int y) => Pixels[Index(x, y) + 3];

    public static RgbaImage Decode(byte[] encoded)
    {
        var result = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        return new RgbaImage(result.Width, result.Height, result.Data);
    }

    public static RgbaImage Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>
    /// A PNG of this image: 8-bit RGBA, no gamma or colour profile chunks (sRGB as stored), so the same pixels always
    /// give the same bytes.
    /// </summary>
    public byte[] EncodePng()
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: RGBA
        WriteChunk(output, "IHDR", header);

        // Each row: filter type 1 (Sub), the difference to the pixel on the left, which compresses smooth artwork well.
        var stride = Width * 4;
        var filtered = new byte[(stride + 1) * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = y * stride;
            var target = y * (stride + 1);
            filtered[target] = 1;
            for (var i = 0; i < stride; i++)
            {
                var left = i >= 4 ? Pixels[row + i - 4] : (byte)0;
                filtered[target + 1 + i] = (byte)(Pixels[row + i] - left);
            }
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(filtered);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public void SavePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, EncodePng());
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32.Append(Crc32.Append(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            return c;
        }).ToArray();

        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }
}
