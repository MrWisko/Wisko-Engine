using System.Reflection;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.UI;
using Wisko.Engine.World;
using GlfwMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

namespace Wisko.Tests.Gui;

/// <summary>
/// Editor workflows in the real editor (EngineApplication, OpenGL), driven like a person would: scripted clicks,
/// right-clicks, drags and key presses fed to Wisko's InputManager and the window's polled keyboard state (no OS cursor
/// or keyboard). Where to click is found with the editor's own hit tests (toolbar, menus, Hub cards, World panel rows,
/// dialog buttons), so no step depends on fixed coordinates. Outcomes are checked on disk and in the editor's state.
/// <list type="bullet">
/// <item>Leaving a project (Phase 12): a new unnamed world left untouched, changed then Don't Save / Cancel / Save +
/// name, an existing project changed then Don't Save / Save, and the window's close button with the same question.</item>
/// <item>World panel groups (Phase 10): Ctrl+click multi-select, right-click Group, rename, collapse / expand, drag into
/// a group, undo / redo, Ctrl+G, a nested group, save and reopen, Play / Stop, Ungroup.</item>
/// </list>
/// </summary>
internal static partial class EditorFlowSmokeTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(TestReport report, string work, string? screenshotDir, string? only = null)
    {
        void Section(string title, Action body)
        {
            if (only is null || title.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                report.Section(title, body);
            }
        }

        Section("GUI: leaving a project (Save | Don't Save | Cancel), scripted clicks", () =>
        {
            var root = Path.Combine(work, "exit");
            var kept = CreateProject(Path.Combine(root, "Projects"), ExitFlow.KeptName, [("Original", new Vector3(0f, 2f, 0f))]);
            RunSession(report, root, "exit flow", (app, window) => new ExitFlow(report, app, window, kept, root, screenshotDir), ExitFlow.After);
        });
        Section("GUI: World panel groups (multi-select, Group, rename, collapse, drag, undo, nesting, save), scripted input", () =>
        {
            var root = Path.Combine(work, "groups");
            var project = CreateProject(Path.Combine(root, "Projects"), GroupFlow.ProjectName,
                [("Alpha", new Vector3(-9f, 1.5f, 0f)), ("Bravo", new Vector3(-3f, 1.5f, 0f)), ("Charlie", new Vector3(3f, 1.5f, 0f)), ("Delta", new Vector3(9f, 1.5f, 0f))]);
            RunSession(report, root, "groups flow", (app, window) => new GroupFlow(report, app, window, project, screenshotDir), GroupFlow.After);
        });
        RunAnimation(Section, work, screenshotDir, report, only);
        RunV5(Section, work, screenshotDir, report);
        RunGraphics(Section, work, screenshotDir, report);
        RunShowcase(Section, work, screenshotDir, report);
        RunReleasePrep(Section, work, screenshotDir, report);
        RunRuntimeNotice(Section, work, screenshotDir, report);
    }

    private static ProjectInfo CreateProject(string projectsRoot, string name, (string Name, Vector3 Position)[] parts)
    {
        var store = new ProjectStore(projectsRoot);
        var workspace = new Workspace();
        foreach (var (partName, position) in parts)
        {
            workspace.AddPart(new Part(partName, position, new Vector3(3f, 3f, 3f), new Vector3(0.3f, 0.6f, 0.9f)));
        }

        if (!store.TryCreateProject(name, WorldTemplate.Classic, workspace, out var project, out var error))
        {
            throw new InvalidOperationException($"could not create the test project: {error}");
        }

        return project;
    }

    private static void RunSession(TestReport report, string root, string label, Func<EngineApplication, EngineWindow, EditorFlow> create,
        Action<TestReport, EditorFlow, string> after)
    {
        var settings = new EngineApplicationSettings
        {
            Title = $"Wisko Engine ({label} test)",
            Width = 1280,
            Height = 720,
            ProjectsRootPath = Path.Combine(root, "Projects"),
            HistoryPath = Path.Combine(root, "history"),
            // Never the real preferences of the person running the tests.
            EditorPreferencesPath = Path.Combine(root, Wisko.Engine.Graphics.EditorGraphicsPreferences.FileName),
            RenderBackend = RenderBackendKind.OpenGL
        };

        var gameType = Type.GetType("Wisko.Editor.WiskoEditorModule, Wisko.Editor", throwOnError: true)!;
        var game = (IGameModule)Activator.CreateInstance(gameType, nonPublic: true)!;
        var log = GuiSmokeTest.ConsoleTee.Start();
        EditorFlow flow;
        try
        {
            using var app = new EngineApplication(settings, game);
            var window = (EngineWindow)typeof(EngineApplication).GetField("_window", Hidden)!.GetValue(app)!;
            flow = create(app, window);
            window.UpdateFrame += flow.OnFrame;
            app.Run();
        }
        finally
        {
            log.Stop();
        }

        var text = log.Text;
        report.Check($"{label}: every scripted step ran", flow.Finished && flow.Failure is null, flow.Failure ?? $"stopped at step \"{flow.Step}\"");
        report.Check($"{label}: the window closed through the editor's own dialog (not forced)", flow.Finished && !flow.Forced);
        report.Check($"{label}: no GL errors (KHR_debug)", flow.GlErrors.Count == 0 && !text.Contains("[OpenGL] Error", StringComparison.Ordinal),
            flow.GlErrors.Count == 0 ? flow.GlNote : string.Join(" | ", flow.GlErrors.Take(3)));
        report.Check($"{label}: nothing was auto-saved on leave", !text.Contains("[Project] Auto-saved", StringComparison.Ordinal));
        after(report, flow, root);
    }

    // ================================================================================================== Phase 12

    private sealed class ExitFlow(TestReport report, EngineApplication app, EngineWindow window, ProjectInfo kept, string root, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string KeptName = "Keep Me";
        private const string NewName = "Named Later";
        private readonly string _projectsRoot = Path.Combine(root, "Projects");
        private readonly string _historyPath = Path.Combine(root, "history");

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var projectsRoot = Path.Combine(root, "Projects");
            var kept = new ProjectStore(projectsRoot).ListProjects().First(p => p.Name == KeptName);
            var finalParts = LoadParts(kept.ScenePath);
            report.Check("Quit + Don't Save: the project keeps its last saved version (2 parts)", finalParts?.Count == 2, $"{finalParts?.Count}");
            report.Check("7. Projects on disk at the end: exactly the existing one and the one named while saving",
                ProjectNames(projectsRoot) == $"{KeptName}|{NewName}", ProjectNames(projectsRoot));
            var history = new ProjectHistory(Path.Combine(root, "history")).Records;
            report.Check("7. History: one Active record each for the two saved projects, none for discarded worlds",
                history.Count == 2 && history.All(r => r.Status == ProjectHistoryStatus.Active)
                && history.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual([KeptName, NewName]),
                string.Join(", ", history.Select(r => $"{r.Name} ({r.Status})")));
        }

        private string HistoryNames() =>
            string.Join("|", new ProjectHistory(_historyPath).Records.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            Check("Started in the Hub", Screen == "Hub");

            // 1. New unnamed world -> leave without changing anything: no question, no name, nothing written.
            foreach (var wait in NewClassicWorld("1")) yield return wait;
            Check("1. New World: an unnamed world in the editor, nothing changed yet", Screen == "Editor" && Current is null && !App.HasUnsavedChanges);
            foreach (var wait in LeaveToHub("1")) yield return wait;
            Check("1. Leave to Hub without naming or saving: straight to the Hub (no question, no name prompt)",
                Screen == "Hub" && !ConfirmOpen && !PromptOpen);
            Check("1. Nothing written: only the existing project on disk", ProjectNames(_projectsRoot) == KeptName, ProjectNames(_projectsRoot));

            // 2. Unnamed -> change -> Don't Save: discarded, no project and no History entry.
            foreach (var wait in NewClassicWorld("2")) yield return wait;
            foreach (var wait in AddCube("2")) yield return wait;
            Check("2. Adding a Cube (Add Meshes) is an unsaved change", PartCount == 1 && App.HasUnsavedChanges, $"{PartCount} parts");
            foreach (var wait in LeaveToHub("2")) yield return wait;
            Check("2. Leave to Hub asks Save | Don't Save | Cancel", ConfirmOpen && DialogTitle == "Save this new world?"
                && DialogButtons == "Save|Don't Save|Cancel", $"{DialogTitle} [{DialogButtons}]");
            foreach (var wait in Shot("exit-unnamed-question")) yield return wait;
            foreach (var wait in Answer("Don't Save")) yield return wait;
            Check("2. Don't Save: back in the Hub, no name asked", Screen == "Hub" && !PromptOpen);
            Check("2. Don't Save: no project folder created", ProjectNames(_projectsRoot) == KeptName, ProjectNames(_projectsRoot));
            Check("2. Don't Save: History has no entry for the discarded world", HistoryNames() == KeptName, HistoryNames());

            // 3. Unnamed -> change -> Cancel: stays, the change is still there and still unsaved.
            foreach (var wait in NewClassicWorld("3")) yield return wait;
            foreach (var wait in AddCube("3")) yield return wait;
            foreach (var wait in LeaveToHub("3")) yield return wait;
            Check("3. Leave to Hub asks first", ConfirmOpen);
            foreach (var wait in Answer("Cancel")) yield return wait;
            Check("3. Cancel: still in the editor with the change, still unsaved, nothing on disk",
                Screen == "Editor" && PartCount == 1 && App.HasUnsavedChanges && Current is null && ProjectNames(_projectsRoot) == KeptName);

            // 4. (The same world) -> Save -> name it: created, saved with the change, then the Hub.
            foreach (var wait in LeaveToHub("4")) yield return wait;
            foreach (var wait in Answer("Save")) yield return wait;
            Check("4. Save on a new world asks for its name", PromptOpen && Screen == "Editor");
            foreach (var wait in TypeName(NewName)) yield return wait;
            Check("4. Named: back in the Hub", Screen == "Hub" && !PromptOpen);
            var named = new ProjectStore(_projectsRoot).ListProjects().FirstOrDefault(p => p.Name == NewName);
            Check("4. The project exists with the Cube saved in Scene.json", named is not null && LoadParts(named.ScenePath)?.Count == 1,
                named is null ? "no project" : $"{LoadParts(named.ScenePath)?.Count} parts");
            Check("4. UI.json and Atmosphere.json saved with it", named is not null && File.Exists(named.UiPath) && File.Exists(named.AtmospherePath));
            Check("4. History now lists it", HistoryNames() == $"{KeptName}|{NewName}", HistoryNames());

            // 5. Existing project -> change -> Don't Save: its files stay byte for byte as last saved.
            var sceneBefore = File.ReadAllBytes(kept.ScenePath);
            foreach (var wait in OpenCard(KeptName, "5")) yield return wait;
            Check("5. The existing project opened, unchanged", Screen == "Editor" && Current?.Name == KeptName && PartCount == 1 && !App.HasUnsavedChanges);
            foreach (var wait in AddCube("5")) yield return wait;
            foreach (var wait in LeaveToHub("5")) yield return wait;
            Check("5. Leave to Hub asks about the named project", ConfirmOpen && DialogTitle == $"Save changes to \"{KeptName}\"?", DialogTitle);
            foreach (var wait in Shot("exit-existing-question")) yield return wait;
            foreach (var wait in Answer("Don't Save")) yield return wait;
            Check("5. Don't Save: back in the Hub", Screen == "Hub");
            Check("5. Don't Save: Scene.json unchanged byte for byte", File.ReadAllBytes(kept.ScenePath).AsSpan().SequenceEqual(sceneBefore));
            Check("5. Don't Save: the project folder is still there (never deleted)", Directory.Exists(kept.FolderPath) && File.Exists(kept.ScenePath));
            Check("5. Don't Save: no UI.json / Atmosphere.json written", !File.Exists(kept.UiPath) && !File.Exists(kept.AtmospherePath));
            Check("5. Don't Save: no thumbnail taken of the discarded scene", !File.Exists(kept.ThumbnailPath));

            // 6. Existing project -> change -> Save: written, thumbnail taken, then the Hub.
            foreach (var wait in OpenCard(KeptName, "6")) yield return wait;
            Check("6. Reopened: still the last saved version (1 part)", PartCount == 1);
            foreach (var wait in AddCube("6")) yield return wait;
            foreach (var wait in LeaveToHub("6")) yield return wait;
            foreach (var wait in Answer("Save")) yield return wait;
            Check("6. Save: back in the Hub without a name prompt", Screen == "Hub" && !PromptOpen);
            Check("6. Save: Scene.json now has the Cube (2 parts)", LoadParts(kept.ScenePath)?.Count == 2, $"{LoadParts(kept.ScenePath)?.Count}");
            Check("6. Save: UI.json, Atmosphere.json and the thumbnail written", File.Exists(kept.UiPath) && File.Exists(kept.AtmospherePath) && File.Exists(kept.ThumbnailPath));

            // Opening and leaving with no change: no question.
            foreach (var wait in OpenCard(KeptName, "6b")) yield return wait;
            Check("6. Reopened with 2 parts and nothing unsaved", PartCount == 2 && !App.HasUnsavedChanges);

            // The window's close button: the same question; Cancel keeps the window, Don't Save closes it.
            foreach (var wait in AddCube("quit")) yield return wait;
            Step = "quit: close the window";
            Window.Close();
            yield return 6;
            Check("Quit with changes: the close is held and the question shown", Screen == "Editor" && ConfirmOpen && !Window.IsExiting);
            foreach (var wait in Answer("Cancel")) yield return wait;
            Check("Quit + Cancel: the window stays open in the editor with the change", Screen == "Editor" && PartCount == 3 && !Window.IsExiting);
            foreach (var wait in QuitWithoutSaving()) yield return wait;
        }
    }

    // ================================================================================================== Phase 10

    private sealed class GroupFlow(TestReport report, EngineApplication app, EngineWindow window, ProjectInfo project, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string ProjectName = "Group World";

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var project = new ProjectStore(Path.Combine(root, "Projects")).ListProjects().First(p => p.Name == ProjectName);
            var parts = LoadParts(project.ScenePath) ?? [];
            string? PathOf(string name) => parts.FirstOrDefault(p => p.Name == name)?.Group?.Path;
            report.Check("Groups: Scene.json keeps the saved groups (Don't Save on quit dropped only the later Ungroup)",
                PathOf("Alpha") == "machines" && PathOf("Bravo") == "machines" && PathOf("Charlie") == "machines" && PathOf("Delta") == "machines/Group",
                $"{PathOf("Alpha")}, {PathOf("Charlie")}, {PathOf("Delta")}");
        }

        private Part P(string name) => Workspace.Parts.First(p => p.Name == name);

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            foreach (var wait in OpenCard(ProjectName, "open")) yield return wait;
            Check("Groups: the project opened (4 parts, no groups)", Screen == "Editor" && Workspace.Parts.All(p => p.Group is null));

            // Ctrl+click selects several rows.
            Step = "click Alpha";
            Click(PartRow("Alpha"));
            yield return 4;
            Step = "Ctrl+click Bravo";
            SetKey(Keys.LeftControl, true);
            yield return 1;
            Click(PartRow("Bravo"));
            yield return 4;
            SetKey(Keys.LeftControl, false);
            yield return 2;
            Check("Multi-select: Ctrl+click adds a second row to the selection", SelectedNames == "Alpha|Bravo", SelectedNames);

            // Right-click -> Group.
            Step = "right-click Bravo";
            RightClick(PartRow("Bravo"));
            yield return 4;
            Check("Right-click on the selection offers Group", ContextItems == "Group", ContextItems);
            Step = "Group";
            Click(ContextItem("Group"));
            yield return 6;
            var group = P("Alpha").Group;
            Check("Group: both selected Parts are in one new group", group is not null && ReferenceEquals(P("Bravo").Group, group) && group.Name == "Group"
                && P("Charlie").Group is null, group?.Path);
            Check("Group: the World panel shows the group row with its Parts indented under it",
                DepthOf((part, g) => g?.Name == "Group") == 0 && DepthOf((part, g) => part?.Name == "Alpha") == 1
                && DepthOf((part, g) => part?.Name == "Charlie") == 0);
            foreach (var wait in Shot("groups-created")) yield return wait;

            // Rename through the context menu, typing like a person (one key per frame).
            Step = "right-click the group";
            RightClick(GroupRow("Group"));
            yield return 4;
            Check("Right-click on a group offers Rename, Group, Ungroup", ContextItems == "Rename|Group|Ungroup", ContextItems);
            Click(ContextItem("Rename"));
            yield return 4;
            for (var i = 0; i < "Group".Length; i++)
            {
                Step = "Backspace";
                PressKey(Keys.Backspace);
                yield return 2;
            }

            foreach (var wait in TypeKeys("machines")) yield return wait;
            Step = "Enter";
            PressKey(Keys.Enter);
            yield return 6;
            Check("Rename: the group is called \"machines\"", group?.Name == "machines", group?.Name);

            // Collapse and expand with the arrow.
            Step = "collapse";
            Click(GroupArrow("machines"));
            yield return 4;
            Check("Collapse: the arrow hides the group's Parts", !HasRow(h => h.Kind == ExplorerRowKind.Part && h.Part?.Name == "Alpha")
                && HasRow(h => h.Kind == ExplorerRowKind.Part && h.Part?.Name == "Charlie"));
            Step = "expand";
            Click(GroupArrow("machines"));
            yield return 4;
            Check("Expand: they are shown again", HasRow(h => h.Kind == ExplorerRowKind.Part && h.Part?.Name == "Alpha"));

            // Drag Charlie onto the group.
            foreach (var wait in DragRow(PartRow("Charlie"), GroupRow("machines"), "drag Charlie into machines")) yield return wait;
            Check("Drag and drop: Charlie moved into the group", ReferenceEquals(P("Charlie").Group, group));

            // Undo / redo (Ctrl+Z, Ctrl+Y).
            foreach (var wait in CtrlKey(Keys.Z)) yield return wait;
            Check("Undo (Ctrl+Z): Charlie is out of the group again", P("Charlie").Group is null);
            foreach (var wait in CtrlKey(Keys.Y)) yield return wait;
            Check("Redo (Ctrl+Y): back in the group", ReferenceEquals(P("Charlie").Group, group));

            // Ctrl+G on Delta, then that group dragged into machines: a nested group.
            Step = "click Delta";
            Click(PartRow("Delta"));
            yield return 4;
            foreach (var wait in CtrlKey(Keys.G)) yield return wait;
            var inner = P("Delta").Group;
            Check("Ctrl+G: groups the selection", inner is not null && inner.Parent is null && inner.Name == "Group", inner?.Path);
            foreach (var wait in DragRow(GroupRow("Group"), GroupRow("machines"), "drag the new group into machines")) yield return wait;
            Check("Nested groups: dragging a group onto another nests it (machines/Group)", inner?.Path == "machines/Group", inner?.Path);
            Check("Nested groups: shown one level deeper (machines > Group > Delta)", DepthOf((part, g) => ReferenceEquals(g, inner)) == 1
                && DepthOf((part, g) => part?.Name == "Delta") == 2);
            Check("Organising never moves a Part", P("Alpha").Position.X == -9f && P("Delta").Position.X == 9f);
            foreach (var wait in Shot("groups-nested")) yield return wait;

            // Save, leave and reopen: the groups come back from Scene.json.
            foreach (var wait in ProjectMenu("Save Project", "save")) yield return wait;
            Check("Save Project: Scene.json has the groups", File.ReadAllText(project.ScenePath).Contains("\"machines/Group\"", StringComparison.Ordinal));
            foreach (var wait in LeaveToHub("leave")) yield return wait;
            Check("Leave to Hub after saving: no question", Screen == "Hub" && !ConfirmOpen);
            foreach (var wait in OpenCard(ProjectName, "reopen")) yield return wait;
            var reopened = P("Alpha").Group;
            Check("Reopen: the groups and their nesting are back", reopened?.Path == "machines" && ReferenceEquals(P("Charlie").Group, reopened)
                && P("Delta").Group?.Parent is { } parent && ReferenceEquals(parent, reopened), $"{reopened?.Path}, {P("Delta").Group?.Path}");

            // Play and Stop leave the organisation alone.
            foreach (var wait in PlayStop()) yield return wait;
            Check("Play / Stop: groups unchanged", P("Alpha").Group?.Path == "machines" && P("Delta").Group?.Path == "machines/Group");

            // Ungroup: the Parts go to the top level, the inner group moves up.
            Step = "right-click machines";
            RightClick(GroupRow("machines"));
            yield return 4;
            Click(ContextItem("Ungroup"));
            yield return 6;
            Check("Ungroup: its Parts are ungrouped and the group inside it moves up a level",
                P("Alpha").Group is null && P("Charlie").Group is null && P("Delta").Group is { Parent: null, Name: "Group" }, P("Delta").Group?.Path);
            Check("Ungroup is an unsaved change", App.HasUnsavedChanges);
            foreach (var wait in QuitWithoutSaving()) yield return wait;
        }
    }

    private static List<Part>? LoadParts(string scenePath) =>
        SceneFile.TryLoad(scenePath, _ => null, out var parts, out _) ? parts : null;

    private static string ProjectNames(string projectsRoot) =>
        string.Join("|", new ProjectStore(projectsRoot).ListProjects().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>The scripted session: a coroutine of steps, each yielding how many frames to wait afterwards.</summary>
    private abstract class EditorFlow
    {
        private const int MaxFrames = 6000;
        private readonly string? _screenshotDir;
        private readonly MethodInfo _setKeyState = typeof(KeyboardState).GetMethod("SetKeyState", Hidden)!;
        private readonly List<Keys> _pressedKeys = [];
        private IEnumerator<int>? _steps;
        private GlErrorMonitor? _gl;
        private int _frames;
        private int _afterFinish;
        private int _wait;
        private bool _hasPointer;
        private GlfwMouseButton? _releasePending;
        private float _x;
        private float _y;

        protected EditorFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        {
            Report = report;
            App = app;
            Window = window;
            Input = (InputManager)typeof(EngineApplication).GetField("_input", Hidden)!.GetValue(app)!;
            Renderer = (Renderer)typeof(EngineApplication).GetField("_renderer", Hidden)!.GetValue(app)!;
            _screenshotDir = screenshotDir;
        }

        protected TestReport Report { get; }

        protected EngineApplication App { get; }

        protected EngineWindow Window { get; }

        protected InputManager Input { get; }

        protected Renderer Renderer { get; }

        public bool Finished { get; private set; }

        public bool Forced { get; private set; }

        public string? Failure { get; private set; }

        public string Step { get; protected set; } = "start";

        public IReadOnlyList<string> GlErrors => _gl?.Errors ?? [];

        public string GlNote => _gl?.Note ?? "GL monitor not installed";

        protected abstract IEnumerable<int> Steps();

        public void OnFrame(FrameEventArgs args)
        {
            _gl ??= GlErrorMonitor.Install();

            if (_hasPointer)
            {
                Input.OnMouseMove(0d, 0d, _x, _y);
            }

            // Polled keys pressed last frame come up again (a one-frame press).
            foreach (var key in _pressedKeys)
            {
                _setKeyState.Invoke(Window.KeyboardState, [key, false]);
            }

            _pressedKeys.Clear();

            if (_releasePending is { } button)
            {
                Input.OnMouseButton(button, false, _x, _y);
                _releasePending = null;
                _wait = System.Math.Max(_wait, 2);
                return;
            }

            if (++_frames > MaxFrames && Failure is null)
            {
                Abort($"timed out at step \"{Step}\"");
                return;
            }

            if (_wait > 0)
            {
                _wait--;
                return;
            }

            if (Failure is not null)
            {
                return;
            }

            if (Finished)
            {
                if (++_afterFinish > 120)
                {
                    Abort("the window did not close after Don't Save");
                }

                return;
            }

            try
            {
                _steps ??= Steps().GetEnumerator();
                if (_steps.MoveNext())
                {
                    _wait = _steps.Current;
                }
                else
                {
                    Finished = true;
                }
            }
            catch (Exception ex)
            {
                Abort($"step \"{Step}\" threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Something went wrong: record it and close the window without asking, so the run never hangs.
        private void Abort(string reason)
        {
            Failure = reason;
            Forced = true;
            typeof(EngineApplication).GetField("_closeApproved", Hidden)!.SetValue(App, true);
            Window.Close();
        }

        // ---------------------------------------------------------------------------------------------- input

        protected void Click((float X, float Y) point) => Press(GlfwMouseButton.Left, point);

        protected void RightClick((float X, float Y) point) => Press(GlfwMouseButton.Right, point);

        private void Press(GlfwMouseButton button, (float X, float Y) point)
        {
            _x = point.X;
            _y = point.Y;
            _hasPointer = true;
            Input.OnMouseMove(0d, 0d, _x, _y);
            Input.OnMouseButton(button, true, _x, _y);
            _releasePending = button;
        }

        // A polled key (as the window's keyboard reports it): down this frame, up on the next.
        protected void PressKey(Keys key)
        {
            _setKeyState.Invoke(Window.KeyboardState, [key, true]);
            _pressedKeys.Add(key);
        }

        protected void SetKey(Keys key, bool down) => _setKeyState.Invoke(Window.KeyboardState, [key, down]);

        // Types like a keyboard: each key goes down (the key state, for shortcuts) and the OS delivers its character
        // (text input, which every text field reads).
        protected IEnumerable<int> TypeKeys(string lowercase)
        {
            foreach (var ch in lowercase)
            {
                Step = $"type '{ch}'";
                PressKey(Keys.A + (ch - 'a'));
                Input.OnTextInput(ch);
                yield return 2;
            }
        }

        protected IEnumerable<int> CtrlKey(Keys key)
        {
            Step = $"Ctrl+{key}";
            SetKey(Keys.LeftControl, true);
            yield return 1;
            PressKey(key);
            yield return 3;
            SetKey(Keys.LeftControl, false);
            yield return 4;
        }

        // Press on one point, move to another over several frames (past the drag threshold), release there.
        protected IEnumerable<int> DragRow((float X, float Y) from, (float X, float Y) to, string label)
        {
            Step = label;
            _x = from.X;
            _y = from.Y;
            _hasPointer = true;
            Input.OnMouseMove(0d, 0d, _x, _y);
            Input.OnMouseButton(GlfwMouseButton.Left, true, _x, _y);
            yield return 2;
            for (var i = 1; i <= 10; i++)
            {
                _x = from.X + (to.X - from.X) * i / 10f;
                _y = from.Y + (to.Y - from.Y) * i / 10f;
                yield return 1;
            }

            Input.OnMouseButton(GlfwMouseButton.Left, false, _x, _y);
            yield return 6;
        }

        // The last presented frame, read back from the window's front buffer (independent of the display's state).
        protected IEnumerable<int> Shot(string name)
        {
            yield return 2;
            if (_screenshotDir is not null)
            {
                WindowCapture.SaveGlFrontBuffer(Path.Combine(_screenshotDir, name + ".png"), Window.FramebufferSize.X, Window.FramebufferSize.Y);
            }

            yield return 1;
        }

        // ---------------------------------------------------------------------------------------------- editor actions

        protected IEnumerable<int> NewClassicWorld(string label)
        {
            Step = $"{label}: Hub New World";
            Click(Center(Hub.NewWorldButton()));
            yield return 6;
            Step = $"{label}: Classic template";
            var card = (UiBox)typeof(ProjectHubRenderer).GetMethod("GetTemplateCard", Hidden)!.Invoke(Hub, [0])!;
            Click(Center(card));
            yield return 20;
        }

        protected IEnumerable<int> OpenCard(string name, string label)
        {
            Step = $"{label}: open the \"{name}\" card";
            var projects = (IReadOnlyList<ProjectInfo>)typeof(ProjectHubRenderer).GetField("_projects", Hidden)!.GetValue(Hub)!;
            var index = projects.ToList().FindIndex(p => p.Name == name);
            if (index < 0)
            {
                throw new InvalidOperationException($"no Hub card for \"{name}\"");
            }

            // The card's preview (its upper part): opens the project.
            var card = Hub.GetCard(index);
            Click((card.X + card.Width * 0.5f, card.Y + card.Height * 0.3f));
            yield return 20;
        }

        protected IEnumerable<int> ProjectMenu(string item, string label)
        {
            Step = $"{label}: project menu";
            Click(Find((x, y) => Toolbar.TryHitProject(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight));
            yield return 4;
            Step = $"{label}: {item}";
            Click(FindMenuItem(item));
            yield return 10;
        }

        protected IEnumerable<int> LeaveToHub(string label) => ProjectMenu("Leave to Hub", label);

        protected IEnumerable<int> AddCube(string label)
        {
            Step = $"{label}: Add Meshes";
            Click(Find((x, y) => Toolbar.TryHitControl(x, y, Width, out var control) && control == ToolbarControl.AddMeshes, 0f, ToolbarRenderer.ToolbarHeight));
            yield return 4;
            Step = $"{label}: Cube";
            Click(FindMenuItem("Cube"));
            yield return 6;
        }

        protected IEnumerable<int> PlayStop()
        {
            var play = Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight);
            Step = "Play";
            Click(play);
            yield return 40;
            Step = "Stop";
            Click(play);
            yield return 20;
        }

        protected IEnumerable<int> Answer(string button)
        {
            Step = $"dialog: {button}";
            Click(DialogButton(button));
            yield return 12;
        }

        // Types the name as text input (as the OS delivers it), then clicks the prompt's confirm button.
        protected IEnumerable<int> TypeName(string name)
        {
            Step = $"type \"{name}\"";
            foreach (var ch in name)
            {
                Input.OnTextInput(ch);
            }

            yield return 3;
            Step = "confirm the name";
            var args = new object?[] { Width, Height, null, null, null, null };
            typeof(TextPrompt).GetMethod("GetLayout", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
            Click(Center((UiBox)args[5]!));
            yield return 20;
        }

        // Closes the window when there is nothing unsaved: no question, it just closes.
        protected IEnumerable<int> QuitClean()
        {
            Step = "quit: close the window (nothing unsaved)";
            Window.Close();
            Finished = true;
            yield return 0;
        }

        // Closes the window: there are unsaved changes, so the question comes; Don't Save closes it.
        protected IEnumerable<int> QuitWithoutSaving()
        {
            Step = "quit: close the window";
            Window.Close();
            yield return 6;
            Check("Quit with unsaved changes asks first", ConfirmOpen && !Window.IsExiting);
            Step = "quit: Don't Save";
            Click(DialogButton("Don't Save"));

            // The answer closes the window during this frame's editor update; the run ends there.
            Finished = true;
            yield return 0;
        }

        // ---------------------------------------------------------------------------------------------- where things are

        protected (float X, float Y) DialogButton(string label)
        {
            var request = Request ?? throw new InvalidOperationException($"no dialog open to answer \"{label}\"");
            var index = request.Buttons.ToList().IndexOf(label);
            if (index < 0)
            {
                throw new InvalidOperationException($"the dialog has no \"{label}\" button ({string.Join("|", request.Buttons)})");
            }

            var rects = (UiBox[])typeof(ConfirmDialog).GetMethod("ButtonRects", Hidden)!.Invoke(Confirm, [Width, Height, request])!;
            return Center(rects[index]);
        }

        protected (float X, float Y) FindMenuItem(string label) =>
            Find((x, y) => Toolbar.TryHitMenuItem(x, y, Width, out var item) && item.Label == label, ToolbarRenderer.ToolbarHeight, Height);

        private ScriptsPanelView ScriptsView => (ScriptsPanelView)typeof(Renderer).GetMethod("BuildScriptsPanelView", Hidden)!.Invoke(Renderer, null)!;

        private bool RowHit(float x, float y, ScriptsPanelView scripts, Func<ExplorerRowHit, bool> match) =>
            Explorer.TryHitRow(x, y, Width, Height, Workspace, scripts, out var hit) && match(hit);

        protected bool HasRow(Func<ExplorerRowHit, bool> match)
        {
            var scripts = ScriptsView;
            return TryFind((x, y) => RowHit(x, y, scripts, match), SidebarLeft, ToolbarRenderer.ToolbarHeight, Height, out _);
        }

        // A World panel row's point. A row scrolled out of the list is brought into view first, as the mouse wheel would
        // (from the top, half a row at a time), so a long list (or a shorter one, with the rotation fields shown) is
        // reached the way a person reaches it.
        protected (float X, float Y) Row(Func<ExplorerRowHit, bool> match)
        {
            var scripts = ScriptsView;
            bool Hit(float x, float y) => RowHit(x, y, scripts, match);
            if (TryFind(Hit, SidebarLeft, ToolbarRenderer.ToolbarHeight, Height, out var found))
            {
                return ((found.MinX + found.MaxX) * 0.5f, (found.MinY + found.MaxY) * 0.5f);
            }

            Explorer.ScrollList(-1_000_000f, Width, Height, scripts, Workspace);
            for (var step = 0; step < 400; step++)
            {
                if (TryFind(Hit, SidebarLeft, ToolbarRenderer.ToolbarHeight, Height, out found))
                {
                    return ((found.MinX + found.MaxX) * 0.5f, (found.MinY + found.MaxY) * 0.5f);
                }

                Explorer.ScrollList(13f, Width, Height, scripts, Workspace);
            }

            throw new InvalidOperationException($"nothing to click for step \"{Step}\"");
        }

        protected (float X, float Y) PartRow(string name) => Row(h => h.Kind == ExplorerRowKind.Part && h.Part?.Name == name);

        protected (float X, float Y) GroupRow(string name) => Row(h => h.Kind == ExplorerRowKind.Group && h.Group?.Name == name);

        protected (float X, float Y) GroupArrow(string name) => Row(h => h.Kind == ExplorerRowKind.GroupDisclosure && h.Group?.Name == name);

        // How deep the World panel draws a Part's or group's row (each level is indented one step), or -1 when not shown.
        protected int DepthOf(Func<Part?, PartGroup?, bool> match)
        {
            foreach (var row in (System.Collections.IList)typeof(WorldExplorerRenderer).GetField("_rows", Hidden)!.GetValue(Explorer)!)
            {
                var type = row!.GetType();
                var part = (Part?)type.GetProperty("Part")!.GetValue(row);
                var group = (PartGroup?)type.GetProperty("Group")!.GetValue(row);
                if ((int)type.GetProperty("ScriptIndex")!.GetValue(row)! < 0 && match(part, group))
                {
                    return (int)type.GetProperty("Depth")!.GetValue(row)!;
                }
            }

            return -1;
        }

        private IReadOnlyList<string> ContextList => (IReadOnlyList<string>)typeof(WorldExplorerRenderer).GetField("_contextItems", Hidden)!.GetValue(Explorer)!;

        protected string ContextItems => string.Join("|", ContextList);

        protected (float X, float Y) ContextItem(string label)
        {
            var items = ContextList.ToList();
            var index = items.IndexOf(label);
            if (!Explorer.IsContextMenuOpen || index < 0)
            {
                throw new InvalidOperationException($"no context menu item \"{label}\" ({string.Join("|", items)})");
            }

            var x = (float)typeof(WorldExplorerRenderer).GetField("_contextMenuX", Hidden)!.GetValue(Explorer)!;
            var y = (float)typeof(WorldExplorerRenderer).GetField("_contextMenuY", Hidden)!.GetValue(Explorer)!;
            return (x + 40f, y + index * 28f + 14f);
        }

        // The middle of the area a hit test accepts (scanned on a 2 px grid).
        protected (float X, float Y) Find(Func<float, float, bool> hit, float top, float bottom, float left = 0f) =>
            TryFind(hit, left, top, bottom, out var b)
                ? ((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f)
                : throw new InvalidOperationException($"nothing to click for step \"{Step}\"");

        private bool TryFind(Func<float, float, bool> hit, float left, float top, float bottom, out (float MinX, float MinY, float MaxX, float MaxY) bounds)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var y = top; y <= bottom; y += 2f)
            {
                for (var x = left; x <= Width; x += 2f)
                {
                    if (hit(x, y))
                    {
                        minX = MathF.Min(minX, x);
                        maxX = MathF.Max(maxX, x);
                        minY = MathF.Min(minY, y);
                        maxY = MathF.Max(maxY, y);
                    }
                }
            }

            bounds = (minX, minY, maxX, maxY);
            return minX <= maxX;
        }

        private static (float X, float Y) Center(UiBox box) => (box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f);

        // ---------------------------------------------------------------------------------------------- editor state

        protected int Width => Renderer.LogicalWidth;

        protected int Height => Renderer.LogicalHeight;

        private float SidebarLeft => Width - WorldExplorerRenderer.SidebarWidth - 8f;

        protected string Screen => typeof(EngineApplication).GetField("_screen", Hidden)!.GetValue(App)!.ToString()!;

        protected ProjectInfo? Current => (ProjectInfo?)typeof(EngineApplication).GetField("_currentProject", Hidden)!.GetValue(App);

        protected Workspace Workspace => (Workspace)typeof(EngineApplication).GetField("_workspace", Hidden)!.GetValue(App)!;

        protected int PartCount => Workspace.Parts.Count;

        protected string SelectedNames
        {
            get
            {
                var selection = (EditorSelection)typeof(EngineApplication).GetField("_selection", Hidden)!.GetValue(App)!;
                return string.Join("|", selection.SelectedParts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            }
        }

        protected ProjectHubRenderer Hub => (ProjectHubRenderer)typeof(EngineApplication).GetField("_hub", Hidden)!.GetValue(App)!;

        private TextPrompt Prompt => (TextPrompt)typeof(EngineApplication).GetField("_namePrompt", Hidden)!.GetValue(App)!;

        protected ToolbarRenderer Toolbar => (ToolbarRenderer)typeof(Renderer).GetField("_toolbar", Hidden)!.GetValue(Renderer)!;

        protected WorldExplorerRenderer Explorer => (WorldExplorerRenderer)typeof(Renderer).GetField("_worldExplorer", Hidden)!.GetValue(Renderer)!;

        private ConfirmDialog Confirm => (ConfirmDialog)typeof(Renderer).GetField("_confirm", Hidden)!.GetValue(Renderer)!;

        protected bool ConfirmOpen => Confirm.IsOpen;

        protected bool PromptOpen => Prompt.IsOpen;

        private ConfirmRequest? Request => (ConfirmRequest?)typeof(ConfirmDialog).GetField("_request", Hidden)!.GetValue(Confirm);

        protected string? DialogTitle => Request?.Title;

        protected string DialogButtons => Request is { } request ? string.Join("|", request.Buttons) : string.Empty;

        protected void Check(string label, bool passed, string? detail = null) => Report.Check(label, passed, detail);
    }
}
