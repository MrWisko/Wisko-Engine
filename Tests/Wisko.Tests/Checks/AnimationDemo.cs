using System.Text;
using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// The Animation System V1 demonstration: real rigged models (examples/animation-demo/Models, with their licences) and
/// Lua scripts (examples/animation-demo/Scripts). <c>Wisko.Tests --make-anim-demo &lt;projects folder&gt;</c> writes it as
/// an ordinary project ("Animation Demo"), importing every model through the editor's own import pipeline; the checks
/// build the same scene headless.
/// </summary>
internal static class AnimationDemo
{
    public const string ProjectName = "Animation Demo";

    /// <summary>The demo's models: folder under examples/animation-demo/Models and file name.</summary>
    public static readonly (string Folder, string File)[] Models =
    [
        ("RobotExpressive", "RobotExpressive.glb"),
        ("Fox", "Fox.glb"),
        ("CesiumMan", "CesiumMan.glb"),
        ("RiggedSimple", "RiggedSimple.glb"),
        ("BoxAnimated", "BoxAnimated.glb"),
        ("Box", "Box.glb"),
        ("AnimationWithSkeletonFbx", "animation_with_skeleton.fbx")
    ];

    public static string ScriptsFolder() => Path.Combine(Path.GetDirectoryName(AnimationChecks.ModelsFolder())!, "Scripts");

    public static string Script(string name) => File.ReadAllText(Path.Combine(ScriptsFolder(), name + ".lua"));

    public static void CopyScripts(string projectRoot)
    {
        var target = Path.Combine(projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(ScriptsFolder(), "*.lua"))
        {
            File.WriteAllText(Path.Combine(target, Path.GetFileName(file)), File.ReadAllText(file), new UTF8Encoding(false));
        }
    }

    /// <summary>Creates the demo project in <paramref name="projectsRoot"/>; refuses when a project of that name exists.</summary>
    public static int Make(string projectsRoot)
    {
        var store = new ProjectStore(projectsRoot);
        if (store.ListProjects().Any(p => string.Equals(p.Name, ProjectName, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"refused: a project named \"{ProjectName}\" already exists in {projectsRoot} (nothing changed)");
            return 1;
        }

        if (!store.TryCreateProject(ProjectName, WorldTemplate.Classic, new Workspace(), out var project, out var error))
        {
            Console.Error.WriteLine($"could not create the project: {error}");
            return 1;
        }

        // Every model goes through the editor's import (copied into Assets/Models, decoded from the copy).
        var assets = new Dictionary<string, ModelAsset>(StringComparer.Ordinal);
        using (var decoder = new AssimpModelImporter())
        {
            var importer = new ProjectModelImporter(decoder);
            foreach (var (folder, file) in Models)
            {
                if (!importer.TryImport(AnimationChecks.ModelFile(folder, file), project.FolderPath, out var import, out error))
                {
                    Console.Error.WriteLine($"could not import {file}: {error}");
                    return 1;
                }

                assets[folder] = import.Asset;
                Console.WriteLine($"imported {file} -> {import.Asset.AssetPath}: {import.Asset.Animation.Clips.Count} clip(s), {import.Asset.Animation.Skeleton.BoneCount} bone(s)");
            }
        }

        var workspace = new Workspace();
        foreach (var part in BuildScene(folder => assets[folder]))
        {
            workspace.AddPart(part);
        }

        if (!SceneFile.TrySave(workspace, project.ScenePath, out error))
        {
            Console.Error.WriteLine($"could not save the scene: {error}");
            return 1;
        }

        CopyScripts(project.FolderPath);
        Console.WriteLine($"created \"{project.Name}\" at {project.FolderPath}: {workspace.Parts.Count} parts, "
            + $"{Directory.GetFiles(Path.Combine(project.FolderPath, "Assets", "Scripts")).Length} scripts");
        return 0;
    }

    /// <summary>The demo's assets decoded straight from the examples folder (headless checks).</summary>
    public static Func<string, ModelAsset> ExampleAssets()
    {
        var cache = new Dictionary<string, ModelAsset>(StringComparer.Ordinal);
        return folder => cache.TryGetValue(folder, out var asset)
            ? asset
            : cache[folder] = AnimationChecks.LoadAsset(folder, Models.First(model => model.Folder == folder).File);
    }

    /// <summary>
    /// The demo scene. The player (an invisible body with the Robot model on it) starts at the origin, looking along -Z
    /// at the foxes, the dancers and the rest.
    /// </summary>
    public static List<Part> BuildScene(Func<string, ModelAsset> asset)
    {
        var parts = new List<Part>();
        var characters = new PartGroup("Characters");
        var herd = new PartGroup("Fox Herd");
        var dancers = new PartGroup("Dancers");
        var rigs = new PartGroup("Rigs and Props");

        Part Model(string name, string folder, Vector3 origin, float scale, float yaw, PartGroup? group, ModelAnimationSettings? animation, params string[] scripts)
        {
            var model = asset(folder);
            var part = new Part(name, Vector3.Zero, new Vector3(1f, 1f, 1f), ModelReference.DefaultTint, anchored: true)
            {
                Model = new ModelReference(model.AssetPath, model),
                Group = group,
                Animation = animation
            };
            ModelTransform.SetInstanceTransform(part, origin, new Vector3(0f, yaw, 0f), new Vector3(scale, scale, scale));
            AddScripts(part, scripts);
            parts.Add(part);
            return part;
        }

        static ModelAnimationSettings OnStart(string clip, float speed = 1f, bool loop = true) =>
            new() { Clip = clip, PlayOnStart = true, Speed = speed, Loop = loop };

        // The player: Character.lua moves the body and animates the Robot that follows it.
        var body = new Part("PlayerBody", new Vector3(0f, 2.3f, 0f), new Vector3(2f, 4.5f, 2f), new Vector3(0.24f, 0.47f, 0.86f), anchored: false)
        {
            Group = characters
        };
        AddScripts(body, "Character");
        parts.Add(body);
        var robot = Model("Robot", "RobotExpressive", new Vector3(0f, 0.05f, 0f), 1f, 180f, characters, null);
        robot.CanCollide = false; // the body collides; the model only shows it
        Model("Cesium Man", "CesiumMan", new Vector3(-18f, 0f, -6f), 3f, 90f, characters, OnStart("Animation 1"));

        // One asset, four instances, four different playbacks.
        Model("Fox Survey", "Fox", new Vector3(-12f, 0f, -22f), 0.05f, 0f, herd, OnStart("Survey"));
        Model("Fox Walk", "Fox", new Vector3(-4f, 0f, -22f), 0.05f, 0f, herd, OnStart("Walk"));
        Model("Fox Run", "Fox", new Vector3(4f, 0f, -22f), 0.05f, 0f, herd, OnStart("Run", speed: 0.5f));
        Model("Fox Tempo", "Fox", new Vector3(12f, 0f, -22f), 0.05f, 0f, herd, null, "SpeedPulse");

        // Copies of the Robot cycling clips at their own tempo.
        Model("Dancer 1", "RobotExpressive", new Vector3(-10f, 0f, -38f), 1f, 0f, dancers, null, "Dancer");
        Model("Dancer 2", "RobotExpressive", new Vector3(0f, 0f, -38f), 1f, 0f, dancers, null, "Dancer");
        Model("Dancer 3", "RobotExpressive", new Vector3(10f, 0f, -38f), 1f, 0f, dancers, null, "Dancer");

        // A simple two-bone rig, node animation without a skeleton, an FBX rig and a static model.
        Model("Rigged Simple", "RiggedSimple", new Vector3(18f, 2.3f, -6f), 0.5f, 0f, rigs, OnStart("Animation 1"));
        Model("Box Animated", "BoxAnimated", new Vector3(16f, 1.6f, 6f), 3f, 0f, rigs, OnStart("Animation 1"));
        Model("Static Box", "Box", new Vector3(-16f, 1.5f, 6f), 3f, 0f, rigs, null);
        Model("FBX Rig", "AnimationWithSkeletonFbx", new Vector3(0f, 2f, -54f), 0.002f, 0f, rigs, OnStart("Armature|ArmatureAction"));
        return parts;
    }

    private static void AddScripts(Part part, params string[] scripts)
    {
        foreach (var script in scripts)
        {
            ScriptReference.TryCreate($"Assets/Scripts/{script}.lua", out var reference);
            part.Scripts.Add(reference!);
        }
    }
}
