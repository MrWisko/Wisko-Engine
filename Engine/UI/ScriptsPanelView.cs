using Wisko.Engine.World;

namespace Wisko.Engine.UI;

public enum ScriptRowStatus
{
    /// <summary>Edit mode, file present.</summary>
    Attached,

    /// <summary>The referenced file is not in the project.</summary>
    Missing,

    /// <summary>Play Mode, running.</summary>
    Running,

    /// <summary>Play Mode, stopped by an error (or missing).</summary>
    Faulted
}

/// <summary>One attached script as the Scripts panel shows it.</summary>
public readonly record struct ScriptRowView(string Label, ScriptRowStatus Status);

/// <summary>
/// What the World Explorer's Scripts section shows for the current selection: the one selected object's scripts in
/// order, or a hint when there is no single selected object. Built by the editor each frame; the panel only draws it
/// and hit-tests it.
/// </summary>
public sealed class ScriptsPanelView
{
    public static readonly ScriptsPanelView Empty = new(null, "Select an object to see its scripts", [], readOnly: false);

    public ScriptsPanelView(Part? target, string? hint, IReadOnlyList<ScriptRowView> rows, bool readOnly)
    {
        Target = target;
        Hint = hint;
        Rows = rows;
        ReadOnly = readOnly;
    }

    /// <summary>The object whose scripts are listed; null shows <see cref="Hint"/> instead.</summary>
    public Part? Target { get; }

    public string? Hint { get; }

    public IReadOnlyList<ScriptRowView> Rows { get; }

    /// <summary>Play Mode: listed for reference, not editable.</summary>
    public bool ReadOnly { get; }

    /// <summary>A selected environment object (the Baseplate): the section shows its details, read-only, instead of scripts.</summary>
    public EnvironmentDetailsView? Environment { get; init; }

    /// <summary>One or more selected Parts: their Rotation X / Y / Z fields, above the scripts (null: none shown).</summary>
    public RotationFieldsView? Rotation { get; init; }
}

/// <summary>
/// The World panel's rotation fields: each axis's value as shown ("—" when the selected Parts differ), the axis being
/// typed (-1: none) with its text as typed so far (kept exactly until it is committed), and Play's read-only state.
/// </summary>
public sealed record RotationFieldsView(IReadOnlyList<string> Values, int EditingAxis, string EditingText, bool SelectAll, bool ReadOnly);

/// <summary>What the World panel shows about a selected environment object: its name and read-only facts, one per line.</summary>
public sealed record EnvironmentDetailsView(string Title, IReadOnlyList<string> Lines);

public enum ScriptsPanelAction
{
    /// <summary>Over the panel but not on a control (the click is still the panel's).</summary>
    None,
    NewScript,
    AttachScript,
    Detach,

    /// <summary>A script row itself (a double-click opens the script in the editor).</summary>
    Row,

    /// <summary>A Rotation field (ScriptIndex: 0 X, 1 Y, 2 Z): starts typing a value.</summary>
    RotationField
}

/// <summary>A click on the Scripts panel: what was hit, and for Detach which script (index in the object's list; the axis for a RotationField).</summary>
public readonly record struct ScriptsPanelHit(ScriptsPanelAction Action, int ScriptIndex);
