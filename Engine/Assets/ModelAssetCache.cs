using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Assets;

/// <summary>
/// Model assets of the open project, keyed by normalized project-relative path: each asset file is decoded once and
/// the resulting <see cref="ModelAsset"/> is shared by every scene instance (instances only hold a reference and
/// their transform). CPU data only; the renderer keeps the matching GPU meshes per asset. Also the entry point for
/// importing a new model into the open project. Cleared on every project change; the Assimp decoder is created on
/// first use and released on <see cref="Dispose"/>.
/// </summary>
public sealed class ModelAssetCache : IDisposable
{
    private readonly Dictionary<string, ModelAsset?> _assets = new(StringComparer.Ordinal);
    private AssimpModelImporter? _decoder;

    /// <summary>Folder of the open project; null for an unnamed (in-memory) world, which cannot own assets.</summary>
    public string? ProjectRoot { get; private set; }

    /// <summary>Number of asset paths currently cached (including ones that failed to load).</summary>
    public int Count => _assets.Count;

    /// <summary>Switches to another project (or none) and drops every cached asset of the previous one.</summary>
    public void SetProject(string? projectRoot)
    {
        ProjectRoot = projectRoot is null ? null : Path.GetFullPath(projectRoot);
        _assets.Clear();
    }

    /// <summary>
    /// The asset for a persisted reference, decoded on first request. Null (with a logged reason) when the reference
    /// is invalid, the file is gone or cannot be decoded; a failure is remembered until the next project change.
    /// </summary>
    public ModelAsset? Load(string assetPath, out string? error)
    {
        error = null;
        if (ProjectRoot is null)
        {
            error = "no project is open";
            return null;
        }

        if (!ProjectAssetPaths.TryNormalize(assetPath, out var key)
            || !ProjectAssetPaths.TryResolve(ProjectRoot, key, out var fullPath))
        {
            error = "not a path inside the project's Assets folder";
            Log($"Rejected model reference '{assetPath}': {error}");
            return null;
        }

        if (_assets.TryGetValue(key, out var cached))
        {
            error = cached is null ? "could not be loaded" : null;
            return cached;
        }

        var asset = TryDecode(key, fullPath, out error);
        _assets[key] = asset;
        return asset;
    }

    /// <summary>Copies an external model into the open project (see <see cref="ProjectModelImporter"/>) and caches it.</summary>
    public bool TryImport(string sourcePath, out ProjectModelImport result, out string error)
    {
        result = null!;
        if (ProjectRoot is null)
        {
            error = "Name this project before importing assets.";
            return false;
        }

        if (!TryGetDecoder(out var decoder, out error))
        {
            return false;
        }

        if (!new ProjectModelImporter(decoder).TryImport(sourcePath, ProjectRoot, out result, out error))
        {
            return false;
        }

        _assets[result.Asset.AssetPath] = result.Asset;
        return true;
    }

    public void Dispose()
    {
        _assets.Clear();
        _decoder?.Dispose();
        _decoder = null;
    }

    private ModelAsset? TryDecode(string key, string fullPath, out string? error)
    {
        error = null;
        if (!File.Exists(fullPath))
        {
            error = "file not found";
        }
        else if (TryGetDecoder(out var decoder, out var decoderError))
        {
            try
            {
                // Project assets are self-contained: reads are confined to the project's Assets folder.
                var log = new ModelFileAccessLog(Path.Combine(ProjectRoot!, ProjectAssetPaths.AssetsFolder));
                var model = decoder.Import(fullPath, log);
                foreach (var refused in log.Refused)
                {
                    Log($"{key}: ignored a read outside the project ({refused})");
                }

                if (model.Bounds.IsEmpty)
                {
                    error = "contains no 3D geometry";
                }
                else
                {
                    var asset = new ModelAsset(key, model);
                    Log($"Loaded {key} ({model.Meshes.Count} mesh(es), {asset.TriangleCount} triangles"
                        + (asset.Animation.HasClips ? $", {asset.Animation.Clips.Count} animation(s), {asset.Animation.Skeleton.BoneCount} bone(s))" : ")"));
                    foreach (var note in asset.Animation.Notes)
                    {
                        Log($"{key}: animation note: {note}");
                    }

                    return asset;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        else
        {
            error = decoderError;
        }

        Log($"Could not load {key}: {error}");
        return null;
    }

    private bool TryGetDecoder(out AssimpModelImporter decoder, out string error)
    {
        error = string.Empty;
        if (_decoder is null && VisualCppRuntime.ModelImportBlocked)
        {
            // Windows without (a new enough) Microsoft Visual C++ runtime: Assimp64.dll is not even loaded.
            Log($"Assimp not loaded: {VisualCppRuntime.Problem}");
            decoder = null!;
            error = $"model import {VisualCppRuntime.ShortProblem}";
            return false;
        }

        try
        {
            decoder = _decoder ??= new AssimpModelImporter();
            return true;
        }
        catch (Exception ex)
        {
            // Native Assimp missing or unloadable on this machine.
            Log($"Assimp unavailable: {ex.Message}");
            decoder = null!;
            error = "Model import is not available on this system";
            return false;
        }
    }

    private static void Log(string message) => Console.WriteLine($"[ModelAssets] {message}");
}
