using Wisko.Engine.Atmosphere;
using Wisko.Engine.Editor;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// Graphics V1 data (no GPU): colour spaces, the material model and presets, lights, model material overrides, the
/// project's Graphics.json, editor preferences, Scene.json persistence, snapshots and copies, and the Atmosphere's new
/// environment-lighting fields.
/// </summary>
internal static class GraphicsDataChecks
{
    public static void ColorsAndMaterials(TestReport report)
    {
        // sRGB <-> linear: every 8-bit value survives the round trip; mid grey is ~0.214 linear (no double gamma).
        var worst = 0f;
        for (var i = 0; i <= 255; i++)
        {
            var srgb = i / 255f;
            worst = MathF.Max(worst, MathF.Abs(ColorSpace.LinearToSrgb(ColorSpace.SrgbToLinear(srgb)) - srgb));
        }

        report.Check("Color space: sRGB -> linear -> sRGB round trip within 1/1000 for all 256 levels", worst < 1e-3f, $"worst {worst:0.000000}");
        report.Check("Color space: sRGB 0.5 is linear 0.214 (IEC 61966-2-1)", MathF.Abs(ColorSpace.SrgbToLinear(0.5f) - 0.2140f) < 5e-4f,
            $"{ColorSpace.SrgbToLinear(0.5f):0.0000}");
        report.Check("Color space: 0 and 1 are fixed points, NaN becomes 0",
            ColorSpace.SrgbToLinear(0f) == 0f && MathF.Abs(ColorSpace.SrgbToLinear(1f) - 1f) < 1e-6f && ColorSpace.SrgbToLinear(float.NaN) == 0f);
        report.Check("Color space: hex parse and format",
            ColorSpace.TryParseHex("#FF8000", out var orange) && ColorSpace.ToHex(orange) == "#FF8000" && !ColorSpace.TryParseHex("#GG0000", out _)
            && !ColorSpace.TryParseHex("#FFF", out _));

        // The material model: defaults, sanitizing, texture references.
        var d = SurfaceMaterial.Default;
        report.Check("Material: defaults (Lit, white, opaque, rough 0.55, dielectric, casts and receives shadows)",
            d.Mode == ShadingMode.Lit && d.BaseColor.Equals(new Vector3(1, 1, 1)) && d.AlphaMode == AlphaMode.Opaque && d.Roughness == 0.55f
            && d.Metallic == 0f && d.CastShadows && d.ReceiveShadows && !d.HasAnyTexture && !d.IsEmissive);
        var wild = new SurfaceMaterial
        {
            Metallic = 4f, Roughness = float.NaN, Opacity = -1f, EmissiveIntensity = float.PositiveInfinity, ToonBands = 99,
            BaseColor = new Vector3(0.123456f, 2f, -1f), Mode = (ShadingMode)42, UvScaleX = 0f
        }.Sanitized();
        report.Check("Material: out-of-range values clamped, non-finite ones take the default, colours snapped to 0-255",
            wild.Metallic == 1f && wild.Roughness == d.Roughness && wild.Opacity == 0f && wild.EmissiveIntensity == d.EmissiveIntensity
            && wild.ToonBands == 8 && wild.Mode == ShadingMode.Lit && wild.BaseColor.Equals(ColorSpace.Snap(new Vector3(0.123456f, 1f, 0f)))
            && wild.UvScaleX == 0.01f,
            $"{wild}");
        report.Check("Material: sanitizing is idempotent", wild.Sanitized() == wild);
        report.Check("Material: texture paths stay inside the project",
            SurfaceMaterial.CleanTexturePath("Assets\\Textures\\Bricks.png") == "Assets/Textures/Bricks.png"
            && SurfaceMaterial.CleanTexturePath("../outside.png") is null
            && SurfaceMaterial.CleanTexturePath("Assets/../../x.png") is null
            && SurfaceMaterial.CleanTexturePath("C:/Windows/x.png") is null
            && SurfaceMaterial.CleanTexturePath("/etc/x.png") is null
            && SurfaceMaterial.CleanTexturePath("@model:2") == "@model:2"
            && SurfaceMaterial.CleanTexturePath("@model:-1") is null
            && SurfaceMaterial.CleanTexturePath("@model:x") is null
            && SurfaceMaterial.CleanTexturePath("  ") is null);
        report.Check("Material: colour textures are sRGB, data textures linear",
            SurfaceMaterial.IsColorTexture(TextureSlot.BaseColor) && SurfaceMaterial.IsColorTexture(TextureSlot.Emissive)
            && !SurfaceMaterial.IsColorTexture(TextureSlot.Normal) && !SurfaceMaterial.IsColorTexture(TextureSlot.MetallicRoughness)
            && !SurfaceMaterial.IsColorTexture(TextureSlot.Occlusion));

        // Presets keep colour and textures; Neon glows in the object's colour.
        var textured = d with { BaseColorTexture = "Assets/Textures/A.png", NormalTexture = "Assets/Textures/N.png", Roughness = 0.1f, Mode = ShadingMode.Toon };
        var metal = MaterialPresets.Find("metal")!.Apply(textured, new Vector3(1, 0, 0));
        report.Check("Material presets: Metal sets metal + polish, keeps textures, resets the shading mode",
            metal.Metallic == 1f && metal.Roughness == 0.25f && metal.BaseColorTexture == "Assets/Textures/A.png"
            && metal.NormalTexture == "Assets/Textures/N.png" && metal.Mode == ShadingMode.Lit);
        var neon = MaterialPresets.Find("Neon")!.Apply(d, new Vector3(0.2f, 0.8f, 1f));
        report.Check("Material presets: Neon emits the object's own colour",
            neon.IsEmissive && neon.EmissiveColor.Equals(ColorSpace.Snap(new Vector3(0.2f, 0.8f, 1f))) && neon.EmissiveIntensity > 1f);
        report.Check("Material presets: Glass is see-through and casts no shadow",
            MaterialPresets.Find("Glass")!.Apply(d, Vector3.Zero) is { AlphaMode: AlphaMode.Blend, Opacity: < 1f, CastShadows: false });
        report.Check("Material presets: every preset gives a sanitized material",
            MaterialPresets.All.All(p => p.Apply(d, new Vector3(0.5f, 0.5f, 0.5f)).Sanitized() == p.Apply(d, new Vector3(0.5f, 0.5f, 0.5f)).Sanitized()));

        // Model material overrides: immutable, value equality, null == empty.
        var set = MaterialOverrideSet.Empty.With(3, d with { Metallic = 1f }).With(1, d with { Roughness = 0.1f });
        report.Check("Model material overrides: kept in index order with value equality",
            set.Count == 2 && set.Entries[0].Index == 1 && set.Get(3)?.Metallic == 1f
            && set.Equals(MaterialOverrideSet.Empty.With(1, d with { Roughness = 0.1f }).With(3, d with { Metallic = 1f })));
        report.Check("Model material overrides: removing one, out-of-range indices ignored, null equals empty",
            set.With(3, null).Count == 1 && set.With(-1, d).Count == 2 && set.With(MaterialOverrideSet.MaxMaterialIndex, d).Count == 2
            && MaterialOverrideSet.Same(null, MaterialOverrideSet.Empty) && !MaterialOverrideSet.Same(null, set));

        // Lights.
        var spot = PartLight.Create(LightType.Spot);
        report.Check("Lights: a new spot light casts shadows and has a cone",
            spot.Type == LightType.Spot && spot.CastShadows && spot.Angle > spot.InnerAngle && spot.Range > 0f);
        var badLight = new PartLight { Intensity = float.NaN, Range = 0f, Angle = 500f, InnerAngle = 400f, Color = new Vector3(3, -1, 0.5f) }.Sanitized();
        report.Check("Lights: sanitized (range minimum, angle and inner angle clamped, NaN intensity default, colour snapped)",
            badLight.Range == PartLight.MinRange && badLight.Angle == 179f && badLight.InnerAngle == 179f
            && badLight.Intensity == new PartLight().Intensity && badLight.Color.Equals(ColorSpace.Snap(new Vector3(1, 0, 0.5f))));
        report.Check("Lights: inner angle never wider than the cone",
            new PartLight { Angle = 30f, InnerAngle = 60f }.Sanitized().InnerAngle == 30f);
    }

    public static void SettingsFiles(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);

        // Quality presets: point-light shadows strictly limited, off on Low and Medium.
        var g = GraphicsSettings.Default;
        report.Check("Graphics: a new project is Universal, Medium, no bloom or vignette, neutral tone mapping, MSAA",
            g.Renderer == RendererKind.Universal && g.Quality == QualityLevel.Medium && !g.BloomEnabled && !g.VignetteEnabled
            && g.ToneMapping == ToneMapping.Neutral && g.AntiAliasing == AntiAliasing.Msaa);
        report.Check("Graphics: a project from before Graphics V1 is Classic", GraphicsSettings.LegacyDefault.Renderer == RendererKind.Classic);
        report.Check("Quality: point-light shadows off on Low and Medium, at most 1 on High and 2 on Ultra",
            g.QualityFor(QualityLevel.Low).MaxPointShadows == 0 && g.QualityFor(QualityLevel.Medium).MaxPointShadows == 0
            && g.QualityFor(QualityLevel.High).MaxPointShadows == 1 && g.QualityFor(QualityLevel.Ultra).MaxPointShadows == 2);
        report.Check("Quality: Low skips post-processing, ambient occlusion, bloom and spot shadows",
            g.QualityFor(QualityLevel.Low) is { PostProcessing: false, AmbientOcclusionSamples: 0, BloomLevels: 0, MaxSpotShadows: 0 });
        report.Check("Quality: cost grows from Low to Ultra (shadow map, cascades, lights per object)",
            g.QualityFor(QualityLevel.Low).ShadowResolution < g.QualityFor(QualityLevel.Ultra).ShadowResolution
            && g.QualityFor(QualityLevel.Low).ShadowCascades < g.QualityFor(QualityLevel.Ultra).ShadowCascades
            && g.QualityFor(QualityLevel.Low).MaxLightsPerObject < g.QualityFor(QualityLevel.Ultra).MaxLightsPerObject);
        var custom = new QualitySettings { MaxPointShadows = 9, ShadowResolution = 3000, ShadowCascades = 0, ShadowFilterSamples = 7, RenderScale = float.NaN }.Sanitized();
        report.Check("Quality: Custom values clamped (point shadows at most 2, power-of-two maps, supported sample counts)",
            custom.MaxPointShadows == 2 && custom.ShadowResolution == 2048 && custom.ShadowCascades == 1 && custom.ShadowFilterSamples == 9 && custom.RenderScale == 1f,
            $"{custom}");

        // Graphics.json: missing / invalid -> Classic; exact round trip; fields fall back on their own.
        var path = Path.Combine(dir, GraphicsFile.FileName);
        var missing = GraphicsFile.Load(path, out var status, out _);
        report.Check("Graphics.json: missing -> Classic (projects from before Graphics V1 look exactly as before)",
            status == GraphicsLoadStatus.Missing && missing.Renderer == RendererKind.Classic);
        File.WriteAllText(path, "{ not json");
        var invalid = GraphicsFile.Load(path, out status, out var problem);
        report.Check("Graphics.json: unreadable -> Classic, problem reported", status == GraphicsLoadStatus.Invalid && invalid.Renderer == RendererKind.Classic && problem is not null);
        var settings = g with
        {
            Quality = QualityLevel.Custom, BloomEnabled = true, BloomThreshold = 0.8f, ToneMapping = ToneMapping.Filmic, Temperature = -0.25f,
            Custom = QualitySettings.High with { MaxPointShadows = 1, RenderScale = 0.75f }, AntiAliasing = AntiAliasing.MsaaAndFxaa
        };
        report.Check("Graphics.json: saved", GraphicsFile.TrySave(path, settings, out var error), error);
        var loaded = GraphicsFile.Load(path, out status, out _);
        report.Check("Graphics.json: loads back exactly", status == GraphicsLoadStatus.Loaded && loaded == settings.Sanitized(), $"{loaded}");
        report.Check("Graphics.json: deterministic text (equal settings, identical files)",
            GraphicsFile.ToJson(loaded) == File.ReadAllText(path) && !File.Exists(path + ".tmp"));
        File.WriteAllText(path, "{ \"Renderer\": \"Universal\", \"Bloom\": { \"Enabled\": \"yes\", \"Intensity\": 9e9 }, \"Quality\": \"Nonsense\" }");
        var partial = GraphicsFile.Load(path, out status, out _);
        report.Check("Graphics.json: each mistyped or missing field falls back on its own",
            status == GraphicsLoadStatus.Loaded && partial.Renderer == RendererKind.Universal && !partial.BloomEnabled && partial.BloomIntensity == 5f
            && partial.Quality == QualityLevel.Medium,
            $"{partial}");
        File.WriteAllText(path, "{ \"Renderer\": \"7\" }");
        report.Check("Graphics.json: an unknown renderer value is Classic", GraphicsFile.Load(path, out _, out _).Renderer == RendererKind.Classic);

        // Editor preference (outside projects).
        var prefsPath = Path.Combine(dir, EditorGraphicsPreferences.FileName);
        report.Check("Editor preferences: missing -> follow the project", EditorGraphicsPreferences.Load(prefsPath).ViewportQuality == ViewportQuality.Project);
        report.Check("Editor preferences: saved and loaded back",
            new EditorGraphicsPreferences { ViewportQuality = ViewportQuality.Low }.TrySave(prefsPath, out _)
            && EditorGraphicsPreferences.Load(prefsPath).ViewportQuality == ViewportQuality.Low);
        report.Check("Editor preferences: a fixed viewport level overrides the project's, Project follows it",
            new EditorGraphicsPreferences { ViewportQuality = ViewportQuality.Ultra }.EffectiveLevel(QualityLevel.Low) == QualityLevel.Ultra
            && EditorGraphicsPreferences.Default.EffectiveLevel(QualityLevel.High) == QualityLevel.High);
    }

    public static void ScenePersistence(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);

        // A scene without Graphics V1 data writes no new keys (older scenes keep their exact JSON).
        var plain = new Part("Plain", new Vector3(0, 1, 0), Part.DefaultSize, Part.DefaultColor);
        var explicitDefault = new Part("Defaulted", new Vector3(5, 1, 0), Part.DefaultSize, Part.DefaultColor) { Material = SurfaceMaterial.Default };
        var json = SceneFile.ToJson([plain, explicitDefault]);
        report.Check("Scene.json: Parts without materials or lights write no Graphics V1 keys (default material included)",
            !json.Contains("\"Material\"", StringComparison.Ordinal) && !json.Contains("\"Light\"", StringComparison.Ordinal)
            && !json.Contains("\"Materials\"", StringComparison.Ordinal));

        var styled = new Part("Styled", new Vector3(1, 2, 3), new Vector3(2, 3, 4), new Vector3(1, 0, 0))
        {
            Material = new SurfaceMaterial
            {
                Mode = ShadingMode.Toon, Metallic = 0.5f, Roughness = 0.2f, EmissiveColor = new Vector3(1, 0.5f, 0), EmissiveIntensity = 3f,
                AlphaMode = AlphaMode.Mask, AlphaCutoff = 0.4f, DoubleSided = true, FlatShading = true, ToonBands = 4, Outline = true,
                OutlineColor = new Vector3(0, 0, 1), OutlineThickness = 0.1f, CastShadows = false, BaseColorTexture = "Assets/Textures/A.png",
                NormalTexture = "Assets/Textures/N.png", UvMode = UvMode.Studs, UvScaleX = 4f, UvScaleY = 2f
            }.Sanitized(),
            Light = new PartLight { Type = LightType.Spot, Intensity = 120f, Range = 33f, Angle = 50f, InnerAngle = 20f, CastShadows = true, Color = ColorSpace.Snap(new Vector3(1, 0.8f, 0.6f)) }
        };
        var lamp = new Part("Lamp", new Vector3(0, 6, 0), new Vector3(1, 1, 1), Part.DefaultColor) { Light = PartLight.Create(LightType.Point) with { Enabled = false } };
        var workspace = new Workspace();
        workspace.AddPart(plain);
        workspace.AddPart(styled);
        workspace.AddPart(lamp);
        var path = Path.Combine(dir, "Scene.json");
        report.Check("Scene.json: saved with materials and lights", SceneFile.TrySave(workspace, path, out var error), error);
        var ok = SceneFile.TryLoad(path, out var parts, out error);
        var back = parts.FirstOrDefault(p => p.Name == "Styled");
        report.Check("Scene.json: material loads back exactly", ok && back?.Material == styled.Material, $"{back?.Material}");
        report.Check("Scene.json: spot light loads back exactly", back?.Light == styled.Light, $"{back?.Light}");
        report.Check("Scene.json: a disabled default point light loads back (type and Enabled kept)",
            parts.FirstOrDefault(p => p.Name == "Lamp")?.Light == lamp.Light);
        report.Check("Scene.json: the plain Part still has no material or light",
            parts.FirstOrDefault(p => p.Name == "Plain") is { Material: null, Light: null });

        // Hand-written values: out of range, wrong type, unknown enum -> defaults/clamped, never a failed load.
        File.WriteAllText(path, """
            [ { "Name": "Odd", "ShapeType": "Block", "Position": [0,0,0], "Rotation": [0,0,0], "Size": [1,1,1], "Color": [10,20,30],
                "Material": { "Mode": "Hologram", "Metallic": 7, "Textures": { "BaseColor": "../../secret.png" } },
                "Light": { "Type": "Laser", "Range": -5 } } ]
            """);
        ok = SceneFile.TryLoad(path, out parts, out error);
        var odd = parts.FirstOrDefault();
        report.Check("Scene.json: bad Graphics values load safely (unknown mode -> Lit, metallic clamped, escaping texture dropped, unknown light -> Point)",
            ok && odd?.Material is { Mode: ShadingMode.Lit, Metallic: 1f, BaseColorTexture: null } && odd.Light is { Type: LightType.Point, Range: PartLight.MinRange },
            $"{error} {odd?.Material} {odd?.Light}");

        // Snapshots (undo, Play restore) and duplicates carry the Graphics V1 data.
        var snapshot = PartSnapshot.FromPart(styled);
        var restored = snapshot.CreatePart();
        report.Check("Snapshot: material and light carried into a recreated Part (undo of delete)",
            restored.Material == styled.Material && restored.Light == styled.Light);
        var changed = new Part("X", Vector3.Zero, Part.DefaultSize, Part.DefaultColor);
        snapshot.ApplyAll(changed);
        report.Check("Snapshot: ApplyAll restores material and light (Play Stop, undo)", changed.Material == styled.Material && changed.Light == styled.Light);
        report.Check("Snapshot: SameGraphics tells a change apart",
            snapshot.SameGraphics(PartSnapshot.FromPart(styled)) && !snapshot.SameGraphics(PartSnapshot.FromPart(plain)));
        var dupWorkspace = new Workspace();
        dupWorkspace.AddPart(styled);
        var duplicate = PartDuplicator.Duplicate(styled, dupWorkspace);
        report.Check("Duplicate: material and light copied", duplicate.Material == styled.Material && duplicate.Light == styled.Light);
    }

    public static void AtmosphereEnvironment(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var d = AtmosphereSettings.Default;
        report.Check("Atmosphere: new environment lighting defaults (Sky ambient, ground colour, reflections 1)",
            d.AmbientMode == AtmosphereAmbientMode.Sky && d.ReflectionIntensity == 1f);
        var path = Path.Combine(dir, AtmosphereFile.FileName);
        var custom = d with { AmbientMode = AtmosphereAmbientMode.Flat, GroundColor = AtmosphereColor.FromBytes(10, 20, 30), ReflectionIntensity = 0.5f };
        AtmosphereFile.TrySave(path, custom, out _);
        var back = AtmosphereFile.Load(path, out var status, out _);
        report.Check("Atmosphere.json: ambient mode, ground colour and reflection intensity round trip",
            status == AtmosphereLoadStatus.Loaded && back == custom, $"{back}");

        // A file written before Graphics V1 (no new fields) loads every older value as before plus the new defaults.
        File.WriteAllText(path, """
            { "FormatVersion": 1, "Enabled": true, "Sky": { "Color": [1,2,3], "HorizonColor": [4,5,6], "Intensity": 1.5 },
              "Ambient": { "Color": [7,8,9], "Intensity": 0.5 }, "Exposure": 1 }
            """);
        var old = AtmosphereFile.Load(path, out status, out _);
        report.Check("Atmosphere.json: a file from before Graphics V1 keeps its values and gets the new defaults",
            status == AtmosphereLoadStatus.Loaded && old.SkyIntensity == 1.5f && old.AmbientIntensity == 0.5f && old.Exposure == 1f
            && old.AmbientMode == d.AmbientMode && old.GroundColor == d.GroundColor && old.ReflectionIntensity == d.ReflectionIntensity);
        report.Check("Atmosphere: the new values use the panel's ranges",
            d.With(AtmosphereValue.ReflectionIntensity, 9f).ReflectionIntensity == 2f && d.With(AtmosphereColorSlot.Ground, AtmosphereColor.FromBytes(1, 2, 3)).GroundColor == AtmosphereColor.FromBytes(1, 2, 3));

        // Lua names (Atmosphere.AmbientMode / GroundColor / ReflectionIntensity).
        var host = new TestAtmosphereHost { Settings = d };
        report.Check("Atmosphere (Lua): AmbientMode reads and writes \"Flat\" / \"Sky\"",
            (string?)ScriptAtmosphere.Get(d, "AmbientMode") == "Sky" && ScriptAtmosphere.Set(host, "AmbientMode", "flat") is null
            && host.Settings.AmbientMode == AtmosphereAmbientMode.Flat && ScriptAtmosphere.Set(host, "AmbientMode", "Dark") is not null
            && ScriptAtmosphere.Set(host, "AmbientMode", "1") is not null);
        report.Check("Atmosphere (Lua): GroundColor and ReflectionIntensity",
            ScriptAtmosphere.Set(host, "GroundColor", "#102030") is null && (string?)ScriptAtmosphere.Get(host.Settings, "GroundColor") == "#102030"
            && ScriptAtmosphere.Set(host, "ReflectionIntensity", 0.25) is null && host.Settings.ReflectionIntensity == 0.25f
            && ScriptAtmosphere.Keys.Contains("AmbientMode") && ScriptAtmosphere.Keys.Contains("GroundColor") && ScriptAtmosphere.Keys.Contains("ReflectionIntensity"));
    }

    private sealed class TestAtmosphereHost : IScriptAtmosphereHost
    {
        public AtmosphereSettings Settings { get; set; } = AtmosphereSettings.Default;
    }
}
