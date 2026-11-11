using System.Text.Json;
using Wisko.Engine.Core;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;

namespace Wisko.Tests.Gui;

// The New World screen's template pictures, rendered by Wisko itself: each template is created through the real New
// World screen (so it is exactly the world a person gets), framed by a fixed camera and captured world-only with the
// Universal renderer at a new world's defaults, at 1x, 2x and 3x of the card's picture size (box-filtered down from the
// window, 4x MSAA). The manifest records the fingerprints the tests compare against the templates as they are now.
//
//   dotnet run --project Tests/Wisko.Tests -- --make-template-previews [folder]   (default: Engine/Assets/TemplatePreviews)
internal static partial class EditorFlowSmokeTests
{
    /// <summary>
    /// Where each template is looked at from: a camera position, the point it looks at and the lens (vertical field of
    /// view; narrower than the editor's 55 degrees, from further away, so nothing is stretched at the picture's sides).
    /// </summary>
    internal static readonly Dictionary<WorldTemplate, (Vector3 Position, Vector3 Target, float FieldOfView)> TemplateCameras = new()
    {
        // The empty baseplate from low over one corner: its edge and the ground reaching away under the open sky.
        [WorldTemplate.Classic] = (new Vector3(300f, 26f, 330f), new Vector3(150f, -6f, 170f), 34f),

        // The three test cubes (green, red, blue from left to right) with their shadows.
        [WorldTemplate.Testing] = (new Vector3(-20f, 10f, 40f), new Vector3(0.5f, 2.8f, 0f), 30f)
    };

    public static int MakeTemplatePreviews(string folder)
    {
        Directory.CreateDirectory(folder);
        var work = Path.Combine(Path.GetTempPath(), "wisko-template-previews-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(work, "Projects"));
        var report = new TestReport();
        try
        {
            report.Section("Template previews (rendered by Wisko)", () =>
                RunSession(report, work, "template previews", (app, window) => new TemplatePreviewFlow(report, app, window, folder), TemplatePreviewFlow.After));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A temporary folder of an unnamed world's run: nothing of the person's.
            }
        }

        return report.PrintSummary() ? 0 : 1;
    }

    /// <summary>The repository's Engine/Assets/TemplatePreviews (found above the test binaries), or null.</summary>
    internal static string? DefaultTemplatePreviewFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wisko.sln")))
            {
                return Path.Combine(dir.FullName, "Engine", "Assets", "TemplatePreviews");
            }
        }

        return null;
    }

    internal static (float Yaw, float Pitch) LookAt(Vector3 position, Vector3 target)
    {
        var forward = (target - position).Normalized();
        return (MathF.Atan2(forward.Z, forward.X) * 180f / MathF.PI, MathF.Asin(global::System.Math.Clamp(forward.Y, -1f, 1f)) * 180f / MathF.PI);
    }

    private sealed class TemplatePreviewFlow(TestReport report, EngineApplication app, EngineWindow window, string folder)
        : EditorFlow(report, app, window, null)
    {
        private readonly Dictionary<string, TemplatePreviewRecord> _records = new();

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private PhysicsEnvironment Environment => PlayModeControllerAccess.Environment(Renderer);

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var maker = (TemplatePreviewFlow)flow;
            var manifest = new TemplatePreviewManifest(1, TemplatePreviews.Width, TemplatePreviews.Height, TemplatePreviews.Densities, maker._records,
                "dotnet run --project Tests/Wisko.Tests -- --make-template-previews");
            var path = Path.Combine(maker.Folder, TemplatePreviews.ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            report.Check("Template previews: manifest written with every template", maker._records.Count == TemplatePreviews.Templates.Length, path);
        }

        private string Folder => folder;

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            for (var index = 0; index < TemplatePreviews.Templates.Length; index++)
            {
                var template = TemplatePreviews.Templates[index];
                Step = $"{template}: Hub New World";
                Click(Center(Hub.NewWorldButton()));
                yield return 6;
                Step = $"{template}: its card";
                Click(Center((UiBox)typeof(ProjectHubRenderer).GetMethod("GetTemplateCard", Hidden)!.Invoke(Hub, [index])!));
                yield return 40;

                var (position, target, fieldOfView) = TemplateCameras[template];
                var (yaw, pitch) = LookAt(position, target);
                var editorFieldOfView = ViewCamera.FieldOfViewDegrees;
                ViewCamera.Position = position;
                ViewCamera.SetOrientation(yaw, pitch);
                ViewCamera.FieldOfViewDegrees = fieldOfView;
                Step = $"{template}: let shadows, ambient occlusion and the sky reflection settle";
                yield return 60;

                foreach (var density in TemplatePreviews.Densities)
                {
                    var width = TemplatePreviews.Width * density;
                    var height = TemplatePreviews.Height * density;
                    var captured = Renderer.TryCaptureViewportThumbnail(width, height, out var rgba);
                    Check($"{template} @{density}x captured ({width} x {height})", captured && rgba.Length == width * height * 4);
                    if (captured)
                    {
                        File.WriteAllBytes(Path.Combine(folder, TemplatePreviews.FileName(template, density)), PngEncoder.EncodeRgba(rgba, width, height));
                    }

                    yield return 2;
                }

                ViewCamera.FieldOfViewDegrees = editorFieldOfView;
                _records[template.ToString()] = new TemplatePreviewRecord(
                    TemplatePreviews.ContentFingerprint(Workspace.Parts.ToList()),
                    TemplatePreviews.EnvironmentFingerprint(Environment.Boxes),
                    [position.X, position.Y, position.Z],
                    yaw,
                    pitch,
                    fieldOfView);

                foreach (var wait in LeaveToHub($"{template}: back to the Hub"))
                {
                    yield return wait;
                }

                Check($"{template}: left untouched, straight back to the Hub", Screen == "Hub" && !ConfirmOpen, Screen);
            }

            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }

        private static (float X, float Y) Center(UiBox box) => (box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f);
    }
}

/// <summary>The renderer's scene environment (the baseplate the host draws), as Play Mode gets it.</summary>
internal static class PlayModeControllerAccess
{
    public static PhysicsEnvironment Environment(Renderer renderer)
    {
        var playMode = typeof(Renderer).GetField("_playMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(renderer)!;
        return (PhysicsEnvironment)playMode.GetType().GetProperty("PhysicsEnvironment")!.GetValue(playMode)!;
    }
}
