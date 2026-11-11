using System.Text;
using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// The Lua Capabilities V5 showcase: one scene where the V5 systems work together, written only with the public Lua API
/// (examples/gameplay-systems/Scripts). <c>Wisko.Tests --make-v5-demo &lt;projects folder&gt;</c> writes it as an ordinary project
/// ("Gameplay Systems Showcase"), importing its models (from examples/animation-demo/Models) through the editor's own pipeline;
/// the checks build the same scene headless.
/// </summary>
internal static class LuaV5Demo
{
    public const string ProjectName = "Gameplay Systems Showcase";

    public static readonly (string Folder, string File)[] Models =
    [
        ("RobotExpressive", "RobotExpressive.glb"),
        ("CesiumMan", "CesiumMan.glb"),
        ("Fox", "Fox.glb")
    ];

    public static string ScriptsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples", "gameplay-systems", "Scripts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("examples/gameplay-systems/Scripts not found above " + AppContext.BaseDirectory);
    }

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

    /// <summary>Creates the showcase project in <paramref name="projectsRoot"/>; refuses when a project of that name exists.</summary>
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
                Console.WriteLine($"imported {file} -> {import.Asset.AssetPath}: {import.Asset.Animation.Clips.Count} clip(s)");
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

    /// <summary>The showcase's models decoded straight from the examples folder (headless checks).</summary>
    public static Func<string, ModelAsset> ExampleAssets()
    {
        var cache = new Dictionary<string, ModelAsset>(StringComparer.Ordinal);
        return folder => cache.TryGetValue(folder, out var asset)
            ? asset
            : cache[folder] = AnimationChecks.LoadAsset(folder, Models.First(model => model.Folder == folder).File);
    }

    private static Vector3 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

    /// <summary>
    /// The showcase scene. The player starts at the village plaza (0, 0, 34) looking north (−Z) at the guard's warehouse;
    /// Mira and the workbench are at the plaza, the vault to the west, the forest to the east, the race course south-east
    /// and the sky island far to the south (reached by the teleport pad, left by the moving platform).
    /// </summary>
    public static List<Part> BuildScene(Func<string, ModelAsset> asset)
    {
        var parts = new List<Part>();
        var village = new PartGroup("Village");
        var guard = new PartGroup("Guard Area");
        var vault = new PartGroup("Vault");
        var island = new PartGroup("Sky Island");
        var race = new PartGroup("Race Course");
        var markers = new PartGroup("Markers");

        Part Block(string name, Vector3 position, Vector3 size, Vector3 color, PartGroup group, bool anchored = true, ShapeType shape = ShapeType.Block,
            params string[] scripts)
        {
            var part = new Part(name, position, size, color, anchored: anchored) { Group = group, ShapeType = shape };
            AddScripts(part, scripts);
            parts.Add(part);
            return part;
        }

        Part Trigger(string name, Vector3 position, Vector3 size, Vector3 color, PartGroup group, params string[] scripts)
        {
            var part = Block(name, position, size, color, group, scripts: scripts);
            part.IsTrigger = true;
            return part;
        }

        Part Marker(string name, Vector3 position, params string[] scripts)
        {
            var part = Block(name, position, new Vector3(1f, 1f, 1f), Rgb(255, 255, 255), markers, scripts: scripts);
            part.CanCollide = false;
            part.Visible = false;
            return part;
        }

        Part Model(string name, string folder, Vector3 origin, float scale, float yaw, PartGroup group, ModelAnimationSettings? animation, params string[] scripts)
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

        // ---- The village plaza ----
        Marker("Spawn", new Vector3(0f, 0.5f, 34f));
        var body = Block("PlayerBody", new Vector3(0f, 2.3f, 34f), new Vector3(2f, 4.5f, 2f), Rgb(60, 120, 220), village, anchored: false, scripts: "Player");
        _ = body;
        var robot = Model("PlayerRobot", "RobotExpressive", new Vector3(0f, 0.05f, 34f), 1f, 180f, village, null);
        robot.CanCollide = false;
        Model("Mira", "CesiumMan", new Vector3(-10f, 0f, 18f), 3f, 90f, village, new ModelAnimationSettings { Clip = "Animation 1", PlayOnStart = true, Speed = 0.4f });
        var miraSpot = Trigger("MiraSpot", new Vector3(-7f, 1.5f, 18f), new Vector3(3f, 3f, 3f), Rgb(255, 210, 120), village, "Mira");
        miraSpot.Visible = false;
        Block("Well", new Vector3(-13f, 0.75f, 23f), new Vector3(3f, 1.5f, 3f), Rgb(140, 140, 150), village, shape: ShapeType.Cylinder);
        Block("Workbench", new Vector3(10f, 1f, 18f), new Vector3(4f, 2f, 2f), Rgb(150, 100, 55), village, scripts: "Workbench");
        Block("HouseWest", new Vector3(-24f, 3f, 34f), new Vector3(8f, 6f, 8f), Rgb(200, 180, 150), village);
        Block("HouseWestRoof", new Vector3(-24f, 7f, 34f), new Vector3(9f, 2f, 9f), Rgb(170, 70, 60), village, shape: ShapeType.Wedge);
        Block("HouseEast", new Vector3(24f, 3f, 36f), new Vector3(8f, 6f, 8f), Rgb(200, 190, 160), village);
        for (var i = 0; i < 3; i++)
        {
            Block("Crate" + (i + 1), new Vector3(16f + i * 2.2f, 1f, 26f), new Vector3(2f, 2f, 2f), Rgb(170, 130, 80), village, anchored: false, scripts: "Crate");
        }

        Trigger("PadPlaza", new Vector3(0f, 0.3f, 48f), new Vector3(4f, 0.6f, 4f), Rgb(150, 90, 230), village);
        Trigger("FlagVillage", new Vector3(6f, 2f, 40f), new Vector3(2f, 4f, 2f), Rgb(255, 220, 90), village);
        Model("Fox", "Fox", new Vector3(-30f, 0f, 52f), 0.05f, 0f, village, null, "Wanderer");

        // ---- The guard and its warehouse (north) ----
        Block("Warehouse", new Vector3(0f, 4f, -25f), new Vector3(30f, 8f, 14f), Rgb(120, 110, 100), guard);
        Block("CoverWallA", new Vector3(8f, 1.5f, -2f), new Vector3(6f, 3f, 1f), Rgb(110, 115, 125), guard);
        Block("CoverWallB", new Vector3(-12f, 1.5f, -2f), new Vector3(6f, 3f, 1f), Rgb(110, 115, 125), guard);
        Block("GuardBody", new Vector3(-26f, 2.3f, -6f), new Vector3(2f, 4.5f, 2f), Rgb(220, 70, 60), guard, anchored: false, scripts: "Guard");
        var guardRobot = Model("GuardRobot", "RobotExpressive", new Vector3(-26f, 0.05f, -6f), 1f, 180f, guard, null);
        guardRobot.CanCollide = false;

        // ---- The vault (west): two switches and a timed plate open its door ----
        Block("VaultFloor", new Vector3(-50f, 0.1f, 0f), new Vector3(14f, 0.2f, 14f), Rgb(70, 72, 80), vault);
        Block("VaultWallN", new Vector3(-50f, 3f, -7f), new Vector3(14f, 6f, 1f), Rgb(95, 98, 110), vault);
        Block("VaultWallS", new Vector3(-50f, 3f, 7f), new Vector3(14f, 6f, 1f), Rgb(95, 98, 110), vault);
        Block("VaultWallW", new Vector3(-57f, 3f, 0f), new Vector3(1f, 6f, 14f), Rgb(95, 98, 110), vault);
        Block("VaultWallE1", new Vector3(-43f, 3f, -4.75f), new Vector3(1f, 6f, 4.5f), Rgb(95, 98, 110), vault);
        Block("VaultWallE2", new Vector3(-43f, 3f, 4.75f), new Vector3(1f, 6f, 4.5f), Rgb(95, 98, 110), vault);
        Block("VaultRoof", new Vector3(-50.5f, 6.25f, 0f), new Vector3(14f, 0.5f, 15f), Rgb(80, 82, 92), vault);
        Block("VaultDoor", new Vector3(-43f, 2.5f, 0f), new Vector3(1f, 5f, 5f), Rgb(180, 140, 60), vault, scripts: "Vault");
        Block("SwitchA", new Vector3(-37f, 1f, -10f), new Vector3(1f, 2f, 1f), Rgb(214, 69, 69), vault);
        Block("SwitchB", new Vector3(-37f, 1f, 10f), new Vector3(1f, 2f, 1f), Rgb(214, 69, 69), vault);
        Trigger("Plate", new Vector3(-36f, 0.2f, 0f), new Vector3(4f, 0.4f, 4f), Rgb(140, 109, 58), vault);
        Trigger("Gem", new Vector3(-52f, 1.2f, 0f), new Vector3(1f, 1f, 1f), Rgb(80, 220, 255), vault).ShapeType = ShapeType.Sphere;

        // ---- The sky island (far south) and the lift down ----
        Block("IslandBase", new Vector3(0f, 20f, 110f), new Vector3(30f, 2f, 30f), Rgb(110, 160, 90), island);
        Trigger("PadIsland", new Vector3(0f, 21.3f, 104f), new Vector3(4f, 0.6f, 4f), Rgb(150, 90, 230), island);
        Trigger("FlagIsland", new Vector3(8f, 23f, 116f), new Vector3(2f, 4f, 2f), Rgb(255, 220, 90), island);
        Block("IslandTree", new Vector3(-8f, 25f, 116f), new Vector3(1.6f, 8f, 1.6f), Rgb(107, 74, 43), island, shape: ShapeType.Cylinder);
        Block("SkyLift", new Vector3(0f, 20.7f, 92f), new Vector3(5f, 0.6f, 5f), Rgb(230, 200, 90), island, scripts: "Platform");
        var liftPoints = new[]
        {
            new Vector3(0f, 20.7f, 92f), new Vector3(5f, 18f, 84f), new Vector3(13f, 13f, 77f), new Vector3(17f, 8f, 69f),
            new Vector3(14f, 3.5f, 61f), new Vector3(8f, 0.6f, 55f)
        };
        for (var i = 0; i < liftPoints.Length; i++)
        {
            Marker("LiftPoint" + (i + 1), liftPoints[i]);
        }

        // ---- The race course (east) ----
        Block("RaceStart", new Vector3(56f, 1.5f, 34f), new Vector3(1f, 3f, 1f), Rgb(255, 255, 255), race, scripts: "RaceCourse");
        Trigger("FlagRace", new Vector3(52f, 2f, 34f), new Vector3(2f, 4f, 2f), Rgb(255, 220, 90), race);
        Trigger("CP1", new Vector3(64f, 3f, 30f), new Vector3(1f, 6f, 10f), Rgb(255, 120, 60), race);
        Trigger("CP2", new Vector3(100f, 3f, 62f), new Vector3(10f, 6f, 1f), Rgb(255, 160, 60), race);
        Trigger("CP3", new Vector3(64f, 3f, 92f), new Vector3(1f, 6f, 10f), Rgb(255, 200, 60), race);
        Block("RaceRock", new Vector3(80f, 2f, 60f), new Vector3(6f, 4f, 20f), Rgb(130, 130, 140), race);

        // ---- Scripts without a body of their own, and the flyover's path ----
        Marker("Game", new Vector3(0f, 0.5f, 200f), "Game");
        Marker("Forest", new Vector3(70f, 0.5f, -30f), "Forest");
        Marker("Portals", new Vector3(2f, 0.5f, 200f), "Portals");
        Marker("Intro", new Vector3(4f, 0.5f, 200f), "Intro");
        var flyPoints = new[]
        {
            new Vector3(70f, 32f, 80f), new Vector3(40f, 24f, 20f), new Vector3(-20f, 20f, -10f), new Vector3(-40f, 14f, 30f), new Vector3(0f, 8f, 50f)
        };
        for (var i = 0; i < flyPoints.Length; i++)
        {
            Marker("FlyPoint" + (i + 1), flyPoints[i]);
        }

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
