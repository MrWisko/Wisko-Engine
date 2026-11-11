namespace Wisko.Engine.Editor;

/// <summary>A ToolBench section: one group of related top-toolbar tools.</summary>
public enum ToolBenchSection
{
    MainTools,
    Scripting,

    /// <summary>
    /// Environment &amp; Graphics: the project's Atmosphere and rendering settings in one panel (Atmosphere, Lighting,
    /// Rendering, Post-Processing), the selection's material and light (Materials), Atmosphere presets and Add Light.
    /// </summary>
    Environment,

    /// <summary>The game GUI: its tools, and the 3D View becomes the 2D GUI workspace (Edit mode).</summary>
    UI
}

/// <summary>A section-specific control on the top toolbar (ToolBench, the project menu and Play are always shown).</summary>
public enum ToolbarControl
{
    Move,
    Resize,
    Rotate,

    /// <summary>Anchors / un-anchors the selection (Part.Anchored); a property toggle, never a tool mode.</summary>
    Anchor,

    /// <summary>Locks / unlocks the selection (Part.Locked, editor protection); a property toggle, never a tool mode.</summary>
    Lock,
    Color,
    AddMeshes,
    World,
    Output,

    /// <summary>Shows or hides the Environment &amp; Graphics panel (on its last global tab: Atmosphere, Lighting, Rendering
    /// or Post-Processing).</summary>
    Environment,

    /// <summary>The Atmosphere presets menu (and Reset to Default).</summary>
    Presets,

    /// <summary>Adds a GUI Frame (one action, never a mode).</summary>
    GuiFrame,

    /// <summary>Adds a GUI Text.</summary>
    GuiText,

    /// <summary>Adds a GUI Button.</summary>
    GuiButton,

    /// <summary>The GUI canvas preview size menu (Viewport, 1280 × 720, 1920 × 1080).</summary>
    GuiPreview,

    /// <summary>Shows or hides the Environment &amp; Graphics panel on its Materials tab (the selection's material and light).</summary>
    Material,

    /// <summary>The Add Light menu (Point, Spot, Directional): a new light in front of the camera.</summary>
    AddLight
}

/// <summary>
/// Which group of tools the top toolbar shows; the ToolBench button (far left) switches it. Editor UI state only: never
/// part of the scene or project, and Main Tools whenever a project is entered. Panels are not section state, so
/// switching never shows or hides World, Output or Physics. A new section is one more row in <see cref="Table"/>.
/// </summary>
public sealed class ToolBench
{
    private static readonly (ToolBenchSection Section, string Label, ToolbarControl[] Controls)[] Table =
    [
        (ToolBenchSection.MainTools, "Main Tools",
            [ToolbarControl.Move, ToolbarControl.Resize, ToolbarControl.Rotate, ToolbarControl.Anchor, ToolbarControl.Lock, ToolbarControl.Color,
                ToolbarControl.AddMeshes, ToolbarControl.World]),
        (ToolBenchSection.Scripting, "Scripting", [ToolbarControl.Output]),
        (ToolBenchSection.Environment, "Environment & Graphics",
            [ToolbarControl.Environment, ToolbarControl.Presets, ToolbarControl.Material, ToolbarControl.AddLight]),
        (ToolBenchSection.UI, "UI", [ToolbarControl.GuiFrame, ToolbarControl.GuiText, ToolbarControl.GuiButton, ToolbarControl.GuiPreview])
    ];

    /// <summary>Every section, in chooser order.</summary>
    public static IReadOnlyList<ToolBenchSection> Sections { get; } = Table.Select(row => row.Section).ToArray();

    public ToolBenchSection Current { get; private set; } = ToolBenchSection.MainTools;

    public IReadOnlyList<ToolbarControl> CurrentControls => ControlsOf(Current);

    public static string LabelOf(ToolBenchSection section) => RowOf(section).Label;

    public static IReadOnlyList<ToolbarControl> ControlsOf(ToolBenchSection section) => RowOf(section).Controls;

    /// <summary>Shows another section's tools; false when it is already the current one.</summary>
    public bool Select(ToolBenchSection section)
    {
        if (section == Current)
        {
            return false;
        }

        Current = section;
        Console.WriteLine($"[ToolBench] {LabelOf(section)}");
        return true;
    }

    /// <summary>Entering a project: Main Tools.</summary>
    public void Reset() => Current = ToolBenchSection.MainTools;

    private static (ToolBenchSection Section, string Label, ToolbarControl[] Controls) RowOf(ToolBenchSection section)
    {
        foreach (var row in Table)
        {
            if (row.Section == section)
            {
                return row;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown ToolBench section.");
    }
}
