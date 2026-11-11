using System.Diagnostics;
using System.Globalization;
using Wisko.Engine.Assets;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Projects;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// The scripts' Lua memory limit (<see cref="LuaMemoryLimit"/>): runaway tables, strings, single huge allocations,
/// coroutines and caught errors are stopped as a script fault with nothing else affected; the memory comes back;
/// creating and disposing many sessions leaks nothing; the accounting matches Lua's own count; the preference file is
/// read safely; the default is measured against every demo and showcase; and the accounting's cost is measured.
/// </summary>
internal static class LuaMemoryChecks
{
    private const long Mb = 1L << 20;

    private static LuaScriptRuntime Runtime(long? limit, bool accounting = true) =>
        new(new LuaScriptRuntimeOptions { MemoryLimitBytes = limit, MemoryAccounting = accounting, Log = _ => { } });

    private static string Megabytes(long bytes) => (bytes / (double)Mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    public static void Enforcement(TestReport report)
    {
        using var lua = Runtime(64 * Mb);
        var baseline = lua.LuaMemoryAccountedBytes;
        report.Check("Lua memory: the accounting matches Lua's own count of its heap (within 4 KB)",
            Math.Abs(lua.LuaMemoryAccountedBytes - lua.LuaMemoryBytes) <= 4096, $"{lua.LuaMemoryAccountedBytes} vs {lua.LuaMemoryBytes}");

        // A legitimate script well inside the limit.
        var legit = lua.LoadString("""
            local t = {}
            for i = 1, 200000 do t[i] = "item " .. i end
            total = #t
            t = nil
            """, "Legit.lua");
        report.Check("Lua memory: a script using about 15 MB under a 64 MB limit runs normally",
            legit.State == ScriptInstanceState.Loaded && legit.Fault is null, legit.Fault?.ToString());

        // Runaway table growth.
        var watch = Stopwatch.StartNew();
        var runaway = lua.LoadString("local t = {} local i = 0 while true do i = i + 1 t[i] = { i } end", "Runaway.lua");
        watch.Stop();
        report.Check("Lua memory: a table growing without end is stopped as a script fault (MemoryLimitExceeded)",
            runaway is { State: ScriptInstanceState.Faulted, Fault.Kind: ScriptDiagnosticKind.MemoryLimitExceeded }, runaway.Fault?.ToString());
        report.Check("Lua memory: the fault says which limit was reached",
            runaway.Fault?.Message.Contains("limit of 64 MB", StringComparison.Ordinal) == true, runaway.Fault?.Message);
        TestReport.Info($"Lua memory: runaway table stopped at the 64 MB limit after {watch.Elapsed.TotalMilliseconds:F0} ms");
        report.Check("Lua memory: the stopped script's memory comes back (heap within 2 MB of where it started)",
            lua.LuaMemoryAccountedBytes - baseline < 2 * Mb, $"{Megabytes(baseline)} before, {Megabytes(lua.LuaMemoryAccountedBytes)} after");
        report.Check("Lua memory: the heap never went far past the limit (peak at most limit + 1 MB)",
            lua.LuaMemoryPeakBytes <= 65 * Mb, Megabytes(lua.LuaMemoryPeakBytes));

        // One huge allocation (a 200 MB string).
        var huge = lua.LoadString("""local s = string.rep("x", 200 * 1024 * 1024) done = #s""", "Huge.lua");
        report.Check("Lua memory: one 200 MB string under a 64 MB limit is refused (MemoryLimitExceeded), nothing allocated",
            huge.Fault?.Kind == ScriptDiagnosticKind.MemoryLimitExceeded && lua.LuaMemoryAccountedBytes - baseline < 2 * Mb, huge.Fault?.ToString());

        // A script that catches "not enough memory" is still stopped.
        var caught = lua.LoadString("""
            local ok = pcall(function() local t = {} for i = 1, 1e9 do t[i] = i end end)
            local x = 0
            for i = 1, 1e6 do x = x + i end
            reachedEnd = true
            """, "Caught.lua");
        report.Check("Lua memory: catching the error with pcall does not escape the limit (still stopped)",
            caught.Fault?.Kind == ScriptDiagnosticKind.MemoryLimitExceeded, caught.Fault?.ToString());

        // Inside a coroutine.
        var coroutine = lua.LoadString("""
            local co = coroutine.create(function() local t = {} for i = 1, 1e9 do t[i] = tostring(i) end end)
            coroutine.resume(co)
            local x = 0
            for i = 1, 1e6 do x = x + i end
            """, "Coroutine.lua");
        report.Check("Lua memory: runaway growth inside a coroutine is stopped too",
            coroutine.Fault?.Kind == ScriptDiagnosticKind.MemoryLimitExceeded, coroutine.Fault?.ToString());

        // In Update, after a clean start.
        var grower = lua.LoadString("""
            local keep = {}
            function Update()
              for i = 1, 100000 do keep[#keep + 1] = { i, i } end
            end
            """, "Grower.lua");
        lua.RunStart(grower);
        var updates = 0;
        while (grower.State != ScriptInstanceState.Faulted && updates < 1000)
        {
            lua.RunUpdate(grower);
            updates++;
        }

        report.Check("Lua memory: a script that grows a little every frame is stopped in Update when it reaches the limit",
            grower.Fault is { Kind: ScriptDiagnosticKind.MemoryLimitExceeded, Phase: ScriptPhase.Update }, $"{updates} updates; {grower.Fault}");

        // Everything else keeps working afterwards.
        var after = lua.LoadString("local t = {} for i = 1, 1000 do t[i] = i * 2 end sum = 0 for _, v in ipairs(t) do sum = sum + v end", "After.lua");
        report.Check("Lua memory: the runtime keeps running other scripts after every stop",
            after.State == ScriptInstanceState.Loaded && after.Fault is null && lua.LuaMemoryAccountedBytes - baseline < 3 * Mb, after.Fault?.ToString());
    }

    /// <summary>At the limit, the host keeps working: events and GUI calls deliver; the script's own allocation fails.</summary>
    public static void HostCallsAtTheLimit(TestReport report, string dir)
    {
        WriteScript(dir, "Hog.lua", """
            local hold = {}
            local received = 0
            function Start()
              Events.On("bulk", function(text) received = received + 1 hold[#hold + 1] = text .. text end)
            end
            function Update()
              -- Fill most of the limit, then keep the host busy: events with large payloads, GUI elements, queries.
              for i = 1, 2000 do hold[#hold + 1] = string.rep("m", 8192) .. i end
              for i = 1, 200 do Events.Emit("bulk", string.rep("e", 4000)) end
              local frame = UI.Create("Frame", { Name = "F" .. #hold })
              local found = Scene.FindAllByTag("none")
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Hog", new Engine.Math.Vector3(0f, 1f, 0f), new Engine.Math.Vector3(1f, 1f, 1f), true, "Assets/Scripts/Hog.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = new PlayModeController { Output = log, GuiSource = new GuiDocument(), ScriptMemoryLimitBytes = 48 * Mb };
        if (!play.TryStart(dir, workspace, [], null, out var error))
        {
            report.Check("Lua memory: Play starts with a 48 MB limit", false, error);
            return;
        }

        faults.AddRange(play.TakeNewFaults());
        var frames = 0;
        while (faults.Count == 0 && frames < 600)
        {
            Step(play, 1, faults);
            frames++;
        }

        var limit = play.Runtime?.LuaMemoryLimitBytes ?? 0;
        report.Check("Lua memory: a script filling the limit while the host delivers events, makes GUI elements and answers queries "
            + "is stopped as a script fault (the editor carries on)",
            faults.Count == 1 && faults[0].Diagnostic.Kind == ScriptDiagnosticKind.MemoryLimitExceeded && limit == 48 * Mb,
            $"{frames} frames; {string.Join(" | ", faults.Select(f => f.Diagnostic.ToString()))}");
        Step(play, 30, faults);
        report.Check("Lua memory: Play keeps running after the stop (30 more frames, no new fault)", faults.Count == 1);
        play.Stop(workspace);
        play.Dispose();
    }

    public static void Lifecycle(TestReport report)
    {
        // Many sessions created and closed: Lua's native memory and the accounting block go with each.
        Collect();
        var before = Process.GetCurrentProcess().PrivateMemorySize64;
        for (var i = 0; i < 100; i++)
        {
            using var lua = Runtime(64 * Mb);
            lua.LoadString("data = {} for i = 1, 100000 do data[i] = { i } end", "Session.lua");
        }

        Collect();
        var growth = Process.GetCurrentProcess().PrivateMemorySize64 - before;
        report.Check("Lua memory: 100 sessions that each held about 8 MB leave the process no more than 32 MB bigger",
            growth < 32 * Mb, $"process private memory {(growth >= 0 ? "+" : string.Empty)}{Megabytes(growth)}");

        // Many scripts loaded and released in one session: the heap returns to where it was.
        using var shared = Runtime(256 * Mb);
        var baseline = shared.LuaMemoryAccountedBytes;
        var scripts = new List<LuaScriptInstance>();
        for (var i = 0; i < 300; i++)
        {
            scripts.Add(shared.LoadString("big = {} for i = 1, 8000 do big[i] = { i } end", $"S{i}.lua"));
        }

        var loaded = shared.LuaMemoryAccountedBytes;
        foreach (var script in scripts)
        {
            shared.Release(script);
        }

        shared.CollectGarbage();
        report.Check("Lua memory: 300 scripts loaded and released in one session: the heap returns to within 1 MB of where it started",
            loaded - baseline > 100 * Mb && shared.LuaMemoryAccountedBytes - baseline < Mb,
            $"{Megabytes(baseline)} -> {Megabytes(loaded)} loaded -> {Megabytes(shared.LuaMemoryAccountedBytes)} released");
    }

    public static void Preferences(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, EditorScriptPreferences.FileName);
        report.Check("Script preferences: no file gives the 256 MB default",
            EditorScriptPreferences.Load(path).MemoryLimitBytes == 256 * Mb);
        var saved = new EditorScriptPreferences { MemoryLimitMegabytes = 512 }.TrySave(path, out var error);
        report.Check("Script preferences: saved and read back (512 MB)", saved && EditorScriptPreferences.Load(path).MemoryLimitBytes == 512 * Mb, error);
        File.WriteAllText(path, "{ \"MemoryLimitMegabytes\": 1 }");
        var low = EditorScriptPreferences.Load(path).MemoryLimitBytes;
        File.WriteAllText(path, "{ \"MemoryLimitMegabytes\": 999999 }");
        var high = EditorScriptPreferences.Load(path).MemoryLimitBytes;
        report.Check("Script preferences: values are clamped to 32 MB .. 4 GB", low == 32 * Mb && high == 4096 * Mb, $"{low >> 20} / {high >> 20}");
        File.WriteAllText(path, "not json");
        var broken = EditorScriptPreferences.Load(path).MemoryLimitBytes;
        File.WriteAllText(path, "{ \"MemoryLimitMegabytes\": \"lots\" }");
        var mistyped = EditorScriptPreferences.Load(path).MemoryLimitBytes;
        report.Check("Script preferences: a broken or mistyped file falls back to the default", broken == 256 * Mb && mistyped == 256 * Mb);
    }

    /// <summary>
    /// The default against reality: every demo and showcase project played for 20 seconds, its Lua heap's peak
    /// recorded. The default must leave at least 8 times the largest.
    /// </summary>
    public static void DefaultAgainstDemos(TestReport report, string dir)
    {
        var projects = Path.Combine(dir, "Projects");
        var makers = new (string Name, Func<string, int> Make)[]
        {
            (LuaV4Demo.ProjectName, LuaV4Demo.Make),
            (LuaV5Demo.ProjectName, LuaV5Demo.Make),
            (AnimationDemo.ProjectName, AnimationDemo.Make),
            (GraphicsShowcase.ProjectName, GraphicsShowcase.Make),
            (ReleaseShowcase.ProjectName, ReleaseShowcase.Make),
        };

        var peaks = new List<(string Name, long Peak, int Faults, int MemoryFaults)>();
        foreach (var (name, make) in makers)
        {
            if (make(projects) != 0)
            {
                report.Check($"Lua memory default: the {name} project is made", false);
                continue;
            }

            var project = new ProjectStore(projects).ListProjects().First(p => p.Name == name);
            using var cache = new ModelAssetCache();
            cache.SetProject(project.FolderPath);
            SceneFile.TryLoadScene(project.ScenePath, assetPath => cache.Load(assetPath, out _), out var contents, out _);
            var workspace = new Workspace();
            foreach (var part in contents.Parts)
            {
                workspace.AddPart(part);
            }

            var log = new OutputLog();
            var faults = new List<PlayScriptFault>();
            var play = StartPlay(project.FolderPath, workspace, GuiFile.Load(project.UiPath, out _), log, faults);
            Step(play, 1200, faults);
            peaks.Add((name, play.Runtime?.LuaMemoryPeakBytes ?? 0, faults.Count, faults.Count(f => f.Diagnostic.Kind == ScriptDiagnosticKind.MemoryLimitExceeded)));
            play.Stop(workspace);
            play.Dispose();
        }

        foreach (var (name, peak, faultCount, _) in peaks)
        {
            TestReport.Info($"Lua memory default: {name}: peak {Megabytes(peak)} in 20 s of Play ({faultCount} script faults)");
        }

        var largest = peaks.Count == 0 ? 0 : peaks.Max(p => p.Peak);
        report.Check("Lua memory default: 256 MB leaves at least 8 times the largest demo or showcase peak, and none of them faulted on memory",
            peaks.Count == makers.Length && peaks.All(p => p.MemoryFaults == 0) && largest * 8 <= EditorScriptPreferences.DefaultMemoryLimitBytes,
            $"largest peak {Megabytes(largest)}, {(EditorScriptPreferences.DefaultMemoryLimitBytes / (double)Math.Max(1, largest)).ToString("0", CultureInfo.InvariantCulture)}x headroom");
    }

    /// <summary>
    /// The accounting's cost (information): allocation-heavy Lua, best of 5 runs each; and a whole game, the Lua V5
    /// showcase's 600 frames (scripts, physics, navigation), best of 3 with and without.
    /// </summary>
    public static void Cost(TestReport report, string dir)
    {
        var projects = Path.Combine(dir, "Projects");
        var played = LuaV5Demo.Make(projects) == 0;
        if (played)
        {
            var project = new ProjectStore(projects).ListProjects().First(p => p.Name == LuaV5Demo.ProjectName);
            var withoutGame = Enumerable.Range(0, 3).Select(_ => PlayGame(project, accounting: false, ref played)).Min();
            var withGame = Enumerable.Range(0, 3).Select(_ => PlayGame(project, accounting: true, ref played)).Min();
            TestReport.Info(FormattableString.Invariant(
                $"Lua memory cost: Gameplay Systems Showcase, 600 frames: {withoutGame / 600:F3} ms per frame without accounting, {withGame / 600:F3} ms with ({(withGame - withoutGame) / withoutGame * 100:+0.0;-0.0}%)"));
        }

        report.Check("Lua memory cost: the Lua V5 showcase played 600 frames with and without the accounting, without script faults", played);

        var cases = new (string Name, string Source, int Operations)[]
        {
            ("small tables", "local t for i = 1, 1000000 do t = { i, i + 1 } end", 1_000_000),
            ("strings", "local s for i = 1, 1000000 do s = 'n' .. i end", 1_000_000),
            ("Vector3 math", "local v = Vector3.new(0, 0, 0) local d = Vector3.new(1, 2, 3) for i = 1, 100000 do v = v + d * 0.5 end", 100_000),
            ("no allocation", "local x = 0 for i = 1, 5000000 do x = x + i * 2 end", 5_000_000),
        };

        var failed = new List<string>();
        foreach (var (name, source, operations) in cases)
        {
            var without = Best(source, accounting: false, out var okWithout);
            var with = Best(source, accounting: true, out var okWith);
            if (!okWithout || !okWith)
            {
                failed.Add(name);
            }

            var overhead = (with - without) / without * 100d;
            TestReport.Info(FormattableString.Invariant($"Lua memory cost: {name}: {without / operations * 1e6:F1} ns per operation without accounting, ")
                + FormattableString.Invariant($"{with / operations * 1e6:F1} ns with ({(overhead >= 0 ? "+" : string.Empty)}{overhead:F1}%)"));
        }

        report.Check("Lua memory cost: every benchmark ran with and without the accounting (timings above)", failed.Count == 0, string.Join(", ", failed));

        static double Best(string source, bool accounting, out bool ok)
        {
            ok = true;
            var best = double.MaxValue;
            for (var run = 0; run < 5; run++)
            {
                using var lua = Runtime(accounting ? 1024 * Mb : null, accounting);
                var watch = Stopwatch.StartNew();
                var script = lua.LoadString(source, "Bench.lua");
                watch.Stop();
                ok &= script.Fault is null;
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }

            return best;
        }
    }

    // Milliseconds for 600 frames of the project in Play (headless), as the editor runs it.
    private static double PlayGame(ProjectInfo project, bool accounting, ref bool ok)
    {
        using var cache = new ModelAssetCache();
        cache.SetProject(project.FolderPath);
        SceneFile.TryLoadScene(project.ScenePath, assetPath => cache.Load(assetPath, out _), out var contents, out _);
        var workspace = new Workspace();
        foreach (var part in contents.Parts)
        {
            workspace.AddPart(part);
        }

        var faults = new List<PlayScriptFault>();
        var play = new PlayModeController
        {
            Output = new OutputLog(), GuiSource = GuiFile.Load(project.UiPath, out _), CameraHost = new StubCamera(), ScriptMemoryAccounting = accounting
        };
        if (!play.TryStart(project.FolderPath, workspace, [], null, out _))
        {
            ok = false;
            return double.NaN;
        }

        var watch = Stopwatch.StartNew();
        Step(play, 600, faults);
        watch.Stop();
        ok &= faults.Count == 0 && play.TakeNewFaults().Count == 0;
        play.Stop(workspace);
        play.Dispose();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
