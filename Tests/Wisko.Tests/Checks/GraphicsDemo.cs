using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// Graphics V1: the sample models (examples/graphics-demo/Models, Khronos glTF Sample Assets; see CREDITS.md there)
/// and the helpers that import them into a project the way the editor does.
/// </summary>
internal static class GraphicsDemo
{
    /// <summary>The sample models: folder (= file name without .glb) and what each shows.</summary>
    public static readonly (string Folder, string Shows)[] Models =
    [
        ("WaterBottle", "metal/roughness, normal, occlusion and emissive maps"),
        ("NormalTangentMirrorTest", "supplied tangents, mirrored normal maps"),
        ("AlphaBlendModeTest", "opaque, mask (cutoff) and blend materials"),
        ("EmissiveStrengthTest", "KHR_materials_emissive_strength"),
        ("TextureSettingsTest", "texture sampler and UV settings")
    ];

    /// <summary>examples/graphics-demo/Models in this checkout (found by walking up to Wisko.sln).</summary>
    public static string ModelsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var models = Path.Combine(dir.FullName, "examples", "graphics-demo", "Models");
            if (File.Exists(Path.Combine(dir.FullName, "Wisko.sln")) && Directory.Exists(models))
            {
                return models;
            }
        }

        throw new DirectoryNotFoundException("examples/graphics-demo/Models not found above " + AppContext.BaseDirectory);
    }

    public static string ModelFile(string folder) => Path.Combine(ModelsFolder(), folder, folder + ".glb");

    /// <summary>Imports the sample models into a project through the editor's import (copied into Assets/Models).</summary>
    public static Dictionary<string, ModelAsset> ImportAll(string projectRoot)
    {
        var assets = new Dictionary<string, ModelAsset>(StringComparer.Ordinal);
        using var decoder = new AssimpModelImporter();
        var importer = new ProjectModelImporter(decoder);
        foreach (var (folder, _) in Models)
        {
            if (!importer.TryImport(ModelFile(folder), projectRoot, out var import, out var error))
            {
                throw new InvalidOperationException($"could not import {folder}: {error}");
            }

            assets[folder] = import.Asset;
        }

        return assets;
    }

    /// <summary>A model instance standing on <paramref name="origin"/>, scaled uniformly and turned about Y.</summary>
    public static Part ModelPart(string name, ModelAsset asset, Vector3 origin, float scale, float yaw = 0f)
    {
        var part = new Part(name, Vector3.Zero, new Vector3(1f, 1f, 1f), ModelReference.DefaultTint, anchored: true)
        {
            Model = new ModelReference(asset.AssetPath, asset)
        };
        ModelTransform.SetInstanceTransform(part, origin, new Vector3(0f, yaw, 0f), new Vector3(scale, scale, scale));
        return part;
    }
}
