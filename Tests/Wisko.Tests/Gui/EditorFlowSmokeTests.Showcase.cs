using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Tests.Checks;

namespace Wisko.Tests.Gui;

// The Graphics V1 showcase in the real editor: built by its maker (as --make-graphics-demo does), opened from the Hub,
// every area seen from its viewpoint in Edit mode, then played with scripted keys: the tour, night, fog, the quality
// presets, and Stop. Screenshots are taken for a visual check.
internal static partial class EditorFlowSmokeTests
{
    private static void RunShowcase(Action<string, Action> section, string work, string? screenshotDir, TestReport report)
    {
        section("GUI: Graphics V1 showcase (12 areas, tour, night, fog, quality presets, Stop), scripted input", () =>
        {
            var root = Path.Combine(work, "graphics-showcase");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            if (GraphicsShowcase.Make(Path.Combine(root, "Projects")) != 0)
            {
                report.Check("Showcase: the project was created", false);
                return;
            }

            RunSession(report, root, "showcase flow", (app, window) => new ShowcaseFlow(report, app, window, screenshotDir), (_, _, _) => { });
        });
    }

    private sealed class ShowcaseFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        // The tour's viewpoints (Tour.lua): position and what it looks at.
        private static readonly (string Name, Vector3 From, Vector3 At)[] Views =
        [
            ("pbr", new(-105f, 14f, 84f), new(-105f, 2f, 50f)),
            ("textured", new(-35f, 9f, 82f), new(-35f, 3f, 50f)),
            ("lights", new(35f, 6f, 60f), new(35f, 2f, 42f)),
            ("shadows", new(105f, 18f, 86f), new(105f, 0f, 48f)),
            ("glow", new(-105f, 6f, 2f), new(-105f, 4f, -24f)),
            ("ao", new(-35f, 9f, 6f), new(-35f, 2f, -24f)),
            ("daynight", new(35f, 8f, 6f), new(35f, 3f, -25f)),
            ("fog", new(74f, 6f, -20f), new(200f, 7f, -20f)),
            ("toon", new(-105f, 12f, -60f), new(-105f, 2f, -92f)),
            ("fox", new(-35f, 8f, -64f), new(-35f, 1f, -90f)),
            ("reflections", new(35f, 6f, -64f), new(35f, 3f, -90f)),
            ("quality", new(105f, 10f, -62f), new(105f, 2f, -92f))
        ];

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private PlayModeController PlayMode => (PlayModeController)typeof(Renderer).GetField("_playMode", Hidden)!.GetValue(Renderer)!;

        private void LookFrom(Vector3 from, Vector3 at)
        {
            var f = (at - from).Normalized();
            ViewCamera.Position = from;
            ViewCamera.SetOrientation(MathF.Atan2(f.Z, f.X) * 180f / MathF.PI, MathF.Asin(global::System.Math.Clamp(f.Y, -1f, 1f)) * 180f / MathF.PI);
        }

        private string Faults => string.Join(" | ", PlayMode.Runtime?.Scripts.Where(s => s.Fault is not null)
            .Select(s => $"{s.Reference.AssetPath}: {s.Fault!.Message}") ?? []);

        private IEnumerable<int> Key(Keys key, string label, int wait = 20)
        {
            Step = label;
            PressKey(key);
            yield return wait;
        }

        protected override IEnumerable<int> Steps()
        {
            foreach (var wait in OpenCard(GraphicsShowcase.ProjectName, "open the showcase"))
            {
                yield return wait;
            }

            yield return 30;
            Check("Showcase: opens with the Universal renderer, every model loaded",
                Renderer.UsingUniversal && Workspace.Parts.Where(p => p.Model is not null).All(p => p.Model!.Asset is not null),
                $"universal {Renderer.UsingUniversal}, {Workspace.Parts.Count(p => p.Model is not null)} models");
            for (var i = 0; i < Views.Length; i++)
            {
                Step = $"Edit: area {i + 1} {Views[i].Name}";
                LookFrom(Views[i].From, Views[i].At);
                yield return 12;
                foreach (var wait in Shot($"show-edit-{i + 1:00}-{Views[i].Name}"))
                {
                    yield return wait;
                }
            }

            Step = "Play";
            Click(Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight));
            yield return 60;
            Check("Showcase Play: every script runs without a fault", PlayMode.IsPlaying && Faults.Length == 0, Faults);
            foreach (var wait in Shot("show-play-01-tour"))
            {
                yield return wait;
            }

            // The coloured lights room, then the glow booth.
            foreach (var wait in Key(Keys.PageDown, "tour: next"))
            {
                yield return wait;
            }

            foreach (var wait in Key(Keys.PageDown, "tour: next (lights)", 40))
            {
                yield return wait;
            }

            foreach (var wait in Shot("show-play-03-lights"))
            {
                yield return wait;
            }

            for (var i = 0; i < 2; i++)
            {
                foreach (var wait in Key(Keys.PageDown, "tour: next"))
                {
                    yield return wait;
                }
            }

            yield return 30;
            foreach (var wait in Shot("show-play-05-glow"))
            {
                yield return wait;
            }

            // Day and night: the street at night.
            for (var i = 0; i < 2; i++)
            {
                foreach (var wait in Key(Keys.PageDown, "tour: next"))
                {
                    yield return wait;
                }
            }

            foreach (var wait in Key(Keys.N, "night", 40))
            {
                yield return wait;
            }

            var lampsOn = Workspace.Parts.Where(p => p.Name.StartsWith("Lamp ", StringComparison.Ordinal) && p.Light is not null).All(p => p.Light!.Enabled);
            Check("Showcase Play: N makes it night and the street lamps switch on (Lua: Light.Enabled, Material glow)", lampsOn);
            foreach (var wait in Shot("show-play-07-night"))
            {
                yield return wait;
            }

            foreach (var wait in Key(Keys.M, "morning", 30))
            {
                yield return wait;
            }

            // The fog avenue: the tour turns fog on there.
            foreach (var wait in Key(Keys.PageDown, "tour: next (fog)", 40))
            {
                yield return wait;
            }

            Check("Showcase Play: the fog area turns the session's fog on", PlayMode.PlayAtmosphere?.FogEnabled == true);
            foreach (var wait in Shot("show-play-08-fog"))
            {
                yield return wait;
            }

            // The Fox, then the quality bench at Low and at Ultra.
            for (var i = 0; i < 2; i++)
            {
                foreach (var wait in Key(Keys.PageDown, "tour: next"))
                {
                    yield return wait;
                }
            }

            yield return 40;
            foreach (var wait in Shot("show-play-10-fox"))
            {
                yield return wait;
            }

            for (var i = 0; i < 2; i++)
            {
                foreach (var wait in Key(Keys.PageDown, "tour: next"))
                {
                    yield return wait;
                }
            }

            foreach (var wait in Key(Keys.P, "pause the clock (same light for both presets)", 10))
            {
                yield return wait;
            }

            foreach (var wait in Key(Keys.D1, "quality Low", 30))
            {
                yield return wait;
            }

            var low = PlayMode.PlayGraphics?.Quality;
            foreach (var wait in Shot("show-play-12-low"))
            {
                yield return wait;
            }

            foreach (var wait in Key(Keys.D4, "quality Ultra", 40))
            {
                yield return wait;
            }

            var ultra = PlayMode.PlayGraphics?.Quality;
            var pointShadows = Renderer.Universal?.Stats.ShadowMapsRendered;
            foreach (var wait in Shot("show-play-12-ultra"))
            {
                yield return wait;
            }

            Check("Showcase Play: keys 1 and 4 switch the session's quality (Low, Ultra)", low == QualityLevel.Low && ultra == QualityLevel.Ultra,
                $"{low}, {ultra}, maps {pointShadows}");
            Check("Showcase Play: still no script faults after the tour", Faults.Length == 0, Faults);

            Step = "Stop";
            Click(Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight));
            yield return 30;
            Check("Showcase Stop: the project's settings are back (Medium, no fog) and the lamps are off again",
                PlayMode.PlayGraphics is null && PlayMode.PlayAtmosphere is null && Renderer.Graphics.Quality == QualityLevel.Medium
                && Workspace.Parts.Where(p => p.Name.StartsWith("Lamp ", StringComparison.Ordinal) && p.Light is not null).All(p => !p.Light!.Enabled));
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }
}
