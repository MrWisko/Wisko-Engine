using System.Globalization;
using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>A clip as the Animation panel lists it.</summary>
internal readonly record struct AnimationPanelClip(string Name, float Duration);

/// <summary>
/// What the Animation panel shows, read every frame from the selected model, its saved settings and the preview (or, in
/// Play, the running playback). <see cref="Clips"/> is empty when there is nothing to animate; <see cref="Message"/>
/// then says why. <see cref="Selected"/> is the clip the list highlights (the one previewed); <see cref="PlayOnStart"/>
/// the saved Play on start clip (-1: none).
/// </summary>
internal readonly record struct AnimationPanelView(
    string Selection,
    string Message,
    IReadOnlyList<AnimationPanelClip> Clips,
    int Selected,
    int PlayOnStart,
    int ScrollTop,
    bool Previewing,
    bool Paused,
    double Time,
    float Duration,
    bool Loop,
    float Speed,
    bool Editable,
    string Status);

/// <summary>
/// The Animation panel: a column docked on the left (beside Physics when both are open), drawn like Physics. For one
/// selected imported model it lists the clips (name and length), previews the chosen clip in the editor (Play, Pause,
/// Stop and a timeline to scrub) and holds the instance's saved settings: Loop, Speed and Play on start (the chosen clip
/// plays by itself when Play starts). The preview is editor-only playback: it is never saved and never an undo step;
/// Stop (or Play, or another selection) puts the model back in its rest pose. In Play the panel shows the running
/// animation, read-only.
/// </summary>
internal sealed class AnimationPanel : IDisposable
{
    public const float Width = WorldExplorerRenderer.SidebarWidth;

    /// <summary>Clip rows shown at once; a longer list scrolls with the wheel.</summary>
    public const int MaxVisibleRows = 8;

    private const float TitleHeight = 34f;
    private const float SidePadding = 10f;
    private const float TextScale = 1.85f;
    private const float SmallTextScale = 1.65f;
    private const float CloseSize = 18f;
    private const float SelectionY = TitleHeight + 10f;
    private const float MessageY = TitleHeight + 30f;
    private const float ClipsHeaderY = TitleHeight + 56f;
    private const float ListY = TitleHeight + 76f;
    private const float ClipRowHeight = 22f;
    private const float RowHeight = 26f;
    private const float CheckboxSize = 16f;
    private const float ButtonHeight = 24f;
    private const float Gap = 6f;

    private static readonly Vector3 Fill = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 Separator = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 TitleText = new(0.92f, 0.94f, 0.98f);
    private static readonly Vector3 Text = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 SectionText = new(0.70f, 0.74f, 0.80f);
    private static readonly Vector3 Muted = new(0.58f, 0.62f, 0.68f);
    private static readonly Vector3 CloseHover = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 RowHover = new(0.18f, 0.19f, 0.22f);
    private static readonly Vector3 RowSelected = new(0.20f, 0.30f, 0.48f);
    private static readonly Vector3 BoxBorder = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 CheckedFill = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 CheckedBorder = new(0.55f, 0.75f, 1.0f);
    private static readonly Vector3 DisabledBorder = new(0.33f, 0.35f, 0.40f);
    private static readonly Vector3 DisabledFill = new(0.24f, 0.26f, 0.30f);
    private static readonly Vector3 DisabledMark = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 FieldBorder = new(0.26f, 0.28f, 0.32f);
    private static readonly Vector3 ButtonFill = new(0.20f, 0.21f, 0.25f);
    private static readonly Vector3 ButtonHover = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 Track = new(0.26f, 0.28f, 0.32f);
    private static readonly Vector3 Accent = new(0.36f, 0.78f, 0.47f);

    private IUiRenderer _ui = null!;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public static UiBox CloseButton(UiBox panel) =>
        new(panel.X + panel.Width - SidePadding - CloseSize, panel.Y + (TitleHeight - CloseSize) * 0.5f, CloseSize, CloseSize);

    /// <summary>Rows the clip list shows for <paramref name="clipCount"/> clips.</summary>
    public static int VisibleRows(int clipCount) => global::System.Math.Min(clipCount, MaxVisibleRows);

    /// <summary>The clip list (its visible rows).</summary>
    public static UiBox ClipList(UiBox panel, int clipCount) =>
        new(panel.X + SidePadding * 0.5f, panel.Y + ListY, MathF.Max(0f, panel.Width - SidePadding), VisibleRows(clipCount) * ClipRowHeight);

    /// <summary>The visible row <paramref name="row"/> (0 = the top one shown).</summary>
    public static UiBox ClipRow(UiBox panel, int clipCount, int row)
    {
        var list = ClipList(panel, clipCount);
        return new UiBox(list.X, list.Y + row * ClipRowHeight, list.Width, ClipRowHeight);
    }

    /// <summary>The clip index under a point of the list, or -1.</summary>
    public static int ClipAt(UiBox panel, int clipCount, int scrollTop, float x, float y)
    {
        var list = ClipList(panel, clipCount);
        if (!list.Contains(x, y))
        {
            return -1;
        }

        var index = scrollTop + (int)((y - list.Y) / ClipRowHeight);
        return index >= 0 && index < clipCount ? index : -1;
    }

    private static float PreviewHeaderY(UiBox panel, int clipCount) => ClipList(panel, clipCount).Y + ClipList(panel, clipCount).Height + 12f;

    private static float TransportY(UiBox panel, int clipCount) => PreviewHeaderY(panel, clipCount) + 22f;

    /// <summary>Play (0), Pause (1) and Stop (2) of the preview.</summary>
    public static UiBox TransportButton(UiBox panel, int clipCount, int index)
    {
        var x = panel.X + SidePadding;
        var width = (panel.Width - SidePadding * 2f - Gap * 2f) / 3f;
        return new UiBox(x + index * (width + Gap), TransportY(panel, clipCount), width, ButtonHeight);
    }

    /// <summary>The timeline track (0 s at the left, the clip's end at the right).</summary>
    public static UiBox Timeline(UiBox panel, int clipCount) =>
        new(panel.X + SidePadding + 6f, TransportY(panel, clipCount) + ButtonHeight + 8f, MathF.Max(24f, panel.Width - SidePadding * 2f - 12f), 22f);

    /// <summary>Where a press starts scrubbing: the track and a little either side of it.</summary>
    public static UiBox Scrubber(UiBox panel, int clipCount)
    {
        var track = Timeline(panel, clipCount);
        return new UiBox(track.X - 6f, track.Y, track.Width + 12f, track.Height);
    }

    /// <summary>The time for a mouse x along the timeline (clamped to the clip).</summary>
    public static double TimeAt(UiBox panel, int clipCount, float x, float duration)
    {
        var track = Timeline(panel, clipCount);
        var fraction = global::System.Math.Clamp((x - track.X) / MathF.Max(track.Width, 1f), 0f, 1f);
        return fraction * duration;
    }

    private static float SettingsHeaderY(UiBox panel, int clipCount) => Timeline(panel, clipCount).Y + Timeline(panel, clipCount).Height + 30f;

    private static UiBox SettingRow(UiBox panel, int clipCount, int index) =>
        new(panel.X + SidePadding * 0.5f, SettingsHeaderY(panel, clipCount) + 20f + index * RowHeight, MathF.Max(0f, panel.Width - SidePadding), RowHeight);

    /// <summary>The Loop row (saved setting): clicking it toggles Loop.</summary>
    public static UiBox LoopRow(UiBox panel, int clipCount) => SettingRow(panel, clipCount, 0);

    public static UiBox SpeedRow(UiBox panel, int clipCount) => SettingRow(panel, clipCount, 1);

    /// <summary>The Play on start row (saved setting): the chosen clip plays by itself when Play starts.</summary>
    public static UiBox PlayOnStartRow(UiBox panel, int clipCount) => SettingRow(panel, clipCount, 2);

    /// <summary>The − (0) and + (1) speed buttons, and the value between them (2: click resets to 1x).</summary>
    public static UiBox SpeedControl(UiBox panel, int clipCount, int index)
    {
        var row = SpeedRow(panel, clipCount);
        const float button = 24f;
        const float value = 58f;
        var right = row.X + row.Width - SidePadding * 0.5f;
        var y = row.Y + (RowHeight - button) * 0.5f;
        return index switch
        {
            0 => new UiBox(right - button * 2f - value - Gap * 2f, y, button, button),
            1 => new UiBox(right - button, y, button, button),
            _ => new UiBox(right - button - value - Gap, y, value, button)
        };
    }

    public static string FormatSeconds(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    public static string FormatSpeed(float speed) => speed.ToString("0.00", CultureInfo.InvariantCulture) + "x";

    public void Render(UiBox panel, AnimationPanelView view, float mouseX, float mouseY, int screenWidth, int screenHeight, float dpiScale)
    {
        _ui.BeginBatch(dpiScale);
        _ui.SetClip(new UiClip(panel.X, panel.Y, panel.Width, panel.Height));
        _ui.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, Fill);
        _ui.DrawRect(panel.X + panel.Width - 1f, panel.Y, 1f, panel.Height, Separator);

        _ui.DrawString(panel.X + SidePadding, panel.Y + 10f, "Animation", TitleText, TextScale, FontWeight.SemiBold);
        var close = CloseButton(panel);
        var overClose = close.Contains(mouseX, mouseY);
        if (overClose)
        {
            _ui.DrawRect(close.X, close.Y, close.Width, close.Height, CloseHover);
        }

        UiIcons.DrawCross(_ui, close.X + close.Width * 0.5f, close.Y + close.Height * 0.5f, 8f, overClose ? TitleText : Text);
        _ui.DrawRect(panel.X + SidePadding, panel.Y + TitleHeight, panel.Width - SidePadding * 2f, 1f, Separator);

        var textWidth = MathF.Max(0f, panel.Width - SidePadding * 2f);
        _ui.DrawString(panel.X + SidePadding, panel.Y + SelectionY, Shorten(view.Selection, textWidth), view.Clips.Count == 0 ? Muted : Text, SmallTextScale);
        _ui.DrawString(panel.X + SidePadding, panel.Y + MessageY, Shorten(view.Message, textWidth), Muted, SmallTextScale);
        if (view.Clips.Count > 0)
        {
            DrawClipList(panel, view, mouseX, mouseY);
            DrawPreview(panel, view, mouseX, mouseY);
            DrawSettings(panel, view, mouseX, mouseY);
        }

        _ui.SetClip(null);
        _ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    private void DrawClipList(UiBox panel, AnimationPanelView view, float mouseX, float mouseY)
    {
        var count = view.Clips.Count;
        _ui.DrawString(panel.X + SidePadding, panel.Y + ClipsHeaderY,
            count > MaxVisibleRows ? $"Clips ({count}, scroll for more)" : $"Clips ({count})", SectionText, SmallTextScale, FontWeight.SemiBold);
        for (var row = 0; row < VisibleRows(count); row++)
        {
            var index = view.ScrollTop + row;
            if (index >= count)
            {
                break;
            }

            var box = ClipRow(panel, count, row);
            if (index == view.Selected)
            {
                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, RowSelected);
            }
            else if (box.Contains(mouseX, mouseY))
            {
                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, RowHover);
            }

            var clip = view.Clips[index];
            var lineY = box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f;
            var duration = FormatSeconds(clip.Duration);
            var durationWidth = _ui.MeasureWidth(duration, SmallTextScale);
            var nameX = box.X + 18f;

            // The saved Play on start clip is marked with a small green dot.
            if (index == view.PlayOnStart)
            {
                UiIcons.FillCircle(_ui, box.X + 9f, box.Y + box.Height * 0.5f, 3.5f, Accent);
            }

            _ui.DrawString(nameX, lineY, Shorten(clip.Name, box.Width - 30f - durationWidth), index == view.Selected ? TitleText : Text, SmallTextScale);
            _ui.DrawString(box.X + box.Width - SidePadding * 0.5f - durationWidth, lineY, duration, Muted, SmallTextScale);
        }
    }

    private void DrawPreview(UiBox panel, AnimationPanelView view, float mouseX, float mouseY)
    {
        var count = view.Clips.Count;
        _ui.DrawString(panel.X + SidePadding, PreviewHeaderY(panel, count), view.Editable ? "Preview" : "Playing", SectionText, SmallTextScale, FontWeight.SemiBold);
        var playing = view.Previewing && !view.Paused;
        DrawButton(TransportButton(panel, count, 0), "Play", playing, view.Editable, mouseX, mouseY);
        DrawButton(TransportButton(panel, count, 1), "Pause", view.Previewing && view.Paused, view.Editable && view.Previewing, mouseX, mouseY);
        DrawButton(TransportButton(panel, count, 2), "Stop", false, view.Editable && view.Previewing, mouseX, mouseY);

        var track = Timeline(panel, count);
        var y = track.Y + track.Height * 0.5f - 2f;
        _ui.DrawRect(track.X, y, track.Width, 4f, Track);
        var fraction = view.Duration > 0f ? (float)global::System.Math.Clamp(view.Time / view.Duration, 0d, 1d) : 0f;
        _ui.DrawRect(track.X, y, track.Width * fraction, 4f, view.Editable ? CheckedFill : Muted);
        var knob = track.X + track.Width * fraction;
        _ui.DrawRect(knob - 4f, track.Y + track.Height * 0.5f - 7f, 8f, 14f, view.Editable ? BoxBorder : DisabledBorder);
        _ui.DrawRect(knob - 3f, track.Y + track.Height * 0.5f - 6f, 6f, 12f, view.Editable ? TitleText : DisabledMark);
        _ui.DrawString(track.X, track.Y + track.Height + 2f, $"{FormatSeconds(view.Time)} / {FormatSeconds(view.Duration)}   {view.Status}", Muted, SmallTextScale);
    }

    private void DrawSettings(UiBox panel, AnimationPanelView view, float mouseX, float mouseY)
    {
        var count = view.Clips.Count;
        _ui.DrawString(panel.X + SidePadding, SettingsHeaderY(panel, count), "Saved settings", SectionText, SmallTextScale, FontWeight.SemiBold);
        DrawCheckRow(LoopRow(panel, count), "Loop", view.Loop, view.Editable, mouseX, mouseY);

        var speedRow = SpeedRow(panel, count);
        DrawRowLabel(speedRow, "Speed", view.Editable);
        DrawButton(SpeedControl(panel, count, 0), "-", false, view.Editable, mouseX, mouseY);
        DrawButton(SpeedControl(panel, count, 1), "+", false, view.Editable, mouseX, mouseY);
        var value = SpeedControl(panel, count, 2);
        DrawCentered(value, FormatSpeed(view.Speed), view.Editable ? Text : Muted);

        DrawCheckRow(PlayOnStartRow(panel, count), "Play on start", view.Selected >= 0 && view.Selected == view.PlayOnStart, view.Editable, mouseX, mouseY);
    }

    private void DrawButton(UiBox box, string label, bool active, bool enabled, float mouseX, float mouseY)
    {
        var hover = enabled && box.Contains(mouseX, mouseY);
        if (active)
        {
            _ui.DrawRect(box.X, box.Y, box.Width, box.Height, enabled ? CheckedFill : DisabledFill);
            _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, enabled ? CheckedBorder : DisabledBorder, 1f);
        }
        else
        {
            _ui.DrawRect(box.X, box.Y, box.Width, box.Height, hover ? ButtonHover : ButtonFill);
            _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, enabled ? FieldBorder : DisabledBorder, 1f);
        }

        DrawCentered(box, label, active && enabled ? TitleText : enabled ? Text : Muted);
    }

    private void DrawCheckRow(UiBox row, string label, bool on, bool editable, float mouseX, float mouseY)
    {
        if (editable && row.Contains(mouseX, mouseY))
        {
            _ui.DrawRect(row.X, row.Y, row.Width, row.Height, RowHover);
        }

        DrawRowLabel(row, label, editable);
        var box = new UiBox(row.X + row.Width - SidePadding * 0.5f - CheckboxSize, row.Y + (row.Height - CheckboxSize) * 0.5f, CheckboxSize, CheckboxSize);
        if (on)
        {
            _ui.DrawRect(box.X, box.Y, box.Width, box.Height, editable ? CheckedFill : DisabledFill);
            _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, editable ? CheckedBorder : DisabledBorder, 1f);
            UiIcons.DrawCheck(_ui, box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f, 10f, editable ? TitleText : DisabledMark);
        }
        else
        {
            _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, editable ? BoxBorder : DisabledBorder, 1.5f);
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
