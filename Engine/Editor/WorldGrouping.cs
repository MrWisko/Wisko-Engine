using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

/// <summary>
/// The World panel's group operations: Group, Ungroup, moving Parts or a group into another group (or out to the
/// Workspace) and renaming a group. Each one changes only memberships, nesting and names (never a Part's transform,
/// physics or scripts) and returns the undo step for it, or null with why when there is nothing to do.
/// Sibling groups never share a name (it would merge them on reload): a group moved next to a same-named one is renamed
/// "Name2", "Name3"... and that is part of the same undo step.
/// </summary>
internal static class WorldGrouping
{
    /// <summary>
    /// Puts <paramref name="parts"/> in a new group. It goes where they already are when they all share a group (a
    /// group inside it), otherwise directly in the Workspace.
    /// </summary>
    public static EditorCommand? Group(Workspace workspace, IReadOnlyList<Part> parts, IReadOnlyList<Part> selection, out PartGroup? group, out string? error)
    {
        group = null;
        error = null;
        var members = parts.Distinct().Where(part => workspace.Parts.Contains(part)).ToList();
        if (members.Count == 0)
        {
            error = "Select one or more objects to group";
            return null;
        }

        var shared = members[0].Group;
        var parent = members.All(part => ReferenceEquals(part.Group, shared)) ? shared : null;
        group = new PartGroup(PartGroups.UniqueName(workspace.Parts, parent), parent);
        var target = group;
        return Change($"Group", group.Name, members, members.Select(_ => (PartGroup?)target).ToList(), [], [], selection);
    }

    /// <summary>Dissolves <paramref name="group"/>: its Parts and inner groups move up into the group that held it.</summary>
    public static EditorCommand? Ungroup(Workspace workspace, PartGroup group, IReadOnlyList<Part> selection, out string? error)
    {
        error = null;
        var parent = group.Parent;
        var parts = workspace.Parts.Where(part => ReferenceEquals(part.Group, group)).ToList();
        var inner = PartGroups.InOrder(workspace.Parts).Where(other => ReferenceEquals(other.Parent, group)).ToList();
        if (parts.Count == 0 && inner.Count == 0)
        {
            error = $"\"{group.Name}\" has nothing in it";
            return null;
        }

        // Inner groups take the dissolved group's place; one whose name is taken there gets a free one.
        var states = new List<(PartGroup? Parent, string Name)>();
        var taken = new HashSet<string>(PartGroups.InOrder(workspace.Parts)
            .Where(other => ReferenceEquals(other.Parent, parent) && !ReferenceEquals(other, group))
            .Select(other => other.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var child in inner)
        {
            var name = child.Name;
            for (var i = 2; taken.Contains(name); i++)
            {
                name = child.Name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            taken.Add(name);
            states.Add((parent, name));
        }

        return Change("Ungroup", group.Name, parts, parts.Select(_ => parent).ToList(), inner, states, selection);
    }

    /// <summary>Moves <paramref name="parts"/> into <paramref name="target"/> (null: directly in the Workspace).</summary>
    public static EditorCommand? MoveParts(Workspace workspace, IReadOnlyList<Part> parts, PartGroup? target, IReadOnlyList<Part> selection, out string? error)
    {
        error = null;
        var moving = parts.Distinct().Where(part => workspace.Parts.Contains(part) && !ReferenceEquals(part.Group, target)).ToList();
        if (moving.Count == 0)
        {
            error = target is null ? "Already directly in the Workspace" : $"Already in \"{target.Name}\"";
            return null;
        }

        return Change("Move to Group", target?.Name ?? "Workspace", moving, moving.Select(_ => target).ToList(), [], [], selection);
    }

    /// <summary>Moves a whole group (with everything in it) into <paramref name="target"/> (null: the Workspace).</summary>
    public static EditorCommand? MoveGroup(Workspace workspace, PartGroup group, PartGroup? target, IReadOnlyList<Part> selection, out string? error)
    {
        error = null;
        if (target is not null && target.IsWithin(group))
        {
            error = "A group cannot go inside itself";
            return null;
        }

        if (ReferenceEquals(group.Parent, target))
        {
            error = target is null ? "Already directly in the Workspace" : $"Already in \"{target.Name}\"";
            return null;
        }

        var name = PartGroups.UniqueName(workspace.Parts, target, group.Name, except: group);
        return Change("Move to Group", target?.Name ?? "Workspace", [], [], [group], [(target, name)], selection);
    }

    /// <summary>Renames a group (1 to 48 characters, no '/', not a sibling's name).</summary>
    public static EditorCommand? Rename(Workspace workspace, PartGroup group, string name, IReadOnlyList<Part> selection, out string? error)
    {
        error = null;
        var trimmed = name.Trim();
        if (!PartGroup.IsValidName(trimmed))
        {
            error = "A group name is 1 to 48 characters, without '/'";
            return null;
        }

        if (string.Equals(trimmed, group.Name, StringComparison.Ordinal))
        {
            return null;
        }

        if (PartGroups.SiblingHasName(workspace.Parts, group, trimmed))
        {
            error = $"Another group here is already called \"{trimmed}\"";
            return null;
        }

        return Change("Rename", trimmed, [], [], [group], [(group.Parent, trimmed)], selection);
    }

    // Applies the change now and returns its undo step (the selection stays as it is on both sides).
    private static EditorCommand Change(string label, string name, IReadOnlyList<Part> parts, IReadOnlyList<PartGroup?> partGroups,
        IReadOnlyList<PartGroup> groups, IReadOnlyList<(PartGroup? Parent, string Name)> groupStates, IReadOnlyList<Part> selection)
    {
        var partsBefore = parts.Select(part => part.Group).ToList();
        var groupsBefore = groups.Select(group => (group.Parent, group.Name)).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            parts[i].Group = partGroups[i];
        }

        for (var i = 0; i < groups.Count; i++)
        {
            groups[i].Parent = groupStates[i].Parent;
            groups[i].Name = groupStates[i].Name;
        }

        Console.WriteLine($"[WorldExplorer] {label} \"{name}\" ({parts.Count} objects, {groups.Count} groups)");
        var kept = selection.ToList();
        return new EditorCommand
        {
            Kind = EditorActionKind.Organize,
            Label = label,
            PartName = name,
            OrganizeParts = parts,
            PartGroupsBefore = partsBefore,
            PartGroupsAfter = partGroups,
            OrganizeGroups = groups,
            GroupStatesBefore = groupsBefore,
            GroupStatesAfter = groupStates,
            SelectManyOnUndo = kept,
            SelectManyOnRedo = kept
        };
    }
}
