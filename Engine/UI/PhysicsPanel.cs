using System.Globalization;
using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>How the selection's mass is set: every Part on Auto, every Part the same manual mass, or not the same.</summary>
internal enum PhysicsMassMode
{
    Auto,
    Manual,
    Mixed
}

/// <summary>A Physics panel value that can be typed in.</summary>
internal enum PhysicsPanelField
{
    None,
    Mass,
    Friction
}

/// <summary>
/// What the Physics panel shows, read from the selected Parts every frame (nothing is stored). <see cref="Friction"/>
/// is null when the selected Parts' frictions differ. <see cref="MassValue"/> is the shared manual mass, or on Auto the
/// mass of a single selected Part (null for several Parts, or for one physics does not simulate). While a value is
/// being typed, <see cref="EditingField"/> names it and <see cref="EditText"/> is the text so far.
/// </summary>
internal readonly record struct PhysicsPanelView(
    string Selection,
    ToggleState Anchored,
    ToggleState Gravity,
    ToggleState CanCollide,
    ToggleState IsTrigger,
    float? Friction,
    PhysicsMassMode MassMode,
    float? MassValue,
    bool Editable,
    PhysicsPanelField EditingField = PhysicsPanelField.None,
    string EditText = "",
    bool EditSelectAll = false);

/// <summary>
/// The Physics panel: a column docked on the left under the toolbar, the counterpart of World on the right and drawn
/// the same way (title band, separator, colours, text sizes). It names the selection and, for a selection, shows its
/// settings: Body (Anchored, Gravity, Mass) and Collision (Can Collide, Is Trigger, Friction). Checkboxes also show a
/// mixed selection; Mass is Auto (with the mass it works out to) or a typed manual mass; Friction is a slider with a
/// typed value. Read-only while playing.
/// </summary>
internal sealed class PhysicsPanel : IDisposable
{
    /// <summary>Same width as World.</summary>
    public const float Width = WorldExplorerRenderer.SidebarWidth;

    private const float TitleHeight = 34f;
    private const float SidePadding = 10f;
    private const float TextScale = 1.85f;
    private const float SmallTextScale = 1.65f;
    private const float CloseSize = 18f;

    // Fixed rows below the title (hit-testing must not depend on font metrics).
    private const float SelectionY = TitleHeight + 10f;
    private const float BodyHeaderY = TitleHeight + 42f;
    private const float AnchoredRowY = TitleHeight + 62f;
    private const float RowHeight = 26f;
    private const float GravityRowY = AnchoredRowY + RowHeight;
    private const float MassRowY = GravityRowY + RowHeight;
    private const float CollisionHeaderY = MassRowY + RowHeight + 10f;
    private const float CanCollideRowY = CollisionHeaderY + 20f;
    private const float IsTriggerRowY = CanCollideRowY + RowHeight;
    private const float FrictionRowY = IsTriggerRowY + RowHeight;
    private const float CheckboxSize = 16f;

    // The value boxes and buttons inside a row, from its right edge.
    private const float FieldHeight = 20f;
    private const float MassFieldWidth = 72f;
    private const float AutoButtonWidth = 44f;
    private const float FrictionFieldWidth = 48f;
    private const float FrictionLabelWidth = 64f;
    private const float Gap = 6f;

    private static readonly Vector3 Fill = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 Separator = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 TitleText = new(0.92f, 0.94f, 0.98f);
    private static readonly Vector3 Text = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 SectionText = new(0.70f, 0.74f, 0.80f);
    private static readonly Vector3 Muted = new(0.58f, 0.62f, 0.68f);
    private static readonly Vector3 CloseHover = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 RowHover = new(0.18f, 0.19f, 0.22f);
    private static readonly Vector3 BoxBorder = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 CheckedFill = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 CheckedBorder = new(0.55f, 0.75f, 1.0f);
    private static readonly Vector3 MixedFill = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 DisabledBorder = new(0.33f, 0.35f, 0.40f);
    private static readonly Vector3 DisabledFill = new(0.24f, 0.26f, 0.30f);
    private static readonly Vector3 DisabledMark = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 FieldFill = new(0.08f, 0.085f, 0.10f);
    private static readonly Vector3 FieldBorder = new(0.26f, 0.28f, 0.32f);
    private static readonly Vector3 ButtonFill = new(0.20f, 0.21f, 0.25f);
    private static readonly Vector3 ButtonHover = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 Track = new(0.26f, 0.28f, 0.32f);

    private IUiRenderer _ui = null!;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    /// <summary>The × at the right of the header.</summary>
    public static UiBox CloseButton(UiBox panel) =>
        new(panel.X + panel.Width - SidePadding - CloseSize, panel.Y + (TitleHeight - CloseSize) * 0.5f, CloseSize, CloseSize);

    /// <summary>The Anchored row (label and checkbox): clicking anywhere on it toggles Anchored. Shown only for a selection.</summary>
    public static UiBox AnchoredRow(UiBox panel) => Row(panel, AnchoredRowY);

    /// <summary>The Gravity row: clicking anywhere on it toggles Gravity.</summary>
    public static UiBox GravityRow(UiBox panel) => Row(panel, GravityRowY);

    public static UiBox MassRow(UiBox panel) => Row(panel, MassRowY);

    /// <summary>The Can Collide row: clicking anywhere on it toggles Can Collide.</summary>
    public static UiBox CanCollideRow(UiBox panel) => Row(panel, CanCollideRowY);

    /// <summary>The Is Trigger row: clicking anywhere on it toggles Is Trigger.</summary>
    public static UiBox IsTriggerRow(UiBox panel) => Row(panel, IsTriggerRowY);

    public static UiBox FrictionRow(UiBox panel) => Row(panel, FrictionRowY);

    /// <summary>The mass value: Auto's mass or the manual one. Clicking it starts typing a manual mass.</summary>
    public static UiBox MassField(UiBox panel) => FieldAtRight(MassRow(panel), MassFieldWidth);

    /// <summary>The Auto button left of the mass value: back to Auto mass.</summary>
    public static UiBox MassAutoButton(UiBox panel)
    {
        var field = MassField(panel);
        return new UiBox(field.X - Gap - AutoButtonWidth, field.Y, AutoButtonWidth, FieldHeight);
    }

    /// <summary>The friction value at the right of its slider. Clicking it starts typing a friction.</summary>
    public static UiBox FrictionField(UiBox panel) => FieldAtRight(FrictionRow(panel), FrictionFieldWidth);

    /// <summary>The friction slider's track (0 at the left, 1 at the right).</summary>
    public static UiBox FrictionTrack(UiBox panel)
    {
        var row = FrictionRow(panel);
        var x = row.X + SidePadding * 0.5f + FrictionLabelWidth;
        return new UiBox(x, row.Y, MathF.Max(24f, FrictionField(panel).X - Gap - 4f - x), RowHeight);
    }

    /// <summary>Where a press starts a friction drag: the track and a little either side of it (for its ends).</summary>
    public static UiBox FrictionSlider(UiBox panel)
    {
        var track = FrictionTrack(panel);
        return new UiBox(track.X - 6f, track.Y, track.Width + 12f, track.Height);
    }

    /// <summary>The friction for a mouse x along the slider, in steps of 0.01 (dragging continues past the ends, clamped).</summary>
    public static float FrictionAt(UiBox panel, float x)
    {
        var track = FrictionTrack(panel);
        var fraction = global::System.Math.Clamp((x - track.X) / MathF.Max(track.Width, 1f), 0f, 1f);
        return MathF.Round(fraction * 100f) / 100f;
    }

    /// <summary>A mass as the panel shows it (and starts the typed text with).</summary>
    public static string FormatMass(float mass) =>
        mass >= 1000f ? mass.ToString("0", CultureInfo.InvariantCulture)
        : mass >= 0.01f ? mass.ToString("0.00", CultureInfo.InvariantCulture)
        : mass.ToString("0.000", CultureInfo.InvariantCulture);

    public static string FormatFriction(float friction) => friction.ToString("0.00", CultureInfo.InvariantCulture);

    public void Render(UiBox panel, PhysicsPanelView view, bool caretVisible, float mouseX, float mouseY, int screenWidth, int screenHeight, float dpiScale)
    {
        _ui.BeginBatch(dpiScale);
        _ui.SetClip(new UiClip(panel.X, panel.Y, panel.Width, panel.Height));
        _ui.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, Fill);
        _ui.DrawRect(panel.X + panel.Width - 1f, panel.Y, 1f, panel.Height, Separator);

        _ui.DrawString(panel.X + SidePadding, panel.Y + 10f, "Physics", TitleText, TextScale, FontWeight.SemiBold);
        var close = CloseButton(panel);
        var overClose = close.Contains(mouseX, mouseY);
        if (overClose)
        {
            _ui.DrawRect(close.X, close.Y, close.Width, close.Height, CloseHover);
        }

        UiIcons.DrawCross(_ui, close.X + close.Width * 0.5f, close.Y + close.Height * 0.5f, 8f, overClose ? TitleText : Text);
        _ui.DrawRect(panel.X + SidePadding, panel.Y + TitleHeight, panel.Width - SidePadding * 2f, 1f, Separator);

        var textWidth = MathF.Max(0f, panel.Width - SidePadding * 2f);
        _ui.DrawString(panel.X + SidePadding, panel.Y + SelectionY, Shorten(view.Selection, textWidth), view.Anchored == ToggleState.None ? Muted : Text, SmallTextScale);
        if (view.Anchored != ToggleState.None)
        {
            _ui.DrawString(panel.X + SidePadding, panel.Y + BodyHeaderY, "Body", SectionText, SmallTextScale, FontWeight.SemiBold);
            DrawCheckRow(AnchoredRow(panel), "Anchored", view.Anchored, view.Editable, mouseX, mouseY);
            DrawCheckRow(GravityRow(panel), "Gravity", view.Gravity, view.Editable, mouseX, mouseY);
            DrawMassRow(panel, view, caretVisible, mouseX, mouseY);
            _ui.DrawString(panel.X + SidePadding, panel.Y + CollisionHeaderY, "Collision", SectionText, SmallTextScale, FontWeight.SemiBold);
            DrawCheckRow(CanCollideRow(panel), "Can Collide", view.CanCollide, view.Editable, mouseX, mouseY);
            DrawCheckRow(IsTriggerRow(panel), "Is Trigger", view.IsTrigger, view.Editable, mouseX, mouseY);
            DrawFrictionRow(panel, view, caretVisible, mouseX, mouseY);
        }

        _ui.SetClip(null);
        _ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    private static UiBox Row(UiBox panel, float y) =>
        new(panel.X + SidePadding * 0.5f, panel.Y + y, MathF.Max(0f, panel.Width - SidePadding), RowHeight);

    private static UiBox FieldAtRight(UiBox row, float width) =>
        new(row.X + row.Width - SidePadding * 0.5f - width, row.Y + (RowHeight - FieldHeight) * 0.5f, width, FieldHeight);

    // The label on the left, its checkbox on the right: ticked, empty, or a dash for a mixed selection. Muted and
    // without hover while playing (settings are changed in Edit Mode only).
    private void DrawCheckRow(UiBox row, string label, ToggleState state, bool editable, float mouseX, float mouseY)
    {
        if (editable && row.Contains(mouseX, mouseY))
        {
            _ui.DrawRect(row.X, row.Y, row.Width, row.Height, RowHover);
        }

        DrawRowLabel(row, label, editable);
        var box = new UiBox(row.X + row.Width - SidePadding * 0.5f - CheckboxSize, row.Y + (row.Height - CheckboxSize) * 0.5f, CheckboxSize, CheckboxSize);
        var cx = box.X + box.Width * 0.5f;
        var cy = box.Y + box.Height * 0.5f;
        switch (state)
        {
            case ToggleState.On:
                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, editable ? CheckedFill : DisabledFill);
                _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, editable ? CheckedBorder : DisabledBorder, 1f);
                UiIcons.DrawCheck(_ui, cx, cy, 10f, editable ? TitleText : DisabledMark);
                break;
            case ToggleState.Mixed:
                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, editable ? MixedFill : DisabledFill);
                _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, editable ? BoxBorder : DisabledBorder, 1.5f);
                _ui.DrawRect(cx - 4f, cy - 1f, 8f, 2f, editable ? TitleText : DisabledMark);
                break;
            default:
                _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, editable ? BoxBorder : DisabledBorder, 1.5f);
                break;
        }
    }

    // "Mass", the Auto button (lit while every selected Part is on Auto) and the value: Auto's mass in muted text, a
    // manual mass in normal text, "Mixed" when the selection differs, "—" for several Parts on Auto (their masses
    // differ, so no one number is shown).
    private void DrawMassRow(UiBox panel, PhysicsPanelView view, bool caretVisible, float mouseX, float mouseY)
    {
        DrawRowLabel(MassRow(panel), "Mass", view.Editable);

        var auto = MassAutoButton(panel);
        var autoOn = view.MassMode == PhysicsMassMode.Auto;
        var hover = view.Editable && auto.Contains(mouseX, mouseY);
        if (autoOn)
        {
            _ui.DrawRect(auto.X, auto.Y, auto.Width, auto.Height, view.Editable ? CheckedFill : DisabledFill);
            _ui.DrawBorderRect(auto.X, auto.Y, auto.Width, auto.Height, view.Editable ? CheckedBorder : DisabledBorder, 1f);
        }
        else
        {
            _ui.DrawRect(auto.X, auto.Y, auto.Width, auto.Height, hover ? ButtonHover : ButtonFill);
            _ui.DrawBorderRect(auto.X, auto.Y, auto.Width, auto.Height, view.Editable ? FieldBorder : DisabledBorder, 1f);
        }

        DrawCentered(auto, "Auto", autoOn && view.Editable ? TitleText : view.Editable ? Text : Muted);

        var (text, muted) = view.MassMode switch
        {
            PhysicsMassMode.Manual when view.MassValue is { } mass => (FormatMass(mass), false),
            PhysicsMassMode.Auto when view.MassValue is { } mass => (FormatMass(mass), true),
            PhysicsMassMode.Mixed => ("Mixed", true),
            _ => ("—", true)
        };
        DrawField(MassField(panel), PhysicsPanelField.Mass, text, muted, view, caretVisible, mouseX, mouseY);
    }

    // "Friction", its slider (0-1; no knob for a mixed selection) and its value.
    private void DrawFrictionRow(UiBox panel, PhysicsPanelView view, bool caretVisible, float mouseX, float mouseY)
    {
        DrawRowLabel(FrictionRow(panel), "Friction", view.Editable);

        var track = FrictionTrack(panel);
        var y = track.Y + track.Height * 0.5f - 2f;
        _ui.DrawRect(track.X, y, track.Width, 4f, Track);
        if (view.Friction is { } friction)
        {
            var fraction = global::System.Math.Clamp(friction, 0f, 1f);
            _ui.DrawRect(track.X, y, track.Width * fraction, 4f, view.Editable ? CheckedFill : Muted);
            var x = track.X + track.Width * fraction;
            _ui.DrawRect(x - 4f, track.Y + track.Height * 0.5f - 7f, 8f, 14f, view.Editable ? BoxBorder : DisabledBorder);
            _ui.DrawRect(x - 3f, track.Y + track.Height * 0.5f - 6f, 6f, 12f, view.Editable ? TitleText : DisabledMark);
        }

        var text = view.Friction is { } value ? FormatFriction(value) : "Mixed";
        DrawField(FrictionField(panel), PhysicsPanelField.Friction, text, view.Friction is null, view, caretVisible, mouseX, mouseY);
    }

    // A value box. While it is being typed in: the typed text (highlighted while it is all selected, so the first key
    // replaces it) with a caret, and an accent border.
    private void DrawField(UiBox field, PhysicsPanelField kind, string text, bool muted, PhysicsPanelView view, bool caretVisible, float mouseX, float mouseY)
    {
        var editing = view.EditingField == kind;
        var border = editing ? CheckedBorder
            : !view.Editable ? DisabledBorder
            : field.Contains(mouseX, mouseY) ? BoxBorder
            : FieldBorder;
        _ui.DrawRect(field.X, field.Y, field.Width, field.Height, FieldFill);
        _ui.DrawBorderRect(field.X, field.Y, field.Width, field.Height, border, 1f);

        var lineHeight = _ui.MeasureLineHeight(SmallTextScale);
        var textY = field.Y + (field.Height - lineHeight) * 0.5f;
        var textX = field.X + 6f;
        var room = field.Width - 12f;
        if (!editing)
        {
            _ui.DrawString(textX, textY, Shorten(text, room), muted || !view.Editable ? Muted : Text, SmallTextScale);
            return;
        }

        // Show the end of a long typed text (the caret is always at the end).
        var shown = view.EditText;
        while (shown.Length > 0 && _ui.MeasureWidth(shown, SmallTextScale) > room)
        {
            shown = shown[1..];
        }

        var shownWidth = _ui.MeasureWidth(shown, SmallTextScale);
        if (view.EditSelectAll && shown.Length > 0)
        {
            _ui.DrawRect(textX - 1f, field.Y + 3f, shownWidth + 2f, field.Height - 6f, CheckedFill);
        }

        _ui.DrawString(textX, textY, shown, TitleText, SmallTextScale);
        if (caretVisible && !view.EditSelectAll)
        {
            _ui.DrawRect(textX + shownWidth + 1f, field.Y + 4f, 1.5f, field.Height - 8f, TitleText);
        }
    }

    private void DrawRowLabel(UiBox row, string label, bool editable)
    {
        var textY = row.Y + (row.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f;
        _ui.DrawString(row.X + SidePadding * 0.5f, textY, label, editable ? Text : Muted, SmallTextScale);
    }

    private void DrawCentered(UiBox box, string text, Vector3 color)
    {
        var width = _ui.MeasureWidth(text, SmallTextScale);
        _ui.DrawString(box.X + (box.Width - width) * 0.5f, box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f, text, color, SmallTextScale);
    }

    // A long object name is cut with an ellipsis rather than running under the panel edge.
    private string Shorten(string text, float width)
    {
        if (_ui.MeasureWidth(text, SmallTextScale) <= width)
        {
            return text;
        }

        var length = text.Length;
        while (length > 0 && _ui.MeasureWidth(text[..length] + "…", SmallTextScale) > width)
        {
            length--;
        }

        return text[..length] + "…";
    }
}
