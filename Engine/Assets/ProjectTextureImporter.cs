using Wisko.Engine.Graphics;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Assets;

/// <summary>
/// Brings an image into a project for a material texture, so the project stays self-contained: a file
/// already inside the project is used where it is; any other file is copied into Assets/Textures (an identical file
/// already there is reused, a different one with the same name gets a numbered name; nothing is ever overwritten).
/// The image must decode (PNG, JPEG, TGA or BMP). Never throws.
/// </summary>
public static class ProjectTextureImporter
{
    public const string Folder = "Assets/Textures";

    /// <summary>Supported extensions, without dots.</summary>
    public static IReadOnlyList<string> Extensions { get; } = ["png", "jpg", "jpeg", "tga", "bmp"];

    public static bool TryImport(string sourcePath, string projectRoot, out string relativePath, out string error)
    {
        relativePath = string.Empty;
        error = string.Empty;
        try
        {
            var source = Path.GetFullPath(sourcePath);
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!File.Exists(source))
            {
                error = "The file does not exist.";
                return false;
            }

            var extension = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
            if (!Extensions.Contains(extension))
            {
                error = "Not a supported image (PNG, JPEG, TGA or BMP).";
                return false;
            }

            if (TextureImage.LoadFile(source, out var decodeError) is null)
            {
                error = $"The image could not be read: {decodeError}";
                return false;
            }

            if (source.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (SurfaceMaterial.CleanTexturePath(Path.GetRelativePath(root, source)) is not { } inside)
                {
                    error = "The file's path inside the project cannot be used.";
                    return false;
                }

                relativePath = inside;
                return true;
            }

            var folder = Path.Combine(root, "Assets", "Textures");
            Directory.CreateDirectory(folder);
            var stem = SafeName(Path.GetFileNameWithoutExtension(source));
            var bytes = File.ReadAllBytes(source);
            for (var n = 1; n < 1000; n++)
            {
                var name = n == 1 ? $"{stem}.{extension}" : $"{stem}-{n}.{extension}";
                var target = Path.Combine(folder, name);
                if (File.Exists(target))
                {
                    if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                    {
                        relativePath = $"{Folder}/{name}";
                        return true;
                    }

                    continue;
                }

                try
                {
                    // Complete or not at all: an interrupted copy never leaves a broken image under the final name.
                    AtomicFile.WriteNew(target, stream => stream.Write(bytes));
                }
                catch (IOException) when (File.Exists(target))
                {
                    continue; // Taken in the meantime: the next name (nothing is overwritten).
                }

                relativePath = $"{Folder}/{name}";
                return true;
            }

            error = "Too many textures with that name.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    // A file name that is safe in every project path (letters, digits, '-', '_', ' ', '.'; others become '_').
    private static string SafeName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' ? c : '_').ToArray();
        var safe = new string(chars).Trim(' ', '.');
        return safe.Length == 0 ? "texture" : safe.Length > 80 ? safe[..80] : safe;
    }
}
