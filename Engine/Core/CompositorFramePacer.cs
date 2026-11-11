using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wisko.Engine.Core;

/// <summary>
/// Windows OpenGL frame pacing (see <see cref="EngineWindow"/>): after each present the window thread waits in the
/// kernel for the desktop compositor's next frame (DwmFlush), so the editor runs at the display's refresh rate. The
/// driver's own vsync (swap interval 1) is not used: NVIDIA's OpenGL busy-waits for it inside SwapBuffers, keeping a
/// whole CPU core busy at 60 fps.
/// A frame the compositor did not pace (DwmFlush failed, or returned without waiting, e.g. while nothing is being
/// composed) is followed by a short sleep once that happens twice in a row, so the loop can never spin: at most about
/// 1000 / <see cref="UnpacedSleepMilliseconds"/> frames per second, the thread asleep most of the time. A single early
/// return is normal (a heavy frame can end just as a composition happens) and costs nothing. There is no permanent
/// fallback: every frame asks the compositor again, so pacing resumes by itself. Nothing here waits beyond DwmFlush
/// and that one sleep. The OS calls are passed in, so the rules can be checked without a compositor.
/// </summary>
internal sealed class CompositorFramePacer(Func<int> flush, Action<int> sleep, Func<long> timestamp, long timestampFrequency)
{
    /// <summary>The sleep after an unpaced frame (the second in a row and each one after it).</summary>
    public const int UnpacedSleepMilliseconds = 8;

    /// <summary>A compositor wait shorter than this did not wait for a composition.</summary>
    public static readonly TimeSpan MinimumPacedWait = TimeSpan.FromMilliseconds(0.2);

    private int _unpacedInARow;
    private bool _throttleLogged;
    private bool _resumeLogged;

    public long PacedFrames { get; private set; }

    public long ThrottledFrames { get; private set; }

    /// <summary>The longest single compositor wait so far (diagnostics: a stall would show here).</summary>
    public TimeSpan LongestWait { get; private set; }

    /// <summary>Unpaced frames are being slept on (two or more in a row).</summary>
    public bool IsThrottling => _unpacedInARow >= 2;

    [SupportedOSPlatform("windows")]
    public static CompositorFramePacer CreateForWindows() =>
        new(DwmFlush, Thread.Sleep, Stopwatch.GetTimestamp, Stopwatch.Frequency);

    /// <summary>Call right after the frame was presented (SwapBuffers).</summary>
    public void AfterPresent()
    {
        var start = timestamp();
        var result = flush();
        var waited = TimeSpan.FromSeconds((timestamp() - start) / (double)timestampFrequency);
        if (waited > LongestWait)
        {
            LongestWait = waited;
        }

        if (result >= 0 && waited >= MinimumPacedWait)
        {
            if (IsThrottling && !_resumeLogged)
            {
                _resumeLogged = true;
                Console.WriteLine("[Renderer] Frame pacing: the compositor is pacing frames again");
            }

            _unpacedInARow = 0;
            PacedFrames++;
            return;
        }

        _unpacedInARow++;
        if (!IsThrottling)
        {
            return;
        }

        if (!_throttleLogged)
        {
            _throttleLogged = true;
            Console.WriteLine(
                $"[Renderer] Frame pacing: the compositor is not pacing frames (DwmFlush 0x{result:X8} after {waited.TotalMilliseconds:F2} ms); " +
                $"sleeping {UnpacedSleepMilliseconds} ms per frame until it does");
        }

        ThrottledFrames++;
        sleep(UnpacedSleepMilliseconds);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
