using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.Platform;
using Wisko.Engine.ScriptEditor;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The integrated script editor: a document workspace in the central content area (the pinned 3D View tab plus one tab
/// per open script), the code editor for the active script, and the Output panel under both. Keyboard focus belongs
/// to the code editor while a script tab is active, so typing never reaches the camera, gizmos or scene shortcuts.
/// </summary>
public sealed partial class Renderer
{
    private const double ValidationDelaySeconds = 0.5;
    private const double ExternalCheckSeconds = 1.0;
    private const double DoubleClickSeconds = 0.4;
    private const double FlashSeconds = 1.6;
    private const float MinEditorHeight = 120f;
    private const float AutoScrollMaxSpeed = 1600f;

    private readonly ScriptDocumentManager _scriptDocuments = new();
    private readonly OutputLog _output = new();
    private readonly DocumentTabStrip _tabStrip = new();
    private readonly CodeEditorView _codeView = new();
    private readonly OutputPanel _outputPanel = new();
    private readonly ConfirmDialog _confirm = new();
    private readonly CodeEditorViewState _codeState = new();
    private readonly TextSearch _search = new();
    private CodeEditorController _codeController = new(ShortcutConvention.Current, null);
    private LuaSyntaxValidator? _validator;
    private string? _projectRoot;
    private int _activeTab = DocumentTabHit.SceneView;
    private double _editorClock;
    private double _caretResetTime;
    private double _lastTextClickTime = -1;
    private TextPosition _lastTextClickPosition;
    private int _textClickCount;
    private bool _selectingText;
    private bool _draggingVerticalBar;
    private bool _draggingHorizontalBar;
    private bool _resizingOutput;
    private float _resizeStartY;
    private float _resizeStartHeight;
    private double _nextExternalCheck;
    private double _flashUntil;
    private double _statusUntil;
    private bool _wheelConsumed;
    private double _lastScriptRowClick = -1;
    private int _lastScriptRowIndex = -1;
    private Part? _lastScriptRowPart;

    /// <summary>The session's structured Output (script prints, warnings, errors).</summary>
    internal OutputLog Output => _output;

    internal bool HasDirtyScripts => _scriptDocuments.DirtyDocuments.Any();

    internal bool IsConfirmOpen => _confirm.IsOpen;

    private ScriptDocument? ActiveDocument =>
        _activeTab >= 0 && _activeTab < _scriptDocuments.Documents.Count ? _scriptDocuments.Documents[_activeTab] : null;

    // ---- Layout ----

    private float RightPanelWidth => _worldExplorer.IsVisible || _colorPanel.IsVisible || GuiPanelVisible ? WorldExplorerRenderer.SidebarWidth : 0f;

    // The workspace column between the Physics panel (left) and World or Color (right).
    private float ContentLeft => LeftPanelWidth;

    private float ContentRight => MathF.Max(0f, _width - RightPanelWidth);

    private float ContentWidth => MathF.Max(0f, ContentRight - ContentLeft);

    private UiBox TabStripRect => new(ContentLeft, ToolbarRenderer.ToolbarHeight, ContentWidth, DocumentTabStrip.Height);

    // Hidden Output (toolbar Output off) takes no room: the workspace above reaches the bottom of the window.
    private UiBox OutputRect
    {
        get
        {
            if (!_outputPanelOn)
            {
                return new UiBox(ContentLeft, _height, ContentWidth, 0f);
            }

            var top = ToolbarRenderer.ToolbarHeight + DocumentTabStrip.Height;
            var maxBody = MathF.Max(OutputPanel.MinBodyHeight, _height - top - OutputPanel.HeaderHeight - MinEditorHeight);
            _outputPanel.BodyHeight = global::System.Math.Clamp(_outputPanel.BodyHeight, OutputPanel.MinBodyHeight, maxBody);
            var height = _outputPanel.TotalHeight;
            return new UiBox(ContentLeft, _height - height, ContentWidth, height);
        }
    }

    private UiBox EditorRect
    {
        get
        {
            var top = ToolbarRenderer.ToolbarHeight + DocumentTabStrip.Height;
            return new UiBox(ContentLeft, top, ContentWidth, MathF.Max(0f, OutputRect.Y - top));
        }
    }

    // Tab strip, Output, Environment & Graphics and Physics panels: editor chrome over the 3D view (viewport input and
    // mouse-look skip it).
    private bool IsOverEditorChrome(float x, float y) =>
        TabStripRect.Contains(x, y) || (_outputPanelOn && OutputRect.Contains(x, y)) || IsOverEnvironmentPanel(x, y)
        || IsOverPhysicsPanel(x, y) || IsOverAnimationPanel(x, y) || IsOverGuiPanel(x, y);

    // ---- Lifecycle ----

    private void InitializeScriptEditorUi(IUiBackend ui)
    {
        _tabStrip.Initialize(ui);
        _codeView.Initialize(ui);
        _outputPanel.Initialize(ui);
        _confirm.Initialize(ui);
        _environmentSheet.Initialize(ui);
        _physicsPanel.Initialize(ui);
        _animationPanel.Initialize(ui);
        _playMode.Output = _output;
    }

    private void ShutdownScriptEditorUi()
    {
        _tabStrip.Dispose();
        _codeView.Dispose();
        _outputPanel.Dispose();
        _confirm.Dispose();
        _environmentSheet.Dispose();
        _physicsPanel.Dispose();
        _animationPanel.Dispose();
        _validator?.Dispose();
        _validator = null;
    }

    /// <summary>Folder scripts are read from in Play Mode and opened from (the open project); null for an unnamed world.</summary>
    internal string? ProjectRoot
    {
        get => _projectRoot;
        set
        {
            if (string.Equals(_projectRoot, value, StringComparison.Ordinal))
            {
                return;
            }

            // A different project: its documents are closed (unsaved changes were settled by the leave flow).
            _projectRoot = value;
            _scriptDocuments.SetProject(value);
            _activeTab = DocumentTabHit.SceneView;
            CloseCompletion();
            _codeState.FindOpen = false;
        }
    }

    /// <summary>Opens (or activates) a project script in the code editor, optionally at a zero-based line.</summary>
    internal bool OpenScript(ScriptReference reference, int? line = null)
    {
        if (!_scriptDocuments.TryOpen(reference, out var document, out var error) || document is null)
        {
            _toast.Show($"Couldn't open {Path.GetFileName(reference.AssetPath)}: {error}", ScriptToastSeconds);
            return false;
        }

        ActivateTab(IndexOf(document));
        if (line is int target)
        {
            GoToLine(document, target);
        }

        return true;
    }

    /// <summary>
    /// Asks about unsaved scripts before leaving (Save All / Don't Save / Cancel). <paramref name="continueLeaving"/> runs
    /// once nothing unsaved is left (saved or deliberately discarded); Cancel or a failed save keeps the editor open.
    /// </summary>
    internal void PromptUnsavedScripts(Action continueLeaving)
    {
        var dirty = _scriptDocuments.DirtyDocuments.ToList();
        if (dirty.Count == 0)
        {
            continueLeaving();
            return;
        }

        var names = string.Join(", ", dirty.Select(document => document.FileName));
        _confirm.Open(new ConfirmRequest(
            dirty.Count == 1 ? $"Save changes to {dirty[0].FileName}?" : $"Save changes to {dirty.Count} scripts?",
            $"{names} {(dirty.Count == 1 ? "has" : "have")} unsaved changes. If you don't save, they are lost.",
            ["Save All", "Don't Save", "Cancel"],
            choice =>
            {
                switch (choice)
                {
                    case 0:
                        foreach (var document in dirty)
                        {
                            if (!TrySaveDocument(document, out var reason))
                            {
                                ActivateTab(IndexOf(document));
                                _toast.Show($"Couldn't save {document.FileName}: {reason}", ScriptToastSeconds);
                                return;
                            }
                        }

                        continueLeaving();
                        break;
                    case 1:
                        foreach (var document in dirty)
                        {
                            CloseDocument(document);
                        }

                        Console.WriteLine($"[ScriptEditor] Discarded unsaved changes in {names}");
                        continueLeaving();
                        break;
                }
            }));
    }

    // ---- Per-frame update ----

    private void HandleTabStrip(InputManager input)
    {
        var strip = TabStripRect;
        var tabs = TabViews();
        if (strip.Contains(input.MouseX, input.MouseY) && !_wheelConsumed && (input.ScrollDelta != 0f || input.ScrollDeltaX != 0f))
        {
            _tabStrip.Scroll((input.ScrollDeltaX - input.ScrollDelta) * 30f, strip.Width, tabs);
            _wheelConsumed = true;
        }

        if (!input.LeftMousePressed || _toolbarConsumedClick
            || !_tabStrip.TryHit(input.MouseX, input.MouseY, strip.X, strip.Y, strip.Width, tabs, out var hit))
        {
            return;
        }

        _toolbarConsumedClick = true;
        if (hit.Tab == DocumentTabHit.SceneView)
        {
            ActivateTab(DocumentTabHit.SceneView);
        }
        else if (hit.Tab >= 0)
        {
            if (hit.OnClose)
            {
                RequestCloseTab(_scriptDocuments.Documents[hit.Tab]);
            }
            else
            {
                ActivateTab(hit.Tab);
            }
        }
    }

    private void HandleOutputPanel(InputManager input)
    {
        if (!_outputPanelOn)
        {
            _resizingOutput = false;
            return;
        }

        var panel = OutputRect;
        if (_resizingOutput)
        {
            if (input.IsLeftMouseHeld)
            {
                _outputPanel.BodyHeight = _resizeStartHeight + (_resizeStartY - input.MouseY);
            }
            else
            {
                _resizingOutput = false;
            }

            _toolbarConsumedClick = true;
            return;
        }

        if (!panel.Contains(input.MouseX, input.MouseY))
        {
            return;
        }

        if (!_wheelConsumed && input.ScrollDelta != 0f)
        {
            _outputPanel.ScrollRows(input.ScrollDelta * 3f, _output, panel);
            _wheelConsumed = true;
        }

        if (!input.LeftMousePressed || _toolbarConsumedClick)
        {
            return;
        }

        _toolbarConsumedClick = true;
        switch (_outputPanel.HitTest(panel, _output, input.MouseX, input.MouseY, out var row))
        {
            case OutputPanelControl.ResizeEdge:
                _resizingOutput = true;
                _resizeStartY = input.MouseY;
                _resizeStartHeight = _outputPanel.BodyHeight;
                break;
            case OutputPanelControl.ToggleCollapse:
            case OutputPanelControl.Header:
                _outputPanel.Collapsed = !_outputPanel.Collapsed;
                _outputPanel.ScrollToBottom();
                break;
            case OutputPanelControl.Clear:
                _output.Clear();
                _outputPanel.ScrollToBottom();
                break;
            case OutputPanelControl.Row:
                var entry = _output[row];
                if (entry.ScriptPath is { } path && ScriptReference.TryCreate(path, out var reference))
                {
                    Console.WriteLine($"[Output] Go to {path}:{entry.Line}");
                    OpenScript(reference, entry.Line is int line ? line - 1 : null);
                }

                break;
        }
    }

    private void HandleCodeEditor(InputManager input, ScriptDocument document, float deltaTime)
    {
        _codeView.UpdateMetrics(DpiScale);
        var layout = _codeView.GetLayout(EditorRect, document, _codeState.FindOpen);
        var mouseX = input.MouseX;
        var mouseY = input.MouseY;
        var overSidebar = _worldExplorer.IsOverSidebar(mouseX, mouseY, _width, _height) || _colorPanel.IsOverPanel(mouseX, mouseY, _width, _height);

        // Mouse button.
        if (input.LeftMousePressed && !_toolbarConsumedClick && !overSidebar && layout.Bounds.Contains(mouseX, mouseY))
        {
            _toolbarConsumedClick = true;
            HandleCodeEditorClick(input, document, layout);
        }

        if (input.IsLeftMouseHeld && !input.LeftMousePressed)
        {
            if (_selectingText)
            {
                // Auto-scroll while dragging past the top/bottom edge, faster the further out (bounded).
                var overshoot = mouseY < layout.Code.Y ? mouseY - layout.Code.Y : mouseY > layout.Code.Y + layout.Code.Height ? mouseY - (layout.Code.Y + layout.Code.Height) : 0f;
                if (overshoot != 0f)
                {
                    document.ScrollY += global::System.Math.Clamp(overshoot * 10f, -AutoScrollMaxSpeed, AutoScrollMaxSpeed) * global::System.Math.Min(deltaTime, 0.1f);
                    _codeView.ClampScroll(document, layout);
                }

                var clampedY = global::System.Math.Clamp(mouseY, layout.Code.Y, layout.Code.Y + layout.Code.Height - 1f);
                _codeController.DragTo(document, _codeView.PositionAt(document, layout, mouseX, clampedY));
                ResetCaretBlink();
            }
            else if (_draggingVerticalBar || _draggingHorizontalBar)
            {
                _codeView.DragScrollbar(document, layout, _draggingVerticalBar, mouseX, mouseY);
            }
        }

        if (input.LeftMouseReleased || !input.IsLeftMouseHeld)
        {
            _selectingText = false;
            _draggingVerticalBar = false;
            _draggingHorizontalBar = false;
        }

        // Wheel: completion list, else the document (Shift: horizontal).
        if (!_wheelConsumed && layout.Bounds.Contains(mouseX, mouseY) && !overSidebar && (input.ScrollDelta != 0f || input.ScrollDeltaX != 0f))
        {
            _wheelConsumed = true;
            if (_codeView.HitTest(document, layout, _codeState, mouseX, mouseY, out _) == CodeEditorControl.Completion && _codeState.Completion is { } completion)
            {
                var maxScroll = global::System.Math.Max(0, completion.Items.Count - CodeEditorView.CompletionPageSize);
                _codeState.CompletionScroll = global::System.Math.Clamp(_codeState.CompletionScroll - (int)MathF.Round(input.ScrollDelta), 0, maxScroll);
            }
            else
            {
                var vertical = input.MoveFast ? 0f : input.ScrollDelta;
                var horizontal = input.MoveFast ? input.ScrollDelta : -input.ScrollDeltaX;
                document.ScrollY -= vertical * _codeView.LineHeight * 3f;
                document.ScrollX -= horizontal * _codeView.CharWidth * 6f;
                _codeView.ClampScroll(document, layout);
            }
        }

        // Keyboard (unless a World Explorer rename has the keys).
        if (!_worldExplorer.IsRenaming && !PhysicsPanelOwnsKeyboard)
        {
            if (_codeState.FindOpen && _codeState.FindFocused)
            {
                HandleFindKeyboard(input, document, layout);
            }
            else
            {
                HandleCodeKeyboard(input, document, layout);
            }
        }
    }

    private void HandleCodeEditorClick(InputManager input, ScriptDocument document, CodeEditorView.Layout layout)
    {
        var control = _codeView.HitTest(document, layout, _codeState, input.MouseX, input.MouseY, out var completionIndex);
        switch (control)
        {
            case CodeEditorControl.Completion:
                if (completionIndex >= 0)
                {
                    ApplyCompletion(document, completionIndex);
                }

                return;
            case CodeEditorControl.FindField:
                _codeState.FindFocused = true;
                return;
            case CodeEditorControl.FindPrevious:
                StepFind(document, forward: false);
                return;
            case CodeEditorControl.FindNext:
                StepFind(document, forward: true);
                return;
            case CodeEditorControl.FindCase:
                _codeState.FindCaseSensitive = !_codeState.FindCaseSensitive;
                RefreshFind(document, selectNearest: true);
                return;
            case CodeEditorControl.FindClose:
                CloseFind();
                return;
            case CodeEditorControl.VerticalScrollbar:
                _draggingVerticalBar = true;
                _codeView.DragScrollbar(document, layout, vertical: true, input.MouseX, input.MouseY);
                return;
            case CodeEditorControl.HorizontalScrollbar:
                _draggingHorizontalBar = true;
                _codeView.DragScrollbar(document, layout, vertical: false, input.MouseX, input.MouseY);
                return;
        }

        if (control is not (CodeEditorControl.Text or CodeEditorControl.Gutter))
        {
            return;
        }

        _codeState.FindFocused = false;
        CloseCompletion();
        var position = _codeView.PositionAt(document, layout, MathF.Max(input.MouseX, layout.Code.X), input.MouseY);
        if (control == CodeEditorControl.Gutter)
        {
            _codeController.Click(document, position, 3, false);
            _selectingText = true;
            ResetCaretBlink();
            return;
        }

        var repeated = _editorClock - _lastTextClickTime <= DoubleClickSeconds && position.Line == _lastTextClickPosition.Line
                       && global::System.Math.Abs(position.Column - _lastTextClickPosition.Column) <= 1;
        _textClickCount = repeated ? _textClickCount % 3 + 1 : 1;
        _lastTextClickTime = _editorClock;
        _lastTextClickPosition = position;
        _codeController.Click(document, position, _textClickCount, input.MoveFast);
        _selectingText = _textClickCount == 1;
        ResetCaretBlink();
    }

    private void HandleCodeKeyboard(InputManager input, ScriptDocument document, CodeEditorView.Layout layout)
    {
        var changed = false;
        var pageLines = layout.VisibleLines(_codeView.LineHeight) - 1;
        foreach (var key in input.KeyEvents)
        {
            if (key.Key == EditorKey.Escape)
            {
                continue; // TryConsumeEscape closes popups / stops Play
            }

            if (_codeState.Completion is { } completion && HandleCompletionKey(document, completion, key))
            {
                changed = true;
                continue;
            }

            var versionBefore = document.Buffer.Version;
            var command = _codeController.HandleKey(document, key, pageLines, _editorClock);
            switch (command)
            {
                case CodeEditorCommand.Save:
                    SaveDocumentInteractive(document);
                    break;
                case CodeEditorCommand.OpenFind:
                    OpenFind(document);
                    break;
                case CodeEditorCommand.FindNext:
                    StepFind(document, forward: true);
                    break;
                case CodeEditorCommand.FindPrevious:
                    StepFind(document, forward: false);
                    break;
                case CodeEditorCommand.TriggerCompletion:
                    UpdateCompletion(document, explicitRequest: true);
                    break;
                case CodeEditorCommand.Handled:
                    changed = true;
                    if (_codeState.Completion is not null)
                    {
                        // Deleting keeps filtering; any caret move closes the list.
                        if (key.Key is EditorKey.Backspace or EditorKey.Delete && document.Buffer.Version != versionBefore)
                        {
                            UpdateCompletion(document, explicitRequest: false);
                        }
                        else
                        {
                            CloseCompletion();
                        }
                    }

                    break;
            }
        }

        var typed = TypedText(input);
        if (typed.Length > 0)
        {
            _codeController.TypeText(document, typed, _editorClock);
            changed = true;
            var last = typed[^1];
            if (last == '.' || last is '"' or '\'' || LuaLexer.IsNamePart(last))
            {
                UpdateCompletion(document, explicitRequest: false);
            }
            else
            {
                CloseCompletion();
            }
        }

        if (changed)
        {
            _codeView.RevealCaret(document, layout);
            _codeView.ClampScroll(document, layout);
            ResetCaretBlink();
        }
    }

    // Up/Down/PageUp/PageDown move through the list; Enter/Tab accept. Everything else goes on to the editor.
    private bool HandleCompletionKey(ScriptDocument document, CompletionResult completion, EditorKeyEvent key)
    {
        if (key.Modifiers != KeyModifierFlags.None)
        {
            return false;
        }

        var count = completion.Items.Count;
        var page = CodeEditorView.CompletionPageSize;
        switch (key.Key)
        {
            case EditorKey.Up:
                SelectCompletion(_codeState.CompletionSelected <= 0 ? count - 1 : _codeState.CompletionSelected - 1, count);
                return true;
            case EditorKey.Down:
                SelectCompletion(_codeState.CompletionSelected >= count - 1 ? 0 : _codeState.CompletionSelected + 1, count);
                return true;
            case EditorKey.PageUp:
                SelectCompletion(global::System.Math.Max(0, _codeState.CompletionSelected - page), count);
                return true;
            case EditorKey.PageDown:
                SelectCompletion(global::System.Math.Min(count - 1, _codeState.CompletionSelected + page), count);
                return true;
            case EditorKey.Enter:
            case EditorKey.Tab:
                ApplyCompletion(document, _codeState.CompletionSelected);
                return true;
            default:
                return false;
        }
    }

    private void SelectCompletion(int index, int count)
    {
        _codeState.CompletionSelected = index;
        var page = CodeEditorView.CompletionPageSize;
        if (index < _codeState.CompletionScroll)
        {
            _codeState.CompletionScroll = index;
        }
        else if (index >= _codeState.CompletionScroll + page)
        {
            _codeState.CompletionScroll = index - page + 1;
        }

        _codeState.CompletionScroll = global::System.Math.Clamp(_codeState.CompletionScroll, 0, global::System.Math.Max(0, count - page));
    }

    private void UpdateCompletion(ScriptDocument document, bool explicitRequest)
    {
        var previous = _codeState.Completion is { } open && _codeState.CompletionSelected < open.Items.Count
            ? open.Items[_codeState.CompletionSelected].Label
            : null;
        var result = LuaCompletion.Complete(document, document.Caret, explicitRequest);
        if (result is null)
        {
            CloseCompletion();
            return;
        }

        _codeState.Completion = result;
        var keep = previous is null ? -1 : result.Items.ToList().FindIndex(item => item.Label == previous);
        _codeState.CompletionScroll = 0;
        SelectCompletion(keep >= 0 ? keep : 0, result.Items.Count);
    }

    private void ApplyCompletion(ScriptDocument document, int index)
    {
        if (_codeState.Completion is not { } completion || index < 0 || index >= completion.Items.Count)
        {
            return;
        }

        var item = completion.Items[index];
        LuaCompletion.Apply(document, completion, item, _editorClock);
        CloseCompletion();
        Console.WriteLine($"[ScriptEditor] Completed {item.Label}");
        var layout = _codeView.GetLayout(EditorRect, document, _codeState.FindOpen);
        _codeView.RevealCaret(document, layout);
        ResetCaretBlink();
    }

    private void CloseCompletion()
    {
        _codeState.Completion = null;
        _codeState.CompletionSelected = 0;
        _codeState.CompletionScroll = 0;
    }

    // ---- Find ----

    private void OpenFind(ScriptDocument document)
    {
        _codeState.FindOpen = true;
        _codeState.FindFocused = true;
        CloseCompletion();
        if (document.HasSelection && document.Selection.Start.Line == document.Selection.End.Line)
        {
            _codeState.FindQuery = document.Buffer.GetText(document.Selection);
        }

        RefreshFind(document, selectNearest: true);
    }

    private void CloseFind()
    {
        _codeState.FindOpen = false;
        _codeState.FindFocused = false;
        _codeState.FindMatches = [];
        _codeState.FindCurrent = -1;
    }

    private void HandleFindKeyboard(InputManager input, ScriptDocument document, CodeEditorView.Layout layout)
    {
        var queryChanged = false;
        foreach (var key in input.KeyEvents)
        {
            var primary = _codeController.Conventions.IsPrimary(key);
            switch (key.Key)
            {
                case EditorKey.Enter:
                    StepFind(document, forward: !key.Shift);
                    break;
                case EditorKey.Backspace when _codeState.FindQuery.Length > 0:
                    var query = _codeState.FindQuery;
                    var cut = query.Length >= 2 && char.IsLowSurrogate(query[^1]) && char.IsHighSurrogate(query[^2]) ? 2 : 1;
                    _codeState.FindQuery = query[..^cut];
                    queryChanged = true;
                    break;
                case EditorKey.Tab:
                    _codeState.FindFocused = false; // back to the code
                    break;
                case EditorKey.G when primary:
                    StepFind(document, forward: !key.Shift);
                    break;
                case EditorKey.V when primary:
                    var pasted = ClipboardText();
                    if (!string.IsNullOrEmpty(pasted) && !pasted.Contains('\n'))
                    {
                        _codeState.FindQuery += pasted;
                        queryChanged = true;
                    }

                    break;
                case EditorKey.S when primary:
                    SaveDocumentInteractive(document);
                    break;
            }
        }

        var typed = TypedText(input);
        if (typed.Length > 0)
        {
            _codeState.FindQuery += typed;
            queryChanged = true;
        }

        if (queryChanged)
        {
            RefreshFind(document, selectNearest: true);
            ResetCaretBlink();
        }

        _ = layout;
    }

    private void RefreshFind(ScriptDocument document, bool selectNearest)
    {
        _search.Query = _codeState.FindQuery;
        _search.CaseSensitive = _codeState.FindCaseSensitive;
        _search.Refresh(document.Buffer);
        _codeState.FindMatches = _search.Matches;
        _codeState.FindCapped = _search.IsCapped;
        if (selectNearest && _search.SelectFrom(document.Selection.Start))
        {
            ShowMatch(document);
        }

        _codeState.FindCurrent = _search.Current;
    }

    private void StepFind(ScriptDocument document, bool forward)
    {
        if (!_codeState.FindOpen)
        {
            OpenFind(document);
            return;
        }

        RefreshFind(document, selectNearest: false);
        if (_search.Current < 0)
        {
            _search.SelectFrom(document.Caret);
        }
        else if (forward ? _search.Next() : _search.Previous())
        {
        }

        _codeState.FindCurrent = _search.Current;
        ShowMatch(document);
    }

    private void ShowMatch(ScriptDocument document)
    {
        if (_search.Current < 0 || _search.Current >= _search.Matches.Count)
        {
            return;
        }

        var match = _search.Matches[_search.Current];
        document.Select(match.Start, match.End);
        var layout = _codeView.GetLayout(EditorRect, document, _codeState.FindOpen);
        _codeView.RevealCaret(document, layout, center: true);
        _codeView.ClampScroll(document, layout);
    }

    // ---- Tabs and documents ----

    private List<DocumentTabView> TabViews() =>
        _scriptDocuments.Documents.Select(document => new DocumentTabView(document.Label, document.IsDirty)).ToList();

    private int IndexOf(ScriptDocument document)
    {
        for (var i = 0; i < _scriptDocuments.Documents.Count; i++)
        {
            if (ReferenceEquals(_scriptDocuments.Documents[i], document))
            {
                return i;
            }
        }

        return DocumentTabHit.SceneView;
    }

    private void ActivateTab(int tab)
    {
        if (tab == _activeTab)
        {
            return;
        }

        if (_activeTab == DocumentTabHit.SceneView)
        {
            // Leaving the 3D view: finish any drag/keyboard move so nothing is left half-done.
            EndAllEditorDrags();
            FinalizeKeyboardMove();
            _boxSelection.Cancel();
        }

        _activeTab = tab;
        CloseCompletion();
        CloseFind();
        _selectingText = false;
        ResetCaretBlink();
        if (tab >= 0)
        {
            _tabStrip.Reveal(tab, TabStripRect.Width, TabViews());
            Console.WriteLine($"[ScriptEditor] Tab {_scriptDocuments.Documents[tab].Reference.AssetPath}");
        }
        else
        {
            Console.WriteLine("[ScriptEditor] Tab 3D View");
        }
    }

    private void RequestCloseTab(ScriptDocument document)
    {
        if (!document.IsDirty)
        {
            CloseDocument(document);
            return;
        }

        ActivateTab(IndexOf(document));
        _confirm.Open(new ConfirmRequest(
            $"Save changes to {document.FileName}?",
            "Your changes will be lost if you don't save them.",
            ["Save", "Don't Save", "Cancel"],
            choice =>
            {
                if (choice == 0 && SaveDocumentInteractive(document))
                {
                    CloseDocument(document);
                }
                else if (choice == 1)
                {
                    Console.WriteLine($"[ScriptEditor] Closed {document.Reference.AssetPath} without saving");
                    CloseDocument(document);
                }
            }));
    }

    private void CloseDocument(ScriptDocument document)
    {
        var index = IndexOf(document);
        if (index < 0)
        {
            return;
        }

        var activeDocument = ActiveDocument;
        _scriptDocuments.Close(document);
        Console.WriteLine($"[ScriptEditor] Closed {document.Reference.AssetPath}");
        if (ReferenceEquals(activeDocument, document))
        {
            _activeTab = _scriptDocuments.Documents.Count == 0
                ? DocumentTabHit.SceneView
                : global::System.Math.Min(index, _scriptDocuments.Documents.Count - 1);
            CloseCompletion();
            CloseFind();
        }
        else if (activeDocument is not null)
        {
            _activeTab = IndexOf(activeDocument);
        }
    }

    private void GoToLine(ScriptDocument document, int line)
    {
        line = global::System.Math.Clamp(line, 0, document.Buffer.LineCount - 1);
        var text = document.Buffer.GetLine(line);
        document.SetCaret(new TextPosition(line, TextColumns.LeadingWhitespaceLength(text)));
        _codeView.UpdateMetrics(DpiScale);
        var layout = _codeView.GetLayout(EditorRect, document, _codeState.FindOpen);
        _codeView.RevealCaret(document, layout, center: true);
        _codeView.ClampScroll(document, layout);
        _codeState.FlashLine = line;
        _flashUntil = _editorClock + FlashSeconds;
        ResetCaretBlink();
    }

    /// <summary>Saves; a file changed on disk opens the conflict choice instead. True when the file was written.</summary>
    private bool SaveDocumentInteractive(ScriptDocument document)
    {
        var result = _scriptDocuments.Save(document, out var error);
        switch (result)
        {
            case ScriptSaveResult.Saved:
                AfterSave(document);
                return true;
            case ScriptSaveResult.Conflict:
                OpenConflictDialog(document, ScriptFileStore.ReadBaseline(document.FullPath), saving: true);
                return false;
            default:
                _toast.Show($"Couldn't save {document.FileName}: {error}", ScriptToastSeconds);
                return false;
        }
    }

    /// <summary>Saves without asking (Play, Save All): a conflict or failure is reported and returns false.</summary>
    private bool TrySaveDocument(ScriptDocument document, out string? reason)
    {
        var result = _scriptDocuments.Save(document, out reason);
        if (result == ScriptSaveResult.Saved)
        {
            AfterSave(document);
            return true;
        }

        if (result == ScriptSaveResult.Conflict)
        {
            reason = "it changed on disk";
        }

        return false;
    }

    private void AfterSave(ScriptDocument document)
    {
        Validate(document);
        if (_playMode.IsPlaying)
        {
            document.SavedDuringPlay = true;
        }

        SetStatus($"Saved {document.FileName}", isError: false);
    }

    private void OpenConflictDialog(ScriptDocument document, ScriptFileBaseline disk, bool saving)
    {
        ActivateTab(IndexOf(document));
        var deleted = !disk.Exists;
        var message = deleted
            ? $"{document.FileName} was deleted by another program. Keep your version to save it again, or close it."
            : $"Another program changed {document.FileName} while you had unsaved edits here. Keep your version (it will overwrite the file), or reload the file from disk (your edits are lost).";
        _confirm.Open(new ConfirmRequest(
            deleted ? $"{document.FileName} was deleted" : $"{document.FileName} changed on disk",
            message,
            deleted ? ["Keep Editor Version", "Cancel"] : ["Reload Disk Version", "Keep Editor Version", "Cancel"],
            choice =>
            {
                var keep = deleted ? choice == 0 : choice == 1;
                var reload = !deleted && choice == 0;
                if (reload)
                {
                    if (_scriptDocuments.TryReload(document, out var error))
                    {
                        SetStatus($"Reloaded {document.FileName} from disk", isError: false);
                    }
                    else
                    {
                        _toast.Show($"Couldn't reload {document.FileName}: {error}", ScriptToastSeconds);
                    }
                }
                else if (keep)
                {
                    ScriptDocumentManager.KeepEditorVersion(document, disk);
                    if (saving)
                    {
                        SaveDocumentInteractive(document);
                    }
                }
                else
                {
                    ScriptDocumentManager.DismissExternalChange(document, disk);
                }
            },
            DefaultButton: deleted ? 1 : 2));
    }

    // ---- Background work: validation, external changes, status ----

    private void UpdateScriptDocuments()
    {
        if (ActiveDocument is { DiagnosticsStale: true } active && _editorClock - active.LastEditTime >= ValidationDelaySeconds)
        {
            Validate(active);
        }

        if (_scriptDocuments.Documents.Count > 0 && _editorClock >= _nextExternalCheck && !_confirm.IsOpen)
        {
            _nextExternalCheck = _editorClock + ExternalCheckSeconds;
            foreach (var change in _scriptDocuments.DetectExternalChanges())
            {
                var document = change.Document;
                if (!document.IsDirty && change.Disk.Exists)
                {
                    if (_scriptDocuments.TryReload(document, out _))
                    {
                        Validate(document);
                        _toast.Show($"Reloaded {document.FileName} (changed on disk)");
                    }

                    continue;
                }

                if (!change.Disk.Exists && !document.IsDirty)
                {
                    ScriptDocumentManager.KeepEditorVersion(document, change.Disk);
                    _toast.Show($"{document.FileName} was deleted on disk; saving recreates it", ScriptToastSeconds);
                    continue;
                }

                OpenConflictDialog(document, change.Disk, saving: false);
                break;
            }
        }

        if (_codeState.StatusMessage is not null && _editorClock >= _statusUntil)
        {
            _codeState.StatusMessage = null;
        }
    }

    private void Validate(ScriptDocument document)
    {
        _validator ??= new LuaSyntaxValidator();
        var diagnostic = _validator.Validate(document.Buffer.GetText());
        document.SetDiagnostics(diagnostic is null ? [] : [diagnostic]);
    }

    private void SetStatus(string message, bool isError)
    {
        _codeState.StatusMessage = message;
        _codeState.StatusIsError = isError;
        _statusUntil = _editorClock + 4.0;
    }

    private void ResetCaretBlink() => _caretResetTime = _editorClock;

    private string? ClipboardText() => _clipboard?.GetText();

    private IClipboard? _clipboard;

    /// <summary>The system clipboard for the code editor and its find field (set by the window layer; never offered to scripts).</summary>
    internal void SetClipboard(IClipboard clipboard)
    {
        _clipboard = clipboard;
        _codeController = new CodeEditorController(ShortcutConvention.Current, clipboard);
    }

    /// <summary>Closes every script tab (all saved or discarded by the leave flow) and shows the 3D View.</summary>
    internal void CloseAllScripts()
    {
        _scriptDocuments.SetProject(_projectRoot);
        _activeTab = DocumentTabHit.SceneView;
        CloseCompletion();
        CloseFind();
    }

    // OS text input of this frame (layout-aware Unicode, surrogate pairs intact), without control characters.
    private static string TypedText(InputManager input)
    {
        var characters = input.TextInputCharacters;
        if (characters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(characters.Count);
        foreach (var ch in characters)
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    // ---- Scripts panel: double-click a script row to open it ----

    private void HandleScriptRowClick(Part target, int index)
    {
        var isDouble = ReferenceEquals(target, _lastScriptRowPart) && index == _lastScriptRowIndex && _editorClock - _lastScriptRowClick <= DoubleClickSeconds;
        _lastScriptRowPart = target;
        _lastScriptRowIndex = index;
        _lastScriptRowClick = isDouble ? -1 : _editorClock;
        if (isDouble && index >= 0 && index < target.Scripts.Count)
        {
            OpenScript(target.Scripts[index]);
        }
    }

    // ---- Play hooks ----

    /// <summary>Before Play: every unsaved script is saved (safely); false (Play not started) when one cannot be.</summary>
    private bool SaveScriptsForPlay()
    {
        foreach (var document in _scriptDocuments.DirtyDocuments.ToList())
        {
            if (!TrySaveDocument(document, out var reason))
            {
                ActivateTab(IndexOf(document));
                _toast.Show($"Couldn't save {document.FileName} ({reason}) — Play not started", ScriptToastSeconds);
                Console.WriteLine($"[PlayMode] Not started: {document.Reference.AssetPath} could not be saved ({reason})");
                return false;
            }

            Console.WriteLine($"[PlayMode] Saved {document.Reference.AssetPath} before Play");
        }

        return true;
    }

    /// <summary>
    /// Save Project: every unsaved script tab is saved too (the safe save; a file changed on disk is never overwritten).
    /// Returns how many were written; the first one that could not be is described in <paramref name="failure"/>.
    /// </summary>
    internal int SaveUnsavedScripts(out string? failure)
    {
        failure = null;
        var saved = 0;
        foreach (var document in _scriptDocuments.DirtyDocuments.ToList())
        {
            if (TrySaveDocument(document, out var reason))
            {
                saved++;
                Console.WriteLine($"[ScriptEditor] Saved {document.Reference.AssetPath} with the project");
                continue;
            }

            Console.WriteLine($"[ScriptEditor] Not saved with the project: {document.Reference.AssetPath} ({reason})");
            failure ??= $"couldn't save {document.FileName} ({reason})";
        }

        return saved;
    }

    private void OnPlayStopped()
    {
        foreach (var document in _scriptDocuments.Documents)
        {
            document.SavedDuringPlay = false;
        }

        _output.Add(new OutputEntry(OutputKind.Separator, $"Play stopped {DateTime.Now:HH:mm:ss}"));
    }

    // ---- Rendering ----

    private void RenderDocumentWorkspace()
    {
        var dpiScale = DpiScale;
        if (ActiveDocument is { } document)
        {
            _codeView.UpdateMetrics(dpiScale);
            var layout = _codeView.GetLayout(EditorRect, document, _codeState.FindOpen);
            _codeView.ClampScroll(document, layout);
            if (_codeState.FindOpen)
            {
                RefreshFind(document, selectNearest: false);
            }

            _codeState.HasFocus = !_confirm.IsOpen;
            _codeState.CaretVisible = (_editorClock - _caretResetTime) % 1.1 < 0.65;
            _codeState.FlashAlpha = _flashUntil > _editorClock ? (float)global::System.Math.Min(1.0, (_flashUntil - _editorClock) / 0.6) : 0f;
            if (_codeState.FlashAlpha <= 0f)
            {
                _codeState.FlashLine = -1;
            }

            _codeState.Playing = _playMode.IsPlaying;
            _codeView.Render(document, layout, _codeState, _width, _height);
        }

        var strip = TabStripRect;
        _tabStrip.Render(strip.X, strip.Y, strip.Width, _activeTab, TabViews(), _lastMouseX, _lastMouseY, _width, _height, dpiScale);
        if (_outputPanelOn)
        {
            _outputPanel.Render(OutputRect, _output, _lastMouseX, _lastMouseY, _width, _height, dpiScale);
        }
    }

    private float _lastMouseX;
    private float _lastMouseY;
}
