namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// Optional record of the files one Assimp import asks for (the model itself, .bin buffers, .mtl libraries, ...),
/// with an optional confinement root: when <see cref="AllowedRoot"/> is set, existing files outside it are refused
/// instead of opened. Lives only for a single import call and is never part of <see cref="ImportedModel"/>.
/// All recorded paths are full, normalized paths.
/// </summary>
internal sealed class ModelFileAccessLog
{
    private readonly List<string> _opened = [];
    private readonly List<string> _missing = [];
    private readonly List<string> _refused = [];

    public ModelFileAccessLog(string? allowedRoot = null)
    {
        AllowedRoot = allowedRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
    }

    public string? AllowedRoot { get; }

    /// <summary>Files that were opened, in first-open order, without duplicates.</summary>
    public IReadOnlyList<string> Opened => _opened;

    /// <summary>Requested files that do not exist (Assimp may tolerate some, e.g. an OBJ without its .mtl).</summary>
    public IReadOnlyList<string> Missing => _missing;

    /// <summary>Existing files that were not opened because they are outside <see cref="AllowedRoot"/>.</summary>
    public IReadOnlyList<string> Refused => _refused;

    internal bool IsAllowed(string fullPath) => AllowedRoot is null || ProjectAssetPaths.IsUnder(fullPath, AllowedRoot);

    internal void RecordOpened(string fullPath) => AddDistinct(_opened, fullPath);

    internal void RecordMissing(string requestedPath) => AddDistinct(_missing, requestedPath);

    internal void RecordRefused(string fullPath) => AddDistinct(_refused, fullPath);

    private static void AddDistinct(List<string> list, string path)
    {
        if (!list.Contains(path, StringComparer.Ordinal))
        {
            list.Add(path);
        }
    }
}
