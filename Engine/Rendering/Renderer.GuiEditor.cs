using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The UI ToolBench section's 2D workspace (Edit mode, 3D View tab): the authored GUI drawn with the same layout and
/// renderer as Play, the canvas bounds, and the selected element's outline and resize handles (editor-only, never in
/// Play). One GUI element can be selected, kept apart from the Part selection. Clicking picks the topmost visible element
/// (Frame, Text or Button); dragging it changes only its Position Offset, a handle only its Size Offset (and Position
/// Offset, so the opposite edge stays put). Frame / Text / Button add one element and select it; Delete removes the
/// selection with its subtree; Ctrl+D duplicates it. Every edit is one undo step holding the whole GUI before and after.
/// The preview is the game view itself, or a fixed size laid out at that size and drawn smaller to fit.
/// </summary>
public sealed partial class Renderer
{
    private const string GuiPlayMessage = "Stop Play Mode to edit UI.";
    private const float HandleSize = 8f;
    private const float HandleReach = 6f;
    private const float MinGuiSize = 1f;
    private static readonly float[] PreviewZooms = [1f, 0.75f, 0.5f, 0.375f, 0.25f];
    private static readonly Vector3 GuiAccent = new(0.55f, 0.75f, 1.0f);

    private readonly GuiSelection _guiSelection = new();
    private readonly GuiLayout _guiEditLayout = new();
    private readonly List<GuiLayoutEntry> _guiEditScreen = [];
    private GuiPreviewSize _guiPreview = GuiPreviewSize.Viewport;
    private GuiDrag? _guiDrag;

    /// <summary>A resize handle of the selected element (None: its body, which moves it).</summary>
    internal enum GuiHandle
    {
        None,
        TopLeft,
        Top,
        TopRight,
        Left,
        Right,
        BottomLeft,
        Bottom,
        BottomRight
    }

    /// <summary>The UI section's workspace is showing: Edit mode, UI section, 3D View tab.</summary>
    private bool GuiEditorActive => !_playMode.IsPlaying && _toolBench.Current == ToolBenchSection.UI && ActiveDocument is null && _workspace is not null;

    private GuiElement? SelectedGuiElement => _guiSelection.SelectedId is { } id ? Gui.Find(id) : null;

    // Where the canvas is: the rectangle it is laid out in, where that appears on screen, and the scale between them.
    private (GuiRect Layout, GuiRect Screen, float Zoom) GuiPreviewFrame
    {
        get
        {
            var view = GameViewRect;
            if (_guiPreview == GuiPreviewSize.Viewport)
            {
                return (view, view, 1f);
            }

            var (width, height) = _guiPreview == GuiPreviewSize.Hd720 ? (1280f, 720f) : (1920f, 1080f);
            var room = (Width: MathF.Max(1f, view.Width - 32f), Height: MathF.Max(1f, view.Height - 32f));
            var zoom = PreviewZooms[^1];
            foreach (var candidate in PreviewZooms)
            {
                if (width * candidate <= room.Width && height * candidate <= room.Height)
                {
                    zoom = candidate;
                    break;
                }
            }

            var screen = new GuiRect(view.X + (view.Width - width * zoom) * 0.5f, view.Y + (view.Height - height * zoom) * 0.5f, width * zoom, height * zoom);
            return (new GuiRect(0f, 0f, width, height), screen, zoom);
        }
    }

    private static bool IsIdentity((GuiRect Layout, GuiRect Screen, float Zoom) frame) => frame.Zoom == 1f && frame.Layout == frame.Screen;

    private static GuiRect ToScreen(GuiRect rect, (GuiRect Layout, GuiRect Screen, float Zoom) frame) =>
        IsIdentity(frame)
            ? rect
            : new GuiRect(frame.Screen.X + (rect.X - frame.Layout.X) * frame.Zoom, frame.Screen.Y + (rect.Y - frame.Layout.Y) * frame.Zoom, rect.Width * frame.Zoom, rect.Height * frame.Zoom);

    private static (float X, float Y) ToLayout(float x, float y, (GuiRect Layout, GuiRect Screen, float Zoom) frame) =>
        IsIdentity(frame) ? (x, y) : (frame.Layout.X + (x - frame.Screen.X) / frame.Zoom, frame.Layout.Y + (y - frame.Screen.Y) / frame.Zoom);

    /// <summary>Entering a project: no GUI selection, no drag, Viewport preview.</summary>
    private void ResetGuiEditor()
    {
        _guiDrag = null;
        _guiSelection.SelectedId = null;
        _guiPreview = GuiPreviewSize.Viewport;
        _guiFieldEdit = null;
        _guiSlider = null;
        _guiTreeDrag = null;
        _guiTreeScroll = 0f;
        _guiPropertiesScroll = 0f;
    }

    private void SetGuiPreview(GuiPreviewSize preview)
    {
        CommitGuiDrag();
        _guiPreview = preview;
        ActivateTab(DocumentTabHit.SceneView);
        Console.WriteLine($"[UI] Preview {preview}");
    }

    // ---- Adding, deleting, duplicating ----

    // Frame / Text / Button: one new element, centred in its parent, selected; the selected Frame is its parent (a selected
    // Text or Button: that one's parent), else the Canvas.
    private void AddGuiElement(GuiElement element)
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show(GuiPlayMessage);
            return;
        }

        ActivateTab(DocumentTabHit.SceneView);
        CommitGuiDrag();
        var document = Gui;
        var selected = SelectedGuiElement;
        var parent = selected is GuiFrame frame ? frame : selected?.Parent ?? document.Canvas;
        element.Name = UniqueGuiName(document, element.Name);
        element.Position = new GuiDim2(0.5f, 0f, 0.5f, 0f);
        element.AnchorPoint = new GuiPoint(0.5f, 0.5f);
        var before = document.Clone();
        var selectedBefore = _guiSelection.SelectedId;
        try
        {
            document.Add(element, parent);
        }
        catch (InvalidOperationException)
        {
            _toast.Show($"Can't add it there: GUI elements can be at most {GuiDocument.MaxDepth} levels deep.");
            return;
        }

        _guiSelection.SelectedId = element.Id;
        RecordGuiChange($"Add {element.Type}", element.Name, before, selectedBefore);
    }

    // Delete / Backspace: the selection and everything inside it.
    private void DeleteGuiSelection()
    {
        if (SelectedGuiElement is not { } element)
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(GuiPlayMessage);
            return;
        }

        CommitGuiDrag();
        var before = Gui.Clone();
        var selectedBefore = _guiSelection.SelectedId;
        Gui.Remove(element);
        _guiSelection.SelectedId = null;
        RecordGuiChange("Delete", element.Name, before, selectedBefore);
    }

    // Ctrl+D: a copy of the selection and its subtree (fresh Ids) right after it, renamed and moved 10 px right and down.
    private void DuplicateGuiSelection()
    {
        if (SelectedGuiElement is not { } element)
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(GuiPlayMessage);
            return;
        }

        CommitGuiDrag();
        var name = UniqueGuiName(Gui, element.Name);
        var before = Gui.Clone();
        var selectedBefore = _guiSelection.SelectedId;
        var copy = Gui.DuplicateSubtree(element);
        copy.Name = name;
        var position = copy.Position;
        copy.Position = new GuiDim2(position.X.Scale, position.X.Offset + 10f, position.Y.Scale, position.Y.Offset + 10f);
        _guiSelection.SelectedId = copy.Id;
        RecordGuiChange("Duplicate", name, before, selectedBefore);
    }

    private void RecordGuiChange(string label, string name, GuiDocument before, int? selectedBefore)
    {
        _editorHistory.Push(EditorCommand.GuiChange(label, name, Gui, before, Gui.Clone(), _guiSelection, selectedBefore, _guiSelection.SelectedId));
        Console.WriteLine($"[UI] {label} {name}");
    }

    // Wisko's naming: the name itself when unused, else its stem (trailing digits removed) and the first free number from 2.
    private static string UniqueGuiName(GuiDocument document, string baseName)
    {
        var names = document.Elements.Select(element => element.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        var stem = baseName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (stem.Length == 0)
        {
            stem = baseName;
        }

        var index = 2;
        while (names.Contains($"{stem}{index}"))
        {
            index++;
        }

        return $"{stem}{index}";
    }

    // ---- Pointer ----

    // The UI section replaces the 3D view's picking: chrome first (it already had the click), then the canvas.
    private void HandleGuiEditorInput(InputManager input)
    {
        var frame = GuiPreviewFrame;
        var entries = _guiEditLayout.Update(Gui, frame.Layout);
        if (_guiDrag is { } drag)
        {
            // Every frame of the drag, the release frame included (the element ends where the button went up).
            _toolbarConsumedClick = true;
            ApplyGuiDrag(drag, input.MouseX, input.MouseY, frame);
            if (!input.IsLeftMouseHeld)
            {
                CommitGuiDrag();
            }

            return;
        }

        var x = input.MouseX;
        var y = input.MouseY;
        if (!input.LeftMousePressed || _toolbarConsumedClick || !GameViewRect.Contains(x, y) || _toolbar.IsMenuOpen
            || _toolbar.IsOverToolbarUi(x, y, _width) || _worldExplorer.BlocksViewportInput(x, y, _width, _height)
            || _colorPanel.IsOverPanel(x, y, _width, _height) || IsOverEditorChrome(x, y))
        {
            return;
        }

        _toolbarConsumedClick = true;
        if (SelectedGuiElement is { } selected && _guiEditLayout.TryGet(selected.Id, out var selectedEntry) && selectedEntry.Visible
            && HandleAt(ToScreen(selectedEntry.Rect, frame), x, y) is { } handle)
        {
            BeginGuiDrag(selected, handle, selectedEntry.Rect, x, y, selected.Id);
            return;
        }

        var (lx, ly) = ToLayout(x, y, frame);
        if (PickGuiElement(entries, frame.Layout, lx, ly) is { } picked)
        {
            if (_guiSelection.SelectedId != picked.Element.Id)
            {
                Console.WriteLine($"[UI] Selected {picked.Element.Name}");
            }

            var selectionBefore = _guiSelection.SelectedId;
            _guiSelection.SelectedId = picked.Element.Id;
            BeginGuiDrag(picked.Element, GuiHandle.None, picked.Rect, x, y, selectionBefore);
            return;
        }

        if (_guiSelection.SelectedId is not null)
        {
            _guiSelection.SelectedId = null;
            Console.WriteLine("[UI] Selection cleared");
        }
    }

    // The topmost visible element (any type) at a canvas point, inside the canvas.
    private static GuiLayoutEntry? PickGuiElement(IReadOnlyList<GuiLayoutEntry> entries, GuiRect canvas, float x, float y)
    {
        if (!canvas.Contains(x, y))
        {
            return null;
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Visible && entries[i].Rect.Contains(x, y))
            {
                return entries[i];
            }
        }

        return null;
    }

    // The handle (corners first) within reach of a screen point; same reach at every preview zoom.
    private static GuiHandle? HandleAt(GuiRect rect, float x, float y)
    {
        foreach (var (handle, hx, hy) in HandlePoints(rect))
        {
            if (MathF.Abs(x - hx) <= HandleReach && MathF.Abs(y - hy) <= HandleReach)
            {
                return handle;
            }
        }

        return null;
    }

    private static IEnumerable<(GuiHandle Handle, float X, float Y)> HandlePoints(GuiRect r)
    {
        var cx = r.X + r.Width * 0.5f;
        var cy = r.Y + r.Height * 0.5f;
        yield return (GuiHandle.TopLeft, r.X, r.Y);
        yield return (GuiHandle.TopRight, r.Right, r.Y);
        yield return (GuiHandle.BottomLeft, r.X, r.Bottom);
        yield return (GuiHandle.BottomRight, r.Right, r.Bottom);
        yield return (GuiHandle.Top, cx, r.Y);
        yield return (GuiHandle.Bottom, cx, r.Bottom);
        yield return (GuiHandle.Left, r.X, cy);
        yield return (GuiHandle.Right, r.Right, cy);
    }

    private void BeginGuiDrag(GuiElement element, GuiHandle handle, GuiRect rect, float x, float y, int? selectedBefore) =>
        _guiDrag = new GuiDrag(element, handle, x, y, element.Position, element.Size, rect.Width, rect.Height, Gui.Clone(), selectedBefore);

    // Absolute from the drag start (no drift): the mouse delta in canvas pixels goes to the Offsets only.
    private void ApplyGuiDrag(GuiDrag drag, float x, float y, (GuiRect Layout, GuiRect Screen, float Zoom) frame)
    {
        var dx = (x - drag.StartX) / frame.Zoom;
        var dy = (y - drag.StartY) / frame.Zoom;
        var position = drag.StartPosition;
        var size = drag.StartSize;
        if (drag.Handle == GuiHandle.None)
        {
            drag.Element.Position = new GuiDim2(position.X.Scale, position.X.Offset + dx, position.Y.Scale, position.Y.Offset + dy);
            return;
        }

        var anchor = drag.Element.AnchorPoint;
        var (moveX, sizeX) = ResizeAxis(drag.Handle is GuiHandle.TopLeft or GuiHandle.Left or GuiHandle.BottomLeft,
            drag.Handle is GuiHandle.TopRight or GuiHandle.Right or GuiHandle.BottomRight, dx, drag.StartWidth, anchor.X);
        var (moveY, sizeY) = ResizeAxis(drag.Handle is GuiHandle.TopLeft or GuiHandle.Top or GuiHandle.TopRight,
            drag.Handle is GuiHandle.BottomLeft or GuiHandle.Bottom or GuiHandle.BottomRight, dy, drag.StartHeight, anchor.Y);
        drag.Element.Size = new GuiDim2(size.X.Scale, size.X.Offset + sizeX, size.Y.Scale, size.Y.Offset + sizeY);
        drag.Element.Position = new GuiDim2(position.X.Scale, position.X.Offset + moveX, position.Y.Scale, position.Y.Offset + moveY);
    }

    // One axis of a resize: dragging the near edge by d grows the size by -d and moves the anchor point by d·(1 − anchor);
    // the far edge grows it by d and moves the anchor point by d·anchor. The size never goes below 1 px.
    private static (float Move, float Size) ResizeAxis(bool nearEdge, bool farEdge, float delta, float startLength, float anchor)
    {
        if (nearEdge)
        {
            delta = MathF.Min(delta, startLength - MinGuiSize);
            return (delta * (1f - anchor), -delta);
        }

        if (farEdge)
        {
            delta = MathF.Max(delta, MinGuiSize - startLength);
            return (delta * anchor, delta);
        }

        return (0f, 0f);
    }

    // Release: one Move / Resize step when anything changed (a click that only selects records nothing).
    private void CommitGuiDrag()
    {
        if (_guiDrag is not { } drag)
        {
            return;
        }

        _guiDrag = null;
        if (drag.Element.Position == drag.StartPosition && drag.Element.Size == drag.StartSize)
        {
            return;
        }

        _editorHistory.Push(EditorCommand.GuiChange(drag.Handle == GuiHandle.None ? "Move" : "Resize", drag.Element.Name, Gui, drag.Before, Gui.Clone(),
            _guiSelection, drag.SelectedBefore, _guiSelection.SelectedId));
        Console.WriteLine($"[UI] {(drag.Handle == GuiHandle.None ? "Move" : "Resize")} {drag.Element.Name}");
    }

    // Escape: the element exactly as before the drag; nothing recorded.
    private bool CancelGuiDrag()
    {
        if (_guiDrag is not { } drag)
        {
            return false;
        }

        _guiDrag = null;
        drag.Element.Position = drag.StartPosition;
        drag.Element.Size = drag.StartSize;
        return true;
    }

    // ---- Drawing ----

    // The authored GUI (same layout and renderer as Play, no button states), the canvas bounds (and outside a fixed
    // preview, a dimmed margin), then the selection outline and its handles.
    private void RenderGuiEditor(float dpiScale)
    {
        if (!GuiEditorActive || _guiUi is null || _guiFonts is null)
        {
            return;
        }

        if (_guiFonts.AtlasOverflowed)
        {
            _guiFonts.ClearAtlas();
        }

        var frame = GuiPreviewFrame;
        var entries = _guiEditLayout.Update(Gui, frame.Layout);
        IReadOnlyList<GuiLayoutEntry> drawn = entries;
        if (!IsIdentity(frame))
        {
            _guiEditScreen.Clear();
            foreach (var entry in entries)
            {
                _guiEditScreen.Add(entry with { Rect = ToScreen(entry.Rect, frame) });
            }

            drawn = _guiEditScreen;
        }

        _guiRenderer.Draw(_guiUi, drawn, frame.Screen, dpiScale, _width, _height, null, frame.Zoom);

        var ui = _guiUi;
        var canvas = frame.Screen;
        ui.BeginBatch(dpiScale);
        ui.SetClip(null);
        if (!IsIdentity(frame))
        {
            var view = GameViewRect;
            var dim = new Vector3(0f, 0f, 0f);
            ui.DrawRect(view.X, view.Y, view.Width, MathF.Max(0f, canvas.Y - view.Y), dim, 0.55f);
            ui.DrawRect(view.X, canvas.Bottom, view.Width, MathF.Max(0f, view.Bottom - canvas.Bottom), dim, 0.55f);
            ui.DrawRect(view.X, canvas.Y, MathF.Max(0f, canvas.X - view.X), canvas.Height, dim, 0.55f);
            ui.DrawRect(canvas.Right, canvas.Y, MathF.Max(0f, view.Right - canvas.Right), canvas.Height, dim, 0.55f);
        }

        ui.DrawBorderRect(canvas.X, canvas.Y, canvas.Width, canvas.Height, GuiAccent, 1f, 0.6f);
        if (SelectedGuiElement is { } selected && _guiEditLayout.TryGet(selected.Id, out var entryOfSelected) && entryOfSelected.Visible)
        {
            var r = ToScreen(entryOfSelected.Rect, frame);
            ui.DrawBorderRect(r.X - 1f, r.Y - 1f, r.Width + 2f, r.Height + 2f, GuiAccent, 1.5f);
            foreach (var (_, hx, hy) in HandlePoints(r))
            {
                ui.DrawRect(hx - HandleSize * 0.5f, hy - HandleSize * 0.5f, HandleSize, HandleSize, GuiAccent);
                ui.DrawRect(hx - HandleSize * 0.5f + 1.5f, hy - HandleSize * 0.5f + 1.5f, HandleSize - 3f, HandleSize - 3f, new Vector3(1f, 1f, 1f));
            }
        }

        ui.Flush(_width, _height);
    }

    // A drag in progress: the element, which handle (None: moving), where it started, and the GUI before it.
    private sealed record GuiDrag(
        GuiElement Element,
        GuiHandle Handle,
        float StartX,
        float StartY,
        GuiDim2 StartPosition,
        GuiDim2 StartSize,
        float StartWidth,
        float StartHeight,
        GuiDocument Before,
        int? SelectedBefore);
}
