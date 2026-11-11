using Wisko.Engine.Assets;
using Wisko.Engine.Platform;

namespace Wisko.Tests.Checks;

/// <summary>
/// Windows: model import (Assimp64.dll) and file dialogs (nfd.dll) need Microsoft's Visual C++ runtime 14.44 or later.
/// Wisko detects it, explains what is missing and offers Microsoft's download link; it never crashes over it and never
/// downloads anything itself. A missing or outdated runtime is simulated (VisualCppRuntime.OverrideForTests).
/// </summary>
internal static class VisualCppRuntimeChecks
{
    private static readonly VisualCppRuntimeStatus Missing = new(VisualCppRuntimeState.Missing, null, null);
    private static readonly VisualCppRuntimeStatus Outdated = new(VisualCppRuntimeState.Outdated, new Version(14, 29, 30133), @"C:\Windows\System32\msvcp140.dll");
    private static readonly VisualCppRuntimeStatus Available = new(VisualCppRuntimeState.Available, new Version(14, 44, 35211), @"C:\Windows\System32\msvcp140.dll");

    public static void Run(TestReport report, string dir)
    {
        try
        {
            VisualCppRuntime.OverrideForTests = null;
            VisualCppRuntime.NoticeForTests = null;
            var detected = VisualCppRuntime.Current;
            TestReport.Info($"This computer: {detected.State}, {VisualCppRuntime.LogLine(detected)}");
            if (!OperatingSystem.IsWindows())
            {
                report.Check("Off Windows the Visual C++ runtime is not needed and nothing is reported",
                    detected.State == VisualCppRuntimeState.NotNeeded && VisualCppRuntime.Problem is null, detected.State.ToString());
            }

            report.Check("Version rule: none is missing, 14.43 is too old, 14.44 and newer are enough",
                VisualCppRuntime.Classify(null) == VisualCppRuntimeState.Missing
                && VisualCppRuntime.Classify(new Version(14, 43, 34808)) == VisualCppRuntimeState.Outdated
                && VisualCppRuntime.Classify(new Version(14, 44, 35211)) == VisualCppRuntimeState.Available
                && VisualCppRuntime.Classify(new Version(14, 51, 36247)) == VisualCppRuntimeState.Available);

            report.Check("Missing runtime: model import and file dialogs are blocked; outdated: only model import; enough: neither",
                Blocked(Missing) == (true, true) && Blocked(Outdated) == (false, true) && Blocked(Available) == (false, false),
                $"{Blocked(Missing)} {Blocked(Outdated)} {Blocked(Available)}");

            VisualCppRuntime.OverrideForTests = Missing;
            var notices = new List<string>();
            VisualCppRuntime.NoticeForTests = notices.Add;
            report.Check("Missing runtime: the explanation names the Microsoft Visual C++ Redistributable (x64) and what needs it",
                VisualCppRuntime.Problem == "Model import and file dialogs need the Microsoft Visual C++ Redistributable (x64), which is not installed on this computer"
                && VisualCppRuntime.ShortProblem == "needs the Microsoft Visual C++ Redistributable (x64)", VisualCppRuntime.Problem);
            report.Check("Missing runtime: the startup log line gives Microsoft's official download link",
                VisualCppRuntime.LogLine(VisualCppRuntime.Current).EndsWith("Microsoft's download: https://aka.ms/vc14/vc_redist.x64.exe", StringComparison.Ordinal),
                VisualCppRuntime.LogLine(VisualCppRuntime.Current));
            VisualCppRuntime.OfferDownload();
            report.Check("Missing runtime: the notice offers Microsoft's link and says nothing is downloaded or installed without a choice",
                notices.Count == 1 && notices[0].Contains(VisualCppRuntime.DownloadUrl, StringComparison.Ordinal)
                && notices[0].Contains("Nothing is downloaded or installed unless you choose to", StringComparison.Ordinal), notices.FirstOrDefault());

            var started = System.Diagnostics.Stopwatch.StartNew();
            var picked = NativeFilePicker.TryPickFile([new FileTypeFilter("Models", ["glb"])], out var path, out var pickError);
            report.Check("Missing runtime: a file dialog is not attempted; the failure says why (no crash, no hang)",
                picked == FilePickResult.Failed && path is null && pickError == VisualCppRuntime.ShortProblem && started.ElapsedMilliseconds < 1000,
                $"{picked}: {pickError} ({started.ElapsedMilliseconds} ms)");

            var project = Path.Combine(dir, "Project");
            Directory.CreateDirectory(Path.Combine(project, "Assets", "Models", "Box"));
            File.Copy(AnimationChecks.ModelFile("Box", "Box.glb"), Path.Combine(project, "Assets", "Models", "Box", "Box.glb"), overwrite: true);
            using (var cache = new ModelAssetCache())
            {
                cache.SetProject(project);
                var asset = cache.Load("Assets/Models/Box/Box.glb", out var error);
                report.Check("Missing runtime: a project's model loads as null with the reason (Assimp is not loaded; the scene keeps a placeholder)",
                    asset is null && error == "model import needs the Microsoft Visual C++ Redistributable (x64)", error);
            }

            VisualCppRuntime.OverrideForTests = Outdated;
            report.Check("Outdated runtime: the explanation gives the required and the installed version",
                VisualCppRuntime.Problem is { } outdated && outdated.Contains("14.44 or later", StringComparison.Ordinal)
                && outdated.Contains("14.29.30133", StringComparison.Ordinal), VisualCppRuntime.Problem);

            VisualCppRuntime.OverrideForTests = Available;
            notices.Clear();
            VisualCppRuntime.OfferDownload();
            using (var cache = new ModelAssetCache())
            {
                cache.SetProject(project);
                var asset = cache.Load("Assets/Models/Box/Box.glb", out var error);
                report.Check("Runtime present: no notice, and the same model loads",
                    notices.Count == 0 && VisualCppRuntime.Problem is null && asset is not null, error);
            }
        }
        finally
        {
            VisualCppRuntime.OverrideForTests = null;
            VisualCppRuntime.NoticeForTests = null;
        }
    }

    private static (bool FileDialogs, bool ModelImport) Blocked(VisualCppRuntimeStatus status)
    {
        var previous = VisualCppRuntime.OverrideForTests;
        VisualCppRuntime.OverrideForTests = status;
        try
        {
            return (VisualCppRuntime.FileDialogsBlocked, VisualCppRuntime.ModelImportBlocked);
        }
        finally
        {
            VisualCppRuntime.OverrideForTests = previous;
        }
    }
}
