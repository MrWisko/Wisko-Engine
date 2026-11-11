using Wisko.Engine.Atmosphere;
using Wisko.Engine.Graphics;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;

namespace Wisko.Engine.Editor;

/// <summary>The tabs of the Environment &amp; Graphics panel, in order.</summary>
internal enum EnvironmentTab
{
    Atmosphere,
    Lighting,
    Rendering,
    PostProcessing,

    /// <summary>The selected object's material and light (<see cref="MaterialSheet"/>).</summary>
    Materials
}

/// <summary>
/// The Environment &amp; Graphics panel's global tabs: the project's Atmosphere (Atmosphere.json) and Graphics settings
/// (Graphics.json) and the person's 3D View preference, each setting in exactly one tab. Pure: rows are built from values,
/// an edit returns new values; the renderer applies them and records undo (Atmosphere edits as Atmosphere changes,
/// rendering edits as rendering changes). Keys: "a.*" Atmosphere ("a.v.&lt;value&gt;", "a.c.&lt;colour&gt;"), "r.*" / "q.*"
/// Graphics (<see cref="RenderingSheet"/>), "e.*" the editor preference. The Materials tab is <see cref="MaterialSheet"/>.
/// </summary>
internal static class EnvironmentSheet
{
    public const string Title = "Environment & Graphics";

    public static readonly string[] TabNames = ["Atmosphere", "Lighting", "Rendering", "Post-Processing", "Materials"];

    public static string ValueKey(AtmosphereValue value) => "a.v." + value;

    public static string ColorKey(AtmosphereColorSlot slot) => "a.c." + slot;

    public static AtmosphereValue? ValueOf(string key) =>
        key.StartsWith("a.v.", StringComparison.Ordinal) && Enum.TryParse<AtmosphereValue>(key[4..], out var value) ? value : null;

    public static AtmosphereColorSlot? SlotOf(string key) =>
        key.StartsWith("a.c.", StringComparison.Ordinal) && Enum.TryParse<AtmosphereColorSlot>(key[4..], out var slot) ? slot : null;

    public static bool IsAtmosphere(string key) => key.StartsWith("a.", StringComparison.Ordinal);

    /// <summary>An Atmosphere slider: its range from the Atmosphere, linear, shown as before (degrees, distances, EV).</summary>
    public static SliderSpec SpecOf(AtmosphereValue value)
    {
        var range = AtmosphereSettings.RangeOf(value);
        return value switch
        {
            AtmosphereValue.SunElevation or AtmosphereValue.SunHeading => new SliderSpec(range.Min, range.Max, Format: "0", Suffix: "°"),
            AtmosphereValue.FogStart or AtmosphereValue.FogEnd => new SliderSpec(range.Min, range.Max, Format: "0"),
            AtmosphereValue.Exposure => new SliderSpec(range.Min, range.Max, Format: "+0.00;-0.00;0.00", Suffix: " EV"),
            _ => new SliderSpec(range.Min, range.Max)
        };
    }

    public static List<SheetRow> Rows(EnvironmentTab tab, AtmosphereSettings atmosphere, GraphicsSettings graphics, EditorGraphicsPreferences preferences,
        RendererCapabilities capabilities, string? expandedColor, bool readOnly)
    {
        var rows = new List<SheetRow>();
        if (readOnly)
        {
            rows.Add(MaterialSheet.Note("Stop Play Mode to change these settings."));
        }

        var context = new Context(atmosphere, graphics, expandedColor, readOnly);
        var start = rows.Count;
        switch (tab)
        {
            case EnvironmentTab.Atmosphere:
                AtmosphereRows(rows, context);
                break;
            case EnvironmentTab.Lighting:
                LightingRows(rows, context, capabilities);
                break;
            case EnvironmentTab.Rendering:
                RenderingRows(rows, context, preferences, capabilities);
                break;
            case EnvironmentTab.PostProcessing:
                PostProcessingRows(rows, context, capabilities);
                break;
        }

        if (!capabilities.UniversalRenderer)
        {
            MuteUniversalOnly(rows, start, tab);
        }

        return rows;
    }

    // Where the Universal renderer cannot draw (the macOS app), its settings are drawn muted but stay editable and are
    // saved, as an Atmosphere value is while the Atmosphere is off: the project keeps its look for Windows. The renderer
    // choice and the reset stay as they are; exposure, saturation and contrast ("a.*") are drawn by Classic too.
    private static void MuteUniversalOnly(List<SheetRow> rows, int start, EnvironmentTab tab)
    {
        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            var universalOnly = tab switch
            {
                EnvironmentTab.Lighting => true,
                EnvironmentTab.Rendering => row.Key is not ("r.renderer" or "r.reset"),
                EnvironmentTab.PostProcessing => row.Key.StartsWith("r.", StringComparison.Ordinal),
                _ => false
            };
            if (universalOnly && row.Kind is not (SheetRowKind.Section or SheetRowKind.Note))
            {
                rows[i] = row with { Muted = true };
            }
        }
    }

    // Two short lines under a tab's top when only the Classic renderer draws here: why, and what this tab still does.
    private static void ClassicOnlyNote(List<SheetRow> rows, RendererCapabilities capabilities, string second)
    {
        rows.Add(MaterialSheet.Note(capabilities.ClassicReason + ":"));
        rows.Add(MaterialSheet.Note(second));
    }

    /// <summary>An Atmosphere slider moved: the settings with that value; other keys leave them unchanged.</summary>
    public static AtmosphereSettings Slide(AtmosphereSettings settings, string key, float fraction) =>
        ValueOf(key) is { } value ? settings.With(value, SpecOf(value).ValueAt(fraction)) : settings;

    /// <summary>An Atmosphere switch: the new settings and the undo label (as the Atmosphere panel named them); null for other keys.</summary>
    public static (AtmosphereSettings Settings, string Label)? Click(AtmosphereSettings s, string key) => key switch
    {
        "a.enabled" => (s with { Enabled = !s.Enabled }, s.Enabled ? "Off" : "On"),
        "a.fog" => (s with { FogEnabled = !s.FogEnabled }, s.FogEnabled ? "Fog Off" : "Fog On"),
        "a.ambientSky" => s.AmbientMode == AtmosphereAmbientMode.Sky
            ? (s with { AmbientMode = AtmosphereAmbientMode.Flat }, "Flat Ambient")
            : (s with { AmbientMode = AtmosphereAmbientMode.Sky }, "Sky Ambient"),
        _ => null
    };

    // ------------------------------------------------------------------------------------------------- tabs

    private static void AtmosphereRows(List<SheetRow> rows, Context c)
    {
        var a = c.Atmosphere;
        rows.Add(MaterialSheet.Toggle("a.enabled", "Atmosphere", a.Enabled) with { Value = a.Enabled ? "On" : "Off", Enabled = !c.ReadOnly });
        if (!a.Enabled)
        {
            rows.Add(MaterialSheet.Note("Off: plain environment. Settings are kept."));
        }

        rows.Add(MaterialSheet.Section("SKY"));
        c.Color(rows, AtmosphereColorSlot.Sky, "Sky Color");
        c.Color(rows, AtmosphereColorSlot.Horizon, "Horizon");
        c.Value(rows, AtmosphereValue.SkyIntensity, "Sky Intensity");
        rows.Add(MaterialSheet.Section("SUN"));
        c.Color(rows, AtmosphereColorSlot.Sun, "Sun Color");
        c.Value(rows, AtmosphereValue.SunIntensity, "Sun Intensity");
        c.Value(rows, AtmosphereValue.SunElevation, "Sun Height");
        c.Value(rows, AtmosphereValue.SunHeading, "Sun Direction");
        c.Value(rows, AtmosphereValue.SunDiscSize, "Sun Disc");
        rows.Add(MaterialSheet.Note("Day and night: Lua moves the sun in Play."));
        rows.Add(MaterialSheet.Section("AMBIENT LIGHT"));
        c.Color(rows, AtmosphereColorSlot.Ambient, "Ambient Color");
        c.Value(rows, AtmosphereValue.AmbientIntensity, "Ambient Intensity");
        rows.Add(MaterialSheet.Section("FOG"));
        rows.Add(c.Atmo(MaterialSheet.Toggle("a.fog", "Fog", a.FogEnabled)));
        c.Color(rows, AtmosphereColorSlot.Fog, "Fog Color");
        c.Value(rows, AtmosphereValue.FogDensity, "Density");
        c.Value(rows, AtmosphereValue.FogStart, "Start Distance");
        c.Value(rows, AtmosphereValue.FogEnd, "End Distance");
        rows.Add(MaterialSheet.Section("DISTANCE"));
        c.Value(rows, AtmosphereValue.Haze, "Haze");
        rows.Add(MaterialSheet.Section("RESET"));
        rows.Add(MaterialSheet.Note("Every Atmosphere value, in every tab:"));
        rows.Add(new SheetRow(SheetRowKind.Buttons, "a.reset", string.Empty) { Options = ["Reset to Default"], Enabled = !c.ReadOnly });
    }

    private static void LightingRows(List<SheetRow> rows, Context c, RendererCapabilities capabilities)
    {
        var a = c.Atmosphere;
        var s = c.Graphics;
        var universal = s.Renderer == RendererKind.Universal;
        if (!capabilities.UniversalRenderer)
        {
            ClassicOnlyNote(rows, capabilities, "no shadows or sky light. Kept for Windows.");
        }
        else if (!universal)
        {
            rows.Add(MaterialSheet.Note("Classic draws none of this (Universal only)."));
        }

        rows.Add(MaterialSheet.Section("ENVIRONMENT LIGHT"));
        rows.Add(c.Atmo(MaterialSheet.Toggle("a.ambientSky", "Sky Ambient", a.AmbientMode == AtmosphereAmbientMode.Sky)));
        if (a.AmbientMode == AtmosphereAmbientMode.Sky)
        {
            c.Color(rows, AtmosphereColorSlot.Ground, "Ground Color");
        }

        c.Value(rows, AtmosphereValue.ReflectionIntensity, "Reflections");

        var edit = !c.ReadOnly && universal;
        rows.Add(MaterialSheet.Section("SHADOWS"));
        rows.Add(MaterialSheet.Toggle("r.shadows", "Shadows", s.ShadowsEnabled) with { Enabled = edit });
        rows.Add(RenderingSheet.Slider("r.shadowDistance", "Distance", s.ShadowDistance) with { Enabled = edit && s.ShadowsEnabled });
        rows.Add(RenderingSheet.Slider("r.shadowBias", "Bias", s.ShadowBias) with { Enabled = edit && s.ShadowsEnabled });
        rows.Add(RenderingSheet.Slider("r.shadowNormalBias", "Normal Bias", s.ShadowNormalBias) with { Enabled = edit && s.ShadowsEnabled });
        rows.Add(MaterialSheet.Toggle("r.pointShadows", "Point Light Shadows", s.PointLightShadows) with
        {
            Enabled = edit && s.ShadowsEnabled,
            Value = s.EffectiveQuality.MaxPointShadows == 0 ? "off at this quality" : $"up to {s.EffectiveQuality.MaxPointShadows}"
        });

        rows.Add(MaterialSheet.Section("LIGHTS"));
        rows.Add(MaterialSheet.Note("Add Light (toolbar) places a light;"));
        rows.Add(MaterialSheet.Note("the Materials tab edits the selection's."));
    }

    private static void RenderingRows(List<SheetRow> rows, Context c, EditorGraphicsPreferences preferences, RendererCapabilities capabilities)
    {
        var s = c.Graphics;
        rows.Add(MaterialSheet.Section("RENDERER"));
        rows.Add(new SheetRow(SheetRowKind.Choice, "r.renderer", "Renderer") { Options = RenderingSheet.RendererNames, Selected = (int)s.Renderer, Enabled = !c.ReadOnly });
        if (!capabilities.UniversalRenderer)
        {
            ClassicOnlyNote(rows, capabilities, "Universal settings are kept for Windows.");
        }
        else if (s.Renderer == RendererKind.Classic)
        {
            rows.Add(MaterialSheet.Note("Classic: Wisko's original look (no textures, lights or post-processing)."));
        }

        var universal = s.Renderer == RendererKind.Universal;
        var edit = !c.ReadOnly && universal;
        rows.Add(MaterialSheet.Section("QUALITY"));
        rows.Add(new SheetRow(SheetRowKind.Choice, "r.quality", "Quality") { Options = RenderingSheet.QualityNames, Selected = (int)s.Quality, Enabled = edit });
        if (s.Quality == QualityLevel.Custom && universal)
        {
            RenderingSheet.CustomRows(rows, s.Custom, !c.ReadOnly);
        }

        rows.Add(new SheetRow(SheetRowKind.Choice, "r.aa", "Anti-aliasing") { Options = RenderingSheet.AntiAliasingNames, Selected = (int)s.AntiAliasing, Enabled = edit });

        rows.Add(MaterialSheet.Section("3D VIEW (THIS COMPUTER)"));
        rows.Add(new SheetRow(SheetRowKind.Choice, "e.viewport", "Preview") { Options = RenderingSheet.ViewportNames, Selected = (int)preferences.ViewportQuality });
        rows.Add(MaterialSheet.Note("Only your editor; the game uses the project's."));

        rows.Add(MaterialSheet.Section("RESET"));
        rows.Add(MaterialSheet.Note("Rendering, shadows and post-processing:"));
        rows.Add(new SheetRow(SheetRowKind.Buttons, "r.reset", string.Empty) { Options = ["Reset to Defaults"], Enabled = !c.ReadOnly });
    }

    private static void PostProcessingRows(List<SheetRow> rows, Context c, RendererCapabilities capabilities)
    {
        var s = c.Graphics;
        var universal = s.Renderer == RendererKind.Universal;
        var edit = !c.ReadOnly && universal;
        if (!capabilities.UniversalRenderer && universal)
        {
            ClassicOnlyNote(rows, capabilities, "only Exposure, Saturation and Contrast apply.");
        }
        else if (!universal)
        {
            rows.Add(MaterialSheet.Note("Classic: exposure, saturation and contrast."));
        }

        rows.Add(MaterialSheet.Section("EXPOSURE AND TONE"));
        c.Value(rows, AtmosphereValue.Exposure, "Exposure");
        rows.Add(new SheetRow(SheetRowKind.Choice, "r.tone", "Tone Mapping") { Options = RenderingSheet.ToneNames, Selected = (int)s.ToneMapping, Enabled = edit });

        rows.Add(MaterialSheet.Section("COLOR"));
        rows.Add(RenderingSheet.Slider("r.temperature", "Temperature", s.Temperature) with { Enabled = edit });
        rows.Add(RenderingSheet.Slider("r.tint", "Tint", s.Tint) with { Enabled = edit });
        c.Value(rows, AtmosphereValue.Saturation, "Saturation");
        c.Value(rows, AtmosphereValue.Contrast, "Contrast");

        rows.Add(MaterialSheet.Section("EFFECTS"));
        if (universal && !s.EffectiveQuality.PostProcessing)
        {
            rows.Add(MaterialSheet.Note("This quality has no post-processing."));
        }

        rows.Add(MaterialSheet.Toggle("r.bloom", "Bloom", s.BloomEnabled) with { Enabled = edit });
        if (s.BloomEnabled)
        {
            rows.Add(RenderingSheet.Slider("r.bloomThreshold", "Threshold", s.BloomThreshold) with { Enabled = edit });
            rows.Add(RenderingSheet.Slider("r.bloomIntensity", "Strength", s.BloomIntensity) with { Enabled = edit });
            rows.Add(RenderingSheet.Slider("r.bloomRadius", "Spread", s.BloomRadius) with { Enabled = edit });
        }

        rows.Add(MaterialSheet.Toggle("r.ao", "Ambient Occlusion", s.AmbientOcclusionEnabled) with { Enabled = edit });
        if (s.AmbientOcclusionEnabled)
        {
            rows.Add(RenderingSheet.Slider("r.aoRadius", "AO Radius", s.AmbientOcclusionRadius) with { Enabled = edit });
            rows.Add(RenderingSheet.Slider("r.aoIntensity", "AO Strength", s.AmbientOcclusionIntensity) with { Enabled = edit });
        }

        rows.Add(MaterialSheet.Toggle("r.vignette", "Vignette", s.VignetteEnabled) with { Enabled = edit });
        if (s.VignetteEnabled)
        {
            rows.Add(RenderingSheet.Slider("r.vignetteIntensity", "Vignette Amount", s.VignetteIntensity) with { Enabled = edit });
        }
    }

    // What every row of a tab is built from. Atmosphere rows are muted (still editable) while the Atmosphere is off, and
    // read-only in Play like every other row.
    private sealed record Context(AtmosphereSettings Atmosphere, GraphicsSettings Graphics, string? ExpandedColor, bool ReadOnly)
    {
        public SheetRow Atmo(SheetRow row) => row with { Muted = !Atmosphere.Enabled, Enabled = !ReadOnly };

        public void Value(List<SheetRow> rows, AtmosphereValue value, string label)
        {
            var spec = SpecOf(value);
            var amount = Atmosphere.Get(value);
            rows.Add(Atmo(new SheetRow(SheetRowKind.Slider, ValueKey(value), label) { Fraction = spec.FractionOf(amount), Value = spec.Text(amount) }));
        }

        public void Color(List<SheetRow> rows, AtmosphereColorSlot slot, string label)
        {
            var key = ColorKey(slot);
            var color = Atmosphere.Get(slot);
            var (r, g, b) = color.ToBytes();
            rows.Add(Atmo(new SheetRow(SheetRowKind.Color, key, label) { Color = color.ToVector3(), Value = $"#{r:X2}{g:X2}{b:X2}" }));
            if (ExpandedColor == key)
            {
                rows.Add(Atmo(new SheetRow(SheetRowKind.Hue, key, "Hue")));
                rows.Add(Atmo(new SheetRow(SheetRowKind.Saturation, key, "Saturation")));
                rows.Add(Atmo(new SheetRow(SheetRowKind.Brightness, key, "Brightness")));
            }
        }
    }
}
