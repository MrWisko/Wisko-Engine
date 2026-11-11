using System.Text;
using System.Text.Json;

namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// Reads the "extensionsUsed" list of a .gltf or .glb file (its JSON only; no buffers or images) so that features
/// Wisko does not render are reported at import instead of silently looking different. Never throws.
/// </summary>
internal static class GltfExtensions
{
    /// <summary>Extensions Wisko interprets (Assimp decodes the rest of the file).</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "KHR_materials_emissive_strength",
        "KHR_materials_unlit"
    };

    /// <summary>What happens to a model that uses an extension Wisko does not support.</summary>
    public static string Effect(string extension) => extension switch
    {
        "KHR_texture_transform" => "texture offsets, rotations and scales are ignored",
        "KHR_lights_punctual" => "the file's lights are not imported (add Wisko lights instead)",
        "KHR_texture_basisu" or "EXT_texture_webp" or "MSFT_texture_dds" => "textures in that format are not shown",
        "KHR_draco_mesh_compression" or "EXT_meshopt_compression" => "compressed meshes may not load",
        "KHR_materials_pbrSpecularGlossiness" => "specular-glossiness materials are shown with approximate metallic-roughness",
        _ when extension.StartsWith("KHR_materials_", StringComparison.Ordinal) => "that material feature is not shown",
        _ => "not supported"
    };

    public static IReadOnlyList<string> ReadUsed(string path)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            byte[]? json = extension switch
            {
                ".gltf" => File.ReadAllBytes(path),
                ".glb" => ReadGlbJson(path),
                _ => null
            };
            if (json is null)
            {
                return [];
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("extensionsUsed", out var used) || used.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return used.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Distinct().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return [];
        }
    }

    // GLB: 12-byte header ("glTF", version, length), then chunk 0 = JSON (length, type "JSON", data).
    private static byte[]? ReadGlbJson(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[20];
        if (stream.Read(header) != 20 || Encoding.ASCII.GetString(header[..4]) != "glTF" || Encoding.ASCII.GetString(header.Slice(16, 4)) != "JSON")
        {
            return null;
        }

        var length = BitConverter.ToUInt32(header.Slice(12, 4));
        if (length == 0 || length > 64 * 1024 * 1024 || length > stream.Length - 20)
        {
            return null;
        }

        var json = new byte[length];
        stream.ReadExactly(json);
        return json;
    }
}
