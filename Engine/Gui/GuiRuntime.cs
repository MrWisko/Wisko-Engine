using Wisko.Engine.Input;

namespace Wisko.Engine.Gui;

/// <summary>A run of game GUI text's size in logical pixels (its width, and the height of one line).</summary>
public readonly record struct GuiTextSize(float Width, float LineHeight);

/// <summary>How a button looks this frame (runtime state only, never saved).</summary>
public enum GuiButtonVisual
{
    Normal,
    Hovered,
    Pressed
}

/// <summary>What happened to a button under the pointer (runtime only), as <see cref="GuiRuntime.ButtonEvent"/> reports it.</summary>
public enum GuiButtonEvent
{
    /// <summary>Pressed and released over it (also <see cref="GuiRuntime.ButtonClicked"/>).</summary>
    Click,

    /// <summary>A left press began on it (it is captured).</summary>
    Pressed,

    /// <summary>Its capture ended: the button was released (over it or not), or it was let go (hidden, disabled, removed).</summary>
    Released,

    /// <summary>The pointer came over it (the topmost enabled button under the pointer), held or not.</summary>
    HoverEnter,

    /// <summary>The pointer left it (or it stopped being the topmost enabled button there).</summary>
    HoverExit
}

/// <summary>
/// The game GUI of one Play session: a deep copy of the edit document made when Play starts (so whatever changes it
/// during Play never reaches the authored GUI or UI.json), its layout, and its pointer state. Thrown away when Play
/// stops, so the next session starts with no hover or capture.
/// <para>
/// Buttons: the topmost effectively visible button whose rectangle (cut to the canvas) contains the pointer takes it
/// (Frames, Text and hidden buttons never do). An enabled one is interactive; a disabled one only blocks: no hover,
/// press or click, but the pointer reaches nothing below it (no lower button, no 3D view). A left press on a button captures
/// it until the button is released; releasing over that same button, still visible and enabled, raises
/// <see cref="ButtonClicked"/> once. The captured button looks pressed only while the pointer is over it, and is let
/// go (no click) if it is hidden, disabled or removed while held.
/// </para>
/// <para>
/// <see cref="ButtonEvent"/> reports the same pointer story in order, for scripts: HoverExit / HoverEnter when the
/// topmost enabled button under the pointer changes (pressing does not end a hover), Pressed when a press captures a
/// button, Released when a capture ends (released anywhere, or let go), then the click.
/// </para>
/// <para>
/// TextBoxes: a visible TextBox takes the pointer like a button (it blocks what is below it). A press on one focuses it
/// (the caret goes to the end); while one has focus, <see cref="HandleKeyboard"/> gets the keyboard (typing, paste, the
/// caret keys, Backspace / Delete), Enter submits (<see cref="TextBoxSubmitted"/>, then focus is let go), and a press
/// anywhere else, Escape (<see cref="ReleaseFocus"/>), hiding or removing the box let go of it
/// (<see cref="TextBoxFocusLost"/>). After Escape let go of a box, scripts cannot focus one for
/// <see cref="ScriptRefocusDelayMilliseconds"/>, so a second Escape always reaches the editor (it stops Play).
/// </para>
/// </summary>
public sealed class GuiRuntime : IDisposable
{
    /// <summary>After Escape let go of a TextBox, how long scripts cannot focus one (the next Escape stops Play).</summary>
    public const int ScriptRefocusDelayMilliseconds = 1000;

    // The caret blinks: shown for this long, then hidden for as long (it is shown again at once after any key).
    private const long CaretBlinkMilliseconds = 530;

    private long _caretEpoch;
    private long? _escapeReleasedAt;

    public GuiRuntime(GuiDocument source)
    {
        Document = source.Clone();
    }

    /// <summary>A TextBox got the keyboard (clicked, or a script's Focus); the argument is its Id.</summary>
    public event Action<int>? TextBoxFocused;

    /// <summary>Enter was pressed in a TextBox: its Id and the text as it was (before ClearOnSubmit empties it).</summary>
    public event Action<int, string>? TextBoxSubmitted;

    /// <summary>A TextBox let go of the keyboard: its Id, and whether that was because Enter submitted it.</summary>
    public event Action<int, bool>? TextBoxFocusLost;

    /// <summary>The TextBox that has the keyboard (null when none): while there is one, the game gets no keys.</summary>
    public int? FocusedTextBoxId { get; private set; }

    /// <summary>Where typing goes in the focused TextBox: a UTF-16 index into its Text, always between characters.</summary>
    public int CaretIndex { get; private set; }

    /// <summary>Whether the focused TextBox's caret is drawn now (it blinks).</summary>
    public bool CaretVisible => FocusedTextBoxId is not null && (Environment.TickCount64 - _caretEpoch) / CaretBlinkMilliseconds % 2 == 0;

    /// <summary>Measures game GUI text (set by the renderer: its font); null: an estimate is used (headless).</summary>
    public Func<string, int, GuiTextSize>? TextMeasurer { get; set; }

    /// <summary>How wide <paramref name="text"/> is at <paramref name="fontSize"/> pixels, and one line's height.</summary>
    public GuiTextSize MeasureText(string text, int fontSize)
    {
        var size = GuiText.ClampFontSize(fontSize);
        if (TextMeasurer is { } measure)
        {
            return measure(text, size);
        }

        // Without a font (headless): an average glyph is about half the font size wide.
        return new GuiTextSize(text.EnumerateRunes().Count() * size * 0.5f, size * 1.25f);
    }

    /// <summary>
    /// Gives a TextBox the keyboard (null when it has it, else why not): it must be visible. A script
    /// (<paramref name="byScript"/>) cannot do this just after Escape let go of a box.
    /// </summary>
    public string? Focus(int id, bool byScript)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Document.Find(id) is not GuiTextBox box || !EffectivelyVisible(id))
        {
            return "only a visible TextBox can take the keyboard";
        }

        if (byScript && _escapeReleasedAt is { } escaped && Environment.TickCount64 - escaped < ScriptRefocusDelayMilliseconds)
        {
            return "the player just pressed Escape to leave the text box; try again in a moment";
        }

        if (FocusedTextBoxId == id)
        {
            return null;
        }

        ReleaseFocus(submitted: false);
        FocusedTextBoxId = id;
        CaretIndex = box.Text.Length;
        _caretEpoch = Environment.TickCount64;
        TextBoxFocused?.Invoke(id);
        return null;
    }

    /// <summary>
    /// The focused TextBox lets go of the keyboard (false when none had it). <paramref name="byEscape"/>: the player
    /// pressed Escape (scripts then cannot refocus for a moment).
    /// </summary>
    public bool ReleaseFocus(bool submitted, bool byEscape = false)
    {
        if (FocusedTextBoxId is not { } id)
        {
            return false;
        }

        FocusedTextBoxId = null;
        CaretIndex = 0;
        if (byEscape)
        {
            _escapeReleasedAt = Environment.TickCount64;
        }

        TextBoxFocusLost?.Invoke(id, submitted);
        return true;
    }

    /// <summary>
    /// One frame of keyboard for the focused TextBox: the typed characters, then the editing keys in order (Left /
    /// Right, Home / End, Backspace / Delete, Ctrl+V or Cmd+V paste, Enter submits). False when no TextBox has focus
    /// (the keyboard is not the GUI's this frame).
    /// </summary>
    public bool HandleKeyboard(IReadOnlyList<char> typed, IReadOnlyList<EditorKeyEvent> keys, Func<string?>? clipboard)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (FocusedTextBoxId is not { } id)
        {
            return false;
        }

        if (Document.Find(id) is not GuiTextBox box || !EffectivelyVisible(id))
        {
            ReleaseFocus(submitted: false);
            return false;
        }

        if (typed.Count > 0)
        {
            Insert(box, new string(typed.ToArray()));
        }

        foreach (var key in keys)
        {
            var text = box.Text;
            CaretIndex = global::System.Math.Clamp(CaretIndex, 0, text.Length);
            switch (key.Key)
            {
                case EditorKey.Left:
                    CaretIndex = PreviousBoundary(text, CaretIndex);
                    break;
                case EditorKey.Right:
                    CaretIndex = NextBoundary(text, CaretIndex);
                    break;
                case EditorKey.Home or EditorKey.Up:
                    CaretIndex = 0;
                    break;
                case EditorKey.End or EditorKey.Down:
                    CaretIndex = text.Length;
                    break;
                case EditorKey.Backspace when CaretIndex > 0:
                {
                    var start = PreviousBoundary(text, CaretIndex);
                    box.Text = text.Remove(start, CaretIndex - start);
                    CaretIndex = start;
                    break;
                }
                case EditorKey.Delete when CaretIndex < text.Length:
                    box.Text = text.Remove(CaretIndex, NextBoundary(text, CaretIndex) - CaretIndex);
                    break;
                case EditorKey.V when key.Control || key.Super:
                    if (clipboard?.Invoke() is { Length: > 0 } pasted)
                    {
                        Insert(box, pasted);
                    }

                    break;
                case EditorKey.Enter:
                    var submitted = box.Text;
                    TextBoxSubmitted?.Invoke(id, submitted);
                    if (box.ClearOnSubmit)
                    {
                        box.Text = string.Empty;
                    }

                    ReleaseFocus(submitted: true);
                    return true;
                default:
                    continue;
            }

            _caretEpoch = Environment.TickCount64;
        }

        return true;
    }

    // Typed or pasted text at the caret: one line, as much as MaxLength leaves room for.
    private void Insert(GuiTextBox box, string text)
    {
        var text0 = box.Text;
        var room = box.MaxLength - text0.EnumerateRunes().Count();
        var insert = GuiTextBox.Normalize(text, global::System.Math.Max(0, room));
        if (insert.Length == 0)
        {
            return;
        }

        var caret = global::System.Math.Clamp(CaretIndex, 0, text0.Length);
        box.Text = text0.Insert(caret, insert);
        CaretIndex = caret + insert.Length;
        _caretEpoch = Environment.TickCount64;
    }

    private static int PreviousBoundary(string text, int index) =>
        index <= 0 ? 0 : index >= 2 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2]) ? index - 2 : index - 1;

    private static int NextBoundary(string text, int index) =>
        index >= text.Length ? text.Length : index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]) ? index + 2 : index + 1;

    // Shown: it and every element above it are visible (as the last layout saw them).
    private bool EffectivelyVisible(int id)
    {
        foreach (var entry in Layout.Update(Document, Layout.Canvas))
        {
            if (entry.Element.Id == id)
            {
                return entry.Visible;
            }
        }

        return false;
    }

    /// <summary>A button was clicked (pressed and released over it); the argument is its element Id.</summary>
    public event Action<int>? ButtonClicked;

    /// <summary>A press, release or hover change on a button (see <see cref="GuiButtonEvent"/>; clicks come through <see cref="ButtonClicked"/>).</summary>
    public event Action<GuiButtonEvent, int>? ButtonEvent;

    /// <summary>The topmost enabled button under the pointer, held or not (null when none): what hover events follow.</summary>
    public int? PointerOverButtonId { get; private set; }

    /// <summary>The Play copy. Never the edit document.</summary>
    public GuiDocument Document { get; }

    /// <summary>The copy laid out in the last canvas rectangle (rebuilt only when the copy or the rectangle changes).</summary>
    public GuiLayout Layout { get; } = new();

    public bool IsDisposed { get; private set; }

    /// <summary>The topmost button under the pointer while nothing is held (null when none).</summary>
    public int? HoveredButtonId { get; private set; }

    /// <summary>The button a left press began on, until it is released (null when none).</summary>
    public int? CapturedButtonId { get; private set; }

    /// <summary>The captured button is under the pointer now (it looks pressed).</summary>
    public bool CapturedUnderPointer { get; private set; }

    /// <summary>The elements in draw order for this frame's canvas rectangle (the game view).</summary>
    public IReadOnlyList<GuiLayoutEntry> Update(GuiRect canvas)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Layout.Update(Document, canvas);
    }

    /// <summary>
    /// One frame of pointer input. <paramref name="pointerInView"/> is false when the pointer is not over the game view
    /// (outside it, or over editor chrome): then nothing is hit. Returns true when this frame's left press was taken by
    /// a button (the caller must not use that press for anything else).
    /// </summary>
    public bool UpdatePointer(GuiRect canvas, float x, float y, bool pointerInView, bool leftPressed, bool leftHeld, bool leftReleased)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var entries = Update(canvas);
        var top = pointerInView ? TopTaker(entries, canvas, x, y) : null;
        var hit = top is GuiButton { Enabled: true } ? top.Id : (int?)null;
        SetPointerOver(hit);

        // A TextBox: a press on it gives it the keyboard; a press anywhere else (or outside the game) lets go of it.
        if (FocusedTextBoxId is { } focused && (!EffectivelyVisible(focused) || leftPressed && top?.Id != focused))
        {
            ReleaseFocus(submitted: false);
        }

        if (leftPressed && top is GuiTextBox box)
        {
            Focus(box.Id, byScript: false);
        }

        // A held button that is no longer clickable is let go: no click.
        if (CapturedButtonId is { } held && !IsClickable(entries, canvas, held))
        {
            CapturedButtonId = null;
            ButtonEvent?.Invoke(GuiButtonEvent.Released, held);
        }

        // A press on any visible button is used up (a disabled one just blocks); only an enabled one is captured.
        var consumed = leftPressed && top is not null;
        if (leftPressed && hit is { } pressed)
        {
            if (CapturedButtonId is { } previous && previous != pressed)
            {
                ButtonEvent?.Invoke(GuiButtonEvent.Released, previous);
            }

            CapturedButtonId = pressed;
            ButtonEvent?.Invoke(GuiButtonEvent.Pressed, pressed);
        }

        if (CapturedButtonId is { } captured && (leftReleased || !leftHeld))
        {
            CapturedButtonId = null;
            ButtonEvent?.Invoke(GuiButtonEvent.Released, captured);
            if (leftReleased && hit == captured)
            {
                ButtonClicked?.Invoke(captured);
            }
        }

        CapturedUnderPointer = CapturedButtonId is { } stillHeld && hit == stillHeld;
        HoveredButtonId = CapturedButtonId is null && !leftHeld ? hit : null;
        return consumed;
    }

    /// <summary>
    /// The pointer left the game (a script tab covers it, Play is stopping): no hover, no capture, no click, and no
    /// TextBox keeps the keyboard.
    /// </summary>
    public void CancelPointer()
    {
        ReleaseFocus(submitted: false);
        SetPointerOver(null);
        if (CapturedButtonId is { } held)
        {
            CapturedButtonId = null;
            ButtonEvent?.Invoke(GuiButtonEvent.Released, held);
        }

        HoveredButtonId = null;
        CapturedUnderPointer = false;
    }

    /// <summary>
    /// A script changed the GUI: a hovered or held button that can no longer be clicked (hidden, disabled, no area, gone)
    /// is let go now, by the same rule the next <see cref="UpdatePointer"/> would apply, so no click can follow.
    /// </summary>
    public void ReleaseUnclickable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (FocusedTextBoxId is { } focused && !EffectivelyVisible(focused))
        {
            ReleaseFocus(submitted: false);
        }

        if (CapturedButtonId is null && HoveredButtonId is null && PointerOverButtonId is null)
        {
            return;
        }

        var canvas = Layout.Canvas;
        var entries = Layout.Update(Document, canvas);
        if (PointerOverButtonId is { } over && !IsClickable(entries, canvas, over))
        {
            SetPointerOver(null);
        }

        if (CapturedButtonId is { } held && !IsClickable(entries, canvas, held))
        {
            CapturedButtonId = null;
            CapturedUnderPointer = false;
            ButtonEvent?.Invoke(GuiButtonEvent.Released, held);
        }

        if (HoveredButtonId is { } hovered && !IsClickable(entries, canvas, hovered))
        {
            HoveredButtonId = null;
        }
    }

    /// <summary>How <paramref name="button"/> looks now: Pressed (captured and under the pointer), Hovered, or Normal.</summary>
    public GuiButtonVisual VisualOf(GuiButton button) =>
        button.Id == CapturedButtonId && CapturedUnderPointer ? GuiButtonVisual.Pressed
        : button.Id == HoveredButtonId ? GuiButtonVisual.Hovered
        : GuiButtonVisual.Normal;

    // The session is over: no more events, nothing held or hovered.
    public void Dispose()
    {
        ButtonEvent = null;
        TextBoxFocused = null;
        TextBoxSubmitted = null;
        TextBoxFocusLost = null;
        PointerOverButtonId = null;
        CancelPointer();
        IsDisposed = true;
    }

    // HoverExit for the button the pointer leaves, then HoverEnter for the one it comes over.
    private void SetPointerOver(int? button)
    {
        if (PointerOverButtonId == button)
        {
            return;
        }

        var left = PointerOverButtonId;
        PointerOverButtonId = button;
        if (left is { } exited)
        {
            ButtonEvent?.Invoke(GuiButtonEvent.HoverExit, exited);
        }

        if (button is { } entered)
        {
            ButtonEvent?.Invoke(GuiButtonEvent.HoverEnter, entered);
        }
    }

    // The topmost visible button (enabled or not) or TextBox under the point: the draw order walked backwards, Frames,
    // Text, Images and hidden elements skipped.
    private static GuiElement? TopTaker(IReadOnlyList<GuiLayoutEntry> entries, GuiRect canvas, float x, float y)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (Occupies(entries[i], canvas) && entries[i].Rect.Contains(x, y) && canvas.Contains(x, y))
            {
                return entries[i].Element;
            }
        }

        return null;
    }

    private static bool IsClickable(IReadOnlyList<GuiLayoutEntry> entries, GuiRect canvas, int id)
    {
        foreach (var entry in entries)
        {
            if (entry.Element.Id == id)
            {
                return Clickable(entry, canvas);
            }
        }

        return false;
    }

    // A visible, enabled button with some area inside the canvas.
    private static bool Clickable(GuiLayoutEntry entry, GuiRect canvas) => entry.Element is GuiButton { Enabled: true } && Occupies(entry, canvas);

    // A visible button (enabled or not) or TextBox with some area inside the canvas: it takes the pointer.
    private static bool Occupies(GuiLayoutEntry entry, GuiRect canvas) =>
        entry.Element is GuiButton or GuiTextBox && entry.Visible && entry.Rect.Width > 0f && entry.Rect.Height > 0f
        && entry.Rect.X < canvas.Right && canvas.X < entry.Rect.Right && entry.Rect.Y < canvas.Bottom && canvas.Y < entry.Rect.Bottom;
}
