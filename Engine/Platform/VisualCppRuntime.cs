using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisko.Engine.Platform;

internal enum VisualCppRuntimeState
{
    NotNeeded,
    Available,
    Missing,
    Outdated,
    Unknown
}

/// <summary>What <see cref="VisualCppRuntime"/> found: the lowest version of its DLLs and where that one came from.</summary>
internal readonly record struct VisualCppRuntimeStatus(VisualCppRuntimeState State, Version? Version, string? Path);

/// <summary>
/// The Microsoft Visual C++ runtime that two of the Windows app's native libraries need: Assimp64.dll (model import:
/// MSVCP140, VCRUNTIME140, VCRUNTIME140_1) and nfd.dll (file dialogs: VCRUNTIME140). Both are built with MSVC 14.44,
/// and Microsoft requires the installed runtime to be at least as new as the build tools. It is not part of Windows.
/// Wisko only detects it, says what is missing and, if the person chooses so, opens Microsoft's official download link
/// in the browser; it never downloads or installs anything itself. Everything else in Wisko works without it.
/// See packaging/windows/NATIVE-LIBRARIES.md.
/// </summary>
internal static class VisualCppRuntime
{
    /// <summary>Microsoft's permanent link to the latest supported Visual C++ v14 Redistributable for x64.</summary>
    public const string DownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";

    /// <summary>The MSVC version Assimp64.dll and nfd.dll were built with: the runtime must be at least as new.</summary>
    public static readonly Version Required = new(14, 44);

    private const string Name = "the Microsoft Visual C++ Redistributable (x64)";
    private static readonly string[] RuntimeFiles = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];
    private static VisualCppRuntimeStatus? _detected;

    /// <summary>Tests replace detection with a fixed result.</summary>
    internal static VisualCppRuntimeStatus? OverrideForTests { get; set; }

    /// <summary>Tests receive the explanation instead of a Windows message box.</summary>
    internal static Action<string>? NoticeForTests { get; set; }

    public static VisualCppRuntimeStatus Current => OverrideForTests ?? (_detected ??= Detect());

    /// <summary>nfd.dll needs only VCRUNTIME140, so file dialogs stop working only when the runtime is missing.</summary>
    public static bool FileDialogsBlocked => Current.State == VisualCppRuntimeState.Missing;

    /// <summary>
    /// Assimp64.dll needs MSVCP140 at version 14.44 or later; with an older one it may load and then fail inside native
    /// code, where nothing can catch it, so model import is not attempted then either.
    /// </summary>
    public static bool ModelImportBlocked => Current.State is VisualCppRuntimeState.Missing or VisualCppRuntimeState.Outdated;

    /// <summary>Full explanation for the log and the message box, or null when nothing is missing.</summary>
    public static string? Problem => Describe(Current);

    /// <summary>One line for a toast or an error message, or null when nothing is missing.</summary>
    public static string? ShortProblem => Current.State switch
    {
        VisualCppRuntimeState.Missing => $"needs {Name}",
        VisualCppRuntimeState.Outdated => $"needs {Name} {Required} or later",
        _ => null
    };

    internal static VisualCppRuntimeState Classify(Version? version) =>
        version is null ? VisualCppRuntimeState.Missing
        : version >= Required ? VisualCppRuntimeState.Available
        : VisualCppRuntimeState.Outdated;

    internal static string? Describe(VisualCppRuntimeStatus status) => status.State switch
    {
        VisualCppRuntimeState.Missing => $"Model import and file dialogs need {Name}, which is not installed on this computer",
        VisualCppRuntimeState.Outdated =>
            $"Model import needs {Name} {Required} or later; this computer has {status.Version} ({status.Path})",
        _ => null
    };

    /// <summary>One line for the startup log.</summary>
    internal static string LogLine(VisualCppRuntimeStatus status) => status.State switch
    {
        VisualCppRuntimeState.NotNeeded => "not needed on this platform",
        VisualCppRuntimeState.Available => $"{status.Version} ({status.Path})",
        VisualCppRuntimeState.Unknown => "could not be checked",
        _ => $"{Describe(status)}. Microsoft's download: {DownloadUrl}"
    };

    /// <summary>The message box text for <see cref="Problem"/>.</summary>
    internal static string NoticeText(string problem) =>
        $"{problem}.\n\nEverything else in Wisko works without it. Microsoft's official installer for it is:\n{DownloadUrl}\n\n"
        + "Open this link in your browser now? (Nothing is downloaded or installed unless you choose to.)";

    /// <summary>
    /// When something is missing: explains it in a Windows message box and, only if the person answers Yes, opens
    /// Microsoft's download link in their browser. Does nothing when nothing is missing.
    /// </summary>
    public static void OfferDownload()
    {
        if (Problem is not { } problem)
        {
            return;
        }

        var text = NoticeText(problem);
        if (NoticeForTests is { } notice)
        {
            notice(text);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const uint YesNo = 0x4, Warning = 0x30, TaskModal = 0x2000, SetForeground = 0x10000;
        const int Yes = 6;
        if (MessageBoxW(IntPtr.Zero, text, "Wisko: Microsoft Visual C++ Redistributable", YesNo | Warning | TaskModal | SetForeground) != Yes)
        {
            Console.WriteLine("[Runtime] Microsoft's download link was not opened");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            Console.WriteLine($"[Runtime] Opened {DownloadUrl} in the browser");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[Runtime] Could not open {DownloadUrl}: {ex.Message}");
        }
    }

    // Loads each runtime DLL the way Assimp64.dll and nfd.dll would find it (the app folder, then Windows' folders),
    // reads its file version and releases it again.
    private static VisualCppRuntimeStatus Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VisualCppRuntimeStatus(VisualCppRuntimeState.NotNeeded, null, null);
        }

        try
        {
            Version? lowest = null;
            string? lowestPath = null;
            foreach (var file in RuntimeFiles)
            {
                if (!NativeLibrary.TryLoad(file, out var handle))
                {
                    return new VisualCppRuntimeStatus(VisualCppRuntimeState.Missing, null, null);
                }

                try
                {
                    var path = ModulePath(handle);
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
                    if (lowest is null || version < lowest)
                    {
                        lowest = version;
                        lowestPath = path;
                    }
                }
                finally
                {
                    NativeLibrary.Free(handle);
                }
            }

            return new VisualCppRuntimeStatus(Classify(lowest), lowest, lowestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"[Runtime] Visual C++ runtime check failed: {ex.Message}");
            return new VisualCppRuntimeStatus(VisualCppRuntimeState.Unknown, null, null);
        }
    }

    private static string ModulePath(IntPtr module)
    {
        var buffer = new char[2048];
        var length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
        {
            throw new IOException($"GetModuleFileName failed (error {Marshal.GetLastPInvokeError()})");
        }

        return new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] fileName, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
