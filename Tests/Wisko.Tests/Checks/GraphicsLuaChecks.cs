using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Graphics V1 for Lua, played headless through PlayModeController: object.Material (values, names, ranges, presets,
/// textures), object.Light with AddLight / RemoveLight, the Graphics global and the Atmosphere's new environment
/// members, then Stop bringing the scene and the project's settings back. A model's materials through the same API.
/// </summary>
internal static class GraphicsLuaChecks
{
    private const string Probe = """
        function Start()
          local box = Scene.Find("Box")
          local m = box.Material
          chk("material_default_lit", m.Shading == "Lit" and m.Metallic == 0)
          m.Metallic = 1
          chk("metallic_set", m.Metallic == 1)
          chk("metallic_range_refused", not pcall(function() m.Metallic = 2 end))
          chk("metallic_nan_refused", not pcall(function() m.Metallic = 0 / 0 end))
          m.Shading = "toon"
          chk("shading_by_name_any_case", m.Shading == "Toon")
          chk("shading_number_refused", not pcall(function() m.Shading = "3" end))
          m.EmissiveColor = "#FF8800"
          m.EmissiveIntensity = 4
          chk("emission", m.EmissiveColor == "#FF8800" and m.EmissiveIntensity == 4)
          chk("colour_format_refused", not pcall(function() m.EmissiveColor = "orange" end))
          local okMember, memberError = pcall(function() return m.Shininess end)
          chk("unknown_member_named", not okMember and string.find(memberError, "Shininess is not a valid member of Material", 1, true) ~= nil)
          m:ApplyPreset("Metal")
          chk("preset_metal", m.Preset == "Metal" and m.Metallic == 1)
          chk("preset_unknown_refused", not pcall(function() m:ApplyPreset("Gold") end))
          chk("preset_needs_colon", not pcall(function() m.ApplyPreset("Metal") end))
          m.BaseColorTexture = "Assets/Textures/brick.png"
          chk("texture_path", m.BaseColorTexture == "Assets/Textures/brick.png")
          chk("texture_outside_refused", not pcall(function() m.BaseColorTexture = "../secret.png" end))
          m.BaseColorTexture = nil
          chk("texture_cleared", m.BaseColorTexture == nil)
          chk("material_not_replaceable", not pcall(function() box.Material = {} end))
          m.AlphaMode = "Blend"
          m.Opacity = 0.4
          chk("transparency", m.AlphaMode == "Blend" and math.abs(m.Opacity - 0.4) < 1e-6)

          chk("no_light_is_nil", box.Light == nil)
          local light = box:AddLight("Spot")
          chk("addlight_returns_light", light ~= nil and light.Type == "Spot" and box.Light.Type == "Spot")
          light.Intensity = 250
          light.Color = "#3366FF"
          light.Range = 30
          light.Angle = 45
          chk("light_values", box.Light.Intensity == 250 and box.Light.Color == "#3366FF" and box.Light.Range == 30 and box.Light.Angle == 45)
          chk("light_range_refused", not pcall(function() light.Range = -5 end))
          light.Type = "Point"
          chk("light_type_change_keeps_colour", box.Light.Type == "Point" and box.Light.Color == "#3366FF")
          box:RemoveLight()
          chk("removelight", box.Light == nil)
          chk("removed_light_view_refused", not pcall(function() return light.Intensity end))
          chk("addlight_type_refused", not pcall(function() box:AddLight("Laser") end))
          box:AddLight()
          chk("addlight_default_point", box.Light.Type == "Point")

          chk("graphics_renderer", Graphics.Renderer == "Universal")
          Graphics.Bloom = true
          Graphics.ToneMapping = "Filmic"
          Graphics.Quality = "Low"
          chk("graphics_values", Graphics.Bloom == true and Graphics.ToneMapping == "Filmic" and Graphics.Quality == "Low")
          chk("graphics_renderer_read_only", not pcall(function() Graphics.Renderer = "Classic" end))
          chk("graphics_unknown_refused", not pcall(function() Graphics.Fancy = true end))
          chk("graphics_range_refused", not pcall(function() Graphics.BloomIntensity = 99 end))

          Atmosphere.AmbientMode = "Flat"
          Atmosphere.GroundColor = "#203040"
          Atmosphere.ReflectionIntensity = 0.5
          chk("atmosphere_environment_members", Atmosphere.AmbientMode == "Flat" and Atmosphere.GroundColor == "#203040"
            and Atmosphere.ReflectionIntensity == 0.5)
        end
        """;

    private static readonly string[] Expected =
    [
        "material_default_lit", "metallic_set", "metallic_range_refused", "metallic_nan_refused", "shading_by_name_any_case",
        "shading_number_refused", "emission", "colour_format_refused", "unknown_member_named", "preset_metal", "preset_unknown_refused",
        "preset_needs_colon", "texture_path", "texture_outside_refused", "texture_cleared", "material_not_replaceable", "transparency",
        "no_light_is_nil", "addlight_returns_light", "light_values", "light_range_refused", "light_type_change_keeps_colour", "removelight",
        "removed_light_view_refused", "addlight_type_refused", "addlight_default_point", "graphics_renderer", "graphics_values",
        "graphics_renderer_read_only", "graphics_unknown_refused", "graphics_range_refused", "atmosphere_environment_members"
    ];

    public static void Run(TestReport report, string dir)
    {
        var root = GameplayV5Checks.Project(dir, ("ProbeGraphics", Probe));
        var workspace = new Workspace();
        var box = GameplayV5Checks.Add(workspace, "Box", new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 4f), true);
        GameplayV5Checks.AddProbe(workspace, "ProbeGraphics");
        var session = GameplayV5Checks.Begin(root, workspace, graphics: GraphicsSettings.Default);
        GameplayV5Checks.Run(session, 3);
        AssertLuaChecks(report, "Graphics Lua", session.Log, Expected);
        AssertNoFaults(report, "Graphics Lua", session.Faults);
        report.Check("Graphics Lua: while playing, the part has what the script gave it (Metal, see-through, a point light)",
            box is { Material: { Metallic: 1f, AlphaMode: AlphaMode.Blend }, Light.Type: LightType.Point });
        report.Check("Graphics Lua: while playing, the session's settings are drawn (bloom on, Filmic, Low)",
            session.Play.PlayGraphics is { BloomEnabled: true, ToneMapping: ToneMapping.Filmic, Quality: QualityLevel.Low });
        GameplayV5Checks.End(session, workspace);
        report.Check("Graphics Lua: Stop brings the part back (no material, no light) and drops the session's settings",
            box.Material is null && box.Light is null && session.Play.PlayGraphics is null);

        ModelMaterials(report);
    }

    // On a model, Material writes every material (each from its imported look, its textures kept); Reset drops them.
    private static void ModelMaterials(TestReport report)
    {
        using var importer = new AssimpModelImporter();
        var file = GraphicsDemo.ModelFile("WaterBottle");
        var asset = new ModelAsset("Assets/Models/WaterBottle/WaterBottle.glb", importer.Import(file));
        var part = GraphicsDemo.ModelPart("Bottle", asset, Vector3.Zero, 10f);
        var read = ScriptGraphics.GetMaterial(part, "BaseColorTexture", out _) as string;
        var error = ScriptGraphics.SetMaterial(part, "Roughness", 0.9);
        var overrides = part.ModelMaterials;
        report.Check("Graphics Lua: a model's Material reads its first material as imported (its own image)",
            read is not null && read.StartsWith(SurfaceMaterial.ModelTexturePrefix, StringComparison.Ordinal), read);
        report.Check("Graphics Lua: a model's Material write changes every material, keeping their textures",
            error is null && overrides is { Count: 2 } && overrides.Entries.All(e => e.Material.Roughness == 0.9f)
            && overrides.Get(0)?.BaseColorTexture == read, error ?? $"{overrides?.Count} materials");
        ScriptGraphics.ResetMaterial(part);
        report.Check("Graphics Lua: Material:Reset() on a model goes back to the imported materials", part.ModelMaterials is null);
    }
}
