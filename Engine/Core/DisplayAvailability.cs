using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Wisko.Engine.Core;

/// <summary>There is no display to open the editor window on (it is asleep, or none is connected).</summary>
public sealed class DisplayUnavailableException(string message) : Exception(message);

/// <summary>
/// Before the editor window exists: is there a display to open it on? With the display asleep, or none connected, GLFW
/// reports no monitor at all, and creating the window then crashes natively (OpenTK's window asks GLFW for the primary
/// monitor's video mode), with nothing in the log. So the start waits a bounded time for a display, sleeping between
/// checks (never spinning) and letting GLFW hear about displays that come back, and otherwise stops with a message.
/// Nothing here changes the display's power state.
/// </summary>
internal static class DisplayAvailability
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    public const string Problem = "No display is available (it may be asleep, or none is connected). Wake or connect a display, then start Wisko again.";

    /// <summary>Returns when a display is available; throws <see cref="DisplayUnavailableException"/> when none came within the timeout.</summary>
    public static void EnsureDisplay()
    {
        var polled = false;
        bool HasDisplay()
        {
            // The first check sees what GLFW found at initialisation; later ones first process the system's display
            // notifications, so a display that woke up in the meantime is seen.
            if (polled)
            {
                GLFW.PollEvents();
            }

            polled = true;
            return GlfwHasPrimaryMonitor();
        }

        if (!WaitForDisplay(HasDisplay, DefaultTimeout, DefaultInterval, Thread.Sleep, Console.WriteLine, out _))
        {
            throw new DisplayUnavailableException(Problem);
        }
    }

    /// <summary>
    /// Checks <paramref name="hasDisplay"/> now and then after each <paramref name="interval"/> of <paramref name="sleep"/>
    /// until <paramref name="timeout"/> has been slept in all. Logs one line when it starts waiting and one when it ends.
    /// </summary>
    internal static bool WaitForDisplay(Func<bool> hasDisplay, TimeSpan timeout, TimeSpan interval, Action<TimeSpan> sleep, Action<string> log, out int checks)
    {
        checks = 1;
        if (hasDisplay())
        {
            return true;
        }

        if (interval <= TimeSpan.Zero)
        {
            interval = DefaultInterval;
        }

        log(FormattableString.Invariant($"[Startup] No display is available (asleep or disconnected); waiting up to {timeout.TotalSeconds:0.#} s"));
        var waited = TimeSpan.Zero;
        while (waited < timeout)
        {
            var step = interval < timeout - waited ? interval : timeout - waited;
            sleep(step);
            waited += step;
            checks++;
            if (hasDisplay())
            {
                log(FormattableString.Invariant($"[Startup] A display is available after {waited.TotalSeconds:0.0} s"));
                return true;
            }
        }

        log(FormattableString.Invariant($"[Startup] Still no display after {timeout.TotalSeconds:0.#} s"));
        return false;
    }

    // GLFW initialised exactly as the window would initialise it; a null primary monitor means no display.
    private static unsafe bool GlfwHasPrimaryMonitor()
    {
        GLFWProvider.EnsureInitialized();
        return GLFW.GetPrimaryMonitor() != null;
    }
}
