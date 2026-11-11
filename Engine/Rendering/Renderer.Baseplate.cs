using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The host's environment objects in the editor (the baseplate): a world may delete them like an ordinary object. The
/// baseplate is selected from its World panel row, or in the 3D View with Ctrl+click (a plain click on the ground still
/// clears the selection, a drag still box-selects); Delete, Backspace or the row's context menu deletes it, one undo step.
/// A deleted environment object is not drawn, not in the World panel, not collided with in Play, and drags no longer
/// rest on it; the world saves without it (Scene.json "Environment") and nothing ever recreates it.
/// </summary>
public sealed partial class Renderer
{
    private static readonly Vector3 EnvironmentOutlineColor = new(0.30f, 0.62f, 1f);

    // The host's environment (every object it draws); the World panel lists the ones this world has.
    private PhysicsEnvironment? _sceneEnvironment;
    private long _environmentListVersion = -1;

    // The environment object a right-click on its World panel row is about (null: the menu is about Parts or a group).
    private string? _explorerContextEnvironment;

    // Ctrl was held when the left button went down over this environment object (and no Part): a release without a drag
    // selects it instead of clearing the selection.
    private string? _environmentPress;

    private bool IsEnvironmentDrawn(SceneObject drawable) => _workspace is null || _workspace.IsEnvironmentPresent(drawable.Name);
    // The World panel's environment rows: the host's objects this world has (the selection stays on one by name).
    private void SyncEnvironmentList()
    {
        if (_sceneEnvironment is null)
        {
            return;
        }

        var selected = _worldExplorer.SelectedEnvironment?.Name;
        var present = _sceneEnvironment.Boxes.Where(box => _workspace?.IsEnvironmentPresent(box.Name) ?? true).ToArray();
        _worldExplorer.Environment = present;
        _worldExplorer.SelectEnvironment(selected is null ? -1 : Array.FindIndex(present, box => box.Name == selected));
        _environmentListVersion = _workspace?.Version ?? 0;
    }

    // After anything that may have deleted or restored an environment object (undo, redo, loading).
    private void SyncEnvironmentListIfChanged()
    {
        if (_workspace is not null && _workspace.Version != _environmentListVersion)
        {
            SyncEnvironmentList();
        }
    }

    /// <summary>Deletes one of the host's environment objects (the baseplate) from this world: one undo step.</summary>
    internal bool DeleteEnvironmentObject(string name)
    {
        if (_workspace is null)
        {
            return false;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(PlayBlockedMessage);
            return false;
        }

        if (!_workspace.SetEnvironmentPresent(name, present: false))
        {
            return false;
        }

        EndAllEditorDrags();
        _editorHistory.Push(EditorCommand.EnvironmentChange(name, present: false));
        _worldExplorer.SelectEnvironment(-1);
        SyncEnvironmentList();
        Console.WriteLine($"[Environment] Deleted {name}");
        _toast.Show($"{name} deleted ({ShortcutConvention.Current.PrimaryLabel}+Z brings it back)");
        return true;
    }

    // The nearest environment object this world has under the ray (its box), or null.
    private string? PickEnvironment(Ray ray, out float distance)
    {
        distance = float.MaxValue;
        string? nearest = null;
        foreach (var box in _worldExplorer.Environment)
        {
            if (TryRayBox(ray, box.Center - box.Size * 0.5f, box.Center + box.Size * 0.5f, out var hit) && hit < distance)
            {
                distance = hit;
                nearest = box.Name;
            }
        }

        return nearest;
    }

    // A left press in the 3D View that hit no Part: with Ctrl (or Command on macOS) over an environment object, remember it
    // for the release.
    private void NoteEnvironmentPress(InputManager input, Ray ray) =>
        _environmentPress = input.SelectionToggleHeld ? PickEnvironment(ray, out _) : null;

    // The release of a press that hit no Part and did not drag a box: Ctrl+click on the baseplate selects it.
    private bool TrySelectPressedEnvironment()
    {
        var name = _environmentPress;
        _environmentPress = null;
        if (name is null || _selection is null)
        {
            return false;
        }

        var index = _worldExplorer.Environment.ToList().FindIndex(box => box.Name == name);
        if (index < 0)
        {
            return false;
        }

        _selection.Select(null);
        _worldExplorer.SelectedGroup = null;
        SyncPartRendererSelection();
        _worldExplorer.SelectEnvironment(index);
        Console.WriteLine($"[Environment] Selected {name} (Ctrl+click in the 3D View)");
        return true;
    }

    // The selected environment object's box, outlined (Edit mode), slightly outside it so its edges never flicker.
    private void AddEnvironmentOutline()
    {
        if (_worldExplorer.SelectedEnvironment is not { } box)
        {
            return;
        }

        var half = box.Size * 0.5f + new Vector3(0.05f, 0.05f, 0.05f);
        var min = box.Center - half;
        var max = box.Center + half;
        Vector3 Corner(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
        {
            Line(Corner(a), Corner(b), EnvironmentOutlineColor);
        }
    }

    // Slab test: where the ray enters the axis-aligned box (0 when it starts inside), or false.
    private static bool TryRayBox(Ray ray, Vector3 min, Vector3 max, out float distance)
    {
        distance = 0f;
        var tMin = float.NegativeInfinity;
        var tMax = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var origin = axis == 0 ? ray.Origin.X : axis == 1 ? ray.Origin.Y : ray.Origin.Z;
            var direction = axis == 0 ? ray.Direction.X : axis == 1 ? ray.Direction.Y : ray.Direction.Z;
            var low = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var high = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(direction) < 1e-8f)
            {
                if (origin < low || origin > high)
                {
                    return false;
                }

                continue;
            }

            var t1 = (low - origin) / direction;
            var t2 = (high - origin) / direction;
            tMin = MathF.Max(tMin, MathF.Min(t1, t2));
            tMax = MathF.Min(tMax, MathF.Max(t1, t2));
        }

        if (tMax < MathF.Max(tMin, 0f))
        {
            return false;
        }

        distance = MathF.Max(tMin, 0f);
        return true;
    }
}
