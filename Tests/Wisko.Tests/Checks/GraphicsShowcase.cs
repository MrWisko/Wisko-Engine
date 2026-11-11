using System.Text;
using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// The Graphics V1 showcase ("Graphics Showcase"): twelve areas, one per Graphics V1 feature, each a World
/// panel group, plus Lua scripts (examples/graphics-demo/Scripts) for a camera tour, quality switching, a day and night
/// cycle and animated materials and lights. <c>Wisko.Tests --make-graphics-demo &lt;projects folder&gt;</c> writes it as an
/// ordinary project: models are imported through the editor's own pipeline, textures are generated into Assets/Textures.
/// </summary>
internal static class GraphicsShowcase
{
    public const string ProjectName = "Graphics Showcase";

    /// <summary>The showcase's project settings: the Universal renderer at Medium with bloom on (for the glow area).</summary>
    public static GraphicsSettings Graphics => GraphicsSettings.Default with { BloomEnabled = true };

    /// <summary>The default sky with fog prepared (off until the tour reaches the fog area, or F).</summary>
    public static AtmosphereSettings Atmosphere => AtmosphereSettings.Default with
    {
        FogEnabled = false, FogColor = AtmosphereColor.FromBytes(196, 208, 222), FogDensity = 0.9f, FogStart = 15f, FogEnd = 150f
    };

    public static string ScriptsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples", "graphics-demo", "Scripts");
            if (File.Exists(Path.Combine(dir.FullName, "Wisko.sln")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("examples/graphics-demo/Scripts not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Creates the showcase in <paramref name="projectsRoot"/>; refuses when a project of that name exists.</summary>
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

        var workspace = new Workspace();
        foreach (var part in Build(project.FolderPath))
        {
            workspace.AddPart(part);
        }

        if (!SceneFile.TrySave(workspace, project.ScenePath, out error)
            || !GraphicsFile.TrySave(project.GraphicsPath, Graphics, out error)
            || !AtmosphereFile.TrySave(project.AtmospherePath, Atmosphere, out error))
        {
            Console.Error.WriteLine($"could not save the project: {error}");
            return 1;
        }

        Console.WriteLine($"created \"{project.Name}\" at {project.FolderPath}: {workspace.Parts.Count} parts");
        return 0;
    }

    /// <summary>Imports the models, writes the textures and scripts into the project, and builds the scene's Parts.</summary>
    public static List<Part> Build(string projectRoot)
    {
        var models = GraphicsDemo.ImportAll(projectRoot);
        using (var decoder = new Wisko.Engine.Assets.Importing.AssimpModelImporter())
        {
            if (!new ProjectModelImporter(decoder).TryImport(AnimationChecks.ModelFile("Fox", "Fox.glb"), projectRoot, out var fox, out var error))
            {
                throw new InvalidOperationException($"could not import Fox.glb: {error}");
            }

            models["Fox"] = fox.Asset;
        }

        WriteTextures(projectRoot);
        var scripts = Path.Combine(projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        foreach (var file in Directory.GetFiles(ScriptsFolder(), "*.lua"))
        {
            File.WriteAllText(Path.Combine(scripts, Path.GetFileName(file)), File.ReadAllText(file), new UTF8Encoding(false));
        }

        return BuildScene(folder => models[folder]);
    }

    // ------------------------------------------------------------------------------------------------- the scene

    private sealed class Scene
    {
        public readonly List<Part> Parts = [];

        public Part Add(string name, PartGroup group, Vector3 position, Vector3 size, Vector3 color, ShapeType shape = ShapeType.Block,
            Vector3? rotation = null, SurfaceMaterial? material = null)
        {
            var part = new Part(name, position, size, color, rotation, anchored: true, shapeType: shape) { Group = group, Material = material };
            Parts.Add(part);
            return part;
        }

        // A light with a small bulb: unlit and bright for a coloured light, else lit glass that the scripts make glow.
        public Part Light(string name, PartGroup group, Vector3 position, LightType type, Vector3 color, float intensity, float range,
            bool shadows = false, Vector3? rotation = null, float angle = 60f, float inner = 40f, bool enabled = true, SurfaceMaterial? bulb = null)
        {
            var part = Add(name, group, position, new Vector3(0.8f, 0.8f, 0.8f), color, ShapeType.Sphere, rotation,
                bulb ?? SurfaceMaterial.Default with { Mode = ShadingMode.Unlit, CastShadows = false });
            part.CanCollide = false;
            part.Light = new PartLight
            {
                Type = type, Color = color, Intensity = intensity, Range = range, CastShadows = shadows, Angle = angle, InnerAngle = inner,
                Enabled = enabled
            }.Sanitized();
            return part;
        }

        public Part Model(string name, PartGroup group, ModelAsset asset, Vector3 origin, float scale, float yaw = 0f)
        {
            var part = GraphicsDemo.ModelPart(name, asset, origin, scale, yaw);
            part.Group = group;
            Parts.Add(part);
            return part;
        }
    }

    private static SurfaceMaterial M(float metallic, float roughness) => SurfaceMaterial.Default with { Metallic = metallic, Roughness = roughness };

    private static SurfaceMaterial Glow(Vector3 color, float strength) =>
        SurfaceMaterial.Default with { EmissiveColor = color, EmissiveIntensity = strength, Roughness = 0.4f };

    private static SurfaceMaterial Toon(bool outline = true) =>
        SurfaceMaterial.Default with { Mode = ShadingMode.Toon, ToonBands = 3, Outline = outline, OutlineColor = new Vector3(0.05f, 0.05f, 0.08f), OutlineThickness = 0.08f };

    private static void Script(Part part, string name)
    {
        ScriptReference.TryCreate($"Assets/Scripts/{name}.lua", out var reference);
        part.Scripts.Add(reference!);
    }

    public static List<Part> BuildScene(Func<string, ModelAsset> asset)
    {
        var s = new Scene();
        var light = new Vector3(0.85f, 0.85f, 0.86f);
        var dark = new Vector3(0.12f, 0.12f, 0.14f);

        // Scripts (markers out of the way, south of the areas).
        var scriptsGroup = new PartGroup("00 Scripts");
        foreach (var (name, x) in new[] { ("Tour", -6f), ("Quality", -2f), ("Clock", 2f), ("Neon", 6f) })
        {
            var marker = s.Add(name, scriptsGroup, new Vector3(x, 0.5f, 110f), new Vector3(1f, 1f, 1f), new Vector3(0.4f, 0.45f, 0.55f));
            marker.CanCollide = false;
            Script(marker, name);
        }

        // 1. PBR materials: metallic 0..1 front to back, roughness 0..1 left to right.
        var pbr = new PartGroup("01 PBR Materials");
        s.Add("PBR Stage", pbr, new Vector3(-105f, 0.25f, 50f), new Vector3(40f, 0.5f, 34f), dark, material: M(0f, 0.9f));
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 6; column++)
            {
                var metallic = row / 4f;
                var roughness = column / 5f;
                s.Add($"Sphere M{metallic:0.00} R{roughness:0.0}", pbr, new Vector3(-105f + (column - 2.5f) * 6f, 2.6f, 62f - row * 6f),
                    new Vector3(4f, 4f, 4f), new Vector3(0.95f, 0.7f, 0.42f), ShapeType.Sphere, material: M(metallic, roughness));
            }
        }

        // 2. Textured models (Khronos glTF Sample Assets) and textured primitives (generated textures).
        var textured = new PartGroup("02 Textured Models");
        s.Add("Texture Stage", textured, new Vector3(-35f, 0.25f, 50f), new Vector3(40f, 0.5f, 34f), light, material: M(0f, 0.8f));
        s.Model("Water Bottle", textured, asset("WaterBottle"), new Vector3(-47f, 3.2f, 54f), 18f);
        s.Model("Normal Map Mirror Test", textured, asset("NormalTangentMirrorTest"), new Vector3(-34f, 4.6f, 44f), 3f);
        s.Model("Alpha Modes Test", textured, asset("AlphaBlendModeTest"), new Vector3(-21f, 1f, 55f), 1.5f);
        s.Model("Texture Settings Test", textured, asset("TextureSettingsTest"), new Vector3(-22f, 5.5f, 38f), 0.7f);
        s.Add("Brick Wall", textured, new Vector3(-50f, 3.5f, 40f), new Vector3(10f, 6f, 1f), new Vector3(1f, 1f, 1f), material: SurfaceMaterial.Default with
        {
            BaseColorTexture = "Assets/Textures/brick.png", NormalTexture = "Assets/Textures/brick_normal.png", UvMode = UvMode.Studs,
            UvScaleX = 0.5f, UvScaleY = 0.5f, Roughness = 0.85f
        });
        s.Add("Plank Crate", textured, new Vector3(-27f, 2f, 62f), new Vector3(3f, 3f, 3f), new Vector3(1f, 1f, 1f),
            material: SurfaceMaterial.Default with { BaseColorTexture = "Assets/Textures/planks.png", Roughness = 0.7f });

        // 3. Coloured lights in a closed room (the walls and roof keep the sun out).
        var lights = new PartGroup("03 Coloured Lights");
        var wall = new Vector3(0.8f, 0.8f, 0.82f);
        s.Add("Room Floor", lights, new Vector3(35f, 0.25f, 50f), new Vector3(26f, 0.5f, 26f), wall, material: M(0f, 0.6f));
        s.Add("Room Back Wall", lights, new Vector3(35f, 5f, 37f), new Vector3(26f, 10f, 1f), wall);
        s.Add("Room Left Wall", lights, new Vector3(22f, 5f, 50f), new Vector3(1f, 10f, 26f), wall);
        s.Add("Room Right Wall", lights, new Vector3(48f, 5f, 50f), new Vector3(1f, 10f, 26f), wall);
        s.Add("Room Front Wall Left", lights, new Vector3(27.75f, 5f, 63f), new Vector3(11.5f, 10f, 1f), wall);
        s.Add("Room Front Wall Right", lights, new Vector3(42.25f, 5f, 63f), new Vector3(11.5f, 10f, 1f), wall);
        s.Add("Room Door Lintel", lights, new Vector3(35f, 8.5f, 63f), new Vector3(3f, 3f, 1f), wall);
        s.Add("Room Roof", lights, new Vector3(35f, 10.5f, 50f), new Vector3(27f, 1f, 27f), wall);
        s.Add("Room Pillar West", lights, new Vector3(28f, 3f, 44f), new Vector3(1.6f, 6f, 1.6f), new Vector3(0.95f, 0.95f, 0.95f), ShapeType.Cylinder);
        s.Add("Room Pillar East", lights, new Vector3(42f, 3f, 44f), new Vector3(1.6f, 6f, 1.6f), new Vector3(0.95f, 0.95f, 0.95f), ShapeType.Cylinder);
        s.Add("Room Sphere", lights, new Vector3(35f, 2f, 45f), new Vector3(3f, 3f, 3f), new Vector3(0.95f, 0.95f, 0.95f), ShapeType.Sphere, material: M(0f, 0.35f));
        s.Add("Room Block", lights, new Vector3(31f, 1f, 52f), new Vector3(2f, 2f, 2f), new Vector3(0.9f, 0.9f, 0.9f), rotation: new Vector3(0f, 30f, 0f));
        s.Light("Red Light", lights, new Vector3(27f, 6f, 49f), LightType.Point, new Vector3(1f, 0.15f, 0.1f), 55f, 16f);
        s.Light("Green Light", lights, new Vector3(43f, 6f, 49f), LightType.Point, new Vector3(0.2f, 1f, 0.25f), 55f, 16f);
        s.Light("Blue Light", lights, new Vector3(35f, 6f, 39f), LightType.Point, new Vector3(0.2f, 0.35f, 1f), 65f, 16f);
        s.Light("Magenta Spot", lights, new Vector3(35f, 9.5f, 50f), LightType.Spot, new Vector3(1f, 0.25f, 0.85f), 220f, 14f, shadows: true,
            rotation: new Vector3(90f, 0f, 0f), angle: 50f, inner: 30f);
        s.Light("Rainbow Orb", lights, new Vector3(35f, 2.5f, 56f), LightType.Point, new Vector3(1f, 1f, 1f), 50f, 10f,
            bulb: Glow(new Vector3(1f, 1f, 1f), 3f));

        // 4. Shadows: a pergola, a turning windmill, a spot light with its own shadows, steps.
        var shadows = new PartGroup("04 Shadows");
        var stone = new Vector3(0.82f, 0.79f, 0.73f);
        s.Add("Shadow Ground", shadows, new Vector3(105f, 0.25f, 50f), new Vector3(44f, 0.5f, 40f), stone, material: M(0f, 0.9f));
        foreach (var (x, z) in new[] { (95f, 40f), (115f, 40f), (95f, 60f), (115f, 60f) })
        {
            s.Add($"Pergola Post {x:0} {z:0}", shadows, new Vector3(x, 4.25f, z), new Vector3(1f, 8f, 1f), new Vector3(0.45f, 0.32f, 0.22f), ShapeType.Cylinder);
        }

        for (var i = 0; i < 10; i++)
        {
            s.Add($"Pergola Slat {i + 1}", shadows, new Vector3(105f, 8.5f, 40f + i * 2.22f), new Vector3(22f, 0.4f, 0.6f), new Vector3(0.5f, 0.36f, 0.25f));
        }

        s.Add("Windmill Tower", shadows, new Vector3(122f, 6.25f, 40f), new Vector3(2f, 12f, 2f), new Vector3(0.9f, 0.88f, 0.84f), ShapeType.Cylinder);
        foreach (var (name, roll) in new[] { ("Windmill Blade A", 0f), ("Windmill Blade B", 90f) })
        {
            var blade = s.Add(name, shadows, new Vector3(122f, 12.5f, 41.4f), new Vector3(11f, 0.9f, 0.3f), new Vector3(0.95f, 0.95f, 0.95f),
                rotation: new Vector3(0f, 0f, roll));
            Script(blade, "Windmill");
        }

        s.Add("Spot Pedestal", shadows, new Vector3(91f, 1.25f, 60f), new Vector3(2f, 2f, 2f), stone);
        s.Add("Spot Sphere", shadows, new Vector3(91f, 3.25f, 60f), new Vector3(2f, 2f, 2f), new Vector3(0.9f, 0.3f, 0.25f), ShapeType.Sphere);
        s.Light("Shadow Spot", shadows, new Vector3(91f, 9f, 64f), LightType.Spot, new Vector3(1f, 0.85f, 0.6f), 500f, 20f, shadows: true,
            rotation: new Vector3(65f, 0f, 0f), angle: 55f, inner: 35f);
        for (var i = 0; i < 5; i++)
        {
            var height = 0.6f * (i + 1);
            s.Add($"Step {i + 1}", shadows, new Vector3(100f, 0.5f + height * 0.5f, 66f - i * 1.4f), new Vector3(6f, height, 1.4f), stone);
        }

        // 5. Glow and bloom in a dark booth: emissive strengths 1-16, neon orbs, a pulsing sign, the Khronos strength test.
        var glow = new PartGroup("05 Glow and Bloom");
        var booth = new Vector3(0.07f, 0.07f, 0.08f);
        s.Add("Booth Floor", glow, new Vector3(-105f, 0.25f, -22.5f), new Vector3(30f, 0.5f, 25f), booth, material: M(0f, 0.5f));
        s.Add("Booth Back Wall", glow, new Vector3(-105f, 4.5f, -35f), new Vector3(30f, 9f, 1f), booth);
        s.Add("Booth Left Wall", glow, new Vector3(-120f, 4.5f, -22.5f), new Vector3(1f, 9f, 25f), booth);
        s.Add("Booth Right Wall", glow, new Vector3(-90f, 4.5f, -22.5f), new Vector3(1f, 9f, 25f), booth);
        s.Add("Booth Roof", glow, new Vector3(-105f, 9.5f, -22.5f), new Vector3(31f, 1f, 26f), booth);
        var strengths = new[] { 1f, 2f, 4f, 8f, 16f };
        for (var i = 0; i < strengths.Length; i++)
        {
            s.Add($"Glow Strength {strengths[i]:0}", glow, new Vector3(-111f, 5.2f + i * 0.8f, -34.3f), new Vector3(8f, 0.35f, 0.3f), booth,
                material: Glow(new Vector3(0.2f, 0.85f, 1f), strengths[i]));
        }

        s.Add("Neon Pulse", glow, new Vector3(-99f, 4.5f, -34.3f), new Vector3(0.45f, 6f, 0.3f), booth, material: Glow(new Vector3(1f, 0.2f, 0.75f), 3f));
        foreach (var (name, color, x) in new[] { ("Neon Orb Orange", new Vector3(1f, 0.45f, 0.1f), -101f), ("Neon Orb Green", new Vector3(0.3f, 1f, 0.35f), -97f),
                     ("Neon Orb Violet", new Vector3(0.6f, 0.3f, 1f), -93f) })
        {
            s.Add(name, glow, new Vector3(x, 1.5f, -26f), new Vector3(2f, 2f, 2f), color, ShapeType.Sphere,
                material: MaterialPresets.Find("Neon")!.Apply(SurfaceMaterial.Default, color));
        }

        s.Model("Emissive Strength Test", glow, asset("EmissiveStrengthTest"), new Vector3(-112f, 2.5f, -22f), 1f);

        // 6. Ambient occlusion: light grey stairs, a crate corner, slats and a colonnade.
        var ao = new PartGroup("06 Ambient Occlusion");
        var grey = new Vector3(0.83f, 0.83f, 0.83f);
        s.Add("AO Floor", ao, new Vector3(-35f, 0.25f, -20f), new Vector3(40f, 0.5f, 34f), grey, material: M(0f, 0.9f));
        s.Add("AO Back Wall", ao, new Vector3(-35f, 4.5f, -36f), new Vector3(36f, 8f, 1f), grey);
        s.Add("AO Side Wall", ao, new Vector3(-52f, 4.5f, -26f), new Vector3(1f, 8f, 20f), grey);
        s.Add("AO East Wall", ao, new Vector3(-17f, 4.5f, -26f), new Vector3(1f, 8f, 20f), grey);
        for (var i = 0; i < 6; i++)
        {
            var height = i + 1f;
            s.Add($"AO Step {i + 1}", ao, new Vector3(-24f, 0.5f + height * 0.5f, -26.75f - i * 1.5f), new Vector3(6f, height, 1.5f), grey);
        }

        foreach (var (x, y, z) in new[] { (-50.5f, 1.5f, -34.5f), (-48f, 1.5f, -34.5f), (-50.5f, 1.5f, -32f), (-50.5f, 3.5f, -34.5f) })
        {
            s.Add($"AO Crate {x:0.0} {y:0.0} {z:0.0}", ao, new Vector3(x, y, z), new Vector3(2f, 2f, 2f), grey);
        }

        for (var i = 0; i < 12; i++)
        {
            s.Add($"AO Slat {i + 1}", ao, new Vector3(-44f + i * 0.65f, 3.5f, -35.2f), new Vector3(0.32f, 6f, 0.5f), grey);
        }

        for (var i = 0; i < 6; i++)
        {
            s.Add($"AO Column {i + 1}", ao, new Vector3(-46f + i * 1.7f, 3f, -18f), new Vector3(1.2f, 5f, 1.2f), grey, ShapeType.Cylinder);
        }

        // 7. Day and night: a street with lamps that Clock.lua lights at night and windows that glow.
        var night = new PartGroup("07 Day and Night");
        s.Add("Street", night, new Vector3(35f, 0.25f, -20f), new Vector3(44f, 0.5f, 8f), new Vector3(0.2f, 0.2f, 0.22f), material: M(0f, 0.75f));
        s.Add("Pavement", night, new Vector3(35f, 0.35f, -14f), new Vector3(44f, 0.7f, 4f), new Vector3(0.62f, 0.6f, 0.57f));
        var warm = new Vector3(1f, 0.78f, 0.45f);
        for (var i = 0; i < 5; i++)
        {
            var x = 17f + i * 9f;
            s.Add($"Lamp Post {i + 1}", night, new Vector3(x, 3.7f, -13f), new Vector3(0.35f, 6f, 0.35f), new Vector3(0.15f, 0.15f, 0.16f), ShapeType.Cylinder);
            s.Light($"Lamp {i + 1}", night, new Vector3(x, 7f, -13f), LightType.Point, warm, 70f, 15f, enabled: false,
                bulb: Glow(warm, 0f) with { BaseColor = new Vector3(1f, 0.98f, 0.92f), Roughness = 0.2f, CastShadows = false });
        }

        var houseColors = new[] { new Vector3(0.85f, 0.7f, 0.55f), new Vector3(0.65f, 0.75f, 0.85f), new Vector3(0.85f, 0.85f, 0.7f) };
        for (var h = 0; h < 3; h++)
        {
            var x = 21f + h * 14f;
            s.Add($"House {h + 1}", night, new Vector3(x, 3.5f, -31f), new Vector3(9f, 6f, 7f), houseColors[h]);
            s.Add($"House {h + 1} Roof", night, new Vector3(x, 8f, -31f), new Vector3(10f, 3f, 8f), new Vector3(0.45f, 0.2f, 0.18f), ShapeType.Pyramid);
            for (var w = 0; w < 2; w++)
            {
                s.Add($"Window {h + 1}{(char)('A' + w)}", night, new Vector3(x - 2.2f + w * 4.4f, 4f, -27.45f), new Vector3(1.6f, 1.6f, 0.15f),
                    new Vector3(0.18f, 0.22f, 0.3f), material: Glow(warm, 0f) with { Roughness = 0.15f });
            }
        }

        // 8. Fog and atmosphere: an avenue of pillars into the distance.
        var fog = new PartGroup("08 Fog and Atmosphere");
        s.Add("Avenue Path", fog, new Vector3(165f, 0.25f, -20f), new Vector3(170f, 0.5f, 10f), new Vector3(0.78f, 0.72f, 0.6f));
        for (var x = 85f; x <= 245f; x += 10f)
        {
            s.Add($"Avenue Pillar N {x:0}", fog, new Vector3(x, 6.25f, -12f), new Vector3(2f, 12f, 2f), new Vector3(0.76f, 0.73f, 0.68f), ShapeType.Cylinder);
            s.Add($"Avenue Pillar S {x:0}", fog, new Vector3(x, 6.25f, -28f), new Vector3(2f, 12f, 2f), new Vector3(0.76f, 0.73f, 0.68f), ShapeType.Cylinder);
        }

        // 9. Toon style: banded shading with drawn outlines.
        var toon = new PartGroup("09 Toon Style");
        s.Add("Toon Meadow", toon, new Vector3(-105f, 0.25f, -92f), new Vector3(36f, 0.5f, 30f), new Vector3(0.45f, 0.75f, 0.35f), material: Toon(outline: false));
        foreach (var (x, z) in new[] { (-116f, -85f), (-95f, -101f), (-113f, -101f), (-93f, -84f) })
        {
            s.Add($"Toon Trunk {x:0} {z:0}", toon, new Vector3(x, 2.5f, z), new Vector3(1f, 4f, 1f), new Vector3(0.55f, 0.35f, 0.2f), ShapeType.Cylinder, material: Toon());
            s.Add($"Toon Crown {x:0} {z:0}", toon, new Vector3(x, 6f, z), new Vector3(4.5f, 4.5f, 4.5f), new Vector3(0.3f, 0.7f, 0.3f), ShapeType.Sphere, material: Toon());
        }

        s.Add("Toon House", toon, new Vector3(-104f, 3f, -95f), new Vector3(7f, 5f, 6f), new Vector3(1f, 0.85f, 0.45f), material: Toon());
        s.Add("Toon Roof", toon, new Vector3(-104f, 7f, -95f), new Vector3(8f, 3f, 7f), new Vector3(0.9f, 0.3f, 0.25f), ShapeType.Pyramid, material: Toon());
        s.Add("Snowman Base", toon, new Vector3(-98f, 1.7f, -86f), new Vector3(2.8f, 2.8f, 2.8f), new Vector3(0.97f, 0.97f, 1f), ShapeType.Sphere, material: Toon());
        s.Add("Snowman Body", toon, new Vector3(-98f, 3.7f, -86f), new Vector3(2f, 2f, 2f), new Vector3(0.97f, 0.97f, 1f), ShapeType.Sphere, material: Toon());
        s.Add("Snowman Head", toon, new Vector3(-98f, 5.2f, -86f), new Vector3(1.4f, 1.4f, 1.4f), new Vector3(0.97f, 0.97f, 1f), ShapeType.Sphere, material: Toon());
        s.Add("Snowman Nose", toon, new Vector3(-98f, 5.2f, -85f), new Vector3(0.35f, 0.35f, 1f), new Vector3(1f, 0.5f, 0.1f), ShapeType.Pyramid,
            new Vector3(90f, 0f, 0f), Toon());
        s.Add("Toon Rock", toon, new Vector3(-110f, 1f, -92f), new Vector3(3f, 1.6f, 2.4f), new Vector3(0.6f, 0.6f, 0.65f), ShapeType.Sphere, material: Toon());

        // 10. The animated textured Fox: one running a loop, one surveying.
        var fox = new PartGroup("10 Animated Character");
        s.Add("Fox Meadow", fox, new Vector3(-35f, 0.25f, -90f), new Vector3(36f, 0.5f, 30f), new Vector3(1f, 1f, 1f),
            material: SurfaceMaterial.Default with { BaseColorTexture = "Assets/Textures/grass.png", UvMode = UvMode.Studs, UvScaleX = 0.5f, UvScaleY = 0.5f, Roughness = 0.95f });
        var runner = s.Model("Fox Runner", fox, asset("Fox"), new Vector3(-35f, 0.5f, -81f), 0.05f, 90f);
        Script(runner, "FoxRun");
        var watcher = s.Model("Fox Watching", fox, asset("Fox"), new Vector3(-27f, 0.5f, -97f), 0.05f, -30f);
        watcher.Animation = new ModelAnimationSettings { Clip = "Survey", PlayOnStart = true, Loop = true, Speed = 1f };
        s.Add("Meadow Rock", fox, new Vector3(-44f, 1f, -96f), new Vector3(3f, 2f, 2.6f), new Vector3(0.55f, 0.55f, 0.58f), ShapeType.Sphere, material: M(0f, 0.9f));

        // 11. Reflections: chrome, gold and copper, a polished floor, glass and smooth plastic.
        var mirror = new PartGroup("11 Reflections");
        s.Add("Polished Floor", mirror, new Vector3(35f, 0.25f, -90f), new Vector3(30f, 0.5f, 26f), new Vector3(0.07f, 0.07f, 0.09f), material: M(0f, 0.06f));
        s.Add("Chrome Sphere", mirror, new Vector3(35f, 3.5f, -94f), new Vector3(6f, 6f, 6f), new Vector3(0.95f, 0.95f, 0.96f), ShapeType.Sphere, material: M(1f, 0.02f));
        s.Add("Gold Sphere", mirror, new Vector3(27f, 2.5f, -88f), new Vector3(4f, 4f, 4f), new Vector3(1f, 0.78f, 0.34f), ShapeType.Sphere, material: M(1f, 0.12f));
        s.Add("Copper Sphere", mirror, new Vector3(43f, 2.5f, -88f), new Vector3(4f, 4f, 4f), new Vector3(0.96f, 0.62f, 0.46f), ShapeType.Sphere, material: M(1f, 0.3f));
        s.Add("Glass Block", mirror, new Vector3(29f, 2f, -81f), new Vector3(3f, 3f, 3f), new Vector3(0.75f, 0.9f, 1f),
            material: MaterialPresets.Find("Glass")!.Apply(SurfaceMaterial.Default, new Vector3(0.75f, 0.9f, 1f)));
        s.Add("Smooth Plastic", mirror, new Vector3(41f, 1.75f, -81f), new Vector3(2.5f, 2.5f, 2.5f), new Vector3(0.85f, 0.12f, 0.1f), ShapeType.Sphere,
            material: MaterialPresets.Find("SmoothPlastic")!.Apply(SurfaceMaterial.Default, new Vector3(0.85f, 0.12f, 0.1f)));
        s.Add("Brushed Column", mirror, new Vector3(35f, 2.5f, -82f), new Vector3(1.5f, 4.5f, 1.5f), new Vector3(0.8f, 0.8f, 0.82f), ShapeType.Cylinder,
            material: MaterialPresets.Find("BrushedMetal")!.Apply(SurfaceMaterial.Default, new Vector3(0.8f, 0.8f, 0.82f)));

        // 12. Quality presets bench: fine shadows, corners, glow and two point lights with shadows (High and Ultra).
        var bench = new PartGroup("12 Quality Presets");
        s.Add("Bench Floor", bench, new Vector3(105f, 0.25f, -90f), new Vector3(36f, 0.5f, 30f), light, material: M(0f, 0.85f));
        s.Add("Bench Back Wall", bench, new Vector3(105f, 4.5f, -105f), new Vector3(36f, 8f, 1f), light);
        for (var i = 0; i < 14; i++)
        {
            s.Add($"Bench Slat {i + 1}", bench, new Vector3(97f + i * 1.15f, 6.5f, -88f), new Vector3(0.4f, 0.3f, 10f), new Vector3(0.45f, 0.4f, 0.35f));
        }

        s.Add("Bench Slat Beam A", bench, new Vector3(105f, 6.1f, -83.5f), new Vector3(17f, 0.5f, 0.5f), new Vector3(0.45f, 0.4f, 0.35f));
        s.Add("Bench Slat Beam B", bench, new Vector3(105f, 6.1f, -92.5f), new Vector3(17f, 0.5f, 0.5f), new Vector3(0.45f, 0.4f, 0.35f));
        foreach (var (x, z) in new[] { (96.5f, -83.5f), (113.5f, -83.5f), (96.5f, -92.5f), (113.5f, -92.5f) })
        {
            s.Add($"Bench Post {x:0} {z:0}", bench, new Vector3(x, 3.1f, z), new Vector3(0.5f, 5.7f, 0.5f), new Vector3(0.45f, 0.4f, 0.35f));
        }

        s.Add("Bench Crate A", bench, new Vector3(89f, 1.5f, -103f), new Vector3(2f, 2f, 2f), light);
        s.Add("Bench Crate B", bench, new Vector3(91.2f, 1.5f, -103f), new Vector3(2f, 2f, 2f), light);
        s.Add("Bench Crate C", bench, new Vector3(89f, 3.5f, -103f), new Vector3(2f, 2f, 2f), light);
        s.Add("Bench Neon", bench, new Vector3(118f, 4f, -104.4f), new Vector3(6f, 0.5f, 0.3f), dark, material: Glow(new Vector3(0.3f, 1f, 0.4f), 8f));
        s.Add("Bench Pillar A", bench, new Vector3(100f, 2.5f, -98f), new Vector3(1f, 4f, 1f), light, ShapeType.Cylinder);
        s.Add("Bench Pillar B", bench, new Vector3(110f, 2.5f, -98f), new Vector3(1f, 4f, 1f), light, ShapeType.Cylinder);
        s.Light("Bench Point Warm", bench, new Vector3(102f, 3f, -96f), LightType.Point, new Vector3(1f, 0.6f, 0.3f), 60f, 12f, shadows: true);
        s.Light("Bench Point Cool", bench, new Vector3(112f, 3f, -96f), LightType.Point, new Vector3(0.35f, 0.6f, 1f), 60f, 12f, shadows: true);
        return s.Parts;
    }

    // ------------------------------------------------------------------------------------------------- textures

    /// <summary>Generated textures (deterministic): brick (colour + normal map), planks, grass.</summary>
    public static void WriteTextures(string projectRoot)
    {
        var folder = Path.Combine(projectRoot, "Assets", "Textures");
        Directory.CreateDirectory(folder);
        const int size = 256;
        var brick = new byte[size * size * 4];
        var brickNormal = new byte[size * size * 4];
        var planks = new byte[size * size * 4];
        var grass = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;

                // Bricks 64 x 32 with 4-pixel mortar, every other row shifted half a brick.
                var row = y / 32;
                var bx = (x + (row % 2) * 32) % 64;
                var by = y % 32;
                var mortar = bx < 4 || by < 4;
                var brickId = row * 8 + (x + (row % 2) * 32) / 64;
                var tone = Hash(brickId) * 0.25f;
                var n = Hash(x * 7 + y * 131) * 0.08f;
                Put(brick, i, mortar ? new Vector3(0.62f + n, 0.6f + n, 0.56f + n) : new Vector3(0.55f + tone + n, 0.22f + tone * 0.5f + n, 0.16f + n));

                // Normal map (OpenGL, +Y up the image): the mortar is a groove; brick edges slope down into it.
                var dx = mortar ? 0f : bx < 7 ? -0.6f : bx > 60 ? 0.6f : 0f;
                var dy = mortar ? 0f : by < 7 ? 0.6f : by > 28 ? -0.6f : 0f;
                var normal = new Vector3(dx + (Hash(x * 3 + y * 17) - 0.5f) * 0.12f, dy, 1f).Normalized();
                Put(brickNormal, i, new Vector3(normal.X * 0.5f + 0.5f, normal.Y * 0.5f + 0.5f, normal.Z * 0.5f + 0.5f));

                // Planks 32 wide with a wavy grain.
                var plank = x / 32;
                var grain = 0.5f + 0.5f * MathF.Sin((x + MathF.Sin(y * 0.05f + plank) * 6f) * 0.9f);
                var gap = x % 32 < 2;
                var shade = Hash(plank * 13) * 0.15f;
                Put(planks, i, gap ? new Vector3(0.18f, 0.12f, 0.08f) : new Vector3(0.55f + shade + grain * 0.08f, 0.38f + shade * 0.7f + grain * 0.05f, 0.22f + grain * 0.03f));

                // Grass: green with blade-sized speckles.
                var speckle = Hash(x * 31 + y * 57);
                Put(grass, i, new Vector3(0.22f + speckle * 0.12f, 0.45f + speckle * 0.2f, 0.15f + speckle * 0.06f));
            }
        }

        File.WriteAllBytes(Path.Combine(folder, "brick.png"), PngEncoder.EncodeRgba(brick, size, size));
        File.WriteAllBytes(Path.Combine(folder, "brick_normal.png"), PngEncoder.EncodeRgba(brickNormal, size, size));
        File.WriteAllBytes(Path.Combine(folder, "planks.png"), PngEncoder.EncodeRgba(planks, size, size));
        File.WriteAllBytes(Path.Combine(folder, "grass.png"), PngEncoder.EncodeRgba(grass, size, size));
    }

    private static void Put(byte[] rgba, int i, Vector3 color)
    {
        rgba[i] = (byte)global::System.Math.Clamp(MathF.Round(color.X * 255f), 0f, 255f);
        rgba[i + 1] = (byte)global::System.Math.Clamp(MathF.Round(color.Y * 255f), 0f, 255f);
        rgba[i + 2] = (byte)global::System.Math.Clamp(MathF.Round(color.Z * 255f), 0f, 255f);
        rgba[i + 3] = 255;
    }

    // A repeatable pseudo-random number in [0, 1) for an integer.
    private static float Hash(int n)
    {
        unchecked
        {
            var h = (uint)n * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
