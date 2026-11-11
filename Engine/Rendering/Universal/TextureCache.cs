using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Assets;
using Wisko.Engine.Graphics;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// One OpenGL 2D texture: RGBA8 for data (normal, metallic-roughness, occlusion) or SRGB8_ALPHA8 for colour (base
/// colour, emissive; the GPU decodes sRGB to linear when sampling, so colour textures are never double-corrected).
/// Mipmapped, trilinear, anisotropic where the driver offers it, repeating.
/// </summary>
internal sealed class GlTexture : IDisposable
{
    private static float? _maxAnisotropy;

    private GlTexture(int id, int width, int height, long bytes)
    {
        Id = id;
        Width = width;
        Height = height;
        Bytes = bytes;
        LiveCount++;
        TotalBytes += bytes;
    }

    public int Id { get; private set; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>GPU memory estimate (all mip levels).</summary>
    public long Bytes { get; }

    internal static int LiveCount { get; private set; }

    internal static long TotalBytes { get; private set; }

    public static GlTexture Create(TextureImage image, bool srgb)
    {
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, srgb ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8,
            image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Pixels);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        var anisotropy = MaxAnisotropy();
        if (anisotropy > 1f)
        {
            // GL_TEXTURE_MAX_ANISOTROPY (EXT_texture_filter_anisotropic; core in 4.6).
            GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, MathF.Min(anisotropy, 8f));
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return new GlTexture(id, image.Width, image.Height, (long)image.Width * image.Height * 4 * 4 / 3);
    }

    /// <summary>A 1x1 texture of one colour (fallbacks: white, flat normal...).</summary>
    public static GlTexture Solid(byte r, byte g, byte b, byte a, bool srgb)
    {
        var image = TextureImage.FromRgba(1, 1, [r, g, b, a], out _)!;
        return Create(image, srgb);
    }

    internal static float MaxAnisotropy()
    {
        if (_maxAnisotropy is null)
        {
            var supported = false;
            GL.GetInteger(GetPName.NumExtensions, out var count);
            for (var i = 0; i < count && !supported; i++)
            {
                var name = GL.GetString(StringNameIndexed.Extensions, i);
                supported = name is "GL_EXT_texture_filter_anisotropic" or "GL_ARB_texture_filter_anisotropic";
            }

            var max = 1f;
            if (supported)
            {
                GL.GetFloat((GetPName)0x84FF, out max); // GL_MAX_TEXTURE_MAX_ANISOTROPY
            }

            _maxAnisotropy = max;
        }

        return _maxAnisotropy.Value;
    }

    public void Dispose()
    {
        if (Id != 0)
        {
            GL.DeleteTexture(Id);
            Id = 0;
            LiveCount--;
            TotalBytes -= Bytes;
        }
    }
}

/// <summary>
/// The Universal renderer's material textures: each image uploaded once per colour space and shared by every material
/// that uses it. Sources are project files ("Assets/Textures/Bricks.png", resolved inside the project folder only) and
/// images of a model asset (embedded in the file, or the files copied next to it at import). A texture that cannot
/// be loaded is remembered as failed (so it is not retried every frame) and reported once; materials then use the
/// neutral fallback for that slot. Textures no material used for <see cref="EvictAfterFrames"/> frames are released;
/// everything is released on <see cref="Clear"/> (project change, renderer shutdown).
/// </summary>
internal sealed class TextureCache : IDisposable
{
    public const int EvictAfterFrames = 900;

    private readonly Dictionary<(string Key, bool Srgb), Entry> _entries = new();
    private readonly List<string> _failures = [];
    private long _frame;

    /// <summary>Why textures could not be loaded (one line each, first occurrence), for the editor and logs.</summary>
    public IReadOnlyList<string> Failures => _failures;

    public int Count => _entries.Values.Count(e => e.Texture is not null);

    /// <summary>A project texture by its project-relative path (null: missing, invalid, outside the project or failed).</summary>
    public GlTexture? GetProjectFile(string? projectRoot, string relativePath, bool srgb)
    {
        if (projectRoot is null)
        {
            return Fail(("project:" + relativePath, srgb), relativePath, "no project folder (save the project first)");
        }

        var clean = SurfaceMaterial.CleanTexturePath(relativePath);
        string full;
        try
        {
            full = clean is null ? string.Empty : Path.GetFullPath(Path.Combine(projectRoot, clean));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = string.Empty;
        }

        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (clean is null || full.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(("project:" + relativePath, srgb), relativePath, "not a path inside the project");
        }

        return Get(("file:" + full, srgb), relativePath, () => (TextureImage.LoadFile(full, out var error), error));
    }

    /// <summary>An image stored inside a model file, by index.</summary>
    public GlTexture? GetEmbedded(ModelAsset asset, int index, bool srgb)
    {
        var label = $"{asset.AssetPath} image {index}";
        return Get(($"embedded:{asset.AssetPath}#{index}", srgb), label, () =>
        {
            var images = asset.Model.EmbeddedTextures;
            if (index < 0 || index >= images.Count)
            {
                return (null, "the model has no such image");
            }

            var image = images[index];
            string? error;
            var decoded = image.IsEncoded
                ? TextureImage.Decode(image.Data, out error)
                : TextureImage.FromRgba(image.Width, image.Height, image.Data, out error);
            return (decoded, error);
        });
    }

    /// <summary>An external image of a model (a file copied into the project next to it at import).</summary>
    public GlTexture? GetModelFile(string absolutePath, string label, bool srgb) =>
        Get(("file:" + absolutePath, srgb), label, () => (TextureImage.LoadFile(absolutePath, out var error), error));

    private GlTexture? Get((string Key, bool Srgb) key, string label, Func<(TextureImage? Image, string? Error)> load)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.LastUsed = _frame;
            return entry.Texture;
        }

        var (image, error) = load();
        if (image is null)
        {
            return Fail(key, label, error ?? "unknown error");
        }

        entry = new Entry { Texture = GlTexture.Create(image, key.Srgb), LastUsed = _frame };
        _entries[key] = entry;
        return entry.Texture;
    }

    private GlTexture? Fail((string Key, bool Srgb) key, string label, string reason)
    {
        if (!_entries.ContainsKey(key))
        {
            _entries[key] = new Entry { Texture = null, LastUsed = _frame };
            var line = $"Texture {label}: {reason}; showing the material without it";
            _failures.Add(line);
            Console.WriteLine($"[Graphics] {line}");
        }

        return null;
    }

    /// <summary>Advances the frame counter and releases textures nothing drew for a while (failures are kept).</summary>
    public void EndFrame()
    {
        _frame++;
        if (_frame % 120 != 0)
        {
            return;
        }

        List<(string, bool)>? stale = null;
        foreach (var (key, entry) in _entries)
        {
            if (entry.Texture is not null && _frame - entry.LastUsed > EvictAfterFrames)
            {
                (stale ??= []).Add(key);
            }
        }

        foreach (var key in stale ?? [])
        {
            _entries[key].Texture!.Dispose();
            _entries.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Texture?.Dispose();
        }

        _entries.Clear();
        _failures.Clear();
    }

    public void Dispose() => Clear();

    private sealed class Entry
    {
        public GlTexture? Texture { get; init; }

        public long LastUsed { get; set; }
    }
}
