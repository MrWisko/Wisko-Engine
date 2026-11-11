using System.Reflection;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Animation;
using Wisko.Engine.Core;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Engine.World;
using Wisko.Tests.Checks;

namespace Wisko.Tests.Gui;

// Animation System V1 in the real editor (OpenGL), driven by scripted input on the demo project's real rigged models:
// the rest pose in the editor, the Animation panel's preview (Play, Pause, scrub, Stop back to the exact rest image,
// never a change to the project), its saved settings (undo, save), Play (Play on start clips, a Lua character that
// walks, runs, jumps and waves from its actual movement), Stop, and the settings surviving leave + reopen.
internal static partial class EditorFlowSmokeTests
{
    private static void RunAnimation(Action<string, Action> section, string work, string? screenshotDir, TestReport report, string? only)
    {
        // Performance (Phase 11): only when asked for (--only performance), it is a measurement more than a check.
        if (only?.Contains("performance", StringComparison.OrdinalIgnoreCase) == true)
        {
            section("GUI performance: animated characters (1, 10, 50), OpenGL timings", () =>
            {
                var root = Path.Combine(work, "animation-perf");
                var projects = Path.Combine(root, "Projects");
                foreach (var count in PerformanceFlow.Counts)
                {
                    PerformanceFlow.CreateProject(projects, count);
                }

                RunSession(report, root, "performance flow", (app, window) => new PerformanceFlow(report, app, window, screenshotDir), (_, _, _) => { });
            });
            return;
        }

        section("GUI: animated models (viewport, Animation panel preview, Lua character, save and reopen), scripted input", () =>
        {
            var root = Path.Combine(work, "animation");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            if (AnimationDemo.Make(Path.Combine(root, "Projects")) != 0)
            {
                report.Check("animation flow: the demo project was created", false);
                return;
            }

            RunSession(report, root, "animation flow", (app, window) => new AnimationFlow(report, app, window, screenshotDir), AnimationFlow.After);
        });
    }

    // Projects of 1, 10 and 50 Robots (RobotExpressive: 4 skinned meshes and 15 rigid parts each), every one playing its own
    // clip on start. Each is opened: the parts' drawing is timed (CPU and GPU) at rest in Edit mode, then animated in Play.
    private sealed class PerformanceFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public static readonly int[] Counts = [1, 10, 50];

        public static void CreateProject(string projectsRoot, int count)
        {
            var store = new ProjectStore(projectsRoot);
            if (!store.TryCreateProject($"Perf {count}", WorldTemplate.Classic, new Workspace(), out var project, out var error))
            {
                throw new InvalidOperationException(error);
            }

            using var decoder = new Wisko.Engine.Assets.Importing.AssimpModelImporter();
            if (!new Wisko.Engine.Assets.ProjectModelImporter(decoder).TryImport(AnimationChecks.ModelFile("RobotExpressive", "RobotExpressive.glb"), project.FolderPath, out var import, out error))
            {
                throw new InvalidOperationException(error);
            }

            var workspace = new Workspace();
            var clips = import.Asset.Animation.Clips;
            for (var i = 0; i < count; i++)
            {
                var part = new Part($"Robot {i + 1}", Engine.Math.Vector3.Zero, new Engine.Math.Vector3(1f, 1f, 1f), ModelReference.DefaultTint)
                {
                    Model = new ModelReference(import.Asset.AssetPath, import.Asset),
                    Animation = new ModelAnimationSettings { Clip = clips[i % clips.Count].Name, PlayOnStart = true, Speed = 0.8f + 0.1f * (i % 5) }
                };
                ModelTransform.SetInstanceTransform(part, new Engine.Math.Vector3((i % 10 - 4.5f) * 4f, 0f, -(i / 10) * 5f), Engine.Math.Vector3.Zero, new Engine.Math.Vector3(1f, 1f, 1f));
                workspace.AddPart(part);
            }

            SceneFile.TrySave(workspace, project.ScenePath, out _);
        }

        protected override IEnumerable<int> Steps()
        {
            Renderer.MeasurePartTiming = true;
            try
            {
                foreach (var count in Counts)
                {
                    foreach (var wait in OpenCard($"Perf {count}", $"open Perf {count}"))
                    {
                        yield return wait;
                    }

                    yield return 30;
                    var rest = new List<(double Cpu, double Gpu)>();
                    for (var i = 0; i < 120; i++)
                    {
                        rest.Add((Renderer.LastPartsCpuMs, Renderer.LastPartsGpuMs));
                        yield return 0; // the next frame (a wait of 1 would skip one)
                    }

                    var play = Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight);
                    Step = $"Perf {count}: Play";
                    Click(play);
                    yield return 30;
                    var animated = new List<(double Cpu, double Gpu)>();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (var i = 0; i < 180; i++)
                    {
                        animated.Add((Renderer.LastPartsCpuMs, Renderer.LastPartsGpuMs));
                        yield return 0;
                    }

                    var fps = 180 / clock.Elapsed.TotalSeconds;
                    var playing = Workspace.Parts.Count(p => p.Animator?.IsPlaying == true);
                    foreach (var wait in Shot($"perf-{count}"))
                    {
                        yield return wait;
                    }

                    Step = $"Perf {count}: Stop";
                    Click(play);
                    yield return 20;
                    TestReport.Info($"PERF {count,2} robots: draw parts CPU at rest {Mean(rest, r => r.Cpu):F3} ms, animated {Mean(animated, r => r.Cpu):F3} ms (p95 {P95(animated, r => r.Cpu):F3}); "
                        + $"GPU at rest {Mean(rest, r => r.Gpu):F3} ms, animated {Mean(animated, r => r.Gpu):F3} ms; {fps:F1} fps in Play; {playing} animating");
                    Check($"Performance, {count} robots: all animate, 60 fps held, GPU time measured", playing == count && fps > 55 && Mean(animated, r => r.Gpu) > 0,
                        $"{playing} animating, {fps:F1} fps");
                    foreach (var wait in LeaveToHub($"leave Perf {count}"))
                    {
                        yield return wait;
                    }
                }
            }
            finally
            {
                Renderer.MeasurePartTiming = false;
            }

            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }

        private static double Mean(List<(double Cpu, double Gpu)> samples, Func<(double Cpu, double Gpu), double> pick) => samples.Average(pick);

        private static double P95(List<(double Cpu, double Gpu)> samples, Func<(double Cpu, double Gpu), double> pick)
        {
            var sorted = samples.Select(pick).OrderBy(v => v).ToList();
            return sorted[(int)(sorted.Count * 0.95)];
        }
    }

    private sealed class AnimationFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        private int _skinnedMeshesFirstSession;

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var project = new ProjectStore(Path.Combine(root, "Projects")).ListProjects().First(p => p.Name == AnimationDemo.ProjectName);
            var json = File.ReadAllText(project.ScenePath);
            report.Check("Saved Scene.json: Fox Walk's animation settings (Run, Play on start, speed 1.25)",
                json.Contains("\"Clip\": \"Run\"", StringComparison.Ordinal) && json.Contains("\"Speed\": 1.25", StringComparison.Ordinal));
        }

        private byte[] Pixels() => WindowCapture.ReadGlFrontBuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y);

        private Part PartNamed(string name) => Workspace.Parts.First(part => part.Name == name);

        private bool Dirty => (bool)typeof(EngineApplication).GetProperty("HasUnsavedChanges", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(App)!;

        private UiBox Panel => (UiBox)typeof(Renderer).GetProperty("AnimationPanelRect", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Renderer)!;

        private static (float X, float Y) Mid(UiBox box) => (box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f);

        private (float X, float Y) PlayButton => Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight);

        protected override IEnumerable<int> Steps()
        {
            foreach (var wait in OpenCard(AnimationDemo.ProjectName, "open the demo"))
            {
                yield return wait;
            }

            yield return 30;
            Check("Animation demo: 14 objects loaded, 13 with their models", PartCount == 14 && Workspace.Parts.Count(p => p.Model?.Asset is not null) == 13,
                $"{PartCount} parts, {Workspace.Parts.Count(p => p.Model?.Asset is not null)} models loaded");
            Check("Edit mode: no model is animated (rest pose)", Workspace.Parts.All(p => p.Animator is null));
            foreach (var wait in Shot("anim-01-edit-rest"))
            {
                yield return wait;
            }

            // ---- Test F: the Animation panel previews a clip without Play and without changing the project.
            Step = "World: expand Fox Herd";
            Click(GroupArrow("Fox Herd"));
            yield return 6;
            Step = "World: select Fox Walk";
            Click(PartRow("Fox Walk"));
            yield return 6;
            Step = "ToolBench menu";
            Click(Find((x, y) => Toolbar.TryHitToolBench(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight));
            yield return 4;
            Step = "ToolBench > Animation";
            Click(FindMenuItem("Animation"));
            yield return 10;
            var fox = PartNamed("Fox Walk");
            var clips = fox.Model!.Asset!.Animation.Clips;
            Check("Animation panel: open, showing Fox Walk's 3 clips with its saved clip (Walk) chosen",
                Panel.Width > 0f && clips.Count == 3 && (int)Field("_animationClip") == 1, $"chosen {Field("_animationClip")}");
            var restWithPanel = Pixels();
            foreach (var wait in Shot("anim-05-panel-rest"))
            {
                yield return wait;
            }

            Step = "panel: choose Run";
            Click(Mid(AnimationPanel.ClipRow(Panel, clips.Count, 2)));
            yield return 4;
            Step = "panel: Play the preview";
            Click(Mid(AnimationPanel.TransportButton(Panel, clips.Count, 0)));
            yield return 30;
            Check("Preview: Run plays on Fox Walk in Edit mode (no Play session)",
                !Renderer.IsPlaying && fox.Animator is { IsPlaying: true, CurrentClip.Name: "Run" } && ReferenceEquals(Renderer.AnimationPreviewPart, fox),
                $"{fox.Animator?.CurrentClip?.Name}, playing {fox.Animator?.IsPlaying}");
            var previewA = Pixels();
            yield return 12;
            var previewB = Pixels();
            Check("Preview: the fox visibly moves in the viewport", Difference(previewA, previewB) > 200, $"{Difference(previewA, previewB)} pixels differ");
            Check("Preview: the project is not changed by previewing", !Dirty);
            foreach (var wait in Shot("anim-06-preview-run"))
            {
                yield return wait;
            }

            Step = "panel: Pause";
            Click(Mid(AnimationPanel.TransportButton(Panel, clips.Count, 1)));
            yield return 6;
            var pausedA = Pixels();
            var pausedTime = fox.Animator?.Time ?? -1d;
            yield return 15;
            var pausedB = Pixels();
            Check("Pause: the pose holds (time and image unchanged)", fox.Animator is { IsPaused: true } && fox.Animator.Time == pausedTime && Difference(pausedA, pausedB) == 0,
                $"time {pausedTime:F3} -> {fox.Animator?.Time:F3}, {Difference(pausedA, pausedB)} pixels differ");

            var timeline = AnimationPanel.Timeline(Panel, clips.Count);
            foreach (var wait in DragRow((timeline.X + 4f, timeline.Y + timeline.Height * 0.5f), (timeline.X + timeline.Width * 0.5f, timeline.Y + timeline.Height * 0.5f), "panel: scrub to the middle"))
            {
                yield return wait;
            }

            var run = clips[2];
            Check("Scrub: the timeline sets the time (about half of Run), still paused",
                fox.Animator is { IsPaused: true } scrubbed && Math.Abs(scrubbed.Time - run.Duration * 0.5) < run.Duration * 0.08,
                $"{fox.Animator?.Time:F3} of {run.Duration:F3}");
            var scrubbedImage = Pixels();
            Check("Scrub: the pose on screen changed", Difference(pausedB, scrubbedImage) > 100, $"{Difference(pausedB, scrubbedImage)} pixels differ");
            foreach (var wait in Shot("anim-07-preview-scrubbed"))
            {
                yield return wait;
            }

            Step = "panel: Stop";
            Click(Mid(AnimationPanel.TransportButton(Panel, clips.Count, 2)));
            yield return 8;
            // The 3D view only: the panel itself now shows another chosen clip.
            var stopped = Pixels();
            var viewDifference = ViewDifference(restWithPanel, stopped);
            Check("Preview Stop: back in the rest pose, the exact 3D view from before the preview", fox.Animator is null && viewDifference == 0,
                $"{viewDifference} pixels differ in the 3D view");
            Check("Preview Stop: still no change to the project", !Dirty);

            // Saved settings: Loop off and back with undo, Speed up one step, Play on start for Run.
            Step = "panel: Loop off";
            Click(Mid(AnimationPanel.LoopRow(Panel, clips.Count)));
            yield return 4;
            Check("Settings: Loop off is a change of the project", fox.Animation is { Loop: false } && Dirty);
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Settings: Ctrl+Z brings Loop back", fox.Animation?.Loop != false, $"{fox.Animation}");
            Step = "panel: Speed +";
            Click(Mid(AnimationPanel.SpeedControl(Panel, clips.Count, 1)));
            yield return 4;
            Step = "panel: Play on start (Run)";
            Click(Mid(AnimationPanel.PlayOnStartRow(Panel, clips.Count)));
            yield return 4;
            Check("Settings: Run plays on start at 1.25x", fox.Animation is { PlayOnStart: true, Clip: "Run", Speed: 1.25f }, $"{fox.Animation}");
            foreach (var wait in Shot("anim-08-panel-settings"))
            {
                yield return wait;
            }

            foreach (var wait in ProjectMenu("Save Project", "save the settings"))
            {
                yield return wait;
            }

            Check("Saved: nothing left unsaved", !Dirty);

            // ---- Play: the saved settings, the Lua character (Test E) and independent instances (Test C).
            Step = "Play";
            Click(PlayButton);
            yield return 45;
            var robot = PartNamed("Robot");
            Check("Play: Fox Walk plays its saved Play on start clip (Run at 1.25x); the others theirs",
                fox.Animator is { CurrentClip.Name: "Run", Speed: 1.25f } && PartNamed("Fox Survey").Animator?.CurrentClip?.Name == "Survey"
                && PartNamed("Fox Run").Animator is { CurrentClip.Name: "Run", Speed: 0.5f },
                $"{fox.Animator?.CurrentClip?.Name} at {fox.Animator?.Speed}");
            Check("Play: one model asset, independent playbacks (four foxes, four states)",
                Workspace.Parts.Where(p => p.Name.StartsWith("Fox", StringComparison.Ordinal)).Select(p => p.Animator).Distinct().Count() == 4
                && PartNamed("Fox Tempo").Animator?.Speed != 1f);
            Check("Play: the character stands idle", robot.Animator?.CurrentClip?.Name == "Idle", robot.Animator?.CurrentClip?.Name);
            _skinnedMeshesFirstSession = SkinnedMesh.LiveCount;
            foreach (var wait in Shot("anim-09-play-idle"))
            {
                yield return wait;
            }

            Step = "hold W";
            SetKey(Keys.W, true);
            yield return 50;
            Check("Lua: walking forward plays Walking (from the body's real speed)", robot.Animator?.CurrentClip?.Name == "Walking", robot.Animator?.CurrentClip?.Name);
            foreach (var wait in Shot("anim-10-walking"))
            {
                yield return wait;
            }

            Step = "hold Shift + W";
            SetKey(Keys.LeftShift, true);
            yield return 60;
            Check("Lua: running plays Running", robot.Animator?.CurrentClip?.Name == "Running", robot.Animator?.CurrentClip?.Name);
            foreach (var wait in Shot("anim-11-running"))
            {
                yield return wait;
            }

            SetKey(Keys.LeftShift, false);
            SetKey(Keys.W, false);
            Step = "release: stop";
            yield return 70;
            Check("Lua: standing still again crossfades back to Idle", robot.Animator?.CurrentClip?.Name == "Idle", robot.Animator?.CurrentClip?.Name);
            Step = "Space: jump";
            PressKey(Keys.Space);
            yield return 8;
            Check("Lua: Space plays Jump once", robot.Animator is { CurrentClip.Name: "Jump", Loop: false }, robot.Animator?.CurrentClip?.Name);
            foreach (var wait in Shot("anim-12-jump"))
            {
                yield return wait;
            }

            yield return 90;
            Check("Lua: after landing the movement clips take over again", robot.Animator?.CurrentClip?.Name == "Idle", robot.Animator?.CurrentClip?.Name);
            Step = "E: wave";
            PressKey(Keys.E);
            yield return 30;
            Check("Lua: E plays the one-shot Wave", robot.Animator is { CurrentClip.Name: "Wave", Loop: false }, robot.Animator?.CurrentClip?.Name);
            foreach (var wait in Shot("anim-13-wave"))
            {
                yield return wait;
            }

            yield return 150;
            Check("Lua: when Wave finishes (OnAnimationFinished) the character idles again", robot.Animator?.CurrentClip?.Name == "Idle", robot.Animator?.CurrentClip?.Name);

            Step = "Escape: stop Play";
            PressKey(Keys.Escape);
            yield return 20;
            Check("Stop: every animator is detached (rest pose again) and the project is unchanged", !Renderer.IsPlaying && Workspace.Parts.All(p => p.Animator is null) && !Dirty);
            foreach (var wait in Shot("anim-14-stopped"))
            {
                yield return wait;
            }

            // ---- Test G: leave, reopen, the settings and the animations are still there.
            foreach (var wait in LeaveToHub("leave"))
            {
                yield return wait;
            }

            foreach (var wait in OpenCard(AnimationDemo.ProjectName, "reopen"))
            {
                yield return wait;
            }

            yield return 30;
            var reopened = PartNamed("Fox Walk");
            Check("Reopened: Fox Walk keeps its settings (Run on start, 1.25x) and its model", reopened.Animation is { PlayOnStart: true, Clip: "Run", Speed: 1.25f }
                && reopened.Model?.Asset?.Animation.Clips.Count == 3, $"{reopened.Animation}");
            Step = "Play after reopening";
            Click(PlayButton);
            yield return 40;
            Check("Reopened: Play animates the models again (Fox Walk runs, the character idles)",
                reopened.Animator?.CurrentClip?.Name == "Run" && PartNamed("Robot").Animator?.CurrentClip?.Name == "Idle");
            // The first session also had the selected fox's skinned outline; a leak would roughly double the count.
            Check("GPU resources: reopening the project does not add skinned meshes (the first session's were released)",
                _skinnedMeshesFirstSession > 0 && SkinnedMesh.LiveCount > 0 && SkinnedMesh.LiveCount <= _skinnedMeshesFirstSession,
                $"{_skinnedMeshesFirstSession} -> {SkinnedMesh.LiveCount}");
            foreach (var wait in Shot("anim-15-reopened-play"))
            {
                yield return wait;
            }

            PressKey(Keys.Escape);
            yield return 20;
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }

        private object Field(string name) => typeof(Renderer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Renderer)!;

        // Differing pixels inside the 3D view (between the left panels and World, below the tab strip, above Output).
        private int ViewDifference(byte[] a, byte[] b)
        {
            var scale = Window.FramebufferSize.X / (float)Width;
            var stride = Window.FramebufferSize.X;
            var left = (int)((Panel.X + Panel.Width + 4f) * scale);
            var right = (int)((Width - WorldExplorerRenderer.SidebarWidth - 12f) * scale);
            var top = (int)((ToolbarRenderer.ToolbarHeight + 40f) * scale);
            var bottom = (int)((Height - 40f) * scale);
            var count = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var i = (y * stride + x) * 4;
                    if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        // Pixels whose colour differs noticeably between two frames of the same size.
        private static int Difference(byte[] a, byte[] b)
        {
            var count = 0;
            for (var i = 0; i + 3 < a.Length && i + 3 < b.Length; i += 4)
            {
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
