using Wisko.Engine.Rendering;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

public sealed class EditorHistory
{
    private const int MaxActions = 100;
    private readonly LinkedList<EditorCommand> _undo = new();
    private readonly Stack<EditorCommand> _redo = new();

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void Push(EditorCommand command)
    {
        _undo.AddLast(command);
        while (_undo.Count > MaxActions)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    public bool TryUndo(Workspace workspace, PartRenderer partRenderer, EditorSelection selection, out string message)
    {
        if (_undo.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        var command = _undo.Last!.Value;
        _undo.RemoveLast();
        Apply(command, undo: true, workspace, partRenderer, selection);
        _redo.Push(command);
        message = $"{command.GetActionLabel()} {command.PartName}";
        return true;
    }

    public bool TryRedo(Workspace workspace, PartRenderer partRenderer, EditorSelection selection, out string message)
    {
        if (_redo.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        var command = _redo.Pop();
        Apply(command, undo: false, workspace, partRenderer, selection);
        _undo.AddLast(command);
        message = $"{command.GetActionLabel()} {command.PartName}";
        return true;
    }

    private static void Apply(
        EditorCommand command,
        bool undo,
        Workspace workspace,
        PartRenderer partRenderer,
        EditorSelection selection)
    {
        switch (command.Kind)
        {
            case EditorActionKind.EnvironmentChange:
                // The baseplate comes back (undo of a delete) or goes again; Parts and the Part selection stay as they are.
                workspace.SetEnvironmentPresent(command.PartName, undo ? !command.EnvironmentPresentAfter : command.EnvironmentPresentAfter);
                return;

            case EditorActionKind.AtmosphereChange:
                // Environment only: the scene and the selection stay as they are.
                command.AtmosphereTarget!.Set(undo ? command.AtmosphereBefore! : command.AtmosphereAfter!);
                return;

            case EditorActionKind.RenderSettingsChange:
                // Project rendering settings only: the scene and the selection stay as they are.
                command.GraphicsTarget!.Set(undo ? command.GraphicsBefore! : command.GraphicsAfter!);
                return;

            case EditorActionKind.GuiChange:
                // The GUI only: the whole document as it was (Ids, order, properties) and its own selection.
                command.GuiTarget!.ReplaceWith(undo ? command.GuiBefore! : command.GuiAfter!);
                command.GuiSelectionTarget!.SelectedId = undo ? command.GuiSelectedBefore : command.GuiSelectedAfter;
                return;

            case EditorActionKind.Organize:
                // World panel groups only: memberships, group nesting and a group's name (the Parts never move).
                var organized = command.OrganizeParts ?? [];
                var partGroups = undo ? command.PartGroupsBefore : command.PartGroupsAfter;
                for (var i = 0; i < organized.Count; i++)
                {
                    organized[i].Group = partGroups![i];
                }

                var groups = command.OrganizeGroups ?? [];
                var states = undo ? command.GroupStatesBefore : command.GroupStatesAfter;
                for (var i = 0; i < groups.Count; i++)
                {
                    groups[i].Parent = states![i].Parent;
                    groups[i].Name = states[i].Name;
                }

                // The selection and the scene's look stay as they are.
                return;

            case EditorActionKind.AddPart:
                if (undo)
                {
                    RemovePart(workspace, partRenderer, command.Part!);
                }
                else
                {
                    RestorePart(workspace, partRenderer, command.Part!, command.After!.Value);
                }

                break;

            case EditorActionKind.DeletePart:
                if (undo)
                {
                    RestorePart(workspace, partRenderer, command.Part!, command.Before!.Value);
                }
                else
                {
                    RemovePart(workspace, partRenderer, command.Part!);
                }

                break;

            case EditorActionKind.Duplicate:
                if (undo)
                {
                    RemovePart(workspace, partRenderer, command.SecondaryPart!);
                }
                else
                {
                    RestorePart(workspace, partRenderer, command.SecondaryPart!, command.After!.Value);
                }

                break;

            case EditorActionKind.Move:
            case EditorActionKind.Rotate:
            case EditorActionKind.Resize:
                (undo ? command.Before!.Value : command.After!.Value).ApplyTransform(command.Part!);
                partRenderer.InvalidatePart(command.Part!);
                break;

            case EditorActionKind.GroupMove:
            case EditorActionKind.GroupResize:
            case EditorActionKind.GroupRotate:
                ApplyGroupTransform(command, undo, partRenderer);
                break;

            case EditorActionKind.Rename:
                command.Part!.Name = undo ? command.Before!.Value.Name : command.After!.Value.Name;
                partRenderer.InvalidatePart(command.Part!);
                break;

            case EditorActionKind.ColorChange:
                command.Part!.Color = undo ? command.Before!.Value.Color : command.After!.Value.Color;
                partRenderer.InvalidatePart(command.Part!);
                break;

            case EditorActionKind.ScriptsChange:
                (undo ? command.Before!.Value : command.After!.Value).ApplyScripts(command.Part!);
                break;

            case EditorActionKind.AnchorChange:
                var anchorParts = command.GroupMoveParts!;
                var anchorStates = undo ? command.GroupMoveBefore! : command.GroupMoveAfter!;
                for (var i = 0; i < anchorParts.Count; i++)
                {
                    anchorParts[i].Anchored = anchorStates[i].Anchored;
                }

                break;

            case EditorActionKind.LockChange:
                var lockParts = command.GroupMoveParts!;
                var lockStates = undo ? command.GroupMoveBefore! : command.GroupMoveAfter!;
                for (var i = 0; i < lockParts.Count; i++)
                {
                    lockParts[i].Locked = lockStates[i].Locked;
                }

                break;

            case EditorActionKind.AnimationChange:
                command.Part!.Animation = (undo ? command.Before!.Value : command.After!.Value).Animation;
                break;

            case EditorActionKind.PhysicsChange:
                var physicsParts = command.GroupMoveParts!;
                var physicsStates = undo ? command.GroupMoveBefore! : command.GroupMoveAfter!;
                for (var i = 0; i < physicsParts.Count; i++)
                {
                    physicsStates[i].ApplyPhysics(physicsParts[i]);
                }

                break;

            case EditorActionKind.GraphicsChange:
                var graphicsParts = command.GroupMoveParts!;
                var graphicsStates = undo ? command.GroupMoveBefore! : command.GroupMoveAfter!;
                for (var i = 0; i < graphicsParts.Count; i++)
                {
                    graphicsStates[i].ApplyGraphics(graphicsParts[i]);
                    graphicsParts[i].Color = graphicsStates[i].Color;
                }

                break;
        }

        if (command.SelectManyOnUndo is not null || command.SelectManyOnRedo is not null)
        {
            selection.SelectMany(undo ? command.SelectManyOnUndo ?? [] : command.SelectManyOnRedo ?? []);
        }
        else
        {
            selection.Select(undo ? command.SelectOnUndo : command.SelectOnRedo);
        }

        partRenderer.SetSelected(selection.SelectedParts);
    }

    private static void ApplyGroupTransform(EditorCommand command, bool undo, PartRenderer partRenderer)
    {
        var parts = command.GroupMoveParts!;
        var snapshots = undo ? command.GroupMoveBefore! : command.GroupMoveAfter!;
        for (var i = 0; i < parts.Count; i++)
        {
            snapshots[i].ApplyTransform(parts[i]);
            partRenderer.InvalidatePart(parts[i]);
        }
    }

    private static void RemovePart(Workspace workspace, PartRenderer partRenderer, Part part)
    {
        workspace.RemovePart(part);
        partRenderer.UnregisterPart(part);
    }

    private static void RestorePart(Workspace workspace, PartRenderer partRenderer, Part part, PartSnapshot snapshot)
    {
        snapshot.ApplyAll(part);
        if (!WorkspaceContains(workspace, part))
        {
            workspace.AddPart(part);
            partRenderer.RegisterPart(part);
        }
        else
        {
            partRenderer.InvalidatePart(part);
        }
    }

    private static bool WorkspaceContains(Workspace workspace, Part part)
    {
        foreach (var workspacePart in workspace.Parts)
        {
            if (ReferenceEquals(workspacePart, part))
            {
                return true;
            }
        }

        return false;
    }
}
