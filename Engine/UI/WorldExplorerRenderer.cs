using System.Globalization;
using System.Text;
using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.Text;
using Wisko.Engine.World;

namespace Wisko.Engine.UI;

/// <summary>
/// What a click on the Workspace list hit: a Part's row, its disclosure arrow, one of its scripts, an environment
/// object (the Baseplate), a group's row or a group's arrow.
/// </summary>
public enum ExplorerRowKind
{
    Part,
    Disclosure,
    Script,
    Environment,
    Group,
    GroupDisclosure
}

/// <summary>
/// A Workspace list hit; <see cref="ScriptIndex"/> is the script's place in the Part's list (Script rows only). An
/// Environment hit has no Part (null) and <see cref="EnvironmentIndex"/> says which environment object; a Group or
/// GroupDisclosure hit has no Part and names its <see cref="Group"/>.
/// </summary>
public readonly record struct ExplorerRowHit(ExplorerRowKind Kind, Part? Part, int ScriptIndex, int EnvironmentIndex = -1, PartGroup? Group = null);

/// <summary>
/// The World panel: the Workspace list and the Scripts section for the selected Part.
/// <para>
/// The list: the environment's objects first (the Baseplate), then the Workspace as a tree: groups (an arrow, right when
/// collapsed and down when expanded, with their Parts and inner groups indented under them) and Parts, in the order the
/// Workspace first reaches them. A Part with scripts has its own arrow that lists them under it. Which groups and Parts
/// are expanded is editor UI state only (never saved). An environment object can be selected to see its details (it is
/// not a Part: it cannot be moved, renamed or coloured); a world can delete it (Delete or its context menu).
/// </para>
/// <para>
/// Groups are editor organisation (see <see cref="PartGroup"/>). Rows can be dragged onto a group row to move into it,
/// onto a Part row to join that Part's group, or onto the empty space below the list to go back to the Workspace; the
/// context menu offers Rename, Group, Ungroup and Remove from Group (the editor decides which and acts on them).
/// </para>
/// </summary>
public sealed class WorldExplorerRenderer : IDisposable
{
    public const float SidebarWidth = 228f;
    private const float ItemHeight = 26f;
    private const float TitleHeight = 34f;
    private const float SectionHeaderHeight = 22f;
    private const float SidePadding = 10f;
    private const float TextScale = 1.85f;
    private const float SmallTextScale = 1.65f;
    private const float ContextMenuWidth = 150f;
    private const float ContextMenuItemHeight = 28f;
    private const int MaxRenameLength = 48;

    // Workspace list: the disclosure arrow's column at the start of every row (its click target is the column plus the
    // side padding before it), how far script rows are indented under their Part, and each group level's indent.
    private const float DisclosureWidth = 18f;
    private const float DisclosureCaretSize = 8f;
    private const float ScriptIndent = 18f;
    private const float ScriptIconRoom = 18f;
    private const float GroupIndent = 14f;

    // Scripts section, pinned to the bottom of the sidebar.
    private const float ScriptsHeaderHeight = 30f;
    private const float ScriptRowHeight = 26f;
    private const float ScriptButtonsHeight = 34f;
    private const float ScriptButtonHeight = 26f;
    private const float ScriptButtonGap = 6f;
    private const float ScriptsBottomPadding = 8f;
    private const float DetachButtonSize = 18f;
    private const int MaxScriptRows = 8;

    // The Rotation fields of the selected Parts, right above the Scripts section.
    private const float RotationTitleHeight = 28f;
    private const float RotationFieldHeight = 24f;
    private const float RotationFieldGap = 6f;
    private const float RotationBlockHeight = RotationTitleHeight + RotationFieldHeight + 8f;

    private IUiRenderer _screenUi = null!;
    private bool _isVisible;

    // Workspace list scroll (px from the top of the list).
    private float _listScroll;
    private bool _contextMenuOpen;
    private IReadOnlyList<string> _contextItems = [];
    private float _contextMenuX;
    private float _contextMenuY;
    private bool _renameActive;
    private Part? _renamePart;
    private PartGroup? _renameGroup;
    private string _renameText = string.Empty;

    // A row being dragged (Parts or a group) and where it would land: a group, or the Workspace (null) when valid.
    private bool _dragging;
    private string _dragLabel = string.Empty;
    private PartGroup? _draggedGroup;
    private bool _dropValid;
    private PartGroup? _dropTarget;
    private int _dropRow = -1;
    private float _dragX;
    private float _dragY;

    // Parts whose scripts are listed under them, expanded groups, and the list's rows (rebuilt whenever it is used).
    private readonly HashSet<Part> _expanded = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PartGroup> _expandedGroups = new(ReferenceEqualityComparer.Instance);
    private readonly List<ExplorerRow> _rows = [];

    // The selected environment object's index in Environment (-1: none). Editor UI state only.
    private int _selectedEnvironment = -1;

    /// <summary>The environment's objects the editor draws itself (the Baseplate), listed before the Parts.</summary>
    public IReadOnlyList<PhysicsEnvironmentBox> Environment { get; set; } = [];

    /// <summary>The environment object selected in the list (its details show instead of scripts), or null.</summary>
    public PhysicsEnvironmentBox? SelectedEnvironment =>
        _selectedEnvironment >= 0 && _selectedEnvironment < Environment.Count ? Environment[_selectedEnvironment] : null;

    /// <summary>Selects an environment object by its index in <see cref="Environment"/> (-1: none).</summary>
    public void SelectEnvironment(int index) => _selectedEnvironment = index >= 0 && index < Environment.Count ? index : -1;

    /// <summary>The group whose row is selected (its Parts are the selection), or null. Editor UI state only.</summary>
    public PartGroup? SelectedGroup { get; set; }

    public bool IsVisible => _isVisible;
    public bool IsContextMenuOpen => _contextMenuOpen;
    public bool IsRenaming => _renameActive;
    public bool IsDragging => _dragging;

    public Action<Part, string, string>? RenameCommitted { get; set; }

    /// <summary>A group's new name was typed (the editor applies it, checks it and records the undo step).</summary>
    public Action<PartGroup, string>? GroupRenameCommitted { get; set; }

    /// <summary>How a script row is labelled (the editor's script label, as in the Scripts section).</summary>
    public Func<ScriptReference, string> ScriptLabelOf { get; set; } = reference => reference.AssetPath;

    /// <summary>True when the Part's scripts are listed under it (it has at least one, and its arrow is open).</summary>
    public bool IsExpanded(Part part) => part.Scripts.Count > 0 && _expanded.Contains(part);

    /// <summary>Opens or closes a Part's script rows (no effect on the selection or the scene).</summary>
    public void SetExpanded(Part part, bool expanded)
    {
        if (expanded)
        {
            _expanded.Add(part);
        }
        else
        {
            _expanded.Remove(part);
        }
    }

    /// <summary>True when the group's contents are listed under it.</summary>
    public bool IsGroupExpanded(PartGroup group) => _expandedGroups.Contains(group);

    /// <summary>Opens or closes a group's rows (no effect on the selection or the scene).</summary>
    public void SetGroupExpanded(PartGroup group, bool expanded)
    {
        if (expanded)
        {
            _expandedGroups.Add(group);
        }
        else
        {
            _expandedGroups.Remove(group);
        }
    }

    internal void Initialize(IUiBackend ui) => _screenUi = ui.CreateRenderer();

    public void Toggle() => _isVisible = !_isVisible;

    public void Close() => _isVisible = false;

    public void SetVisible(bool visible) => _isVisible = visible;

    public bool ShouldSuppressRightMouseLook(float mouseX, float mouseY, int screenWidth, int screenHeight)
    {
        if (_renameActive || _dragging)
        {
            return true;
        }

        if (!_isVisible)
        {
            return false;
        }

        if (IsOverSidebar(mouseX, mouseY, screenWidth, screenHeight))
        {
            return true;
        }

        return _contextMenuOpen && IsOverContextMenu(mouseX, mouseY);
    }

    public bool TryConsumeEscape()
    {
        if (_dragging)
        {
            EndDrag();
            return true;
        }

        if (_renameActive)
        {
            CancelRename();
            return true;
        }

        if (_contextMenuOpen)
        {
            CloseContextMenu();
            return true;
        }

        return false;
    }

    public bool HandleRenameKeyboard(InputManager input)
    {
        if (!_renameActive || (_renamePart is null && _renameGroup is null))
        {
            return false;
        }

        if (input.EscapePressed)
        {
            CancelRename();
            return true;
        }

        if (input.EnterPressed)
        {
            ConfirmRename();
            return true;
        }

        if (input.TextBackspacePressed && _renameText.Length > 0)
        {
            // A character outside the Basic Multilingual Plane is two UTF-16 units: remove both.
            var cut = _renameText.Length >= 2 && char.IsLowSurrogate(_renameText[^1]) && char.IsHighSurrogate(_renameText[^2]) ? 2 : 1;
            _renameText = _renameText[..^cut];
            return true;
        }

        // The OS's text input: the characters the keyboard layout produces (AZERTY, accents, dead keys, other scripts),
        // never physical key positions.
        var changed = false;
        var typed = input.TextInputCharacters;
        for (var i = 0; i < typed.Count; i++)
        {
            Rune rune;
            if (char.IsHighSurrogate(typed[i]) && i + 1 < typed.Count && char.IsLowSurrogate(typed[i + 1]))
            {
                rune = new Rune(typed[i], typed[i + 1]);
                i++;
            }
            else if (!Rune.TryCreate(typed[i], out rune))
            {
                continue;
            }

            if (_renameText.Length + rune.Utf16SequenceLength > MaxRenameLength)
            {
                break;
            }

            if (IsAllowedRenameCharacter(rune))
            {
                _renameText += rune.ToString();
                changed = true;
            }
        }

        return changed || input.TextBackspacePressed || input.EnterPressed || input.EscapePressed;
    }

    /// <summary>The name being typed (diagnostics and tests).</summary>
    internal string RenameText => _renameText;

    /// <summary>Opens the context menu with these items (in order) at the point, kept inside the sidebar.</summary>
    public void OpenContextMenu(IReadOnlyList<string> items, float mouseX, float mouseY, int screenWidth, int screenHeight)
    {
        if (items.Count == 0)
        {
            CloseContextMenu();
            return;
        }

        _contextItems = items;
        _contextMenuOpen = true;
        var sidebar = GetSidebarRect(screenWidth, screenHeight);
        var height = items.Count * ContextMenuItemHeight;
        _contextMenuX = global::System.Math.Clamp(mouseX, sidebar.X + SidePadding, sidebar.X + sidebar.Width - ContextMenuWidth - SidePadding);
        _contextMenuY = global::System.Math.Clamp(mouseY, sidebar.Y + TitleHeight, sidebar.Y + sidebar.Height - height - 4f);
    }

    public void CloseContextMenu()
    {
        _contextMenuOpen = false;
        _contextItems = [];
    }

    /// <summary>The context menu item under the point (and closes the menu), or false when the point is not on one.</summary>
    public bool TryHitContextMenu(float mouseX, float mouseY, out string item)
    {
        item = string.Empty;
        if (!IsOverContextMenu(mouseX, mouseY))
        {
            return false;
        }

        var index = (int)((mouseY - _contextMenuY) / ContextMenuItemHeight);
        if (index < 0 || index >= _contextItems.Count)
        {
            return false;
        }

        item = _contextItems[index];
        CloseContextMenu();
        return true;
    }

    public bool IsOverSidebar(float mouseX, float mouseY, int screenWidth, int screenHeight)
    {
        if (!_isVisible)
        {
            return false;
        }

        var rect = GetSidebarRect(screenWidth, screenHeight);
        return mouseX >= rect.X
            && mouseX <= rect.X + rect.Width
            && mouseY >= rect.Y
            && mouseY <= rect.Y + rect.Height;
    }

    public bool IsOverContextMenu(float mouseX, float mouseY)
    {
        if (!_contextMenuOpen)
        {
            return false;
        }

        return mouseX >= _contextMenuX
            && mouseX <= _contextMenuX + ContextMenuWidth
            && mouseY >= _contextMenuY
            && mouseY <= _contextMenuY + _contextItems.Count * ContextMenuItemHeight;
    }

    public bool BlocksViewportInput(float mouseX, float mouseY, int screenWidth, int screenHeight)
    {
        if (_renameActive || _dragging)
        {
            return true;
        }

        if (IsOverSidebar(mouseX, mouseY, screenWidth, screenHeight))
        {
            return true;
        }

        return _contextMenuOpen && IsOverContextMenu(mouseX, mouseY);
    }

    /// <summary>The Part whose own row (arrow included) is under the point; script, group and environment rows are not Parts.</summary>
    public bool TryHitPart(
        float mouseX,
        float mouseY,
        int screenWidth,
        int screenHeight,
        Workspace workspace,
        ScriptsPanelView scripts,
        out Part? part)
    {
        part = null;
        if (!TryHitRow(mouseX, mouseY, screenWidth, screenHeight, workspace, scripts, out var hit)
            || hit.Kind is not (ExplorerRowKind.Part or ExplorerRowKind.Disclosure))
        {
            return false;
        }

        part = hit.Part;
        return true;
    }

    /// <summary>
    /// The Workspace list row under the point: a Part (its name), its disclosure arrow (only a Part with scripts has
    /// one), one of its script rows, a group or a group's arrow, or an environment object.
    /// </summary>
    public bool TryHitRow(
        float mouseX,
        float mouseY,
        int screenWidth,
        int screenHeight,
        Workspace workspace,
        ScriptsPanelView scripts,
        out ExplorerRowHit hit)
    {
        hit = default;
        if (!TryRowIndex(mouseX, mouseY, screenWidth, screenHeight, workspace, scripts, out var rowIndex))
        {
            return false;
        }

        var sidebar = GetSidebarRect(screenWidth, screenHeight);
        var row = _rows[rowIndex];
        var arrowEdge = sidebar.X + SidePadding + row.Depth * GroupIndent + DisclosureWidth + 2f;
        if (row.Group is { } group)
        {
            hit = new ExplorerRowHit(mouseX < arrowEdge ? ExplorerRowKind.GroupDisclosure : ExplorerRowKind.Group, null, -1, -1, group);
        }
        else if (row.Part is null)
        {
            hit = new ExplorerRowHit(ExplorerRowKind.Environment, null, -1, row.EnvironmentIndex);
        }
        else if (row.ScriptIndex >= 0)
        {
            hit = new ExplorerRowHit(ExplorerRowKind.Script, row.Part, row.ScriptIndex);
        }
        else if (row.Part.Scripts.Count > 0 && mouseX < arrowEdge)
        {
            hit = new ExplorerRowHit(ExplorerRowKind.Disclosure, row.Part, -1);
        }
        else
        {
            hit = new ExplorerRowHit(ExplorerRowKind.Part, row.Part, -1);
        }

        return true;
    }

    /// <summary>
    /// The Parts and groups whose rows lie between two Part rows of the list as it is shown now (inclusive), for a
    /// Shift+click range: the visible Part rows from one to the other.
    /// </summary>
    public List<Part> PartsBetween(Part from, Part to, Workspace workspace)
    {
        BuildRows(workspace);
        var first = _rows.FindIndex(row => row.ScriptIndex < 0 && ReferenceEquals(row.Part, from));
        var last = _rows.FindIndex(row => row.ScriptIndex < 0 && ReferenceEquals(row.Part, to));
        if (first < 0 || last < 0)
        {
            return [to];
        }

        if (first > last)
        {
            (first, last) = (last, first);
        }

        var parts = new List<Part>();
        for (var i = first; i <= last; i++)
        {
            if (_rows[i] is { Part: { } part, ScriptIndex: < 0, Group: null })
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    /// <summary>Starts dragging rows: <paramref name="group"/> (a whole group) or the selected Parts, labelled for the cursor.</summary>
    public void BeginDrag(PartGroup? group, string label)
    {
        _dragging = true;
        _draggedGroup = group;
        _dragLabel = label;
        _dropValid = false;
        _dropTarget = null;
        _dropRow = -1;
    }

    /// <summary>
    /// The drag moved: where it would land now. Over a group row: into that group; over a Part row: that Part's group;
    /// over the empty space below the rows: the Workspace. A group can never land inside itself.
    /// </summary>
    public void UpdateDrag(float mouseX, float mouseY, int screenWidth, int screenHeight, Workspace workspace, ScriptsPanelView scripts)
    {
        _dragX = mouseX;
        _dragY = mouseY;
        _dropValid = false;
        _dropRow = -1;
        if (!IsOverSidebar(mouseX, mouseY, screenWidth, screenHeight))
        {
            return;
        }

        var list = GetListRect(GetSidebarRect(screenWidth, screenHeight), scripts);
        if (mouseY < list.Y || mouseY > list.Y + list.Height)
        {
            return;
        }

        BuildRows(workspace);
        var rowIndex = (int)((mouseY - list.Y + _listScroll) / ItemHeight);
        PartGroup? target;
        if (rowIndex >= _rows.Count)
        {
            target = null; // below the rows: back to the Workspace
        }
        else if (rowIndex < 0)
        {
            return;
        }
        else
        {
            var row = _rows[rowIndex];
            target = row.Group ?? row.Part?.Group;
            if (row.Group is null && row.Part is null)
            {
                target = null; // an environment row: the Workspace
            }

            _dropRow = rowIndex;
        }

        _dropValid = _draggedGroup is null || target is null || !target.IsWithin(_draggedGroup);
        _dropTarget = target;
    }

    /// <summary>Where the drag would land (a group, or null for the Workspace); false when it cannot land here.</summary>
    public bool TryGetDropTarget(out PartGroup? target)
    {
        target = _dropTarget;
        return _dragging && _dropValid;
    }

    public void EndDrag()
    {
        _dragging = false;
        _draggedGroup = null;
        _dropValid = false;
        _dropTarget = null;
        _dropRow = -1;
    }

    /// <summary>True over the scrollable Workspace list (mouse wheel scrolls it there, not the camera).</summary>
    public bool IsOverList(float mouseX, float mouseY, int screenWidth, int screenHeight, ScriptsPanelView scripts)
    {
        if (!_isVisible || !IsOverSidebar(mouseX, mouseY, screenWidth, screenHeight))
        {
            return false;
        }

        var list = GetListRect(GetSidebarRect(screenWidth, screenHeight), scripts);
        return mouseY >= list.Y && mouseY <= list.Y + list.Height;
    }

    /// <summary>Scrolls the Workspace list by <paramref name="delta"/> px (clamped to its content).</summary>
    public void ScrollList(float delta, int screenWidth, int screenHeight, ScriptsPanelView scripts, Workspace workspace)
    {
        var list = GetListRect(GetSidebarRect(screenWidth, screenHeight), scripts);
        BuildRows(workspace);
        _listScroll = global::System.Math.Clamp(_listScroll + delta, 0f, MaxListScroll(list));
    }

    /// <summary>
    /// Scrolls just enough to show the part's row (e.g. after selecting it in the viewport), opening the groups it is in.
    /// </summary>
    public void RevealPart(Part part, int screenWidth, int screenHeight, ScriptsPanelView scripts, Workspace workspace)
    {
        for (var group = part.Group; group is not null; group = group.Parent)
        {
            _expandedGroups.Add(group);
        }

        BuildRows(workspace);
        var index = -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].ScriptIndex < 0 && ReferenceEquals(_rows[i].Part, part))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var list = GetListRect(GetSidebarRect(screenWidth, screenHeight), scripts);
        var top = index * ItemHeight;
        if (top < _listScroll)
        {
            _listScroll = top;
        }
        else if (top + ItemHeight > _listScroll + list.Height)
        {
            _listScroll = top + ItemHeight - list.Height;
        }

        _listScroll = global::System.Math.Clamp(_listScroll, 0f, MaxListScroll(list));
    }

    /// <summary>
    /// A left click on the Scripts section. True when the point is over the section (the click is the panel's even when
    /// it hits no control); <paramref name="hit"/> says which control, if any.
    /// </summary>
    public bool TryHitScriptsPanel(float mouseX, float mouseY, int screenWidth, int screenHeight, ScriptsPanelView scripts, out ScriptsPanelHit hit)
    {
        hit = new ScriptsPanelHit(ScriptsPanelAction.None, -1);
        if (!_isVisible || _renameActive || !IsOverSidebar(mouseX, mouseY, screenWidth, screenHeight))
        {
            return false;
        }

        var sidebar = GetSidebarRect(screenWidth, screenHeight);
        var panel = GetScriptsPanelRect(sidebar, scripts);
        var rotation = GetRotationRect(sidebar, scripts);
        if (mouseY < rotation.Y)
        {
            return false;
        }

        // The Rotation fields above the scripts (the block is the panel's even between the fields).
        if (scripts.Rotation is not null && mouseY < panel.Y)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                if (RotationFieldRect(rotation, axis).Contains(mouseX, mouseY))
                {
                    hit = new ScriptsPanelHit(ScriptsPanelAction.RotationField, axis);
                    break;
                }
            }

            return true;
        }

        if (scripts.Target is null)
        {
            return true;
        }

        for (var i = 0; i < VisibleScriptRows(scripts); i++)
        {
            if (!scripts.ReadOnly && GetDetachRect(panel, i).Contains(mouseX, mouseY))
            {
                hit = new ScriptsPanelHit(ScriptsPanelAction.Detach, i);
                return true;
            }

            var rowY = panel.Y + ScriptsHeaderHeight + i * ScriptRowHeight;
            if (mouseY >= rowY && mouseY < rowY + ScriptRowHeight - 4f && mouseX >= panel.X + SidePadding && mouseX <= panel.X + panel.Width - SidePadding)
            {
                hit = new ScriptsPanelHit(ScriptsPanelAction.Row, i);
                return true;
            }
        }

        GetScriptButtonRects(panel, out var newRect, out var attachRect);
        if (newRect.Contains(mouseX, mouseY))
        {
            hit = new ScriptsPanelHit(ScriptsPanelAction.NewScript, -1);
        }
        else if (attachRect.Contains(mouseX, mouseY))
        {
            hit = new ScriptsPanelHit(ScriptsPanelAction.AttachScript, -1);
        }

        return true;
    }

    /// <param name="activeScriptPath">The script shown in the code editor now (its rows are marked), or null.</param>
    public void Render(int screenWidth, int screenHeight, float dpiScale, Workspace workspace, IReadOnlyList<Part> selectedParts, ScriptsPanelView scripts,
        string? activeScriptPath = null)
    {
        if (!_isVisible)
        {
            return;
        }

        var sidebar = GetSidebarRect(screenWidth, screenHeight);
        var barColor = new Vector3(0.12f, 0.13f, 0.15f);
        var separator = new Vector3(0.22f, 0.24f, 0.28f);
        var titleText = new Vector3(0.92f, 0.94f, 0.98f);
        var sectionText = new Vector3(0.70f, 0.74f, 0.80f);
        var itemText = new Vector3(0.82f, 0.84f, 0.88f);
        var shapeText = new Vector3(0.58f, 0.62f, 0.68f);
        var itemInactive = new Vector3(0.18f, 0.19f, 0.22f);
        var groupFill = new Vector3(0.16f, 0.18f, 0.22f);
        var itemSelected = new Vector3(0.20f, 0.42f, 0.78f);
        var itemSelectedBorder = new Vector3(0.55f, 0.75f, 1.0f);
        var editField = new Vector3(0.10f, 0.11f, 0.14f);
        var editBorder = new Vector3(0.45f, 0.62f, 0.95f);
        var menuBg = new Vector3(0.16f, 0.17f, 0.20f);
        var menuBorder = new Vector3(0.30f, 0.33f, 0.38f);
        var dropColor = new Vector3(0.36f, 0.80f, 0.52f);
        _screenUi.BeginBatch(dpiScale);

        _screenUi.DrawRect(sidebar.X, sidebar.Y, sidebar.Width, sidebar.Height, barColor);
        _screenUi.DrawRect(sidebar.X, sidebar.Y, 1f, sidebar.Height, separator);

        DrawString(sidebar.X + SidePadding, sidebar.Y + 10f, "World", titleText, TextScale, FontWeight.SemiBold);
        _screenUi.DrawRect(
            sidebar.X + SidePadding,
            sidebar.Y + TitleHeight,
            sidebar.Width - SidePadding * 2f,
            1f,
            separator);

        DrawString(sidebar.X + SidePadding, sidebar.Y + TitleHeight + 6f, "Workspace", sectionText, SmallTextScale);

        var list = GetListRect(sidebar, scripts);
        BuildRows(workspace);
        _listScroll = global::System.Math.Clamp(_listScroll, 0f, MaxListScroll(list));
        _screenUi.SetClip(new UiClip(list.X, list.Y, list.Width, list.Height));
        var firstVisible = (int)(_listScroll / ItemHeight);
        var lastVisible = global::System.Math.Min(_rows.Count - 1, (int)((_listScroll + list.Height) / ItemHeight));
        for (var i = firstVisible; i <= lastVisible; i++)
        {
            var row = _rows[i];
            var itemY = list.Y + i * ItemHeight - _listScroll;
            var indent = row.Depth * GroupIndent;
            var itemRectX = sidebar.X + SidePadding + indent;
            var itemRectW = sidebar.Width - SidePadding * 2f - indent;
            var itemRectH = ItemHeight - 4f;
            var isDropRow = _dragging && _dropValid && i == _dropRow && row.Group is not null;

            if (row.Group is { } group)
            {
                var groupSelected = ReferenceEquals(SelectedGroup, group);
                var groupEditing = _renameActive && ReferenceEquals(group, _renameGroup);
                if (groupEditing)
                {
                    DrawRenameField(itemRectX, itemY, itemRectW, itemRectH, editField, editBorder, titleText);
                    continue;
                }

                if (groupSelected)
                {
                    _screenUi.DrawRect(itemRectX - 1f, itemY - 1f, itemRectW + 2f, itemRectH + 2f, itemSelectedBorder);
                    _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, itemSelected);
                }
                else
                {
                    _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, groupFill);
                }

                if (isDropRow)
                {
                    _screenUi.DrawBorderRect(itemRectX - 1f, itemY - 1f, itemRectW + 2f, itemRectH + 2f, dropColor, 2f);
                }

                var groupCaretX = itemRectX + DisclosureWidth * 0.5f + 1f;
                var groupCaretY = itemY + itemRectH * 0.5f;
                var groupCaretColor = groupSelected ? titleText : sectionText;
                if (_expandedGroups.Contains(group))
                {
                    UiIcons.DrawCaretDown(_screenUi, groupCaretX, groupCaretY, DisclosureCaretSize, groupCaretColor);
                }
                else
                {
                    UiIcons.DrawCaretRight(_screenUi, groupCaretX, groupCaretY, DisclosureCaretSize, groupCaretColor);
                }

                const string groupKind = "Group";
                var groupKindX = itemRectX + itemRectW - MeasureTextWidth(groupKind, SmallTextScale) - 6f;
                var groupNameX = itemRectX + DisclosureWidth + 2f;
                DrawString(groupNameX, itemY + 5f, FitText(group.Name, groupKindX - 8f - groupNameX, SmallTextScale), groupSelected ? titleText : itemText,
                    SmallTextScale, FontWeight.SemiBold);
                DrawString(groupKindX, itemY + 5f, groupKind, groupSelected ? titleText : shapeText, SmallTextScale);
                continue;
            }

            if (row.Part is not { } part)
            {
                // An environment object: its name and "Environment", selectable like a Part (deletable, not editable).
                var environmentSelected = row.EnvironmentIndex == _selectedEnvironment;
                if (environmentSelected)
                {
                    _screenUi.DrawRect(itemRectX - 1f, itemY - 1f, itemRectW + 2f, itemRectH + 2f, itemSelectedBorder);
                    _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, itemSelected);
                }
                else
                {
                    _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, itemInactive);
                }

                const string environmentKind = "Environment";
                var environmentKindX = itemRectX + itemRectW - MeasureTextWidth(environmentKind, SmallTextScale) - 6f;
                var environmentNameX = itemRectX + DisclosureWidth + 2f;
                DrawString(environmentNameX, itemY + 5f, FitText(Environment[row.EnvironmentIndex].Name, environmentKindX - 8f - environmentNameX, SmallTextScale),
                    environmentSelected ? titleText : itemText, SmallTextScale);
                DrawString(environmentKindX, itemY + 5f, environmentKind, environmentSelected ? titleText : shapeText, SmallTextScale);
                continue;
            }

            if (row.ScriptIndex >= 0)
            {
                var reference = part.Scripts[row.ScriptIndex];
                var isActive = activeScriptPath is not null
                    && string.Equals(reference.AssetPath, activeScriptPath, StringComparison.OrdinalIgnoreCase);
                DrawScriptRow(itemRectX + ScriptIndent, itemY, itemRectW - ScriptIndent, itemRectH, ScriptLabelOf(reference), isActive,
                    itemText, shapeText, itemSelectedBorder);
                continue;
            }

            var isSelected = selectedParts.Contains(part);
            var isEditing = _renameActive && ReferenceEquals(part, _renamePart);

            if (isEditing)
            {
                DrawRenameField(itemRectX, itemY, itemRectW, itemRectH, editField, editBorder, titleText);
                continue;
            }

            if (isSelected)
            {
                _screenUi.DrawRect(itemRectX - 1f, itemY - 1f, itemRectW + 2f, itemRectH + 2f, itemSelectedBorder);
                _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, itemSelected);
            }
            else
            {
                _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, itemInactive);
            }

            // The arrow column comes first on every row, so names line up whether or not a Part has scripts.
            if (part.Scripts.Count > 0)
            {
                var caretX = itemRectX + DisclosureWidth * 0.5f + 1f;
                var caretY = itemY + itemRectH * 0.5f;
                var caretColor = isSelected ? titleText : sectionText;
                if (_expanded.Contains(part))
                {
                    UiIcons.DrawCaretDown(_screenUi, caretX, caretY, DisclosureCaretSize, caretColor);
                }
                else
                {
                    UiIcons.DrawCaretRight(_screenUi, caretX, caretY, DisclosureCaretSize, caretColor);
                }
            }

            var kind = GetKindLabel(part);
            var kindX = itemRectX + itemRectW - MeasureTextWidth(kind, SmallTextScale) - 6f;
            var nameX = itemRectX + DisclosureWidth + 2f;
            DrawString(nameX, itemY + 5f, FitText(part.Name, kindX - 8f - nameX, SmallTextScale), isSelected ? titleText : itemText, SmallTextScale);
            DrawString(kindX, itemY + 5f, kind, isSelected ? titleText : shapeText, SmallTextScale);
        }

        // Dropping below the rows sends the dragged rows back to the Workspace: a line where they would go.
        if (_dragging && _dropValid && _dropRow < 0)
        {
            var endY = list.Y + _rows.Count * ItemHeight - _listScroll;
            _screenUi.DrawRect(sidebar.X + SidePadding, MathF.Min(endY, list.Y + list.Height - 3f), sidebar.Width - SidePadding * 2f, 2f, dropColor);
        }

        _screenUi.SetClip(null);
        var maxScroll = MaxListScroll(list);
        if (maxScroll > 0f)
        {
            // Scrollbar thumb at the sidebar's right edge.
            var thumb = MathF.Max(18f, list.Height * list.Height / (_rows.Count * ItemHeight));
            var thumbY = list.Y + (list.Height - thumb) * (_listScroll / maxScroll);
            _screenUi.DrawRect(list.X + list.Width - 5f, thumbY, 3f, thumb, separator);
        }

        RenderRotationFields(sidebar, scripts, barColor, separator, sectionText, itemText, shapeText);
        RenderScriptsPanel(sidebar, scripts, barColor, separator, sectionText, itemText, shapeText, itemInactive);

        if (_contextMenuOpen)
        {
            var height = _contextItems.Count * ContextMenuItemHeight;
            _screenUi.DrawRect(_contextMenuX - 1f, _contextMenuY - 1f, ContextMenuWidth + 2f, height + 2f, menuBorder);
            _screenUi.DrawRect(_contextMenuX, _contextMenuY, ContextMenuWidth, height, menuBg);
            for (var i = 0; i < _contextItems.Count; i++)
            {
                DrawString(_contextMenuX + 10f, _contextMenuY + i * ContextMenuItemHeight + 7f, _contextItems[i], titleText, SmallTextScale);
            }
        }

        // The dragged rows follow the cursor as a small label.
        if (_dragging && _dragLabel.Length > 0)
        {
            var labelWidth = MeasureTextWidth(_dragLabel, SmallTextScale) + 16f;
            _screenUi.DrawRect(_dragX + 12f, _dragY + 8f, labelWidth, ItemHeight - 4f, _dropValid ? itemSelected : menuBg, 0.92f);
            DrawString(_dragX + 20f, _dragY + 13f, _dragLabel, titleText, SmallTextScale);
        }

        _screenUi.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _screenUi.Dispose();

    /// <summary>Starts typing a new name for the Part's row.</summary>
    public void BeginRename(Part part)
    {
        _renameActive = true;
        _renamePart = part;
        _renameGroup = null;
        _renameText = part.Name;
    }

    /// <summary>Starts typing a new name for the group's row.</summary>
    public void BeginRenameGroup(PartGroup group)
    {
        _renameActive = true;
        _renameGroup = group;
        _renamePart = null;
        _renameText = group.Name;
    }

    private void DrawRenameField(float x, float y, float width, float height, Vector3 field, Vector3 border, Vector3 text)
    {
        _screenUi.DrawRect(x - 1f, y - 1f, width + 2f, height + 2f, border);
        _screenUi.DrawRect(x, y, width, height, field);
        DrawString(x + 6f, y + 5f, _renameText, text, SmallTextScale);
        var caretX = x + 6f + MeasureTextWidth(_renameText, SmallTextScale) + 1f;
        _screenUi.DrawRect(caretX, y + 6f, 2f, height - 8f, text);
    }

    private void RenderScriptsPanel(
        UiRect sidebar,
        ScriptsPanelView scripts,
        Vector3 barColor,
        Vector3 separator,
        Vector3 sectionText,
        Vector3 itemText,
        Vector3 mutedText,
        Vector3 rowFill)
    {
        var missingText = new Vector3(0.93f, 0.72f, 0.38f);
        var faultText = new Vector3(0.95f, 0.45f, 0.42f);
        var runningDot = new Vector3(0.36f, 0.80f, 0.52f);
        var buttonFill = new Vector3(0.24f, 0.26f, 0.30f);
        var buttonText = new Vector3(0.88f, 0.90f, 0.94f);
        var disabledFill = new Vector3(0.18f, 0.19f, 0.22f);
        var disabledText = new Vector3(0.45f, 0.47f, 0.52f);
        var crossColor = new Vector3(0.62f, 0.65f, 0.70f);

        var panel = GetScriptsPanelRect(sidebar, scripts);
        var left = panel.X + SidePadding;
        var width = panel.Width - SidePadding * 2f;

        // Opaque background: long part lists end under the section instead of showing through it.
        _screenUi.DrawRect(panel.X + 1f, panel.Y, panel.Width - 1f, panel.Height, barColor);
        _screenUi.DrawRect(left, panel.Y, width, 1f, separator);

        var rowsTop = panel.Y + ScriptsHeaderHeight;
        if (scripts.Environment is { } environment)
        {
            // An environment object's facts instead of scripts: read-only lines, no buttons.
            DrawString(left, panel.Y + 8f, FitText(environment.Title, width, SmallTextScale), sectionText, SmallTextScale);
            for (var i = 0; i < environment.Lines.Count; i++)
            {
                DrawString(left, rowsTop + i * ScriptRowHeight + 5f, FitText(environment.Lines[i], width, SmallTextScale), i == 0 ? itemText : mutedText, SmallTextScale);
            }

            return;
        }

        DrawString(left, panel.Y + 8f, "Scripts", sectionText, SmallTextScale);
        if (scripts.Target is not { } target)
        {
            DrawString(left, rowsTop + 5f, FitText(scripts.Hint ?? string.Empty, width, SmallTextScale), mutedText, SmallTextScale);
            return;
        }

        var headerLabelWidth = MeasureTextWidth("Scripts", SmallTextScale) + 10f;
        var targetName = FitText(target.Name, width - headerLabelWidth, SmallTextScale);
        DrawString(left + width - MeasureTextWidth(targetName, SmallTextScale), panel.Y + 8f, targetName, mutedText, SmallTextScale);

        if (scripts.Rows.Count == 0)
        {
            DrawString(left, rowsTop + 5f, "No scripts attached", mutedText, SmallTextScale);
        }

        var visibleRows = VisibleScriptRows(scripts);
        for (var i = 0; i < visibleRows; i++)
        {
            var row = scripts.Rows[i];
            var rowY = rowsTop + i * ScriptRowHeight;
            var rowHeight = ScriptRowHeight - 4f;
            _screenUi.DrawRect(left, rowY, width, rowHeight, rowFill);

            var (tag, tagColor) = row.Status switch
            {
                ScriptRowStatus.Missing => ("missing", missingText),
                ScriptRowStatus.Faulted => ("error", faultText),
                _ => (string.Empty, mutedText)
            };

            // Right side: the remove button (Edit) or the run state (Play), then an optional tag.
            var rightEdge = left + width - 6f;
            if (!scripts.ReadOnly)
            {
                var detach = GetDetachRect(panel, i);
                UiIcons.DrawCross(_screenUi, detach.X + detach.Width * 0.5f, detach.Y + detach.Height * 0.5f, 8f, crossColor);
                rightEdge = detach.X - 4f;
            }
            else if (row.Status == ScriptRowStatus.Running)
            {
                UiIcons.FillCircle(_screenUi, rightEdge - 4f, rowY + rowHeight * 0.5f, 3.5f, runningDot);
                rightEdge -= 14f;
            }

            if (tag.Length > 0)
            {
                var tagWidth = MeasureTextWidth(tag, SmallTextScale);
                DrawString(rightEdge - tagWidth, rowY + 5f, tag, tagColor, SmallTextScale);
                rightEdge -= tagWidth + 8f;
            }

            var labelColor = row.Status is ScriptRowStatus.Missing or ScriptRowStatus.Faulted ? mutedText : itemText;
            DrawString(left + 6f, rowY + 5f, FitText(row.Label, rightEdge - left - 6f, SmallTextScale), labelColor, SmallTextScale);
        }

        if (scripts.Rows.Count > visibleRows)
        {
            DrawString(left + 6f, rowsTop + visibleRows * ScriptRowHeight + 5f, $"+ {scripts.Rows.Count - visibleRows} more", mutedText, SmallTextScale);
        }

        GetScriptButtonRects(panel, out var newRect, out var attachRect);
        DrawPanelButton(newRect, "+ New Script", scripts.ReadOnly ? disabledFill : buttonFill, scripts.ReadOnly ? disabledText : buttonText);
        DrawPanelButton(attachRect, "+ Attach Script", scripts.ReadOnly ? disabledFill : buttonFill, scripts.ReadOnly ? disabledText : buttonText);
    }

    // A script under its Part: a script icon and its label. The script open in the code editor is marked like a menu's
    // current item (accent bar, lighter fill), not like a selected Part.
    private void DrawScriptRow(float x, float y, float width, float height, string label, bool isActive, Vector3 text, Vector3 muted, Vector3 accent)
    {
        var rowFill = new Vector3(0.15f, 0.16f, 0.19f);
        var activeFill = new Vector3(0.20f, 0.27f, 0.40f);
        _screenUi.DrawRect(x, y, width, height, isActive ? activeFill : rowFill);
        if (isActive)
        {
            _screenUi.DrawRect(x, y + 4f, 3f, height - 8f, accent);
        }

        UiIcons.DrawScript(_screenUi, x + ScriptIconRoom * 0.5f + 2f, y + height * 0.5f, 11f, isActive ? accent : muted);
        var labelX = x + ScriptIconRoom + 4f;
        DrawString(labelX, y + 5f, FitText(label, x + width - 6f - labelX, SmallTextScale), isActive ? new Vector3(1f, 1f, 1f) : text, SmallTextScale);
    }

    private void DrawPanelButton(UiRect rect, string label, Vector3 fill, Vector3 text)
    {
        _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, fill);
        var scale = SmallTextScale;
        var textWidth = MeasureTextWidth(label, scale);
        if (textWidth > rect.Width - 10f && textWidth > 0f)
        {
            scale *= (rect.Width - 10f) / textWidth;
            textWidth = MeasureTextWidth(label, scale);
        }

        var textHeight = _screenUi.MeasureLineHeight(scale);
        DrawString(rect.X + (rect.Width - textWidth) * 0.5f, rect.Y + (rect.Height - textHeight) * 0.5f, label, text, scale);
    }

    // The text, shortened with "..." when it is wider than the space it has.
    private string FitText(string text, float maxWidth, float scale)
    {
        if (MeasureTextWidth(text, scale) <= maxWidth)
        {
            return text;
        }

        var shortened = text;
        while (shortened.Length > 1 && MeasureTextWidth(shortened + "...", scale) > maxWidth)
        {
            shortened = shortened[..^1];
        }

        return shortened.TrimEnd() + "...";
    }

    private static int VisibleScriptRows(ScriptsPanelView scripts) =>
        global::System.Math.Min(scripts.Rows.Count, MaxScriptRows);

    // The section's height follows its content: a header, one line per script (at least one line), the buttons.
    private static UiRect GetScriptsPanelRect(UiRect sidebar, ScriptsPanelView scripts)
    {
        if (scripts.Environment is { } environment)
        {
            var detailsHeight = ScriptsHeaderHeight + global::System.Math.Max(1, environment.Lines.Count) * ScriptRowHeight + ScriptsBottomPadding;
            return new UiRect(sidebar.X, sidebar.Y + sidebar.Height - detailsHeight, sidebar.Width, detailsHeight);
        }

        var lines = scripts.Target is null
            ? 1
            : global::System.Math.Max(1, VisibleScriptRows(scripts) + (scripts.Rows.Count > MaxScriptRows ? 1 : 0));
        var height = ScriptsHeaderHeight + lines * ScriptRowHeight + (scripts.Target is null ? 0f : ScriptButtonsHeight) + ScriptsBottomPadding;
        return new UiRect(sidebar.X, sidebar.Y + sidebar.Height - height, sidebar.Width, height);
    }

    private static UiRect GetDetachRect(UiRect panel, int row)
    {
        var rowY = panel.Y + ScriptsHeaderHeight + row * ScriptRowHeight;
        var rowHeight = ScriptRowHeight - 4f;
        return new UiRect(
            panel.X + panel.Width - SidePadding - DetachButtonSize - 2f,
            rowY + (rowHeight - DetachButtonSize) * 0.5f,
            DetachButtonSize,
            DetachButtonSize);
    }

    private static void GetScriptButtonRects(UiRect panel, out UiRect newRect, out UiRect attachRect)
    {
        var y = panel.Y + panel.Height - ScriptsBottomPadding - ScriptButtonHeight;
        var left = panel.X + SidePadding;
        var buttonWidth = (panel.Width - SidePadding * 2f - ScriptButtonGap) * 0.5f;
        newRect = new UiRect(left, y, buttonWidth, ScriptButtonHeight);
        attachRect = new UiRect(left + buttonWidth + ScriptButtonGap, y, buttonWidth, ScriptButtonHeight);
    }

    // The Workspace list area: from under its header to above the Rotation fields / Scripts section (scrolls when the
    // parts do not fit).
    private static UiRect GetListRect(UiRect sidebar, ScriptsPanelView scripts)
    {
        var top = sidebar.Y + TitleHeight + SectionHeaderHeight + 4f;
        var bottom = GetRotationRect(sidebar, scripts).Y - 4f;
        return new UiRect(sidebar.X, top, sidebar.Width, MathF.Max(0f, bottom - top));
    }

    // The Rotation block, right above the Scripts section (no height when no Part is selected).
    private static UiRect GetRotationRect(UiRect sidebar, ScriptsPanelView scripts)
    {
        var scriptsTop = GetScriptsPanelRect(sidebar, scripts).Y;
        var height = scripts.Rotation is null ? 0f : RotationBlockHeight;
        return new UiRect(sidebar.X, scriptsTop - height, sidebar.Width, height);
    }

    // The X, Y and Z fields side by side under the block's title.
    private static UiRect RotationFieldRect(UiRect block, int axis)
    {
        var width = (block.Width - SidePadding * 2f - RotationFieldGap * 2f) / 3f;
        return new UiRect(block.X + SidePadding + axis * (width + RotationFieldGap), block.Y + RotationTitleHeight, width, RotationFieldHeight);
    }

    private void RenderRotationFields(UiRect sidebar, ScriptsPanelView scripts, Vector3 barColor, Vector3 separator, Vector3 sectionText, Vector3 itemText,
        Vector3 mutedText)
    {
        if (scripts.Rotation is not { } rotation)
        {
            return;
        }

        var block = GetRotationRect(sidebar, scripts);
        var left = block.X + SidePadding;
        var width = block.Width - SidePadding * 2f;
        _screenUi.DrawRect(block.X + 1f, block.Y, block.Width - 1f, block.Height, barColor);
        _screenUi.DrawRect(left, block.Y, width, 1f, separator);
        DrawString(left, block.Y + 7f, "Rotation", sectionText, SmallTextScale);
        var unit = rotation.ReadOnly ? "degrees (Play: read-only)" : "degrees";
        DrawString(left + width - MeasureTextWidth(unit, SmallTextScale), block.Y + 7f, unit, mutedText, SmallTextScale);

        var accent = new Vector3(0.32f, 0.56f, 0.95f);
        for (var axis = 0; axis < 3; axis++)
        {
            var field = RotationFieldRect(block, axis);
            var editing = rotation.EditingAxis == axis;
            _screenUi.DrawRect(field.X - 1f, field.Y - 1f, field.Width + 2f, field.Height + 2f, editing ? accent : separator);
            _screenUi.DrawRect(field.X, field.Y, field.Width, field.Height, editing ? new Vector3(0.09f, 0.10f, 0.12f) : new Vector3(0.15f, 0.16f, 0.19f));
            DrawString(field.X + 5f, field.Y + 5f, RotationInput.AxisNames[axis], AxisColor(axis), SmallTextScale);
            var textLeft = field.X + 5f + MeasureTextWidth("X", SmallTextScale) + 5f;
            var room = field.X + field.Width - 5f - textLeft;
            if (editing)
            {
                // The text exactly as typed; the whole of it highlighted while the first key would replace it.
                var typed = FitTextStart(rotation.EditingText, room - 3f, SmallTextScale);
                var typedWidth = MeasureTextWidth(typed, SmallTextScale);
                if (rotation.SelectAll && typed.Length > 0)
                {
                    _screenUi.DrawRect(textLeft - 1f, field.Y + 4f, typedWidth + 2f, field.Height - 8f, new Vector3(0.20f, 0.36f, 0.64f));
                }

                DrawString(textLeft, field.Y + 5f, typed, itemText, SmallTextScale);
                _screenUi.DrawRect(textLeft + typedWidth + 1f, field.Y + 5f, 1.5f, field.Height - 10f, itemText);
            }
            else
            {
                var value = FitText(rotation.Values[axis], room, SmallTextScale);
                DrawString(field.X + field.Width - 5f - MeasureTextWidth(value, SmallTextScale), field.Y + 5f, value,
                    rotation.ReadOnly ? mutedText : itemText, SmallTextScale);
            }
        }
    }

    // The gizmo's colour for each axis (X red, Y green, Z blue).
    private static Vector3 AxisColor(int axis) => axis switch
    {
        0 => new Vector3(0.93f, 0.38f, 0.38f),
        1 => new Vector3(0.45f, 0.85f, 0.45f),
        _ => new Vector3(0.42f, 0.62f, 1f)
    };

    // The end of the text that fits (what is being typed stays in view as it grows).
    private string FitTextStart(string text, float maxWidth, float scale)
    {
        var shown = text;
        while (shown.Length > 0 && MeasureTextWidth(shown, scale) > maxWidth)
        {
            shown = shown[1..];
        }

        return shown;
    }

    private float MaxListScroll(UiRect list) => MathF.Max(0f, _rows.Count * ItemHeight - list.Height);

    // The row index under a point of the list, or false.
    private bool TryRowIndex(float mouseX, float mouseY, int screenWidth, int screenHeight, Workspace workspace, ScriptsPanelView scripts, out int rowIndex)
    {
        rowIndex = -1;
        if (!_isVisible || _renameActive || !IsOverSidebar(mouseX, mouseY, screenWidth, screenHeight))
        {
            return false;
        }

        var sidebar = GetSidebarRect(screenWidth, screenHeight);
        if (mouseY >= GetRotationRect(sidebar, scripts).Y)
        {
            return false;
        }

        var list = GetListRect(sidebar, scripts);
        if (mouseY < list.Y || mouseY > list.Y + list.Height)
        {
            return false;
        }

        BuildRows(workspace);
        rowIndex = (int)((mouseY - list.Y + _listScroll) / ItemHeight);
        return rowIndex >= 0 && rowIndex < _rows.Count;
    }

    // The environment's rows, then the Workspace tree: at each level its Parts and groups in the order the Workspace
    // first reaches them; an expanded group lists its contents one level deeper, an expanded Part its scripts (in
    // attachment order). Expansion is forgotten for a Part that has no scripts left or has left the scene, and for a
    // group that no longer holds anything.
    private void BuildRows(Workspace workspace)
    {
        _rows.Clear();
        for (var i = 0; i < Environment.Count; i++)
        {
            _rows.Add(new ExplorerRow(null, -1, i));
        }

        var parts = workspace.Parts;
        var groups = PartGroups.InOrder(parts);

        // Where each group is first reached (its earliest Part, at any depth), to place it among its siblings.
        var firstIndex = new Dictionary<PartGroup, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < parts.Count; i++)
        {
            for (var group = parts[i].Group; group is not null; group = group.Parent)
            {
                firstIndex.TryAdd(group, i);
            }
        }

        AddLevel(null, 0);

        if (_expanded.Count > 0)
        {
            _expanded.RemoveWhere(part => part.Scripts.Count == 0 || !IsInWorkspace(workspace, part));
        }

        if (_expandedGroups.Count > 0)
        {
            _expandedGroups.RemoveWhere(group => !firstIndex.ContainsKey(group));
        }

        if (SelectedGroup is { } selected && !firstIndex.ContainsKey(selected))
        {
            SelectedGroup = null;
        }

        void AddLevel(PartGroup? container, int depth)
        {
            // This level's items: Parts directly in it and groups directly in it, by where the Workspace reaches them.
            var items = new List<(int Order, Part? Part, PartGroup? Group)>();
            for (var i = 0; i < parts.Count; i++)
            {
                if (ReferenceEquals(parts[i].Group, container))
                {
                    items.Add((i, parts[i], null));
                }
            }

            foreach (var group in groups)
            {
                if (ReferenceEquals(group.Parent, container))
                {
                    items.Add((firstIndex[group], null, group));
                }
            }

            items.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : (a.Group is null ? 1 : 0).CompareTo(b.Group is null ? 1 : 0));
            foreach (var (_, part, group) in items)
            {
                if (group is not null)
                {
                    _rows.Add(new ExplorerRow(null, -1, -1, group, depth));
                    if (_expandedGroups.Contains(group))
                    {
                        AddLevel(group, depth + 1);
                    }

                    continue;
                }

                _rows.Add(new ExplorerRow(part, -1, -1, null, depth));
                if (part!.Scripts.Count > 0 && _expanded.Contains(part))
                {
                    for (var i = 0; i < part.Scripts.Count; i++)
                    {
                        _rows.Add(new ExplorerRow(part, i, -1, null, depth));
                    }
                }
            }
        }
    }

    private static bool IsInWorkspace(Workspace workspace, Part part)
    {
        foreach (var candidate in workspace.Parts)
        {
            if (ReferenceEquals(candidate, part))
            {
                return true;
            }
        }

        return false;
    }

    private void CancelRename()
    {
        _renameActive = false;
        _renamePart = null;
        _renameGroup = null;
        _renameText = string.Empty;
    }

    private void ConfirmRename()
    {
        var trimmed = _renameText.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        if (_renameGroup is { } group)
        {
            var groupName = group.Name;
            CancelRename();
            if (!string.Equals(groupName, trimmed, StringComparison.Ordinal))
            {
                GroupRenameCommitted?.Invoke(group, trimmed);
            }

            return;
        }

        if (_renamePart is null)
        {
            CancelRename();
            return;
        }

        var previousName = _renamePart.Name;
        _renamePart.Name = trimmed;
        RenameCommitted?.Invoke(_renamePart, previousName, trimmed);
        CancelRename();
        Console.WriteLine($"[WorldExplorer] Renamed {previousName} -> {trimmed}");
    }

    // Letters and digits of any script, combining accents, spaces, '_' and '-' (as names like "Point Light" already use).
    private static bool IsAllowedRenameCharacter(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
        || rune.Value is ' ' or '_' or '-';

    private static UiRect GetSidebarRect(int screenWidth, int screenHeight)
    {
        var x = screenWidth - SidebarWidth;
        var y = ToolbarRenderer.ToolbarHeight;
        var height = screenHeight - y;
        return new UiRect(x, y, SidebarWidth, height);
    }


    // An imported model is one entry labelled "Model"; its internal meshes/nodes are not listed. A part (or model) that
    // gives light says which light.
    private static string GetKindLabel(Part part) => part.Light is { } light
        ? light.Type switch { Graphics.LightType.Spot => "Spot Light", Graphics.LightType.Directional => "Directional", _ => "Point Light" }
        : part.Model is not null ? "Model" : GetShapeLabel(part.ShapeType);

    private static string GetShapeLabel(ShapeType shapeType) =>
        shapeType switch
        {
            ShapeType.Cylinder => "Cylinder",
            ShapeType.Wedge => "Wedge",
            ShapeType.Sphere => "Sphere",
            ShapeType.Pyramid => "Pyramid",
            _ => "Cube" // ShapeType.Block is displayed as "Cube" (serialized name stays "Block")
        };

    private void DrawString(float x, float y, string text, Vector3 color, float scale, FontWeight weight = FontWeight.Regular) =>
        _screenUi.DrawString(x, y, text, color, scale, weight);

    private float MeasureTextWidth(string text, float scale) => _screenUi.MeasureWidth(text, scale);

    // A Workspace list row: a Part (ScriptIndex -1), one of its scripts, an environment object (no Part, no Group) or a
    // group; Depth is how many groups it sits in (its indent).
    private readonly record struct ExplorerRow(Part? Part, int ScriptIndex, int EnvironmentIndex = -1, PartGroup? Group = null, int Depth = 0);

    private readonly struct UiRect(float x, float y, float width, float height)
    {
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Width { get; } = width;
        public float Height { get; } = height;

        public bool Contains(float px, float py) => px >= X && px <= X + Width && py >= Y && py <= Y + Height;
    }
}
