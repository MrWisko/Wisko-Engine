using System.Text;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.Projects;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// The Lua V4 demonstration: its scripts live in the repository (examples/gameplay-basics/Scripts, plain Lua files), and
/// this builds its scene from placeholder Parts, grouped in the World panel. <c>Wisko.Tests --make-demo &lt;projects
/// folder&gt;</c> writes it as an ordinary project ("Gameplay Basics Demo"); the checks play the same scene headless.
/// </summary>
internal static class LuaV4Demo
{
    public const string ProjectName = "Gameplay Basics Demo";

    /// <summary>examples/gameplay-basics/Scripts in this checkout (found by walking up to Wisko.sln).</summary>
    public static string ScriptsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wisko.sln")))
            {
                var scripts = Path.Combine(dir.FullName, "examples", "gameplay-basics", "Scripts");
                if (Directory.Exists(scripts))
                {
                    return scripts;
                }
            }
        }

        throw new DirectoryNotFoundException("examples/gameplay-basics/Scripts not found above " + AppContext.BaseDirectory);
    }

    public static string Script(string name) => File.ReadAllText(Path.Combine(ScriptsFolder(), name + ".lua"));

    /// <summary>Copies every example script into the project's Assets/Scripts.</summary>
    public static void CopyScripts(string projectRoot)
    {
        var target = Path.Combine(projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(ScriptsFolder(), "*.lua"))
        {
            File.WriteAllText(Path.Combine(target, Path.GetFileName(file)), File.ReadAllText(file), new UTF8Encoding(false));
        }
    }

    /// <summary>The Baseplate the editor gives every world (headless Play needs it spelled out).</summary>
    public static PhysicsEnvironment Baseplate()
    {
        var environment = new PhysicsEnvironment();
        environment.AddBox("Baseplate", new Vector3(0f, -0.5f, 0f), new Vector3(512f, 1f, 512f));
        return environment;
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

        var workspace = new Workspace();
        foreach (var part in BuildScene())
        {
            workspace.AddPart(part);
        }

        if (!store.TryCreateProject(ProjectName, WorldTemplate.Classic, workspace, out var project, out var error))
        {
            Console.Error.WriteLine($"could not create the project: {error}");
            return 1;
        }

        CopyScripts(project.FolderPath);
        Console.WriteLine($"created \"{project.Name}\" at {project.FolderPath}: {workspace.Parts.Count} parts, "
            + $"{Directory.GetFiles(Path.Combine(project.FolderPath, "Assets", "Scripts")).Length} scripts");
        return 0;
    }

    /// <summary>The demo scene. The player starts at the origin looking along -Z.</summary>
    public static List<Part> BuildScene()
    {
        var parts = new List<Part>();
        var coins = new PartGroup("Coins");
        var resources = new PartGroup("Resources");
        var food = new PartGroup("Food and Animals");
        var machines = new PartGroup("Machines");
        var conveyor = new PartGroup("Conveyor Line");
        var playground = new PartGroup("Playground");
        var hazards = new PartGroup("Hazards", playground);
        var collision = new PartGroup("Collision Rules", playground);
        var targets = new PartGroup("Targets", playground);

        Part Add(string name, ShapeType shape, Vector3 position, Vector3 size, string color, bool anchored, PartGroup? group, params string[] scripts)
        {
            var part = new Part(name, position, size, Hex(color), anchored: anchored, shapeType: shape) { Group = group };
            foreach (var script in scripts)
            {
                ScriptReference.TryCreate($"Assets/Scripts/{script}.lua", out var reference);
                part.Scripts.Add(reference!);
            }

            parts.Add(part);
            return part;
        }

        // The player: movement, flight, interaction, inventory, stats, abilities, objectives and the HUD.
        Add("Player", ShapeType.Block, new Vector3(0, 2.5f, 0), new Vector3(2, 4, 2), "#3C78DC", false, null,
            "Player", "Flight", "Interact", "Inventory", "Collector", "Survival", "Status", "Pulse", "Objectives", "HUD");
        var rules = Add("GameRules", ShapeType.Block, new Vector3(0, 0.5f, 40), new Vector3(1, 1, 1), "#202020", true, null, "Rules");
        rules.CanCollide = false;
        rules.Visible = false;

        // Coins along the way north (-Z).
        var coinSpots = new[] { new Vector3(0, 2, -6), new Vector3(0, 2, -9), new Vector3(0, 2, -12), new Vector3(3, 2, -15), new Vector3(-3, 2, -15) };
        for (var i = 0; i < coinSpots.Length; i++)
        {
            var coin = Add($"Coin{i + 1}", ShapeType.Cylinder, coinSpots[i], new Vector3(1.6f, 0.3f, 1.6f), "#FFD84A", true, coins, "Coin");
            coin.Rotation = new Vector3(90, 0, 0);
            coin.IsTrigger = true;
        }

        // Flight orb (touch it to fly).
        Add("FlightOrb", ShapeType.Sphere, new Vector3(8, 3, 8), new Vector3(3, 3, 3), "#46E6FF", true, null, "FlightOrb").IsTrigger = true;

        // Resources to harvest with E.
        Add("Tree", ShapeType.Cylinder, new Vector3(-14, 4, -10), new Vector3(2, 8, 2), "#6B4A2B", true, resources, "ResourceNode");
        Add("Rock", ShapeType.Sphere, new Vector3(-18, 1.5f, -4), new Vector3(3, 3, 3), "#8E9196", true, resources, "ResourceNode");

        // Food, water and a goat to feed.
        Add("Apple1", ShapeType.Sphere, new Vector3(-8, 0.5f, 6), new Vector3(1, 1, 1), "#D23C32", true, food, "Food");
        Add("Apple2", ShapeType.Sphere, new Vector3(-9.5f, 0.5f, 7.5f), new Vector3(1, 1, 1), "#D23C32", true, food, "Food");
        Add("Apple3", ShapeType.Sphere, new Vector3(-7, 0.5f, 8.5f), new Vector3(1, 1, 1), "#D23C32", true, food, "Food");
        Add("Well", ShapeType.Cylinder, new Vector3(-15, 1.5f, 8), new Vector3(3, 3, 3), "#4A6E96", true, food, "Food");
        Add("Goat", ShapeType.Block, new Vector3(-12, 1.2f, 15), new Vector3(3, 2.4f, 1.6f), "#F0EBE1", false, food, "Survival", "Animal");

        // The car (E next to it to drive).
        Add("Car", ShapeType.Block, new Vector3(18, 1.6f, 0), new Vector3(4, 1.4f, 7), "#D23C3C", false, null, "Car", "CarSeat");

        // Machines: a spinning platform that carries you round and a spinning fan.
        Add("Spinner", ShapeType.Cylinder, new Vector3(-30, 0.5f, -24), new Vector3(12, 1, 12), "#8C64C8", true, machines, "Spinner");
        Add("Fan", ShapeType.Block, new Vector3(-24, 5, -34), new Vector3(1, 8, 1), "#C8C8D2", true, machines, "Spinner");

        // Conveyor line: two belts north, a third turning east; crates drop in at the start; the lever switches them.
        Add("Belt1", ShapeType.Block, new Vector3(10, 0.25f, -12), new Vector3(4, 0.5f, 12), "#3A3A40", true, conveyor, "Conveyor");
        Add("Belt2", ShapeType.Block, new Vector3(10, 0.25f, -24), new Vector3(4, 0.5f, 12), "#3A3A40", true, conveyor, "Conveyor");
        var belt3 = Add("Belt3", ShapeType.Block, new Vector3(16, 0.25f, -32), new Vector3(4, 0.5f, 12), "#3A3A40", true, conveyor, "Conveyor");
        belt3.Rotation = new Vector3(0, -90, 0); // its Forward (local -Z) points +X
        var chute = Add("CrateChute", ShapeType.Block, new Vector3(10, 4, -7), new Vector3(2, 0.4f, 2), "#5A5A64", true, conveyor, "CrateSpawner");
        chute.CanCollide = false;
        Add("Lever", ShapeType.Block, new Vector3(6, 1.5f, -8), new Vector3(0.6f, 3, 0.6f), "#3CC86E", true, conveyor, "Lever");

        // Collision rules: solid, pass-through, player-only door, trigger zone.
        Add("SolidWall", ShapeType.Block, new Vector3(6, 3, 22), new Vector3(8, 6, 1), "#7A7A82", true, collision);
        Add("GhostWall", ShapeType.Block, new Vector3(16, 3, 22), new Vector3(8, 6, 1), "#B4DCFF", true, collision).CanCollide = false;
        Add("PlayerDoor", ShapeType.Block, new Vector3(26, 3, 22), new Vector3(8, 6, 1), "#A050D2", true, collision);
        Add("TriggerZone", ShapeType.Block, new Vector3(36, 2, 22), new Vector3(8, 4, 4), "#2A6E8C", true, collision, "Zone").IsTrigger = true;

        // Hazards (timed status effects) and a bumper (knockback).
        Add("Lava", ShapeType.Block, new Vector3(-10, 0.25f, 24), new Vector3(6, 0.5f, 6), "#FF6A1F", true, hazards, "Hazard").IsTrigger = true;
        Add("Mud", ShapeType.Block, new Vector3(-18, 0.25f, 24), new Vector3(6, 0.5f, 6), "#6E4E2E", true, hazards, "Hazard").IsTrigger = true;
        Add("HealPad", ShapeType.Block, new Vector3(-26, 0.25f, 24), new Vector3(6, 0.5f, 6), "#46D278", true, hazards, "Hazard").IsTrigger = true;
        Add("Bumper", ShapeType.Cylinder, new Vector3(-4, 1.5f, 32), new Vector3(4, 3, 4), "#FF4AA0", true, hazards, "Bumper");

        // Targets: one in the open, one behind the solid wall (X-ray reaches it, a normal blast does not).
        Add("Dummy1", ShapeType.Block, new Vector3(6, 2.5f, 18), new Vector3(2, 5, 2), "#8CDC40", false, targets, "Status", "Dummy");
        Add("Dummy2", ShapeType.Block, new Vector3(6, 2.5f, 26), new Vector3(2, 5, 2), "#8CDC40", false, targets, "Status", "Dummy");
        return parts;
    }

    private static Vector3 Hex(string hex) => new(
        Convert.ToInt32(hex.Substring(1, 2), 16) / 255f,
        Convert.ToInt32(hex.Substring(3, 2), 16) / 255f,
        Convert.ToInt32(hex.Substring(5, 2), 16) / 255f);
}
