using System.Diagnostics;
using System.Reflection;
using System.Text;
using OpenTK.Windowing.Common;
using Wisko.Engine.Core;
using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.World;
using GlfwMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

namespace Wisko.Tests.Gui;

/// <summary>
/// Runs the real editor (EngineApplication with the editor's WiskoEditorModule, OpenGL) on a project made for the test and
/// replays a fixed tour by feeding Wisko's InputManager between frames (the OS cursor is never moved). Every outcome is
/// checked from the editor's log, GL debug output and frame measurements. Windows only; needs a desktop session and a
/// GPU. Click positions assume the 1280x720 window at 100% display scaling.
/// </summary>
internal static class GuiSmokeTest
{
    private const string ProjectName = "Smoke World";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    // Positions in window coordinates (1280x720, 100% scaling): Hub card, toolbar, World panel, game view.
    private static readonly string[] Steps =
    [
        "wait 30",
        "measure hub 3",
        "click 220 195",        // the project's card in the Hub
        "wait 30",
        "measure editor 3",
        "screenshot editor",
        "click 640 278",        // the Ball in the 3D view
        "wait 5",
        "click 302 19",         // Move
        "wait 10",
        "screenshot move-gizmo",
        "click 408 19",         // Resize
        "wait 10",
        "click 514 19",         // Rotate
        "wait 10",
        "screenshot rotate-gizmo",
        "click 1120 109",       // the Baseplate row (first in the World panel): selected, details shown read-only
        "wait 5",
        "screenshot baseplate-selected",
        "click 1072 135",       // expand Ball in the World panel
        "wait 5",
        "click 1120 161",       // its Game.lua row: opens the script editor
        "wait 20",
        "screenshot script-editor",
        "click 1222 19",        // Play
        "measure play 3",
        "screenshot play",
        "click 526 640",        // the game GUI's Jump button
        "seconds 1",
        "screenshot jump",
        "click 1222 19",        // Stop
        "wait 10",
        "minimize",
        "measure minimized 3",
        "restore",
        "wait 30",
        "click 183 19",         // project menu
        "wait 5",
        "click 214 128",        // Leave to Hub (saves and captures the thumbnail)
        "seconds 0.5",
        "screenshot hub-thumbnail",
        "quit",
    ];

    /// <summary>The tour, then the editor workflows; <paramref name="only"/> keeps just the sections whose title contains it.</summary>
    public static void Run(TestReport report, string work, string? screenshotDir, string? only = null)
    {
        using var awake = OperatingSystem.IsWindows() ? DisplayConditions.KeepAwake() : null;
        if (OperatingSystem.IsWindows())
        {
            report.Section("GUI display conditions (frame timings need a local display that is on)", () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var display = DisplayConditions.Inspect();
                    TestReport.Info(display.ToString());
                    report.Check("GUI display conditions: the display is on and composing (20-500 Hz), the session unlocked and local",
                        display.Suitable, display.Suitable ? display.ToString() : display.Problem);
                }
            });
        }

        const string title = "GUI smoke test (real editor, OpenGL)";
        if (only is null || title.Contains(only, StringComparison.OrdinalIgnoreCase))
        {
            report.Section(title, () => RunTour(report, work, screenshotDir));
        }

        if (OperatingSystem.IsWindows())
        {
            EditorFlowSmokeTests.Run(report, work, screenshotDir, only);
        }
    }

    private static void RunTour(TestReport report, string work, string? screenshotDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            report.Check("GUI smoke test: runs on Windows", false, "this test drives the Windows editor (window capture, DwmFlush pacing)");
            return;
        }

        var projectsRoot = Path.Combine(work, "gui", "Projects");
        var project = CreateProject(projectsRoot);
        if (screenshotDir is not null)
        {
            Directory.CreateDirectory(screenshotDir);
        }

        var settings = new EngineApplicationSettings
        {
            Title = "Wisko Engine (GUI smoke test)",
            Width = 1280,
            Height = 720,
            ProjectsRootPath = projectsRoot,
            HistoryPath = Path.Combine(work, "gui", "history"),
            EditorPreferencesPath = Path.Combine(work, "gui", Wisko.Engine.Graphics.EditorGraphicsPreferences.FileName),
            RenderBackend = RenderBackendKind.OpenGL
        };

        var gameType = Type.GetType("Wisko.Editor.WiskoEditorModule, Wisko.Editor", throwOnError: true)!;
        var game = (IGameModule)Activator.CreateInstance(gameType, nonPublic: true)!;
        var log = ConsoleTee.Start();
        Driver driver;
        CompositorFramePacer? pacer;
        try
        {
            using var app = new EngineApplication(settings, game);
            var window = (EngineWindow)typeof(EngineApplication).GetField("_window", Hidden)!.GetValue(app)!;
            var input = (InputManager)typeof(EngineApplication).GetField("_input", Hidden)!.GetValue(app)!;
            pacer = (CompositorFramePacer?)typeof(EngineWindow).GetField("_framePacer", Hidden)!.GetValue(window);
            driver = new Driver(window, input, Steps, screenshotDir);
            window.UpdateFrame += driver.OnFrame;
            app.Run();
        }
        finally
        {
            log.Stop();
        }

        var text = log.Text;
        report.Check("Every scripted step ran", driver.Completed == Steps.Length, $"{driver.Completed}/{Steps.Length}");
        report.Check("OpenGL: no GL errors during the whole session (KHR_debug)", driver.GlErrors.Count == 0 && !text.Contains("[OpenGL] Error", StringComparison.Ordinal),
            driver.GlErrors.Count == 0 ? driver.GlMonitorNote : string.Join(" | ", driver.GlErrors.Take(3)));
        report.Check("OpenGL: 4x MSAA active", text.Contains("[Renderer] MSAA: 4x (requested 4x)", StringComparison.Ordinal));
        report.Check("Frame pacing: desktop compositor (DwmFlush) active", pacer is not null && text.Contains("[Renderer] Frame pacing: desktop compositor (DwmFlush)", StringComparison.Ordinal));

        foreach (var (label, expected) in new[]
                 {
                     ("Project opened from the Hub", $"[Project] Opened \"{ProjectName}\""),
                     ("Move tool", "[Tool] Move"),
                     ("Resize tool", "[Tool] Resize"),
                     ("Rotate tool", "[Tool] Rotate"),
                     ("Baseplate selected in the World panel", "[WorldExplorer] Selected Baseplate (environment, read-only)"),
                     ("Script opened from the World panel", "[ScriptEditor] Opened Assets/Scripts/Game.lua"),
                     ("Play started with its scripts and GUI", "[PlayMode] Play #1: 4 objects, 2 scripts, 2 GUI elements"),
                     ("Physics: the Ball landed on the baseplate (Lua OnCollisionEnter)", "[Game.lua] Landed on baseplate at y=3.0"),
                     ("Game GUI: the Jump button reached Lua OnClick", "[Game.lua] Jump button clicked 1"),
                     ("Stop restored the scene", "[PlayMode] Stopped: scene restored (4 objects)"),
                     ("Leave to Hub saved the thumbnail", "[Project] Thumbnail updated"),
                 })
        {
            report.Check(label, text.Contains(expected, StringComparison.Ordinal), $"expected log line containing \"{expected}\"");
        }

        report.Check("Thumbnail.png written", File.Exists(project.ThumbnailPath));

        foreach (var (name, maxCpu) in new[] { ("hub", 0.5), ("editor", 0.5), ("play", 0.75), ("minimized", 0.5) })
        {
            if (!driver.Measurements.TryGetValue(name, out var m))
            {
                report.Check($"Frame pacing ({name}): measured", false);
                continue;
            }

            report.Check($"Frame pacing ({name}): {m.Fps:F0} fps, CPU {m.Cpu * 100:F0}% of a core (paced: 20-500 fps, CPU <= {maxCpu * 100:F0}%)",
                m.Fps is >= 20 and <= 500 && m.Cpu <= maxCpu);
        }

        if (pacer is not null)
        {
            var total = pacer.PacedFrames + pacer.ThrottledFrames;
            TestReport.Info($"compositor pacing: {pacer.PacedFrames} paced, {pacer.ThrottledFrames} throttled frames, longest wait {pacer.LongestWait.TotalMilliseconds:F1} ms");
            report.Check("Frame pacing: the compositor paced the session (throttled frames <= 5%)", total > 0 && pacer.ThrottledFrames <= total * 0.05);
        }

        if (screenshotDir is not null)
        {
            TestReport.Info($"screenshots: {screenshotDir}");
        }
    }

    private static ProjectInfo CreateProject(string projectsRoot)
    {
        var store = new ProjectStore(projectsRoot);
        var workspace = new Workspace();
        var ball = new Part("Ball", new Vector3(0f, 24f, 0f), new Vector3(6f, 6f, 6f), new Vector3(0.95f, 0.55f, 0.1f), anchored: false, shapeType: ShapeType.Sphere);
        AddScript(ball, "Assets/Scripts/Game.lua");
        var spinner = new Part("Spinner", new Vector3(14f, 3f, 0f), new Vector3(2f, 6f, 10f), new Vector3(0.2f, 0.6f, 0.95f));
        AddScript(spinner, "Assets/Scripts/Spinner.lua");
        workspace.AddPart(ball);
        workspace.AddPart(spinner);
        workspace.AddPart(new Part("Block", new Vector3(-14f, 2f, 0f), new Vector3(4f, 4f, 4f), new Vector3(0.25f, 0.8f, 0.3f), rotation: new Vector3(0f, 30f, 0f)));
        workspace.AddPart(new Part("Ramp", new Vector3(-4f, 1.5f, 10f), new Vector3(6f, 3f, 6f), new Vector3(0.8f, 0.8f, 0.85f), shapeType: ShapeType.Wedge));
        if (!store.TryCreateProject(ProjectName, WorldTemplate.Classic, workspace, out var project, out var error))
        {
            throw new InvalidOperationException($"could not create the test project: {error}");
        }

        var scripts = Path.Combine(project.FolderPath, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "Game.lua"), """
            local jumps = 0
            function Start()
              print("[Game.lua] Start on " .. self.Name .. " (" .. _VERSION .. ")")
              UI.Find("Status").Text = "Lua running - ball falling"
              UI.Find("Jump"):OnClick(function()
                jumps = jumps + 1
                self.LinearVelocity = Vector3.new(0, 45, 0)
                UI.Find("Status").Text = "Jump clicked " .. jumps .. "x"
                print("[Game.lua] Jump button clicked " .. jumps)
              end)
            end
            function OnCollisionEnter(other, contact)
              local what = other and other.Name or "baseplate"
              UI.Find("Status").Text = "Ball landed on " .. what
              print("[Game.lua] Landed on " .. what .. " at y=" .. string.format("%.2f", self.Transform.Position.y))
            end
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(scripts, "Spinner.lua"), """
            function Update(dt)
              local r = self.Transform.Rotation
              self.Transform.Rotation = Vector3.new(r.x, r.y + 90 * dt, r.z)
            end
            """, new UTF8Encoding(false));

        var gui = new GuiDocument();
        gui.Add(new GuiText
        {
            Name = "Status",
            Text = "Edit mode",
            FontSize = 28,
            Position = new GuiDim2(0.5f, 0f, 0f, 24f),
            Size = new GuiDim2(0f, 640f, 0f, 48f),
            AnchorPoint = new GuiPoint(0.5f, 0f)
        });
        gui.Add(new GuiButton
        {
            Name = "Jump",
            Text = "Jump",
            Position = new GuiDim2(0.5f, 0f, 1f, -40f),
            Size = new GuiDim2(0f, 200f, 0f, 56f),
            AnchorPoint = new GuiPoint(0.5f, 1f)
        });
        if (!GuiFile.TrySave(project.UiPath, gui, out var guiError))
        {
            throw new InvalidOperationException($"could not save the test project's UI: {guiError}");
        }

        return project;
    }

    private static void AddScript(Part part, string path)
    {
        if (!ScriptReference.TryCreate(path, out var reference))
        {
            throw new InvalidOperationException(path);
        }

        part.Scripts.Add(reference);
    }

    private readonly record struct Measurement(double Fps, double Cpu);

    /// <summary>Plays the steps from the window's UpdateFrame event (before the editor's own update each frame).</summary>
    private sealed class Driver(EngineWindow window, InputManager input, string[] steps, string? screenshotDir)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private GlErrorMonitor? _gl;
        private IntPtr _hwnd;
        private int _index;
        private int _wait;
        private bool _hasPointer;
        private float _x;
        private float _y;
        private bool _releasePending;
        private Task? _screenshot;
        private TimeSpan? _deadline;
        private TimeSpan _timedStart;
        private TimeSpan _timedCpuStart;
        private int _timedFrames;
        private string? _measuring;

        public int Completed => _index;

        public Dictionary<string, Measurement> Measurements { get; } = [];

        public IReadOnlyList<string> GlErrors => _gl?.Errors ?? [];

        public string GlMonitorNote => _gl?.Note ?? "GL monitor not installed";

        public void OnFrame(FrameEventArgs args)
        {
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = WindowCapture.Handle(window);
                _gl = GlErrorMonitor.Install();
                if (window.TryGetCurrentMonitorScale(out var scaleX, out var scaleY) && (scaleX != 1f || scaleY != 1f))
                {
                    TestReport.Info($"display scaling is {scaleX * 100:F0}%: the tour's click positions assume 100%");
                }
            }

            // Keep Wisko's pointer where the tour put it (a real mouse event cannot drag it away).
            if (_hasPointer)
            {
                input.OnMouseMove(0d, 0d, _x, _y);
            }

            if (_releasePending)
            {
                input.OnMouseButton(GlfwMouseButton.Left, false, _x, _y);
                _releasePending = false;
                _wait = 2;
                return;
            }

            if (_screenshot is { IsCompleted: false })
            {
                return;
            }

            if (_screenshot is { IsFaulted: true })
            {
                TestReport.Info($"screenshot failed: {_screenshot.Exception?.GetBaseException().Message}");
                _screenshot = null;
            }

            if (_wait > 0)
            {
                _wait--;
                return;
            }

            if (_deadline is { } deadline)
            {
                _timedFrames++;
                if (_clock.Elapsed < deadline)
                {
                    return;
                }

                FinishTimedWait();
            }

            if (_index >= steps.Length)
            {
                return;
            }

            var step = steps[_index++];
            var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "wait":
                    _wait = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "seconds":
                    StartTimedWait(null, double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "measure":
                    StartTimedWait(parts[1], double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "click":
                    _x = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    _y = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    _hasPointer = true;
                    input.OnMouseMove(0d, 0d, _x, _y);
                    input.OnMouseButton(GlfwMouseButton.Left, true, _x, _y);
                    _releasePending = true;
                    break;
                case "minimize":
                    window.WindowState = WindowState.Minimized;
                    break;
                case "restore":
                    window.WindowState = WindowState.Normal;
                    break;
                case "screenshot":
                    if (screenshotDir is not null)
                    {
                        var path = Path.Combine(screenshotDir, parts[1] + ".png");
                        var hwnd = _hwnd;
                        _screenshot = Task.Run(() => WindowCapture.SavePng(hwnd, path));
                    }

                    break;
                case "quit":
                    window.Close();
                    break;
                default:
                    throw new InvalidOperationException($"unknown step '{step}'");
            }
        }

        private void StartTimedWait(string? measure, double seconds)
        {
            _measuring = measure;
            _timedStart = _clock.Elapsed;
            _timedCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            _timedFrames = 0;
            _deadline = _timedStart + TimeSpan.FromSeconds(seconds);
        }

        private void FinishTimedWait()
        {
            _deadline = null;
            if (_measuring is null)
            {
                return;
            }

            var seconds = (_clock.Elapsed - _timedStart).TotalSeconds;
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - _timedCpuStart).TotalSeconds / seconds;
            Measurements[_measuring] = new Measurement(_timedFrames / seconds, cpu);
            _measuring = null;
        }
    }

    /// <summary>Mirrors console output (the editor's log) into memory for the checks.</summary>
    internal sealed class ConsoleTee : TextWriter
    {
        private readonly TextWriter _original;
        private readonly StringBuilder _text = new();

        private ConsoleTee(TextWriter original) => _original = original;

        public override Encoding Encoding => _original.Encoding;

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public static ConsoleTee Start()
        {
            var tee = new ConsoleTee(Console.Out);
            Console.SetOut(tee);
            return tee;
        }

        public void Stop() => Console.SetOut(_original);

        public override void Write(char value)
        {
            lock (_text)
            {
                _text.Append(value);
            }

            _original.Write(value);
        }

        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
            }

            _original.Write(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_text)
            {
                _text.AppendLine(value);
            }

            _original.WriteLine(value);
        }
    }
}
