using System.Diagnostics;
using Wisko.Engine.Core;

namespace Wisko.Tests.Checks;

/// <summary>
/// CompositorFramePacer (Windows OpenGL frame pacing). A simulated clock and compositor drive the real pacer through
/// normal pacing and every failure shape, so the guarantees are checked on any OS: the loop never spins, a frame never
/// waits longer than one compositor wait plus one short sleep, failures never cause retries or log floods, and pacing
/// resumes by itself. On Windows the real DwmFlush is also checked.
/// </summary>
internal static class FramePacingChecks
{
    private const int EFail = unchecked((int)0x80004005);
    private const double FrameWorkMs = 0.3; // update + render + present of a light editor frame

    public static void Run(TestReport report)
    {
        var sleep = CompositorFramePacer.UnpacedSleepMilliseconds;

        var normal = Simulate(600, _ => (0, 16.0));
        report.Check("Normal pacing: every frame waits for the compositor, no extra sleep",
            normal.Sleeps == 0 && normal.Pacer.PacedFrames == 600 && !normal.Pacer.IsThrottling, normal.ToString());

        var heavy = Simulate(600, frame => frame % 10 == 5 ? (0, 0.05) : (0, 16.0));
        report.Check("A single early return (a heavy frame ending at a composition) costs no sleep", heavy.Sleeps == 0, heavy.ToString());

        foreach (var (name, behaviour) in new (string, Func<int, (int, double)>)[]
                 {
                     ("DwmFlush failing every frame", _ => (EFail, 0.0)),
                     ("DwmFlush succeeding without waiting (nothing composed)", _ => (0, 0.0)),
                 })
        {
            var run = Simulate(1000, behaviour);
            report.Check($"{name}: one compositor call per frame (no retry loop)", run.Flushes == 1000, run.ToString());
            report.Check($"{name}: throttled from the second frame, one {sleep} ms sleep per frame",
                run.Sleeps == 999 && run.Pacer.ThrottledFrames == 999 && run.Pacer.IsThrottling, run.ToString());
            report.Check($"{name}: no stall (the pacer never adds more than {sleep} ms to a frame)", run.LongestPacerMs <= sleep + 1e-9, run.ToString());
            report.Check($"{name}: frame rate bounded (<= {1000.0 / sleep:F0} fps) and the thread mostly asleep (busy <= 5%)",
                run.Fps <= 1000.0 / sleep && run.BusyFraction <= 0.05, run.ToString());
        }

        var recovery = Simulate(100, frame => frame < 50 ? (EFail, 0.0) : (0, 16.0));
        report.Check("Recovery: pacing resumes on the first paced frame, no permanent fallback",
            recovery.Sleeps == 49 && recovery.Pacer.PacedFrames == 50 && !recovery.Pacer.IsThrottling && recovery.Flushes == 100, recovery.ToString());

        var flapping = Simulate(10_000, frame => frame % 3 == 2 ? (0, 16.0) : (EFail, 0.0), captureLog: true);
        report.Check("Logging is bounded: 10,000 frames flapping between failed and paced log at most 2 lines",
            flapping.LogLines <= 2, $"{flapping.LogLines} lines");

        var longWait = Simulate(3, frame => frame == 1 ? (0, 250.0) : (0, 16.0));
        report.Check("Diagnostics: a long compositor wait shows in LongestWait",
            Math.Abs(longWait.Pacer.LongestWait.TotalMilliseconds - 250.0) < 0.01, $"{longWait.Pacer.LongestWait.TotalMilliseconds:F2} ms");

        if (!OperatingSystem.IsWindows())
        {
            TestReport.Info("Real DwmFlush check skipped (not Windows)");
            return;
        }

        // The real compositor, measured in a fresh process on the pacing thread itself: the CPU time of that thread is
        // what busy-waiting would raise, and nothing else the test process did before (or does beside it) is counted.
        var real = RunProbe(ProbeMode.Real);
        TestReport.Info(real?.ToString() ?? "real DwmFlush: the probe did not report");
        report.Check("Real DwmFlush (Windows): frame rate bounded and CPU low (<= 500 fps, <= 50% of a core on the pacing thread)",
            real is not null && real.Passes, real?.Verdict ?? "no result");

        // The check still catches what it is for: a pacer that busy-waits for each frame (as NVIDIA's OpenGL vsync does).
        var busy = RunProbe(ProbeMode.BusyWait);
        TestReport.Info(busy?.ToString() ?? "busy-waiting pacer: the probe did not report");
        report.Check("Real DwmFlush check, regression: a busy-waiting pacer fails it (pacing thread above 50% of a core)",
            busy is not null && !busy.Passes && busy.ThreadCpu > 0.5, busy?.Verdict ?? "no result");

        // ...and is not fooled by unrelated work: real pacing beside a thread that keeps a whole core busy still passes,
        // while the whole process's CPU (the old measure) is high.
        var loaded = RunProbe(ProbeMode.BackgroundLoad);
        TestReport.Info(loaded?.ToString() ?? "background load: the probe did not report");
        report.Check("Real DwmFlush check, regression: a busy background thread does not fail it (pacing thread low, process CPU high)",
            loaded is not null && loaded.Passes && loaded.ProcessCpu > 0.5, loaded?.Verdict ?? "no result");
    }

    internal enum ProbeMode
    {
        Real,
        BusyWait,
        BackgroundLoad
    }

    /// <summary>What one probe process measured over 60 frames (after 5 warm-up frames).</summary>
    internal sealed record ProbeResult(ProbeMode Mode, double Fps, double ThreadCpu, double ProcessCpu, long Paced, long Throttled, double LongestWaitMs)
    {
        public const double MaxFps = 500;
        public const double MaxThreadCpu = 0.5;

        public bool Passes => Fps <= MaxFps && ThreadCpu <= MaxThreadCpu;

        public string Verdict => FormattableString.Invariant($"{Fps:F0} fps, pacing thread {ThreadCpu * 100:F1}% of a core");

        public override string ToString() => FormattableString.Invariant(
            $"{Mode} probe: {Fps:F0} frames/s, pacing thread CPU {ThreadCpu * 100:F1}% of a core, whole process {ProcessCpu * 100:F1}% (diagnostic), paced {Paced}, throttled {Throttled}, longest wait {LongestWaitMs:F1} ms");
    }

    private static ProbeResult? RunProbe(ProbeMode mode)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var start = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(FramePacingChecks).Assembly.Location);
        }

        start.ArgumentList.Add("--frame-pacing-probe");
        start.ArgumentList.Add(mode.ToString());
        using var probe = Process.Start(start);
        if (probe is null)
        {
            return null;
        }

        var output = probe.StandardOutput.ReadToEnd();
        probe.WaitForExit();
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("PROBE ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line[6..].Split(' ').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);
        double Number(string key) => double.Parse(values[key], System.Globalization.CultureInfo.InvariantCulture);
        return new ProbeResult(mode, Number("fps"), Number("thread"), Number("process"), (long)Number("paced"), (long)Number("throttled"), Number("longest"));
    }

    /// <summary>
    /// The probe process (Wisko.Tests --frame-pacing-probe Real|BusyWait|BackgroundLoad): 5 warm-up frames, then 60
    /// frames timed, with the pacing thread's own CPU time (GetThreadTimes) and the whole process's beside it.
    /// </summary>
    public static int RunProbeProcess(string modeName)
    {
        if (!OperatingSystem.IsWindows() || !Enum.TryParse<ProbeMode>(modeName, out var mode))
        {
            Console.Error.WriteLine("--frame-pacing-probe needs Windows and Real, BusyWait or BackgroundLoad");
            return 2;
        }

        // The busy-waiting stand-in spins until the next 60 Hz boundary instead of waiting in the kernel.
        var interval = Stopwatch.Frequency / 60;
        var pacer = mode == ProbeMode.BusyWait
            ? new CompositorFramePacer(() =>
            {
                var next = (Stopwatch.GetTimestamp() / interval + 1) * interval;
                while (Stopwatch.GetTimestamp() < next)
                {
                }

                return 0;
            }, Thread.Sleep, Stopwatch.GetTimestamp, Stopwatch.Frequency)
            : CompositorFramePacer.CreateForWindows();

        using var stop = new CancellationTokenSource();
        var background = mode == ProbeMode.BackgroundLoad
            ? new Thread(() =>
            {
                var x = 0L;
                while (!stop.IsCancellationRequested)
                {
                    x++;
                }

                GC.KeepAlive(x);
            }) { IsBackground = true }
            : null;
        background?.Start();

        for (var i = 0; i < 5; i++)
        {
            pacer.AfterPresent();
        }

        const int frames = 60;
        var process = Process.GetCurrentProcess();
        var processStart = process.TotalProcessorTime;
        var threadStart = ThreadCpu();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            pacer.AfterPresent();
        }

        clock.Stop();
        var thread = ThreadCpu() - threadStart;
        process.Refresh();
        var processCpu = (process.TotalProcessorTime - processStart).TotalSeconds;
        stop.Cancel();
        background?.Join();

        var seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine(FormattableString.Invariant(
            $"PROBE fps={frames / seconds:F2} thread={thread.TotalSeconds / seconds:F4} process={processCpu / seconds:F4} paced={pacer.PacedFrames} throttled={pacer.ThrottledFrames} longest={pacer.LongestWait.TotalMilliseconds:F2}"));
        return 0;
    }

    // Kernel + user time of the calling thread.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static TimeSpan ThreadCpu()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return TimeSpan.FromTicks(kernel + user);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    private static SimulationResult Simulate(int frames, Func<int, (int Result, double WaitMs)> compositor, bool captureLog = false)
    {
        const long frequency = 10_000_000;
        long now = 0;
        var flushes = 0;
        var sleeps = 0;
        void Advance(double ms) => now += (long)Math.Round(ms * frequency / 1000.0);

        var pacer = new CompositorFramePacer(
            () =>
            {
                var (result, waitMs) = compositor(flushes++);
                Advance(waitMs);
                return result;
            },
            ms =>
            {
                sleeps++;
                Advance(ms);
            },
            () => now,
            frequency);

        var originalOut = Console.Out;
        var log = new StringWriter();
        if (captureLog)
        {
            Console.SetOut(log);
        }

        var longestPacerMs = 0.0;
        try
        {
            for (var i = 0; i < frames; i++)
            {
                Advance(FrameWorkMs);
                var before = now;
                pacer.AfterPresent();
                longestPacerMs = Math.Max(longestPacerMs, (now - before) * 1000.0 / frequency);
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var totalSeconds = now / (double)frequency;
        var logLines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new SimulationResult(pacer, flushes, sleeps, frames / totalSeconds, frames * FrameWorkMs / 1000.0 / totalSeconds, longestPacerMs, logLines);
    }

    private sealed record SimulationResult(
        CompositorFramePacer Pacer, int Flushes, int Sleeps, double Fps, double BusyFraction, double LongestPacerMs, int LogLines)
    {
        public override string ToString() =>
            $"{Flushes} flushes, {Sleeps} sleeps, {Fps:F0} fps, busy {BusyFraction * 100:F1}%, longest pacer time {LongestPacerMs:F2} ms";
    }
}
