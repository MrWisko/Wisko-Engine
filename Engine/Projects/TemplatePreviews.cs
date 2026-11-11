using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StbImageSharp;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.World;

namespace Wisko.Engine.Projects;

/// <summary>One template's entry in the previews' manifest: what the pictures were rendered from, and how.</summary>
internal sealed record TemplatePreviewRecord(string Content, string Environment, float[] CameraPosition, float CameraYaw, float CameraPitch,
    float CameraFieldOfView);

/// <summary>previews.json: written by the generator beside the pictures, embedded with them.</summary>
internal sealed record TemplatePreviewManifest(int Version, int Width, int Height, int[] Densities, Dictionary<string, TemplatePreviewRecord> Templates,
    string Command);

/// <summary>
/// The New World screen's template pictures: rendered by Wisko itself from each template's real starting world (the
/// Universal renderer at a new world's defaults, a fixed camera) by
/// <c>dotnet run --project Tests/Wisko.Tests -- --make-template-previews</c>, at 1x, 2x and 3x of
/// <see cref="Width"/> x <see cref="Height"/> so they stay sharp at every display scale, and embedded in the engine
/// (Assets/TemplatePreviews). Nothing is fetched at run time. The manifest keeps a fingerprint of what each picture
/// shows; the tests compare it with the templates as they are now, so a changed template without new pictures fails.
/// </summary>
internal static class TemplatePreviews
{
    /// <summary>The picture's size on screen in logical pixels (16:9).</summary>
    public const int Width = 320;

    public const int Height = 180;

    public const string ManifestFileName = "previews.json";

    public static readonly int[] Densities = [1, 2, 3];

    public static readonly WorldTemplate[] Templates = [WorldTemplate.Classic, WorldTemplate.Testing];

    private const string ResourcePrefix = "Engine.TemplatePreviews.";

    public static string FileName(WorldTemplate template, int density) => $"{template}@{density}x.png";

    /// <summary>
    /// The density to draw for a display scale: the smallest picture at least as large as the screen needs (so it is only
    /// ever drawn at its size or smaller, never stretched), the largest above 3x.
    /// </summary>
    public static int DensityFor(float dpiScale)
    {
        foreach (var density in Densities)
        {
            if (dpiScale <= density + 0.01f)
            {
                return density;
            }
        }

        return Densities[^1];
    }

    /// <summary>The embedded picture decoded to RGBA (top row first), or false when it is missing or unreadable.</summary>
    public static bool TryLoad(WorldTemplate template, int density, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = height = 0;
        try
        {
            using var stream = typeof(TemplatePreviews).Assembly.GetManifestResourceStream(ResourcePrefix + FileName(template, density));
            if (stream is null)
            {
                return false;
            }

            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            rgba = image.Data;
            width = image.Width;
            height = image.Height;
            return width > 0 && height > 0 && rgba.Length == width * height * 4;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine($"[Hub] Template preview {FileName(template, density)} unreadable: {ex.Message}");
            return false;
        }
    }

    public static TemplatePreviewManifest? LoadManifest()
    {
        using var stream = typeof(TemplatePreviews).Assembly.GetManifestResourceStream(ResourcePrefix + ManifestFileName);
        return stream is null ? null : JsonSerializer.Deserialize<TemplatePreviewManifest>(stream);
    }

    /// <summary>
    /// What a template starts with: its Parts (name, shape, place, size, rotation, colour, anchoring, material, light)
    /// and the Atmosphere and rendering settings every new world gets.
    /// </summary>
    public static string ContentFingerprint(IReadOnlyList<Part> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(part.Name).Append('|').Append(part.ShapeType).Append('|').Append(Vector(part.Position)).Append('|')
                .Append(Vector(part.Size)).Append('|').Append(Vector(part.Rotation)).Append('|').Append(Vector(part.Color)).Append('|')
                .Append(part.Anchored).Append('|').Append(part.Model?.AssetPath ?? "-").Append('|')
                .Append(part.Material is { } material ? JsonSerializer.Serialize(material) : "-").Append('|')
                .Append(part.Light is { } light ? JsonSerializer.Serialize(light) : "-").Append('\n');
        }

        text.Append(JsonSerializer.Serialize(AtmosphereSettings.Default)).Append('\n');
        text.Append(JsonSerializer.Serialize(GraphicsSettings.Default)).Append('\n');
        return Hash(text.ToString());
    }

    /// <summary>The environment the host draws for every world (the baseplate): each object's name, centre and size.</summary>
    public static string EnvironmentFingerprint(IReadOnlyList<PhysicsEnvironmentBox> environment) =>
        Hash(string.Concat(environment.Select(box => $"{box.Name}|{Vector(box.Center)}|{Vector(box.Size)}\n")));

    private static string Vector(Vector3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X:R},{v.Y:R},{v.Z:R}");

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
