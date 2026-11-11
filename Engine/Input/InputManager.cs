using Wisko.Engine.Scripting;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Wisko.Engine.Input;

public enum MouseButton
{
    Left,
    Right,
    Middle
}

public sealed class InputManager
{
    private readonly HashSet<MouseButton> _mouseButtonsDown = new();
    private float _lookDeltaX;
    private float _lookDeltaY;
    private float _scrollDelta;
    private bool _suppressNextLookDelta;
    private readonly List<char> _textInput = new();
    private readonly List<EditorKeyEvent> _keyEvents = new();
    private float _scrollDeltaX;

    public float LookDeltaX => _lookDeltaX;
    public float LookDeltaY => _lookDeltaY;
    public float ScrollDelta => _scrollDelta;

    /// <summary>Horizontal wheel/trackpad scroll this frame (positive = right).</summary>
    public float ScrollDeltaX => _scrollDeltaX;

    /// <summary>Key presses this frame, in order, including the OS's key repeat (for text editing).</summary>
    public IReadOnlyList<EditorKeyEvent> KeyEvents => _keyEvents;

    public bool IsRightMouseHeld => _mouseButtonsDown.Contains(MouseButton.Right);
    public bool LeftMousePressed { get; private set; }
    public bool LeftMouseReleased { get; private set; }
    public bool RightMousePressed { get; private set; }
    public bool IsLeftMouseHeld => _mouseButtonsDown.Contains(MouseButton.Left);
    public float MouseX { get; private set; }
    public float MouseY { get; private set; }
    public bool MoveForward { get; private set; }
    public bool MoveBackward { get; private set; }
    public bool MoveLeft { get; private set; }
    public bool MoveRight { get; private set; }
    public bool MoveUp { get; private set; }
    public bool MoveDown { get; private set; }
    public bool MoveFast { get; private set; }
    public float PartMoveX { get; private set; }
    public float PartMoveZ { get; private set; }
    public float PartMoveY { get; private set; }
    public bool DeleteSelectedPartPressed { get; private set; }
    public bool DuplicateSelectedPartPressed { get; private set; }
    public bool EnterPressed { get; private set; }
    public bool EscapePressed { get; private set; }

    /// <summary>Arrow keys and Tab pressed this frame (list and card navigation, e.g. the New World screen).</summary>
    public bool ArrowLeftPressed { get; private set; }
    public bool ArrowRightPressed { get; private set; }
    public bool ArrowUpPressed { get; private set; }
    public bool ArrowDownPressed { get; private set; }
    public bool TabPressed { get; private set; }
    public bool TextBackspacePressed { get; private set; }

    // Layout-aware Unicode text from the OS (spaces, punctuation, AZERTY, accents); used by every text field.
    public IReadOnlyList<char> TextInputCharacters => _textInput;

    public bool RotatePartPressed { get; private set; }
    public float RotatePartDeltaY { get; private set; }
    public bool UndoPressed { get; private set; }
    public bool RedoPressed { get; private set; }

    /// <summary>Save Project: Ctrl+S (Command+S on macOS), see <see cref="EditorShortcuts"/>.</summary>
    public bool SaveProjectPressed { get; private set; }

    /// <summary>Ctrl (either) is held.</summary>
    public bool ControlHeld { get; private set; }

    /// <summary>
    /// The click modifier that adds or removes from a selection (World panel rows, the baseplate in the 3D View): Ctrl,
    /// and on macOS also Command.
    /// </summary>
    public bool SelectionToggleHeld { get; private set; }

    /// <summary>Shift (either) is held: e.g. Shift+click selects a range of World panel rows.</summary>
    public bool ShiftHeld { get; private set; }

    /// <summary>Ctrl+G (Command+G on macOS): put the selection in a new World panel group.</summary>
    public bool GroupPressed { get; private set; }

    /// <summary>Ctrl+Shift+G (Command+Shift+G on macOS): dissolve the selected World panel group.</summary>
    public bool UngroupPressed { get; private set; }

    /// <summary>This frame's keyboard and mouse as Lua scripts see them (one snapshot shared by every script).</summary>
    public ScriptInputState ScriptInput { get; } = new();

    internal void BeginFrame(GameWindow window)
    {
        var keyboard = window.KeyboardState;
        ScriptInputSampler.Sample(keyboard, window.MouseState, ScriptInput);

        EnterPressed = keyboard.IsKeyPressed(Keys.Enter);
        EscapePressed = keyboard.IsKeyPressed(Keys.Escape);
        ArrowLeftPressed = keyboard.IsKeyPressed(Keys.Left);
        ArrowRightPressed = keyboard.IsKeyPressed(Keys.Right);
        ArrowUpPressed = keyboard.IsKeyPressed(Keys.Up);
        ArrowDownPressed = keyboard.IsKeyPressed(Keys.Down);
        TabPressed = keyboard.IsKeyPressed(Keys.Tab);
        TextBackspacePressed = keyboard.IsKeyPressed(Keys.Backspace);
        RotatePartPressed = false;
        RotatePartDeltaY = 0f;

        if (keyboard.IsKeyPressed(Keys.R))
        {
            RotatePartPressed = true;
            var rotateShiftHeld = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
            RotatePartDeltaY = rotateShiftHeld ? -90f : 90f;
        }

        DeleteSelectedPartPressed = keyboard.IsKeyPressed(Keys.Delete)
            || keyboard.IsKeyPressed(Keys.Backspace);

        var ctrlHeld = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        var superHeld = keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper);
        var shiftHeld = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        var convention = ShortcutConvention.Current;
        ControlHeld = ctrlHeld;
        ShiftHeld = shiftHeld;
        SelectionToggleHeld = ctrlHeld || (convention.IsMacStyle && superHeld);

        // The command shortcuts (EditorShortcuts): Ctrl on Windows and Linux (physical keys, as always), Command on macOS
        // (by the letter the keyboard layout puts on the key).
        var shortcuts = EditorShortcuts.Evaluate(convention, ctrlHeld, superHeld, shiftHeld, convention.IsMacStyle
            ? letter => LayoutLetterPressed(keyboard, letter)
            : letter => keyboard.IsKeyPressed(Keys.A + (letter - 'A')));
        GroupPressed = shortcuts.Group;
        UngroupPressed = shortcuts.Ungroup;
        DuplicateSelectedPartPressed = shortcuts.Duplicate;
        UndoPressed = shortcuts.Undo;
        RedoPressed = shortcuts.Redo;
        SaveProjectPressed = shortcuts.Save;

        // macOS: Command+R is not the editor's R (rotate 90°).
        if (convention.IsMacStyle && shortcuts.CommandHeld)
        {
            RotatePartPressed = false;
            RotatePartDeltaY = 0f;
        }

        // The command modifier is reserved for editor shortcuts only — no camera translation while it is held
        // (Ctrl; on macOS Command or Ctrl, so Command+S / D / W never fly the camera).
        if (shortcuts.CommandHeld)
        {
            MoveForward = false;
            MoveBackward = false;
            MoveLeft = false;
            MoveRight = false;
            MoveUp = false;
            MoveDown = false;
        }
        else
        {
            // AZERTY ZQSD: GLFW reports physical key positions (US QWERTY layout), not printed labels.
            // AZERTY Z -> Keys.W, AZERTY Q -> Keys.A; S and D share the same positions on both layouts.
            MoveForward = keyboard.IsKeyDown(Keys.W);
            MoveBackward = keyboard.IsKeyDown(Keys.S);
            MoveLeft = keyboard.IsKeyDown(Keys.A);
            MoveRight = keyboard.IsKeyDown(Keys.D);
            MoveUp = keyboard.IsKeyDown(Keys.Space);
            MoveDown = keyboard.IsKeyDown(Keys.C);
        }

        MoveFast = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);

        var partMoveX = 0f;
        var partMoveZ = 0f;
        var partMoveY = 0f;

        if (keyboard.IsKeyDown(Keys.Left))
        {
            partMoveX -= 1f;
        }

        if (keyboard.IsKeyDown(Keys.Right))
        {
            partMoveX += 1f;
        }

        if (keyboard.IsKeyDown(Keys.Up))
        {
            partMoveZ += 1f;
        }

        if (keyboard.IsKeyDown(Keys.Down))
        {
            partMoveZ -= 1f;
        }

        if (keyboard.IsKeyDown(Keys.PageUp))
        {
            partMoveY += 1f;
        }

        if (keyboard.IsKeyDown(Keys.PageDown))
        {
            partMoveY -= 1f;
        }

        PartMoveX = partMoveX;
        PartMoveZ = partMoveZ;
        PartMoveY = partMoveY;
    }

    // macOS: a letter key pressed this frame that the active keyboard layout labels with this letter (only the keys
    // pressed this frame are looked up).
    private static bool LayoutLetterPressed(KeyboardState keyboard, char letter)
    {
        var wanted = Keys.A + (letter - 'A');
        for (var key = Keys.A; key <= Keys.Z; key++)
        {
            if (keyboard.IsKeyPressed(key) && LayoutLetter(key, 0) == wanted)
            {
                return true;
            }
        }

        return false;
    }

    internal void EndFrame()
    {
        // Wheel and key events arrive (from the window's event processing) before the frame that reads them, so they
        // are cleared after it, not at its start.
        _scrollDelta = 0f;
        _scrollDeltaX = 0f;
        _keyEvents.Clear();

        _lookDeltaX = 0f;
        _lookDeltaY = 0f;
        LeftMousePressed = false;
        LeftMouseReleased = false;
        RightMousePressed = false;
        _textInput.Clear();
    }

    // A native modal (e.g. the file picker) swallows the mouse release, so forget held buttons after it.
    internal void ReleaseMouseButtons()
    {
        _mouseButtonsDown.Clear();
        LeftMousePressed = false;
        LeftMouseReleased = false;
        RightMousePressed = false;
    }

    /// <summary>Headless tests: this frame's text-editing keys, as <see cref="BeginFrame"/> sets them from a window.</summary>
    internal void SetEditingKeysForTest(bool enter = false, bool escape = false, bool backspace = false)
    {
        EnterPressed = enter;
        EscapePressed = escape;
        TextBackspacePressed = backspace;
    }

    internal void OnTextInput(int unicode)
    {
        if (unicode is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
        {
            return;
        }

        _textInput.AddRange(char.ConvertFromUtf32(unicode));
    }

    internal void OnMouseMove(double deltaX, double deltaY, double mouseX, double mouseY)
    {
        MouseX = (float)mouseX;
        MouseY = (float)mouseY;

        if (_mouseButtonsDown.Contains(MouseButton.Right))
        {
            if (_suppressNextLookDelta)
            {
                _suppressNextLookDelta = false;
            }
            else
            {
                _lookDeltaX += (float)deltaX;
                _lookDeltaY += (float)deltaY;
            }
        }
    }

    internal void OnMouseButton(
        OpenTK.Windowing.GraphicsLibraryFramework.MouseButton button,
        bool isDown,
        double mouseX,
        double mouseY)
    {
        if (button == OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left)
        {
            if (isDown)
            {
                LeftMousePressed = true;
                MouseX = (float)mouseX;
                MouseY = (float)mouseY;
            }
            else
            {
                LeftMouseReleased = true;
            }
        }
        else if (button == OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right && isDown)
        {
            RightMousePressed = true;
            MouseX = (float)mouseX;
            MouseY = (float)mouseY;
        }

        if (!TryMapMouseButton(button, out var engineButton))
        {
            return;
        }

        if (isDown)
        {
            _mouseButtonsDown.Add(engineButton);
            if (engineButton == MouseButton.Right)
            {
                _suppressNextLookDelta = true;
            }
        }
        else
        {
            _mouseButtonsDown.Remove(engineButton);
            if (engineButton == MouseButton.Right)
            {
                _suppressNextLookDelta = false;
            }
        }
    }

    internal void OnMouseWheel(float offsetY) => _scrollDelta += offsetY;

    internal void OnMouseWheel(float offsetX, float offsetY)
    {
        _scrollDeltaX += offsetX;
        _scrollDelta += offsetY;
    }

    internal void OnKeyDown(Keys key, int scanCode, KeyModifiers modifiers, bool isRepeat)
    {
        var editorKey = ToEditorKey(LayoutLetter(key, scanCode));
        if (editorKey == EditorKey.Unknown)
        {
            return;
        }

        var flags = KeyModifierFlags.None;
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            flags |= KeyModifierFlags.Shift;
        }

        if ((modifiers & KeyModifiers.Control) != 0)
        {
            flags |= KeyModifierFlags.Control;
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            flags |= KeyModifierFlags.Alt;
        }

        if ((modifiers & KeyModifiers.Super) != 0)
        {
            flags |= KeyModifierFlags.Super;
        }

        _keyEvents.Add(new EditorKeyEvent(editorKey, flags, isRepeat));
    }

    // GLFW key codes are physical (US) positions. Letter shortcuts follow the letter the active keyboard layout puts on
    // the key (as the OS menus do), so on AZERTY the key labelled A is Cmd/Ctrl+A and the key labelled Q never acts as
    // select-all while the OS treats it as Quit. Non-Latin layouts keep the physical position.
    private static Keys LayoutLetter(Keys key, int scanCode)
    {
        if (key is < Keys.A or > Keys.Z)
        {
            return key;
        }

        string? name;
        try
        {
            name = GLFW.GetKeyName(key, scanCode);
        }
        catch (Exception)
        {
            return key;
        }

        return name is { Length: 1 } && char.IsAsciiLetter(name[0])
            ? Keys.A + (char.ToUpperInvariant(name[0]) - 'A')
            : key;
    }

    private static EditorKey ToEditorKey(Keys key) =>
        key switch
        {
            Keys.Left => EditorKey.Left,
            Keys.Right => EditorKey.Right,
            Keys.Up => EditorKey.Up,
            Keys.Down => EditorKey.Down,
            Keys.Home => EditorKey.Home,
            Keys.End => EditorKey.End,
            Keys.PageUp => EditorKey.PageUp,
            Keys.PageDown => EditorKey.PageDown,
            Keys.Backspace => EditorKey.Backspace,
            Keys.Delete => EditorKey.Delete,
            Keys.Enter or Keys.KeyPadEnter => EditorKey.Enter,
            Keys.Tab => EditorKey.Tab,
            Keys.Escape => EditorKey.Escape,
            Keys.Space => EditorKey.Space,
            Keys.A => EditorKey.A,
            Keys.C => EditorKey.C,
            Keys.F => EditorKey.F,
            Keys.G => EditorKey.G,
            Keys.S => EditorKey.S,
            Keys.V => EditorKey.V,
            Keys.W => EditorKey.W,
            Keys.X => EditorKey.X,
            Keys.Y => EditorKey.Y,
            Keys.Z => EditorKey.Z,
            _ => EditorKey.Unknown
        };

    private static bool TryMapMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton button, out MouseButton engineButton)
    {
        switch (button)
        {
            case OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left:
                engineButton = MouseButton.Left;
                return true;
            case OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right:
                engineButton = MouseButton.Right;
                return true;
            default:
                engineButton = default;
                return false;
        }
    }
}
