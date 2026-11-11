using Wisko.Engine.Math;

namespace Wisko.Engine.Gui;

/// <summary>One axis of a GUI position or size: a fraction of the parent's length plus a number of pixels.</summary>
public readonly record struct GuiDim(float Scale, float Offset)
{
    /// <summary>The length this gives inside a parent of <paramref name="parentLength"/> pixels.</summary>
    public float Resolve(float parentLength) => parentLength * Scale + Offset;
}

/// <summary>A GUI position or size on both axes (UI.json order: XScale, XOffset, YScale, YOffset).</summary>
public readonly record struct GuiDim2(GuiDim X, GuiDim Y)
{
    public GuiDim2(float xScale, float xOffset, float yScale, float yOffset)
        : this(new GuiDim(xScale, xOffset), new GuiDim(yScale, yOffset))
    {
    }

    public static GuiDim2 Zero => default;

    /// <summary>As UI.json writes it: [XScale, XOffset, YScale, YOffset].</summary>
    public override string ToString() => FormattableString.Invariant($"[{X.Scale}, {X.Offset}, {Y.Scale}, {Y.Offset}]");

    // Each component that is not finite takes the matching component of the fallback.
    internal GuiDim2 Finite(GuiDim2 fallback) => new(
        float.IsFinite(X.Scale) ? X.Scale : fallback.X.Scale,
        float.IsFinite(X.Offset) ? X.Offset : fallback.X.Offset,
        float.IsFinite(Y.Scale) ? Y.Scale : fallback.Y.Scale,
        float.IsFinite(Y.Offset) ? Y.Offset : fallback.Y.Offset);
}

/// <summary>A point inside an element as fractions of its size (0,0 = top-left, 1,1 = bottom-right).</summary>
public readonly record struct GuiPoint(float X, float Y)
{
    /// <summary>As UI.json writes it: [X, Y].</summary>
    public override string ToString() => FormattableString.Invariant($"[{X}, {Y}]");

    // Clamped to 0..1; a component that is not a number becomes 0.
    internal GuiPoint Clamped() => new(Clamp01(X), Clamp01(Y));

    private static float Clamp01(float value) => float.IsNaN(value) ? 0f : global::System.Math.Clamp(value, 0f, 1f);
}

/// <summary>An opaque colour, 0-255 per channel (UI.json stores the same bytes, so it saves and loads exactly).</summary>
public readonly record struct GuiColor(byte R, byte G, byte B)
{
    public Vector3 ToVector3() => new(R / 255f, G / 255f, B / 255f);

    /// <summary>As UI.json writes it: [R, G, B].</summary>
    public override string ToString() => $"[{R}, {G}, {B}]";
}

public enum GuiElementType
{
    Frame,
    Text,
    Button,
    Image,

    /// <summary>A one-line text field the player types into (made by scripts with UI.Create("TextBox")).</summary>
    TextBox
}

public enum GuiHorizontalAlignment
{
    Left,
    Center,
    Right
}

public enum GuiVerticalAlignment
{
    Top,
    Middle,
    Bottom
}

/// <summary>Something GUI elements can be children of: the Canvas or another element. Children are kept in order.</summary>
public abstract class GuiNode
{
    private readonly List<GuiElement> _children = [];

    /// <summary>In their saved order (the tiebreak for equal ZOrder).</summary>
    public IReadOnlyList<GuiElement> Children => _children;

    /// <summary>The document this node belongs to (null for an element that is not in one).</summary>
    public GuiDocument? Document { get; internal set; }

    internal List<GuiElement> ChildList => _children;
}

/// <summary>
/// The root of a GUI document: the runtime GUI viewport. It has no geometry of its own (its rectangle is the screen
/// area the GUI is laid out in), owns the top-level elements, and can never be deleted or reparented (it is not a
/// <see cref="GuiElement"/>, so no element operation accepts it).
/// </summary>
public sealed class GuiCanvas : GuiNode
{
    public const int CanvasId = 0;

    public int Id => CanvasId;
}

/// <summary>
/// A GUI element: identity (<see cref="Id"/>, unique in its document; the <see cref="Name"/> is free text), visibility,
/// ZOrder among its siblings, and geometry relative to its parent's rectangle (<see cref="GuiLayout"/>). Setters
/// keep values valid (see each property) and mark the document changed. Hovered / pressed are runtime states, never
/// element data.
/// </summary>
public abstract class GuiElement : GuiNode
{
    public const int DefaultZOrder = 1;

    private string _name;
    private bool _visible = true;
    private int _zOrder = DefaultZOrder;
    private GuiDim2 _position;
    private GuiDim2 _size;
    private GuiPoint _anchorPoint;

    protected GuiElement(string name, GuiDim2 size)
    {
        _name = name;
        _size = size;
    }

    public abstract GuiElementType Type { get; }

    /// <summary>The size a new element of this type gets (and that replaces a size that is not finite).</summary>
    public abstract GuiDim2 DefaultSize { get; }

    /// <summary>Unique in its document; 0 while the element is not in a document.</summary>
    public int Id { get; internal set; }

    /// <summary>The Canvas or the element this one is inside (null while not in a document).</summary>
    public GuiNode? Parent { get; internal set; }

    /// <summary>Free text (not unique, not identity). Null becomes empty.</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    /// <summary>Drawn only when this and every ancestor are visible.</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    /// <summary>Order among siblings: higher draws later (above); equal values keep their saved order.</summary>
    public int ZOrder
    {
        get => _zOrder;
        set => Set(ref _zOrder, value);
    }

    /// <summary>The anchor point's place in the parent. Default 0. A component that is not finite becomes 0.</summary>
    public GuiDim2 Position
    {
        get => _position;
        set => Set(ref _position, value.Finite(GuiDim2.Zero));
    }

    /// <summary>
    /// Width and height. Stored as given (a negative result is drawn and hit-tested as 0, never rewritten); a
    /// component that is not finite takes <see cref="DefaultSize"/>'s.
    /// </summary>
    public GuiDim2 Size
    {
        get => _size;
        set => Set(ref _size, value.Finite(DefaultSize));
    }

    /// <summary>Which point of the element sits at <see cref="Position"/>: 0..1 on each axis (clamped). Default 0,0.</summary>
    public GuiPoint AnchorPoint
    {
        get => _anchorPoint;
        set => Set(ref _anchorPoint, value.Clamped());
    }

    /// <summary>A new element of the same type with the same properties: no Id, no parent, no children.</summary>
    internal GuiElement CopyProperties()
    {
        var copy = CreateEmpty();
        copy._name = _name;
        copy._visible = _visible;
        copy._zOrder = _zOrder;
        copy._position = _position;
        copy._size = _size;
        copy._anchorPoint = _anchorPoint;
        CopyTypeProperties(copy);
        return copy;
    }

    private protected abstract GuiElement CreateEmpty();

    private protected abstract void CopyTypeProperties(GuiElement copy);

    private protected void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Document?.Touch();
    }
}

/// <summary>A rectangle of colour. Defaults: 200 x 100 px, dark grey (45, 48, 56), fully opaque.</summary>
public sealed class GuiFrame : GuiElement
{
    public static readonly GuiColor DefaultBackgroundColor = new(45, 48, 56);

    private GuiColor _backgroundColor = DefaultBackgroundColor;
    private float _opacity = 1f;

    public GuiFrame()
        : base("Frame", new GuiDim2(0f, 200f, 0f, 100f))
    {
    }

    public override GuiElementType Type => GuiElementType.Frame;

    public override GuiDim2 DefaultSize => new(0f, 200f, 0f, 100f);

    public GuiColor BackgroundColor
    {
        get => _backgroundColor;
        set => Set(ref _backgroundColor, value);
    }

    /// <summary>0 (invisible) to 1 (opaque), clamped; not a number becomes 1.</summary>
    public float Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, float.IsNaN(value) ? 1f : global::System.Math.Clamp(value, 0f, 1f));
    }

    private protected override GuiElement CreateEmpty() => new GuiFrame();

    private protected override void CopyTypeProperties(GuiElement copy)
    {
        var frame = (GuiFrame)copy;
        frame._backgroundColor = _backgroundColor;
        frame._opacity = _opacity;
    }
}

/// <summary>
/// A picture from the project, stretched to its box: <see cref="Image"/> is a project-relative path inside Assets/ (a
/// PNG or JPEG, e.g. "Assets/Images/Coin.png"); empty or unreadable draws nothing. Defaults: 100 x 100 px, no image.
/// </summary>
public sealed class GuiImage : GuiElement
{
    private string _image = string.Empty;

    public GuiImage()
        : base("Image", new GuiDim2(0f, 100f, 0f, 100f))
    {
    }

    public override GuiElementType Type => GuiElementType.Image;

    public override GuiDim2 DefaultSize => new(0f, 100f, 0f, 100f);

    /// <summary>The picture's project-relative path ("Assets/..."); null becomes empty (no picture).</summary>
    public string Image
    {
        get => _image;
        set => Set(ref _image, value ?? string.Empty);
    }

    private protected override GuiElement CreateEmpty() => new GuiImage();

    private protected override void CopyTypeProperties(GuiElement copy) => ((GuiImage)copy)._image = _image;
}

/// <summary>
/// A single line of text in its box (no background). Defaults: 200 x 50 px, "Text", white, 20 px, centred both ways.
/// </summary>
public sealed class GuiText : GuiElement
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 72;
    public const int DefaultFontSize = 20;
    public static readonly GuiColor DefaultTextColor = new(255, 255, 255);

    private string _text = "Text";
    private GuiColor _textColor = DefaultTextColor;
    private int _fontSize = DefaultFontSize;
    private GuiHorizontalAlignment _horizontalAlignment = GuiHorizontalAlignment.Center;
    private GuiVerticalAlignment _verticalAlignment = GuiVerticalAlignment.Middle;

    public GuiText()
        : base("Text", new GuiDim2(0f, 200f, 0f, 50f))
    {
    }

    public override GuiElementType Type => GuiElementType.Text;

    public override GuiDim2 DefaultSize => new(0f, 200f, 0f, 50f);

    /// <summary>Null becomes empty.</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    public GuiColor TextColor
    {
        get => _textColor;
        set => Set(ref _textColor, value);
    }

    /// <summary>Pixels, 8 to 72 (clamped).</summary>
    public int FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, ClampFontSize(value));
    }

    /// <summary>A value outside the enum becomes Center.</summary>
    public GuiHorizontalAlignment HorizontalAlignment
    {
        get => _horizontalAlignment;
        set => Set(ref _horizontalAlignment, Enum.IsDefined(value) ? value : GuiHorizontalAlignment.Center);
    }

    /// <summary>A value outside the enum becomes Middle.</summary>
    public GuiVerticalAlignment VerticalAlignment
    {
        get => _verticalAlignment;
        set => Set(ref _verticalAlignment, Enum.IsDefined(value) ? value : GuiVerticalAlignment.Middle);
    }

    public static int ClampFontSize(int size) => global::System.Math.Clamp(size, MinFontSize, MaxFontSize);

    private protected override GuiElement CreateEmpty() => new GuiText();

    private protected override void CopyTypeProperties(GuiElement copy)
    {
        var text = (GuiText)copy;
        text._text = _text;
        text._textColor = _textColor;
        text._fontSize = _fontSize;
        text._horizontalAlignment = _horizontalAlignment;
        text._verticalAlignment = _verticalAlignment;
    }
}

/// <summary>
/// A clickable box with its own centred label (no Text child). Defaults: 200 x 50 px, enabled, blue (52, 120, 246),
/// "Button", white, 20 px. Hovered / pressed belong to the runtime, not to the element.
/// </summary>
public sealed class GuiButton : GuiElement
{
    public static readonly GuiColor DefaultBackgroundColor = new(52, 120, 246);

    private bool _enabled = true;
    private GuiColor _backgroundColor = DefaultBackgroundColor;
    private string _text = "Button";
    private GuiColor _textColor = GuiText.DefaultTextColor;
    private int _fontSize = GuiText.DefaultFontSize;

    public GuiButton()
        : base("Button", new GuiDim2(0f, 200f, 0f, 50f))
    {
    }

    public override GuiElementType Type => GuiElementType.Button;

    public override GuiDim2 DefaultSize => new(0f, 200f, 0f, 50f);

    /// <summary>Whether it can be clicked (it is drawn either way; <see cref="GuiElement.Visible"/> decides that).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public GuiColor BackgroundColor
    {
        get => _backgroundColor;
        set => Set(ref _backgroundColor, value);
    }

    /// <summary>The label. Null becomes empty.</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    public GuiColor TextColor
    {
        get => _textColor;
        set => Set(ref _textColor, value);
    }

    /// <summary>Pixels, 8 to 72 (clamped).</summary>
    public int FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, GuiText.ClampFontSize(value));
    }

    private protected override GuiElement CreateEmpty() => new GuiButton();

    private protected override void CopyTypeProperties(GuiElement copy)
    {
        var button = (GuiButton)copy;
        button._enabled = _enabled;
        button._backgroundColor = _backgroundColor;
        button._text = _text;
        button._textColor = _textColor;
        button._fontSize = _fontSize;
    }
}

/// <summary>
/// A one-line text field: the player clicks it (or a script calls Focus) and types; Enter submits, Escape or a click
/// elsewhere lets go. Defaults: 300 x 36 px, dark (30, 32, 38), white 18 px text, no placeholder, at most 200
/// characters. <see cref="Text"/> never holds line breaks or control characters and is never longer than
/// <see cref="MaxLength"/> (Unicode scalar values). Focus, the caret and typing belong to the runtime, not to the element.
/// </summary>
public sealed class GuiTextBox : GuiElement
{
    public const int DefaultMaxLength = 200;
    public const int MaxMaxLength = 1000;
    public const int DefaultFontSize = 18;
    public static readonly GuiColor DefaultBackgroundColor = new(30, 32, 38);

    private string _text = string.Empty;
    private string _placeholderText = string.Empty;
    private GuiColor _backgroundColor = DefaultBackgroundColor;
    private GuiColor _textColor = GuiText.DefaultTextColor;
    private int _fontSize = DefaultFontSize;
    private int _maxLength = DefaultMaxLength;
    private bool _clearOnSubmit;

    public GuiTextBox()
        : base("TextBox", new GuiDim2(0f, 300f, 0f, 36f))
    {
    }

    public override GuiElementType Type => GuiElementType.TextBox;

    public override GuiDim2 DefaultSize => new(0f, 300f, 0f, 36f);

    /// <summary>What is typed; line breaks and tabs become spaces, control characters go, cut to <see cref="MaxLength"/>.</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, Normalize(value, _maxLength));
    }

    /// <summary>Shown dimmed while the box is empty (e.g. "Press / to chat").</summary>
    public string PlaceholderText
    {
        get => _placeholderText;
        set => Set(ref _placeholderText, Normalize(value, MaxMaxLength));
    }

    public GuiColor BackgroundColor
    {
        get => _backgroundColor;
        set => Set(ref _backgroundColor, value);
    }

    public GuiColor TextColor
    {
        get => _textColor;
        set => Set(ref _textColor, value);
    }

    /// <summary>Pixels, 8 to 72 (clamped).</summary>
    public int FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, GuiText.ClampFontSize(value));
    }

    /// <summary>The longest text, 1 to 1000 (clamped); a shorter limit cuts the current text.</summary>
    public int MaxLength
    {
        get => _maxLength;
        set
        {
            Set(ref _maxLength, global::System.Math.Clamp(value, 1, MaxMaxLength));
            Set(ref _text, Normalize(_text, _maxLength));
        }
    }

    /// <summary>Whether the text empties itself when submitted (Enter).</summary>
    public bool ClearOnSubmit
    {
        get => _clearOnSubmit;
        set => Set(ref _clearOnSubmit, value);
    }

    /// <summary>Text as a TextBox keeps it: one line, no control characters, at most <paramref name="maxLength"/> scalar values.</summary>
    public static string Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new global::System.Text.StringBuilder(global::System.Math.Min(value.Length, maxLength * 2));
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count >= maxLength)
            {
                break;
            }

            if (rune.Value is '\t' or '\n' or '\r' or 0x2028 or 0x2029)
            {
                builder.Append(' ');
            }
            else if (rune.Value < 0x20 || rune.Value is 0x7F or (>= 0x80 and <= 0x9F) || rune == global::System.Text.Rune.ReplacementChar && !value.Contains('\uFFFD'))
            {
                continue;
            }
            else
            {
                builder.Append(rune.ToString());
            }

            count++;
        }

        return builder.ToString();
    }

    private protected override GuiElement CreateEmpty() => new GuiTextBox();

    private protected override void CopyTypeProperties(GuiElement copy)
    {
        var box = (GuiTextBox)copy;
        box._text = _text;
        box._placeholderText = _placeholderText;
        box._backgroundColor = _backgroundColor;
        box._textColor = _textColor;
        box._fontSize = _fontSize;
        box._maxLength = _maxLength;
        box._clearOnSubmit = _clearOnSubmit;
    }
}
