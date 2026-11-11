namespace Wisko.Engine.Gui;

/// <summary>
/// One GUI: the <see cref="Canvas"/> root and its tree of elements. It owns identity (each element's Id is unique here;
/// new Ids come from <see cref="NextId"/> and are never handed out twice) and the hierarchy rules: no cycles, at most
/// <see cref="MaxDepth"/> levels below the Canvas, and removing an element removes its whole subtree.
/// <see cref="Version"/> changes with every edit (structure or any element property), so a layout cached against it
/// knows when to rebuild. Pure data: nothing here knows about rendering, the editor or Play.
/// </summary>
public sealed class GuiDocument
{
    /// <summary>Deepest level an element may be at (a child of the Canvas is level 1).</summary>
    public const int MaxDepth = 64;

    /// <summary>Largest Id (far beyond any real GUI; keeps NextId from overflowing).</summary>
    public const int MaxId = int.MaxValue / 2;

    private readonly Dictionary<int, GuiElement> _byId = new();
    private int _nextId = 1;

    public GuiDocument()
    {
        Canvas = new GuiCanvas { Document = this };
    }

    public GuiCanvas Canvas { get; }

    /// <summary>The Id the next new element gets. Always above every Id in the document.</summary>
    public int NextId => _nextId;

    /// <summary>Changes on every edit; a copy starts its own count.</summary>
    public long Version { get; private set; }

    /// <summary>Elements in the document (the Canvas not counted).</summary>
    public int Count => _byId.Count;

    /// <summary>Every element, parents before children, siblings in saved order.</summary>
    public IEnumerable<GuiElement> Elements => Descendants(Canvas);

    public GuiElement? Find(int id) => id != GuiCanvas.CanvasId && _byId.TryGetValue(id, out var element) ? element : null;

    /// <summary>
    /// Puts a new element (one not in any document) under <paramref name="parent"/> (default: the Canvas) at
    /// <paramref name="index"/> (default: last). It and anything inside it get fresh Ids.
    /// </summary>
    public T Add<T>(T element, GuiNode? parent = null, int index = -1)
        where T : GuiElement
    {
        parent ??= Canvas;
        if (element.Document is not null || element.Parent is not null)
        {
            throw new InvalidOperationException("The element is already in a document.");
        }

        if (parent.Document != this)
        {
            throw new InvalidOperationException("The parent is not in this document.");
        }

        if (DepthOf(parent) + HeightOf(element) > MaxDepth)
        {
            throw new InvalidOperationException($"GUI elements can be at most {MaxDepth} levels deep.");
        }

        Attach(element, parent, index, keepIds: false);
        Touch();
        return element;
    }

    /// <summary>Whether <paramref name="element"/> may move under <paramref name="newParent"/>; the reason when not.</summary>
    public bool CanReparent(GuiElement element, GuiNode newParent, out string? reason)
    {
        reason = element.Document != this ? "the element is not in this document"
            : newParent.Document != this ? "the new parent is not in this document"
            : ReferenceEquals(element, newParent) ? "an element cannot be its own parent"
            : IsAncestorOf(element, newParent) ? "an element cannot move inside one of its own descendants"
            : DepthOf(newParent) + HeightOf(element) > MaxDepth ? $"GUI elements can be at most {MaxDepth} levels deep"
            : null;
        return reason is null;
    }

    /// <summary>
    /// Moves <paramref name="element"/> (with its subtree) under <paramref name="newParent"/> at <paramref name="index"/>
    /// in the new parent's children after the move (default: last); also reorders within the same parent. Refused
    /// (nothing changes) for itself, one of its descendants, another document, or a result deeper than
    /// <see cref="MaxDepth"/>. Its own Position / Size values are kept as they are.
    /// </summary>
    public bool TryReparent(GuiElement element, GuiNode newParent, int index, out string? reason)
    {
        if (!CanReparent(element, newParent, out reason))
        {
            return false;
        }

        var oldParent = element.Parent!;
        oldParent.ChildList.Remove(element);
        var children = newParent.ChildList;
        children.Insert(index < 0 || index > children.Count ? children.Count : index, element);
        element.Parent = newParent;
        Touch();
        return true;
    }

    public bool TryReparent(GuiElement element, GuiNode newParent, out string? reason) => TryReparent(element, newParent, -1, out reason);

    /// <summary>Removes the element and its whole subtree; false when it is not in this document. Their Ids are not reused.</summary>
    public bool Remove(GuiElement element)
    {
        if (element.Document != this || element.Parent is not { } parent)
        {
            return false;
        }

        parent.ChildList.Remove(element);
        element.Parent = null;
        Detach(element);
        Touch();
        return true;
    }

    /// <summary>
    /// A copy of <paramref name="source"/> and everything inside it, with the same properties and child order and fresh
    /// Ids for every copied element, placed right after the source among its siblings. Names are copied as they are.
    /// </summary>
    public GuiElement DuplicateSubtree(GuiElement source)
    {
        if (source.Document != this || source.Parent is not { } parent)
        {
            throw new InvalidOperationException("The element is not in this document.");
        }

        var copy = CopySubtree(source, keepIds: false);
        Attach(copy, parent, parent.ChildList.IndexOf(source) + 1, keepIds: false);
        Touch();
        return copy;
    }

    /// <summary>
    /// An independent deep copy: the same element types, Ids, NextId, hierarchy, sibling order and properties. Changing
    /// either document afterwards never changes the other.
    /// </summary>
    public GuiDocument Clone()
    {
        var clone = new GuiDocument { _nextId = _nextId };
        foreach (var child in Canvas.Children)
        {
            clone.Attach(CopySubtree(child, keepIds: true), clone.Canvas, -1, keepIds: true);
        }

        return clone;
    }

    /// <summary>
    /// Makes this document an exact copy of <paramref name="source"/> in place (element types, Ids, hierarchy, sibling
    /// order, properties), so everything holding this document sees it (undo / redo). NextId never goes back: an Id once
    /// handed out is not handed out again.
    /// </summary>
    public void ReplaceWith(GuiDocument source)
    {
        if (ReferenceEquals(source, this))
        {
            return;
        }

        foreach (var child in Canvas.Children.ToArray())
        {
            Canvas.ChildList.Remove(child);
            child.Parent = null;
            Detach(child);
        }

        foreach (var child in source.Canvas.Children)
        {
            Attach(CopySubtree(child, keepIds: true), Canvas, -1, keepIds: true);
        }

        _nextId = global::System.Math.Max(_nextId, source._nextId);
        Touch();
    }

    /// <summary>The Canvas is level 0; its children are level 1.</summary>
    public static int DepthOf(GuiNode node)
    {
        var depth = 0;
        for (var current = node; current is GuiElement element; current = element.Parent!)
        {
            depth++;
            if (element.Parent is null)
            {
                break;
            }
        }

        return depth;
    }

    /// <summary>Levels in the subtree (an element without children is 1).</summary>
    public static int HeightOf(GuiElement element)
    {
        var height = 0;
        foreach (var child in element.Children)
        {
            height = global::System.Math.Max(height, HeightOf(child));
        }

        return height + 1;
    }

    /// <summary>Whether <paramref name="ancestor"/> contains <paramref name="node"/> somewhere below it.</summary>
    public static bool IsAncestorOf(GuiNode ancestor, GuiNode node)
    {
        for (var current = (node as GuiElement)?.Parent; current is not null; current = (current as GuiElement)?.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Everything below <paramref name="node"/>, parents before children, siblings in saved order.</summary>
    public static IEnumerable<GuiElement> Descendants(GuiNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    internal void Touch() => Version++;

    // Loading: the trees read from UI.json (detached, each element's Id as saved, 0 when it had none). The first element
    // (parents before children) with a given positive Id keeps it; a missing, invalid or repeated Id gets a fresh one
    // above every kept Id. NextId keeps its saved value when that is above every kept Id.
    internal void Load(IReadOnlyList<GuiElement> roots, int? savedNextId, out int idsRepaired, out bool nextIdRepaired)
    {
        var kept = new HashSet<int>();
        idsRepaired = 0;
        foreach (var element in roots.SelectMany(root => Descendants(root).Prepend(root)))
        {
            if (element.Id is > 0 and <= MaxId && kept.Add(element.Id))
            {
                continue;
            }

            element.Id = 0;
            idsRepaired++;
        }

        var minimum = kept.Count == 0 ? 1 : kept.Max() + 1;
        nextIdRepaired = savedNextId is not { } saved || saved < minimum || saved > MaxId + 1;
        _nextId = nextIdRepaired ? minimum : savedNextId!.Value;
        foreach (var root in roots)
        {
            Attach(root, Canvas, -1, keepIds: true);
        }

        Touch();
    }

    // Puts the subtree in; with keepIds an element keeps a non-zero Id (Clone, loading), otherwise every element of the
    // subtree gets a fresh one. Ids above NextId move NextId past them.
    private void Attach(GuiElement element, GuiNode parent, int index, bool keepIds)
    {
        var children = parent.ChildList;
        children.Insert(index < 0 || index > children.Count ? children.Count : index, element);
        element.Parent = parent;
        Register(element, keepIds);
    }

    private void Register(GuiElement element, bool keepIds)
    {
        if (!keepIds || element.Id <= 0 || _byId.ContainsKey(element.Id))
        {
            element.Id = _nextId;
        }

        _byId.Add(element.Id, element);
        _nextId = global::System.Math.Max(_nextId, element.Id + 1);
        element.Document = this;
        foreach (var child in element.Children)
        {
            Register(child, keepIds);
        }
    }

    private void Detach(GuiElement element)
    {
        _byId.Remove(element.Id);
        element.Document = null;
        foreach (var child in element.Children)
        {
            Detach(child);
        }
    }

    // A detached copy of the subtree: same properties and child order; Ids copied (keepIds) or left 0.
    private static GuiElement CopySubtree(GuiElement source, bool keepIds)
    {
        var copy = source.CopyProperties();
        copy.Id = keepIds ? source.Id : 0;
        foreach (var child in source.Children)
        {
            var childCopy = CopySubtree(child, keepIds);
            copy.ChildList.Add(childCopy);
            childCopy.Parent = copy;
        }

        return copy;
    }
}
