using System.Globalization;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;

namespace Wisko.Engine.Editor;

/// <summary>A slider's range: value = Min + (Max - Min) x fraction^Curve, rounded to Step (when set), shown with Format.</summary>
internal readonly record struct SliderSpec(float Min, float Max, float Curve = 1f, float Step = 0f, string Format = "0.00", string Suffix = "")
{
    public float ValueAt(float fraction)
    {
        var f = global::System.Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f);
        var value = Min + (Max - Min) * MathF.Pow(f, Curve);
        if (Step > 0f)
        {
            value = MathF.Round(value / Step) * Step;
        }

        return global::System.Math.Clamp(value, Min, Max);
    }

    public float FractionOf(float value) =>
        Max <= Min || !float.IsFinite(value) ? 0f : MathF.Pow(global::System.Math.Clamp((value - Min) / (Max - Min), 0f, 1f), 1f / Curve);

    public string Text(float value) => value.ToString(Format, CultureInfo.InvariantCulture) + Suffix;
}

/// <summary>What the Materials tab edits: the selection's surface (a primitive's material and colour, or one material
/// of a model) and its light.</summary>
internal sealed record MaterialSheetTarget(
    string Selection,
    bool IsModel,
    SurfaceMaterial Material,
    Vector3 Color,
    IReadOnlyList<string> ModelMaterials,
    int ModelMaterialIndex,
    bool ModelMaterialEdited,
    PartLight? Light,
    bool ShowLight);

/// <summary>
/// The Materials tab's rows and edits (docs/graphics.md, "Editor"). Pure: rows are built from values, an edit
/// returns new values; the renderer applies them to the selection and records undo. Keys: "m.*" material, "l.*" light.
/// </summary>
internal static class MaterialSheet
{
    public static readonly string[] ShadingNames = ["Lit", "Simple", "Unlit", "Toon"];
    public static readonly string[] AlphaNames = ["Opaque", "Cutout", "Blend"];
    public static readonly string[] UvNames = ["Mesh", "Studs"];
    public static readonly string[] LightTypeNames = ["Point", "Spot", "Directional"];

    public static readonly (TextureSlot Slot, string Key, string Label)[] TextureRows =
    [
        (TextureSlot.BaseColor, "m.tex.BaseColor", "Color Map"),
        (TextureSlot.Normal, "m.tex.Normal", "Normal Map"),
        (TextureSlot.MetallicRoughness, "m.tex.MetallicRoughness", "Metal/Rough"),
        (TextureSlot.Occlusion, "m.tex.Occlusion", "Occlusion"),
        (TextureSlot.Emissive, "m.tex.Emissive", "Glow Map")
    ];

    public static readonly IReadOnlyDictionary<string, SliderSpec> Sliders = new Dictionary<string, SliderSpec>
    {
        ["m.metallic"] = new(0f, 1f),
        ["m.roughness"] = new(0f, 1f),
        ["m.opacity"] = new(0f, 1f),
        ["m.cutoff"] = new(0f, 1f),
        ["m.emissiveIntensity"] = new(0f, 100f, 2.5f, 0.01f, "0.00"),
        ["m.normalStrength"] = new(0f, 4f, 1.5f),
        ["m.occlusionStrength"] = new(0f, 1f),
        ["m.uvScaleX"] = new(0.05f, 20f, 2.5f, 0.05f),
        ["m.uvScaleY"] = new(0.05f, 20f, 2.5f, 0.05f),
        ["m.toonBands"] = new(2f, 8f, 1f, 1f, "0"),
        ["m.outlineThickness"] = new(0.005f, 0.5f, 2f, 0.005f, "0.000"),
        ["l.intensity"] = new(0f, 10000f, 3f, 0.1f, "0.0"),
        ["l.sunIntensity"] = new(0f, 10f, 1.5f, 0.01f),
        ["l.range"] = new(0.5f, 200f, 2f, 0.1f, "0.0"),
        ["l.angle"] = new(1f, 179f, 1f, 1f, "0", "°"),
        ["l.innerAngle"] = new(0f, 179f, 1f, 1f, "0", "°")
    };

    public static List<SheetRow> Rows(MaterialSheetTarget? target, string? expandedColor, bool universal, bool readOnly,
        RendererCapabilities? capabilities = null)
    {
        var rows = new List<SheetRow>();
        if (target is null)
        {
            rows.Add(Note("Select a part or a model to edit its"));
            rows.Add(Note("material and light."));
            return rows;
        }

        rows.Add(Note(target.Selection));
        if (readOnly)
        {
            rows.Add(Note("Stop Play Mode to edit materials and lights."));
        }

        if (capabilities is { UniversalRenderer: false, ClassicReason: { } reason })
        {
            // Nothing to switch here: the project's materials and lights are kept for where Universal draws (Windows).
            rows.Add(Note(reason + ":"));
            rows.Add(Note("materials and lights are kept for Windows."));
        }
        else if (!universal)
        {
            rows.Add(Note("Classic renderer: these show with Universal"));
            rows.Add(Note("(Environment & Graphics > Rendering)."));
        }

        var m = target.Material;
        if (target.IsModel)
        {
            rows.Add(Section("MODEL MATERIAL"));
            rows.Add(new SheetRow(SheetRowKind.Choice, "m.index", "Material")
            {
                Options = target.ModelMaterials, Selected = target.ModelMaterialIndex
            });
            rows.Add(target.ModelMaterialEdited
                ? new SheetRow(SheetRowKind.Buttons, "m.resetImported", string.Empty) { Options = ["Back to Imported"] }
                : Note("As imported (an edit makes it this instance's own)."));
        }

        rows.Add(Section("SURFACE"));
        var preset = MaterialPresets.All.ToList().FindIndex(p => p.Apply(m, target.Color) == m);
        rows.Add(new SheetRow(SheetRowKind.Choice, "m.preset", "Preset")
        {
            Options = MaterialPresets.All.Select(p => p.Name).ToArray(), Selected = preset, Value = "Custom"
        });
        rows.Add(new SheetRow(SheetRowKind.Choice, "m.mode", "Shading") { Options = ShadingNames, Selected = (int)m.Mode });
        Color(rows, "m.color", "Color", target.Color, expandedColor);
        var lit = m.Mode != ShadingMode.Unlit;
        rows.Add(Slider("m.metallic", "Metallic", m.Metallic) with { Enabled = lit });
        rows.Add(Slider("m.roughness", "Roughness", m.Roughness) with { Enabled = lit });
        rows.Add(new SheetRow(SheetRowKind.Choice, "m.alpha", "Transparency") { Options = AlphaNames, Selected = (int)m.AlphaMode });
        if (m.AlphaMode == AlphaMode.Blend)
        {
            rows.Add(Slider("m.opacity", "Opacity", m.Opacity));
        }
        else if (m.AlphaMode == AlphaMode.Mask)
        {
            rows.Add(Slider("m.cutoff", "Cutoff", m.AlphaCutoff));
        }

        rows.Add(Section("GLOW"));
        Color(rows, "m.emissive", "Glow Color", m.EmissiveColor, expandedColor);
        rows.Add(Slider("m.emissiveIntensity", "Glow Strength", m.EmissiveIntensity));

        rows.Add(Section("TEXTURES"));
        foreach (var (slot, key, label) in TextureRows)
        {
            rows.Add(new SheetRow(SheetRowKind.Texture, key, label) { Value = TextureLabel(m.GetTexture(slot)) });
        }

        if (m.NormalTexture is not null)
        {
            rows.Add(Slider("m.normalStrength", "Normal Strength", m.NormalStrength));
        }

        if (m.OcclusionTexture is not null)
        {
            rows.Add(Slider("m.occlusionStrength", "Occlusion", m.OcclusionStrength));
        }

        if (!target.IsModel)
        {
            rows.Add(new SheetRow(SheetRowKind.Choice, "m.uv", "Mapping") { Options = UvNames, Selected = (int)m.UvMode });
        }

        rows.Add(Slider("m.uvScaleX", "Tiling X", m.UvScaleX));
        rows.Add(Slider("m.uvScaleY", "Tiling Y", m.UvScaleY));

        rows.Add(Section("STYLE"));
        if (m.Mode == ShadingMode.Toon)
        {
            rows.Add(Slider("m.toonBands", "Toon Bands", m.ToonBands));
        }

        rows.Add(Toggle("m.outline", "Outline", m.Outline));
        if (m.Outline)
        {
            Color(rows, "m.outlineColor", "Outline Color", m.OutlineColor, expandedColor);
            rows.Add(Slider("m.outlineThickness", "Outline Width", m.OutlineThickness));
        }

        rows.Add(Toggle("m.flat", "Flat Shading", m.FlatShading) with { Enabled = lit });
        rows.Add(Toggle("m.doubleSided", "Double Sided", m.DoubleSided));
        rows.Add(Section("SHADOWS"));
        rows.Add(Toggle("m.castShadows", "Cast Shadows", m.CastShadows));
        rows.Add(Toggle("m.receiveShadows", "Receive Shadows", m.ReceiveShadows) with { Enabled = lit });
        rows.Add(new SheetRow(SheetRowKind.Buttons, "m.reset", string.Empty) { Options = [target.IsModel ? "Reset to Imported" : "Reset Material"] });

        if (target.ShowLight)
        {
            LightRows(rows, target.Light, expandedColor);
        }

        if (readOnly)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i] = rows[i] with { Enabled = false };
            }
        }

        return rows;
    }

    private static void LightRows(List<SheetRow> rows, PartLight? light, string? expandedColor)
    {
        rows.Add(Section("LIGHT"));
        if (light is null)
        {
            rows.Add(Note("Make this object give light:"));
            rows.Add(new SheetRow(SheetRowKind.Buttons, "l.add", string.Empty) { Options = LightTypeNames });
            return;
        }

        rows.Add(new SheetRow(SheetRowKind.Choice, "l.type", "Type") { Options = LightTypeNames, Selected = (int)light.Type });
        rows.Add(Toggle("l.enabled", "Enabled", light.Enabled));
        Color(rows, "l.color", "Light Color", light.Color, expandedColor);
        if (light.Type == LightType.Directional)
        {
            rows.Add(Slider("l.sunIntensity", "Intensity", light.Intensity));
            rows.Add(Note("Lights the whole scene along its facing."));
            rows.Add(Note("No shadows (the Atmosphere sun has them)."));
        }
        else
        {
            rows.Add(Slider("l.intensity", "Intensity", light.Intensity));
            rows.Add(Slider("l.range", "Range", light.Range));
            if (light.Type == LightType.Spot)
            {
                rows.Add(Slider("l.angle", "Cone Angle", light.Angle));
                rows.Add(Slider("l.innerAngle", "Inner Cone", light.InnerAngle));
            }

            rows.Add(Toggle("l.shadows", "Cast Shadows", light.CastShadows) with
            {
                Value = light.Type == LightType.Point ? "costly; quality limits" : string.Empty
            });
        }

        rows.Add(new SheetRow(SheetRowKind.Buttons, "l.remove", string.Empty) { Options = ["Remove Light"] });
    }

    /// <summary>A slider moved: the material (or colour) with that value; other keys leave it unchanged.</summary>
    public static SurfaceMaterial Slide(SurfaceMaterial m, string key, float fraction)
    {
        if (!Sliders.TryGetValue(key, out var spec))
        {
            return m;
        }

        var v = spec.ValueAt(fraction);
        return (key switch
        {
            "m.metallic" => m with { Metallic = v },
            "m.roughness" => m with { Roughness = v },
            "m.opacity" => m with { Opacity = v },
            "m.cutoff" => m with { AlphaCutoff = v },
            "m.emissiveIntensity" => m with { EmissiveIntensity = v },
            "m.normalStrength" => m with { NormalStrength = v },
            "m.occlusionStrength" => m with { OcclusionStrength = v },
            "m.uvScaleX" => m with { UvScaleX = v },
            "m.uvScaleY" => m with { UvScaleY = v },
            "m.toonBands" => m with { ToonBands = (int)MathF.Round(v) },
            "m.outlineThickness" => m with { OutlineThickness = v },
            _ => m
        }).Sanitized();
    }

    /// <summary>A switch, a choice or a button on a material row; <paramref name="index"/> is the option chosen.</summary>
    public static SurfaceMaterial Click(SurfaceMaterial m, Vector3 color, string key, SheetHitPart part, int index)
    {
        switch (key)
        {
            case "m.mode":
                return (m with { Mode = (ShadingMode)global::System.Math.Clamp(Step(part, index, (int)m.Mode, ShadingNames.Length), 0, 3) }).Sanitized();
            case "m.alpha":
                var alpha = (AlphaMode)Step(part, index, (int)m.AlphaMode, AlphaNames.Length);
                // Blend from an opaque material starts half see-through, so the change shows.
                return (m with { AlphaMode = alpha, Opacity = alpha == AlphaMode.Blend && m.Opacity >= 1f ? 0.5f : m.Opacity }).Sanitized();
            case "m.uv":
                return (m with { UvMode = (UvMode)Step(part, index, (int)m.UvMode, UvNames.Length) }).Sanitized();
            case "m.preset":
                var presets = MaterialPresets.All;
                var current = presets.ToList().FindIndex(p => p.Apply(m, color) == m);
                var next = part == SheetHitPart.Option ? index
                    : current < 0 ? (part == SheetHitPart.Next ? 0 : presets.Count - 1)
                    : (current + (part == SheetHitPart.Next ? 1 : -1) + presets.Count) % presets.Count;
                return presets[next].Apply(m, color).Sanitized();
            case "m.outline":
                return m with { Outline = !m.Outline };
            case "m.flat":
                return m with { FlatShading = !m.FlatShading };
            case "m.doubleSided":
                return m with { DoubleSided = !m.DoubleSided };
            case "m.castShadows":
                return m with { CastShadows = !m.CastShadows };
            case "m.receiveShadows":
                return m with { ReceiveShadows = !m.ReceiveShadows };
            default:
                return m;
        }
    }

    /// <summary>A colour set on a material row ("m.emissive", "m.outlineColor"); "m.color" is the part's colour.</summary>
    public static SurfaceMaterial SetColor(SurfaceMaterial m, string key, Vector3 color) => key switch
    {
        "m.emissive" => (m with { EmissiveColor = color }).Sanitized(),
        "m.outlineColor" => (m with { OutlineColor = color }).Sanitized(),
        _ => m
    };

    public static TextureSlot? SlotOf(string key) =>
        TextureRows.FirstOrDefault(row => row.Key == key) is { Key: not null } row ? row.Slot : null;

    /// <summary>A light row's slider moved.</summary>
    public static PartLight SlideLight(PartLight light, string key, float fraction)
    {
        if (!Sliders.TryGetValue(key, out var spec))
        {
            return light;
        }

        var v = spec.ValueAt(fraction);
        return (key switch
        {
            "l.intensity" or "l.sunIntensity" => light with { Intensity = v },
            "l.range" => light with { Range = v },
            "l.angle" => light with { Angle = v, InnerAngle = MathF.Min(light.InnerAngle, v) },
            "l.innerAngle" => light with { InnerAngle = MathF.Min(v, light.Angle) },
            _ => light
        }).Sanitized();
    }

    /// <summary>A light row's switch or choice. Changing the type keeps the colour and starts from that type's defaults.</summary>
    public static PartLight ClickLight(PartLight light, string key, SheetHitPart part, int index)
    {
        switch (key)
        {
            case "l.type":
                var type = (LightType)Step(part, index, (int)light.Type, LightTypeNames.Length);
                return type == light.Type ? light : (PartLight.Create(type) with { Color = light.Color, Enabled = light.Enabled }).Sanitized();
            case "l.enabled":
                return light with { Enabled = !light.Enabled };
            case "l.shadows":
                return light with { CastShadows = !light.CastShadows };
            default:
                return light;
        }
    }

    public static string TextureLabel(string? path) =>
        path is null ? string.Empty
        : path.StartsWith(SurfaceMaterial.ModelTexturePrefix, StringComparison.Ordinal) ? $"Model image {path[SurfaceMaterial.ModelTexturePrefix.Length..]}"
        : path[(path.LastIndexOf('/') + 1)..];

    /// <summary>A choice's new index: the option clicked, or one step back / on (wrapping).</summary>
    public static int Step(SheetHitPart part, int index, int current, int count) => part switch
    {
        SheetHitPart.Option => global::System.Math.Clamp(index, 0, count - 1),
        SheetHitPart.Previous => (current - 1 + count) % count,
        SheetHitPart.Next => (current + 1) % count,
        _ => current
    };

    internal static SheetRow Section(string label) => new(SheetRowKind.Section, string.Empty, label);

    internal static SheetRow Note(string label) => new(SheetRowKind.Note, string.Empty, label);

    internal static SheetRow Toggle(string key, string label, bool on) => new(SheetRowKind.Toggle, key, label) { On = on };

    internal static SheetRow Slider(string key, string label, float value, IReadOnlyDictionary<string, SliderSpec>? specs = null)
    {
        var spec = (specs ?? Sliders)[key];
        return new SheetRow(SheetRowKind.Slider, key, label) { Fraction = spec.FractionOf(value), Value = spec.Text(value) };
    }

    // A colour swatch, and its Hue / Saturation / Brightness rows while it is the one being edited.
    internal static void Color(List<SheetRow> rows, string key, string label, Vector3 color, string? expanded)
    {
        rows.Add(new SheetRow(SheetRowKind.Color, key, label) { Color = color, Value = ColorSpace.ToHex(color) });
        if (expanded == key)
        {
            rows.Add(new SheetRow(SheetRowKind.Hue, key, "Hue"));
            rows.Add(new SheetRow(SheetRowKind.Saturation, key, "Saturation"));
            rows.Add(new SheetRow(SheetRowKind.Brightness, key, "Brightness"));
        }
    }
}

/// <summary>
/// The edits of the project's Graphics.json settings (renderer, quality, image, effects, shadows; undoable) and of the
/// person's editor preference (3D View quality; saved apart, not undoable), shown in the Environment &amp; Graphics panel's
/// Lighting, Rendering and Post-Processing tabs (<see cref="EnvironmentSheet"/>). Keys: "r.*" settings, "q.*" Custom
/// quality values, "e.*" editor preference.
/// </summary>
internal static class RenderingSheet
{
    public static readonly string[] RendererNames = ["Classic", "Universal"];
    public static readonly string[] QualityNames = ["Low", "Medium", "High", "Ultra", "Custom"];
    public static readonly string[] ViewportNames = ["As Project", "Low", "Medium", "High", "Ultra"];
    public static readonly string[] ToneNames = ["None", "Neutral", "Filmic"];
    public static readonly string[] AntiAliasingNames = ["MSAA", "FXAA", "Both", "Off"];
    public static readonly int[] ShadowSizes = [512, 1024, 2048, 4096];
    public static readonly int[] FilterTaps = [1, 4, 9, 16];
    public static readonly int[] PointSizes = [128, 256, 512, 1024];
    public static readonly int[] AoSamples = [0, 8, 12, 16];
    public static readonly int[] ReflectionSizes = [32, 64, 128, 256];

    public static readonly IReadOnlyDictionary<string, SliderSpec> Sliders = new Dictionary<string, SliderSpec>
    {
        ["r.temperature"] = new(-1f, 1f),
        ["r.tint"] = new(-1f, 1f),
        ["r.bloomThreshold"] = new(0f, 10f, 2f),
        ["r.bloomIntensity"] = new(0f, 5f, 1.5f),
        ["r.bloomRadius"] = new(0f, 1f),
        ["r.vignetteIntensity"] = new(0f, 1f),
        ["r.aoRadius"] = new(0.1f, 10f, 2f),
        ["r.aoIntensity"] = new(0f, 4f, 1.5f),
        ["r.shadowDistance"] = new(10f, 2000f, 2.5f, 1f, "0"),
        ["r.shadowBias"] = new(0f, 10f, 1.5f),
        ["r.shadowNormalBias"] = new(0f, 10f, 1.5f),
        ["q.cascades"] = new(1f, 4f, 1f, 1f, "0"),
        ["q.spotShadows"] = new(0f, 8f, 1f, 1f, "0"),
        ["q.pointShadows"] = new(0f, 2f, 1f, 1f, "0"),
        ["q.bloomLevels"] = new(0f, 6f, 1f, 1f, "0"),
        ["q.lights"] = new(1f, 16f, 1f, 1f, "0"),
        ["q.renderScale"] = new(0.5f, 1f, 1f, 0.05f, "0.00", "x")
    };

    internal static void CustomRows(List<SheetRow> rows, QualitySettings q, bool enabled)
    {
        SheetRow Sizes(string key, string label, int[] sizes, int value) =>
            new(SheetRowKind.Choice, key, label)
            {
                Options = sizes.Select(size => size.ToString(CultureInfo.InvariantCulture)).ToArray(),
                Selected = Array.IndexOf(sizes, value),
                Value = value.ToString(CultureInfo.InvariantCulture),
                Enabled = enabled
            };

        rows.Add(MaterialSheet.Toggle("q.post", "Post-processing", q.PostProcessing) with { Enabled = enabled });
        rows.Add(Sizes("q.shadowSize", "Shadow Map", ShadowSizes, q.ShadowResolution));
        rows.Add(Slider("q.cascades", "Cascades", q.ShadowCascades) with { Enabled = enabled });
        rows.Add(Sizes("q.filter", "Shadow Softness", FilterTaps, q.ShadowFilterSamples));
        rows.Add(Slider("q.spotShadows", "Spot Shadows", q.MaxSpotShadows) with { Enabled = enabled });
        rows.Add(Slider("q.pointShadows", "Point Shadows", q.MaxPointShadows) with { Enabled = enabled });
        rows.Add(Sizes("q.pointSize", "Point Map", PointSizes, q.PointShadowResolution));
        rows.Add(Sizes("q.aoSamples", "AO Samples", AoSamples, q.AmbientOcclusionSamples));
        rows.Add(Slider("q.bloomLevels", "Bloom Levels", q.BloomLevels) with { Enabled = enabled });
        rows.Add(Sizes("q.reflections", "Reflections", ReflectionSizes, q.ReflectionResolution));
        rows.Add(Slider("q.lights", "Lights / Object", q.MaxLightsPerObject) with { Enabled = enabled });
        rows.Add(Slider("q.renderScale", "Render Scale", q.RenderScale) with { Enabled = enabled });
    }

    public static GraphicsSettings Slide(GraphicsSettings s, string key, float fraction)
    {
        if (!Sliders.TryGetValue(key, out var spec))
        {
            return s;
        }

        var v = spec.ValueAt(fraction);
        var q = s.Custom;
        var i = (int)MathF.Round(v);
        return (key switch
        {
            "r.temperature" => s with { Temperature = v },
            "r.tint" => s with { Tint = v },
            "r.bloomThreshold" => s with { BloomThreshold = v },
            "r.bloomIntensity" => s with { BloomIntensity = v },
            "r.bloomRadius" => s with { BloomRadius = v },
            "r.vignetteIntensity" => s with { VignetteIntensity = v },
            "r.aoRadius" => s with { AmbientOcclusionRadius = v },
            "r.aoIntensity" => s with { AmbientOcclusionIntensity = v },
            "r.shadowDistance" => s with { ShadowDistance = v },
            "r.shadowBias" => s with { ShadowBias = v },
            "r.shadowNormalBias" => s with { ShadowNormalBias = v },
            "q.cascades" => s with { Custom = q with { ShadowCascades = i } },
            "q.spotShadows" => s with { Custom = q with { MaxSpotShadows = i } },
            "q.pointShadows" => s with { Custom = q with { MaxPointShadows = i } },
            "q.bloomLevels" => s with { Custom = q with { BloomLevels = i is > 0 and < 3 ? 3 : i } },
            "q.lights" => s with { Custom = q with { MaxLightsPerObject = i } },
            "q.renderScale" => s with { Custom = q with { RenderScale = v } },
            _ => s
        }).Sanitized();
    }

    /// <summary>A switch, choice or button. Choosing Custom starts from the quality the project had.</summary>
    public static GraphicsSettings Click(GraphicsSettings s, string key, SheetHitPart part, int index)
    {
        var q = s.Custom;
        int Pick(int[] values, int current) => values[MaterialSheet.Step(part, index, global::System.Math.Max(0, Array.IndexOf(values, current)), values.Length)];
        return (key switch
        {
            "r.renderer" => s with { Renderer = (RendererKind)MaterialSheet.Step(part, index, (int)s.Renderer, RendererNames.Length) },
            "r.quality" => ChooseQuality(s, (QualityLevel)MaterialSheet.Step(part, index, (int)s.Quality, QualityNames.Length)),
            "r.tone" => s with { ToneMapping = (ToneMapping)MaterialSheet.Step(part, index, (int)s.ToneMapping, ToneNames.Length) },
            "r.aa" => s with { AntiAliasing = (AntiAliasing)MaterialSheet.Step(part, index, (int)s.AntiAliasing, AntiAliasingNames.Length) },
            "r.bloom" => s with { BloomEnabled = !s.BloomEnabled },
            "r.vignette" => s with { VignetteEnabled = !s.VignetteEnabled },
            "r.ao" => s with { AmbientOcclusionEnabled = !s.AmbientOcclusionEnabled },
            "r.shadows" => s with { ShadowsEnabled = !s.ShadowsEnabled },
            "r.pointShadows" => s with { PointLightShadows = !s.PointLightShadows },
            "r.reset" => GraphicsSettings.Default with { Renderer = s.Renderer },
            "q.post" => s with { Custom = q with { PostProcessing = !q.PostProcessing } },
            "q.shadowSize" => s with { Custom = q with { ShadowResolution = Pick(ShadowSizes, q.ShadowResolution) } },
            "q.filter" => s with { Custom = q with { ShadowFilterSamples = Pick(FilterTaps, q.ShadowFilterSamples) } },
            "q.pointSize" => s with { Custom = q with { PointShadowResolution = Pick(PointSizes, q.PointShadowResolution) } },
            "q.aoSamples" => s with { Custom = q with { AmbientOcclusionSamples = Pick(AoSamples, q.AmbientOcclusionSamples) } },
            "q.reflections" => s with { Custom = q with { ReflectionResolution = Pick(ReflectionSizes, q.ReflectionResolution) } },
            _ => s
        }).Sanitized();
    }

    /// <summary>The editor's 3D View preference choice.</summary>
    public static EditorGraphicsPreferences ClickPreference(EditorGraphicsPreferences p, string key, SheetHitPart part, int index) =>
        key == "e.viewport"
            ? p with { ViewportQuality = (ViewportQuality)MaterialSheet.Step(part, index, (int)p.ViewportQuality, ViewportNames.Length) }
            : p;

    // Custom starts as a copy of the level the project was at, so nothing changes until a value is edited.
    private static GraphicsSettings ChooseQuality(GraphicsSettings s, QualityLevel level) =>
        level == QualityLevel.Custom && s.Quality != QualityLevel.Custom
            ? s with { Quality = level, Custom = s.EffectiveQuality }
            : s with { Quality = level };

    internal static SheetRow Slider(string key, string label, float value) => MaterialSheet.Slider(key, label, value, Sliders);
}
