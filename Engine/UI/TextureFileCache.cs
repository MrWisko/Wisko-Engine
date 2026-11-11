using Wisko.Engine.Projects;

namespace Wisko.Engine.UI;

/// <summary>
/// One backend texture per image file path, reloaded only when the file's write time or length changes.
/// Missing or undecodable files simply have no texture (callers fall back). Every texture that is
/// replaced, dropped or disposed is destroyed through the factory, so nothing leaks across hub
/// refreshes. File tracking and decoding live here; the factory owns the GPU side (GL or bgfx).
/// </summary>
internal sealed class TextureFileCache(IUiTextureFactory textures) : IUiTextureCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Refresh(IEnumerable<string> paths)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            wanted.Add(path);
            FileStamp? stamp;
            try
            {
                var info = new FileInfo(path);
                stamp = info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
            }
            catch
            {
                stamp = null;
            }

            _entries.TryGetValue(path, out var cached);
            if (cached is not null && cached.Stamp == stamp)
            {
                continue; // unchanged (including "still missing" / "still corrupt")
            }

            if (cached?.Texture is UiTexture oldTexture)
            {
                textures.Destroy(oldTexture);
            }

            _entries[path] = new Entry(stamp, stamp is null ? null : TryLoad(path));
        }

        foreach (var path in _entries.Keys.Where(path => !wanted.Contains(path)).ToList())
        {
            if (_entries[path].Texture is UiTexture texture)
            {
                textures.Destroy(texture);
            }

            _entries.Remove(path);
        }
    }

    public bool TryGet(string path, out UiTexture texture)
    {
        texture = default;
        if (_entries.TryGetValue(path, out var entry) && entry.Texture is UiTexture loaded)
        {
            texture = loaded;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Texture is UiTexture texture)
            {
                textures.Destroy(texture);
            }
        }

        _entries.Clear();
    }

    private UiTexture? TryLoad(string path)
    {
        try
        {
            if (CustomThumbnailImage.TryDecode(File.ReadAllBytes(path), out var rgba, out var width, out var height))
            {
                return textures.Create(rgba, width, height);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectHub] Could not read {path}: {ex.Message}");
            return null;
        }

        Console.WriteLine($"[ProjectHub] Could not decode {path}; using fallback");
        return null;
    }

    private sealed record FileStamp(DateTime WriteTimeUtc, long Length);

    private sealed record Entry(FileStamp? Stamp, UiTexture? Texture);
}
