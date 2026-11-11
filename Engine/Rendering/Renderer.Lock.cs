using Wisko.Engine.Editor;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering;

/// <summary>
/// Lock: editor protection against accidental changes, not physics (Play and scripts ignore it). A locked Part is
/// skipped by viewport click and box selection, and a selection containing one refuses Move / Resize / Rotate (gizmos,
/// dragging, keys) and Delete as a whole, so nothing half-moves. World Explorer still selects it, so it can always be
/// unlocked. The Main Tools Lock button reads Part.Locked from the selection every frame (nothing is cached).
/// </summary>
public sealed partial class Renderer
{
    private const string LockedMessage = "Locked — unlock to edit.";

    /// <summary>The selection's Part.Locked, read from the Parts themselves (None when nothing is selected).</summary>
    private ToggleState SelectionLockState()
    {
        var selected = _selection?.SelectedParts ?? [];
        if (selected.Count == 0)
        {
            return ToggleState.None;
        }

        var locked = selected.Count(part => part.Locked);
        return locked == 0 ? ToggleState.Off : locked == selected.Count ? ToggleState.On : ToggleState.Mixed;
    }

    private bool SelectionHasLocked() => _selection?.SelectedParts.Any(part => part.Locked) == true;

    /// <summary>Tells the user why an edit of a locked selection did nothing.</summary>
    private void RefuseLockedEdit()
    {
        _toast.Show(LockedMessage);
    }

    /// <summary>
    /// Lock: when every selected Part is locked, unlocks them all; otherwise (none or a mix) locks them all. One undo
    /// step that remembers each Part's own previous value. The selection is kept, so a second click unlocks again.
    /// Edit Mode only.
    /// </summary>
    private void ToggleLock()
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show("Stop Play Mode to change Lock.");
            return;
        }

        var selected = (_selection?.SelectedParts ?? []).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var locked = !selected.All(part => part.Locked);
        if (selected.All(part => part.Locked == locked))
        {
            return;
        }

        var before = selected.Select(PartSnapshot.FromPart).ToArray();
        foreach (var part in selected)
        {
            part.Locked = locked;
        }

        // A locked selection must not keep dragging (a drag can only be in progress before the click, but be safe).
        EndAllEditorDrags();
        _editorHistory.Push(EditorCommand.LockChange(selected, before, selected.Select(PartSnapshot.FromPart).ToArray()));
        Console.WriteLine($"[Lock] {(locked ? "Locked" : "Unlocked")} {(selected.Length == 1 ? selected[0].Name : $"{selected.Length} parts")}");
    }

    // The mouse is over a handle of the active transform tool's gizmo (hover is worked out earlier in the same frame).
    private bool IsOverGizmoHandle() => _toolState.ActiveTool switch
    {
        EditorToolMode.Move => _moveHover != GizmoAxis.None,
        EditorToolMode.Resize => _resizeHover != ResizeHandle.None || _resizeCornerHover != ResizeCornerHandle.None,
        EditorToolMode.Rotate => _rotateHover != RotateHandle.None,
        _ => false
    };
}
