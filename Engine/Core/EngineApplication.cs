using Wisko.Engine.Assets;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.Platform;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.UI;
using Wisko.Engine.World;
using OpenTK.Windowing.Common.Input;

namespace Wisko.Engine.Core;

public sealed class EngineApplication : IDisposable
{
    private const float ErrorToastSeconds = 4f;

    private readonly EngineWindow _window;
    private readonly InputManager _input;
    private readonly Renderer _renderer;
    private readonly IGameModule _game;
    private readonly Workspace _workspace = new();
    private readonly EditorSelection _selection = new();
    private readonly ProjectStore _projects;
    private readonly ProjectHubRenderer _hub = new();
    private readonly TextPrompt _namePrompt = new();
    private readonly ProjectHubController _hubController;

    // Model assets of the current project (CPU side; the renderer keeps the GPU meshes per asset).
    private readonly ModelAssetCache _modelAssets = new();

    // The current project's game GUI (UI.json). False save permission only when an unreadable UI.json could not be
    // kept aside: then it is never overwritten this session.
    private GuiDocument _gui = new();
    private bool _guiSaveAllowed = true;
    private AppScreen _screen = AppScreen.Hub;

    // Null while in the editor means the session is unnamed and lives only in memory.
    private ProjectInfo? _currentProject;
    private WorldTemplate _sessionTemplate = WorldTemplate.Classic;
    private LeaveAction _pendingLeave = LeaveAction.None;
    private PromptPurpose _promptPurpose = PromptPurpose.ProjectName;

    // The object "+ New Script" / "+ Attach Script" was clicked for, kept while a prompt or the naming flow runs.
    private Part? _pendingScriptTarget;

    // A native file dialog is open (it blocks this frame); a close request arriving meanwhile is refused.
    private bool _fileDialogOpen;
    private bool _closeApproved;
    private bool _isRunning;
    private bool _firstFrameReported;

    // Windows without Microsoft's Visual C++ runtime: explained once, after the Hub's first frame is on screen.
    private bool _runtimeNoticePending;

    public EngineApplication(EngineApplicationSettings settings, IGameModule game)
    {
        // Refuse an unsupported bgfx platform before any window or renderer exists (no silent fallback).
        if (settings.RenderBackend == RenderBackendKind.Bgfx
            && !Engine.Rendering.Backend.Bgfx.BgfxDevice.IsPlatformSupported(out var reason))
        {
            throw new PlatformNotSupportedException(reason);
        }

        _game = game;
        _projects = new ProjectStore(settings.ProjectsRootPath);
        _input = new InputManager();

        // History lives beside the projects folder (it outlives the projects it records), unless the host says where.
        var historyPath = settings.HistoryPath
            ?? Path.Combine(Path.GetDirectoryName(_projects.RootPath) ?? _projects.RootPath, ".wisko-history");
        _hubController = new ProjectHubController(_projects, new ProjectHistory(historyPath), _hub, _input)
        {
            IsProjectInUse = project => _screen == AppScreen.Editor && _currentProject?.Id == project.Id,
            ProjectDeleted = ForgetDeletedProject
        };
        _renderer = new Renderer(settings.Width, settings.Height, settings.RenderBackend);
        _hub.TemplatePreviewNote = () => _renderer.Capabilities.TemplatePreviewNote;

        // The person's editor preferences belong to this computer, not to any project: %LOCALAPPDATA%\Wisko (macOS:
        // ~/Library/Application Support/Wisko).
        _editorPreferencesPath = settings.EditorPreferencesPath ?? Path.Combine(Graphics.EditorGraphicsPreferences.DefaultFolder(),
            Graphics.EditorGraphicsPreferences.FileName);
        _renderer.EditorGraphics = Graphics.EditorGraphicsPreferences.Load(_editorPreferencesPath);
        _renderer.EditorGraphicsChanged = preferences =>
        {
            if (!preferences.TrySave(_editorPreferencesPath, out var error))
            {
                Console.WriteLine($"[Graphics] Could not save editor preferences: {error}");
            }
        };

        // The scripts' Lua memory limit, per computer too, so a project made by someone else cannot raise it.
        var scriptPreferences = Scripting.EditorScriptPreferences.Load(Path.Combine(Path.GetDirectoryName(_editorPreferencesPath) ?? ".",
            Scripting.EditorScriptPreferences.FileName));
        _renderer.ScriptMemoryLimitBytes = scriptPreferences.MemoryLimitBytes;
        Console.WriteLine($"[Scripts] Lua memory limit: {scriptPreferences.MemoryLimitBytes >> 20} MB per Play session");
        _window = EngineWindow.Create(settings, _input, _renderer, this);
        _renderer.CursorLockChanged = locked => _window.SetGameCursorLocked(locked);
    }

    private enum AppScreen
    {
        Hub,
        Editor
    }

    private enum LeaveAction
    {
        None,
        Quit,
        ToHub,

        // Not a leave: the toolbar Import continues once the unnamed world has been named (assets need its folder).
        Import,

        // Not a leave: "+ New Script" / "+ Attach Script" continue once the unnamed world has been named.
        NewScript,
        AttachScript
    }

    private enum PromptPurpose
    {
        ProjectName,
        ScriptName
    }

    public void Run()
    {
        _isRunning = true;
        _window.Run();
    }

    internal void Initialize()
    {
        // Backend GPU setup first: the scene's meshes are created as soon as it is composed.
        _renderer.PrepareBackend();

        var scene = new SceneComposer();

        if (_game is ISceneProvider sceneProvider)
        {
            sceneProvider.BuildScene(scene);
        }

        // The workspace starts empty; IWorkspaceProvider content is only used by the Testing World template.
        _renderer.Initialize(scene, _workspace, _selection);
        _renderer.SaveRequested = HandleSaveRequested;
        _renderer.LeaveRequested = RequestReturnToHub;
        _renderer.DeleteProjectRequested = RequestDeleteCurrentProject;
        _renderer.ImportRequested = HandleImportRequested;
        _renderer.TextureRequested = PickTexture;
        _renderer.NewScriptRequested = HandleNewScriptRequested;
        _renderer.AttachScriptRequested = HandleAttachScriptRequested;
        _renderer.SetClipboard(new WindowClipboard(_window));
        var ui = _renderer.UiBackend;
        _hub.Initialize(ui);
        _namePrompt.Initialize(ui);
        Console.WriteLine($"[ProjectHub] Projects root: {_projects.RootPath}");

        // What an interrupted Delete Project left behind (already out of the library) goes now.
        _projects.PurgeDeletedProjects();
        ShowHub();
        _game.OnInitialize();

        // Model import and file dialogs need Microsoft's Visual C++ runtime on Windows (Assimp64.dll, nfd.dll).
        if (OperatingSystem.IsWindows() || VisualCppRuntime.OverrideForTests is not null)
        {
            Console.WriteLine($"[Startup] Visual C++ runtime: {VisualCppRuntime.LogLine(VisualCppRuntime.Current)}");
            _runtimeNoticePending = VisualCppRuntime.Problem is not null;
        }
    }

    internal void Update(float deltaTime)
    {
        if (_runtimeNoticePending && _firstFrameReported)
        {
            _runtimeNoticePending = false;
            VisualCppRuntime.OfferDownload();
        }

        Time.DeltaTime = deltaTime;
        Time.TotalTime += deltaTime;

        _input.BeginFrame(_window);
        if (_screen == AppScreen.Hub)
        {
            UpdateHub();
        }
        else if (_namePrompt.IsOpen)
        {
            UpdateNamePrompt(deltaTime);
        }
        else
        {
            _renderer.Update(_input, deltaTime);
        }

        _input.EndFrame();
    }

    internal void Render()
    {
        if (_screen == AppScreen.Hub)
        {
            _renderer.BeginScreenUiFrame();
            _hub.Render(_renderer.LogicalWidth, _renderer.LogicalHeight, _renderer.UiDpiScale);
        }
        else
        {
            _renderer.Render();
            _namePrompt.Render(_renderer.LogicalWidth, _renderer.LogicalHeight, _renderer.UiDpiScale);
        }

        if (!_firstFrameReported)
        {
            // Startup time as the person sees it: from the process starting to the first drawn frame.
            _firstFrameReported = true;
            var sinceStart = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Console.WriteLine(FormattableString.Invariant($"[Startup] First frame {sinceStart.TotalMilliseconds:F0} ms after the process started"));
        }
    }

    /// <summary>Escape: closes the name prompt, else lets the editor consume it. False means "close the app".</summary>
    internal bool TryConsumeEscape()
    {
        if (_screen == AppScreen.Hub)
        {
            return _hubController.TryConsumeEscape();
        }

        if (_namePrompt.IsOpen)
        {
            CancelNamePrompt();
            return true;
        }

        return _renderer.TryConsumeEscape();
    }

    internal bool ShouldBeginRightMouseLook(float mouseX, float mouseY) =>
        _screen == AppScreen.Editor
        && !_namePrompt.IsOpen
        && _renderer.ShouldBeginRightMouseLook(mouseX, mouseY);

    /// <summary>
    /// Window close gate. False vetoes the close (the Save | Don't Save | Cancel question, a name prompt or a save
    /// error is shown instead; the answer closes the window itself).
    /// </summary>
    internal bool RequestClose()
    {
        if (_closeApproved || _screen == AppScreen.Hub)
        {
            return true;
        }

        // Quitting from inside a file dialog would save the scene before the dialog's result (an import or attach)
        // is applied and then lose that result; the user can quit once the dialog is closed.
        if (_fileDialogOpen)
        {
            Console.WriteLine("[Project] Close requested while a file dialog is open: ignored");
            return false;
        }

        return TryLeaveEditor(LeaveAction.Quit);
    }

    /// <summary>
    /// Project menu Leave to Hub: straight to the Hub when nothing changed; otherwise Save | Don't Save | Cancel
    /// (see <see cref="TryLeaveEditor"/>).
    /// </summary>
    internal void RequestReturnToHub()
    {
        if (_screen == AppScreen.Editor && TryLeaveEditor(LeaveAction.ToHub))
        {
            CaptureThumbnail();
            ShowHub();
        }
    }

    /// <summary>
    /// Project menu Delete Project…: asks first (the Hub's question). On Delete Project the editor closes without
    /// saving, then the project is deleted exactly as from the Hub (validated, preview kept, History shows it Deleted)
    /// and the Hub opens. Cancel leaves the editor untouched.
    /// </summary>
    internal void RequestDeleteCurrentProject()
    {
        if (_screen != AppScreen.Editor || _currentProject is not { } project || _renderer.IsConfirmOpen)
        {
            return;
        }

        _renderer.ConfirmDeleteProject(project.Name, () => DeleteCurrentProject(project));
    }

    // After the confirmation. Nothing is saved: the project is going away, and an auto-save now would write into the
    // folder being deleted. The session lets go of the project first (no save, leave, quit, thumbnail, import or
    // script tab can reach its folder afterwards), then the Hub's one deletion path runs.
    private void DeleteCurrentProject(ProjectInfo project)
    {
        if (_screen != AppScreen.Editor || _currentProject?.Id != project.Id)
        {
            return;
        }

        // Refused before anything closes (e.g. the folder was moved meanwhile): the editor stays exactly as it was.
        if (!_projects.TryValidateDeletion(project, out _, out var problem))
        {
            Console.WriteLine($"[Project] Delete refused: {problem}");
            _renderer.ShowToast(problem ?? "Couldn't delete the project", ErrorToastSeconds);
            return;
        }

        if (_renderer.IsPlaying)
        {
            Console.WriteLine("[PlayMode] Deleting the project: stopping Play first");
            _renderer.StopPlay(showToast: false);
        }

        Console.WriteLine($"[Project] Closing \"{project.Name}\" without saving to delete it");
        _currentProject = null;
        _modelAssets.SetProject(null);
        _renderer.ProjectRoot = null; // closes every script tab; unsaved script edits are discarded with the project
        _gui = new GuiDocument();
        _guiSaveAllowed = true;
        _renderer.LoadGui(_gui);

        // The Hub first (it clears the status line), then the deletion, whose failure is shown there: the project
        // then stays Hub Active and can be opened again.
        ShowHub();
        _hubController.DeleteProject(project);
    }

    internal void Shutdown()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _game.OnShutdown();
        _hub.Dispose();
        _namePrompt.Dispose();
        _renderer.Dispose();
        _modelAssets.Dispose();
    }

    public void Dispose()
    {
        Shutdown();
        _window.Dispose();
    }

    // The single leave gate. Play Mode stops first (the edit scene is restored), so everything after this sees, names
    // and saves the edit scene. No changes since the last save: leave (true). Changes: Save | Don't Save | Cancel
    // (false; the chosen answer finishes the leave itself). Nothing is ever saved without being asked.
    private bool TryLeaveEditor(LeaveAction action)
    {
        if (_renderer.IsPlaying)
        {
            Console.WriteLine($"[PlayMode] Leaving the editor ({action}): stopping Play first");
            _renderer.StopPlay(showToast: false);
        }

        // A UI value still being typed is kept, as clicking away would keep it.
        _renderer.FinishGuiEdits();

        // Leaving abandons an unfinished New Script (nothing has been created yet) and carries on.
        if (_namePrompt.IsOpen && _promptPurpose == PromptPurpose.ScriptName)
        {
            CancelNamePrompt();
        }

        // A question is already on screen (e.g. unsaved scripts): answer that first.
        if (_renderer.IsConfirmOpen)
        {
            return false;
        }

        // Never lose code: unsaved script tabs are saved or deliberately discarded before anything else; the leave
        // continues from the start once they are.
        if (_renderer.HasDirtyScripts)
        {
            Console.WriteLine($"[ScriptEditor] Leaving ({action}) with unsaved scripts: asking first");
            _renderer.PromptUnsavedScripts(() => ContinueLeave(action));
            return false;
        }

        if (_namePrompt.IsOpen)
        {
            _pendingLeave = action;
            return false;
        }

        // Nothing changed since the last save (or since this new world began): leave at once, writing nothing. An
        // unnamed world that was never changed is simply gone (it never existed on disk or in History).
        if (!HasUnsavedChanges)
        {
            Console.WriteLine(_currentProject is null
                ? "[Project] Leaving an unnamed world without changes: nothing saved, nothing kept"
                : $"[Project] Leaving \"{_currentProject.Name}\": no unsaved changes");
            return true;
        }

        AskToSaveBeforeLeaving(action);
        return false;
    }

    /// <summary>
    /// The scene, UI or Atmosphere differ from what was last saved (or, for a new world, from how it began). Compared as
    /// the files' own text, so any change that would be saved counts and nothing else does.
    /// </summary>
    internal bool HasUnsavedChanges => _screen == AppScreen.Editor && !string.Equals(_savedState, CurrentState(), StringComparison.Ordinal);

    private string? _savedState;

    private string CurrentState() =>
        SceneFile.ToJson(_workspace) + "\n" + GuiFile.ToJson(_gui) + "\n" + AtmosphereFile.ToJson(_renderer.Atmosphere)
        + "\n" + Graphics.GraphicsFile.ToJson(_renderer.Graphics);

    private readonly string _editorPreferencesPath;

    // What is on disk now (or how a new world began): the reference for HasUnsavedChanges.
    private void MarkSaved() => _savedState = CurrentState();

    // Save | Don't Save | Cancel. Save: a named project saves (an unnamed world asks for its name first) and then
    // leaves; Don't Save leaves without writing anything (a saved project keeps its last saved version on disk; an
    // unnamed world is discarded); Cancel stays.
    private void AskToSaveBeforeLeaving(LeaveAction action)
    {
        var project = _currentProject;
        Console.WriteLine($"[Project] Leaving ({action}) with unsaved changes: asking Save / Don't Save / Cancel");
        _renderer.Ask(new ConfirmRequest(
            project is null ? "Save this new world?" : $"Save changes to \"{project.Name}\"?",
            project is null
                ? "It has never been saved. If you don't save it, it is discarded and nothing is kept."
                : "If you don't save, the changes since the last save are lost; the saved project stays as it was.",
            ["Save", "Don't Save", "Cancel"],
            choice =>
            {
                switch (choice)
                {
                    case 0:
                        SaveThenLeave(action);
                        break;
                    case 1:
                        Console.WriteLine(project is null
                            ? "[Project] Don't Save: the unnamed world is discarded"
                            : $"[Project] Don't Save: \"{project.Name}\" keeps its last saved version");
                        FinishLeave(action, saved: false);
                        break;
                    default:
                        Console.WriteLine("[Project] Leave cancelled: staying in the editor");
                        break;
                }
            },
            DefaultButton: 0,
            DestructiveButton: 1));
    }

    private void SaveThenLeave(LeaveAction action)
    {
        if (_currentProject is null)
        {
            // Naming creates the project and then carries on with the leave (see UpdateNamePrompt).
            OpenNamePrompt(action);
            return;
        }

        if (!TrySaveCurrentProject(out var error))
        {
            Console.WriteLine($"[Project] Save failed, staying in editor: {error}");
            _renderer.ShowToast("Save failed - project not closed", ErrorToastSeconds);
            return;
        }

        MarkSaved();
        Console.WriteLine($"[Project] Saved \"{_currentProject.Name}\" ({_workspace.Parts.Count} parts)");
        FinishLeave(action, saved: true);
    }

    private void FinishLeave(LeaveAction action, bool saved)
    {
        switch (action)
        {
            case LeaveAction.Quit:
                _closeApproved = true;
                _window.Close();
                break;
            case LeaveAction.ToHub:
                // The preview shows the saved scene: not taken when the changes were thrown away.
                if (saved)
                {
                    CaptureThumbnail();
                }

                ShowHub();
                break;
        }
    }

    // Retries a leave after the unsaved-scripts question was settled (it passes the gate again from the start).
    private void ContinueLeave(LeaveAction action)
    {
        switch (action)
        {
            case LeaveAction.Quit:
                _window.Close();
                break;
            case LeaveAction.ToHub:
                RequestReturnToHub();
                break;
        }
    }

    private void ShowHub()
    {
        _renderer.CloseAllScripts();
        _renderer.ReleaseProjectGraphics();
        _screen = AppScreen.Hub;
        _hubController.Show();
    }

    private void UpdateHub()
    {
        switch (_hubController.Update(_renderer.LogicalWidth, _renderer.LogicalHeight, out var project, out var template))
        {
            case ProjectHubRequest.NewWorld:
                StartUnnamedSession(template);
                break;
            case ProjectHubRequest.OpenProject when project is not null:
                OpenProject(project);
                break;
        }
    }

    // A project deleted in the Hub: if it was the last one open, nothing keeps pointing at its (gone) folder.
    private void ForgetDeletedProject(ProjectInfo project)
    {
        if (_currentProject?.Id != project.Id)
        {
            return;
        }

        _currentProject = null;
        _modelAssets.SetProject(null);
        _renderer.ProjectRoot = null;
    }

    private void StartUnnamedSession(WorldTemplate template)
    {
        _currentProject = null;
        _modelAssets.SetProject(null);
        _renderer.ProjectRoot = null;
        _sessionTemplate = template;
        _gui = new GuiDocument();
        _guiSaveAllowed = true;
        _renderer.LoadGui(_gui);
        _renderer.LoadAtmosphere(AtmosphereSettings.Default);
        _renderer.LoadGraphics(Graphics.GraphicsSettings.Default); // a new world starts on the Universal renderer
        _renderer.ReplaceWorkspace(BuildTemplateParts(template), resetCamera: true);
        _screen = AppScreen.Editor;
        MarkSaved(); // the template as it began: leaving it untouched asks nothing and keeps nothing
        Console.WriteLine($"[Project] New unnamed {template.DisplayName()} world (in memory, {_workspace.Parts.Count} parts)");
        _renderer.ShowGraphicsCompatibilityNotice("new world");
    }

    private List<Part> BuildTemplateParts(WorldTemplate template)
    {
        if (template != WorldTemplate.Testing)
        {
            return [];
        }

        if (_game is not IWorkspaceProvider workspaceProvider)
        {
            Console.WriteLine("[Project] No IWorkspaceProvider: Testing World starts empty");
            return [];
        }

        var templateWorkspace = new Workspace();
        workspaceProvider.BuildWorkspace(templateWorkspace);
        return templateWorkspace.Parts.ToList();
    }

    private void CaptureThumbnail()
    {
        if (_currentProject is null)
        {
            return;
        }

        if (!_renderer.TryCaptureViewportThumbnail(ProjectStore.ThumbnailWidth, ProjectStore.ThumbnailHeight, out var rgba))
        {
            Console.WriteLine("[Project] Thumbnail capture skipped (renderer not ready)");
            return;
        }

        if (!_projects.TryWriteThumbnail(_currentProject, rgba, ProjectStore.ThumbnailWidth, ProjectStore.ThumbnailHeight, out var error))
        {
            Console.WriteLine($"[Project] Could not write thumbnail for \"{_currentProject.Name}\": {error}");
            return;
        }

        Console.WriteLine($"[Project] Thumbnail updated: {_currentProject.ThumbnailPath}");
    }

    private void OpenProject(ProjectInfo project)
    {
        // A save interrupted earlier (a crash, a power cut) is finished, or reported, before anything is read.
        var recovery = ProjectSaveJournal.Recover(project.FolderPath);
        if (recovery.Outcome != ProjectRecoveryOutcome.Nothing)
        {
            Console.WriteLine($"[Save] \"{project.Name}\": {recovery.Message}");
        }

        // Model instances resolve their project-relative asset through the cache: one decode per asset file.
        _modelAssets.SetProject(project.FolderPath);
        var unavailableModels = 0;
        ModelAsset? ResolveModel(string assetPath)
        {
            var asset = _modelAssets.Load(assetPath, out _);
            if (asset is null)
            {
                unavailableModels++;
            }

            return asset;
        }

        var scene = new SceneContents([], []);
        if (File.Exists(project.ScenePath)
            && !SceneFile.TryLoadScene(project.ScenePath, ResolveModel, out scene, out var loadError))
        {
            Console.WriteLine($"[Project] Could not open \"{project.Name}\": {loadError}");
            _hub.ShowStatus($"Couldn't open \"{project.Name}\": {loadError}");
            return;
        }

        if (!_projects.TryMarkOpened(project, out var metadataError))
        {
            Console.WriteLine($"[Project] Could not update last-opened time for \"{project.Name}\": {metadataError}");
        }

        _hubController.NoteProjectActive(project);

        // A project from before Atmosphere has no Atmosphere.json: it opens with the defaults (written on its next save).
        var atmosphere = AtmosphereFile.Load(project.AtmospherePath, out var atmosphereStatus, out var atmosphereProblem);
        if (atmosphereStatus == AtmosphereLoadStatus.Invalid)
        {
            Console.WriteLine($"[Atmosphere] Could not read {project.AtmospherePath}: {atmosphereProblem}; using the defaults");
        }

        // A project without Graphics.json is an older one and keeps the Classic renderer.
        var graphics = Graphics.GraphicsFile.Load(project.GraphicsPath, out var graphicsStatus, out var graphicsProblem);
        if (graphicsStatus == Graphics.GraphicsLoadStatus.Invalid)
        {
            Console.WriteLine($"[Graphics] Could not read {project.GraphicsPath}: {graphicsProblem}; using the Classic renderer");
        }

        // A project without UI.json opens with an empty Canvas; an unreadable one is kept aside, never overwritten.
        var gui = GuiFile.Load(project.UiPath, out var guiReport);

        _currentProject = project;
        _sessionTemplate = project.WorldTemplate;
        _gui = gui;
        _guiSaveAllowed = guiReport.SafeToSave;
        _renderer.LoadGui(gui);
        _renderer.ProjectRoot = project.FolderPath;
        _renderer.LoadAtmosphere(atmosphere);
        _renderer.LoadGraphics(graphics);
        _renderer.ReplaceWorkspace(scene.Parts, resetCamera: true, scene.RemovedEnvironment);
        _screen = AppScreen.Editor;
        MarkSaved();
        Console.WriteLine($"[Project] Opened \"{project.Name}\" ({scene.Parts.Count} parts, Atmosphere {atmosphereStatus}, UI {guiReport.Status}: {gui.Count} elements, Graphics {graphicsStatus}: {graphics.Renderer}"
            + (scene.RemovedEnvironment.Count > 0 ? $", deleted environment: {string.Join(", ", scene.RemovedEnvironment)})" : ")"));
        _renderer.ShowGraphicsCompatibilityNotice(project.Id);
        if (guiReport.Status == GuiLoadStatus.Invalid)
        {
            _renderer.ShowToast(guiReport.PreservedPath is null
                ? "UI.json couldn't be read, and it won't be overwritten"
                : "UI.json couldn't be read: kept as UI.json.invalid", ErrorToastSeconds);
        }
        if (atmosphereStatus == AtmosphereLoadStatus.Invalid)
        {
            _renderer.ShowToast("Atmosphere settings couldn't be read: using the defaults", ErrorToastSeconds);
        }
        if (unavailableModels > 0)
        {
            // Kept as placeholder boxes with their saved transforms, so saving does not lose them.
            _renderer.ShowToast(
                unavailableModels == 1 ? "1 model couldn't be loaded" : $"{unavailableModels} models couldn't be loaded",
                ErrorToastSeconds);
        }

        switch (recovery.Outcome)
        {
            case ProjectRecoveryOutcome.Completed:
                _renderer.ShowToast("An interrupted save of this project was finished");
                break;
            case ProjectRecoveryOutcome.NotApplied:
                _renderer.ShowToast("An interrupted save had changed nothing: the project is as it was saved last");
                break;
            case ProjectRecoveryOutcome.Inconsistent:
                _renderer.ShowToast("An interrupted save could not be finished safely; nothing was changed (see the log)", ErrorToastSeconds);
                break;
        }
    }

    private void OpenNamePrompt(LeaveAction action, string? subtitle = null)
    {
        _pendingLeave = action;
        _promptPurpose = PromptPurpose.ProjectName;
        _namePrompt.Open(subtitle is null ? TextPromptOptions.ProjectName() : TextPromptOptions.ProjectName(subtitle));
    }

    private void CancelNamePrompt()
    {
        var purpose = _promptPurpose;
        var cancelled = _pendingLeave;
        _namePrompt.Close();
        _pendingLeave = LeaveAction.None;
        _promptPurpose = PromptPurpose.ProjectName;
        _pendingScriptTarget = null;
        if (purpose == PromptPurpose.ScriptName)
        {
            Console.WriteLine("[Scripts] New script cancelled");
            return;
        }

        if (cancelled == LeaveAction.Import)
        {
            _renderer.ShowToast("Name this project before importing assets.", ErrorToastSeconds);
        }
        else if (cancelled is LeaveAction.NewScript or LeaveAction.AttachScript)
        {
            _renderer.ShowToast("Name this project before adding scripts.", ErrorToastSeconds);
        }
    }

    private void UpdateNamePrompt(float deltaTime)
    {
        var result = _namePrompt.Update(_input, _renderer.LogicalWidth, _renderer.LogicalHeight, deltaTime);
        if (result == TextPromptResult.Cancelled)
        {
            CancelNamePrompt();
            return;
        }

        if (result != TextPromptResult.Confirmed)
        {
            return;
        }

        if (_promptPurpose == PromptPurpose.ScriptName)
        {
            ConfirmNewScript();
            return;
        }

        if (!_projects.TryCreateProject(_namePrompt.Text, _sessionTemplate, _workspace, out var project, out var error))
        {
            _namePrompt.SetError(error);
            return;
        }

        _currentProject = project;
        _hubController.NoteProjectActive(project);
        _modelAssets.SetProject(project.FolderPath);
        _renderer.ProjectRoot = project.FolderPath;
        // The new project's Atmosphere, UI and graphics settings join its scene, saved together like any later save.
        var allSaved = true;
        if (!TrySaveCurrentProject(out var settingsError))
        {
            Console.WriteLine($"[Project] Could not save the new project's settings: {settingsError}");
            _renderer.ShowToast("The project's settings couldn't be saved", ErrorToastSeconds);
            allSaved = false;
        }

        // Something that couldn't be written still counts as unsaved, so a later leave asks again.
        if (allSaved)
        {
            MarkSaved();
        }
        else
        {
            _savedState = null;
        }

        var action = _pendingLeave;
        _namePrompt.Close();
        _pendingLeave = LeaveAction.None;
        Console.WriteLine($"[Project] Created \"{project.Name}\" at {project.FolderPath} ({_workspace.Parts.Count} parts)");

        switch (action)
        {
            case LeaveAction.Quit:
                _closeApproved = true;
                _window.Close();
                break;
            case LeaveAction.ToHub:
                CaptureThumbnail();
                ShowHub();
                break;
            case LeaveAction.Import:
                Console.WriteLine("[Import] Project named; continuing the import");
                PickAndImportModel();
                break;
            case LeaveAction.NewScript:
                Console.WriteLine("[Scripts] Project named; continuing New Script");
                OpenScriptNamePrompt();
                break;
            case LeaveAction.AttachScript:
                Console.WriteLine("[Scripts] Project named; continuing Attach Script");
                PickAndAttachScript();
                break;
            default:
                _renderer.ShowToast($"Saved as \"{project.Name}\"");
                break;
        }
    }

    // Import Model (Add Meshes or the project menu). Assets live in the project folder, so an unnamed world is named first through the existing
    // prompt and the import continues afterwards; cancelling the prompt cancels the import.
    private void HandleImportRequested()
    {
        if (_renderer.IsPlaying)
        {
            _renderer.ShowToast("Stop Play Mode to edit the scene.");
            return;
        }

        if (_currentProject is null)
        {
            Console.WriteLine("[Import] Unnamed world: asking for a project name first");
            OpenNamePrompt(LeaveAction.Import);
            return;
        }

        PickAndImportModel();
    }

    // Materials tab texture: an image picked by the person, brought into the project (Assets/Textures).
    private string? PickTexture()
    {
        if (_currentProject is null)
        {
            _renderer.ShowToast("Save the project first: textures are copied into it.");
            return null;
        }

        FilePickResult result;
        string? path;
        string? pickError;
        _fileDialogOpen = true;
        try
        {
            result = NativeFilePicker.TryPickFile([new FileTypeFilter("Images (PNG, JPEG, TGA, BMP)", ProjectTextureImporter.Extensions)],
                Path.Combine(_currentProject.FolderPath, "Assets"), out path, out pickError);
        }
        finally
        {
            _fileDialogOpen = false;
        }

        if (result == FilePickResult.Cancelled)
        {
            return null;
        }

        if (result != FilePickResult.Picked || path is null)
        {
            Console.WriteLine($"[Texture] File picker failed: {pickError}");
            ReportFilePickerFailure();
            return null;
        }

        if (!ProjectTextureImporter.TryImport(path, _currentProject.FolderPath, out var relative, out var error))
        {
            Console.WriteLine($"[Texture] Not used: {error}");
            _renderer.ShowToast($"Texture not used: {error}", ErrorToastSeconds);
            return null;
        }

        Console.WriteLine($"[Texture] {path} -> {relative}");
        return relative;
    }

    // A file dialog could not open. Without Microsoft's Visual C++ runtime (Windows) that is the reason: name it and
    // offer Microsoft's download instead of a bare failure.
    private void ReportFilePickerFailure()
    {
        if (VisualCppRuntime.FileDialogsBlocked && VisualCppRuntime.ShortProblem is { } problem)
        {
            _renderer.ShowToast($"The file dialog {problem}", ErrorToastSeconds);
            VisualCppRuntime.OfferDownload();
            return;
        }

        _renderer.ShowToast("Couldn't open the file picker", ErrorToastSeconds);
    }

    private void PickAndImportModel()
    {
        FilePickResult result;
        string? path;
        string? pickError;
        _fileDialogOpen = true;
        try
        {
            result = ModelFilePicker.TryPick(out path, out pickError);
        }
        finally
        {
            _fileDialogOpen = false;
        }

        switch (result)
        {
            case FilePickResult.Cancelled:
                Console.WriteLine("[Import] Cancelled");
                return;
            case FilePickResult.Picked when path is not null:
                ImportModelFile(path);
                return;
            default:
                Console.WriteLine($"[Import] File picker failed: {pickError}");
                ReportFilePickerFailure();
                return;
        }
    }

    // Copies the chosen file into the project (ModelAssetCache -> ProjectModelImporter), then places one instance.
    private void ImportModelFile(string path)
    {
        try
        {
            if (!_modelAssets.TryImport(path, out var import, out var error))
            {
                Console.WriteLine($"[Import] Failed: {error}");
                _renderer.ShowToast($"Import failed: {error}", ErrorToastSeconds);
                return;
            }

            var part = _renderer.AddModelInstance(import.Asset, import.DisplayName);
            var missing = import.MissingFiles.Count switch
            {
                0 => string.Empty,
                1 => " (1 referenced file missing)",
                var count => $" ({count} referenced files missing)"
            };
            _renderer.ShowToast($"Imported \"{part?.Name ?? import.DisplayName}\"{missing}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Import] Unexpected failure: {ex}");
            _renderer.ShowToast("Import failed", ErrorToastSeconds);
        }
    }

    private void HandleSaveRequested()
    {
        // Play Mode changes are temporary: only the edit scene is ever saved.
        if (_renderer.IsPlaying)
        {
            _renderer.ShowToast("Stop Play Mode before saving.");
            return;
        }

        // A UI value still being typed is saved, as clicking away would keep it.
        _renderer.FinishGuiEdits();
        if (_currentProject is null)
        {
            OpenNamePrompt(LeaveAction.None);
            return;
        }

        // Unsaved script tabs are saved with the project (each through the script editor's safe save).
        var scriptsSaved = _renderer.SaveUnsavedScripts(out var scriptFailure);
        if (!TrySaveCurrentProject(out var error))
        {
            Console.WriteLine($"[SaveScene] Failed: {error}");
            _renderer.ShowToast(scriptFailure is null ? "Save failed" : $"Save failed; {scriptFailure}", ErrorToastSeconds);
            return;
        }

        MarkSaved();
        Console.WriteLine($"[SaveScene] Saved {_workspace.Parts.Count} parts to {_currentProject.ScenePath}");
        if (scriptFailure is not null)
        {
            _renderer.ShowToast($"Scene saved, but {scriptFailure}", ErrorToastSeconds);
        }
        else
        {
            _renderer.ShowToast(scriptsSaved switch
            {
                0 => "Scene saved",
                1 => "Scene and 1 script saved",
                _ => $"Scene and {scriptsSaved} scripts saved"
            });
        }
    }

    // "+ New Script": scripts live in the project folder, so an unnamed world is named first (the flow continues).
    private void HandleNewScriptRequested(Part target)
    {
        if (_renderer.IsPlaying)
        {
            return;
        }

        _pendingScriptTarget = target;
        if (_currentProject is null)
        {
            Console.WriteLine("[Scripts] Unnamed world: asking for a project name first");
            OpenNamePrompt(LeaveAction.NewScript, "Scripts are kept in the project folder; name it first.");
            return;
        }

        OpenScriptNamePrompt();
    }

    private void OpenScriptNamePrompt()
    {
        var targetName = _pendingScriptTarget?.Name ?? "the selected object";
        _promptPurpose = PromptPurpose.ScriptName;
        _namePrompt.Open(new TextPromptOptions(
            "New script",
            $"Saved in Assets/Scripts and attached to {targetName}",
            "Create",
            ProjectScriptAssets.MaxScriptNameLength,
            "e.g. Spin"));
    }

    // Creates Assets/Scripts/<name>.lua (unique, never overwriting) from the template and attaches it. Not run.
    private void ConfirmNewScript()
    {
        if (_currentProject is null)
        {
            CancelNamePrompt();
            return;
        }

        if (!ProjectScriptAssets.TryValidateScriptName(_namePrompt.Text, out var name, out var error))
        {
            _namePrompt.SetError(error);
            return;
        }

        if (!ProjectScriptAssets.TryCreateScript(_currentProject.FolderPath, name, out var reference, out error) || reference is null)
        {
            Console.WriteLine($"[Scripts] Could not create script \"{name}\": {error}");
            _namePrompt.SetError($"Couldn't create the script: {error}");
            return;
        }

        var target = _pendingScriptTarget;
        _pendingScriptTarget = null;
        _namePrompt.Close();
        _promptPurpose = PromptPurpose.ProjectName;
        Console.WriteLine($"[Scripts] Created {reference.AssetPath}");
        string? reason = "no object";
        if (target is not null && _renderer.TryAttachScript(target, reference, out reason))
        {
            _renderer.ShowToast($"Created {reference.AssetPath} — attached to {target.Name}");
            _renderer.OpenScript(reference, line: 1);
        }
        else
        {
            _renderer.ShowToast($"Created {reference.AssetPath} (not attached: {reason})", ErrorToastSeconds);
        }
    }

    // "+ Attach Script": an unnamed world is named first (the flow continues), then a .lua file is picked.
    private void HandleAttachScriptRequested(Part target)
    {
        if (_renderer.IsPlaying)
        {
            return;
        }

        _pendingScriptTarget = target;
        if (_currentProject is null)
        {
            Console.WriteLine("[Scripts] Unnamed world: asking for a project name first");
            OpenNamePrompt(LeaveAction.AttachScript, "Scripts are kept in the project folder; name it first.");
            return;
        }

        PickAndAttachScript();
    }

    private void PickAndAttachScript()
    {
        var target = _pendingScriptTarget;
        _pendingScriptTarget = null;
        if (target is null || _currentProject is null)
        {
            return;
        }

        var scriptsFolder = Path.Combine(_currentProject.FolderPath, ProjectAssetPaths.ScriptsFolder.Replace('/', Path.DirectorySeparatorChar));
        FilePickResult result;
        string? path;
        string? pickError;
        _fileDialogOpen = true;
        try
        {
            result = ScriptFilePicker.TryPick(Directory.Exists(scriptsFolder) ? scriptsFolder : _currentProject.FolderPath, out path, out pickError);
        }
        finally
        {
            _fileDialogOpen = false;
        }

        // The modal dialog swallowed the click's release.
        _input.ReleaseMouseButtons();
        switch (result)
        {
            case FilePickResult.Cancelled:
                Console.WriteLine("[Scripts] Attach cancelled");
                return;
            case FilePickResult.Picked when path is not null:
                AttachScriptFile(target, path);
                return;
            default:
                Console.WriteLine($"[Scripts] File picker failed: {pickError}");
                ReportFilePickerFailure();
                return;
        }
    }

    // A script inside Assets/Scripts is referenced where it is; any other .lua file is copied in first.
    private void AttachScriptFile(Part target, string path)
    {
        if (_currentProject is null)
        {
            return;
        }

        if (!ProjectScriptAssets.TryAddExistingScript(_currentProject.FolderPath, path, out var reference, out var copied, out var error)
            || reference is null)
        {
            Console.WriteLine($"[Scripts] Could not attach {path}: {error}");
            _renderer.ShowToast($"Couldn't attach the script: {error}", ErrorToastSeconds);
            return;
        }

        if (copied)
        {
            Console.WriteLine($"[Scripts] Copied {path} -> {reference.AssetPath}");
        }

        var label = Renderer.ScriptLabel(reference);
        if (!_renderer.TryAttachScript(target, reference, out var reason))
        {
            _renderer.ShowToast(copied ? $"{reason} (copy kept in Assets/Scripts)" : reason ?? "Couldn't attach the script", ErrorToastSeconds);
            return;
        }

        _renderer.ShowToast(copied
            ? $"Copied {label} into Assets/Scripts — attached to {target.Name}"
            : $"Attached {label} to {target.Name}");
        _renderer.OpenScript(reference);
    }

    // The scene, the project's Atmosphere, its UI and its graphics settings, saved together as one transaction
    // (ProjectSaveJournal): Save Project, leave and quit all come here.
    private bool TrySaveCurrentProject(out string? error)
    {
        error = null;
        if (_currentProject is not { } project)
        {
            return false;
        }

        List<(string Name, byte[] Contents)> files;
        try
        {
            files = [(ProjectStore.SceneFileName, Utf8(SceneFile.ToJson(_workspace))), (AtmosphereFile.FileName, Utf8(AtmosphereFile.ToJson(_renderer.Atmosphere)))];

            // An unreadable UI.json that could not be kept aside is left as it is.
            if (_guiSaveAllowed)
            {
                files.Add((GuiFile.FileName, Utf8(GuiFile.ToJson(_gui))));
            }
            else
            {
                Console.WriteLine($"[UI] Not saved: the unreadable {project.UiPath} could not be kept aside, so it is left as it is");
            }

            // Graphics.json is written once a project has one or its settings differ from the Classic renderer's
            // defaults, so saving an older project that nobody switched adds no file.
            if (File.Exists(project.GraphicsPath) || _renderer.Graphics != Graphics.GraphicsSettings.LegacyDefault)
            {
                files.Add((Graphics.GraphicsFile.FileName, Utf8(Graphics.GraphicsFile.ToJson(_renderer.Graphics))));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            error = $"the project could not be serialized ({ex.Message})";
            return false;
        }

        return ProjectSaveJournal.TrySave(project.FolderPath, files, out error);

        static byte[] Utf8(string text) => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    }
}

public sealed class EngineApplicationSettings
{
    public required string Title { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;

    // Optional multi-size window icon (applied on Windows/Linux). The Engine stays brand-neutral; the
    // host application supplies the icon.
    public WindowIcon? Icon { get; init; }

    // Folder holding one sub-folder per project. Supplied by the host so the Engine stays brand-neutral.
    public required string ProjectsRootPath { get; init; }

    // Folder for the Hub's History (history.json and kept previews). Null: ".wisko-history" beside the projects folder.
    public string? HistoryPath { get; init; }

    // The editor preferences file. Null: EditorGraphicsPreferences.DefaultFolder() (per computer, never a project).
    public string? EditorPreferencesPath { get; init; }

    // Backend that owns the window and presentation. OpenGL is the only one the editor can run today.
    public RenderBackendKind RenderBackend { get; init; } = RenderBackendKind.OpenGL;

    /// <summary>bgfx (macOS): the back buffer's multisample count, 0 (off), 2 or 4 (the default; <c>--msaa=</c>).</summary>
    public int BgfxMsaaSamples { get; init; } = 4;
}
