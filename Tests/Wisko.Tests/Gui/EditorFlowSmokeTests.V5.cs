using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Tests.Checks;

namespace Wisko.Tests.Gui;

// Lua Capabilities V5 in the real editor (OpenGL), on the showcase project, with scripted input: the flyover (a named
// camera shown instead of the gameplay camera, blended back), the security camera feed rendered into the HUD (render to
// texture), rain particles, debug lines, and Stop letting all of it go. Screenshots are taken for a visual check.
internal static partial class EditorFlowSmokeTests
{
    private static void RunV5(Action<string, Action> section, string work, string? screenshotDir, TestReport report)
    {
        section("GUI: Lua V5 showcase (cameras, camera feed, particles, debug lines, Stop), scripted input", () =>
        {
            var root = Path.Combine(work, "v5");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            if (LuaV5Demo.Make(Path.Combine(root, "Projects")) != 0)
            {
                report.Check("V5 flow: the showcase project was created", false);
                return;
            }

            RunSession(report, root, "V5 flow", (app, window) => new V5Flow(report, app, window, screenshotDir), (_, _, _) => { });
        });
        section("GUI: Graphics V1 + Lua V5 showcase under the Universal renderer (camera feed, particles, debug lines), scripted input", () =>
        {
            var root = Path.Combine(work, "v5-universal");
            var projects = Path.Combine(root, "Projects");
            Directory.CreateDirectory(projects);
            if (LuaV5Demo.Make(projects) != 0
                || !Wisko.Engine.Graphics.GraphicsFile.TrySave(Path.Combine(projects, LuaV5Demo.ProjectName, Wisko.Engine.Graphics.GraphicsFile.FileName),
                    Wisko.Engine.Graphics.GraphicsSettings.Default, out _))
            {
                report.Check("V5 flow (Universal): the showcase project was created", false);
                return;
            }

            RunSession(report, root, "V5 flow (Universal)", (app, window) => new V5Flow(report, app, window, screenshotDir, universal: true), (_, _, _) => { });
        });
    }

    private sealed class V5Flow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir, bool universal = false)
        : EditorFlow(report, app, window, screenshotDir)
    {
        // The Universal variant labels its checks so both runs stay apart in the report.
        private new void Check(string label, bool passed, string? detail = null) => base.Check(universal ? "[Universal] " + label : label, passed, detail);

        private byte[] Pixels() => WindowCapture.ReadGlFrontBuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y);

        private PlayModeController PlayMode => (PlayModeController)typeof(Renderer).GetField("_playMode", Hidden)!.GetValue(Renderer)!;

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private GameplaySession? Gameplay => PlayMode.Gameplay;

        private int FeedTargets => ((System.Collections.ICollection)typeof(Renderer).GetField("_cameraFeeds", Hidden)!.GetValue(Renderer)!).Count;

        private (float X, float Y) PlayButton => Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight);

        // Pixels of a window-space rectangle (logical = framebuffer pixels at 100 %) that differ between two frames.
        private int RegionDifference(byte[] a, byte[] b, int x0, int y0, int x1, int y1)
        {
            var width = Window.FramebufferSize.X;
            var height = Window.FramebufferSize.Y;
            var count = 0;
            for (var y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
            {
                // Pixels() gives top-down rows.
                var row = y * width * 4;
                for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
                {
                    var i = row + x * 4;
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

        // Wall time over some frames (diagnostics): frames per second.
        private IEnumerable<int> MeasureFps(string label, int frames)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var longest = 0d;
            var last = 0d;
            for (var i = 0; i < frames; i++)
            {
                yield return 0;
                var now = clock.Elapsed.TotalMilliseconds;
                longest = Math.Max(longest, now - last);
                last = now;
            }

            TestReport.Info($"V5{(universal ? " Universal" : "")} FPS {label}: {frames / clock.Elapsed.TotalSeconds:F1} fps, longest frame {longest:F1} ms, parts {PartCount}");
        }

        // Frames for about this many seconds of real time.
        private static IEnumerable<int> Seconds(double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                yield return 0;
            }
        }

        // Frames until the condition holds (or the timeout, in seconds, passes).
        private static IEnumerable<int> Until(Func<bool> condition, double timeout)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < timeout)
            {
                yield return 0;
            }
        }

        // The share of the most common (coarse) colour: near 1 for a flat image.
        private static double LargestColorShare(byte[] rgba)
        {
            var counts = new Dictionary<int, int>();
            var samples = 0;
            for (var i = 0; i < rgba.Length; i += 4 * 7)
            {
                var key = (rgba[i] >> 3) << 10 | (rgba[i + 1] >> 3) << 5 | (rgba[i + 2] >> 3);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                samples++;
            }

            return samples == 0 ? 1d : counts.Values.Max() / (double)samples;
        }

        // Mean colour difference (0..255 per channel) between the screen inside rect and the feed texture (rows bottom-up)
        // sampled at the same relative places: small when the screen shows that feed the right way up.
        private double FeedMatch(byte[] screen, byte[] feed, int feedWidth, int feedHeight, Wisko.Engine.Gui.GuiRect rect)
        {
            var width = Window.FramebufferSize.X;
            var height = Window.FramebufferSize.Y;
            var total = 0d;
            var samples = 0;
            for (var sy = 1; sy < 12; sy++)
            {
                for (var sx = 1; sx < 16; sx++)
                {
                    var u = sx / 16d;
                    var v = sy / 12d;
                    var x = (int)(rect.X + u * rect.Width);
                    var y = (int)(rect.Y + v * rect.Height);
                    var s = (y * width + x) * 4;
                    var fx = (int)(u * (feedWidth - 1));
                    var fy = (int)((1d - v) * (feedHeight - 1));
                    var f = (fy * feedWidth + fx) * 4;
                    total += (Math.Abs(screen[s] - feed[f]) + Math.Abs(screen[s + 1] - feed[f + 1]) + Math.Abs(screen[s + 2] - feed[f + 2])) / 3d;
                    samples++;
                }
            }

            return total / samples;
        }

        private static int DistinctColors(byte[] rgba)
        {
            var colors = new HashSet<int>();
            for (var i = 0; i < rgba.Length; i += 4 * 7)
            {
                colors.Add((rgba[i] >> 3) << 10 | (rgba[i + 1] >> 3) << 5 | (rgba[i + 2] >> 3));
            }

            return colors.Count;
        }

        protected override IEnumerable<int> Steps()
        {
            foreach (var wait in OpenCard(LuaV5Demo.ProjectName, "open the showcase"))
            {
                yield return wait;
            }

            yield return 30;
            Check("V5 showcase: the scene and its 3 models loaded", PartCount > 40 && Workspace.Parts.Count(p => p.Model?.Asset is not null) == 4,
                $"{PartCount} parts, {Workspace.Parts.Count(p => p.Model?.Asset is not null)} models");
            Check("V5 showcase: drawn by the renderer its Graphics settings ask for", Renderer.UsingUniversal == universal,
                $"Universal {Renderer.UsingUniversal}, {Renderer.Universal?.FailureReason}");
            foreach (var wait in MeasureFps("edit", 60))
            {
                yield return wait;
            }

            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "01-edit"))
            {
                yield return wait;
            }

            Step = "Play";
            Click(PlayButton);
            yield return 3;

            // The flyover starts with Play. Gameplay timing is real time, so waits below are in seconds, not frames (the
            // frame rate depends on the display: a sleeping display paces frames at a few per second).
            var flyover = Gameplay?.Cameras.Active;
            Check("Play: the flyover camera is shown (Cameras.SetActive), the view follows its pose",
                flyover is { Name: "Flyover" } && (ViewCamera.Position - flyover.Pose.Position).Length < 1f,
                $"{flyover?.Name}, view {ViewCamera.Position}, camera {flyover?.Pose.Position}");
            // The lighting stays put for the checks below: the game clock stops at 10:00 (weather still changes on T).
            Gameplay!.Clock.Set(1, 10);
            Gameplay.Clock.Paused = true;
            var flyA = Pixels();
            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "02-flyover-a"))
            {
                yield return wait;
            }

            foreach (var wait in Seconds(2.5))
            {
                yield return wait;
            }

            var flyB = Pixels();
            Check("Flyover: the camera moves along its path (the view changes)", Difference(flyA, flyB) > 20000, $"{Difference(flyA, flyB)} pixels differ");
            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "03-flyover-b"))
            {
                yield return wait;
            }

            foreach (var wait in Until(() => Gameplay?.Cameras is { Active: null, IsBlending: false }, 20))
            {
                yield return wait;
            }

            Check("Flyover over: back on the gameplay camera (blended back)", Gameplay?.Cameras.Active is null && !Gameplay!.Cameras.IsBlending, Gameplay?.Cameras.Active?.Name);
            foreach (var wait in MeasureFps("play", 60))
            {
                yield return wait;
            }

            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "04-gameplay"))
            {
                yield return wait;
            }

            // The security camera's feed in the HUD (render to texture).
            var beforeMonitor = Pixels();
            Step = "M: vault camera on the HUD";
            PressKey(Keys.M);
            yield return 20;
            var feed = Renderer.ReadCameraFeed("VaultCam", out var feedWidth, out var feedHeight);
            Check("Camera feed: VaultCam renders into a 384x216 texture every frame", Renderer.LastCameraFeedCount == 1 && feed is not null && feedWidth == 384 && feedHeight == 216,
                $"{Renderer.LastCameraFeedCount} feeds");
            Check("Camera feed: the texture shows the scene (not one flat colour)", feed is not null && DistinctColors(feed) > 12 && LargestColorShare(feed) < 0.8,
                feed is null ? "none" : $"{DistinctColors(feed)} colours, largest {LargestColorShare(feed):P0}");
            var withMonitor = Pixels();
            // Where the HUD's Image ("camera:VaultCam") is laid out on screen: its pixels now are the feed's, the right way up.
            var image = PlayMode.Gui!.Layout.Entries.First(entry => entry.Element.Name == "MonitorFeed").Rect;
            var monitorDifference = RegionDifference(beforeMonitor, withMonitor, (int)image.X, (int)image.Y, (int)image.Right, (int)(image.Y + image.Height));
            var match = feed is null ? double.MaxValue : FeedMatch(withMonitor, feed, feedWidth, feedHeight, image);
            Check("Camera feed: the HUD's Image shows it on screen, the right way up", monitorDifference > image.Width * image.Height * 0.3 && match < 30,
                $"{monitorDifference} pixels changed, mean difference from the feed {match:F1}, image at {image}, framebuffer {Window.FramebufferSize}, logical {Width}x{Height}");
            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "05-security-feed"))
            {
                yield return wait;
            }

            // Rain: T moves the weather on (Clear -> Cloudy -> Rain), and particles are drawn.
            Step = "T, T: rain";
            PressKey(Keys.T);
            yield return 10;
            PressKey(Keys.T);
            foreach (var wait in Seconds(8))
            {
                yield return wait;
            }
            Check("Weather: rain particles are simulated and drawn", Renderer.LastParticleCount > 800 && Gameplay!.Particles.TotalCount > 800,
                $"{Renderer.LastParticleCount} drawn");
            Check("Weather: the Atmosphere's fog is on (the session's, not the project's)", PlayMode.PlayAtmosphere is { FogEnabled: true });
            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "06-rain"))
            {
                yield return wait;
            }

            // Debug lines: the guard's path, grid and view cone, and the lift's path.
            Step = "N: debug lines";
            PressKey(Keys.N);
            yield return 20;
            Check("Debug lines: drawn (guard path, navigation grid, sensor cone, lift path)", Renderer.LastDebugLineCount > 100, $"{Renderer.LastDebugLineCount} lines");
            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "07-debug-lines"))
            {
                yield return wait;
            }

            // Diagnostics: what the V5 passes cost per frame with the security feed, rain and debug lines all on (GPU
            // time queries; the frame rate itself depends on the display).
            Renderer.MeasurePartTiming = true;
            var timings = new List<(double FeedsCpu, double FeedsGpu, double DrawCpu, double DrawGpu, double PartsCpu, double PartsGpu)>();
            try
            {
                yield return 5;
                for (var i = 0; i < 60; i++)
                {
                    timings.Add((Renderer.LastFeedsCpuMs, Renderer.LastFeedsGpuMs, Renderer.LastGameplayDrawCpuMs, Renderer.LastGameplayDrawGpuMs,
                        Renderer.LastPartsCpuMs, Renderer.LastPartsGpuMs));
                    yield return 0;
                }
            }
            finally
            {
                Renderer.MeasurePartTiming = false;
            }

            TestReport.Info($"V5 PASSES ({Renderer.LastCameraFeedCount} feed 384x216, {Renderer.LastParticleCount} particles, {Renderer.LastDebugLineCount} lines, {PartCount} parts): "
                + $"feeds CPU {timings.Average(t => t.FeedsCpu):F3} ms GPU {timings.Average(t => t.FeedsGpu):F3} ms; "
                + $"particles+lines CPU {timings.Average(t => t.DrawCpu):F3} ms GPU {timings.Average(t => t.DrawGpu):F3} ms; "
                + $"main view parts CPU {timings.Average(t => t.PartsCpu):F3} ms GPU {timings.Average(t => t.PartsGpu):F3} ms");
            foreach (var wait in MeasureFps("play with feed, rain and debug lines", 120))
            {
                yield return wait;
            }

            Step = "Escape: stop Play";
            PressKey(Keys.Escape);
            yield return 20;
            Check("Stop: no camera feeds, particles or debug lines are left, the project is unchanged",
                !Renderer.IsPlaying && FeedTargets == 0 && Renderer.LastParticleCount == 0 && Renderer.LastDebugLineCount == 0 && PlayMode.Gameplay is null,
                $"feeds {FeedTargets}, particles {Renderer.LastParticleCount}");
            foreach (var wait in Shot((universal ? "v5u-" : "v5-") + "08-stopped"))
            {
                yield return wait;
            }

            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }
}
