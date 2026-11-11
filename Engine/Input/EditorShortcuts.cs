namespace Wisko.Engine.Input;

/// <summary>This frame's 3D-editor command shortcuts, decided by <see cref="EditorShortcuts.Evaluate"/>.</summary>
/// <param name="CommandHeld">
/// The platform's command modifier is held (Ctrl; on macOS Command or Ctrl): letter keys are then shortcuts, never
/// camera flight or the editor's plain-letter keys.
/// </param>
internal readonly record struct EditorShortcutKeys(
    bool Undo,
    bool Redo,
    bool Duplicate,
    bool Group,
    bool Ungroup,
    bool Save,
    bool CommandHeld);

/// <summary>
/// The 3D editor's command shortcuts (Undo, Redo, Duplicate, Group, Ungroup, Save Project) for the platform's convention
/// (<see cref="ShortcutConvention"/>), from the modifiers held and the letter keys pressed this frame. Pure, so both
/// conventions are testable on any OS.
/// <list type="bullet">
/// <item>Windows / Linux (unchanged): Ctrl+Z (and Ctrl+W, AZERTY's Z key) undo, Ctrl+Y redo, Ctrl+D duplicate, Ctrl+G /
/// Ctrl+Shift+G group / ungroup, plus Ctrl+S save. Letters are physical key positions, as before.</item>
/// <item>macOS: Command+Z undo, Command+Shift+Z (or Command+Y) redo, Command+D duplicate, Command+G / Command+Shift+G group /
/// ungroup, Command+S save. Letters are the ones the keyboard layout puts on the keys (AZERTY's Z is Command+Z), as macOS
/// menus do, so Command+W is never Undo.</item>
/// </list>
/// </summary>
internal static class EditorShortcuts
{
    /// <param name="pressed">Whether the key for this upper-case letter was pressed this frame (see the conventions above).</param>
    public static EditorShortcutKeys Evaluate(ShortcutConvention convention, bool control, bool super, bool shift, Func<char, bool> pressed)
    {
        if (!convention.IsMacStyle)
        {
            return new EditorShortcutKeys(
                Undo: control && (pressed('Z') || pressed('W')),
                Redo: control && pressed('Y'),
                Duplicate: control && pressed('D'),
                Group: control && !shift && pressed('G'),
                Ungroup: control && shift && pressed('G'),
                Save: control && !shift && pressed('S'),
                CommandHeld: control);
        }

        // Command alone (Ctrl+Command is not an editor shortcut); Ctrl still keeps the letters from flying the camera.
        var command = super && !control;
        return new EditorShortcutKeys(
            Undo: command && !shift && pressed('Z'),
            Redo: command && ((shift && pressed('Z')) || (!shift && pressed('Y'))),
            Duplicate: command && !shift && pressed('D'),
            Group: command && !shift && pressed('G'),
            Ungroup: command && shift && pressed('G'),
            Save: command && !shift && pressed('S'),
            CommandHeld: super || control);
    }
}
