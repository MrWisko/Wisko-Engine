using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Graphics;
using Wisko.Engine.Rendering;
using Wisko.Engine.Rendering.Universal;

namespace Wisko.Tests.Checks;

/// <summary>
/// Graphics V1 import of the Khronos sample models (examples/graphics-demo/Models): what each file says about its
/// materials (alpha modes, double-sided flags, sampler wrap modes, emission strength, texture slots) and meshes
/// (supplied tangents) arrives in Wisko's imported model unchanged, survives the copy into a project, and an external
/// texture of an OBJ stays usable when its material is edited.
/// </summary>
internal static class GraphicsImportChecks
{
    public static void SampleModels(TestReport report, string dir)
    {
        using var importer = new AssimpModelImporter();
        var models = GraphicsDemo.Models.ToDictionary(m => m.Folder, m => importer.Import(GraphicsDemo.ModelFile(m.Folder)));
        foreach (var (folder, model) in models)
        {
            report.Check($"{folder}: imports without warnings", model.Warnings.Count == 0, string.Join(" | ", model.Warnings));
        }

        static ImportedMaterial Material(ImportedModel model, string name) => model.Materials.First(m => m.Name == name);

        var bottle = Material(models["WaterBottle"], "BottleMat");
        report.Check("WaterBottle: base colour, metallic-roughness, normal, occlusion and emissive textures all imported",
            bottle.BaseColorTexture is not null && bottle.MetallicRoughnessTexture is not null && bottle.NormalTexture is not null
            && bottle.OcclusionTexture is not null && bottle.EmissiveTexture is not null);
        report.Check("NormalTangentMirrorTest: the file's tangents are imported (not regenerated)",
            models["NormalTangentMirrorTest"].Meshes.All(m => m.Tangents is { } t && t.Length == m.Positions.Length * 4),
            string.Join(", ", models["NormalTangentMirrorTest"].Meshes.Select(m => m.Tangents?.Length ?? 0)));

        var alpha = models["AlphaBlendModeTest"];
        report.Check("AlphaBlendModeTest: OPAQUE, BLEND and MASK with cutoffs 0.25, 0.5 (default) and 0.75",
            Material(alpha, "MatOpaque").AlphaMode == ImportedAlphaMode.Opaque
            && Material(alpha, "MatBlend").AlphaMode == ImportedAlphaMode.Blend
            && Material(alpha, "MatCutoff25") is { AlphaMode: ImportedAlphaMode.Mask, AlphaCutoff: 0.25f }
            && Material(alpha, "MatCutoffDefault") is { AlphaMode: ImportedAlphaMode.Mask, AlphaCutoff: 0.5f }
            && Material(alpha, "MatCutoff75") is { AlphaMode: ImportedAlphaMode.Mask, AlphaCutoff: 0.75f });

        var emissive = models["EmissiveStrengthTest"];
        report.Check("EmissiveStrengthTest: KHR_materials_emissive_strength 1, 2, 4, 8 and 16",
            new[] { 1f, 2f, 4f, 8f, 16f }.All(s => Material(emissive, $"Emit{s:0}").EmissiveStrength == s));

        var settings = models["TextureSettingsTest"];
        report.Check("TextureSettingsTest: doubleSided false / true",
            !Material(settings, "SingleSidedMaterial").DoubleSided && Material(settings, "DoubleSidedMaterial").DoubleSided);
        static string Wrap(ImportedMaterial m) => $"{m.BaseColorTexture?.WrapU}/{m.BaseColorTexture?.WrapV}";
        report.Check("TextureSettingsTest: sampler wrap modes (clamp, repeat, mirrored repeat) per axis",
            Wrap(Material(settings, "TextureClampMaterialS")) == "Clamp/Repeat"
            && Wrap(Material(settings, "TextureClampMaterialT")) == "Repeat/Clamp"
            && Wrap(Material(settings, "TextureMirrorMaterialS")) == "Mirror/Repeat"
            && Wrap(Material(settings, "TextureMirrorMaterialT")) == "Repeat/Mirror"
            && Wrap(Material(settings, "TextureRepeatMaterialS")) == "Repeat/Clamp",
            string.Join(", ", settings.Materials.Where(m => m.BaseColorTexture is not null).Select(m => $"{m.Name} {Wrap(m)}")));

        // Through the editor's import (copied into the project): the wrap modes come along.
        var project = Path.Combine(dir, "wrap-project");
        Directory.CreateDirectory(project);
        var projectImporter = new ProjectModelImporter(importer);
        var imported = projectImporter.TryImport(GraphicsDemo.ModelFile("TextureSettingsTest"), project, out var copy, out var error);
        report.Check("TextureSettingsTest through the project import keeps its wrap modes",
            imported && Wrap(copy.Asset.Model.Materials.First(m => m.Name == "TextureMirrorMaterialS")) == "Mirror/Repeat", error);
        if (imported)
        {
            var mirror = copy.Asset.Model.Materials.ToList().FindIndex(m => m.Name == "TextureMirrorMaterialS");
            var surface = MaterialResolver.ImportedAsSurface(copy.Asset, mirror, project);
            report.Check("Editing an imported glTF material starts from its look (the file's own image as @model:N)",
                surface.BaseColorTexture is { } path && path.StartsWith(SurfaceMaterial.ModelTexturePrefix, StringComparison.Ordinal),
                surface.BaseColorTexture);
        }

        ExternalTexture(report, dir, importer);
    }

    // An OBJ whose MTL names an external PNG: after the project import, editing its material keeps the texture as a
    // project-relative path (before Graphics V1 there was no way to refer to it).
    private static void ExternalTexture(TestReport report, string dir, AssimpModelImporter importer)
    {
        var source = Path.Combine(dir, "obj-source");
        Directory.CreateDirectory(Path.Combine(source, "textures"));
        File.WriteAllText(Path.Combine(source, "crate.obj"),
            "mtllib crate.mtl\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nusemtl Wood\n"
            + "f 1/1/1 2/2/1 3/3/1\nf 1/1/1 3/3/1 4/4/1\n");
        File.WriteAllText(Path.Combine(source, "crate.mtl"), "newmtl Wood\nKd 1 1 1\nmap_Kd textures/wood.png\n");
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 150; pixels[i + 1] = 100; pixels[i + 2] = 50; pixels[i + 3] = 255;
        }

        File.WriteAllBytes(Path.Combine(source, "textures", "wood.png"), PngEncoder.EncodeRgba(pixels, 4, 4));
        var project = Path.Combine(dir, "obj-project");
        Directory.CreateDirectory(project);
        var ok = new ProjectModelImporter(importer).TryImport(Path.Combine(source, "crate.obj"), project, out var import, out var error);
        report.Check("OBJ with an external texture imports into the project (texture copied)", ok && import.MissingFiles.Count == 0, error);
        if (!ok)
        {
            return;
        }

        var wood = import.Asset.Model.Materials.ToList().FindIndex(m => m.Name == "Wood");
        var surface = MaterialResolver.ImportedAsSurface(import.Asset, wood, project);
        var expected = $"{import.AssetFolder}/textures/wood.png";
        report.Check("Editing the OBJ's material keeps its external texture (a project-relative path that loads)",
            surface.BaseColorTexture == expected && File.Exists(Path.Combine(project, expected.Replace('/', Path.DirectorySeparatorChar))),
            $"{surface.BaseColorTexture} (expected {expected})");
        report.Check("OBJ colours stay display colours and both sides draw (OBJ rarely says)",
            surface.DoubleSided && MathF.Abs(surface.BaseColor.X - 1f) < 1e-4f);
    }
}
