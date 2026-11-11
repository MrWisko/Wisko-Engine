using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

/// <summary>
/// Numeric rotation in the World panel (above the selection's scripts): Rotation X / Y / Z in degrees for the selected
/// Parts, live while the Rotate gizmo turns them (both are the same <see cref="Part.Rotation"/> numbers). Clicking a
/// value starts typing (the first key replaces it); what is typed stays as typed until Enter, Tab (the next axis) or a
/// click elsewhere commits it: one undo step for every selected Part, that axis set (normalized to [0, 360) like the
/// gizmo) and the others kept. Escape drops the typing. Anything that is not a number of degrees is refused (the field
/// stays open on Enter). Locked Parts refuse it as they refuse the gizmo; Play shows the values read-only.
/// </summary>
public sealed partial class Renderer
{
    private RotationEdit? _rotationEdit;

    // The fields had the keyboard this frame (Enter may have just committed): keys are not editor shortcuts.
    private bool _rotationKeyboardThisFrame;

    /// <summary>A rotation value is being typed (or was committed this frame).</summary>
    private bool RotationFieldOwnsKeyboard => _rotationEdit is not null || _rotationKeyboardThisFrame;

    // What the fields show for the selection now (null: no Part selected, or a script tab / the UI section is in front).
    private RotationFieldsView? BuildRotationView()
    {
        var parts = _selection?.SelectedParts ?? [];
        if (parts.Count == 0)
        {
            return null;
        }

        var values = new[] { RotationInput.FormatAxis(parts, 0), RotationInput.FormatAxis(parts, 1), RotationInput.FormatAxis(parts, 2) };
        return _rotationEdit is { } edit
            ? new RotationFieldsView(values, edit.Axis, edit.Text, edit.SelectAll, _playMode.IsPlaying)
            : new RotationFieldsView(values, -1, string.Empty, false, _playMode.IsPlaying);
    }

    // A click on a field: start typing there (a field already being typed in keeps its text).
    private void BeginRotationEdit(int axis)
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show("Stop Play Mode to rotate objects.");
            return;
        }

        if (_rotationEdit is { } current)
        {
            if (current.Axis == axis)
            {
                current.SelectAll = false;
                return;
            }

            CommitRotationEdit(keepOpenWhenInvalid: false);
        }

        var parts = _selection?.SelectedParts.ToArray() ?? [];
        if (parts.Length == 0)
        {
            return;
        }

        var text = RotationInput.FormatAxis(parts, axis);
        _rotationEdit = new RotationEdit(axis, parts, text == "—" ? string.Empty : text);
        Console.WriteLine($"[Rotation] Typing {RotationInput.AxisNames[axis]} for {(parts.Length == 1 ? parts[0].Name : $"{parts.Length} parts")}");
    }

    // Each frame: the typing (digits, sign, decimal point, Backspace, Enter, Tab), and a click elsewhere commits it.
    private void HandleRotationFields(InputManager input)
    {
        _rotationKeyboardThisFrame = false;
        if (_rotationEdit is not { } edit)
        {
            return;
        }

        // The Parts it was started for are no longer the selection (undo, a click in the World panel, a new world): drop it.
        if (_selection is null || !_selection.SelectedParts.SequenceEqual(edit.Parts))
        {
            _rotationEdit = null;
            return;
        }

        _rotationKeyboardThisFrame = true;
        if (input.TextBackspacePressed)
        {
            edit.Text = edit.SelectAll ? string.Empty : edit.Text.Length > 0 ? edit.Text[..^1] : edit.Text;
            edit.SelectAll = false;
        }

        foreach (var ch in input.TextInputCharacters)
        {
            if (!char.IsAsciiDigit(ch) && ch is not ('.' or ',' or '-' or '+'))
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

        if (input.TabPressed)
        {
            var next = (edit.Axis + (input.ShiftHeld ? 2 : 1)) % 3;
            if (CommitRotationEdit(keepOpenWhenInvalid: true))
            {
                BeginRotationEdit(next);
            }

            return;
        }

        if (input.EnterPressed)
        {
            CommitRotationEdit(keepOpenWhenInvalid: true);
            return;
        }

        if (input.LeftMousePressed && !IsOverRotationField(input, edit.Axis))
        {
            CommitRotationEdit(keepOpenWhenInvalid: false);
        }
    }

    private bool IsOverRotationField(InputManager input, int axis)
    {
        var scripts = BuildScriptsPanelView();
        return _worldExplorer.TryHitScriptsPanel(input.MouseX, input.MouseY, _width, _height, scripts, out var hit)
               && hit.Action == ScriptsPanelAction.RotationField && hit.ScriptIndex == axis;
    }

    /// <summary>
    /// Applies the typed value: that axis of every Part it was started for, one undo step. Nothing typed (or the text as it
    /// started) changes nothing; a value that is not degrees is refused. True when the typing is over.
    /// </summary>
    private bool CommitRotationEdit(bool keepOpenWhenInvalid)
    {
        if (_rotationEdit is not { } edit)
        {
            return true;
        }

        var text = edit.Text.Trim();
        if (text.Length == 0 || text == edit.InitialText)
        {
            _rotationEdit = null;
            return true;
        }

        if (!RotationInput.TryParse(text, out var degrees))
        {
            _toast.Show($"Rotation is a number of degrees, such as 22.5, 90 or -45 (\"{text}\" is not).");
            if (!keepOpenWhenInvalid)
            {
                _rotationEdit = null;
            }

            return !keepOpenWhenInvalid;
        }

        _rotationEdit = null;
        if (edit.Parts.Any(part => part.Locked))
        {
            RefuseLockedEdit();
            return true;
        }

        var value = RotationInput.Normalize(degrees);
        var before = edit.Parts.Select(PartSnapshot.FromPart).ToArray();
        foreach (var part in edit.Parts)
        {
            part.Rotation = RotationInput.WithAxis(part.Rotation, edit.Axis, value);
            _partRenderer.InvalidatePart(part);
        }

        var after = edit.Parts.Select(PartSnapshot.FromPart).ToArray();
        if (before.Select(b => b.Rotation).SequenceEqual(after.Select(a => a.Rotation)))
        {
            return true;
        }

        _editorHistory.Push(EditorCommand.GroupRotate(edit.Parts, before, after));
        Console.WriteLine($"[Rotation] {RotationInput.AxisNames[edit.Axis]} = {RotationInput.Format(value)} (typed \"{text}\") "
            + $"for {(edit.Parts.Length == 1 ? edit.Parts[0].Name : $"{edit.Parts.Length} parts")}");
        return true;
    }

    // Escape: the typing is dropped, nothing changes. True when there was some.
    private bool CancelRotationEdit()
    {
        var had = _rotationEdit is not null;
        _rotationEdit = null;
        return had;
    }

    private sealed class RotationEdit(int axis, Part[] parts, string initialText)
    {
        public int Axis { get; } = axis;

        public Part[] Parts { get; } = parts;

        public string InitialText { get; } = initialText;

        public string Text { get; set; } = initialText;

        // The whole value is selected: the first key replaces it (clicking a value never changes it by itself).
        public bool SelectAll { get; set; } = initialText.Length > 0;
    }
}
