namespace Wisko.Engine.Input;

/// <summary>Keys the editor's text fields and code editor react to (typed characters arrive separately as text).</summary>
public enum EditorKey
{
    Unknown,
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    PageUp,
    PageDown,
    Backspace,
    Delete,
    Enter,
    Tab,
    Escape,
    Space,
    A,
    C,
    F,
    G,
    S,
    V,
    W,
    X,
    Y,
    Z
}

[Flags]
public enum KeyModifierFlags
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,

    /// <summary>Command on macOS, the Windows key elsewhere.</summary>
    Super = 8
}

/// <summary>
/// One key press as the window reported it, including the operating system's key repeat. Engine-owned and
/// platform-neutral: the window layer translates its native key codes into these.
/// </summary>
public readonly record struct EditorKeyEvent(EditorKey Key, KeyModifierFlags Modifiers, bool IsRepeat)
{
    public bool Shift => (Modifiers & KeyModifierFlags.Shift) != 0;

    public bool Control => (Modifiers & KeyModifierFlags.Control) != 0;

    public bool Alt => (Modifiers & KeyModifierFlags.Alt) != 0;

    public bool Super => (Modifiers & KeyModifierFlags.Super) != 0;
}

/// <summary>
/// The platform's shortcut conventions for text editing, decided once here so editor code never asks which OS it
/// runs on: macOS uses Command for shortcuts, Option for word moves, Command+arrows for line/document edges;
/// Windows and Linux use Control for shortcuts and word moves, Home/End and Control+Home/End for edges.
/// </summary>
public sealed class ShortcutConvention
{
    public static readonly ShortcutConvention MacStyle = new(isMacStyle: true);
    public static readonly ShortcutConvention PcStyle = new(isMacStyle: false);

    private ShortcutConvention(bool isMacStyle)
    {
        IsMacStyle = isMacStyle;
    }

    /// <summary>The convention of the platform the editor is running on.</summary>
    public static ShortcutConvention Current { get; } = OperatingSystem.IsMacOS() ? MacStyle : PcStyle;

    public bool IsMacStyle { get; }

    /// <summary>Cmd (macOS) / Ctrl (others) held, with no other command modifier mixed in.</summary>
    public bool IsPrimary(EditorKeyEvent e) =>
        IsMacStyle ? e.Super && !e.Control : e.Control && !e.Super;

    /// <summary>Word-wise caret movement: Option (macOS) / Ctrl (others).</summary>
    public bool IsWordMove(EditorKeyEvent e) => IsMacStyle ? e.Alt : e.Control;

    /// <summary>Label of the primary modifier for hints ("Cmd" / "Ctrl").</summary>
    public string PrimaryLabel => IsMacStyle ? "Cmd" : "Ctrl";
}
