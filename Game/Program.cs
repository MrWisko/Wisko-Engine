using System.Reflection;
using System.Runtime.InteropServices;
using Wisko.Engine.Core;
using Wisko.Engine.Rendering.Backend;

namespace Wisko.Editor;

internal static class Program
{
    private static int Main(string[] args)
    {
        AppLog.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Fatal(e.ExceptionObject as Exception);
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            AppLog.Fatal(ex);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (!RenderBackendArguments.TryParse(args, out var renderBackend, out var argumentError))
        {
            Console.Error.WriteLine($"[Startup] {argumentError}");
            return 2;
        }

        // The macOS app bundle runs the production bgfx/Metal renderer. Command-line runs keep the
        // existing contract (no argument = OpenGL), and an explicit --renderer always wins.
        var rendererSource = HasRendererArgument(args) ? "--renderer" : "default";
        if (rendererSource == "default" && IsRunningFromMacAppBundle())
        {
            renderBackend = RenderBackendKind.Bgfx;
            rendererSource = "macOS app bundle default";
        }

        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Console.WriteLine($"[Startup] Wisko Engine {version} on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"[Startup] App directory: {AppContext.BaseDirectory}");
        if (AppLog.FilePath is not null)
        {
            Console.WriteLine($"[Startup] Log file: {AppLog.FilePath}");
        }

        Console.WriteLine($"[Startup] Renderer: {renderBackend} ({rendererSource})");

        // Decode + verify the branding icon on every platform; only APPLY it where the windowing
        // backend supports it. GLFW cannot set a window icon on macOS (Cocoa), and OpenTK's default
        // error handler turns that into a fatal exception, so the icon is applied on Windows/Linux only.
        var windowIcon = BrandingIcon.Load();
        var applyWindowIcon = !OperatingSystem.IsMacOS();
        if (!applyWindowIcon)
        {
            Console.WriteLine(
                "[BrandingIcon] macOS: no window icon is set (GLFW cannot set one there); the Dock shows the app bundle's icon");
        }

        if (!TryGetProjectsFolder(args, out var projectsRoot, out var projectsError))
        {
            Console.Error.WriteLine($"[Startup] {projectsError}");
            return 2;
        }

        if (!TryGetMsaa(args, out var msaa, out var msaaError))
        {
            Console.Error.WriteLine($"[Startup] {msaaError}");
            return 2;
        }

        Console.WriteLine($"[Startup] Projects folder: {projectsRoot}");
        var settings = new EngineApplicationSettings
        {
            Title = "Wisko Engine",
            Width = 1280,
            Height = 720,
            Icon = applyWindowIcon ? windowIcon : null,
            ProjectsRootPath = projectsRoot,
            RenderBackend = renderBackend,
            BgfxMsaaSamples = msaa
        };

        // Both backends run the same editor; bgfx is refused (not silently swapped) on unsupported platforms.
        EngineApplication app;
        try
        {
            app = new EngineApplication(settings, new WiskoEditorModule());
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine($"[Startup] Cannot use --renderer=bgfx: {ex.Message}");
            return 3;
        }
        catch (DisplayUnavailableException ex)
        {
            Console.Error.WriteLine($"[Startup] {ex.Message}");
            return 5;
        }

        if (renderBackend == RenderBackendKind.Bgfx)
        {
            Console.WriteLine("[Renderer] Requested: Bgfx");
        }

        try
        {
            using (app)
            {
                app.Run();
            }
        }
        catch (Exception ex) when (renderBackend == RenderBackendKind.Bgfx
            && ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Startup] bgfx backend failed: {ex.Message}");
            return 4;
        }

        return 0;
    }

    // Where the Hub keeps projects: Documents\Wisko\Projects (in the user profile), or the folder given with
    // --projects <folder> / --projects=<folder> (portable use, tests). The History is kept next to it.
    private static bool TryGetProjectsFolder(string[] args, out string folder, out string? error)
    {
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Wisko", "Projects");
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? given = null;
            if (args[i].Equals("--projects", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "--projects needs a folder: --projects <folder>";
                    return false;
                }

                given = args[i + 1];
            }
            else if (args[i].StartsWith("--projects=", StringComparison.OrdinalIgnoreCase))
            {
                given = args[i]["--projects=".Length..];
            }

            if (given is null)
            {
                continue;
            }

            try
            {
                folder = Path.GetFullPath(given.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"--projects: '{given}' is not a usable folder ({ex.Message})";
                return false;
            }
        }

        return true;
    }

    // bgfx (the macOS app) multisampling: --msaa=0 (off), 2 or 4 (the default).
    private static bool TryGetMsaa(string[] args, out int samples, out string? error)
    {
        samples = 4;
        error = null;
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--msaa=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(arg["--msaa=".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out samples)
                || samples is not (0 or 2 or 4))
            {
                error = $"Unknown {arg}. Use --msaa=0 (off), --msaa=2 or --msaa=4.";
                return false;
            }
        }

        return true;
    }

    private static bool HasRendererArgument(string[] args) =>
        args.Any(arg => arg.StartsWith("--renderer", StringComparison.OrdinalIgnoreCase));

    // ".../Wisko Engine.app/Contents/MacOS/" (the published executable inside the bundle).
    private static bool IsRunningFromMacAppBundle() =>
        OperatingSystem.IsMacOS()
        && Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).EndsWith(".app/Contents/MacOS", StringComparison.Ordinal);
}
