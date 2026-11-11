using Wisko.Engine.Assets;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Editor;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;

namespace Wisko.Tests.Checks;

/// <summary>
/// Graphics V1 editor logic without a window: the Materials tab's and the rendering settings' rows and what each edit does (sliders,
/// choices, switches, presets, lights), and bringing a texture image into a project (copy, reuse, refusals).
/// </summary>
internal static class GraphicsEditingChecks
{
    public static void Sheets(TestReport report)
    {
        var spec = new SliderSpec(0f, 100f, 2.5f, 0.01f);
        report.Check("Sliders: value and position round-trip, ends and steps hold",
            spec.ValueAt(0f) == 0f && spec.ValueAt(1f) == 100f && MathF.Abs(spec.ValueAt(spec.FractionOf(4f)) - 4f) < 0.011f
            && spec.ValueAt(float.NaN) == 0f && spec.ValueAt(5f) == 100f);

        var m = SurfaceMaterial.Default;
        var white = new Vector3(1f, 1f, 1f);
        report.Check("Material edits: Metallic slider, Shading next / option, Transparency Blend starts half see-through",
            MaterialSheet.Slide(m, "m.metallic", 1f).Metallic == 1f
            && MaterialSheet.Click(m, white, "m.mode", SheetHitPart.Next, -1).Mode == ShadingMode.SimpleLit
            && MaterialSheet.Click(m, white, "m.mode", SheetHitPart.Previous, -1).Mode == ShadingMode.Toon
            && MaterialSheet.Click(m, white, "m.mode", SheetHitPart.Option, 2).Mode == ShadingMode.Unlit
            && MaterialSheet.Click(m, white, "m.alpha", SheetHitPart.Option, 2) is { AlphaMode: AlphaMode.Blend, Opacity: 0.5f });
        var metal = MaterialSheet.Click(m, white, "m.preset", SheetHitPart.Option, MaterialPresets.All.ToList().FindIndex(p => p.Name == "Metal"));
        report.Check("Material edits: presets step through the list and apply (Metal: metallic 1)",
            metal.Metallic == 1f && MaterialSheet.Click(metal, white, "m.preset", SheetHitPart.Next, -1) != metal);
        report.Check("Material edits: switches flip (outline, shadows), colours set (glow), textures by slot",
            MaterialSheet.Click(m, white, "m.outline", SheetHitPart.Toggle, -1).Outline
            && !MaterialSheet.Click(m, white, "m.castShadows", SheetHitPart.Toggle, -1).CastShadows
            && MaterialSheet.SetColor(m, "m.emissive", new Vector3(1f, 0f, 0f)).EmissiveColor.X == 1f
            && MaterialSheet.SlotOf("m.tex.Normal") == TextureSlot.Normal && MaterialSheet.SlotOf("m.metallic") is null);

        var spot = PartLight.Create(LightType.Spot);
        report.Check("Light edits: intensity and cone sliders (inner never wider than the cone), type change keeps the colour",
            MaterialSheet.SlideLight(spot, "l.intensity", 1f).Intensity == 10000f
            && MaterialSheet.SlideLight(spot with { InnerAngle = 50f }, "l.angle", 0f) is { Angle: 1f, InnerAngle: 1f }
            && MaterialSheet.ClickLight(spot with { Color = new Vector3(1f, 0f, 0f) }, "l.type", SheetHitPart.Option, 0) is { Type: LightType.Point, Color.X: 1f });

        static MaterialSheetTarget Target(SurfaceMaterial material, PartLight? light = null, bool model = false) =>
            new("Box", model, material, new Vector3(0.5f, 0.5f, 0.5f), model ? ["A", "B"] : [], 0, false, light, true);
        var unlitRows = MaterialSheet.Rows(Target(m with { Mode = ShadingMode.Unlit }), null, true, false);
        report.Check("Material rows: Unlit greys out Metallic and Roughness; no light shows Add buttons; a model lists its materials",
            unlitRows.Any(r => r.Key == "m.metallic" && !r.Enabled) && unlitRows.Any(r => r.Key == "l.add")
            && MaterialSheet.Rows(Target(m, model: true), null, true, false).Any(r => r.Key == "m.index" && r.Options.Count == 2));
        report.Check("Material rows: a spot light shows its cone, a directional light no range or shadows; Play makes it all read-only",
            MaterialSheet.Rows(Target(m, spot), null, true, false).Any(r => r.Key == "l.angle")
            && !MaterialSheet.Rows(Target(m, PartLight.Create(LightType.Directional)), null, true, false).Any(r => r.Key is "l.range" or "l.shadows")
            && MaterialSheet.Rows(Target(m, spot), null, true, true).All(r => !r.Enabled));
        report.Check("Material rows: the open colour gets Hue / Saturation / Brightness rows",
            MaterialSheet.Rows(Target(m), "m.color", true, false).Count(r => r.Key == "m.color") == 4);

        var g = GraphicsSettings.Default;
        var high = RenderingSheet.Click(g, "r.quality", SheetHitPart.Option, (int)QualityLevel.High);
        var custom = RenderingSheet.Click(high, "r.quality", SheetHitPart.Option, (int)QualityLevel.Custom);
        report.Check("Rendering edits: quality levels; Custom starts as a copy of the level the project had",
            high.Quality == QualityLevel.High && custom.Quality == QualityLevel.Custom && custom.Custom == QualitySettings.High);
        report.Check("Rendering edits: switches, tone mapping, sliders, Custom values; Reset keeps the renderer",
            RenderingSheet.Click(g, "r.bloom", SheetHitPart.Toggle, -1).BloomEnabled
            && RenderingSheet.Click(g, "r.tone", SheetHitPart.Option, 2).ToneMapping == ToneMapping.Filmic
            && RenderingSheet.Slide(g, "r.shadowDistance", 1f).ShadowDistance == 2000f
            && RenderingSheet.Slide(custom, "q.pointShadows", 1f).Custom.MaxPointShadows == 2
            && RenderingSheet.Click(custom, "q.shadowSize", SheetHitPart.Option, 3).Custom.ShadowResolution == 4096
            && RenderingSheet.Click(g with { Renderer = RendererKind.Classic, BloomEnabled = true }, "r.reset", SheetHitPart.Button, 0)
                is { Renderer: RendererKind.Classic, BloomEnabled: false });
        static List<SheetRow> GlobalRows(GraphicsSettings settings, AtmosphereSettings? atmosphere = null, bool readOnly = false) =>
            new[] { EnvironmentTab.Atmosphere, EnvironmentTab.Lighting, EnvironmentTab.Rendering, EnvironmentTab.PostProcessing }
                .SelectMany(tab => EnvironmentSheet.Rows(tab, atmosphere ?? AtmosphereSettings.Default, settings, EditorGraphicsPreferences.Default, RendererCapabilities.Full, null, readOnly))
                .ToList();
        report.Check("Rendering rows: Custom shows its values; Classic greys out the Universal-only settings (the Atmosphere's stay editable)",
            EnvironmentSheet.Rows(EnvironmentTab.Rendering, AtmosphereSettings.Default, custom, EditorGraphicsPreferences.Default, RendererCapabilities.Full, null, false).Any(r => r.Key == "q.shadowSize")
            && GlobalRows(g with { Renderer = RendererKind.Classic })
                .Where(r => r.Key.StartsWith("r.", StringComparison.Ordinal) && r.Key is not ("r.renderer" or "r.reset")).All(r => !r.Enabled)
            && GlobalRows(g with { Renderer = RendererKind.Classic }).Where(r => EnvironmentSheet.IsAtmosphere(r.Key)).All(r => r.Enabled));
        report.Check("Rendering: the 3D View preference steps through As Project, Low ... Ultra",
            RenderingSheet.ClickPreference(EditorGraphicsPreferences.Default, "e.viewport", SheetHitPart.Option, 1).ViewportQuality == ViewportQuality.Low
            && RenderingSheet.ClickPreference(EditorGraphicsPreferences.Default, "e.viewport", SheetHitPart.Previous, -1).ViewportQuality == ViewportQuality.Ultra);
    }

    /// <summary>
    /// The Environment &amp; Graphics panel holds every setting the Atmosphere and Rendering panels had (their keys, listed
    /// here as they were), each in exactly one tab and the tab the request names; its Atmosphere edits are the old panel's.
    /// </summary>
    public static void EnvironmentPanel(TestReport report)
    {
        string[] atmosphereKeys =
        [
            "a.enabled", "a.fog", "a.ambientSky", "a.reset",
            .. AtmosphereSettings.Values.Select(EnvironmentSheet.ValueKey),
            .. AtmosphereSettings.ColorSlots.Select(EnvironmentSheet.ColorKey)
        ];
        string[] renderingKeys =
        [
            "r.renderer", "r.quality", "q.post", "q.shadowSize", "q.cascades", "q.filter", "q.spotShadows", "q.pointShadows", "q.pointSize",
            "q.aoSamples", "q.bloomLevels", "q.reflections", "q.lights", "q.renderScale", "e.viewport", "r.tone", "r.aa", "r.temperature", "r.tint",
            "r.bloom", "r.bloomThreshold", "r.bloomIntensity", "r.bloomRadius", "r.vignette", "r.vignetteIntensity", "r.ao", "r.aoRadius",
            "r.aoIntensity", "r.shadows", "r.shadowDistance", "r.shadowBias", "r.shadowNormalBias", "r.pointShadows", "r.reset"
        ];

        // Every optional row shown: Custom quality, bloom, vignette and ambient occlusion on, Sky ambient (Ground Color).
        var graphics = GraphicsSettings.Default with
        {
            Renderer = RendererKind.Universal, Quality = QualityLevel.Custom, Custom = QualitySettings.High, BloomEnabled = true, VignetteEnabled = true,
            AmbientOcclusionEnabled = true
        };
        var atmosphere = AtmosphereSettings.Default with { AmbientMode = AtmosphereAmbientMode.Sky };
        var tabs = new[] { EnvironmentTab.Atmosphere, EnvironmentTab.Lighting, EnvironmentTab.Rendering, EnvironmentTab.PostProcessing };
        var keysOf = tabs.ToDictionary(tab => tab, tab => EnvironmentSheet.Rows(tab, atmosphere, graphics, EditorGraphicsPreferences.Default, RendererCapabilities.Full, null, false)
            .Where(r => r.Key.Length > 0).Select(r => r.Key).ToList());
        var all = keysOf.Values.SelectMany(k => k).ToList();
        var expected = atmosphereKeys.Concat(renderingKeys).ToHashSet();
        report.Check("Environment & Graphics: every Atmosphere and Rendering panel setting is in the panel, each exactly once (none lost, none doubled)",
            all.Count == expected.Count && all.ToHashSet().SetEquals(expected),
            $"{all.Count} rows / {expected.Count} settings; missing [{string.Join(", ", expected.Except(all))}]; extra [{string.Join(", ", all.Except(expected))}]; "
                + $"doubled [{string.Join(", ", all.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key))}]");

        static bool Exactly(List<string> keys, params string[] wanted) => keys.ToHashSet().SetEquals(wanted);
        report.Check("Environment & Graphics: Atmosphere = sky, sun, ambient light, fog, haze (+ on/off, Reset)",
            Exactly(keysOf[EnvironmentTab.Atmosphere], "a.enabled", "a.c.Sky", "a.c.Horizon", "a.v.SkyIntensity", "a.c.Sun", "a.v.SunIntensity",
                "a.v.SunElevation", "a.v.SunHeading", "a.v.SunDiscSize", "a.c.Ambient", "a.v.AmbientIntensity", "a.fog", "a.c.Fog", "a.v.FogDensity",
                "a.v.FogStart", "a.v.FogEnd", "a.v.Haze", "a.reset"), string.Join(", ", keysOf[EnvironmentTab.Atmosphere]));
        report.Check("Environment & Graphics: Lighting = environment light (Sky Ambient, Ground, Reflections) and shadows",
            Exactly(keysOf[EnvironmentTab.Lighting], "a.ambientSky", "a.c.Ground", "a.v.ReflectionIntensity", "r.shadows", "r.shadowDistance", "r.shadowBias",
                "r.shadowNormalBias", "r.pointShadows"), string.Join(", ", keysOf[EnvironmentTab.Lighting]));
        report.Check("Environment & Graphics: Rendering = renderer, quality (and Custom values), anti-aliasing, 3D View, Reset",
            Exactly(keysOf[EnvironmentTab.Rendering], [.. renderingKeys.Where(k => k.StartsWith("q.", StringComparison.Ordinal)), "r.renderer", "r.quality", "r.aa", "e.viewport", "r.reset"]),
            string.Join(", ", keysOf[EnvironmentTab.Rendering]));
        report.Check("Environment & Graphics: Post-Processing = exposure, tone mapping, white balance, saturation, contrast, bloom, ambient occlusion, vignette",
            Exactly(keysOf[EnvironmentTab.PostProcessing], "a.v.Exposure", "r.tone", "r.temperature", "r.tint", "a.v.Saturation", "a.v.Contrast", "r.bloom",
                "r.bloomThreshold", "r.bloomIntensity", "r.bloomRadius", "r.ao", "r.aoRadius", "r.aoIntensity", "r.vignette", "r.vignetteIntensity"),
            string.Join(", ", keysOf[EnvironmentTab.PostProcessing]));

        // The Atmosphere panel's own arithmetic (min + fraction x range, then the Atmosphere's clamp and step) and labels.
        var d = AtmosphereSettings.Default;
        var sameSlides = AtmosphereSettings.Values.All(value => new[] { 0f, 0.37f, 0.8f, 1f }.All(f =>
        {
            var range = AtmosphereSettings.RangeOf(value);
            return EnvironmentSheet.Slide(d, EnvironmentSheet.ValueKey(value), f) == d.With(value, range.Min + f * (range.Max - range.Min));
        }));
        report.Check("Environment & Graphics: Atmosphere sliders set exactly what the Atmosphere panel's did (every value, ends and middle)", sameSlides);
        report.Check("Environment & Graphics: Atmosphere switches and their undo labels as before (Fog On, Off, Flat Ambient)",
            EnvironmentSheet.Click(d with { FogEnabled = false }, "a.fog") is { Settings.FogEnabled: true, Label: "Fog On" }
            && EnvironmentSheet.Click(d, "a.enabled") is { Settings.Enabled: false, Label: "Off" }
            && EnvironmentSheet.Click(d with { AmbientMode = AtmosphereAmbientMode.Sky }, "a.ambientSky") is { Settings.AmbientMode: AtmosphereAmbientMode.Flat, Label: "Flat Ambient" }
            && EnvironmentSheet.Click(d, "a.v.Haze") is null);
        static string Shown(AtmosphereValue value, float amount) => EnvironmentSheet.SpecOf(value).Text(amount);
        report.Check("Environment & Graphics: Atmosphere values read as before (+0.60 EV, -1.00 EV, 0.00 EV, 45°, 15, 0.25)",
            Shown(AtmosphereValue.Exposure, 0.6f) == "+0.60 EV" && Shown(AtmosphereValue.Exposure, -1f) == "-1.00 EV" && Shown(AtmosphereValue.Exposure, 0f) == "0.00 EV"
            && Shown(AtmosphereValue.SunElevation, 45f) == "45°" && Shown(AtmosphereValue.FogStart, 15f) == "15" && Shown(AtmosphereValue.Haze, 0.25f) == "0.25",
            $"{Shown(AtmosphereValue.Exposure, 0.6f)} {Shown(AtmosphereValue.Exposure, -1f)} {Shown(AtmosphereValue.Exposure, 0f)} {Shown(AtmosphereValue.SunElevation, 45f)}");

        var off = tabs.SelectMany(tab => EnvironmentSheet.Rows(tab, d with { Enabled = false }, graphics, EditorGraphicsPreferences.Default, RendererCapabilities.Full, null, false))
            .Where(r => EnvironmentSheet.IsAtmosphere(r.Key)).ToList();
        report.Check("Environment & Graphics: with the Atmosphere off its values are muted but still editable (the switch and Reset are not muted)",
            off.Where(r => r.Key is not ("a.enabled" or "a.reset")).All(r => r is { Muted: true, Enabled: true }) && off.Where(r => r.Key is "a.enabled" or "a.reset").All(r => !r.Muted));
        var playing = tabs.Select(tab => EnvironmentSheet.Rows(tab, d, graphics, EditorGraphicsPreferences.Default, RendererCapabilities.Full, null, true)).ToList();
        report.Check("Environment & Graphics: in Play every tab says so first and is read-only (except this computer's 3D View preference)",
            playing.All(rows => rows[0].Kind == SheetRowKind.Note && rows[0].Label.StartsWith("Stop Play Mode", StringComparison.Ordinal))
            && playing.SelectMany(rows => rows).Where(r => r.Key.Length > 0 && r.Key != "e.viewport").All(r => !r.Enabled)
            && playing.SelectMany(rows => rows).Single(r => r.Key == "e.viewport").Enabled);
        var expanded = EnvironmentSheet.Rows(EnvironmentTab.Atmosphere, d, graphics, EditorGraphicsPreferences.Default, RendererCapabilities.Full, EnvironmentSheet.ColorKey(AtmosphereColorSlot.Fog), false);
        report.Check("Environment & Graphics: an open Atmosphere colour gets Hue / Saturation / Brightness rows",
            expanded.Count(r => r.Key == "a.c.Fog") == 4);
    }

    public static void TextureImport(TestReport report, string dir)
    {
        var project = Path.Combine(dir, "project");
        var outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        static byte[] Png(byte value) => PngEncoder.EncodeRgba(Enumerable.Repeat(value, 4 * 4 * 4).ToArray(), 4, 4);
        File.WriteAllBytes(Path.Combine(outside, "brick.png"), Png(100));
        var first = ProjectTextureImporter.TryImport(Path.Combine(outside, "brick.png"), project, out var path1, out var error1);
        var again = ProjectTextureImporter.TryImport(Path.Combine(outside, "brick.png"), project, out var path2, out _);
        var otherPath = Path.Combine(outside, "other", "brick.png");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.WriteAllBytes(otherPath, Png(200));
        var different = ProjectTextureImporter.TryImport(otherPath, project, out var path3, out _);
        report.Check("Texture import: copied into Assets/Textures; the same image again is reused; another with that name gets -2",
            first && path1 == "Assets/Textures/brick.png" && File.Exists(Path.Combine(project, "Assets", "Textures", "brick.png"))
            && again && path2 == path1 && different && path3 == "Assets/Textures/brick-2.png", error1 ?? $"{path1} {path2} {path3}");

        var inside = Path.Combine(project, "Art", "wall.png");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, Png(50));
        report.Check("Texture import: an image already in the project is used where it is (no copy)",
            ProjectTextureImporter.TryImport(inside, project, out var insidePath, out _) && insidePath == "Art/wall.png"
            && !File.Exists(Path.Combine(project, "Assets", "Textures", "wall.png")));

        File.WriteAllText(Path.Combine(outside, "fake.png"), "not an image");
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "text");
        report.Check("Texture import: refuses an unreadable image, an unsupported type and a missing file (nothing copied)",
            !ProjectTextureImporter.TryImport(Path.Combine(outside, "fake.png"), project, out _, out var fakeError) && fakeError.Contains("could not be read")
            && !ProjectTextureImporter.TryImport(Path.Combine(outside, "notes.txt"), project, out _, out _)
            && !ProjectTextureImporter.TryImport(Path.Combine(outside, "missing.png"), project, out _, out _)
            && !File.Exists(Path.Combine(project, "Assets", "Textures", "fake.png")), fakeError);
    }
}
