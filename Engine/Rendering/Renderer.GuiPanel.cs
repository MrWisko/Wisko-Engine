using System.Globalization;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The UI section's right-hand panel (in World's place while the section is UI): the Hierarchy and the selected element's
/// Properties. It uses the canvas's own <see cref="GuiSelection"/> (one selection, two views) and edits the authored
/// <see cref="GuiDocument"/> directly, so the canvas and the panel always show the same values. Rows: click selects,
/// double-click renames, drag onto a Frame / the Canvas reparents (onto a Text or Button: next to it, in its parent).
/// Fields work like the Physics panel's (all text selected on click, Enter / click-away commit, Escape cancels; the panel
/// has the keyboard meanwhile). Every change is one undo step; nothing changes in Play.
/// </summary>
public sealed partial class Renderer
{
    private const double DoubleClickTime = 0.5;
    private const float TreeDragThreshold = 4f;

    private readonly GuiPanel _guiPanel = new();
    private float _guiTreeScroll;
    private float _guiPropertiesScroll;
    private GuiFieldEdit? _guiFieldEdit;
    private bool _guiKeyboardThisFrame;
    private GuiSliderDrag? _guiSlider;
    private GuiTreeDrag? _guiTreeDrag;
    private int? _guiLastRowId;
    private double _guiLastRowTime = -10d;

    private bool GuiPanelVisible => _toolBench.Current == ToolBenchSection.UI && _workspace is not null;

    private UiBox GuiPanelRect => new(MathF.Max(0f, _width - GuiPanel.Width), ToolbarRenderer.ToolbarHeight, MathF.Min(GuiPanel.Width, _width),
        MathF.Max(0f, _height - ToolbarRenderer.ToolbarHeight));

    private bool IsOverGuiPanel(float x, float y) => GuiPanelVisible && GuiPanelRect.Contains(x, y);

    /// <summary>A UI panel value is being typed (or was finished this frame): keys are not editor shortcuts.</summary>
    private bool GuiPanelOwnsKeyboard => _guiFieldEdit is not null || _guiKeyboardThisFrame;

    private void BuildGuiPanel() => _guiPanel.Build(GuiPanelRect, Gui, SelectedGuiElement, ref _guiTreeScroll, ref _guiPropertiesScroll);

    private void HandleGuiPanel(InputManager input)
    {
        if (!GuiPanelVisible)
        {
            CommitGuiPanelEdits();
            return;
        }

        BuildGuiPanel();
        var x = input.MouseX;
        var y = input.MouseY;
        if (input.LeftMousePressed && RowAt(x, y)?.Element is null)
        {
            // A press anywhere but an element row: the next row press is a first click again.
            _guiLastRowId = null;
        }

        if (_guiSlider is { } slider)
        {
            // Every frame of the drag, the release frame included (the value is where the button went up).
            _toolbarConsumedClick = true;
            SetGuiOpacity(slider, x);
            if (!input.IsLeftMouseHeld)
            {
                CommitGuiSlider();
            }

            return;
        }

        if (_guiTreeDrag is { } drag)
        {
            _toolbarConsumedClick = true;
            if (input.IsLeftMouseHeld)
            {
                drag.Dragging |= MathF.Abs(x - drag.StartX) > TreeDragThreshold || MathF.Abs(y - drag.StartY) > TreeDragThreshold;
            }
            else
            {
                _guiTreeDrag = null;
                if (drag.Dragging)
                {
                    // A drag is not the first click of a double-click.
                    _guiLastRowId = null;
                }

                if (drag.Dragging && RowAt(x, y) is { } target)
                {
                    DropGuiElement(drag.Element, target);
                }
            }

            return;
        }

        if (_guiFieldEdit is { } edit)
        {
            _guiKeyboardThisFrame = true;
            TypeGuiField(edit, input);
            if (_guiFieldEdit is not null && input.LeftMousePressed && !(EditBox(edit)?.Contains(x, y) ?? false))
            {
                CommitGuiField(keepOpenWhenInvalid: false);
                BuildGuiPanel();
            }
        }

        var panel = GuiPanelRect;
        if (!panel.Contains(x, y))
        {
            return;
        }

        if (input.ScrollDelta != 0f)
        {
            _wheelConsumed = true;
            if (_guiPanel.Tree.Contains(x, y))
            {
                _guiTreeScroll -= input.ScrollDelta * 40f;
            }
            else
            {
                _guiPropertiesScroll -= input.ScrollDelta * 40f;
            }
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

        if (_guiPanel.Tree.Contains(x, y))
        {
            if (RowAt(x, y) is { } row)
            {
                PressGuiRow(row, x, y);
            }

            return;
        }

        if (!_guiPanel.Properties.Contains(x, y) || ControlAt(x, y) is not { } control)
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(GuiPlayMessage);
            return;
        }

        switch (control.Kind)
        {
            case GuiPanelControlKind.Field:
                BeginGuiField(control.Prop, inTree: false);
                break;
            case GuiPanelControlKind.Checkbox when control.Prop == GuiProp.Visible:
                ChangeGuiElement("Visible", element => element.Visible = !element.Visible);
                break;
            case GuiPanelControlKind.Checkbox when control.Prop == GuiProp.Enabled:
                ChangeGuiElement("Enabled", element => ((GuiButton)element).Enabled = !((GuiButton)element).Enabled);
                break;
            case GuiPanelControlKind.Toggle:
                ChangeGuiElement("Alignment", element => SetAlignment((GuiText)element, control.Prop));
                break;
            case GuiPanelControlKind.Slider when SelectedGuiElement is GuiFrame frame:
                _guiSlider = new GuiSliderDrag(frame, frame.Opacity, Gui.Clone(), Gui.Version, control.Box);
                SetGuiOpacity(_guiSlider, x);
                break;
        }
    }

    // A row press: the Canvas row clears the element selection; an element row selects it (a second press soon after on
    // the same row renames it) and may become a drag (to reparent; not in Play).
    private void PressGuiRow(GuiTreeRow row, float x, float y)
    {
        if (row.Element is not { } element)
        {
            _guiSelection.SelectedId = null;
            _guiLastRowId = null;
            return;
        }

        var again = _guiLastRowId == element.Id && _editorClock - _guiLastRowTime <= DoubleClickTime;
        _guiLastRowId = element.Id;
        _guiLastRowTime = _editorClock;
        if (_guiSelection.SelectedId != element.Id)
        {
            _guiSelection.SelectedId = element.Id;
            Console.WriteLine($"[UI] Selected {element.Name}");
        }

        if (again)
        {
            _guiLastRowId = null;
            BeginGuiField(GuiProp.Name, inTree: true);
            return;
        }

        if (!_playMode.IsPlaying)
        {
            _guiTreeDrag = new GuiTreeDrag(element, x, y);
        }
    }

    // Drop: onto the Canvas or a Frame, as its last child; onto a Text or Button, right after it in its parent.
    private void DropGuiElement(GuiElement element, GuiTreeRow target)
    {
        if (ReferenceEquals(target.Element, element))
        {
            return;
        }

        GuiNode parent;
        var index = -1;
        if (target.Element is null)
        {
            parent = Gui.Canvas;
        }
        else if (target.Element is GuiFrame frame)
        {
            parent = frame;
        }
        else
        {
            parent = target.Element.Parent ?? Gui.Canvas;
            var siblings = parent.Children.Where(child => !ReferenceEquals(child, element)).ToList();
            index = siblings.IndexOf(target.Element) + 1;
        }

        if (!Gui.CanReparent(element, parent, out var reason))
        {
            _toast.Show($"Can't move it there: {reason}.");
            return;
        }

        var before = Gui.Clone();
        var version = Gui.Version;
        Gui.TryReparent(element, parent, index, out _);
        if (Gui.Version != version)
        {
            RecordGuiChange("Reparent", element.Name, before, _guiSelection.SelectedId);
        }
    }

    private static void SetAlignment(GuiText text, GuiProp prop)
    {
        switch (prop)
        {
            case GuiProp.AlignLeft:
                text.HorizontalAlignment = GuiHorizontalAlignment.Left;
                break;
            case GuiProp.AlignCenter:
                text.HorizontalAlignment = GuiHorizontalAlignment.Center;
                break;
            case GuiProp.AlignRight:
                text.HorizontalAlignment = GuiHorizontalAlignment.Right;
                break;
            case GuiProp.AlignTop:
                text.VerticalAlignment = GuiVerticalAlignment.Top;
                break;
            case GuiProp.AlignMiddle:
                text.VerticalAlignment = GuiVerticalAlignment.Middle;
                break;
            case GuiProp.AlignBottom:
                text.VerticalAlignment = GuiVerticalAlignment.Bottom;
                break;
        }
    }

    // One change to the selected element: one undo step when anything actually changed (the document's Version tells).
    private void ChangeGuiElement(string label, Action<GuiElement> apply)
    {
        if (SelectedGuiElement is not { } element)
        {
            return;
        }

        var before = Gui.Clone();
        var version = Gui.Version;
        apply(element);
        if (Gui.Version != version)
        {
            RecordGuiChange(label, element.Name, before, _guiSelection.SelectedId);
        }
    }

    // ---- The Opacity slider (one undo step per drag) ----

    private static void SetGuiOpacity(GuiSliderDrag slider, float x)
    {
        var fraction = global::System.Math.Clamp((x - slider.Track.X) / MathF.Max(slider.Track.Width, 1f), 0f, 1f);
        slider.Frame.Opacity = MathF.Round(fraction * 100f) / 100f;
    }

    private void CommitGuiSlider()
    {
        if (_guiSlider is not { } slider)
        {
            return;
        }

        _guiSlider = null;
        if (Gui.Version != slider.Version && slider.Frame.Document == Gui)
        {
            RecordGuiChange("Opacity", slider.Frame.Name, slider.Before, _guiSelection.SelectedId);
        }
    }

    // ---- Typed values ----

    private void BeginGuiField(GuiProp prop, bool inTree)
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show(GuiPlayMessage);
            return;
        }

        if (SelectedGuiElement is not { } element)
        {
            return;
        }

        if (_guiFieldEdit is { } current && current.Prop == prop && current.InTree == inTree && current.ElementId == element.Id)
        {
            current.SelectAll = false;
            return;
        }

        _guiFieldEdit = new GuiFieldEdit(prop, element.Id, GuiPanel.ValueText(prop, element), inTree);
    }

    private UiBox? EditBox(GuiFieldEdit edit)
    {
        if (!edit.InTree)
        {
            return _guiPanel.ControlBox(edit.Prop);
        }

        foreach (var row in _guiPanel.Rows)
        {
            if (row.Element?.Id == edit.ElementId)
            {
                return row.Box;
            }
        }

        return null;
    }

    private static bool IsTextProp(GuiProp prop) => prop is GuiProp.Name or GuiProp.Text;

    private static bool IsColorProp(GuiProp prop) => prop is GuiProp.BackgroundColor or GuiProp.TextColor;

    private void TypeGuiField(GuiFieldEdit edit, InputManager input)
    {
        if (input.TextBackspacePressed)
        {
            edit.Text = edit.SelectAll ? string.Empty : edit.Text.Length > 0 ? edit.Text[..^1] : edit.Text;
            edit.SelectAll = false;
        }

        var maxLength = IsTextProp(edit.Prop) ? 200 : 24;
        foreach (var ch in input.TextInputCharacters)
        {
            var accepted = IsTextProp(edit.Prop) ? !char.IsControl(ch)
                : IsColorProp(edit.Prop) ? char.IsAsciiHexDigit(ch) || ch == '#'
                : char.IsAsciiDigit(ch) || ch is '.' or ',' or '-' or '+' or 'e' or 'E';
            if (!accepted)
            {
                continue;
            }

            if (edit.SelectAll)
            {
                edit.Text = string.Empty;
                edit.SelectAll = false;
            }

            if (edit.Text.Length < maxLength)
            {
                edit.Text += ch;
            }
        }

        if (input.EnterPressed)
        {
            CommitGuiField(keepOpenWhenInvalid: true);
        }
    }

    // Applies the typed value: unchanged text changes nothing; a value that cannot be used is refused (the field stays
    // open after Enter, closes after a click away) with a message; a font size outside 8-72 is clamped and says so.
    private void CommitGuiField(bool keepOpenWhenInvalid)
    {
        if (_guiFieldEdit is not { } edit)
        {
            return;
        }

        if (Gui.Find(edit.ElementId) is not { } element || edit.Text == edit.InitialText || _playMode.IsPlaying)
        {
            _guiFieldEdit = null;
            return;
        }

        var text = IsTextProp(edit.Prop) ? edit.Text : edit.Text.Trim();
        string? problem = null;
        Action<GuiElement>? apply = null;
        var label = edit.InTree || edit.Prop == GuiProp.Name ? "Rename" : LabelOf(edit.Prop);
        switch (edit.Prop)
        {
            case GuiProp.Name:
                apply = e => e.Name = text;
                break;
            case GuiProp.Text:
                apply = e =>
                {
                    if (e is GuiText t)
                    {
                        t.Text = text;
                    }
                    else if (e is GuiButton b)
                    {
                        b.Text = text;
                    }
                };
                break;
            case GuiProp.ZOrder:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
                {
                    apply = e => e.ZOrder = z;
                }
                else
                {
                    problem = "ZOrder must be a whole number.";
                }

                break;
            case GuiProp.FontSize:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                {
                    if (size != GuiText.ClampFontSize(size))
                    {
                        _toast.Show($"Font size is {GuiText.MinFontSize} to {GuiText.MaxFontSize}: set to {GuiText.ClampFontSize(size)}.");
                    }

                    apply = e =>
                    {
                        if (e is GuiText t)
                        {
                            t.FontSize = size;
                        }
                        else if (e is GuiButton b)
                        {
                            b.FontSize = size;
                        }
                    };
                }
                else
                {
                    problem = "Font size must be a whole number.";
                }

                break;
            case GuiProp.BackgroundColor:
            case GuiProp.TextColor:
                if (TryParseHex(text, out var color))
                {
                    apply = e => SetColor(e, edit.Prop, color);
                }
                else
                {
                    problem = "Color must be #RRGGBB (six hex digits).";
                }

                break;
            default:
                if (float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value))
                {
                    if (edit.Prop is GuiProp.AnchorX or GuiProp.AnchorY && value != global::System.Math.Clamp(value, 0f, 1f))
                    {
                        _toast.Show("Anchor is 0 to 1: clamped.");
                    }

                    apply = e => SetNumber(e, edit.Prop, value);
                }
                else
                {
                    problem = "Enter a number.";
                }

                break;
        }

        if (apply is null)
        {
            _toast.Show(problem ?? "That value can't be used.");
            if (!keepOpenWhenInvalid)
            {
                _guiFieldEdit = null;
            }

            return;
        }

        _guiFieldEdit = null;
        var before = Gui.Clone();
        var version = Gui.Version;
        apply(element);
        if (Gui.Version != version)
        {
            RecordGuiChange(label, element.Name, before, _guiSelection.SelectedId);
        }
    }

    private static string LabelOf(GuiProp prop) => prop switch
    {
        GuiProp.ZOrder => "ZOrder",
        GuiProp.PosXScale or GuiProp.PosXOffset or GuiProp.PosYScale or GuiProp.PosYOffset => "Position",
        GuiProp.SizeXScale or GuiProp.SizeXOffset or GuiProp.SizeYScale or GuiProp.SizeYOffset => "Size",
        GuiProp.AnchorX or GuiProp.AnchorY => "Anchor",
        GuiProp.BackgroundColor => "Color",
        GuiProp.TextColor => "Text Color",
        GuiProp.Text => "Text",
        GuiProp.FontSize => "Font Size",
        _ => prop.ToString()
    };

    private static void SetNumber(GuiElement element, GuiProp prop, float value)
    {
        var p = element.Position;
        var s = element.Size;
        var a = element.AnchorPoint;
        switch (prop)
        {
            case GuiProp.PosXScale:
                element.Position = new GuiDim2(value, p.X.Offset, p.Y.Scale, p.Y.Offset);
                break;
            case GuiProp.PosXOffset:
                element.Position = new GuiDim2(p.X.Scale, value, p.Y.Scale, p.Y.Offset);
                break;
            case GuiProp.PosYScale:
                element.Position = new GuiDim2(p.X.Scale, p.X.Offset, value, p.Y.Offset);
                break;
            case GuiProp.PosYOffset:
                element.Position = new GuiDim2(p.X.Scale, p.X.Offset, p.Y.Scale, value);
                break;
            case GuiProp.SizeXScale:
                element.Size = new GuiDim2(value, s.X.Offset, s.Y.Scale, s.Y.Offset);
                break;
            case GuiProp.SizeXOffset:
                element.Size = new GuiDim2(s.X.Scale, value, s.Y.Scale, s.Y.Offset);
                break;
            case GuiProp.SizeYScale:
                element.Size = new GuiDim2(s.X.Scale, s.X.Offset, value, s.Y.Offset);
                break;
            case GuiProp.SizeYOffset:
                element.Size = new GuiDim2(s.X.Scale, s.X.Offset, s.Y.Scale, value);
                break;
            case GuiProp.AnchorX:
                element.AnchorPoint = new GuiPoint(value, a.Y);
                break;
            case GuiProp.AnchorY:
                element.AnchorPoint = new GuiPoint(a.X, value);
                break;
        }
    }

    private static void SetColor(GuiElement element, GuiProp prop, GuiColor color)
    {
        switch (element)
        {
            case GuiFrame frame when prop == GuiProp.BackgroundColor:
                frame.BackgroundColor = color;
                break;
            case GuiButton button when prop == GuiProp.BackgroundColor:
                button.BackgroundColor = color;
                break;
            case GuiText text when prop == GuiProp.TextColor:
                text.TextColor = color;
                break;
            case GuiButton button when prop == GuiProp.TextColor:
                button.TextColor = color;
                break;
        }
    }

    // "#RRGGBB" or "RRGGBB", exactly six hex digits.
    private static bool TryParseHex(string text, out GuiColor color)
    {
        color = default;
        var hex = text.StartsWith('#') ? text[1..] : text;
        if (hex.Length != 6 || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = new GuiColor((byte)(value >> 16), (byte)(value >> 8 & 0xFF), (byte)(value & 0xFF));
        return true;
    }

    /// <summary>
    /// Saving or leaving the project: a GUI drag and a value being typed are finished first (as a release or a click away
    /// would), so what is on screen is what gets saved.
    /// </summary>
    internal void FinishGuiEdits()
    {
        CommitGuiDrag();
        CommitGuiPanelEdits();
    }

    /// <summary>Starting Play or leaving the section: typing is finished (as a click away would), a slider or row drag ends.</summary>
    private void CommitGuiPanelEdits()
    {
        CommitGuiField(keepOpenWhenInvalid: false);
        CommitGuiSlider();
        _guiTreeDrag = null;
    }

    /// <summary>Escape: typing is dropped, a slider drag goes back to where it started, a row drag stops. True if there was one.</summary>
    private bool CancelGuiPanelEdits()
    {
        var cancelled = _guiFieldEdit is not null || _guiSlider is not null || _guiTreeDrag is not null;
        _guiFieldEdit = null;
        if (_guiSlider is { } slider)
        {
            slider.Frame.Opacity = slider.StartOpacity;
        }

        _guiSlider = null;
        _guiTreeDrag = null;
        return cancelled;
    }

    private GuiTreeRow? RowAt(float x, float y)
    {
        if (!_guiPanel.Tree.Contains(x, y))
        {
            return null;
        }

        foreach (var row in _guiPanel.Rows)
        {
            if (row.Box.Contains(x, y))
            {
                return row;
            }
        }

        return null;
    }

    private GuiPanelControl? ControlAt(float x, float y)
    {
        foreach (var control in _guiPanel.Controls)
        {
            if (control.Kind != GuiPanelControlKind.Swatch && control.Box.Contains(x, y))
            {
                return control;
            }
        }

        return null;
    }

    private void RenderGuiPanel(float dpiScale)
    {
        if (!GuiPanelVisible)
        {
            return;
        }

        BuildGuiPanel();
        var drop = _guiTreeDrag is { Dragging: true } ? RowAt(_lastMouseX, _lastMouseY) : null;
        var edit = _guiFieldEdit;
        _guiPanel.Render(GuiPanelRect, new GuiPanelView(_guiSelection.SelectedId, !_playMode.IsPlaying, edit?.Prop, edit?.InTree == true, edit?.Text ?? string.Empty,
            edit?.SelectAll == true, _editorClock % 1d < 0.6d, drop, _lastMouseX, _lastMouseY), _width, _height, dpiScale);
    }

    private sealed class GuiFieldEdit(GuiProp prop, int elementId, string initialText, bool inTree)
    {
        public GuiProp Prop { get; } = prop;

        public int ElementId { get; } = elementId;

        public string InitialText { get; } = initialText;

        public bool InTree { get; } = inTree;

        public string Text { get; set; } = initialText;

        public bool SelectAll { get; set; } = initialText.Length > 0;
    }

    private sealed record GuiSliderDrag(GuiFrame Frame, float StartOpacity, GuiDocument Before, long Version, UiBox Track);

    private sealed class GuiTreeDrag(GuiElement element, float startX, float startY)
    {
        public GuiElement Element { get; } = element;

        public float StartX { get; } = startX;

        public float StartY { get; } = startY;

        public bool Dragging { get; set; }
    }
}
