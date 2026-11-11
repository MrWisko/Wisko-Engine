using Wisko.Engine.Atmosphere;
using Wisko.Engine.Gui;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

public enum EditorActionKind
{
    AddPart,
    DeletePart,
    Move,
    GroupMove,
    Rotate,
    Resize,
    GroupResize,
    Duplicate,
    Rename,
    ColorChange,
    ScriptsChange,

    /// <summary>Anchoring / un-anchoring one or more Parts in one step (each Part's own previous value is kept).</summary>
    AnchorChange,

    /// <summary>Locking / unlocking one or more Parts in one step (each Part's own previous value is kept).</summary>
    LockChange,

    /// <summary>A Physics panel setting (Gravity, Can Collide, Friction or Mass) on one or more Parts in one step.</summary>
    PhysicsChange,

    /// <summary>A model's saved animation settings (Animation panel: Loop, Speed, Play on start) in one step.</summary>
    AnimationChange,

    /// <summary>A GUI edit (add, move, resize, delete, duplicate): the whole GUI before and after, and its selection.</summary>
    GuiChange,

    /// <summary>An Atmosphere edit (one slider drag, toggle, colour change, preset or Reset); selection is untouched.</summary>
    AtmosphereChange,

    /// <summary>
    /// World panel organisation (Group, Ungroup, moving Parts or groups between groups, renaming a group): which group
    /// each Part and group was in, and a group's name, before and after. Transforms, physics and scripts never change.
    /// </summary>
    Organize,

    /// <summary>
    /// A Materials tab edit on one or more Parts in one step: material, model material overrides, light
    /// and colour, each Part's own values before and after (one slider drag, switch, choice, texture or button).
    /// </summary>
    GraphicsChange,

    /// <summary>A rendering settings edit (the project's Graphics.json settings); the scene and selection are untouched.</summary>
    RenderSettingsChange,

    /// <summary>Deleting (or restoring) one of the host's environment objects, the baseplate, in this world; Parts untouched.</summary>
    EnvironmentChange,

    /// <summary>A typed rotation (World panel: Rotation X / Y / Z) on one or more Parts in one step.</summary>
    GroupRotate
}

public sealed class EditorCommand
{
    public required EditorActionKind Kind { get; init; }
    public required string PartName { get; init; }
    public Part? Part { get; init; }
    public Part? SecondaryPart { get; init; }
    public PartSnapshot? Before { get; init; }
    public PartSnapshot? After { get; init; }
    public Part? SelectOnUndo { get; init; }
    public Part? SelectOnRedo { get; init; }
    public IReadOnlyList<Part>? GroupMoveParts { get; init; }
    public IReadOnlyList<PartSnapshot>? GroupMoveBefore { get; init; }
    public IReadOnlyList<PartSnapshot>? GroupMoveAfter { get; init; }
    public IReadOnlyList<Part>? SelectManyOnUndo { get; init; }
    public IReadOnlyList<Part>? SelectManyOnRedo { get; init; }
    public AtmosphereState? AtmosphereTarget { get; init; }
    public AtmosphereSettings? AtmosphereBefore { get; init; }
    public AtmosphereSettings? AtmosphereAfter { get; init; }
    public Graphics.GraphicsState? GraphicsTarget { get; init; }
    public Graphics.GraphicsSettings? GraphicsBefore { get; init; }
    public Graphics.GraphicsSettings? GraphicsAfter { get; init; }

    /// <summary>EnvironmentChange: whether the environment object named <see cref="PartName"/> is there after the step.</summary>
    public bool EnvironmentPresentAfter { get; init; }

    /// <summary>What a <see cref="EditorActionKind.PhysicsChange"/>, GuiChange or Organize did ("Gravity", "Add Frame", "Group", ...).</summary>
    public string? Label { get; init; }

    // Organize: each Part's group before / after (same order as OrganizeParts), and each group's parent group and name
    // before / after (same order as OrganizeGroups).
    public IReadOnlyList<Part>? OrganizeParts { get; init; }
    public IReadOnlyList<PartGroup?>? PartGroupsBefore { get; init; }
    public IReadOnlyList<PartGroup?>? PartGroupsAfter { get; init; }
    public IReadOnlyList<PartGroup>? OrganizeGroups { get; init; }
    public IReadOnlyList<(PartGroup? Parent, string Name)>? GroupStatesBefore { get; init; }
    public IReadOnlyList<(PartGroup? Parent, string Name)>? GroupStatesAfter { get; init; }

    public GuiDocument? GuiTarget { get; init; }
    public GuiDocument? GuiBefore { get; init; }
    public GuiDocument? GuiAfter { get; init; }
    public GuiSelection? GuiSelectionTarget { get; init; }
    public int? GuiSelectedBefore { get; init; }
    public int? GuiSelectedAfter { get; init; }

    /// <summary>
    /// A GUI edit: <paramref name="target"/> becomes a copy of <paramref name="before"/> (undo) or
    /// <paramref name="after"/> (redo) in place, and the GUI selection what it was then. The Part selection is untouched.
    /// </summary>
    public static EditorCommand GuiChange(string label, string name, GuiDocument target, GuiDocument before, GuiDocument after, GuiSelection selection, int? selectedBefore, int? selectedAfter) =>
        new()
        {
            Kind = EditorActionKind.GuiChange,
            Label = label,
            PartName = name,
            GuiTarget = target,
            GuiBefore = before,
            GuiAfter = after,
            GuiSelectionTarget = selection,
            GuiSelectedBefore = selectedBefore,
            GuiSelectedAfter = selectedAfter
        };

    /// <summary>The environment object <paramref name="name"/> (the baseplate) deleted (<paramref name="present"/> false) or restored.</summary>
    public static EditorCommand EnvironmentChange(string name, bool present) =>
        new()
        {
            Kind = EditorActionKind.EnvironmentChange,
            PartName = name,
            EnvironmentPresentAfter = present
        };

    /// <summary>An Atmosphere edit; <paramref name="label"/> names it in the undo message (e.g. "Sky Color").</summary>
    public static EditorCommand AtmosphereChange(AtmosphereState target, AtmosphereSettings before, AtmosphereSettings after, string label) =>
        new()
        {
            Kind = EditorActionKind.AtmosphereChange,
            PartName = label,
            AtmosphereTarget = target,
            AtmosphereBefore = before,
            AtmosphereAfter = after
        };

    public static EditorCommand Add(Part part) =>
        new()
        {
            Kind = EditorActionKind.AddPart,
            PartName = part.Name,
            Part = part,
            After = PartSnapshot.FromPart(part),
            SelectOnUndo = null,
            SelectOnRedo = part
        };

    public static EditorCommand Delete(Part part) =>
        new()
        {
            Kind = EditorActionKind.DeletePart,
            PartName = part.Name,
            Part = part,
            Before = PartSnapshot.FromPart(part),
            SelectOnUndo = part,
            SelectOnRedo = null
        };

    public static EditorCommand Move(Part part, PartSnapshot before, PartSnapshot after) =>
        new()
        {
            Kind = EditorActionKind.Move,
            PartName = part.Name,
            Part = part,
            Before = before,
            After = after,
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    public static EditorCommand GroupMove(
        IReadOnlyList<Part> parts,
        IReadOnlyList<PartSnapshot> before,
        IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.GroupMove,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    /// <summary>A typed rotation on these Parts (each one's own transform before and after): one undo step.</summary>
    public static EditorCommand GroupRotate(IReadOnlyList<Part> parts, IReadOnlyList<PartSnapshot> before, IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.GroupRotate,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    public static EditorCommand Rotate(Part part, PartSnapshot before, PartSnapshot after) =>
        new()
        {
            Kind = EditorActionKind.Rotate,
            PartName = part.Name,
            Part = part,
            Before = before,
            After = after,
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    public static EditorCommand Resize(Part part, PartSnapshot before, PartSnapshot after) =>
        new()
        {
            Kind = EditorActionKind.Resize,
            PartName = part.Name,
            Part = part,
            Before = before,
            After = after,
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    public static EditorCommand GroupResize(
        IReadOnlyList<Part> parts,
        IReadOnlyList<PartSnapshot> before,
        IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.GroupResize,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    public static EditorCommand Duplicate(Part source, Part duplicate) =>
        new()
        {
            Kind = EditorActionKind.Duplicate,
            PartName = duplicate.Name,
            Part = source,
            SecondaryPart = duplicate,
            After = PartSnapshot.FromPart(duplicate),
            SelectOnUndo = source,
            SelectOnRedo = duplicate
        };

    public static EditorCommand Rename(Part part, string beforeName, string afterName) =>
        new()
        {
            Kind = EditorActionKind.Rename,
            PartName = afterName,
            Part = part,
            Before = PartSnapshot.FromPart(part) with { Name = beforeName },
            After = PartSnapshot.FromPart(part) with { Name = afterName },
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    public static EditorCommand ColorChange(Part part, PartSnapshot before, PartSnapshot after) =>
        new()
        {
            Kind = EditorActionKind.ColorChange,
            PartName = part.Name,
            Part = part,
            Before = before,
            After = after,
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    /// <summary>Attaching, creating or detaching scripts on one object (the script files themselves are not touched).</summary>
    public static EditorCommand ScriptsChange(Part part, PartSnapshot before, PartSnapshot after) =>
        new()
        {
            Kind = EditorActionKind.ScriptsChange,
            PartName = part.Name,
            Part = part,
            Before = before,
            After = after,
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    /// <summary>
    /// Part.Anchored set on several Parts at once; <paramref name="before"/> keeps each Part's own previous value, so
    /// undoing a mixed selection brings back the mix.
    /// </summary>
    public static EditorCommand AnchorChange(IReadOnlyList<Part> parts, IReadOnlyList<PartSnapshot> before, IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.AnchorChange,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    /// <summary>Part.Locked set on several Parts at once; <paramref name="before"/> keeps each Part's own previous value.</summary>
    public static EditorCommand LockChange(IReadOnlyList<Part> parts, IReadOnlyList<PartSnapshot> before, IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.LockChange,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    /// <summary>
    /// A Physics panel setting set on several Parts at once; <paramref name="before"/> keeps each Part's own previous
    /// settings, so undo brings back a mix. <paramref name="label"/> names the setting.
    /// </summary>
    public static EditorCommand PhysicsChange(string label, IReadOnlyList<Part> parts, IReadOnlyList<PartSnapshot> before, IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.PhysicsChange,
            Label = label,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    /// <summary>A model's saved animation settings changed (the preview itself is never an undo step).</summary>
    public static EditorCommand AnimationChange(Part part, PartSnapshot before, PartSnapshot after, string label) =>
        new()
        {
            Kind = EditorActionKind.AnimationChange,
            Label = label,
            PartName = part.Name,
            Part = part,
            Before = before,
            After = after,
            SelectOnUndo = part,
            SelectOnRedo = part
        };

    /// <summary>
    /// A Materials tab edit on several Parts at once; <paramref name="before"/> keeps each Part's own previous
    /// material, light and colour, so undo brings back a mix. <paramref name="label"/> names the setting.
    /// </summary>
    public static EditorCommand GraphicsChange(string label, IReadOnlyList<Part> parts, IReadOnlyList<PartSnapshot> before, IReadOnlyList<PartSnapshot> after) =>
        new()
        {
            Kind = EditorActionKind.GraphicsChange,
            Label = label,
            PartName = parts.Count == 1 ? parts[0].Name : $"{parts.Count} parts",
            GroupMoveParts = parts,
            GroupMoveBefore = before,
            GroupMoveAfter = after,
            SelectManyOnUndo = parts,
            SelectManyOnRedo = parts
        };

    /// <summary>A rendering settings edit; <paramref name="label"/> names it in the undo message (e.g. "Bloom").</summary>
    public static EditorCommand RenderSettingsChange(Graphics.GraphicsState target, Graphics.GraphicsSettings before, Graphics.GraphicsSettings after, string label) =>
        new()
        {
            Kind = EditorActionKind.RenderSettingsChange,
            Label = label,
            PartName = label,
            GraphicsTarget = target,
            GraphicsBefore = before,
            GraphicsAfter = after
        };

    public string GetActionLabel() =>
        Kind switch
        {
            EditorActionKind.AddPart => "Add",
            EditorActionKind.DeletePart => "Delete",
            EditorActionKind.Move => "Move",
            EditorActionKind.GroupMove => "Move",
            EditorActionKind.GroupRotate => "Rotate",
            EditorActionKind.Rotate => "Rotate",
            EditorActionKind.Resize => "Resize",
            EditorActionKind.GroupResize => "Resize",
            EditorActionKind.Duplicate => "Duplicate",
            EditorActionKind.Rename => "Rename",
            EditorActionKind.ColorChange => "Color",
            EditorActionKind.ScriptsChange => "Scripts",
            EditorActionKind.AnchorChange => "Anchor",
            EditorActionKind.LockChange => "Lock",
            EditorActionKind.PhysicsChange => Label ?? "Physics",
            EditorActionKind.AnimationChange => Label ?? "Animation",
            EditorActionKind.GuiChange => Label ?? "UI",
            EditorActionKind.AtmosphereChange => "Atmosphere",
            EditorActionKind.Organize => Label ?? "Organize",
            EditorActionKind.GraphicsChange => Label ?? "Material",
            EditorActionKind.RenderSettingsChange => "Rendering",
            EditorActionKind.EnvironmentChange => EnvironmentPresentAfter ? "Restore" : "Delete",
            _ => Kind.ToString()
        };
}
