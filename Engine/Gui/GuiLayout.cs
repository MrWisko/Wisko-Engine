namespace Wisko.Engine.Gui;

/// <summary>A rectangle in GUI pixels: origin top-left, +X right, +Y down.</summary>
public readonly record struct GuiRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public bool Contains(float x, float y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>
/// One element as laid out: its rectangle on screen, whether it is effectively visible (it and every ancestor are
/// Visible) and its level (1 = a child of the Canvas). No renderer objects, no editor state.
/// </summary>
public readonly record struct GuiLayoutEntry(GuiElement Element, GuiRect Rect, bool Visible, int Depth);

/// <summary>
/// Resolves a <see cref="GuiDocument"/> inside a canvas rectangle into <see cref="Entries"/>: every element's rectangle
/// in draw order. Draw order is sibling-relative: a parent comes before its children, and siblings are sorted by
/// ZOrder (lower first) and then by their saved order, so a child always draws above its parent. Hit testing walks
/// the same list backwards. The result is kept until the document's Version or the canvas rectangle changes.
/// <para>
/// For a parent rectangle (L, T, W, H): width = W·Size.X.Scale + Size.X.Offset and height = H·Size.Y.Scale +
/// Size.Y.Offset, each at least 0 (the stored values are never changed); the anchor point sits at
/// (L + W·Position.X.Scale + Position.X.Offset, T + H·Position.Y.Scale + Position.Y.Offset), so the element's left is
/// that x − width·AnchorPoint.X and its top that y − height·AnchorPoint.Y. Children resolve inside that rectangle.
/// Lengths and positions beyond <see cref="Limit"/> pixels (far past any screen) are held at it, so huge but finite
/// values never overflow into infinite or NaN rectangles.
/// </para>
/// </summary>
public sealed class GuiLayout
{
    /// <summary>The largest resolved length and offset from the parent, in pixels.</summary>
    public const float Limit = 10_000_000f;

    private readonly List<GuiLayoutEntry> _entries = [];
    private readonly Dictionary<int, int> _indexById = new();
    private static readonly Comparer<(GuiElement Element, int Index)> DrawOrder = Comparer<(GuiElement Element, int Index)>.Create((a, b) =>
    {
        var byZ = a.Element.ZOrder.CompareTo(b.Element.ZOrder);
        return byZ != 0 ? byZ : a.Index.CompareTo(b.Index);
    });

    private readonly List<(GuiElement Element, int Index)> _siblings = [];
    private GuiDocument? _document;
    private long _version = -1;
    private GuiRect _canvas;

    /// <summary>Every element of the document, in draw order (bottom first).</summary>
    public IReadOnlyList<GuiLayoutEntry> Entries => _entries;

    /// <summary>The canvas rectangle the entries were resolved in.</summary>
    public GuiRect Canvas => _canvas;

    /// <summary>How many times the entries were actually recomputed (not served from the cache).</summary>
    public int Rebuilds { get; private set; }

    /// <summary>The rectangle of an element inside a parent rectangle (a negative width or height counts as 0).</summary>
    public static GuiRect Resolve(GuiElement element, GuiRect parent)
    {
        var size = element.Size;
        var position = element.Position;
        var anchor = element.AnchorPoint;
        var width = Length(size.X.Resolve(parent.Width));
        var height = Length(size.Y.Resolve(parent.Height));
        var x = parent.X + Offset(position.X.Resolve(parent.Width));
        var y = parent.Y + Offset(position.Y.Resolve(parent.Height));
        return new GuiRect(x - width * anchor.X, y - height * anchor.Y, width, height);
    }

    // 0..Limit (a Scale times a parent length can overflow to infinity).
    private static float Length(float value) => value > 0f ? MathF.Min(value, Limit) : 0f;

    private static float Offset(float value) => global::System.Math.Clamp(value, -Limit, Limit);

    /// <summary>
    /// The entries for <paramref name="document"/> in <paramref name="canvas"/>; recomputed only when the document, its
    /// Version or the canvas rectangle changed since the last call.
    /// </summary>
    public IReadOnlyList<GuiLayoutEntry> Update(GuiDocument document, GuiRect canvas)
    {
        canvas = canvas with { Width = global::System.Math.Max(0f, canvas.Width), Height = global::System.Math.Max(0f, canvas.Height) };
        if (ReferenceEquals(document, _document) && document.Version == _version && canvas == _canvas)
        {
            return _entries;
        }

        _document = document;
        _version = document.Version;
        _canvas = canvas;
        _entries.Clear();
        _indexById.Clear();
        AddChildren(document.Canvas, canvas, parentVisible: true, depth: 1);
        Rebuilds++;
        return _entries;
    }

    /// <summary>The entry of an element from the last <see cref="Update"/>.</summary>
    public bool TryGet(int id, out GuiLayoutEntry entry)
    {
        if (_indexById.TryGetValue(id, out var index))
        {
            entry = _entries[index];
            return true;
        }

        entry = default;
        return false;
    }

    private void AddChildren(GuiNode parent, GuiRect parentRect, bool parentVisible, int depth)
    {
        // Sorted copy of the children: ZOrder, then saved order (never dictionary order; List.Sort alone is not stable,
        // so the saved index is part of the key).
        var children = parent.Children;
        var start = _siblings.Count;
        for (var i = 0; i < children.Count; i++)
        {
            _siblings.Add((children[i], i));
        }

        _siblings.Sort(start, children.Count, DrawOrder);
        for (var i = start; i < start + children.Count; i++)
        {
            var child = _siblings[i].Element;
            var rect = Resolve(child, parentRect);
            var visible = parentVisible && child.Visible;
            _indexById[child.Id] = _entries.Count;
            _entries.Add(new GuiLayoutEntry(child, rect, visible, depth));
            AddChildren(child, rect, visible, depth + 1);
        }

        _siblings.RemoveRange(start, children.Count);
    }
}
