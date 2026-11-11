namespace Wisko.Engine.Scripting;

/// <summary>
/// The keyboard and mouse as scripts see them for one frame: one snapshot taken by the engine per frame and shared by
/// every script, so all scripts agree on what happened. Keys and mouse buttons are identified by engine-owned names
/// (<see cref="KeyNames"/>, <see cref="MouseButtonNames"/>), never by platform key codes. Pressed and Released are
/// edges: true for exactly the one frame in which the key or button went down or up.
/// </summary>
public sealed class ScriptInputState
{
    /// <summary>Every key name scripts can ask about (case-insensitive in Lua): letters, digits, named keys, punctuation.</summary>
    public static IReadOnlyList<string> KeyNames { get; } = BuildKeyNames();

    /// <summary>Every mouse button name scripts can ask about (case-insensitive in Lua).</summary>
    public static IReadOnlyList<string> MouseButtonNames { get; } = ["Left", "Right", "Middle", "Button4", "Button5", "Button6", "Button7", "Button8"];

    private static readonly Dictionary<string, int> KeyIndices = BuildIndex(KeyNames);
    private static readonly Dictionary<string, int> MouseButtonIndices = BuildIndex(MouseButtonNames);

    private readonly ButtonStates _keys = new(KeyNames.Count);
    private readonly ButtonStates _mouseButtons = new(MouseButtonNames.Count);

    /// <summary>Cursor position in window coordinates (logical pixels, origin top-left).</summary>
    public float MouseX { get; set; }

    public float MouseY { get; set; }

    /// <summary>Cursor movement during this frame (logical pixels; +x right, +y down).</summary>
    public float MouseDeltaX { get; set; }

    public float MouseDeltaY { get; set; }

    /// <summary>Vertical scroll during this frame (wheel notches / trackpad units; positive = away from the user).</summary>
    public float ScrollDelta { get; set; }

    public static bool TryGetKeyIndex(string name, out int index) => KeyIndices.TryGetValue(name, out index);

    public static bool TryGetMouseButtonIndex(string name, out int index) => MouseButtonIndices.TryGetValue(name, out index);

    public bool IsKeyDown(string name) => TryGetKeyIndex(name, out var index) && _keys.Down[index];

    public bool IsKeyPressed(string name) => TryGetKeyIndex(name, out var index) && _keys.Pressed[index];

    public bool IsKeyReleased(string name) => TryGetKeyIndex(name, out var index) && _keys.Released[index];

    public bool IsMouseButtonDown(string name) => TryGetMouseButtonIndex(name, out var index) && _mouseButtons.Down[index];

    public bool IsMouseButtonPressed(string name) => TryGetMouseButtonIndex(name, out var index) && _mouseButtons.Pressed[index];

    public bool IsMouseButtonReleased(string name) => TryGetMouseButtonIndex(name, out var index) && _mouseButtons.Released[index];

    /// <summary>Sets one key (by <see cref="KeyNames"/> index) for this frame.</summary>
    public void SetKey(int index, bool down, bool pressed, bool released) => _keys.Set(index, down, pressed, released);

    /// <summary>Sets one mouse button (by <see cref="MouseButtonNames"/> index) for this frame.</summary>
    public void SetMouseButton(int index, bool down, bool pressed, bool released) => _mouseButtons.Set(index, down, pressed, released);

    /// <summary>Nothing held, nothing moved: the state before any input arrives.</summary>
    public void Clear()
    {
        _keys.Clear();
        _mouseButtons.Clear();
        MouseDeltaX = MouseDeltaY = ScrollDelta = 0f;
    }

    /// <summary>Every key up, no key edges (the mouse untouched): a game TextBox has the keyboard.</summary>
    public void ClearKeys() => _keys.Clear();

    /// <summary>The same held keys/buttons and cursor position without this frame's edges and movement.</summary>
    public void ClearEdges()
    {
        _keys.ClearEdges();
        _mouseButtons.ClearEdges();
        MouseDeltaX = MouseDeltaY = ScrollDelta = 0f;
    }

    public void CopyFrom(ScriptInputState other)
    {
        _keys.CopyFrom(other._keys);
        _mouseButtons.CopyFrom(other._mouseButtons);
        MouseX = other.MouseX;
        MouseY = other.MouseY;
        MouseDeltaX = other.MouseDeltaX;
        MouseDeltaY = other.MouseDeltaY;
        ScrollDelta = other.ScrollDelta;
    }

    // Names whose state is set, in name order (what the Lua runtime publishes each frame).
    internal IEnumerable<string> KeysDown => Active(KeyNames, _keys.Down);

    internal IEnumerable<string> KeysPressed => Active(KeyNames, _keys.Pressed);

    internal IEnumerable<string> KeysReleased => Active(KeyNames, _keys.Released);

    internal IEnumerable<string> MouseButtonsDown => Active(MouseButtonNames, _mouseButtons.Down);

    internal IEnumerable<string> MouseButtonsPressed => Active(MouseButtonNames, _mouseButtons.Pressed);

    internal IEnumerable<string> MouseButtonsReleased => Active(MouseButtonNames, _mouseButtons.Released);

    private static IEnumerable<string> Active(IReadOnlyList<string> names, bool[] flags)
    {
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i])
            {
                yield return names[i];
            }
        }
    }

    private static string[] BuildKeyNames()
    {
        var names = new List<string>();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            names.Add(letter.ToString());
        }

        for (var digit = '0'; digit <= '9'; digit++)
        {
            names.Add(digit.ToString());
        }

        names.AddRange(
        [
            "Space", "Enter", "Escape", "Tab", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
            "Left", "Right", "Up", "Down",
            "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt", "LeftSuper", "RightSuper",
            "CapsLock",
            "Minus", "Equal", "LeftBracket", "RightBracket", "Backslash", "Semicolon", "Apostrophe", "Comma", "Period",
            "Slash", "GraveAccent"
        ]);

        for (var f = 1; f <= 12; f++)
        {
            names.Add($"F{f}");
        }

        return names.ToArray();
    }

    private static Dictionary<string, int> BuildIndex(IReadOnlyList<string> names)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            index.Add(names[i], i);
        }

        return index;
    }

    private sealed class ButtonStates(int count)
    {
        public bool[] Down { get; } = new bool[count];

        public bool[] Pressed { get; } = new bool[count];

        public bool[] Released { get; } = new bool[count];

        public void Set(int index, bool down, bool pressed, bool released)
        {
            Down[index] = down;
            Pressed[index] = pressed;
            Released[index] = released;
        }

        public void Clear()
        {
            Array.Clear(Down);
            ClearEdges();
        }

        public void ClearEdges()
        {
            Array.Clear(Pressed);
            Array.Clear(Released);
        }

        public void CopyFrom(ButtonStates other)
        {
            Array.Copy(other.Down, Down, Down.Length);
            Array.Copy(other.Pressed, Pressed, Pressed.Length);
            Array.Copy(other.Released, Released, Released.Length);
        }
    }
}
