using Wisko.Engine.Atmosphere;
using Wisko.Engine.Editor;
using Wisko.Engine.Graphics;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.Rendering.Universal;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The Environment &amp; Graphics panel (ToolBench > Environment &amp; Graphics): one place, over the left of the 3D View,
/// for the project's visual settings, in tabs: Atmosphere (sky, sun, ambient light, fog, haze), Lighting (environment
/// light, reflections, shadows), Rendering (renderer, quality, anti-aliasing, the person's 3D View preference),
/// Post-Processing (exposure, tone mapping, colour, bloom, ambient occlusion, vignette) and Materials (the selection's
/// surface material, a model's materials, and its light). Every setting is in exactly one tab; Atmosphere values are the
/// Atmosphere's own (Atmosphere.json, Atmosphere undo steps), the others the project's Graphics.json. Opens in Play
/// read-only; every edit is one undo step (a whole slider or colour drag, a switch, a choice, a texture, a button). A
/// primitive's colour is its Part.Color, exactly as the Color tool sets it.
/// </summary>
public sealed partial class Renderer
{
    private readonly PropertySheet _environmentSheet = new();
    private bool _environmentOpen;
    private EnvironmentTab _environmentTab = EnvironmentTab.Atmosphere;

    // The tab the Environment button and the section open: the last global tab shown (Materials has its own button).
    private EnvironmentTab _environmentGlobalTab = EnvironmentTab.Atmosphere;

    // The colour whose Hue / Saturation / Brightness rows are open, and the HSV being dragged (kept while dragging so a
    // grey keeps its hue).
    private string? _sheetColor;
    private AtmosphereHsv _sheetHsv;
    private Vector3? _sheetHsvSource;

    // A drag in progress: its row and what it started from (Parts for Materials, Graphics settings, or the Atmosphere).
    private SheetRow? _sheetDrag;
    private Part[]? _sheetDragParts;
    private PartSnapshot[]? _sheetDragBefore;
    private GraphicsSettings? _sheetDragSettings;
    private AtmosphereSettings? _sheetDragAtmosphere;

    // Which material of the selected model the Materials tab shows (reset when another model is selected).
    private Part? _materialModel;
    private int _materialIndex;

    /// <summary>
    /// Picks an image for a material texture and brings it into the project; returns its project-relative path, or null
    /// when cancelled or failed (the host reports failures). Set by the project session.
    /// </summary>
    internal Func<string?>? TextureRequested { get; set; }

    private bool EnvironmentPanelVisible => _environmentOpen && ActiveDocument is null && _workspace is not null;

    // Over the left of the 3D View (right of the Physics panel), from the tab strip down to the Output panel (World
    // keeps the right-hand column).
    private UiBox EnvironmentPanelRect
    {
        get
        {
            var top = ToolbarRenderer.ToolbarHeight + DocumentTabStrip.Height;
            var bottom = _outputPanelOn ? OutputRect.Y : _height;
            var width = MathF.Min(PropertySheet.MaxWidth, MathF.Max(PropertySheet.MinWidth, ContentWidth * 0.6f));
            return new UiBox(ContentLeft, top, MathF.Min(width, ContentWidth), MathF.Max(0f, bottom - top));
        }
    }

    private bool IsOverEnvironmentPanel(float x, float y) => EnvironmentPanelVisible && EnvironmentPanelRect.Contains(x, y);

    private bool UniversalAvailable => _backend == RenderBackendKind.OpenGL && _universal is { Started: false } or { Healthy: true };

    /// <summary>What this editor's renderer can draw (see <see cref="RendererCapabilities"/>).</summary>
    internal RendererCapabilities Capabilities => RendererCapabilities.For(_backend, UniversalAvailable);

    /// <summary>Shows the panel on <paramref name="tab"/>, or closes it (null). A drag in progress is recorded first.</summary>
    private void ShowEnvironmentPanel(EnvironmentTab? tab, bool showSceneView = false)
    {
        CommitSheetDrag();
        var open = tab is not null;
        if (open != _environmentOpen || (tab is { } changed && changed != _environmentTab))
        {
            _sheetColor = null;
            _sheetHsvSource = null;
            _environmentSheet.ResetScroll();
            Console.WriteLine(tab is { } shown ? $"[Environment] {EnvironmentSheet.TabNames[(int)shown]} tab" : "[Environment] panel closed");
        }

        _environmentOpen = open;
        if (tab is { } chosen)
        {
            _environmentTab = chosen;
            if (chosen != EnvironmentTab.Materials)
            {
                _environmentGlobalTab = chosen;
            }
        }

        if (open && showSceneView)
        {
            ActivateTab(DocumentTabHit.SceneView);
        }
    }

    /// <summary>Add Light: a new light in front of the camera, selected (one undo step).</summary>
    internal Part? AddLight(LightType type)
    {
        if (_workspace is null || _selection is null)
        {
            return null;
        }

        var part = PartSpawner.CreateLightInFrontOfCamera(_workspace, _camera, type);
        _workspace.AddPart(part);
        _partRenderer.RegisterPart(part);
        _selection.Select(part);
        SyncPartRendererSelection();
        _toolState.SetTool(EditorToolMode.Select);
        _editorHistory.Push(EditorCommand.Add(part));
        Console.WriteLine($"[AddLight] Created {part.Name} ({type}) at ({part.Position.X:F2}, {part.Position.Y:F2}, {part.Position.Z:F2})");
        if (!UsingUniversal)
        {
            _toast.Show("Lights show with the Universal renderer (Environment & Graphics > Rendering).", 5f);
        }

        return part;
    }

    // ------------------------------------------------------------------------------------------------- what is shown

    private List<SheetRow> SheetRows() => _environmentTab == EnvironmentTab.Materials
        ? MaterialSheet.Rows(MaterialTarget().Target, _sheetColor, UsingUniversal, _playMode.IsPlaying, Capabilities)
        : EnvironmentSheet.Rows(_environmentTab, EffectiveAtmosphere, _graphicsState.Settings, EditorGraphics, Capabilities, _sheetColor, _playMode.IsPlaying);

    // The Parts a Material edit changes: the one selected model (its shown material), else the selected primitives
    // (models in a mixed selection are skipped).
    private (MaterialSheetTarget? Target, Part[] Parts) MaterialTarget()
    {
        var selected = _selection?.SelectedParts ?? [];
        if (selected.Count == 0)
        {
            return (null, []);
        }

        var first = selected[0];
        if (selected.Count == 1 && first.Model?.Asset is { } asset)
        {
            if (!ReferenceEquals(_materialModel, first))
            {
                _materialModel = first;
                _materialIndex = 0;
            }

            var materials = asset.Model.Materials;
            _materialIndex = global::System.Math.Clamp(_materialIndex, 0, global::System.Math.Max(0, materials.Count - 1));
            var names = materials.Select((m, i) => string.IsNullOrWhiteSpace(m.Name) ? $"Material {i + 1}" : m.Name).ToArray();
            var edited = first.ModelMaterials?.Get(_materialIndex);
            var shown = edited ?? MaterialResolver.ImportedAsSurface(asset, _materialIndex, ProjectRoot);
            return (new MaterialSheetTarget($"{first.Name} (model)", true, shown, shown.BaseColor, names, _materialIndex, edited is not null,
                first.Light, ShowLight: true), [first]);
        }

        var primitives = selected.Where(p => p.Model is null).ToArray();
        if (primitives.Length == 0)
        {
            return (null, []);
        }

        var shownPart = primitives[0];
        var label = primitives.Length == 1 ? shownPart.Name : $"{primitives.Length} parts";
        if (primitives.Length < selected.Count)
        {
            label += " (models skipped)";
        }

        return (new MaterialSheetTarget(label, false, shownPart.Material ?? SurfaceMaterial.Default, shownPart.Color, [], 0, false,
            shownPart.Light, ShowLight: true), primitives);
    }

    // ------------------------------------------------------------------------------------------------- input

    private void HandleEnvironmentPanel(InputManager input)
    {
        if (!EnvironmentPanelVisible)
        {
            CommitSheetDrag();
            return;
        }

        _environmentSheet.SetTabs(EnvironmentSheet.TabNames, (int)_environmentTab);
        var panel = EnvironmentPanelRect;
        if (_sheetDrag is { } drag)
        {
            _toolbarConsumedClick = true;
            if (input.IsLeftMouseHeld)
            {
                ApplySheetDrag(drag, PropertySheet.FractionAt(panel, input.MouseX));
            }
            else
            {
                CommitSheetDrag();
            }

            return;
        }

        if (!panel.Contains(input.MouseX, input.MouseY))
        {
            return;
        }

        if (!_wheelConsumed && input.ScrollDelta != 0f)
        {
            _environmentSheet.Scroll(-input.ScrollDelta * 30f, panel, SheetRows());
            _wheelConsumed = true;
        }

        if (!input.LeftMousePressed || _toolbarConsumedClick)
        {
            return;
        }

        _toolbarConsumedClick = true;
        if (_environmentSheet.HitTest(panel, SheetRows(), input.MouseX, input.MouseY) is { } hit)
        {
            HandleSheetHit(hit);
        }
    }

    // A click on the sheet (also used by the tests to drive the panel the way a click does).
    private void HandleSheetHit(SheetHit hit)
    {
        switch (hit.Part)
        {
            case SheetHitPart.Close:
                ShowEnvironmentPanel(null);
                return;
            case SheetHitPart.Tab:
                ShowEnvironmentPanel((EnvironmentTab)global::System.Math.Clamp(hit.Index, 0, EnvironmentSheet.TabNames.Length - 1));
                return;
        }

        var row = hit.Row!;

        // Editor-only state: which colour is open, which model material is shown, the person's 3D View preference.
        if (hit.Part == SheetHitPart.Color)
        {
            _sheetColor = _sheetColor == row.Key ? null : row.Key;
            _sheetHsvSource = null;
            return;
        }

        if (row.Key == "m.index")
        {
            _materialIndex = MaterialSheet.Step(hit.Part, hit.Index, _materialIndex, row.Options.Count);
            _sheetColor = null;
            return;
        }

        if (row.Key == "e.viewport")
        {
            EditorGraphics = RenderingSheet.ClickPreference(EditorGraphics, row.Key, hit.Part, hit.Index);
            EditorGraphicsChanged?.Invoke(EditorGraphics);
            Console.WriteLine($"[Graphics] 3D View quality: {EditorGraphics.ViewportQuality}");
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show("Stop Play Mode to change these settings.");
            return;
        }

        if (EnvironmentSheet.IsAtmosphere(row.Key))
        {
            HandleAtmosphereHit(row, hit);
        }
        else if (_environmentTab == EnvironmentTab.Materials)
        {
            HandleMaterialHit(row, hit);
        }
        else
        {
            HandleRenderingHit(row, hit);
        }
    }

    private void HandleAtmosphereHit(SheetRow row, SheetHit hit)
    {
        switch (hit.Part)
        {
            case SheetHitPart.Slider:
                // One drag = one undo step: remember where it started, apply live, record on release.
                SyncSheetHsv();
                _sheetDrag = row;
                _sheetDragAtmosphere = _atmosphere.Settings;
                ApplySheetDrag(row, hit.Fraction);
                return;
            case SheetHitPart.Button when row.Key == "a.reset":
                ResetAtmosphere();
                return;
            case SheetHitPart.Toggle when EnvironmentSheet.Click(_atmosphere.Settings, row.Key) is { } change:
                ChangeAtmosphere(change.Settings, change.Label);
                return;
        }
    }

    private void HandleRenderingHit(SheetRow row, SheetHit hit)
    {
        if (hit.Part == SheetHitPart.Slider)
        {
            _sheetDrag = row;
            _sheetDragSettings = _graphicsState.Settings;
            ApplySheetDrag(row, hit.Fraction);
            return;
        }

        var label = hit.Part == SheetHitPart.Button ? row.Options[hit.Index] : row.Label;
        ChangeRenderSettings(RenderingSheet.Click(_graphicsState.Settings, row.Key, hit.Part, hit.Index), label);
    }

    private void HandleMaterialHit(SheetRow row, SheetHit hit)
    {
        var (target, parts) = MaterialTarget();
        if (target is null || parts.Length == 0)
        {
            return;
        }

        var key = row.Key;
        switch (hit.Part)
        {
            case SheetHitPart.Slider:
                // One drag = one undo step: remember where it started, apply live, record on release.
                SyncSheetHsv();
                _sheetDrag = row;
                _sheetDragParts = parts;
                _sheetDragBefore = parts.Select(PartSnapshot.FromPart).ToArray();
                ApplySheetDrag(row, hit.Fraction);
                return;

            case SheetHitPart.Toggle or SheetHitPart.Option or SheetHitPart.Previous or SheetHitPart.Next:
                ChangeParts(row.Label, parts, key.StartsWith("l.", StringComparison.Ordinal)
                    ? () => EditLights(parts, light => MaterialSheet.ClickLight(light, key, hit.Part, hit.Index))
                    : () => EditMaterials(parts, (m, color) => MaterialSheet.Click(m, color, key, hit.Part, hit.Index)));
                return;

            case SheetHitPart.Browse when MaterialSheet.SlotOf(key) is { } slot:
                if (ProjectRoot is null)
                {
                    _toast.Show("Save the project first: textures are copied into it.", 5f);
                    return;
                }

                if (TextureRequested?.Invoke() is { } path)
                {
                    ChangeParts(row.Label, parts, () => EditMaterials(parts, (m, _) => m.WithTexture(slot, path).Sanitized()));
                }

                return;

            case SheetHitPart.Clear when MaterialSheet.SlotOf(key) is { } slot:
                ChangeParts(row.Label, parts, () => EditMaterials(parts, (m, _) => m.WithTexture(slot, null)));
                return;

            case SheetHitPart.Button:
                switch (key)
                {
                    case "l.add":
                        var type = (LightType)global::System.Math.Clamp(hit.Index, 0, 2);
                        ChangeParts("Add Light", parts, () => SetLights(parts, PartLight.Create(type)));
                        if (!UsingUniversal)
                        {
                            _toast.Show("Lights show with the Universal renderer (Environment & Graphics > Rendering).", 5f);
                        }

                        return;
                    case "l.remove":
                        ChangeParts("Remove Light", parts, () => SetLights(parts, null));
                        return;
                    case "m.reset" or "m.resetImported":
                        ChangeParts(target.IsModel ? "Back to Imported" : "Reset Material", parts, () => ResetMaterials(parts));
                        return;
                }

                return;
        }
    }

    private void ApplySheetDrag(SheetRow row, float fraction)
    {
        if (_sheetDragAtmosphere is not null)
        {
            if (row.Kind == SheetRowKind.Slider)
            {
                _atmosphere.Set(EnvironmentSheet.Slide(_atmosphere.Settings, row.Key, fraction));
            }
            else if (EnvironmentSheet.SlotOf(row.Key) is { } slot && DragHsv(row.Kind, fraction))
            {
                var color = _sheetHsv.ToColor();
                _atmosphere.Set(_atmosphere.Settings.With(slot, color));
                _sheetHsvSource = color.ToVector3();
            }

            return;
        }

        if (_sheetDragSettings is not null)
        {
            _graphicsState.Set(RenderingSheet.Slide(_graphicsState.Settings, row.Key, fraction));
            return;
        }

        if (_sheetDragParts is not { } parts)
        {
            return;
        }

        var key = row.Key;
        switch (row.Kind)
        {
            case SheetRowKind.Slider when key.StartsWith("l.", StringComparison.Ordinal):
                EditLights(parts, light => MaterialSheet.SlideLight(light, key, fraction));
                return;
            case SheetRowKind.Slider:
                EditMaterials(parts, (m, _) => MaterialSheet.Slide(m, key, fraction));
                return;
        }

        if (!DragHsv(row.Kind, fraction))
        {
            return;
        }

        var snapped = ColorSpace.Snap(_sheetHsv.ToColor().ToVector3());
        _sheetHsvSource = snapped;
        SetSheetColor(parts, key, snapped);
    }

    // A Hue / Saturation / Brightness row moved: the HSV being edited follows it (false for other rows).
    private bool DragHsv(SheetRowKind kind, float fraction)
    {
        switch (kind)
        {
            case SheetRowKind.Hue:
                _sheetHsv = _sheetHsv with { H = global::System.Math.Min(fraction, 0.999f) };
                return true;
            case SheetRowKind.Saturation:
                _sheetHsv = _sheetHsv with { S = fraction };
                return true;
            case SheetRowKind.Brightness:
                _sheetHsv = _sheetHsv with { V = fraction };
                return true;
            default:
                return false;
        }
    }

    private void CommitSheetDrag()
    {
        var drag = _sheetDrag;
        var parts = _sheetDragParts;
        var before = _sheetDragBefore;
        var settings = _sheetDragSettings;
        var atmosphere = _sheetDragAtmosphere;
        _sheetDrag = null;
        _sheetDragParts = null;
        _sheetDragBefore = null;
        _sheetDragSettings = null;
        _sheetDragAtmosphere = null;
        if (drag is null)
        {
            return;
        }

        var colorDrag = drag.Kind is SheetRowKind.Hue or SheetRowKind.Saturation or SheetRowKind.Brightness;
        if (atmosphere is not null)
        {
            var atmosphereLabel = colorDrag && EnvironmentSheet.SlotOf(drag.Key) is { } slot ? SlotLabel(slot) : drag.Label;
            RecordAtmosphereChange(atmosphere, _atmosphere.Settings, atmosphereLabel);
            return;
        }

        var label = colorDrag ? ColorLabel(drag.Key) : drag.Label;
        if (settings is not null)
        {
            RecordRenderSettings(settings, _graphicsState.Settings, label);
            return;
        }

        if (parts is not null && before is not null)
        {
            RecordParts(label, parts, before);
        }
    }

    // Escape while dragging: back to where the drag started, nothing recorded.
    private bool CancelSheetDrag()
    {
        if (_sheetDrag is null)
        {
            return false;
        }

        if (_sheetDragAtmosphere is { } atmosphere)
        {
            _atmosphere.Set(atmosphere);
        }
        else if (_sheetDragSettings is { } settings)
        {
            _graphicsState.Set(settings);
        }
        else if (_sheetDragParts is { } parts && _sheetDragBefore is { } before)
        {
            for (var i = 0; i < parts.Length; i++)
            {
                before[i].ApplyGraphics(parts[i]);
                parts[i].Color = before[i].Color;
            }
        }

        DropAtmosphereDrag();
        _sheetDrag = null;
        _sheetDragParts = null;
        _sheetDragBefore = null;
        _sheetDragSettings = null;
        _sheetHsvSource = null;
        return true;
    }

    // Entering a project mid-drag (never in practice): the drag is forgotten, nothing recorded.
    private void DropAtmosphereDrag()
    {
        if (_sheetDragAtmosphere is null)
        {
            return;
        }

        _sheetDrag = null;
        _sheetDragAtmosphere = null;
    }

    // Escape: cancel a drag, then fold an open colour, then close the panel.
    private bool TryConsumeEnvironmentEscape()
    {
        if (CancelSheetDrag())
        {
            return true;
        }

        if (!EnvironmentPanelVisible)
        {
            return false;
        }

        if (_sheetColor is not null)
        {
            _sheetColor = null;
            return true;
        }

        ShowEnvironmentPanel(null);
        return true;
    }

    // ------------------------------------------------------------------------------------------------- edits

    // Applies a change to the shown material of every Part (a primitive's own material, or the model's shown material,
    // which becomes its own on first edit); a primitive whose material is back to the default stores none.
    private bool EditMaterials(Part[] parts, Func<SurfaceMaterial, Vector3, SurfaceMaterial> change)
    {
        var changed = false;
        foreach (var part in parts)
        {
            if (part.Model?.Asset is { } asset)
            {
                var current = part.ModelMaterials?.Get(_materialIndex) ?? MaterialResolver.ImportedAsSurface(asset, _materialIndex, ProjectRoot);
                var next = change(current, current.BaseColor);
                if (next != current)
                {
                    part.ModelMaterials = (part.ModelMaterials ?? MaterialOverrideSet.Empty).With(_materialIndex, next);
                    changed = true;
                }
            }
            else
            {
                var current = part.Material ?? SurfaceMaterial.Default;
                var next = change(current, part.Color);
                if (next != current)
                {
                    part.Material = next == SurfaceMaterial.Default ? null : next;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool EditLights(Part[] parts, Func<PartLight, PartLight> change)
    {
        var changed = false;
        foreach (var part in parts)
        {
            if (part.Light is { } light && change(light) is var next && next != light)
            {
                part.Light = next;
                changed = true;
            }
        }

        return changed;
    }

    private static bool SetLights(Part[] parts, PartLight? light)
    {
        var changed = false;
        foreach (var part in parts)
        {
            if (!Equals(part.Light, light))
            {
                part.Light = light;
                changed = true;
            }
        }

        return changed;
    }

    private bool ResetMaterials(Part[] parts)
    {
        var changed = false;
        foreach (var part in parts)
        {
            if (part.Model is not null)
            {
                if (part.ModelMaterials?.Get(_materialIndex) is not null)
                {
                    var rest = part.ModelMaterials.With(_materialIndex, null);
                    part.ModelMaterials = rest.Count == 0 ? null : rest;
                    changed = true;
                }
            }
            else if (part.Material is not null)
            {
                part.Material = null;
                changed = true;
            }
        }

        return changed;
    }

    // "m.color" is a primitive's Part.Color (a model material's base colour); the others are material or light colours.
    private void SetSheetColor(Part[] parts, string key, Vector3 color)
    {
        switch (key)
        {
            case "m.color":
                foreach (var part in parts.Where(p => p.Model is null))
                {
                    part.Color = color;
                }

                EditMaterials(parts.Where(p => p.Model is not null).ToArray(), (m, _) => (m with { BaseColor = color }).Sanitized());
                return;
            case "l.color":
                EditLights(parts, light => (light with { Color = color }).Sanitized());
                return;
            default:
                EditMaterials(parts, (m, _) => MaterialSheet.SetColor(m, key, color));
                return;
        }
    }

    private void ChangeParts(string label, Part[] parts, Func<bool> apply)
    {
        var before = parts.Select(PartSnapshot.FromPart).ToArray();
        if (apply())
        {
            RecordParts(label, parts, before);
        }
    }

    private void RecordParts(string label, Part[] parts, PartSnapshot[] before)
    {
        var after = parts.Select(PartSnapshot.FromPart).ToArray();
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            changed |= !before[i].SameGraphics(after[i]) || !before[i].Color.Equals(after[i].Color);
        }

        if (!changed)
        {
            return;
        }

        _editorHistory.Push(EditorCommand.GraphicsChange(label, parts, before, after));
        Console.WriteLine($"[Graphics] {label}: {(parts.Length == 1 ? parts[0].Name : $"{parts.Length} parts")}");
    }

    private void ChangeRenderSettings(GraphicsSettings after, string label)
    {
        var before = _graphicsState.Settings;
        _graphicsState.Set(after);
        RecordRenderSettings(before, _graphicsState.Settings, label);
    }

    private void RecordRenderSettings(GraphicsSettings before, GraphicsSettings after, string label)
    {
        if (before == after)
        {
            return;
        }

        _editorHistory.Push(EditorCommand.RenderSettingsChange(_graphicsState, before, after, label));
        Console.WriteLine($"[Graphics] Rendering: {label}");
    }

    // ------------------------------------------------------------------------------------------------- drawing

    private void RenderEnvironmentPanel(float dpiScale)
    {
        if (!EnvironmentPanelVisible)
        {
            return;
        }

        SyncSheetHsv();
        _environmentSheet.SetTabs(EnvironmentSheet.TabNames, (int)_environmentTab);
        _environmentSheet.Render(EnvironmentPanelRect, EnvironmentSheet.Title, SheetRows(), _sheetHsv, _sheetColor, _playMode.IsPlaying,
            _lastMouseX, _lastMouseY, _width, _height, dpiScale);
    }

    // The H/S/B rows follow the open colour unless it is the one they just produced (a grey keeps its hue).
    private void SyncSheetHsv()
    {
        if (_sheetColor is not { } key)
        {
            return;
        }

        var color = EnvironmentSheet.SlotOf(key) is { } slot
            ? _atmosphere.Settings.Get(slot).ToVector3()
            : _environmentTab == EnvironmentTab.Materials && MaterialTarget().Target is { } target
                ? key switch
                {
                    "m.color" => target.Color,
                    "m.emissive" => target.Material.EmissiveColor,
                    "m.outlineColor" => target.Material.OutlineColor,
                    "l.color" => target.Light?.Color ?? new Vector3(1f, 1f, 1f),
                    _ => (Vector3?)null
                }
                : null;
        if (color is { } c && !Equals(_sheetHsvSource, c))
        {
            _sheetHsvSource = c;
            _sheetHsv = AtmosphereHsv.FromColor(AtmosphereColor.FromRgb(c.X, c.Y, c.Z));
        }
    }

    private static string ColorLabel(string key) => key switch
    {
        "m.emissive" => "Glow Color",
        "m.outlineColor" => "Outline Color",
        "l.color" => "Light Color",
        _ => "Color"
    };
}
