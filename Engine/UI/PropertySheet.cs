using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>What a row of a <see cref="PropertySheet"/> shows.</summary>
internal enum SheetRowKind
{
    Section,
    Note,
    Slider,

    /// <summary>A colour swatch; clicked, it opens Hue / Saturation / Brightness rows under it.</summary>
    Color,
    Hue,
    Saturation,
    Brightness,
    Toggle,

    /// <summary>One of <see cref="SheetRow.Options"/>: segments when they fit, else the value between arrows that step through them.</summary>
    Choice,

    /// <summary>A texture file (or none) with Browse (…) and Clear (×).</summary>
    Texture,

    /// <summary>One or more buttons side by side (<see cref="SheetRow.Options"/> are their labels).</summary>
    Buttons
}

/// <summary>
/// One row. <see cref="Key"/> names what it edits (the panel's owner maps it to a value); <see cref="Value"/> is the
/// text shown at the right (a slider's number, a texture's file); <see cref="Fraction"/> a slider's position (0-1).
/// </summary>
internal sealed record SheetRow(SheetRowKind Kind, string Key, string Label)
{
    public string Value { get; init; } = string.Empty;

    public float Fraction { get; init; }

    public bool On { get; init; }

    public Vector3 Color { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public int Selected { get; init; } = -1;

    /// <summary>Drawn muted and not clickable (e.g. a value that has no effect in the current mode).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Drawn muted but still editable (an Atmosphere value while the Atmosphere is off: it is kept for later).</summary>
    public bool Muted { get; init; }
}

/// <summary>Which part of a row a click hit.</summary>
internal enum SheetHitPart
{
    Slider,
    Toggle,
    Option,
    Previous,
    Next,
    Color,
    Browse,
    Clear,
    Button,
    Close,

    /// <summary>A tab under the title (<see cref="SheetHit.Index"/> is the tab).</summary>
    Tab
}

/// <summary>A click on the sheet: the row, the part, a slider position (0-1) or an option / button index.</summary>
internal readonly record struct SheetHit(SheetRow? Row, SheetHitPart Part, float Fraction = 0f, int Index = -1);

/// <summary>
/// A scrolling column of property rows (sections, notes, sliders, colours with H/S/B, switches, choices, texture
/// files, buttons) under a title with a close button and, optionally, a row of tabs (wrapped onto a second line when
/// they do not fit). Drawing and hit-testing only: the owner builds the rows from its data every frame and applies what
/// a hit means (and the undo grouping).
/// </summary>
internal sealed class PropertySheet : IDisposable
{
    public const float MaxWidth = 300f;
    public const float MinWidth = 220f;
    private const float TitleHeight = 40f;
    private const float TabHeight = 24f;
    private const float TabGap = 4f;
    private const float TabPadding = 8f;
    private const float RowHeight = 26f;
    private const float SectionHeight = 28f;
    private const float ButtonsHeight = 34f;
    private const float Padding = 12f;
    private const float LabelWidth = 108f;
    private const float ValueWidth = 54f;
    private const float MinTrackWidth = 24f;
    private const float TextScale = 1.6f;
    private const float SectionScale = 1.5f;
    private const float CloseSize = 18f;
    private const float SmallButton = 22f;

    private static readonly Vector3 Fill = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 Border = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 Title = new(0.95f, 0.96f, 0.98f);
    private static readonly Vector3 Text = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 Muted = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 Track = new(0.24f, 0.26f, 0.30f);
    private static readonly Vector3 Accent = new(0.32f, 0.56f, 0.95f);
    private static readonly Vector3 Knob = new(0.92f, 0.94f, 0.97f);
    private static readonly Vector3 Hover = new(0.16f, 0.17f, 0.20f);
    private static readonly Vector3 SwitchOff = new(0.30f, 0.32f, 0.36f);
    private static readonly Vector3 ButtonFill = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 ButtonHover = new(0.28f, 0.30f, 0.35f);
    private static readonly Vector3 SegmentOn = new(0.20f, 0.42f, 0.78f);

    private IUiRenderer _ui = null!;
    private float _scroll;
    private IReadOnlyList<string> _tabs = [];
    private int _selectedTab;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public void ResetScroll() => _scroll = 0f;

    /// <summary>The tabs under the title (none: a plain sheet) and the one shown as chosen.</summary>
    public void SetTabs(IReadOnlyList<string> tabs, int selected)
    {
        _tabs = tabs;
        _selectedTab = selected;
    }

    /// <summary>Scrolls just enough that row <paramref name="index"/> is in view.</summary>
    internal void Reveal(UiBox panel, IReadOnlyList<SheetRow> rows, int index)
    {
        var top = 4f + rows.Take(index).Sum(HeightOf);
        var bottom = top + HeightOf(rows[index]);
        var body = panel.Height - HeaderHeight(panel);
        _scroll = top < _scroll ? top : bottom > _scroll + body ? bottom - body + 4f : _scroll;
        _scroll = global::System.Math.Clamp(_scroll, 0f, MaxScroll(panel, rows));
    }

    public void Scroll(float delta, UiBox panel, IReadOnlyList<SheetRow> rows) =>
        _scroll = global::System.Math.Clamp(_scroll + delta, 0f, MaxScroll(panel, rows));

    /// <summary>The × at the right of the title.</summary>
    public static UiBox CloseButton(UiBox panel) =>
        new(panel.X + panel.Width - Padding - CloseSize, panel.Y + (TitleHeight - CloseSize) * 0.5f, CloseSize, CloseSize);

    /// <summary>
    /// Where each tab is: as many side by side as fit their names, each line stretched to the panel's width (a narrow
    /// panel puts the rest on another line).
    /// </summary>
    internal UiBox[] TabBoxes(UiBox panel)
    {
        var boxes = new UiBox[_tabs.Count];
        var width = panel.Width - Padding * 2f;
        var y = panel.Y + TitleHeight;
        for (var start = 0; start < _tabs.Count;)
        {
            var end = start;
            var used = 0f;
            while (end < _tabs.Count)
            {
                var natural = TabNaturalWidth(end);
                if (end > start && used + TabGap + natural > width)
                {
                    break;
                }

                used += (end > start ? TabGap : 0f) + natural;
                end++;
            }

            var extra = (width - used) / (end - start);
            var x = panel.X + Padding;
            for (var i = start; i < end; i++)
            {
                var tabWidth = MathF.Max(1f, TabNaturalWidth(i) + extra);
                boxes[i] = new UiBox(x, y, tabWidth, TabHeight);
                x += tabWidth + TabGap;
            }

            y += TabHeight + TabGap;
            start = end;
        }

        return boxes;
    }

    // The title, then the tab lines (if any) and a small gap above the rows.
    private float HeaderHeight(UiBox panel)
    {
        if (_tabs.Count == 0)
        {
            return TitleHeight;
        }

        var boxes = TabBoxes(panel);
        return boxes[^1].Y + TabHeight + 8f - panel.Y;
    }

    private float TabNaturalWidth(int index) => _ui.MeasureWidth(_tabs[index], TextScale) + TabPadding * 2f;

    /// <summary>A slider position (0-1) for a mouse x: dragging continues past the ends, clamped.</summary>
    public static float FractionAt(UiBox panel, float x)
    {
        var track = TrackBox(new UiBox(panel.X, 0f, panel.Width - 6f, RowHeight));
        return global::System.Math.Clamp((x - track.X) / MathF.Max(track.Width, 1f), 0f, 1f);
    }

    public SheetHit? HitTest(UiBox panel, IReadOnlyList<SheetRow> rows, float x, float y)
    {
        if (!panel.Contains(x, y))
        {
            return null;
        }

        if (y < panel.Y + HeaderHeight(panel))
        {
            if (CloseButton(panel).Contains(x, y))
            {
                return new SheetHit(null, SheetHitPart.Close);
            }

            var tabs = TabBoxes(panel);
            for (var i = 0; i < tabs.Length; i++)
            {
                if (tabs[i].Contains(x, y))
                {
                    return new SheetHit(null, SheetHitPart.Tab, Index: i);
                }
            }

            return null;
        }

        foreach (var (row, box) in Layout(panel, rows))
        {
            if (!box.Contains(x, y))
            {
                continue;
            }

            if (!row.Enabled)
            {
                return null;
            }

            var track = TrackBox(box);
            var fraction = global::System.Math.Clamp((x - track.X) / MathF.Max(track.Width, 1f), 0f, 1f);
            switch (row.Kind)
            {
                case SheetRowKind.Slider or SheetRowKind.Hue or SheetRowKind.Saturation or SheetRowKind.Brightness when x >= track.X - 6f:
                    return new SheetHit(row, SheetHitPart.Slider, fraction);
                case SheetRowKind.Color:
                    return new SheetHit(row, SheetHitPart.Color);
                case SheetRowKind.Toggle:
                    return new SheetHit(row, SheetHitPart.Toggle);
                case SheetRowKind.Choice when x >= track.X:
                    if (Segmented(row, ChoiceBox(box)))
                    {
                        var choice = ChoiceBox(box);
                        var index = (int)((x - choice.X) / MathF.Max(1f, choice.Width) * row.Options.Count);
                        return new SheetHit(row, SheetHitPart.Option, Index: global::System.Math.Clamp(index, 0, row.Options.Count - 1));
                    }

                    return new SheetHit(row, x < ChoiceBox(box).X + ChoiceBox(box).Width * 0.5f ? SheetHitPart.Previous : SheetHitPart.Next);
                case SheetRowKind.Texture:
                    if (row.Value.Length > 0 && ClearBox(box).Contains(x, y))
                    {
                        return new SheetHit(row, SheetHitPart.Clear);
                    }

                    return x >= track.X ? new SheetHit(row, SheetHitPart.Browse) : null;
                case SheetRowKind.Buttons:
                    for (var i = 0; i < row.Options.Count; i++)
                    {
                        if (ButtonBox(box, i, row.Options.Count).Contains(x, y))
                        {
                            return new SheetHit(row, SheetHitPart.Button, Index: i);
                        }
                    }

                    return null;
            }

            return null;
        }

        return null;
    }

    public void Render(UiBox panel, string title, IReadOnlyList<SheetRow> rows, AtmosphereHsv hsv, string? expandedColor, bool readOnly,
        float mouseX, float mouseY, int screenWidth, int screenHeight, float dpiScale)
    {
        _scroll = global::System.Math.Clamp(_scroll, 0f, MaxScroll(panel, rows));
        _ui.BeginBatch(dpiScale);
        _ui.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, Fill);
        _ui.DrawRect(panel.X + panel.Width - 1f, panel.Y, 1f, panel.Height, Border);

        var titleY = panel.Y + (TitleHeight - _ui.MeasureLineHeight(1.9f, FontWeight.SemiBold)) * 0.5f;
        _ui.DrawString(panel.X + Padding, titleY, UiText.Fit(_ui, title, panel.Width - Padding * 3f - CloseSize, 1.9f), Title, 1.9f, FontWeight.SemiBold);
        var close = CloseButton(panel);
        var overClose = close.Contains(mouseX, mouseY);
        if (overClose)
        {
            _ui.DrawRect(close.X, close.Y, close.Width, close.Height, ButtonHover);
        }

        UiIcons.DrawCross(_ui, close.X + close.Width * 0.5f, close.Y + close.Height * 0.5f, 8f, overClose ? Title : Text);
        var tabs = TabBoxes(panel);
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            var chosen = i == _selectedTab;
            _ui.DrawRect(tab.X, tab.Y, tab.Width, tab.Height, chosen ? SegmentOn : tab.Contains(mouseX, mouseY) ? ButtonHover : ButtonFill);
            var label = UiText.Fit(_ui, _tabs[i], tab.Width - 4f, TextScale);
            _ui.DrawString(tab.X + (tab.Width - _ui.MeasureWidth(label, TextScale)) * 0.5f, tab.Y + (tab.Height - _ui.MeasureLineHeight(TextScale)) * 0.5f,
                label, chosen ? Title : Text, TextScale, chosen ? FontWeight.SemiBold : FontWeight.Regular);
        }

        var header = HeaderHeight(panel);
        _ui.DrawRect(panel.X + Padding, panel.Y + header - 1f, panel.Width - Padding * 2f, 1f, Border);

        var body = new UiBox(panel.X, panel.Y + header, panel.Width, MathF.Max(0f, panel.Height - header));
        _ui.SetClip(new UiClip(body.X, body.Y, body.Width, body.Height));
        foreach (var (row, box) in Layout(panel, rows))
        {
            if (box.Y + box.Height < body.Y || box.Y > body.Y + body.Height)
            {
                continue;
            }

            DrawRow(row, box, hsv, expandedColor, readOnly || !row.Enabled || row.Muted, !readOnly && row.Enabled,
                box.Contains(mouseX, mouseY) && body.Contains(mouseX, mouseY), mouseX, mouseY);
        }

        _ui.SetClip(null);
        DrawScrollbar(body, rows);
        _ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    // dim: drawn muted; interactive: clickable (a muted row can still be, e.g. an Atmosphere value while it is off).
    private void DrawRow(SheetRow row, UiBox box, AtmosphereHsv hsv, string? expandedColor, bool dim, bool interactive, bool hovered, float mouseX, float mouseY)
    {
        var textColor = dim ? Muted : Text;
        var textY = box.Y + (box.Height - _ui.MeasureLineHeight(TextScale)) * 0.5f;
        switch (row.Kind)
        {
            case SheetRowKind.Section:
                _ui.DrawString(box.X + Padding, box.Y + box.Height - _ui.MeasureLineHeight(SectionScale, FontWeight.SemiBold) - 4f, row.Label, Muted,
                    SectionScale, FontWeight.SemiBold);
                return;
            case SheetRowKind.Note:
                _ui.DrawString(box.X + Padding, textY, UiText.Fit(_ui, row.Label, box.Width - Padding * 2f, TextScale), Muted, TextScale);
                return;
            case SheetRowKind.Buttons:
                for (var i = 0; i < row.Options.Count; i++)
                {
                    var button = ButtonBox(box, i, row.Options.Count);
                    var over = interactive && button.Contains(mouseX, mouseY);
                    _ui.DrawRect(button.X, button.Y, button.Width, button.Height, over ? ButtonHover : ButtonFill);
                    var label = UiText.Fit(_ui, row.Options[i], button.Width - 8f, TextScale);
                    var y = button.Y + (button.Height - _ui.MeasureLineHeight(TextScale)) * 0.5f;
                    _ui.DrawString(button.X + (button.Width - _ui.MeasureWidth(label, TextScale)) * 0.5f, y, label, textColor, TextScale);
                }

                return;
        }

        if (hovered && interactive)
        {
            _ui.DrawRect(box.X, box.Y, box.Width, box.Height, Hover);
        }

        var indent = row.Kind is SheetRowKind.Hue or SheetRowKind.Saturation or SheetRowKind.Brightness ? 12f : 0f;
        _ui.DrawString(box.X + Padding + indent, textY, UiText.Fit(_ui, row.Label, LabelColumn(box) - 4f - indent, TextScale), textColor, TextScale);
        var track = TrackBox(box);
        switch (row.Kind)
        {
            case SheetRowKind.Slider:
                DrawSlider(track, row.Fraction, dim);
                _ui.DrawString(box.X + box.Width - Padding - _ui.MeasureWidth(row.Value, TextScale), textY, row.Value, textColor, TextScale);
                break;

            case SheetRowKind.Color:
            {
                var swatch = new UiBox(track.X, box.Y + 5f, 36f, box.Height - 10f);
                _ui.DrawRect(swatch.X - 1f, swatch.Y - 1f, swatch.Width + 2f, swatch.Height + 2f, expandedColor == row.Key ? Accent : Border);
                _ui.DrawRect(swatch.X, swatch.Y, swatch.Width, swatch.Height, row.Color);
                _ui.DrawString(swatch.X + swatch.Width + 10f, textY, row.Value, textColor, TextScale);
                break;
            }

            case SheetRowKind.Hue:
            case SheetRowKind.Saturation:
            case SheetRowKind.Brightness:
                DrawGradient(track, row.Kind, hsv);
                DrawKnob(track, row.Kind switch { SheetRowKind.Hue => hsv.H, SheetRowKind.Saturation => hsv.S, _ => hsv.V });
                break;

            case SheetRowKind.Toggle:
                DrawSwitch(new UiBox(track.X, box.Y + (box.Height - 16f) * 0.5f, 32f, 16f), row.On, dim);
                if (row.Value.Length > 0)
                {
                    _ui.DrawString(track.X + 42f, textY, UiText.Fit(_ui, row.Value, box.Width - Padding - track.X + box.X - 42f, TextScale), Muted, TextScale);
                }

                break;

            case SheetRowKind.Choice:
                DrawChoice(row, ChoiceBox(box), dim, interactive, mouseX, mouseY);
                break;

            case SheetRowKind.Texture:
            {
                var clear = ClearBox(box);
                var right = row.Value.Length > 0 ? clear.X - 4f : box.X + box.Width - Padding;
                var browse = new UiBox(track.X, box.Y + 3f, MathF.Max(24f, right - track.X), box.Height - 6f);
                _ui.DrawRect(browse.X, browse.Y, browse.Width, browse.Height, interactive && browse.Contains(mouseX, mouseY) ? ButtonHover : ButtonFill);
                var text = row.Value.Length > 0 ? row.Value : "Choose…";
                _ui.DrawString(browse.X + 6f, textY, UiText.Fit(_ui, text, browse.Width - 12f, TextScale), row.Value.Length > 0 ? textColor : Muted, TextScale);
                if (row.Value.Length > 0)
                {
                    var over = interactive && clear.Contains(mouseX, mouseY);
                    _ui.DrawRect(clear.X, clear.Y, clear.Width, clear.Height, over ? ButtonHover : ButtonFill);
                    UiIcons.DrawCross(_ui, clear.X + clear.Width * 0.5f, clear.Y + clear.Height * 0.5f, 7f, over ? Title : textColor);
                }

                break;
            }
        }
    }

    private void DrawChoice(SheetRow row, UiBox choice, bool dim, bool interactive, float mouseX, float mouseY)
    {
        var textY = choice.Y + (choice.Height - _ui.MeasureLineHeight(TextScale)) * 0.5f;
        if (Segmented(row, choice))
        {
            var width = choice.Width / row.Options.Count;
            for (var i = 0; i < row.Options.Count; i++)
            {
                var segment = new UiBox(choice.X + i * width, choice.Y, width - 2f, choice.Height);
                var selected = i == row.Selected;
                var over = interactive && segment.Contains(mouseX, mouseY);
                _ui.DrawRect(segment.X, segment.Y, segment.Width, segment.Height, selected ? (dim ? Muted : SegmentOn) : over ? ButtonHover : ButtonFill);
                var label = UiText.Fit(_ui, row.Options[i], segment.Width - 4f, TextScale);
                _ui.DrawString(segment.X + (segment.Width - _ui.MeasureWidth(label, TextScale)) * 0.5f, textY, label, selected ? Title : dim ? Muted : Text, TextScale);
            }

            return;
        }

        _ui.DrawRect(choice.X, choice.Y, choice.Width, choice.Height, interactive && choice.Contains(mouseX, mouseY) ? ButtonHover : ButtonFill);
        var value = row.Selected >= 0 && row.Selected < row.Options.Count ? row.Options[row.Selected] : row.Value;
        var fitted = UiText.Fit(_ui, value, choice.Width - 40f, TextScale);
        _ui.DrawString(choice.X + (choice.Width - _ui.MeasureWidth(fitted, TextScale)) * 0.5f, textY, fitted, dim ? Muted : Text, TextScale);
        var cy = choice.Y + choice.Height * 0.5f;
        UiIcons.DrawCaretLeft(_ui, choice.X + 10f, cy, 9f, dim ? Muted : Text);
        UiIcons.DrawCaretRight(_ui, choice.X + choice.Width - 10f, cy, 9f, dim ? Muted : Text);
    }

    // Segments when every option fits its share of the width (four at most), else the value between arrows.
    internal bool Segmented(SheetRow row, UiBox choice)
    {
        if (row.Options.Count is 0 or > 4)
        {
            return false;
        }

        var width = choice.Width / row.Options.Count - 6f;
        return row.Options.All(option => _ui.MeasureWidth(option, TextScale) <= width);
    }

    private void DrawSlider(UiBox track, float fraction, bool dim)
    {
        fraction = global::System.Math.Clamp(fraction, 0f, 1f);
        var y = track.Y + track.Height * 0.5f - 2f;
        _ui.DrawRect(track.X, y, track.Width, 4f, Track);
        _ui.DrawRect(track.X, y, track.Width * fraction, 4f, dim ? Muted : Accent);
        DrawKnob(track, fraction);
    }

    private void DrawKnob(UiBox track, float fraction)
    {
        var x = track.X + track.Width * global::System.Math.Clamp(fraction, 0f, 1f);
        _ui.DrawRect(x - 4f, track.Y + track.Height * 0.5f - 7f, 8f, 14f, Border);
        _ui.DrawRect(x - 3f, track.Y + track.Height * 0.5f - 6f, 6f, 12f, Knob);
    }

    private void DrawGradient(UiBox track, SheetRowKind kind, AtmosphereHsv hsv)
    {
        const int strips = 24;
        var y = track.Y + track.Height * 0.5f - 4f;
        var width = track.Width / strips;
        for (var i = 0; i < strips; i++)
        {
            var t = (i + 0.5f) / strips;
            var (r, g, b) = kind switch
            {
                SheetRowKind.Hue => AtmosphereHsv.Rgb(t, 1f, 1f),
                SheetRowKind.Saturation => AtmosphereHsv.Rgb(hsv.H, t, MathF.Max(hsv.V, 0.35f)),
                _ => AtmosphereHsv.Rgb(hsv.H, hsv.S, t)
            };
            _ui.DrawRect(track.X + i * width, y, width + 0.5f, 8f, new Vector3(r, g, b));
        }
    }

    private void DrawSwitch(UiBox box, bool on, bool dim)
    {
        _ui.DrawRect(box.X, box.Y, box.Width, box.Height, on ? (dim ? Muted : Accent) : SwitchOff);
        var knob = box.Height - 4f;
        _ui.DrawRect(on ? box.X + box.Width - knob - 2f : box.X + 2f, box.Y + 2f, knob, knob, Knob);
    }

    private void DrawScrollbar(UiBox body, IReadOnlyList<SheetRow> rows)
    {
        var content = ContentHeight(rows);
        if (content <= body.Height)
        {
            return;
        }

        var thumb = MathF.Max(24f, body.Height * body.Height / content);
        var y = body.Y + (body.Height - thumb) * (_scroll / MathF.Max(1f, content - body.Height));
        _ui.DrawRect(body.X + body.Width - 5f, y, 3f, thumb, Track);
    }

    internal IEnumerable<(SheetRow Row, UiBox Box)> Layout(UiBox panel, IReadOnlyList<SheetRow> rows)
    {
        var y = panel.Y + HeaderHeight(panel) + 4f - _scroll;
        foreach (var row in rows)
        {
            var height = HeightOf(row);
            yield return (row, new UiBox(panel.X, y, panel.Width - 6f, height));
            y += height;
        }
    }

    private static float HeightOf(SheetRow row) => row.Kind switch
    {
        SheetRowKind.Section => SectionHeight,
        SheetRowKind.Buttons => ButtonsHeight,
        _ => RowHeight
    };

    private static float ContentHeight(IReadOnlyList<SheetRow> rows) => 4f + rows.Sum(HeightOf) + 12f;

    private float MaxScroll(UiBox panel, IReadOnlyList<SheetRow> rows) => MathF.Max(0f, ContentHeight(rows) - (panel.Height - HeaderHeight(panel)));

    internal static UiBox TrackBox(UiBox row) =>
        new(row.X + Padding + LabelColumn(row), row.Y, MathF.Max(MinTrackWidth, row.Width - Padding * 2f - LabelColumn(row) - ValueWidth), row.Height);

    // A choice uses the slider's track and the value column.
    internal static UiBox ChoiceBox(UiBox row)
    {
        var x = row.X + Padding + LabelColumn(row);
        return new UiBox(x, row.Y + 3f, MathF.Max(MinTrackWidth, row.X + row.Width - Padding - x), row.Height - 6f);
    }

    internal static UiBox ClearBox(UiBox row) => new(row.X + row.Width - Padding - SmallButton, row.Y + 2f, SmallButton, row.Height - 4f);

    internal static UiBox ButtonBox(UiBox row, int index, int count)
    {
        const float gap = 6f;
        var width = (row.Width - Padding * 2f - gap * (count - 1)) / count;
        return new UiBox(row.X + Padding + index * (width + gap), row.Y + 5f, width, row.Height - 10f);
    }

    private static float LabelColumn(UiBox row) =>
        MathF.Min(LabelWidth, MathF.Max(48f, row.Width - Padding * 2f - ValueWidth - MinTrackWidth));
}
