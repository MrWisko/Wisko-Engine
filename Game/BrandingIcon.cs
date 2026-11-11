using OpenTK.Windowing.Common.Input;
using StbImageSharp;

namespace Wisko.Editor;

/// <summary>
/// Wisko-specific window-icon loading, kept in the editor host so the Engine library stays
/// brand-neutral. Decodes the four embedded branding PNGs to RGBA and builds a multi-size OpenTK
/// <see cref="WindowIcon"/>. (Visible on Windows/Linux; GLFW ignores window icons on macOS, where the
/// Dock icon requires a real .app bundle.)
/// </summary>
internal static class BrandingIcon
{
    private static readonly (string ResourceName, int Size)[] Sources =
    {
        ("Wisko.Branding.Icon16", 16),
        ("Wisko.Branding.Icon32", 32),
        ("Wisko.Branding.Icon64", 64),
        ("Wisko.Branding.Icon1024", 1024),
    };

    public static WindowIcon Load()
    {
        var assembly = typeof(BrandingIcon).Assembly;
        var images = new Image[Sources.Length];

        for (var i = 0; i < Sources.Length; i++)
        {
            var (resourceName, size) = Sources[i];

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded branding resource not found: {resourceName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            var decoded = ImageResult.FromMemory(buffer.ToArray(), ColorComponents.RedGreenBlueAlpha);

            if (decoded.Width != size || decoded.Height != size)
            {
                throw new InvalidOperationException(
                    $"{resourceName}: expected {size}x{size}, decoded {decoded.Width}x{decoded.Height}");
            }

            var expectedBytes = size * size * 4;
            if (decoded.Data.Length != expectedBytes)
            {
                throw new InvalidOperationException(
                    $"{resourceName}: RGBA buffer {decoded.Data.Length} != width*height*4 ({expectedBytes})");
            }

            Console.WriteLine(
                $"[BrandingIcon] {resourceName} decoded {decoded.Width}x{decoded.Height} RGBA buffer={decoded.Data.Length}");
            images[i] = new Image(decoded.Width, decoded.Height, decoded.Data);
        }

        var icon = new WindowIcon(images);
        Console.WriteLine($"[BrandingIcon] WindowIcon built from {images.Length} sizes (16/32/64/1024)");
        return icon;
    }
}
