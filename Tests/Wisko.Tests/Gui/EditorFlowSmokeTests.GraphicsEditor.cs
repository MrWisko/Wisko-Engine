using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Tests.Gui;

// Graphics V1 editing in the real editor, with scripted clicks found through the panel's own layout: ToolBench >
// Environment & Graphics opens one panel with tabs. Every keyed row of the Atmosphere, Lighting, Rendering and
// Post-Processing tabs is scrolled to and hit where a click edits it; Atmosphere edits (a slider drag, the fog and
// Atmosphere switches, a colour, Sky Ambient, Reflections, Exposure) with undo and redo; the Materials tab on a primitive
// (shading, a Metallic drag, a colour drag, add / remove a light) with undo and redo; Add Light from the toolbar; a
// model's own material and Back to Imported; quality, the 3D View preference and the renderer; bloom and tone mapping; the
// toolbar buttons and an Atmosphere preset; then Save, Leave to Hub and reopen (everything comes back).
internal static partial class EditorFlowSmokeTests
{
    private sealed class GraphicsEditorFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private PropertySheet Sheet => (PropertySheet)typeof(Renderer).GetField("_environmentSheet", Hidden)!.GetValue(Renderer)!;

        private UiBox PanelRect => (UiBox)typeof(Renderer).GetProperty("EnvironmentPanelRect", Hidden)!.GetValue(Renderer)!;

        private List<SheetRow> Rows => (List<SheetRow>)typeof(Renderer).GetMethod("SheetRows", Hidden)!.Invoke(Renderer, null)!;

        private bool PanelOpen => (bool)typeof(Renderer).GetField("_environmentOpen", Hidden)!.GetValue(Renderer)!;

        private EnvironmentTab Tab => (EnvironmentTab)typeof(Renderer).GetField("_environmentTab", Hidden)!.GetValue(Renderer)!;

        // "None" when the panel is closed, else the tab it shows.
        private string PanelMode => PanelOpen ? Tab.ToString() : "None";

        private Part PartNamed(string name) => Workspace.Parts.First(p => p.Name == name);

        private Part? Selected => Workspace.Parts.FirstOrDefault(p => p.Name == SelectedNames);

        // The Atmosphere as saved (checked again after reopening and in Atmosphere.json).
        private AtmosphereSettings? _saved;

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var project = Path.Combine(root, "Projects", GraphicsFlow.ProjectName);
            var graphics = GraphicsFile.Load(Path.Combine(project, GraphicsFile.FileName), out var status, out _);
            report.Check("Graphics editor: Save wrote the Rendering and Post-Processing settings to Graphics.json (bloom on, High, Neutral, Universal)",
                status == GraphicsLoadStatus.Loaded && graphics is { BloomEnabled: true, Quality: QualityLevel.High, ToneMapping: ToneMapping.Neutral, Renderer: RendererKind.Universal },
                $"{status}: bloom {graphics.BloomEnabled}, {graphics.Quality}, {graphics.ToneMapping}, {graphics.Renderer}");
            var parts = SceneFile.TryLoad(Path.Combine(project, "Scene.json"), out var loaded, out var error) ? loaded : null;
            var rough = parts?.FirstOrDefault(p => p.Name == "Rough 0");
            report.Check("Graphics editor: Save wrote the material and light edits to Scene.json (Metallic 1, a point light)",
                rough is { Material.Metallic: >= 0.99f, Light.Type: LightType.Point }, error ?? $"{rough?.Material?.Metallic} {rough?.Light?.Type}");
            var saved = ((GraphicsEditorFlow)flow)._saved;
            var atmosphere = AtmosphereFile.Load(Path.Combine(project, AtmosphereFile.FileName), out var atmosphereStatus, out var problem);
            report.Check("Graphics editor: Save wrote the Atmosphere tab's edits to Atmosphere.json (the same settings the editor had)",
                atmosphereStatus == AtmosphereLoadStatus.Loaded && saved is not null && atmosphere == saved,
                problem ?? $"{atmosphereStatus}: sun {atmosphere.SunIntensity} fog {atmosphere.FogEnabled} ambient {atmosphere.AmbientMode} "
                    + $"reflections {atmosphere.ReflectionIntensity} exposure {atmosphere.Exposure}");
            var preferences = EditorGraphicsPreferences.Load(Path.Combine(root, EditorGraphicsPreferences.FileName));
            report.Check("Graphics editor: the 3D View preference was saved apart (back to As Project)",
                File.Exists(Path.Combine(root, EditorGraphicsPreferences.FileName)) && preferences.ViewportQuality == ViewportQuality.Project);
        }

        // The row with this key and kind, scrolled into view, and where it is now.
        private UiBox RowBox(string key, SheetRowKind kind)
        {
            var rows = Rows;
            var index = rows.FindIndex(r => r.Key == key && r.Kind == kind);
            if (index < 0)
            {
                throw new InvalidOperationException($"the {PanelMode} tab has no {kind} row \"{key}\"");
            }

            Sheet.Reveal(PanelRect, rows, index);
            return Sheet.Layout(PanelRect, rows).ElementAt(index).Box;
        }

        private static (float X, float Y) Center(UiBox box) => (box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f);

        private (float X, float Y) Track(string key, float fraction, SheetRowKind kind = SheetRowKind.Slider)
        {
            var track = PropertySheet.TrackBox(RowBox(key, kind));
            return (track.X + track.Width * fraction, track.Y + track.Height * 0.5f);
        }

        private (float X, float Y) Switch(string key) => (PropertySheet.TrackBox(RowBox(key, SheetRowKind.Toggle)).X + 10f, Center(RowBox(key, SheetRowKind.Toggle)).Y);

        private (float X, float Y) Button(string key, int index, int count) => Center(PropertySheet.ButtonBox(RowBox(key, SheetRowKind.Buttons), index, count));

        private string ValueOf(string key) => Rows.First(r => r.Key == key && r.Kind != SheetRowKind.Hue && r.Kind != SheetRowKind.Saturation && r.Kind != SheetRowKind.Brightness).Value;

        // A choice: its segment when the options are shown side by side, else the arrows clicked until it is chosen.
        private IEnumerable<int> Choose(string key, int index)
        {
            Step = $"{PanelMode} tab: {key} = option {index}";
            var row = Rows.First(r => r.Key == key && r.Kind == SheetRowKind.Choice);
            var choice = PropertySheet.ChoiceBox(RowBox(key, SheetRowKind.Choice));
            if (Sheet.Segmented(row, choice))
            {
                Click((choice.X + (index + 0.5f) * choice.Width / row.Options.Count, choice.Y + choice.Height * 0.5f));
                yield return 4;
                yield break;
            }

            for (var guard = 0; guard < row.Options.Count && Rows.First(r => r.Key == key && r.Kind == SheetRowKind.Choice).Selected != index; guard++)
            {
                choice = PropertySheet.ChoiceBox(RowBox(key, SheetRowKind.Choice));
                Click((choice.X + choice.Width - 8f, choice.Y + choice.Height * 0.5f));
                yield return 4;
            }
        }

        // A tab of the panel, clicked where the panel draws it.
        private IEnumerable<int> ChooseTab(EnvironmentTab tab)
        {
            Step = $"panel tab {EnvironmentSheet.TabNames[(int)tab]}";
            Click(Center(Sheet.TabBoxes(PanelRect)[(int)tab]));
            yield return 6;
        }

        private IEnumerable<int> ToolbarButton(ToolbarControl control)
        {
            Step = $"toolbar {control}";
            Click(Find((x, y) => Toolbar.TryHitControl(x, y, Width, out var hit) && hit == control, 0f, ToolbarRenderer.ToolbarHeight));
            yield return 6;
        }

        private IEnumerable<int> ChooseSection(string label)
        {
            Step = "ToolBench menu";
            Click(Find((x, y) => Toolbar.TryHitToolBench(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight));
            yield return 4;
            Step = $"ToolBench > {label}";
            Click(FindMenuItem(label));
            yield return 8;
        }

        // Every keyed row of the shown tab, scrolled into view and pointed at where a click edits it (a slider's track, a
        // switch, a choice, a button, a swatch): the panel's own hit test must answer with that row, inside the panel.
        private (int Count, List<string> Missed, List<string> Keys) Sweep()
        {
            var rows = Rows;
            var missed = new List<string>();
            var keys = new List<string>();
            var panel = PanelRect;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Key.Length == 0 || row.Kind is SheetRowKind.Hue or SheetRowKind.Saturation or SheetRowKind.Brightness)
                {
                    continue;
                }

                keys.Add(row.Key);
                Sheet.Reveal(panel, rows, i);
                var box = Sheet.Layout(panel, rows).ElementAt(i).Box;
                var track = PropertySheet.TrackBox(box);
                var point = row.Kind switch
                {
                    SheetRowKind.Slider => (track.X + track.Width * 0.5f, track.Y + track.Height * 0.5f),
                    SheetRowKind.Toggle or SheetRowKind.Texture => (track.X + 10f, box.Y + box.Height * 0.5f),
                    SheetRowKind.Choice => Center(PropertySheet.ChoiceBox(box)),
                    SheetRowKind.Buttons => Center(PropertySheet.ButtonBox(box, 0, row.Options.Count)),
                    _ => Center(box)
                };
                var inside = box.Y >= panel.Y && box.Y + box.Height <= panel.Y + panel.Height;
                if (!inside || Sheet.HitTest(panel, rows, point.Item1, point.Item2)?.Row?.Key != row.Key)
                {
                    missed.Add(row.Key);
                }
            }

            Sheet.ResetScroll();
            return (keys.Count, missed, keys);
        }

        protected override IEnumerable<int> Steps()
        {
            foreach (var wait in OpenCard(GraphicsFlow.ProjectName, "open the graphics test"))
            {
                yield return wait;
            }

            yield return 20;
            ViewCamera.Position = new Vector3(-8f, 10f, 24f);
            ViewCamera.SetOrientation(-90f, -20f);

            // One ToolBench section for the environment and graphics settings.
            var sections = ToolBench.Sections.Select(ToolBench.LabelOf).ToArray();
            Check("ToolBench: one Environment & Graphics section (no separate Atmosphere or Graphics section)",
                sections.Contains("Environment & Graphics") && !sections.Contains("Atmosphere") && !sections.Contains("Graphics"), string.Join(", ", sections));
            foreach (var wait in ChooseSection("Environment & Graphics"))
            {
                yield return wait;
            }

            Check("Environment & Graphics: the section shows Environment, Presets, Material and Add Light, and opens the panel on Atmosphere",
                ToolBench.ControlsOf(ToolBenchSection.Environment).SequenceEqual([ToolbarControl.Environment, ToolbarControl.Presets, ToolbarControl.Material, ToolbarControl.AddLight])
                && PanelMode == "Atmosphere", PanelMode);

            // Every tab: each of its settings can be scrolled to and clicked; screenshots of each.
            var tabKeys = new Dictionary<EnvironmentTab, List<string>>();
            var unreachable = new List<string>();
            var swept = 0;
            foreach (var tab in new[] { EnvironmentTab.Atmosphere, EnvironmentTab.Lighting, EnvironmentTab.Rendering, EnvironmentTab.PostProcessing })
            {
                foreach (var wait in ChooseTab(tab))
                {
                    yield return wait;
                }

                if (Tab != tab)
                {
                    unreachable.Add($"tab {tab} did not open ({Tab})");
                    continue;
                }

                foreach (var wait in Shot($"gfx-ed-00-tab-{tab.ToString().ToLowerInvariant()}"))
                {
                    yield return wait;
                }

                var (rowCount, missed, keys) = Sweep();
                swept += rowCount;
                unreachable.AddRange(missed.Select(key => $"{tab}:{key}"));
                tabKeys[tab] = keys;
                yield return 2;
            }

            var all = tabKeys.Values.SelectMany(k => k).ToList();
            var duplicated = all.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            var atmosphereShown = AtmosphereSettings.Values.All(v => all.Contains(EnvironmentSheet.ValueKey(v)))
                && AtmosphereSettings.ColorSlots.All(s => all.Contains(EnvironmentSheet.ColorKey(s)) || (s == AtmosphereColorSlot.Ground && Renderer.Atmosphere.AmbientMode != AtmosphereAmbientMode.Sky))
                && new[] { "a.enabled", "a.fog", "a.ambientSky", "a.reset" }.All(all.Contains);
            Check("Environment & Graphics: every row of the four global tabs can be scrolled to and clicked (each setting in one tab only)",
                swept > 40 && unreachable.Count == 0 && duplicated.Count == 0, $"{swept} rows; unreachable [{string.Join(", ", unreachable)}]; in two tabs [{string.Join(", ", duplicated)}]");
            Check("Environment & Graphics: every Atmosphere value and colour, Fog, Sky Ambient, the on/off switch and Reset are in the panel",
                atmosphereShown, string.Join(", ", all.Where(EnvironmentSheet.IsAtmosphere)));
            Check("Environment & Graphics: the tabs hold what they say (sky, sun, fog, haze | environment light, shadows | renderer, quality, AA | exposure, tone, colour, effects)",
                tabKeys.TryGetValue(EnvironmentTab.Atmosphere, out var a) && a.Contains("a.c.Sky") && a.Contains("a.v.SunIntensity") && a.Contains("a.fog") && a.Contains("a.v.Haze") && a.Contains("a.c.Ambient")
                && tabKeys.TryGetValue(EnvironmentTab.Lighting, out var l) && l.Contains("a.ambientSky") && l.Contains("a.v.ReflectionIntensity") && l.Contains("r.shadows") && l.Contains("r.pointShadows")
                && tabKeys.TryGetValue(EnvironmentTab.Rendering, out var r) && r.Contains("r.renderer") && r.Contains("r.quality") && r.Contains("r.aa") && r.Contains("e.viewport")
                && tabKeys.TryGetValue(EnvironmentTab.PostProcessing, out var p) && p.Contains("a.v.Exposure") && p.Contains("r.tone") && p.Contains("r.bloom") && p.Contains("r.ao")
                    && p.Contains("r.vignette") && p.Contains("r.temperature") && p.Contains("a.v.Saturation") && p.Contains("a.v.Contrast"));

            // The Atmosphere tab: a Sun Intensity drag is one undo step.
            foreach (var wait in ChooseTab(EnvironmentTab.Atmosphere))
            {
                yield return wait;
            }

            var sunBefore = Renderer.Atmosphere.SunIntensity;
            foreach (var wait in DragRow(Track("a.v.SunIntensity", 0.05f), Track("a.v.SunIntensity", 0.8f), "Atmosphere tab: drag Sun Intensity"))
            {
                yield return wait;
            }

            var sunDragged = Renderer.Atmosphere.SunIntensity;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var sunUndone = Renderer.Atmosphere.SunIntensity;
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            Check("Atmosphere tab: a Sun Intensity drag sets it (3.20), undoes in one step and redoes",
                MathF.Abs(sunDragged - 3.2f) < 0.02f && sunUndone == sunBefore && Renderer.Atmosphere.SunIntensity == sunDragged && ValueOf("a.v.SunIntensity") == "3.20",
                $"{sunBefore} -> {sunDragged}, undo {sunUndone}, redo {Renderer.Atmosphere.SunIntensity}, shown {ValueOf("a.v.SunIntensity")}");

            // Fog on, undo, redo.
            var fogBefore = Renderer.Atmosphere.FogEnabled;
            Step = "Atmosphere tab: Fog switch";
            Click(Switch("a.fog"));
            yield return 6;
            var fogSwitched = Renderer.Atmosphere.FogEnabled;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var fogUndone = Renderer.Atmosphere.FogEnabled;
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            Check("Atmosphere tab: the Fog switch flips fog, undo and redo follow", fogSwitched != fogBefore && fogUndone == fogBefore && Renderer.Atmosphere.FogEnabled == fogSwitched);

            // A colour: Sky Color's hue.
            var skyBefore = Renderer.Atmosphere.SkyColor;
            Step = "Atmosphere tab: open Sky Color";
            Click(Center(RowBox("a.c.Sky", SheetRowKind.Color)));
            yield return 4;
            foreach (var wait in DragRow(Track("a.c.Sky", 0.05f, SheetRowKind.Hue), Track("a.c.Sky", 0.9f, SheetRowKind.Hue), "Sky Color: hue"))
            {
                yield return wait;
            }

            var skyAfter = Renderer.Atmosphere.SkyColor;
            foreach (var wait in Shot("gfx-ed-01-atmosphere-colour"))
            {
                yield return wait;
            }

            Check("Atmosphere tab: Sky Color's Hue row changes the Atmosphere's sky colour", skyAfter != skyBefore, $"{skyBefore} -> {skyAfter}");

            // The Atmosphere off: its rows are muted but still editable; undo turns it back on.
            Step = "Atmosphere tab: Atmosphere off";
            Click(Switch("a.enabled"));
            yield return 6;
            var off = !Renderer.Atmosphere.Enabled;
            var mutedRow = Rows.First(row => row.Key == "a.v.Haze");
            var hazeHit = Sheet.HitTest(PanelRect, Rows, Track("a.v.Haze", 0.5f).X, Track("a.v.Haze", 0.5f).Y)?.Row?.Key;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Atmosphere tab: the Atmosphere switch turns it off (values muted, still editable); undo turns it on",
                off && mutedRow is { Muted: true, Enabled: true } && hazeHit == "a.v.Haze" && Renderer.Atmosphere.Enabled, $"off {off}, muted {mutedRow.Muted}, hit {hazeHit}");

            // The Lighting tab: Sky Ambient (the ground colour row follows it) and Reflections.
            foreach (var wait in ChooseTab(EnvironmentTab.Lighting))
            {
                yield return wait;
            }

            var modeBefore = Renderer.Atmosphere.AmbientMode;
            Step = "Lighting tab: Sky Ambient switch";
            Click(Switch("a.ambientSky"));
            yield return 6;
            var modeAfter = Renderer.Atmosphere.AmbientMode;
            var groundShown = Rows.Any(row => row.Key == "a.c.Ground");
            foreach (var wait in DragRow(Track("a.v.ReflectionIntensity", 0.1f), Track("a.v.ReflectionIntensity", 0.25f), "Lighting tab: drag Reflections"))
            {
                yield return wait;
            }

            Check("Lighting tab: Sky Ambient switches the ambient mode (Ground Color shows only for Sky); Reflections drag to 0.50",
                modeAfter != modeBefore && groundShown == (modeAfter == AtmosphereAmbientMode.Sky) && MathF.Abs(Renderer.Atmosphere.ReflectionIntensity - 0.5f) < 0.02f,
                $"{modeBefore} -> {modeAfter}, ground row {groundShown}, reflections {Renderer.Atmosphere.ReflectionIntensity}");

            // The Materials tab, from the toolbar's Material button.
            foreach (var wait in ToolbarButton(ToolbarControl.Material))
            {
                yield return wait;
            }

            Check("Material button: the panel shows the Materials tab", PanelMode == "Materials", PanelMode);
            Step = "World: select Rough 0";
            Click(PartRow("Rough 0"));
            yield return 6;
            var rough = PartNamed("Rough 0");
            Check("Materials tab: shows the selected sphere's material (roughness 0.05)", Rows.Any(r => r.Key == "m.roughness" && r.Value == "0.05"),
                string.Join(", ", Rows.Where(r => r.Key == "m.roughness").Select(r => r.Value)));
            foreach (var wait in Shot("gfx-ed-02-materials-tab"))
            {
                yield return wait;
            }

            // Shading: Simple (one click, whether shown as segments or arrows), undo, redo, undo.
            foreach (var wait in Choose("m.mode", 1))
            {
                yield return wait;
            }

            Check("Materials tab: Shading > Simple changes the part's material", rough.Material?.Mode == ShadingMode.SimpleLit, $"{rough.Material?.Mode}");
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var undone = rough.Material?.Mode ?? ShadingMode.Lit;
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            var redone = rough.Material?.Mode;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Materials tab: Shading change is one undo step (undo Lit, redo Simple)", undone == ShadingMode.Lit && redone == ShadingMode.SimpleLit,
                $"undo {undone}, redo {redone}, now {rough.Material?.Mode ?? ShadingMode.Lit}");

            // A whole Metallic drag is one undo step.
            foreach (var wait in DragRow(Track("m.metallic", 0.02f), Track("m.metallic", 1f), "Materials tab: drag Metallic to 1"))
            {
                yield return wait;
            }

            var dragged = rough.Material?.Metallic ?? 0f;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var afterUndo = rough.Material?.Metallic ?? 0f;
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            Check("Materials tab: a Metallic drag sets it (1) and undoes in one step (0), redo brings it back",
                dragged >= 0.99f && afterUndo == 0f && (rough.Material?.Metallic ?? 0f) >= 0.99f, $"drag {dragged}, undo {afterUndo}, redo {rough.Material?.Metallic}");

            // The colour: open it, raise saturation, move the hue; Part.Color changes like the Color tool would.
            var before = rough.Color;
            Step = "Materials tab: open Color";
            Click(Center(RowBox("m.color", SheetRowKind.Color)));
            yield return 4;
            foreach (var wait in DragRow(Track("m.color", 0.1f, SheetRowKind.Saturation), Track("m.color", 0.8f, SheetRowKind.Saturation), "Color: saturation"))
            {
                yield return wait;
            }

            foreach (var wait in DragRow(Track("m.color", 0.05f, SheetRowKind.Hue), Track("m.color", 0.6f, SheetRowKind.Hue), "Color: hue"))
            {
                yield return wait;
            }

            var colored = rough.Color;
            Check("Materials tab: the Color rows change the part's colour (Part.Color)",
                MathF.Abs(colored.Z - colored.X) > 0.2f && !colored.Equals(before), $"{ColorSpace.ToHex(before)} -> {ColorSpace.ToHex(colored)}");

            // A light on the selected sphere, its gizmo, then Remove and undo.
            Step = "Materials tab: add a point light";
            Click(Button("l.add", 0, 3));
            yield return 6;
            Check("Materials tab: Add Light (Point) gives the sphere a light", rough.Light?.Type == LightType.Point, $"{rough.Light?.Type}");
            foreach (var wait in Shot("gfx-ed-03-light-gizmo"))
            {
                yield return wait;
            }

            Step = "Materials tab: remove the light";
            Click(Button("l.remove", 0, 1));
            yield return 6;
            var removed = rough.Light is null;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Materials tab: Remove Light removes it; undo brings it back", removed && rough.Light?.Type == LightType.Point);

            // Add Light from the toolbar: a new spot light part, selected, pointing down.
            var count = PartCount;
            foreach (var wait in ToolbarButton(ToolbarControl.AddLight))
            {
                yield return wait;
            }

            Step = "Add Light > Spot Light";
            Click(FindMenuItem("Spot Light"));
            yield return 8;
            var spot = Selected;
            Check("Add Light: a new selected spot light part (invisible, no collision), pointing down",
                PartCount == count + 1 && spot is { Light.Type: LightType.Spot, CanCollide: false, Material.IsInvisible: true } && spot.Rotation.X == 90f,
                $"{PartCount - count} added, {spot?.Name} {spot?.Light?.Type} rotation {spot?.Rotation.X}");
            foreach (var wait in Shot("gfx-ed-04-add-spot"))
            {
                yield return wait;
            }

            // A model's own material, then back to imported.
            Step = "World: select Water Bottle";
            Click(PartRow("Water Bottle"));
            yield return 6;
            var bottle = PartNamed("Water Bottle");
            foreach (var wait in DragRow(Track("m.roughness", 0.5f), Track("m.roughness", 0.95f), "Materials tab: the bottle's roughness"))
            {
                yield return wait;
            }

            var own = bottle.ModelMaterials?.Count ?? 0;
            Step = "Materials tab: Back to Imported";
            Click(Button("m.resetImported", 0, 1));
            yield return 6;
            Check("Materials tab: editing a model material makes it the instance's own; Back to Imported removes it",
                own == 1 && bottle.ModelMaterials is null, $"own {own}, after {bottle.ModelMaterials?.Count}");

            // The Rendering tab: quality, the 3D View preference, the renderer.
            foreach (var wait in ChooseTab(EnvironmentTab.Rendering))
            {
                yield return wait;
            }

            Check("Rendering tab: its tab replaces Materials in the same panel", PanelMode == "Rendering", PanelMode);
            foreach (var wait in Choose("r.quality", 2))
            {
                yield return wait;
            }

            Check("Rendering tab: Quality High", Renderer.Graphics.Quality == QualityLevel.High, $"{Renderer.Graphics.Quality}");
            foreach (var wait in Choose("e.viewport", 1))
            {
                yield return wait;
            }

            var low = Renderer.EditorGraphics.ViewportQuality;
            yield return 4;
            var lowQuality = Renderer.Universal?.Quality;
            foreach (var wait in Choose("e.viewport", 0))
            {
                yield return wait;
            }

            Check("Rendering tab: the 3D View preference (Low) applies Low quality to the view, and goes back to As Project",
                low == ViewportQuality.Low && lowQuality is { PostProcessing: false } && Renderer.EditorGraphics.ViewportQuality == ViewportQuality.Project,
                $"{low}, post {lowQuality?.PostProcessing}");

            foreach (var wait in Choose("r.renderer", 0))
            {
                yield return wait;
            }

            yield return 4;
            var classic = !Renderer.UsingUniversal;
            foreach (var wait in Shot("gfx-ed-05-classic"))
            {
                yield return wait;
            }

            foreach (var wait in ChooseTab(EnvironmentTab.PostProcessing))
            {
                yield return wait;
            }

            // In Classic the Universal-only rows are greyed out; the Atmosphere's exposure, saturation and contrast stay.
            var classicRows = Rows;
            var classicGreyed = classicRows.Where(row => row.Key.StartsWith("r.", StringComparison.Ordinal)).All(row => !row.Enabled)
                && classicRows.Where(EnvironmentSheetRowIsAtmosphere).All(row => row.Enabled);
            foreach (var wait in ChooseTab(EnvironmentTab.Rendering))
            {
                yield return wait;
            }

            foreach (var wait in Choose("r.renderer", 1))
            {
                yield return wait;
            }

            yield return 4;
            Check("Rendering tab: Renderer Classic draws Classic (Post-Processing keeps only exposure, saturation and contrast); Universal again draws Universal",
                classic && classicGreyed && Renderer.UsingUniversal);

            // The Post-Processing tab: bloom on, tone mapping Filmic and undo, Exposure.
            foreach (var wait in ChooseTab(EnvironmentTab.PostProcessing))
            {
                yield return wait;
            }

            Step = "Post-Processing tab: Bloom on";
            Click(Switch("r.bloom"));
            yield return 6;
            Check("Post-Processing tab: Bloom switch", Renderer.Graphics.BloomEnabled);
            foreach (var wait in Choose("r.tone", 2))
            {
                yield return wait;
            }

            var filmic = Renderer.Graphics.ToneMapping;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Post-Processing tab: Filmic, and undo of the last change (back to Neutral)",
                filmic == ToneMapping.Filmic && Renderer.Graphics.ToneMapping == ToneMapping.Neutral, $"{filmic} -> {Renderer.Graphics.ToneMapping}");
            foreach (var wait in DragRow(Track("a.v.Exposure", 0.5f), Track("a.v.Exposure", 0.6f), "Post-Processing tab: drag Exposure"))
            {
                yield return wait;
            }

            Check("Post-Processing tab: Exposure (the Atmosphere's) drags to +0.60 EV", MathF.Abs(Renderer.Atmosphere.Exposure - 0.6f) < 0.03f && ValueOf("a.v.Exposure") == "+0.60 EV",
                $"{Renderer.Atmosphere.Exposure} shown {ValueOf("a.v.Exposure")}");
            foreach (var wait in Shot("gfx-ed-06-post-processing"))
            {
                yield return wait;
            }

            // The toolbar buttons: Environment closes the panel and reopens its last global tab; Material toggles Materials.
            foreach (var wait in ToolbarButton(ToolbarControl.Environment))
            {
                yield return wait;
            }

            var closed = PanelMode;
            foreach (var wait in ToolbarButton(ToolbarControl.Environment))
            {
                yield return wait;
            }

            var reopened = PanelMode;
            foreach (var wait in ToolbarButton(ToolbarControl.Material))
            {
                yield return wait;
            }

            var materials = PanelMode;
            foreach (var wait in ToolbarButton(ToolbarControl.Material))
            {
                yield return wait;
            }

            Check("Toolbar: Environment closes the panel and reopens it on its last tab (Post-Processing); Material opens and closes Materials",
                closed == "None" && reopened == "PostProcessing" && materials == "Materials" && PanelMode == "None", $"{closed}, {reopened}, {materials}, {PanelMode}");

            // An Atmosphere preset from the toolbar opens the Atmosphere tab; undo puts the edited Atmosphere back.
            _saved = Renderer.Atmosphere;
            foreach (var wait in ToolbarButton(ToolbarControl.Presets))
            {
                yield return wait;
            }

            Step = "Presets > Golden Hour";
            Click(FindMenuItem("Golden Hour"));
            yield return 8;
            var golden = Renderer.Atmosphere == AtmospherePresets.Find("Golden Hour")!.Settings && PanelMode == "Atmosphere";
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Presets: Golden Hour applies and shows the Atmosphere tab; undo restores the edited Atmosphere", golden && Renderer.Atmosphere == _saved, PanelMode);

            // Save, leave, reopen: every edit comes back.
            foreach (var wait in ProjectMenu("Save Project", "save"))
            {
                yield return wait;
            }

            foreach (var wait in LeaveToHub("leave after saving"))
            {
                yield return wait;
            }

            Check("Leave to Hub after saving: no question", Screen == "Hub" && !ConfirmOpen, Screen);
            foreach (var wait in OpenCard(GraphicsFlow.ProjectName, "reopen the graphics test"))
            {
                yield return wait;
            }

            yield return 20;
            var reloadedRough = PartNamed("Rough 0");
            Check("Reopened: the Atmosphere, rendering settings, material and light edits are back (Universal, High, bloom)",
                Renderer.Atmosphere == _saved && Renderer.Graphics is { Renderer: RendererKind.Universal, Quality: QualityLevel.High, BloomEnabled: true, ToneMapping: ToneMapping.Neutral }
                && reloadedRough is { Material.Metallic: >= 0.99f, Light.Type: LightType.Point },
                $"atmosphere {Renderer.Atmosphere == _saved}, {Renderer.Graphics.Renderer} {Renderer.Graphics.Quality} bloom {Renderer.Graphics.BloomEnabled}");
            foreach (var wait in ChooseSection("Environment & Graphics"))
            {
                yield return wait;
            }

            Check("Reopened: the panel opens on Atmosphere and shows the saved Sun Intensity (3.20)", PanelMode == "Atmosphere" && ValueOf("a.v.SunIntensity") == "3.20",
                $"{PanelMode} {ValueOf("a.v.SunIntensity")}");

            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }

        private static bool EnvironmentSheetRowIsAtmosphere(SheetRow row) => EnvironmentSheet.IsAtmosphere(row.Key);
    }
}
