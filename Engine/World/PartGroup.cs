namespace Wisko.Engine.World;

/// <summary>
/// A World panel group: a name and the group it sits inside (null: the Workspace itself). A Part belongs to at most one
/// group (<see cref="Part.Group"/>); groups can nest. Purely editor organisation: a group has no transform, does
/// nothing to physics or scripts, and does not exist at runtime. It is saved through its members: each Part stores its
/// group's path ("Outer/Inner"), so a group with no Parts anywhere inside it is not kept.
/// </summary>
public sealed class PartGroup(string name, PartGroup? parent = null)
{
    public const char PathSeparator = '/';
    public const int MaxNameLength = 48;

    public string Name { get; set; } = name;

    public PartGroup? Parent { get; set; } = parent;

    /// <summary>0 for a group directly in the Workspace, 1 inside one group, and so on.</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>Its names from the Workspace down, joined with '/': how Scene.json stores it.</summary>
    public string Path => Parent is null ? Name : Parent.Path + PathSeparator + Name;

    /// <summary>True when this group is <paramref name="other"/> or lies inside it (at any depth).</summary>
    public bool IsWithin(PartGroup other)
    {
        for (var group = this; group is not null; group = group.Parent)
        {
            if (ReferenceEquals(group, other))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A usable group name: 1 to 48 characters, no '/', not only spaces.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && !name.Contains(PathSeparator);

    public override string ToString() => Path;
}

/// <summary>Questions about the groups of a workspace's Parts (groups live only through their members).</summary>
public static class PartGroups
{
    /// <summary>
    /// Every group that holds a Part (directly or deeper), parents before children, in the order the workspace first
    /// reaches them (by its Parts' order).
    /// </summary>
    public static List<PartGroup> InOrder(IEnumerable<Part> parts)
    {
        var seen = new HashSet<PartGroup>(ReferenceEqualityComparer.Instance);
        var ordered = new List<PartGroup>();
        foreach (var part in parts)
        {
            if (part.Group is null)
            {
                continue;
            }

            // The chain from the outermost group down to the Part's own.
            var chain = new Stack<PartGroup>();
            for (var group = part.Group; group is not null; group = group.Parent)
            {
                chain.Push(group);
            }

            foreach (var group in chain)
            {
                if (seen.Add(group))
                {
                    ordered.Add(group);
                }
            }
        }

        return ordered;
    }

    /// <summary>The Parts inside <paramref name="group"/> (directly or in groups within it), in workspace order.</summary>
    public static List<Part> PartsIn(IEnumerable<Part> parts, PartGroup group) =>
        parts.Where(part => part.Group is not null && part.Group.IsWithin(group)).ToList();

    /// <summary>
    /// A name for a new group among <paramref name="parent"/>'s groups that no sibling uses: "Group", "Group2", "Group3"...
    /// (siblings never share a name, so every group has its own path).
    /// </summary>
    public static string UniqueName(IEnumerable<Part> parts, PartGroup? parent, string baseName = "Group", PartGroup? except = null)
    {
        var taken = new HashSet<string>(InOrder(parts).Where(group => ReferenceEquals(group.Parent, parent) && !ReferenceEquals(group, except))
            .Select(group => group.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Whether a sibling of <paramref name="group"/> (same parent) already has <paramref name="name"/>.</summary>
    public static bool SiblingHasName(IEnumerable<Part> parts, PartGroup group, string name) =>
        InOrder(parts).Any(other => !ReferenceEquals(other, group) && ReferenceEquals(other.Parent, group.Parent)
            && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase));
}
