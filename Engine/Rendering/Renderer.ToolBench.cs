using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The top toolbar's behaviour: ToolBench sections, the section controls, the project menu and Play / Stop, plus the
/// editor layout the user chooses (World and Output panels). All of it is editor UI state for this editor session:
/// never scene or project data, and back to the defaults (Main Tools, World and Output shown) when a project is
/// entered. Switching sections only changes which tools the toolbar shows.
/// </summary>
public sealed partial class Renderer
{
    private readonly ToolBench _toolBench = new();

    // Persistent layout: World and Output stay as the user set them, whatever the ToolBench section.
    private bool _worldPanelOn = true;
    private bool _outputPanelOn = true;

    private ToolbarView ToolbarState =>
        new(_toolState.ActiveTool, _worldPanelOn, _colorPanel.IsVisible, _outputPanelOn, _playMode.IsPlaying,
            _environmentOpen && _environmentTab != EnvironmentTab.Materials, MatchingAtmospherePreset(), _physicsPanelOn, SelectionAnchorState(),
            SelectionLockState(), _guiPreview, HasProject: ProjectRoot is not null, AnimationOn: _animationPanelOn,
            MaterialOn: _environmentOpen && _environmentTab == EnvironmentTab.Materials);

    /// <summary>
    /// Entering a project: Main Tools, World shown, Output shown (collapsed), no menu, Color, Environment &amp; Graphics or
    /// Physics panel open (the Environment &amp; Graphics panel next opens on its Atmosphere tab).
    /// </summary>
    private void ResetEditorLayout()
    {
        _toolBench.Reset();
        _toolbar.CloseMenu();
        _colorPanel.Close();
        ShowEnvironmentPanel(null);
        _environmentSheet.ResetScroll();
        _environmentTab = EnvironmentTab.Atmosphere;
        _environmentGlobalTab = EnvironmentTab.Atmosphere;
        _worldPanelOn = true;
        _outputPanelOn = true;
        _physicsPanelOn = false;
        CloseAnimationPanel(quiet: true);
        _outputPanel.Collapsed = true;
        _resizingOutput = false;
        SyncSidePanels();
    }

    // The right-hand column shows Color while it is open, else World when it is on (Color never turns World off); the UI
    // section shows its UI panel there instead, and World comes back as it was when the section is left.
    private void SyncSidePanels() => _worldExplorer.SetVisible(_worldPanelOn && !_colorPanel.IsVisible && _toolBench.Current != ToolBenchSection.UI);

    private void HandleToolbar(InputManager input)
    {
        if (_toolbar.IsMenuOpen)
        {
            HandleOpenToolbarMenu(input);
            return;
        }

        if (!input.LeftMousePressed)
        {
            return;
        }

        if (_toolbar.TryHitToolBench(input.MouseX, input.MouseY, _width))
        {
            _toolbar.ToggleMenu(ToolbarMenu.ToolBench);
            _toolbarConsumedClick = true;
            return;
        }

        if (_toolbar.TryHitProject(input.MouseX, input.MouseY, _width))
        {
            _toolbar.ToggleMenu(ToolbarMenu.Project);
            _toolbarConsumedClick = true;
            return;
        }

        if (_toolbar.TryHitPlay(input.MouseX, input.MouseY, _width))
        {
            _toolbarConsumedClick = true;
            if (_playMode.IsPlaying)
            {
                StopPlay(showToast: true);
            }
            else
            {
                StartPlay(input);
                if (_playMode.IsPlaying)
                {
                    ActivateTab(DocumentTabHit.SceneView); // show the scene that is now running
                }
            }

            return;
        }

        if (_toolbar.TryHitControl(input.MouseX, input.MouseY, _width, out var control))
        {
            _toolbarConsumedClick = true;
            HandleToolbarControl(control);
        }
    }

    // An open menu takes the next click wherever it lands: an item runs, a click elsewhere only closes the menu, so the
    // same click never reaches the scene, a panel or another button behind it.
    private void HandleOpenToolbarMenu(InputManager input)
    {
        var overMenu = _toolbar.IsOverMenu(input.MouseX, input.MouseY, _width);
        if (overMenu)
        {
            _wheelConsumed = true;
        }

        if (!input.LeftMousePressed && !input.RightMousePressed)
        {
            return;
        }

        _toolbarConsumedClick = true;
        if (input.LeftMousePressed && _toolbar.TryHitMenuItem(input.MouseX, input.MouseY, _width, out var item))
        {
            _toolbar.CloseMenu();
            RunToolbarCommand(item);
            return;
        }

        // The menu's own padding, header or separators: nothing to do, it stays open.
        if (input.LeftMousePressed && overMenu)
        {
            return;
        }

        _toolbar.CloseMenu();
    }

    private void HandleToolbarControl(ToolbarControl control)
    {
        // Panels are layout, not editing: they toggle in Play Mode too and never change the active tab.
        if (control == ToolbarControl.World)
        {
            ToggleWorldPanel();
            return;
        }

        if (control == ToolbarControl.Output)
        {
            ToggleOutputPanel();
            return;
        }

        // The Environment & Graphics panel opens in Play too (read-only there: Play renders the settings, never edits
        // them). Environment shows its last global tab, Material its Materials tab; the button of the tab shown closes it.
        if (control is ToolbarControl.Environment or ToolbarControl.Material)
        {
            var tab = control == ToolbarControl.Material ? EnvironmentTab.Materials : _environmentGlobalTab;
            var showing = _environmentOpen && (_environmentTab == EnvironmentTab.Materials) == (control == ToolbarControl.Material);
            ShowEnvironmentPanel(showing ? null : tab, showSceneView: true);
            return;
        }

        // Anchor and Lock are properties of the selection, not tools: they never change the active tool (blocked in Play).
        if (control == ToolbarControl.Anchor)
        {
            ToggleAnchor();
            return;
        }

        if (control == ToolbarControl.Lock)
        {
            ToggleLock();
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(control == ToolbarControl.Presets ? "Stop Play Mode to edit Atmosphere." : PlayBlockedMessage);
            return;
        }

        switch (control)
        {
            case ToolbarControl.Move:
                ChooseTransformTool(EditorToolMode.Move);
                break;
            case ToolbarControl.Resize:
                ChooseTransformTool(EditorToolMode.Resize);
                break;
            case ToolbarControl.Rotate:
                ChooseTransformTool(EditorToolMode.Rotate);
                break;
            case ToolbarControl.Color:
                // Scene-editing tools bring the 3D View to the front.
                ActivateTab(DocumentTabHit.SceneView);
                _colorPanel.Toggle();
                SyncSidePanels();
                break;
            case ToolbarControl.AddMeshes:
                _toolbar.ToggleMenu(ToolbarMenu.AddMeshes);
                break;
            case ToolbarControl.Presets:
                _toolbar.ToggleMenu(ToolbarMenu.Presets);
                break;
            case ToolbarControl.GuiFrame:
                AddGuiElement(new Gui.GuiFrame());
                break;
            case ToolbarControl.GuiText:
                AddGuiElement(new Gui.GuiText());
                break;
            case ToolbarControl.GuiButton:
                AddGuiElement(new Gui.GuiButton());
                break;
            case ToolbarControl.GuiPreview:
                _toolbar.ToggleMenu(ToolbarMenu.GuiPreview);
                break;
            case ToolbarControl.AddLight:
                _toolbar.ToggleMenu(ToolbarMenu.AddLight);
                break;
        }
    }

    // Move / Resize / Rotate add their handles on top of ordinary selection, which needs no tool of its own; choosing
    // the active tool again puts it away.
    private void ChooseTransformTool(EditorToolMode tool)
    {
        ActivateTab(DocumentTabHit.SceneView);
        _toolState.SetTool(_toolState.ActiveTool == tool ? EditorToolMode.Select : tool);
        EndAllEditorDrags();
        FinalizeKeyboardMove();
        ResetGizmoHovers();
    }

    private void RunToolbarCommand(ToolbarMenuItem item)
    {
        switch (item.Command)
        {
            case ToolbarCommand.ChooseSection:
                SelectToolBenchSection(item.Section);
                break;
            case ToolbarCommand.AddShape:
                if (_playMode.IsPlaying)
                {
                    _toast.Show(PlayBlockedMessage);
                    break;
                }

                ActivateTab(DocumentTabHit.SceneView);
                _shapeState.SetShape(item.Shape);
                HandleAddShape();
                break;
            case ToolbarCommand.ImportModel:
                if (_playMode.IsPlaying)
                {
                    _toast.Show(PlayBlockedMessage);
                    break;
                }

                ActivateTab(DocumentTabHit.SceneView);
                if (ImportRequested is null)
                {
                    Console.WriteLine("[Import] No project session handles Import here");
                }

                ImportRequested?.Invoke();
                break;
            case ToolbarCommand.SaveProject:
                RequestSaveProject();
                break;
            case ToolbarCommand.LeaveToHub:
                // The project session's leave gate: stops Play, settles unsaved scripts, names or saves the project.
                LeaveRequested?.Invoke();
                break;
            case ToolbarCommand.DeleteProject:
                // An unnamed world has no files to delete (its item is drawn disabled).
                if (ProjectRoot is null)
                {
                    _toast.Show("This world isn't saved as a project yet: there is nothing to delete.");
                    break;
                }

                // The project session asks first, then closes the editor and deletes the project the Hub's way.
                DeleteProjectRequested?.Invoke();
                break;
            case ToolbarCommand.SetGuiPreview:
                SetGuiPreview(item.Preview);
                break;
            case ToolbarCommand.AddLight:
                if (_playMode.IsPlaying)
                {
                    _toast.Show(PlayBlockedMessage);
                    break;
                }

                ActivateTab(DocumentTabHit.SceneView);
                AddLight(item.Light);
                break;
            case ToolbarCommand.OpenPhysics:
                // A panel, not a section or a tool: the current section, its tools and the active tool stay; Play too.
                OpenPhysicsPanel();
                break;
            case ToolbarCommand.OpenAnimation:
                // Likewise a panel (in Play it shows the running animation, read-only).
                OpenAnimationPanel();
                break;
            case ToolbarCommand.ApplyPreset:
            case ToolbarCommand.ResetAtmosphere:
                if (_playMode.IsPlaying)
                {
                    _toast.Show("Stop Play Mode to edit Atmosphere.");
                    break;
                }

                ShowEnvironmentPanel(EnvironmentTab.Atmosphere, showSceneView: true);
                if (item.Command == ToolbarCommand.ResetAtmosphere)
                {
                    ResetAtmosphere();
                }
                else
                {
                    ApplyAtmospherePreset(item.Label);
                }

                break;
        }
    }

    // A section switch only changes which tools the toolbar shows: the tab, camera, selection, Play and the World /
    // Output panels stay as they are. Tool panels (Color, Environment & Graphics) belong to their section's button: they
    // are put away with a section without it; the Environment & Graphics panel opens with its section (on its last tab).
    private void SelectToolBenchSection(ToolBenchSection section)
    {
        if (!_toolBench.Select(section))
        {
            return;
        }

        // A GUI drag or panel edit ends with its section; both selections stay as they are. Gizmo hovers belong to the 3D
        // view. The right-hand column follows the section (UI panel or World).
        CommitGuiDrag();
        CommitGuiPanelEdits();
        ResetGizmoHovers();
        SyncSidePanels();

        if (_colorPanel.IsVisible && !_toolBench.CurrentControls.Contains(ToolbarControl.Color))
        {
            _colorPanel.Close();
            SyncSidePanels();
        }

        ShowEnvironmentPanel(_toolBench.CurrentControls.Contains(ToolbarControl.Environment) ? _environmentTab : null);
    }

    // Only the World button changes World. While Color covers the column, World brings it back instead of turning off.
    private void ToggleWorldPanel()
    {
        if (_colorPanel.IsVisible)
        {
            _colorPanel.Close();
            _worldPanelOn = true;
        }
        else
        {
            _worldPanelOn = !_worldPanelOn;
        }

        SyncSidePanels();
    }

    // Only the Output button changes Output. Hidden, it keeps collecting; shown again, it opens on the retained rows.
    private void ToggleOutputPanel()
    {
        _outputPanelOn = !_outputPanelOn;
        _resizingOutput = false;
        if (_outputPanelOn)
        {
            _outputPanel.Collapsed = false;
            _outputPanel.ScrollToBottom();
        }

    }
}
