using System.Text;

namespace Wisko.Engine.Assets;

/// <summary>
/// Persistent asset references: relative to the project folder, forward slashes, and always inside the project's
/// Assets folder (e.g. <c>Assets/Models/Tree/Tree.glb</c>). Never an absolute machine path.
/// </summary>
public static class ProjectAssetPaths
{
    public const string AssetsFolder = "Assets";
    public const string ModelsFolder = "Assets/Models";
    public const string ScriptsFolder = "Assets/Scripts";
    public const string ScriptExtension = ".lua";

    private const int MaxSafeNameLength = 48;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Canonical form of a persisted reference: forward slashes, starts with "Assets/", not rooted, no empty, "." or
    /// ".." segments. Anything else is rejected so a scene file can never point outside its project.
    /// </summary>
    public static bool TryNormalize(string? assetPath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return false;
        }

        var path = assetPath.Trim().Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || Path.IsPathRooted(path))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Length < 2 || !string.Equals(segments[0], AssetsFolder, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// A persisted script reference: a normalized asset path (see <see cref="TryNormalize"/>) to a <c>.lua</c> file
    /// inside <c>Assets/Scripts</c>, e.g. <c>Assets/Scripts/Spin.lua</c>.
    /// </summary>
    public static bool TryNormalizeScriptPath(string? assetPath, out string normalized)
    {
        if (TryNormalize(assetPath, out var candidate)
            && candidate.StartsWith(ScriptsFolder + "/", StringComparison.Ordinal)
            && candidate.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase)
            && candidate.Length > ScriptsFolder.Length + 1 + ScriptExtension.Length
            && candidate[candidate.LastIndexOf('/') + 1] != '.')
        {
            normalized = candidate;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    /// <summary>Full path of a reference, provided it normalizes and resolves inside <c>&lt;project&gt;/Assets</c>.</summary>
    public static bool TryResolve(string projectRoot, string assetPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryNormalize(assetPath, out var normalized))
        {
            return false;
        }

        var root = Path.GetFullPath(projectRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(candidate, Path.Combine(root, AssetsFolder)))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>Persistent reference for a file inside the project folder.</summary>
    public static string ToAssetPath(string projectRoot, string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(fullPath))
            .Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Strict containment: <paramref name="fullPath"/> is strictly inside <paramref name="fullRoot"/> (both full paths;
    /// ordinal, separator-aware, so "/a/bc" is not inside "/a/b").
    /// </summary>
    public static bool IsUnder(string fullPath, string fullRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(fullRoot);
        var path = Path.TrimEndingDirectorySeparator(fullPath);
        if (root.EndsWith(Path.DirectorySeparatorChar))
        {
            return path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal);
        }

        return path.Length > root.Length
            && path.StartsWith(root, StringComparison.Ordinal)
            && path[root.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Portable, human-readable folder name from a file name: Unicode NFC (macOS hands out decomposed names), letters,
    /// digits, '-' and '_' kept, every other run of characters becomes one '-', capped in length; "Model" when nothing
    /// usable remains or the result is a reserved Windows device name.
    /// </summary>
    public static string ToSafeName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var name = builder.ToString().Trim('-', '_');
        if (name.Length > MaxSafeNameLength)
        {
            name = name[..MaxSafeNameLength].TrimEnd('-', '_');
        }

        return name.Length == 0 || ReservedNames.Contains(name) ? "Model" : name;
    }
}
