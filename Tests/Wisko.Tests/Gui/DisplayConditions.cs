using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wisko.Tests.Gui;

/// <summary>
/// Whether this desktop can give the GUI suite meaningful frame timings. The suite keeps the display awake while it runs
/// (SetThreadExecutionState, as a video player does; no input is simulated and no setting changes), and before it
/// starts it checks that the compositor is composing at a real display rate, that the session is not locked and that
/// it is not a remote session. A display that is already off cannot be woken this way: then the suite says so plainly,
/// instead of failing the frame-pacing checks with a misleading frame rate.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed record DisplayConditions(double CompositorHz, bool Locked, bool Remote)
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    public bool Suitable => CompositorHz is >= 20 and <= 500 && !Locked && !Remote;

    public string Problem => Locked ? "the session is locked"
        : Remote ? "this is a remote desktop session (frame pacing is measured on a local display)"
        : CompositorHz < 20 ? FormattableString.Invariant($"the compositor composes at {CompositorHz:F1} Hz: the display is off or asleep (wake it and run again)")
        : CompositorHz > 500 ? "the compositor is not composing (DwmFlush returns at once)"
        : string.Empty;

    public override string ToString() => FormattableString.Invariant(
        $"display conditions: compositor {CompositorHz:F1} Hz, {(Locked ? "locked" : "unlocked")}, {(Remote ? "remote" : "local")} session");

    /// <summary>Keeps the display and the computer awake until disposed (this thread's execution state).</summary>
    public static IDisposable KeepAwake()
    {
        SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        return new Release();
    }

    /// <summary>30 compositor waits timed, the lock state and the session type.</summary>
    public static DisplayConditions Inspect()
    {
        const int frames = 30;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            DwmFlush();
        }

        var hz = frames / clock.Elapsed.TotalSeconds;
        var desktop = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
        var locked = desktop == IntPtr.Zero;
        if (!locked)
        {
            CloseDesktop(desktop);
        }

        return new DisplayConditions(hz, locked, GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0);
    }

    private sealed class Release : IDisposable
    {
        public void Dispose() => SetThreadExecutionState(EsContinuous);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
