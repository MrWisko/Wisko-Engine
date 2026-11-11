using System.Text;
using Wisko.Engine.Assets;

namespace Wisko.Tests.Checks;

/// <summary>Model import through the native Assimp library into a project's Assets/Models.</summary>
internal static class ModelImportChecks
{
    public static void Run(TestReport report, string dir)
    {
        // The source sits in a folder with a space and non-ASCII characters (a classic native-library path pitfall).
        var source = Path.Combine(dir, "Bron ë model");
        Directory.CreateDirectory(source);
        var obj = new StringBuilder();
        obj.AppendLine("mtllib cube.mtl");
        foreach (var v in new[] { "-1 -1 -1", "1 -1 -1", "1 1 -1", "-1 1 -1", "-1 -1 1", "1 -1 1", "1 1 1", "-1 1 1" })
        {
            obj.AppendLine("v " + v);
        }

        obj.AppendLine("usemtl Red");
        foreach (var f in new[] { "1 3 2", "1 4 3", "5 6 7", "5 7 8", "1 2 6", "1 6 5", "2 3 7", "2 7 6", "3 4 8", "3 8 7", "4 1 5", "4 5 8" })
        {
            obj.AppendLine("f " + f);
        }

        File.WriteAllText(Path.Combine(source, "cube.obj"), obj.ToString());
        File.WriteAllText(Path.Combine(source, "cube.mtl"), "newmtl Red\nKd 0.9 0.1 0.1\n");

        var root = Path.Combine(dir, "Proj");
        Directory.CreateDirectory(root);
        using var cache = new ModelAssetCache();
        cache.SetProject(root);
        var ok = cache.TryImport(Path.Combine(source, "cube.obj"), out var import, out var error);
        report.Check("Assimp: OBJ imported through the native library", ok, error);
        if (!ok)
        {
            return;
        }

        TestReport.Info($"imported {import.Asset.AssetPath} into {import.AssetFolder}, {import.Asset.TriangleCount} triangles, missing files: {import.MissingFiles.Count}");
        report.Check("Assimp: 12 triangles", import.Asset.TriangleCount == 12, $"{import.Asset.TriangleCount}");
        report.Check("Assimp: copied into Assets/Models with a project-relative reference",
            import.Asset.AssetPath.StartsWith("Assets/Models/", StringComparison.Ordinal)
            && File.Exists(Path.Combine(root, import.Asset.AssetPath.Replace('/', Path.DirectorySeparatorChar))));
        report.Check("Assimp: material file came along (no missing files)", import.MissingFiles.Count == 0);
        using var fresh = new ModelAssetCache();
        fresh.SetProject(root);
        var reloaded = fresh.Load(import.Asset.AssetPath, out error);
        report.Check("Assimp: re-loads from the project by asset path", reloaded is not null && reloaded.TriangleCount == 12, error);
    }
}
