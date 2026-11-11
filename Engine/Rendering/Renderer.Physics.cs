using System.Globalization;
using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.Physics;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The Physics panel, docked on the left as World is on the right. Whether it is open is editor UI state for this
/// editor session only: never scene, project or Atmosphere data and never an undo step. The ToolBench menu's Physics
/// entry opens it (without changing the section) and only its own × closes it; ToolBench sections, tools, tabs,
/// selection, World, Output and Play leave it as it is, and entering a project starts with it closed. The tab strip, script editor, Output and Environment & Graphics panel
/// sit between it and World; the 3D View keeps rendering full-window behind both columns.
/// <para>
/// Anchor: the panel's Anchored checkbox and the Main Tools Anchor button are two views of the same Part.Anchored,
/// read from the selection every frame (nothing is cached) and changed only through <see cref="ToggleAnchor"/>.
/// </para>
/// <para>
/// Physics settings (Gravity, Can Collide, Is Trigger, Friction, Mass) are read from the selected Parts the same way and
/// changed for the whole selection at once, one undo step per action (a whole Friction drag, a whole typed value). Edit
/// Mode only: the next Play builds the physics world from them. While a value is being typed the panel has the keyboard.
/// </para>
/// </summary>
public sealed partial class Renderer
{
    private const string PhysicsPlayMessage = "Stop Play Mode to change Physics settings.";
    private const string MassRangeMessage = "Mass must be a number from 0.001 to 1000000.";
    private const string FrictionRangeMessage = "Friction must be a number from 0 to 1.";

    private readonly PhysicsPanel _physicsPanel = new();
    private bool _physicsPanelOn;

    // A value being typed (Mass or Friction), for the Parts selected when typing started.
    private PhysicsValueEdit? _physicsEdit;

    // A Friction slider drag: the Parts it changes and their settings when it started (recorded on release).
    private Part[]? _frictionDragParts;
    private PartSnapshot[]? _frictionDragBefore;

    // The panel had the keyboard this frame (it may have just finished typing with Enter).
    private bool _physicsKeyboardThisFrame;

    /// <summary>
    /// A Physics, UI panel or World panel rotation value is being typed (or was finished this frame): keys are not editor
    /// shortcuts (no camera movement, Delete, undo or R).
    /// </summary>
    private bool PhysicsPanelOwnsKeyboard => _physicsEdit is not null || _physicsKeyboardThisFrame || GuiPanelOwnsKeyboard || RotationFieldOwnsKeyboard || RuntimeTextBoxOwnsKeyboard;

    private bool PhysicsPanelVisible => _physicsPanelOn && _workspace is not null;

    // From under the toolbar to the bottom of the window; never wider than the room World or Color leave.
    private UiBox PhysicsPanelRect =>
        new(0f, ToolbarRenderer.ToolbarHeight, MathF.Min(PhysicsPanel.Width, MathF.Max(0f, _width - RightPanelWidth)),
            MathF.Max(0f, _height - ToolbarRenderer.ToolbarHeight));

    // Physics and Animation share the left side (Animation beside Physics when both are open).
    private float LeftPanelWidth => (PhysicsPanelVisible ? PhysicsPanelRect.Width : 0f) + (AnimationPanelVisible ? AnimationPanelRect.Width : 0f);

    private bool IsOverPhysicsPanel(float x, float y) => PhysicsPanelVisible && PhysicsPanelRect.Contains(x, y);

    /// <summary>ToolBench's Physics entry: shows the panel; when it is already open it stays open.</summary>
    private void OpenPhysicsPanel()
    {
        if (_physicsPanelOn)
        {
            Console.WriteLine("[Physics] Panel already open");
            return;
        }

        _physicsPanelOn = true;
        Console.WriteLine("[Physics] Panel open");
    }

    private void ClosePhysicsPanel()
    {
        _physicsPanelOn = false;
        Console.WriteLine("[Physics] Panel closed");
    }

    // Everything over the panel is the panel's: the × closes it, rows and values change the selection's settings, any
    // other click or wheel stops here. A Friction drag and typing continue whatever the mouse does.
    private void HandlePhysicsPanel(InputManager input)
    {
        if (!PhysicsPanelVisible)
        {
            CancelPhysicsEdits();
            return;
        }

        var panel = PhysicsPanelRect;
        if (_frictionDragParts is { } dragged)
        {
            _toolbarConsumedClick = true;
            if (input.IsLeftMouseHeld)
            {
                SetFriction(dragged, PhysicsPanel.FrictionAt(panel, input.MouseX));
            }
            else
            {
                CommitFrictionDrag();
            }

            return;
        }

        if (_physicsEdit is { } edit)
        {
            _physicsKeyboardThisFrame = true;
            TypePhysicsValue(edit, input);

            // A click anywhere but the value finishes the typing first (Auto instead drops a typed mass), then does
            // what it does.
            if (_physicsEdit is not null && input.LeftMousePressed && !FieldOf(panel, edit.Field).Contains(input.MouseX, input.MouseY))
            {
                if (edit.Field == PhysicsPanelField.Mass && PhysicsPanel.MassAutoButton(panel).Contains(input.MouseX, input.MouseY))
                {
                    _physicsEdit = null;
                }
                else
                {
                    CommitPhysicsEdit(keepOpenWhenInvalid: false);
                }
            }
        }

        if (!panel.Contains(input.MouseX, input.MouseY))
        {
            return;
        }

        if (input.ScrollDelta != 0f)
        {
            _wheelConsumed = true;
        }

        if (_toolbarConsumedClick || (!input.LeftMousePressed && !input.RightMousePressed))
        {
            return;
        }

        _toolbarConsumedClick = true;
        if (!input.LeftMousePressed)
        {
            return;
        }

        var (x, y) = (input.MouseX, input.MouseY);
        if (PhysicsPanel.CloseButton(panel).Contains(x, y))
        {
            ClosePhysicsPanel();
        }
        else if (SelectionAnchorState() == ToggleState.None)
        {
        }
        else if (PhysicsPanel.AnchoredRow(panel).Contains(x, y))
        {
            ToggleAnchor();
        }
        else if (PhysicsPanel.GravityRow(panel).Contains(x, y))
        {
            ToggleSetting("Gravity", part => part.GravityEnabled, (part, on) => part.GravityEnabled = on);
        }
        else if (PhysicsPanel.MassAutoButton(panel).Contains(x, y))
        {
            ChangePhysics("Mass", SelectedPartsArray(), part => part.MassOverride = null, "Mass Auto");
        }
        else if (PhysicsPanel.MassField(panel).Contains(x, y))
        {
            BeginPhysicsEdit(PhysicsPanelField.Mass);
        }
        else if (PhysicsPanel.CanCollideRow(panel).Contains(x, y))
        {
            ToggleSetting("Can Collide", part => part.CanCollide, (part, on) => part.CanCollide = on);
        }
        else if (PhysicsPanel.IsTriggerRow(panel).Contains(x, y))
        {
            ToggleSetting("Is Trigger", part => part.IsTrigger, (part, on) => part.IsTrigger = on);
        }
        else if (PhysicsPanel.FrictionField(panel).Contains(x, y))
        {
            BeginPhysicsEdit(PhysicsPanelField.Friction);
        }
        else if (PhysicsPanel.FrictionSlider(panel).Contains(x, y))
        {
            BeginFrictionDrag(PhysicsPanel.FrictionAt(panel, x));
        }
    }

    /// <summary>The selection's Part.Anchored, read from the Parts themselves (None when nothing is selected).</summary>
    private ToggleState SelectionAnchorState()
    {
        var selected = _selection?.SelectedParts ?? [];
        if (selected.Count == 0)
        {
            return ToggleState.None;
        }

        var anchored = selected.Count(part => part.Anchored);
        return anchored == 0 ? ToggleState.Off : anchored == selected.Count ? ToggleState.On : ToggleState.Mixed;
    }

    /// <summary>
    /// Anchor / Anchored: when every selected Part is anchored, un-anchors them all; otherwise (none or a mix) anchors
    /// them all. One undo step that remembers each Part's own previous value. Edit Mode only; the next Play builds the
    /// physics bodies from the result.
    /// </summary>
    private void ToggleAnchor()
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show("Stop Play Mode to change Anchor.");
            return;
        }

        var selected = (_selection?.SelectedParts ?? []).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var anchor = !selected.All(part => part.Anchored);
        if (selected.All(part => part.Anchored == anchor))
        {
            return;
        }

        var before = selected.Select(PartSnapshot.FromPart).ToArray();
        foreach (var part in selected)
        {
            part.Anchored = anchor;
        }

        _editorHistory.Push(EditorCommand.AnchorChange(selected, before, selected.Select(PartSnapshot.FromPart).ToArray()));
        Console.WriteLine($"[Anchor] {(anchor ? "Anchored" : "Unanchored")} {(selected.Length == 1 ? selected[0].Name : $"{selected.Length} parts")}");
    }

    // Gravity / Can Collide / Is Trigger: when every selected Part has it on, turns it off for all; otherwise (off or a mix) on for all.
    private void ToggleSetting(string label, Func<Part, bool> isOn, Action<Part, bool> set)
    {
        var selected = SelectedPartsArray();
        var on = !selected.All(isOn);
        ChangePhysics(label, selected, part => set(part, on), $"{label} {(on ? "on" : "off")}");
    }

    // One Physics setting change for these Parts: applied, then one undo step (none when nothing changed). Edit Mode only.
    private bool ChangePhysics(string label, Part[] parts, Action<Part> apply, string description)
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show(PhysicsPlayMessage);
            return false;
        }

        if (parts.Length == 0)
        {
            return false;
        }

        var before = parts.Select(PartSnapshot.FromPart).ToArray();
        foreach (var part in parts)
        {
            apply(part);
        }

        return RecordPhysicsChange(label, parts, before, description);
    }

    private bool RecordPhysicsChange(string label, Part[] parts, PartSnapshot[] before, string description)
    {
        var after = parts.Select(PartSnapshot.FromPart).ToArray();
        if (before.Zip(after).All(pair => pair.First.SamePhysics(pair.Second)))
        {
            return false;
        }

        _editorHistory.Push(EditorCommand.PhysicsChange(label, parts, before, after));
        Console.WriteLine($"[Physics] {description}: {(parts.Length == 1 ? parts[0].Name : $"{parts.Length} parts")}");
        return true;
    }

    private Part[] SelectedPartsArray() => (_selection?.SelectedParts ?? []).ToArray();

    private void BeginFrictionDrag(float friction)
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show(PhysicsPlayMessage);
            return;
        }

        var parts = SelectedPartsArray();
        if (parts.Length == 0)
        {
            return;
        }

        _frictionDragParts = parts;
        _frictionDragBefore = parts.Select(PartSnapshot.FromPart).ToArray();
        SetFriction(parts, friction);
    }

    private static void SetFriction(Part[] parts, float friction)
    {
        foreach (var part in parts)
        {
            part.Friction = friction;
        }
    }

    private void CommitFrictionDrag()
    {
        if (_frictionDragParts is not { } parts || _frictionDragBefore is not { } before)
        {
            return;
        }

        _frictionDragParts = null;
        _frictionDragBefore = null;
        RecordPhysicsChange("Friction", parts, before, $"Friction {PhysicsPanel.FormatFriction(parts[0].Friction)}");
    }

    // Starts typing a value, beginning with what the field shows, all selected (the first key replaces it). A click
    // on the value being typed only puts the caret at the end.
    private void BeginPhysicsEdit(PhysicsPanelField field)
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show(PhysicsPlayMessage);
            return;
        }

        if (_physicsEdit is { } current && current.Field == field)
        {
            current.SelectAll = false;
            return;
        }

        var parts = SelectedPartsArray();
        if (parts.Length == 0)
        {
            return;
        }

        var view = BuildPhysicsPanelView();
        var text = field == PhysicsPanelField.Friction
            ? view.Friction is { } friction ? PhysicsPanel.FormatFriction(friction) : string.Empty
            : view.MassMode != PhysicsMassMode.Mixed && view.MassValue is { } mass ? PhysicsPanel.FormatMass(mass) : string.Empty;
        _physicsEdit = new PhysicsValueEdit(field, parts, text);
    }

    // Number keys, Backspace and Enter (finishes; an unusable value keeps the field open with a message).
    private void TypePhysicsValue(PhysicsValueEdit edit, InputManager input)
    {
        if (input.TextBackspacePressed)
        {
            edit.Text = edit.SelectAll ? string.Empty : edit.Text.Length > 0 ? edit.Text[..^1] : edit.Text;
            edit.SelectAll = false;
        }

        foreach (var ch in input.TextInputCharacters)
        {
            if (!char.IsAsciiDigit(ch) && ch is not ('.' or ',' or '-' or '+' or 'e' or 'E'))
            {
                continue;
            }

            if (edit.SelectAll)
            {
                edit.Text = string.Empty;
                edit.SelectAll = false;
            }

            if (edit.Text.Length < 16)
            {
                edit.Text += ch;
            }
        }

        if (input.EnterPressed)
        {
            CommitPhysicsEdit(keepOpenWhenInvalid: true);
        }
    }

    // Applies the typed value to the Parts it was started for: one undo step. Nothing typed (the text as it started, or
    // empty) changes nothing. A mass must be a number from MinMass to MaxMass (anything else is refused, never turned
    // into another value); a friction outside 0-1 is clamped, and says so.
    private void CommitPhysicsEdit(bool keepOpenWhenInvalid)
    {
        if (_physicsEdit is not { } edit)
        {
            return;
        }

        var text = edit.Text.Trim();
        if (text.Length == 0 || text == edit.InitialText)
        {
            _physicsEdit = null;
            return;
        }

        var parsed = float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value);
        if (edit.Field == PhysicsPanelField.Mass)
        {
            if (!parsed || value < Part.MinMass || value > Part.MaxMass)
            {
                _toast.Show(MassRangeMessage);
                if (!keepOpenWhenInvalid)
                {
                    _physicsEdit = null;
                }

                return;
            }

            _physicsEdit = null;
            ChangePhysics("Mass", edit.Parts, part => part.MassOverride = value, $"Mass {PhysicsPanel.FormatMass(value)}");
            return;
        }

        if (!parsed)
        {
            _toast.Show(FrictionRangeMessage);
            if (!keepOpenWhenInvalid)
            {
                _physicsEdit = null;
            }

            return;
        }

        var friction = global::System.Math.Clamp(value, 0f, 1f);
        if (friction != value)
        {
            _toast.Show($"Friction is from 0 to 1: set to {PhysicsPanel.FormatFriction(friction)}.");
        }

        _physicsEdit = null;
        ChangePhysics("Friction", edit.Parts, part => part.Friction = friction, $"Friction {PhysicsPanel.FormatFriction(friction)}");
    }

    /// <summary>Starting Play: a Friction drag or a typed value in progress is finished as if released / confirmed.</summary>
    private void CommitPhysicsEdits()
    {
        CommitFrictionDrag();
        CommitPhysicsEdit(keepOpenWhenInvalid: false);
    }

    /// <summary>Escape (and leaving the project): drops typing, and puts a Friction drag back where it started. True if there was one.</summary>
    private bool CancelPhysicsEdits()
    {
        var cancelled = _physicsEdit is not null;
        _physicsEdit = null;
        if (_frictionDragParts is { } parts && _frictionDragBefore is { } before)
        {
            for (var i = 0; i < parts.Length; i++)
            {
                before[i].ApplyPhysics(parts[i]);
            }

            cancelled = true;
        }

        _frictionDragParts = null;
        _frictionDragBefore = null;
        return cancelled;
    }

    private static UiBox FieldOf(UiBox panel, PhysicsPanelField field) =>
        field == PhysicsPanelField.Mass ? PhysicsPanel.MassField(panel) : PhysicsPanel.FrictionField(panel);

    private void RenderPhysicsPanel(float dpiScale)
    {
        if (!PhysicsPanelVisible)
        {
            return;
        }

        _physicsPanel.Render(PhysicsPanelRect, BuildPhysicsPanelView(), _editorClock % 1d < 0.6d, _lastMouseX, _lastMouseY, _width, _height, dpiScale);
    }

    // Everything the panel shows, read from the selected Parts now. Auto's mass comes from the same calculation (and
    // settings) the next Play's physics world uses.
    private PhysicsPanelView BuildPhysicsPanelView()
    {
        var selected = _selection?.SelectedParts ?? [];
        float? friction = selected.Count > 0 && selected.All(part => part.Friction.Equals(selected[0].Friction)) ? selected[0].Friction : null;
        var (massMode, massValue) = selected.Count == 0 || selected.All(part => part.MassOverride is null)
            ? (PhysicsMassMode.Auto, selected.Count == 1 ? PhysicsWorld.AutoMassOf(selected[0], _playMode.PhysicsSettings) : null)
            : selected[0].MassOverride is { } mass && selected.All(part => part.MassOverride == mass)
                ? (PhysicsMassMode.Manual, mass)
                : (PhysicsMassMode.Mixed, (float?)null);
        return new PhysicsPanelView(
            PhysicsSelectionLabel(),
            SelectionAnchorState(),
            StateOf(selected, part => part.GravityEnabled),
            StateOf(selected, part => part.CanCollide),
            StateOf(selected, part => part.IsTrigger),
            friction,
            massMode,
            massValue,
            !_playMode.IsPlaying,
            _physicsEdit?.Field ?? PhysicsPanelField.None,
            _physicsEdit?.Text ?? string.Empty,
            _physicsEdit?.SelectAll == true);
    }

    private static ToggleState StateOf(IReadOnlyList<Part> parts, Func<Part, bool> isOn)
    {
        if (parts.Count == 0)
        {
            return ToggleState.None;
        }

        var on = parts.Count(isOn);
        return on == 0 ? ToggleState.Off : on == parts.Count ? ToggleState.On : ToggleState.Mixed;
    }

    private sealed class PhysicsValueEdit(PhysicsPanelField field, Part[] parts, string initialText)
    {
        public PhysicsPanelField Field { get; } = field;

        public Part[] Parts { get; } = parts;

        public string InitialText { get; } = initialText;

        public string Text { get; set; } = initialText;

        // The whole text is selected: the first key replaces it (so clicking a value never changes it by itself).
        public bool SelectAll { get; set; } = initialText.Length > 0;
    }

    private string PhysicsSelectionLabel()
    {
        var selected = _selection?.SelectedParts ?? [];
        return selected.Count switch
        {
            0 => "No object selected",
            1 => $"Selected: {selected[0].Name}",
            _ => $"{selected.Count} objects selected"
        };
    }
}
