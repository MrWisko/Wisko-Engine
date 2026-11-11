using Wisko.Engine.Atmosphere;
using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.Text;
using Wisko.Engine.World;

namespace Wisko.Engine.UI;

/// <summary>A small menu opened from the top toolbar.</summary>
internal enum ToolbarMenu
{
    None,
    ToolBench,
    AddMeshes,
    Project,
    Presets,
    GuiPreview,
    AddLight
}

/// <summary>What a toolbar menu item does.</summary>
internal enum ToolbarCommand
{
    ChooseSection,
    AddShape,
    ImportModel,
    SaveProject,
    LeaveToHub,

    /// <summary>Deletes the open project (after a confirmation); only for a project saved on disk.</summary>
    DeleteProject,

    /// <summary>Opens the Physics panel (ToolBench menu); the current section stays.</summary>
    OpenPhysics,

    /// <summary>Opens the Animation panel (ToolBench menu): clips, preview and animation settings of a model.</summary>
    OpenAnimation,

    /// <summary>Applies the Atmosphere preset named by the item's label.</summary>
    ApplyPreset,
    ResetAtmosphere,

    /// <summary>Sets the GUI canvas preview size (<see cref="ToolbarMenuItem.Preview"/>).</summary>
    SetGuiPreview,

    /// <summary>Adds a light of <see cref="ToolbarMenuItem.Light"/> in front of the camera.</summary>
    AddLight
}

/// <summary>
/// One toolbar menu entry; <see cref="Section"/> applies to ChooseSection, <see cref="Shape"/> to AddShape. A
/// <see cref="Destructive"/> entry (Delete Project) is drawn red.
/// </summary>
internal readonly record struct ToolbarMenuItem(
    string Label,
    ToolbarCommand Command,
    ToolBenchSection Section = ToolBenchSection.MainTools,
    ShapeType Shape = ShapeType.Block,
    bool SeparatorBefore = false,
    GuiPreviewSize Preview = GuiPreviewSize.Viewport,
    bool Destructive = false,
    Graphics.LightType Light = Graphics.LightType.Point);

/// <summary>
/// A selection's on/off property (Part.Anchored for Anchor, Part.Locked for Lock) as the toolbar and the Physics panel
/// show it, read fresh from the selected Parts (never stored).
/// </summary>
internal enum ToggleState
{
    /// <summary>Nothing selected.</summary>
    None,
    Off,
    On,

    /// <summary>Some selected Parts have it on, some do not.</summary>
    Mixed
}

/// <summary>The editor state the toolbar shows (the toolbar itself only keeps which menu is open).</summary>
internal readonly record struct ToolbarView(
    EditorToolMode ActiveTool,
    bool WorldOn,
    bool ColorOn,
    bool OutputOn,
    bool IsPlaying,
    bool EnvironmentOn = false,
    string? MatchingPreset = null,
    bool PhysicsOn = false,
    ToggleState Anchor = ToggleState.None,
    ToggleState Lock = ToggleState.None,
    GuiPreviewSize GuiPreview = GuiPreviewSize.Viewport,
    bool HasProject = false,
    bool AnimationOn = false,
    bool MaterialOn = false);

/// <summary>
/// The top toolbar: ToolBench (far left, chooses the section) and the project button next to it, the current section's
/// controls centred, and Play / Stop (far right, in every section). ToolBench, the project button, Add Meshes and Presets open
/// small menus, drawn by <see cref="RenderMenu"/> over the side panels. Narrow windows get compact pinned buttons and
/// a proportionally narrower section row; no control is hidden.
/// </summary>
public sealed class ToolbarRenderer : IDisposable
{
    public const float ToolbarHeight = 38f;
    private const float ButtonHeight = 28f;
    private const float ButtonGap = 10f;
    private const float EdgeMargin = 8f;

    // Space kept between the centred section row and the pinned buttons, and between ToolBench and the project button.
    private const float PinnedGap = 12f;
    private const float ClusterGap = 6f;
    private const float ToolBenchWidth = 146f;
    private const float CompactToolBenchWidth = 48f;
    private const float ProjectButtonWidth = 46f;
    private const float PlayButtonWidth = 92f;
    private const float CompactPlayButtonWidth = 72f;

    // Windows narrower than this get the compact (icon-only) ToolBench button and a narrower Play button.
    private const int CompactBelowWidth = 900;

    private const float MenuWidth = 188f;
    private const float MenuItemHeight = 30f;
    private const float MenuPadding = 4f;
    private const float MenuHeaderHeight = 26f;
    private const float MenuSeparatorHeight = 9f;
    private const float LabelPadding = 12f;
    private const float CaretSize = 8f;
    private const float CaretRoom = 16f;

    private static readonly Vector3 BarColor = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 Separator = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 Inactive = new(0.24f, 0.26f, 0.30f);
    private static readonly Vector3 InactiveText = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 ActiveFill = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 ActiveBorder = new(0.55f, 0.75f, 1.0f);
    private static readonly Vector3 ActiveText = new(1f, 1f, 1f);
    private static readonly Vector3 DisabledFill = new(0.18f, 0.19f, 0.22f);
    private static readonly Vector3 DisabledText = new(0.45f, 0.47f, 0.52f);
    private static readonly Vector3 MenuFill = new(0.16f, 0.17f, 0.20f);
    private static readonly Vector3 MenuHover = new(0.24f, 0.26f, 0.31f);
    private static readonly Vector3 MenuCurrent = new(0.20f, 0.27f, 0.40f);
    private static readonly Vector3 MenuMuted = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 PlayAccent = new(0.36f, 0.80f, 0.52f);
    private static readonly Vector3 PlayingFill = new(0.15f, 0.47f, 0.31f);
    private static readonly Vector3 PlayingBorder = new(0.40f, 0.78f, 0.55f);

    // Delete Project: red like the confirmation's Delete button (ConfirmDialog), set apart below Leave to Hub.
    private static readonly Vector3 DestructiveText = new(0.95f, 0.50f, 0.48f);
    private static readonly Vector3 DestructiveHover = new(0.66f, 0.22f, 0.22f);

    // Anchor and Lock show their state quietly: on = a lighter box than the other buttons, mixed = the plain box with
    // a light outline (no tool-style blue: they are properties of the selection, not the active tool).
    private static readonly Vector3 AnchoredFill = new(0.36f, 0.39f, 0.45f);
    private static readonly Vector3 AnchoredText = new(0.95f, 0.96f, 0.98f);
    private static readonly Vector3 MixedOutline = new(0.52f, 0.56f, 0.63f);

    // Single source of truth for the primitives Add Meshes offers: display label + underlying ShapeType. "Cube" is the
    // editor label for ShapeType.Block (serialized as "Block").
    private static readonly (ShapeType Shape, string Label)[] ShapeOptions =
    [
        (ShapeType.Block, "Cube"),
        (ShapeType.Cylinder, "Cylinder"),
        (ShapeType.Wedge, "Wedge"),
        (ShapeType.Sphere, "Sphere"),
        (ShapeType.Pyramid, "Pyramid"),
    ];

    // The sections, then Physics and Animation: auxiliary panels opened from ToolBench, not sections.
    private static readonly ToolbarMenuItem[] ToolBenchItems =
    [
        .. ToolBench.Sections.Select(section => new ToolbarMenuItem(ToolBench.LabelOf(section), ToolbarCommand.ChooseSection, Section: section)),
        new("Physics", ToolbarCommand.OpenPhysics, SeparatorBefore: true),
        new("Animation", ToolbarCommand.OpenAnimation)
    ];

    private static readonly ToolbarMenuItem[] AddMeshesItems =
    [
        .. ShapeOptions.Select(option => new ToolbarMenuItem(option.Label, ToolbarCommand.AddShape, Shape: option.Shape)),
        new("Import Model…", ToolbarCommand.ImportModel, SeparatorBefore: true)
    ];

    private static readonly ToolbarMenuItem[] ProjectItems =
    [
        new("Save Project", ToolbarCommand.SaveProject),
        new("Import Model…", ToolbarCommand.ImportModel),
        new("Leave to Hub", ToolbarCommand.LeaveToHub, SeparatorBefore: true),
        new("Delete Project…", ToolbarCommand.DeleteProject, SeparatorBefore: true, Destructive: true)
    ];

    private static readonly ToolbarMenuItem[] PresetItems =
    [
        .. AtmospherePresets.All.Select(preset => new ToolbarMenuItem(preset.Name, ToolbarCommand.ApplyPreset)),
        new("Reset to Default", ToolbarCommand.ResetAtmosphere, SeparatorBefore: true)
    ];

    private static readonly ToolbarMenuItem[] AddLightItems =
    [
        new("Point Light", ToolbarCommand.AddLight, Light: Graphics.LightType.Point),
        new("Spot Light", ToolbarCommand.AddLight, Light: Graphics.LightType.Spot),
        new("Directional Light", ToolbarCommand.AddLight, Light: Graphics.LightType.Directional)
    ];

    private static readonly ToolbarMenuItem[] GuiPreviewItems =
    [
        new("Viewport", ToolbarCommand.SetGuiPreview, Preview: GuiPreviewSize.Viewport),
        new("1280 × 720", ToolbarCommand.SetGuiPreview, Preview: GuiPreviewSize.Hd720),
        new("1920 × 1080", ToolbarCommand.SetGuiPreview, Preview: GuiPreviewSize.Hd1080)
    ];

    private IUiRenderer _screenUi = null!;
    private ToolBench _bench = null!;

    internal void Initialize(IUiBackend ui, ToolBench bench)
    {
        _screenUi = ui.CreateRenderer();
        _bench = bench;
    }

    internal ToolbarMenu OpenMenu { get; private set; }

    internal bool IsMenuOpen => OpenMenu != ToolbarMenu.None;

    internal void ToggleMenu(ToolbarMenu menu) => OpenMenu = OpenMenu == menu ? ToolbarMenu.None : menu;

    internal void CloseMenu() => OpenMenu = ToolbarMenu.None;

    internal bool TryHitToolBench(float mouseX, float mouseY, int screenWidth) => GetLayout(screenWidth).ToolBench.Contains(mouseX, mouseY);

    // The project button (where Leave used to be): opens Save Project / Import Model / Leave to Hub / Delete Project.
    internal bool TryHitProject(float mouseX, float mouseY, int screenWidth) => GetLayout(screenWidth).Project.Contains(mouseX, mouseY);

    // Play / Stop, pinned to the top-right in every section.
    public bool TryHitPlay(float mouseX, float mouseY, int screenWidth) => GetLayout(screenWidth).Play.Contains(mouseX, mouseY);

    internal bool TryHitControl(float mouseX, float mouseY, int screenWidth, out ToolbarControl control)
    {
        var layout = GetLayout(screenWidth);
        for (var i = 0; i < layout.Controls.Length; i++)
        {
            if (layout.Controls[i].Contains(mouseX, mouseY))
            {
                control = layout.ControlIds[i];
                return true;
            }
        }

        control = default;
        return false;
    }

    internal bool TryHitMenuItem(float mouseX, float mouseY, int screenWidth, out ToolbarMenuItem item)
    {
        item = default;
        if (!IsMenuOpen)
        {
            return false;
        }

        var items = ItemsOf(OpenMenu);
        var rects = ItemRects(MenuRect(screenWidth, OpenMenu), OpenMenu, items);
        for (var i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(mouseX, mouseY))
            {
                item = items[i];
                return true;
            }
        }

        return false;
    }

    internal bool IsOverMenu(float mouseX, float mouseY, int screenWidth) =>
        IsMenuOpen && MenuRect(screenWidth, OpenMenu).Contains(mouseX, mouseY);

    public bool IsOverToolbar(float mouseY) => mouseY >= 0f && mouseY <= ToolbarHeight;

    internal bool IsOverToolbarUi(float mouseX, float mouseY, int screenWidth) =>
        IsOverToolbar(mouseY) || IsOverMenu(mouseX, mouseY, screenWidth);

    internal void Render(int screenWidth, int screenHeight, float dpiScale, ToolbarView view)
    {
        _screenUi.BeginBatch(dpiScale);
        _screenUi.DrawRect(0f, 0f, screenWidth, ToolbarHeight, BarColor);
        if (view.IsPlaying)
        {
            // Play Mode: a thin accent line under the whole toolbar.
            _screenUi.DrawRect(0f, ToolbarHeight - 2f, screenWidth, 2f, PlayAccent);
        }
        else
        {
            _screenUi.DrawRect(0f, ToolbarHeight - 1f, screenWidth, 1f, Separator);
        }

        var layout = GetLayout(screenWidth);
        DrawToolBenchButton(layout);
        DrawProjectButton(layout.Project);

        // One label size for the whole row: when narrow buttons force a label smaller, they all shrink alike.
        var shortLabels = layout.Scale < 0.8f;
        var labels = new string[layout.ControlIds.Length];
        var labelFactor = 1f;
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = LabelOf(layout.ControlIds[i], shortLabels);
            labelFactor = MathF.Min(labelFactor, FitLabelScale(labels[i], LabelBox(layout.Controls[i], layout.ControlIds[i]).Width, 2.1f) / 2.1f);
        }

        for (var i = 0; i < labels.Length; i++)
        {
            var control = layout.ControlIds[i];
            var rect = layout.Controls[i];
            var isActive = IsActive(control, view);
            Vector3 text;
            float scale;
            if (view.IsPlaying && control is not (ToolbarControl.World or ToolbarControl.Output or ToolbarControl.Environment or ToolbarControl.Material))
            {
                // Editing is unavailable while playing: shown, but quiet.
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, isActive ? Inactive : DisabledFill);
                text = DisabledText;
                scale = 2.0f;
            }
            else if (control is ToolbarControl.Anchor or ToolbarControl.Lock)
            {
                (text, scale) = DrawToggleBox(rect, control == ToolbarControl.Anchor ? view.Anchor : view.Lock);
            }
            else if (isActive)
            {
                _screenUi.DrawRect(rect.X - 1f, rect.Y - 1f, rect.Width + 2f, rect.Height + 2f, ActiveBorder);
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, ActiveFill);
                text = ActiveText;
                scale = 2.1f;
            }
            else
            {
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Inactive);
                text = InactiveText;
                scale = 2.0f;
            }

            if (HasMenu(control))
            {
                UiIcons.DrawCaretDown(_screenUi, rect.X + rect.Width - CaretRoom * 0.5f - 4f, rect.Y + rect.Height * 0.5f, CaretSize * MathF.Min(1f, layout.Scale + 0.2f), text);
            }

            DrawCenteredLabel(LabelBox(rect, control), labels[i], text, scale * labelFactor);
        }

        DrawPlayButton(layout.Play, view.IsPlaying);
        _screenUi.Flush(screenWidth, screenHeight);
    }

    /// <summary>The open menu, if any (drawn after the side panels so it is on top of them).</summary>
    internal void RenderMenu(int screenWidth, int screenHeight, float dpiScale, ToolbarView view, float mouseX, float mouseY)
    {
        if (!IsMenuOpen)
        {
            return;
        }

        var menu = OpenMenu;
        var items = ItemsOf(menu);
        var panel = MenuRect(screenWidth, menu);
        var rects = ItemRects(panel, menu, items);
        _screenUi.BeginBatch(dpiScale);
        _screenUi.DrawRect(panel.X - 1f, panel.Y - 1f, panel.Width + 2f, panel.Height + 2f, Separator);
        _screenUi.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, MenuFill);
        if (menu == ToolbarMenu.ToolBench)
        {
            var headerY = panel.Y + MenuPadding + (MenuHeaderHeight - _screenUi.MeasureLineHeight(1.6f)) * 0.5f;
            _screenUi.DrawString(panel.X + MenuPadding + 10f, headerY, "ToolBench", MenuMuted, 1.6f, FontWeight.SemiBold);
        }

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var rect = rects[i];
            if (item.SeparatorBefore)
            {
                _screenUi.DrawRect(rect.X + 6f, rect.Y - MenuSeparatorHeight * 0.5f, rect.Width - 12f, 1f, Separator);
            }

            var current = item.Command == ToolbarCommand.ChooseSection && item.Section == _bench.Current
                || item.Command == ToolbarCommand.ApplyPreset && item.Label == view.MatchingPreset
                || item.Command == ToolbarCommand.SetGuiPreview && item.Preview == view.GuiPreview;
            var enabled = item.Command == ToolbarCommand.DeleteProject
                ? view.HasProject
                : !view.IsPlaying || item.Command is ToolbarCommand.ChooseSection or ToolbarCommand.LeaveToHub or ToolbarCommand.OpenPhysics
                    or ToolbarCommand.OpenAnimation;
            var hovered = rect.Contains(mouseX, mouseY);
            if (current)
            {
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, hovered ? MenuHover : MenuCurrent);
                _screenUi.DrawRect(rect.X, rect.Y + 5f, 3f, rect.Height - 10f, ActiveBorder);
                UiIcons.FillCircle(_screenUi, rect.X + rect.Width - 14f, rect.Y + rect.Height * 0.5f, 3.5f, ActiveBorder);
            }
            else if (hovered && enabled)
            {
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, item.Destructive ? DestructiveHover : MenuHover);
            }

            // An open Physics or Animation panel shows as the dot only: it is not the current section.
            if (item.Command == ToolbarCommand.OpenPhysics && view.PhysicsOn || item.Command == ToolbarCommand.OpenAnimation && view.AnimationOn)
            {
                UiIcons.FillCircle(_screenUi, rect.X + rect.Width - 14f, rect.Y + rect.Height * 0.5f, 3.5f, ActiveBorder);
            }

            var height = _screenUi.MeasureLineHeight(1.9f);
            _screenUi.DrawString(
                rect.X + 12f,
                rect.Y + (rect.Height - height) * 0.5f,
                item.Label,
                !enabled ? DisabledText
                    : item.Destructive ? (hovered ? ActiveText : DestructiveText)
                    : current ? ActiveText : InactiveText,
                1.9f,
                current ? FontWeight.SemiBold : FontWeight.Regular);
        }

        _screenUi.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _screenUi.Dispose();

    private bool IsActive(ToolbarControl control, ToolbarView view) => control switch
    {
        ToolbarControl.Move => view.ActiveTool == EditorToolMode.Move,
        ToolbarControl.Resize => view.ActiveTool == EditorToolMode.Resize,
        ToolbarControl.Rotate => view.ActiveTool == EditorToolMode.Rotate,
        ToolbarControl.Anchor => view.Anchor == ToggleState.On,
        ToolbarControl.Lock => view.Lock == ToggleState.On,
        ToolbarControl.Color => view.ColorOn,
        ToolbarControl.AddMeshes => OpenMenu == ToolbarMenu.AddMeshes,
        ToolbarControl.World => view.WorldOn,
        ToolbarControl.Output => view.OutputOn,
        ToolbarControl.Environment => view.EnvironmentOn,
        ToolbarControl.Presets => OpenMenu == ToolbarMenu.Presets,
        ToolbarControl.GuiPreview => OpenMenu == ToolbarMenu.GuiPreview,
        ToolbarControl.Material => view.MaterialOn,
        ToolbarControl.AddLight => OpenMenu == ToolbarMenu.AddLight,
        _ => false
    };

    private static bool HasMenu(ToolbarControl control) =>
        control is ToolbarControl.AddMeshes or ToolbarControl.Presets or ToolbarControl.GuiPreview or ToolbarControl.AddLight;

    private static string LabelOf(ToolbarControl control, bool shortLabel) => control switch
    {
        ToolbarControl.Move => "Move",
        ToolbarControl.Resize => "Resize",
        ToolbarControl.Rotate => "Rotate",
        ToolbarControl.Anchor => "Anchor",
        ToolbarControl.Lock => "Lock",
        ToolbarControl.Color => "Color",
        ToolbarControl.AddMeshes => shortLabel ? "Add" : "Add Meshes",
        ToolbarControl.World => "World",
        ToolbarControl.Output => "Output",
        ToolbarControl.Environment => "Environment",
        ToolbarControl.Presets => "Presets",
        ToolbarControl.GuiFrame => "Frame",
        ToolbarControl.GuiText => "Text",
        ToolbarControl.GuiButton => "Button",
        ToolbarControl.GuiPreview => "Preview",
        ToolbarControl.Material => "Material",
        ToolbarControl.AddLight => shortLabel ? "Light" : "Add Light",
        _ => control.ToString()
    };

    private static float BaseWidth(ToolbarControl control) => control switch
    {
        ToolbarControl.AddMeshes => 136f,
        ToolbarControl.AddLight => 126f,
        ToolbarControl.Material => 104f,
        ToolbarControl.Environment => 128f,
        ToolbarControl.Presets => 108f,
        ToolbarControl.GuiPreview => 108f,
        _ => 96f
    };

    // The part of a button its label is centred in (menu buttons keep room for their caret).
    private static UiBox LabelBox(UiBox rect, ToolbarControl control) =>
        HasMenu(control) ? rect with { Width = MathF.Max(1f, rect.Width - CaretRoom) } : rect;

    // ToolBench: the grid icon, the current section's name (when there is room) and a menu caret.
    private void DrawToolBenchButton(Layout layout)
    {
        var rect = layout.ToolBench;
        var open = OpenMenu == ToolbarMenu.ToolBench;
        DrawMenuButtonFill(rect, open);
        var text = open ? ActiveText : InactiveText;
        var centerY = rect.Y + rect.Height * 0.5f;
        UiIcons.DrawGrid(_screenUi, rect.X + 16f, centerY, 12f, text);
        UiIcons.DrawCaretDown(_screenUi, rect.X + rect.Width - 12f, centerY, CaretSize, text);
        if (layout.Compact)
        {
            return;
        }

        var label = ToolBench.LabelOf(_bench.Current);
        var left = rect.X + 30f;
        var room = rect.Width - 30f - 22f;
        var scale = FitLabelScale(label, room + LabelPadding, 2.0f);
        _screenUi.DrawString(left, rect.Y + (rect.Height - _screenUi.MeasureLineHeight(scale)) * 0.5f, label, text, scale);
    }

    // The project button keeps the old Leave arrow, with a caret: it now opens the project menu.
    // Anchor's / Lock's box for the selection's state: nothing selected = the disabled look, off = a plain button, on =
    // a lighter box, mixed = the plain box with a light outline.
    private (Vector3 Text, float Scale) DrawToggleBox(UiBox rect, ToggleState state)
    {
        switch (state)
        {
            case ToggleState.None:
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, DisabledFill);
                return (DisabledText, 2.0f);
            case ToggleState.On:
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, AnchoredFill);
                return (AnchoredText, 2.0f);
            case ToggleState.Mixed:
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Inactive);
                _screenUi.DrawBorderRect(rect.X, rect.Y, rect.Width, rect.Height, MixedOutline, 1f);
                return (InactiveText, 2.0f);
            default:
                _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Inactive);
                return (InactiveText, 2.0f);
        }
    }

    private void DrawProjectButton(UiBox rect)
    {
        var open = OpenMenu == ToolbarMenu.Project;
        DrawMenuButtonFill(rect, open);
        var text = open ? ActiveText : InactiveText;
        var centerY = rect.Y + rect.Height * 0.5f;
        UiIcons.DrawBackArrow(_screenUi, rect.X + 17f, centerY, 14f, text);
        UiIcons.DrawCaretDown(_screenUi, rect.X + rect.Width - 11f, centerY, 7f, text);
    }

    private void DrawMenuButtonFill(UiBox rect, bool open)
    {
        if (open)
        {
            _screenUi.DrawRect(rect.X - 1f, rect.Y - 1f, rect.Width + 2f, rect.Height + 2f, ActiveBorder);
            _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, ActiveFill);
        }
        else
        {
            _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Inactive);
        }
    }

    // Play: a green triangle and "Play". Playing: a filled green button with a stop square and "Stop".
    private void DrawPlayButton(UiBox rect, bool isPlaying)
    {
        if (isPlaying)
        {
            _screenUi.DrawRect(rect.X - 1f, rect.Y - 1f, rect.Width + 2f, rect.Height + 2f, PlayingBorder);
            _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, PlayingFill);
        }
        else
        {
            _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, Inactive);
        }

        const float iconSize = 11f;
        const float iconGap = 8f;
        var label = isPlaying ? "Stop" : "Play";
        var scale = FitLabelScale(label, rect.Width - iconSize - iconGap, isPlaying ? 2.1f : 2.0f);
        var textWidth = _screenUi.MeasureWidth(label, scale);
        var textHeight = _screenUi.MeasureLineHeight(scale);
        var contentX = rect.X + (rect.Width - (iconSize + iconGap + textWidth)) * 0.5f;
        var centerY = rect.Y + rect.Height * 0.5f;
        if (isPlaying)
        {
            UiIcons.DrawStop(_screenUi, contentX + iconSize * 0.5f, centerY, iconSize - 1f, ActiveText);
        }
        else
        {
            UiIcons.DrawPlay(_screenUi, contentX + iconSize * 0.5f, centerY, iconSize, PlayAccent);
        }

        _screenUi.DrawString(contentX + iconSize + iconGap, rect.Y + (rect.Height - textHeight) * 0.5f, label, isPlaying ? ActiveText : InactiveText, scale);
    }

    // The label scale, reduced when the label would not fit the button (narrow windows shrink the buttons).
    private float FitLabelScale(string label, float width, float scale)
    {
        var available = width - LabelPadding;
        var textWidth = _screenUi.MeasureWidth(label, scale);
        return textWidth <= available || textWidth <= 0f ? scale : scale * MathF.Max(available, 1f) / textWidth;
    }

    private void DrawCenteredLabel(UiBox rect, string label, Vector3 color, float scale)
    {
        // Centering uses the same measured metrics the glyph path renders with, so labels stay
        // centered under proportional Inter just as they did under the old fixed-width font.
        scale = FitLabelScale(label, rect.Width, scale);
        var textWidth = _screenUi.MeasureWidth(label, scale);
        var textHeight = _screenUi.MeasureLineHeight(scale);
        var x = rect.X + (rect.Width - textWidth) * 0.5f;
        var y = rect.Y + (rect.Height - textHeight) * 0.5f;
        _screenUi.DrawString(x, y, label, color, scale);
    }

    private static ToolbarMenuItem[] ItemsOf(ToolbarMenu menu) => menu switch
    {
        ToolbarMenu.ToolBench => ToolBenchItems,
        ToolbarMenu.AddMeshes => AddMeshesItems,
        ToolbarMenu.Project => ProjectItems,
        ToolbarMenu.Presets => PresetItems,
        ToolbarMenu.GuiPreview => GuiPreviewItems,
        ToolbarMenu.AddLight => AddLightItems,
        _ => []
    };

    // A menu opens below its button, shifted left when needed so it stays inside the window.
    private UiBox MenuRect(int screenWidth, ToolbarMenu menu)
    {
        var layout = GetLayout(screenWidth);
        var anchor = layout.ToolBench;
        if (menu == ToolbarMenu.Project)
        {
            anchor = layout.Project;
        }
        else if (menu is ToolbarMenu.AddMeshes or ToolbarMenu.Presets or ToolbarMenu.GuiPreview or ToolbarMenu.AddLight)
        {
            var control = menu switch
            {
                ToolbarMenu.AddMeshes => ToolbarControl.AddMeshes,
                ToolbarMenu.Presets => ToolbarControl.Presets,
                ToolbarMenu.AddLight => ToolbarControl.AddLight,
                _ => ToolbarControl.GuiPreview
            };
            var index = Array.IndexOf(layout.ControlIds, control);
            anchor = index >= 0 ? layout.Controls[index] : anchor;
        }

        var items = ItemsOf(menu);
        var height = MenuPadding * 2f + (menu == ToolbarMenu.ToolBench ? MenuHeaderHeight : 0f) + items.Length * MenuItemHeight;
        foreach (var item in items)
        {
            height += item.SeparatorBefore ? MenuSeparatorHeight : 0f;
        }

        var x = MathF.Max(EdgeMargin, MathF.Min(anchor.X, screenWidth - EdgeMargin - MenuWidth));
        return new UiBox(x, ToolbarHeight + 2f, MenuWidth, height);
    }

    private static UiBox[] ItemRects(UiBox panel, ToolbarMenu menu, ToolbarMenuItem[] items)
    {
        var rects = new UiBox[items.Length];
        var y = panel.Y + MenuPadding + (menu == ToolbarMenu.ToolBench ? MenuHeaderHeight : 0f);
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].SeparatorBefore)
            {
                y += MenuSeparatorHeight;
            }

            rects[i] = new UiBox(panel.X + MenuPadding, y, panel.Width - MenuPadding * 2f, MenuItemHeight);
            y += MenuItemHeight;
        }

        return rects;
    }

    // ToolBench and the project button pinned left, Play pinned right; the section row centred in the window when it
    // fits there, else centred in the space between the pinned buttons, else shrunk proportionally to fit that space.
    private Layout GetLayout(int screenWidth)
    {
        var compact = screenWidth < CompactBelowWidth;
        var y = (ToolbarHeight - ButtonHeight) * 0.5f;
        var bench = new UiBox(EdgeMargin, y, compact ? CompactToolBenchWidth : ToolBenchButtonWidth(), ButtonHeight);
        var project = new UiBox(bench.X + bench.Width + ClusterGap, y, ProjectButtonWidth, ButtonHeight);
        var playWidth = compact ? CompactPlayButtonWidth : PlayButtonWidth;
        var play = new UiBox(screenWidth - EdgeMargin - playWidth, y, playWidth, ButtonHeight);

        var ids = _bench.CurrentControls.ToArray();
        var total = ButtonGap * global::System.Math.Max(0, ids.Length - 1);
        foreach (var id in ids)
        {
            total += BaseWidth(id);
        }

        var left = project.X + project.Width + PinnedGap;
        var right = play.X - PinnedGap;
        var scale = 1f;
        var startX = (screenWidth - total) * 0.5f;
        if (startX < left || startX + total > right)
        {
            var available = MathF.Max(right - left, 1f);
            scale = MathF.Min(1f, available / MathF.Max(total, 1f));
            startX = left + (available - total * scale) * 0.5f;
        }

        var rects = new UiBox[ids.Length];
        var x = startX;
        for (var i = 0; i < ids.Length; i++)
        {
            var width = BaseWidth(ids[i]) * scale;
            rects[i] = new UiBox(x, y, width, ButtonHeight);
            x += width + ButtonGap * scale;
        }

        return new Layout(bench, project, play, ids, rects, compact, scale);
    }

    // Wide enough for the current section's name at full size (icon and caret beside it): the usual width, wider only for
    // a longer name (Environment & Graphics), so the other sections keep their layout.
    private float ToolBenchButtonWidth()
    {
        var label = _screenUi is null ? 0f : _screenUi.MeasureWidth(ToolBench.LabelOf(_bench.Current), 2.0f);
        return MathF.Max(ToolBenchWidth, MathF.Ceiling(label) + 56f);
    }

    private readonly record struct Layout(UiBox ToolBench, UiBox Project, UiBox Play, ToolbarControl[] ControlIds, UiBox[] Controls, bool Compact, float Scale);
}
