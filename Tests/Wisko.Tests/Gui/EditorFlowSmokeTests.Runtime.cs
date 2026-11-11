using Wisko.Engine.Core;
using Wisko.Engine.Platform;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Tests.Checks;

namespace Wisko.Tests.Gui;

internal static partial class EditorFlowSmokeTests
{
    private static void RunRuntimeNotice(Action<string, Action> section, string work, string? screenshotDir, TestReport report)
    {
        section("GUI: Windows without the Visual C++ runtime (simulated): explained once at start, Import says why, no crash, scripted input", () =>
        {
            var root = Path.Combine(work, "vc-runtime");
            CreateProject(Path.Combine(root, "Projects"), RuntimeNoticeFlow.ProjectName, [("Crate", new Engine.Math.Vector3(0f, 1.5f, 0f))]);
            var notices = new List<string>();
            VisualCppRuntime.OverrideForTests = new VisualCppRuntimeStatus(VisualCppRuntimeState.Missing, null, null);
            VisualCppRuntime.NoticeForTests = notices.Add;
            try
            {
                RunSession(report, root, "runtime notice flow", (app, window) => new RuntimeNoticeFlow(report, app, window, screenshotDir, notices), (_, _, _) => { });
            }
            finally
            {
                VisualCppRuntime.OverrideForTests = null;
                VisualCppRuntime.NoticeForTests = null;
            }
        });
    }

    // The real editor while VisualCppRuntime reports the runtime missing: the notice (a Windows message box in the app)
    // is captured instead of shown. The Import and model paths are the editor's own handlers, reached by reflection
    // because a real file dialog cannot be scripted; with the runtime missing they return before opening one.
    private sealed class RuntimeNoticeFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir, List<string> notices)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string ProjectName = "Runtime Test";

        private string? ToastText =>
            (string?)typeof(ToastRenderer).GetField("_message", Hidden)!.GetValue(typeof(Renderer).GetField("_toast", Hidden)!.GetValue(Renderer));

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            Check("Runtime notice: shown once, after the Hub's first frame, with Microsoft's download link",
                notices.Count == 1 && notices[0].Contains(VisualCppRuntime.DownloadUrl, StringComparison.Ordinal) && Screen == "Hub",
                $"{notices.Count} notice(s), screen {Screen}");

            foreach (var wait in OpenCard(ProjectName, "open the runtime test project"))
            {
                yield return wait;
            }

            Step = "Import while the runtime is missing";
            typeof(EngineApplication).GetMethod("PickAndImportModel", Hidden)!.Invoke(App, null);
            yield return 5;
            Check("Runtime notice: Import names the missing runtime in a toast and offers the download again (no dialog, no crash)",
                Screen == "Editor" && ToastText == "The file dialog needs the Microsoft Visual C++ Redistributable (x64)" && notices.Count == 2,
                $"toast \"{ToastText}\", {notices.Count} notice(s)");
            foreach (var wait in Shot("vc-runtime-missing-import"))
            {
                yield return wait;
            }

            Step = "import a model file while the runtime is missing";
            var parts = PartCount;
            typeof(EngineApplication).GetMethod("ImportModelFile", Hidden)!.Invoke(App, [AnimationChecks.ModelFile("Box", "Box.glb")]);
            yield return 5;
            Check("Runtime notice: importing a model file says model import needs the runtime; nothing is added",
                ToastText == "Import failed: model import needs the Microsoft Visual C++ Redistributable (x64)" && PartCount == parts && !App.HasUnsavedChanges,
                $"toast \"{ToastText}\", parts {parts} -> {PartCount}");

            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }
}
