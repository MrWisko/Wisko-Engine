using System.Buffers.Binary;
using System.Text;

namespace Wisko.BrandingAssets;

/// <summary>Windows .ico and macOS .icns files: written with PNG images inside, and read back for validation.</summary>
internal static class IconFiles
{
    /// <summary>The sizes in Wisko's .ico: what Windows asks for at 100-200% scaling (taskbar, Explorer, shortcuts, title bar).</summary>
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>The .icns entries (type, pixel size): every size and @2x size a macOS app icon has, as iconutil writes them.</summary>
    public static readonly (string Type, int Size)[] IcnsEntries =
    [
        ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256),
        ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024)
    ];

    public static byte[] WriteIco(IReadOnlyList<RgbaImage> images)
    {
        var encoded = images.Select(image => image.EncodePng()).ToList();
        using var output = new MemoryStream();
        var header = new byte[6 + 16 * images.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1); // type 1: icon
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)images.Count);
        var offset = header.Length;
        for (var i = 0; i < images.Count; i++)
        {
            var entry = header.AsSpan(6 + 16 * i);
            entry[0] = (byte)(images[i].Width >= 256 ? 0 : images[i].Width); // 0 means 256
            entry[1] = (byte)(images[i].Height >= 256 ? 0 : images[i].Height);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);  // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], encoded[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            offset += encoded[i].Length;
        }

        output.Write(header);
        foreach (var png in encoded)
        {
            output.Write(png);
        }

        return output.ToArray();
    }

    /// <summary>Every image in an .ico (PNG or BMP entries), with the size its directory entry states.</summary>
    public static List<(int StatedSize, RgbaImage Image, bool Png)> ReadIco(byte[] data)
    {
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) != 1)
        {
            throw new InvalidDataException("not an icon file");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        var result = new List<(int, RgbaImage, bool)>();
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(6 + 16 * i, 16);
            var stated = entry[0] == 0 ? 256 : entry[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            var image = data.AsSpan(offset, length).ToArray();
            var png = image.Length > 8 && image[0] == 0x89 && image[1] == (byte)'P';
            result.Add((stated, png ? RgbaImage.Decode(image) : DecodeIcoBitmap(image), png));
        }

        return result;
    }

    public static byte[] WriteIcns(IReadOnlyDictionary<int, RgbaImage> bySize)
    {
        using var body = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var (type, size) in IcnsEntries)
        {
            var png = bySize[size].EncodePng();
            body.Write(Encoding.ASCII.GetBytes(type));
            BinaryPrimitives.WriteInt32BigEndian(length, png.Length + 8);
            body.Write(length);
            body.Write(png);
        }

        using var output = new MemoryStream();
        output.Write("icns"u8);
        BinaryPrimitives.WriteInt32BigEndian(length, (int)body.Length + 8);
        output.Write(length);
        body.WriteTo(output);
        return output.ToArray();
    }

    /// <summary>The .icns entries that hold PNG images (type, image); other entry kinds are listed with a null image.</summary>
    public static List<(string Type, RgbaImage? Image)> ReadIcns(byte[] data)
    {
        if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "icns")
        {
            throw new InvalidDataException("not an icns file");
        }

        var result = new List<(string, RgbaImage?)>();
        var offset = 8;
        var end = Math.Min(data.Length, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)));
        while (offset + 8 <= end)
        {
            var type = Encoding.ASCII.GetString(data, offset, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4));
            if (length < 8 || offset + length > end)
            {
                throw new InvalidDataException($"icns entry {type} has a bad length");
            }

            var payload = data.AsSpan(offset + 8, length - 8);
            var png = payload.Length > 8 && payload[0] == 0x89 && payload[1] == (byte)'P';
            result.Add((type, png ? RgbaImage.Decode(payload.ToArray()) : null));
            offset += length;
        }

        return result;
    }

    // A BMP-style icon entry: BITMAPINFOHEADER, 32-bit BGRA rows bottom-up (the height counts the AND mask too).
    private static RgbaImage DecodeIcoBitmap(byte[] data)
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)) / 2;
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        if (bits != 32)
        {
            throw new InvalidDataException($"icon bitmap with {bits} bits per pixel (only 32-bit is read)");
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data);
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = headerSize + ((height - 1 - y) * width + x) * 4;
                var d = image.Index(x, y);
                image.Pixels[d] = data[s + 2];
                image.Pixels[d + 1] = data[s + 1];
                image.Pixels[d + 2] = data[s];
                image.Pixels[d + 3] = data[s + 3];
            }
        }

        return image;
    }
}
