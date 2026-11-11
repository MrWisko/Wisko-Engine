using System.Diagnostics.CodeAnalysis;
using Wisko.Engine.Assets;

namespace Wisko.Engine.World;

/// <summary>
/// A Lua script attached to a scene object, by project-relative path inside <c>Assets/Scripts</c>
/// (e.g. <c>Assets/Scripts/Spin.lua</c>). Only created through validation, so it never holds an absolute or
/// escaping path. Attaching, duplicating or deleting objects never touches the script file itself.
/// </summary>
public sealed record ScriptReference
{
    private ScriptReference(string assetPath)
    {
        AssetPath = assetPath;
    }

    /// <summary>Normalized project-relative path with forward slashes (what Scene.json stores).</summary>
    public string AssetPath { get; }

    public static bool TryCreate(string? assetPath, [NotNullWhen(true)] out ScriptReference? reference)
    {
        reference = ProjectAssetPaths.TryNormalizeScriptPath(assetPath, out var normalized)
            ? new ScriptReference(normalized)
            : null;
        return reference is not null;
    }

    public override string ToString() => AssetPath;
}
