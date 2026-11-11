using System.Globalization;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>An editable value of the UI panel's Properties (one field, checkbox, slider or toggle).</summary>
internal enum GuiProp
{
    Name,
    Visible,
    ZOrder,
    PosXScale,
    PosXOffset,
    PosYScale,
    PosYOffset,
    SizeXScale,
    SizeXOffset,
    SizeYScale,
    SizeYOffset,
    AnchorX,
    AnchorY,
    BackgroundColor,
    Opacity,
    Text,
    FontSize,
    TextColor,
    AlignLeft,
    AlignCenter,
    AlignRight,
    AlignTop,
    AlignMiddle,
    AlignBottom,
    Enabled
}

internal enum GuiPanelControlKind
{
    Field,
    Checkbox,
    Slider,
    Toggle,
    Swatch
}

/// <summary>One control of the Properties area: what it edits, where it is, its text (or label) and whether it is on.</summary>
internal readonly record struct GuiPanelControl(GuiProp Prop, GuiPanelControlKind Kind, UiBox Box, string Text, bool On, Vector3 Color);

/// <summary>One hierarchy row: an element (null for the Canvas), its depth and where it is.</summary>
internal readonly record struct GuiTreeRow(GuiElement? Element, int Depth, UiBox Box);

/// <summary>What the panel draws besides the document: selection, the value being typed, hover, drop target, Play.</summary>
internal readonly record struct GuiPanelView(
    int? SelectedId,
    bool Editable,
    GuiProp? EditingProp,
    bool EditingInTree,
    string EditText,
    bool EditSelectAll,
    bool CaretVisible,
    GuiTreeRow? DropTarget,
    float MouseX,
    float MouseY);

/// <summary>
/// The UI section's right-hand panel: a "UI" title, the Hierarchy (Canvas and every element, always expanded, indented by
/// depth, in saved sibling order) and the selected element's Properties. The two areas scroll on their own. Layout is
/// computed once per frame into <see cref="Rows"/> / <see cref="Controls"/> and used for both drawing and hit testing.
/// Values are read from the document every frame (nothing is cached).
/// </summary>
internal sealed class GuiPanel : IDisposable
{
    public const float Width = WorldExplorerRenderer.SidebarWidth;
    public const float RowHeight = 24f;
    public const float PropertyRowHeight = 26f;

    private const float TitleHeight = 34f;
    private const float SectionHeight = 22f;
    private const float Padding = 10f;
    private const float Indent = 14f;
    private const float MaxIndent = 112f;
    private const float LabelWidth = 66f;
    private const float FieldHeight = 20f;
    private const float Gap = 4f;
    private const float TextScale = 1.85f;
    private const float SmallTextScale = 1.65f;

    private static readonly Vector3 Fill = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 Separator = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 TitleText = new(0.92f, 0.94f, 0.98f);
    private static readonly Vector3 Text = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 SectionText = new(0.70f, 0.74f, 0.80f);
    private static readonly Vector3 Muted = new(0.58f, 0.62f, 0.68f);
    private static readonly Vector3 RowHover = new(0.18f, 0.19f, 0.22f);
    private static readonly Vector3 Selected = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 Accent = new(0.55f, 0.75f, 1.0f);
    private static readonly Vector3 FieldFill = new(0.08f, 0.085f, 0.10f);
    private static readonly Vector3 FieldBorder = new(0.26f, 0.28f, 0.32f);
    private static readonly Vector3 BoxBorder = new(0.55f, 0.58f, 0.64f);
    private static readonly Vector3 ButtonFill = new(0.20f, 0.21f, 0.25f);
    private static readonly Vector3 Track = new(0.26f, 0.28f, 0.32f);

    private IUiRenderer _ui = null!;
    private float _treeScroll;
    private float _propertiesScroll;

    public List<GuiTreeRow> Rows { get; } = [];

    public List<GuiPanelControl> Controls { get; } = [];

    /// <summary>Property row labels and their y (drawn in the Properties area).</summary>
    public List<(string Label, float Y)> Labels { get; } = [];

    public UiBox Tree { get; private set; }

    public UiBox Properties { get; private set; }

    public float TreeContentHeight { get; private set; }

    public float PropertiesContentHeight { get; private set; }

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public void Dispose() => _ui.Dispose();

    public static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Hex(GuiColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>The text a field shows (and typing starts from) for an element.</summary>
    public static string ValueText(GuiProp prop, GuiElement element) => prop switch
    {
        GuiProp.Name => element.Name,
        GuiProp.ZOrder => element.ZOrder.ToString(CultureInfo.InvariantCulture),
        GuiProp.PosXScale => Number(element.Position.X.Scale),
        GuiProp.PosXOffset => Number(element.Position.X.Offset),
        GuiProp.PosYScale => Number(element.Position.Y.Scale),
        GuiProp.PosYOffset => Number(element.Position.Y.Offset),
        GuiProp.SizeXScale => Number(element.Size.X.Scale),
        GuiProp.SizeXOffset => Number(element.Size.X.Offset),
        GuiProp.SizeYScale => Number(element.Size.Y.Scale),
        GuiProp.SizeYOffset => Number(element.Size.Y.Offset),
        GuiProp.AnchorX => Number(element.AnchorPoint.X),
        GuiProp.AnchorY => Number(element.AnchorPoint.Y),
        GuiProp.BackgroundColor => element switch { GuiFrame f => Hex(f.BackgroundColor), GuiButton b => Hex(b.BackgroundColor), _ => "" },
        GuiProp.Opacity => element is GuiFrame frame ? frame.Opacity.ToString("0.00", CultureInfo.InvariantCulture) : "",
        GuiProp.Text => element switch { GuiText t => t.Text, GuiButton b => b.Text, _ => "" },
        GuiProp.FontSize => element switch { GuiText t => t.FontSize.ToString(CultureInfo.InvariantCulture), GuiButton b => b.FontSize.ToString(CultureInfo.InvariantCulture), _ => "" },
        GuiProp.TextColor => element switch { GuiText t => Hex(t.TextColor), GuiButton b => Hex(b.TextColor), _ => "" },
        _ => ""
    };

    /// <summary>The panel's areas, hierarchy rows and property controls for this frame (scroll offsets clamped here).</summary>
    public void Build(UiBox panel, GuiDocument document, GuiElement? selected, ref float treeScroll, ref float propertiesScroll)
    {
        Rows.Clear();
        Controls.Clear();
        Labels.Clear();
        var bodyTop = panel.Y + TitleHeight;
        var bodyHeight = MathF.Max(0f, panel.Height - TitleHeight);
        var count = document.Count + 1;
        TreeContentHeight = SectionHeight + count * RowHeight + 6f;
        var treeHeight = MathF.Min(TreeContentHeight, MathF.Max(RowHeight * 3f, bodyHeight * 0.45f));
        Tree = new UiBox(panel.X, bodyTop, panel.Width, treeHeight);
        Properties = new UiBox(panel.X, bodyTop + treeHeight, panel.Width, MathF.Max(0f, bodyHeight - treeHeight));
        treeScroll = global::System.Math.Clamp(treeScroll, 0f, MathF.Max(0f, TreeContentHeight - Tree.Height));

        var y = Tree.Y + SectionHeight - treeScroll;
        Rows.Add(new GuiTreeRow(null, 0, new UiBox(panel.X + 4f, y, panel.Width - 8f, RowHeight)));
        y += RowHeight;
        foreach (var element in document.Elements)
        {
            Rows.Add(new GuiTreeRow(element, GuiDocument.DepthOf(element), new UiBox(panel.X + 4f, y, panel.Width - 8f, RowHeight)));
            y += RowHeight;
        }

        BuildProperties(selected, propertiesScroll);
        propertiesScroll = global::System.Math.Clamp(propertiesScroll, 0f, MathF.Max(0f, PropertiesContentHeight - Properties.Height));
        if (propertiesScroll != 0f && Controls.Count > 0)
        {
            // Re-place with the clamped scroll (it may have changed above).
            BuildProperties(selected, propertiesScroll);
        }

        _treeScroll = treeScroll;
        _propertiesScroll = propertiesScroll;
    }

    /// <summary>The typed field of a property (colours also have a swatch, which is not it).</summary>
    public UiBox? ControlBox(GuiProp prop)
    {
        foreach (var control in Controls)
        {
            if (control.Prop == prop && control.Kind == GuiPanelControlKind.Field)
            {
                return control.Box;
            }
        }

        return null;
    }

    public void Render(UiBox panel, GuiPanelView view, int screenWidth, int screenHeight, float dpiScale)
    {
        _ui.BeginBatch(dpiScale);
        _ui.SetClip(new UiClip(panel.X, panel.Y, panel.Width, panel.Height));
        _ui.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, Fill);
        _ui.DrawRect(panel.X, panel.Y, 1f, panel.Height, Separator);
        _ui.DrawString(panel.X + Padding, panel.Y + 10f, "UI", TitleText, TextScale, FontWeight.SemiBold);
        _ui.DrawRect(panel.X + Padding, panel.Y + TitleHeight - 1f, panel.Width - Padding * 2f, 1f, Separator);

        // Hierarchy.
        _ui.SetClip(new UiClip(Tree.X, Tree.Y, Tree.Width, Tree.Height));
        _ui.DrawString(panel.X + Padding, Rows.Count > 0 ? Rows[0].Box.Y - SectionHeight + 4f : Tree.Y + 4f, "Hierarchy", SectionText, SmallTextScale, FontWeight.SemiBold);
        foreach (var row in Rows)
        {
            if (row.Box.Y + row.Box.Height < Tree.Y || row.Box.Y > Tree.Y + Tree.Height)
            {
                continue;
            }

            DrawRow(row, view);
        }

        DrawScrollThumb(Tree, TreeContentHeight, _treeScroll);
        _ui.SetClip(null);
        _ui.DrawRect(panel.X + Padding, Properties.Y, panel.Width - Padding * 2f, 1f, Separator);

        // Properties.
        _ui.SetClip(new UiClip(Properties.X, Properties.Y + 1f, Properties.Width, MathF.Max(0f, Properties.Height - 1f)));
        foreach (var (label, y) in Labels)
        {
            // "#Header", "~muted note" (both the full width), or a row label.
            var isHeader = label.StartsWith('#');
            var isNote = label.StartsWith('~');
            var shown = isHeader || isNote ? label[1..] : label;
            _ui.DrawString(panel.X + Padding, y + (PropertyRowHeight - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f,
                UiText.Fit(_ui, shown, isHeader || isNote ? panel.Width - Padding * 2f : LabelWidth - 4f, SmallTextScale),
                isHeader ? SectionText : isNote || !view.Editable ? Muted : Text, SmallTextScale, isHeader ? FontWeight.SemiBold : FontWeight.Regular);
        }

        foreach (var control in Controls)
        {
            DrawControl(control, view);
        }

        DrawScrollThumb(Properties, PropertiesContentHeight, _propertiesScroll);
        _ui.SetClip(null);
        _ui.Flush(screenWidth, screenHeight);
    }

    private void BuildProperties(GuiElement? element, float scroll)
    {
        Controls.Clear();
        Labels.Clear();
        var x = Properties.X + Padding;
        var right = Properties.X + Properties.Width - Padding;
        var fieldsX = x + LabelWidth;
        var full = right - fieldsX;
        var half = (full - Gap) * 0.5f;
        var y = Properties.Y + 6f - scroll;

        void Header(string text)
        {
            Labels.Add(("#" + text, y));
            y += SectionHeight + 2f;
        }

        void Row(string label)
        {
            Labels.Add((label, y));
        }

        UiBox Box(float left, float width) => new(left, y + (PropertyRowHeight - FieldHeight) * 0.5f, width, FieldHeight);

        void Field(string label, GuiProp prop, GuiElement e)
        {
            Row(label);
            Controls.Add(new GuiPanelControl(prop, GuiPanelControlKind.Field, Box(fieldsX, full), ValueText(prop, e), false, default));
            y += PropertyRowHeight;
        }

        void Pair(string label, GuiProp first, GuiProp second, GuiElement e)
        {
            Row(label);
            Controls.Add(new GuiPanelControl(first, GuiPanelControlKind.Field, Box(fieldsX, half), ValueText(first, e), false, default));
            Controls.Add(new GuiPanelControl(second, GuiPanelControlKind.Field, Box(fieldsX + half + Gap, half), ValueText(second, e), false, default));
            y += PropertyRowHeight;
        }

        void Check(string label, GuiProp prop, bool on)
        {
            Row(label);
            Controls.Add(new GuiPanelControl(prop, GuiPanelControlKind.Checkbox, new UiBox(right - 16f, y + 5f, 16f, 16f), "", on, default));
            y += PropertyRowHeight;
        }

        void Color(string label, GuiProp prop, GuiColor color, GuiElement e)
        {
            Row(label);
            Controls.Add(new GuiPanelControl(prop, GuiPanelControlKind.Swatch, Box(fieldsX, 20f), "", false, color.ToVector3()));
            Controls.Add(new GuiPanelControl(prop, GuiPanelControlKind.Field, Box(fieldsX + 20f + Gap, full - 20f - Gap), ValueText(prop, e), false, default));
            y += PropertyRowHeight;
        }

        void Toggles(string label, (GuiProp Prop, string Text, bool On)[] items)
        {
            Row(label);
            var width = (full - Gap * (items.Length - 1)) / items.Length;
            for (var i = 0; i < items.Length; i++)
            {
                Controls.Add(new GuiPanelControl(items[i].Prop, GuiPanelControlKind.Toggle, Box(fieldsX + i * (width + Gap), width), items[i].Text, items[i].On, default));
            }

            y += PropertyRowHeight;
        }

        Header("Properties");
        if (element is null)
        {
            Labels.Add(("#Canvas: the game screen", y));
            y += PropertyRowHeight;
            Labels.Add(("~Select an element to edit it.", y));
            y += PropertyRowHeight;
            PropertiesContentHeight = y + scroll - Properties.Y + 8f;
            return;
        }

        Field("Name", GuiProp.Name, element);
        Check("Visible", GuiProp.Visible, element.Visible);
        if (element is GuiButton enabledButton)
        {
            Check("Enabled", GuiProp.Enabled, enabledButton.Enabled);
        }

        Row("ZOrder");
        Controls.Add(new GuiPanelControl(GuiProp.ZOrder, GuiPanelControlKind.Field, Box(fieldsX, half), ValueText(GuiProp.ZOrder, element), false, default));
        y += PropertyRowHeight;

        Header("Position  (Scale, Offset)");
        Pair("X", GuiProp.PosXScale, GuiProp.PosXOffset, element);
        Pair("Y", GuiProp.PosYScale, GuiProp.PosYOffset, element);
        Header("Size  (Scale, Offset)");
        Pair("Width", GuiProp.SizeXScale, GuiProp.SizeXOffset, element);
        Pair("Height", GuiProp.SizeYScale, GuiProp.SizeYOffset, element);
        Header("Anchor Point  (X, Y)");
        Pair("Anchor", GuiProp.AnchorX, GuiProp.AnchorY, element);

        switch (element)
        {
            case GuiFrame frame:
                Header("Frame");
                Color("Color", GuiProp.BackgroundColor, frame.BackgroundColor, element);
                Row("Opacity");
                Controls.Add(new GuiPanelControl(GuiProp.Opacity, GuiPanelControlKind.Slider, Box(fieldsX, full - 40f), ValueText(GuiProp.Opacity, element), false, default));
                y += PropertyRowHeight;
                break;
            case GuiText text:
                Header("Text");
                Field("Text", GuiProp.Text, element);
                Row("Font Size");
                Controls.Add(new GuiPanelControl(GuiProp.FontSize, GuiPanelControlKind.Field, Box(fieldsX, half), ValueText(GuiProp.FontSize, element), false, default));
                y += PropertyRowHeight;
                Color("Text Color", GuiProp.TextColor, text.TextColor, element);
                Toggles("Align H", [(GuiProp.AlignLeft, "Left", text.HorizontalAlignment == GuiHorizontalAlignment.Left),
                    (GuiProp.AlignCenter, "Center", text.HorizontalAlignment == GuiHorizontalAlignment.Center),
                    (GuiProp.AlignRight, "Right", text.HorizontalAlignment == GuiHorizontalAlignment.Right)]);
                Toggles("Align V", [(GuiProp.AlignTop, "Top", text.VerticalAlignment == GuiVerticalAlignment.Top),
                    (GuiProp.AlignMiddle, "Middle", text.VerticalAlignment == GuiVerticalAlignment.Middle),
                    (GuiProp.AlignBottom, "Bottom", text.VerticalAlignment == GuiVerticalAlignment.Bottom)]);
                break;
            case GuiButton button:
                Header("Button");
                Color("Color", GuiProp.BackgroundColor, button.BackgroundColor, element);
                Field("Text", GuiProp.Text, element);
                Row("Font Size");
                Controls.Add(new GuiPanelControl(GuiProp.FontSize, GuiPanelControlKind.Field, Box(fieldsX, half), ValueText(GuiProp.FontSize, element), false, default));
                y += PropertyRowHeight;
                Color("Text Color", GuiProp.TextColor, button.TextColor, element);
                break;
        }

        PropertiesContentHeight = y + scroll - Properties.Y + 8f;
    }

    private void DrawRow(GuiTreeRow row, GuiPanelView view)
    {
        var box = row.Box;
        var selected = row.Element is { } element && element.Id == view.SelectedId;
        var hovered = box.Contains(view.MouseX, view.MouseY) && Tree.Contains(view.MouseX, view.MouseY);
        if (selected)
        {
            _ui.DrawRect(box.X, box.Y + 1f, box.Width, box.Height - 2f, Selected);
        }
        else if (hovered)
        {
            _ui.DrawRect(box.X, box.Y + 1f, box.Width, box.Height - 2f, RowHover);
        }

        if (view.DropTarget is { } target && target.Box == box)
        {
            _ui.DrawBorderRect(box.X, box.Y + 1f, box.Width, box.Height - 2f, Accent, 1.5f);
        }

        var indent = MathF.Min(row.Depth * Indent, MaxIndent);
        var textX = box.X + 6f + indent;
        var textY = box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f;
        if (row.Element is null)
        {
            _ui.DrawString(textX, textY, "Canvas", TitleText, SmallTextScale, FontWeight.SemiBold);
            return;
        }

        var kind = row.Element.Type.ToString();
        var kindWidth = _ui.MeasureWidth(kind, SmallTextScale);
        _ui.DrawString(box.X + box.Width - 6f - kindWidth, textY, kind, selected ? Text : Muted, SmallTextScale);
        var room = box.Width - 6f - indent - kindWidth - 12f;
        if (view.EditingInTree && selected)
        {
            DrawEditText(new UiBox(textX - 3f, box.Y + 2f, MathF.Max(20f, room), box.Height - 4f), view);
            return;
        }

        var name = row.Element.Name.Length == 0 ? "(unnamed)" : row.Element.Name;
        // A hidden element is muted; on the selection colour it is a step brighter than that, so it stays readable.
        var color = row.Element.Visible ? (selected ? TitleText : Text) : selected ? Text : Muted;
        _ui.DrawString(textX, textY, UiText.Fit(_ui, name, room, SmallTextScale), color, SmallTextScale);
    }

    private void DrawControl(GuiPanelControl control, GuiPanelView view)
    {
        var box = control.Box;
        var hovered = view.Editable && box.Contains(view.MouseX, view.MouseY);
        switch (control.Kind)
        {
            case GuiPanelControlKind.Field:
                if (view.EditingProp == control.Prop && !view.EditingInTree)
                {
                    DrawEditText(box, view);
                    return;
                }

                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, FieldFill);
                _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, hovered ? BoxBorder : FieldBorder, 1f);
                _ui.DrawString(box.X + 5f, box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f, UiText.Fit(_ui, control.Text, box.Width - 10f, SmallTextScale),
                    view.Editable ? Text : Muted, SmallTextScale);
                return;
            case GuiPanelControlKind.Checkbox:
                if (control.On)
                {
                    _ui.DrawRect(box.X, box.Y, box.Width, box.Height, view.Editable ? Selected : ButtonFill);
                    _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, view.Editable ? Accent : FieldBorder, 1f);
                    UiIcons.DrawCheck(_ui, box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f, 10f, view.Editable ? TitleText : Muted);
                }
                else
                {
                    _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, view.Editable ? BoxBorder : FieldBorder, 1.5f);
                }

                return;
            case GuiPanelControlKind.Swatch:
                _ui.DrawRect(box.X - 1f, box.Y - 1f, box.Width + 2f, box.Height + 2f, BoxBorder);
                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, control.Color);
                return;
            case GuiPanelControlKind.Toggle:
                _ui.DrawRect(box.X, box.Y, box.Width, box.Height, control.On ? (view.Editable ? Selected : ButtonFill) : hovered ? RowHover : ButtonFill);
                _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, control.On && view.Editable ? Accent : FieldBorder, 1f);
                var width = _ui.MeasureWidth(control.Text, SmallTextScale);
                var label = width <= box.Width - 4f ? control.Text : control.Text[..1];
                width = _ui.MeasureWidth(label, SmallTextScale);
                _ui.DrawString(box.X + (box.Width - width) * 0.5f, box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f, label,
                    control.On && view.Editable ? TitleText : view.Editable ? Text : Muted, SmallTextScale);
                return;
            case GuiPanelControlKind.Slider:
                var value = float.TryParse(control.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? global::System.Math.Clamp(parsed, 0f, 1f) : 0f;
                var trackY = box.Y + box.Height * 0.5f - 2f;
                _ui.DrawRect(box.X, trackY, box.Width, 4f, Track);
                _ui.DrawRect(box.X, trackY, box.Width * value, 4f, view.Editable ? Selected : Muted);
                var knobX = box.X + box.Width * value;
                _ui.DrawRect(knobX - 4f, box.Y + box.Height * 0.5f - 7f, 8f, 14f, view.Editable ? BoxBorder : FieldBorder);
                _ui.DrawRect(knobX - 3f, box.Y + box.Height * 0.5f - 6f, 6f, 12f, view.Editable ? TitleText : Muted);
                _ui.DrawString(box.X + box.Width + 8f, box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f, control.Text, view.Editable ? Text : Muted, SmallTextScale);
                return;
        }
    }

    // A thin thumb at the right edge while the area's content is taller than the area (as the property sheets show it).
    private void DrawScrollThumb(UiBox area, float content, float scroll)
    {
        if (content <= area.Height + 1f)
        {
            return;
        }

        var thumb = MathF.Max(24f, area.Height * area.Height / content);
        var y = area.Y + (area.Height - thumb) * (scroll / MathF.Max(1f, content - area.Height));
        _ui.DrawRect(area.X + area.Width - 5f, y, 3f, thumb, Track);
    }

    // The value being typed: the text (highlighted while all selected, so the first key replaces it) and a caret.
    private void DrawEditText(UiBox box, GuiPanelView view)
    {
        _ui.DrawRect(box.X, box.Y, box.Width, box.Height, FieldFill);
        _ui.DrawBorderRect(box.X, box.Y, box.Width, box.Height, Accent, 1f);
        var room = box.Width - 10f;
        var shown = view.EditText;
        while (shown.Length > 0 && _ui.MeasureWidth(shown, SmallTextScale) > room)
        {
            shown = shown[1..];
        }

        var width = _ui.MeasureWidth(shown, SmallTextScale);
        var textY = box.Y + (box.Height - _ui.MeasureLineHeight(SmallTextScale)) * 0.5f;
        if (view.EditSelectAll && shown.Length > 0)
        {
            _ui.DrawRect(box.X + 4f, box.Y + 3f, width + 2f, box.Height - 6f, Selected);
        }

        _ui.DrawString(box.X + 5f, textY, shown, TitleText, SmallTextScale);
        if (view.CaretVisible && !view.EditSelectAll)
        {
            _ui.DrawRect(box.X + 5f + width + 1f, box.Y + 4f, 1.5f, box.Height - 8f, TitleText);
        }
    }
}
