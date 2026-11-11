using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Graphics;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// A project save is one transaction (<see cref="ProjectSaveJournal"/>): interrupted at any stage, the project's files
/// always end up from one save; recovery finishes only verified, committed saves and never puts an older file over a
/// newer one; malformed or hostile journals (other file names, path traversal, links, bad checksums) are refused without
/// changing anything; simultaneous saves are refused; a full disk changes nothing; and a real crash test kills a process
/// that is saving four files, many times over.
/// </summary>
internal static class ProjectSaveJournalChecks
{
    private const int CrashKills = 200;
    private static readonly string[] Names = ["Scene.json", "Atmosphere.json", "UI.json", "Graphics.json"];

    private sealed class SimulatedCrash(string stage) : Exception($"simulated crash at '{stage}'");

    // Four files that all say which save they belong to.
    private static List<(string Name, byte[] Contents)> Generation(string label, int padding = 2_000) =>
        Names.Select(name => (name, Encoding.UTF8.GetBytes(
            $"{{\n  \"Generation\": \"{label}\",\n  \"File\": \"{name}\",\n  \"Pad\": \"{new string('x', padding)}\"\n}}\n"))).ToList();

    private static string? GenerationOf(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            return json.RootElement.GetProperty("Generation").GetString();
        }
        catch (JsonException)
        {
            return "<damaged>";
        }
    }

    private static string Generations(string folder) => string.Join(",", Names.Select(name => GenerationOf(folder, name) ?? "-"));

    private static bool AllFrom(string folder, string label) => Names.All(name => GenerationOf(folder, name) == label);

    private static string Wisko(string folder) => Path.Combine(folder, ProjectSaveJournal.FolderName);

    private static bool JournalLeft(string folder) => File.Exists(Path.Combine(Wisko(folder), ProjectSaveJournal.JournalFileName));

    private static string NewProject(string dir, string name)
    {
        var folder = Path.Combine(dir, name);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static bool Save(string folder, string label, out string? error) => ProjectSaveJournal.TrySave(folder, Generation(label), out error);

    public static void Basics(TestReport report, string dir)
    {
        report.Check("Save journal: it names exactly the project files the engine writes",
            ProjectSaveJournal.FileNames.SequenceEqual([ProjectStore.SceneFileName, AtmosphereFile.FileName, GuiFile.FileName, GraphicsFile.FileName]));

        var folder = NewProject(dir, "Basics");
        report.Check("Save journal: a project that never used it has nothing to recover",
            ProjectSaveJournal.Recover(folder).Outcome == ProjectRecoveryOutcome.Nothing);
        var first = Save(folder, "one", out var error);
        var wisko = Wisko(folder);
        report.Check("Save journal: a first save writes every file and leaves no journal, pending or backup files",
            first && AllFrom(folder, "one") && !JournalLeft(folder) && !Directory.Exists(Path.Combine(wisko, "pending"))
            && !Directory.Exists(Path.Combine(wisko, "backup")), error);
        if (OperatingSystem.IsWindows())
        {
            report.Check("Save journal: the .wisko folder is hidden (Windows)", (File.GetAttributes(wisko) & FileAttributes.Hidden) != 0);
        }

        var second = Save(folder, "two", out error);
        report.Check("Save journal: the next save keeps the previous one in .wisko/previous",
            second && AllFrom(folder, "two") && AllFrom(Path.Combine(wisko, "previous"), "one"), error);

        var badName = ProjectSaveJournal.TrySave(folder, [("../Escape.json", [1])], out var badNameError);
        var twice = ProjectSaveJournal.TrySave(folder, [("Scene.json", [1]), ("Scene.json", [2])], out _);
        report.Check("Save journal: a save naming another file, or one file twice, is refused and changes nothing",
            !badName && !twice && AllFrom(folder, "two") && !File.Exists(Path.Combine(dir, "Escape.json")), badNameError);

        // A file that did not exist before (Graphics.json in an older project) is created as part of the save.
        var older = NewProject(dir, "Older");
        ProjectSaveJournal.TrySave(older, Generation("a").Where(f => f.Name != "Graphics.json").ToList(), out _);
        var addsGraphics = Save(older, "b", out error);
        report.Check("Save journal: a save that adds Graphics.json to an older project", addsGraphics && AllFrom(older, "b"), error);

        // Compatibility: the project files are exactly what the writers produce (no format change).
        var project = NewProject(dir, "Format");
        var workspace = new Workspace();
        workspace.AddPart(new Part("Kept", new Vector3(1f, 2f, 3f), new Vector3(2f, 2f, 2f), new Vector3(0.5f, 0.5f, 0.5f)));
        var scene = SceneFile.ToJson(workspace);
        var atmosphere = AtmosphereFile.ToJson(AtmosphereSettings.Default);
        var utf8 = new UTF8Encoding(false);
        ProjectSaveJournal.TrySave(project, [("Scene.json", utf8.GetBytes(scene)), ("Atmosphere.json", utf8.GetBytes(atmosphere))], out _);
        var loaded = SceneFile.TryLoadScene(Path.Combine(project, "Scene.json"), null, out var contents, out _);
        report.Check("Save journal: the files are byte for byte what the writers produce, and read back as before",
            File.ReadAllText(Path.Combine(project, "Scene.json")) == scene && File.ReadAllText(Path.Combine(project, "Atmosphere.json")) == atmosphere
            && loaded && contents.Parts.Single().Name == "Kept");

        // The project library is unaffected by the .wisko folder: listed once, and deleted as a whole.
        var store = new ProjectStore(Path.Combine(dir, "Library"));
        if (store.TryCreateProject("Journaled", WorldTemplate.Classic, workspace, out var info, out var createError))
        {
            var journaled = Save(info.FolderPath, "one", out error) && Save(info.FolderPath, "two", out error);
            var listed = store.ListProjects().Count(p => p.Id == info.Id) == 1 && store.ListProjects().Count == 1;
            var deleted = store.TryDeleteProject(info, out var deleteError);
            report.Check("Save journal: a project with a .wisko folder is listed once and deletes completely",
                journaled && listed && deleted && !Directory.Exists(info.FolderPath) && store.ListProjects().Count == 0, error ?? deleteError);
        }
        else
        {
            report.Check("Save journal: a project could be created for the library check", false, createError);
        }
    }

    public static void Interruptions(TestReport report, string dir)
    {
        // A crash at each stage of a save, then the project opens (recovery).
        foreach (var (stage, finished) in new[]
                 {
                     ("staged", false), ("backed up", false), ("committed", true),
                     ("applied Scene.json", true), ("applied Atmosphere.json", true), ("applied UI.json", true), ("rotated", true),
                 })
        {
            var folder = NewProject(dir, "Stage " + stage);
            Save(folder, "old", out _);
            var crashed = false;
            ProjectSaveJournal.FaultInjection = reached =>
            {
                if (reached == stage)
                {
                    throw new SimulatedCrash(stage);
                }
            };
            try
            {
                Save(folder, "new", out _);
            }
            catch (SimulatedCrash)
            {
                crashed = true;
            }
            finally
            {
                ProjectSaveJournal.FaultInjection = null;
            }

            var middle = Generations(folder);
            var recovery = ProjectSaveJournal.Recover(folder);
            var expected = finished ? "new" : "old";
            var outcomeOk = finished ? recovery.Outcome == ProjectRecoveryOutcome.Completed : recovery.Outcome == ProjectRecoveryOutcome.NotApplied;
            report.Check($"Save journal: interrupted at '{stage}' ({middle} at the crash), the project opens with every file from the {expected} save",
                crashed && outcomeOk && AllFrom(folder, expected) && !JournalLeft(folder), $"{recovery.Outcome}: {recovery.Message}");
            var after = Save(folder, "after", out var error);
            report.Check($"Save journal: after an interruption at '{stage}', the next save works", after && AllFrom(folder, "after"), error);
        }

        // An interrupted save is also finished by the next save when the project was not reopened in between.
        var direct = NewProject(dir, "Next save finishes");
        Save(direct, "old", out _);
        ProjectSaveJournal.FaultInjection = reached =>
        {
            if (reached == "applied Atmosphere.json")
            {
                throw new SimulatedCrash(reached);
            }
        };
        try
        {
            Save(direct, "new", out _);
        }
        catch (SimulatedCrash)
        {
        }
        finally
        {
            ProjectSaveJournal.FaultInjection = null;
        }

        var nextSave = Save(direct, "newer", out var nextError);
        report.Check("Save journal: a save after an unrecovered interruption first finishes it, then saves", nextSave && AllFrom(direct, "newer") && !JournalLeft(direct), nextError);
    }

    public static void Failures(TestReport report, string dir)
    {
        // The disk fills up while the new files are written: nothing changes.
        var full = NewProject(dir, "Disk full");
        Save(full, "old", out _);
        ProjectSaveJournal.FaultInjection = reached =>
        {
            if (reached == "staged")
            {
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            }
        };
        var saved = Save(full, "new", out var error);
        ProjectSaveJournal.FaultInjection = null;
        report.Check("Save journal: a full disk before the commit point changes nothing and leaves nothing behind",
            !saved && error!.Contains("space", StringComparison.Ordinal) && AllFrom(full, "old") && !JournalLeft(full)
            && !Directory.Exists(Path.Combine(Wisko(full), "pending")) && !Directory.Exists(Path.Combine(Wisko(full), "backup")), error);

        // A failure after the commit point that goes away at once: the save finishes itself.
        var transient = NewProject(dir, "Transient after commit");
        Save(transient, "old", out _);
        var failures = 0;
        ProjectSaveJournal.FaultInjection = reached =>
        {
            if (reached == "applied Scene.json" && failures++ == 0)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
        };
        saved = Save(transient, "new", out error);
        ProjectSaveJournal.FaultInjection = null;
        report.Check("Save journal: a passing failure after the commit point: the save finishes at once",
            saved && AllFrom(transient, "new") && !JournalLeft(transient), error);

        // A project file held open by another program after the commit point: reported; the next save cannot run past
        // it either; once the file is free, opening the project finishes the save.
        var held = NewProject(dir, "Held after commit");
        Save(held, "old", out _);
        FileStream? holder = null;
        ProjectSaveJournal.FaultInjection = reached =>
        {
            if (reached == "committed")
            {
                holder = new FileStream(Path.Combine(held, "Graphics.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            }
        };
        bool blockedSave;
        string? blockedError;
        try
        {
            saved = Save(held, "new", out error);
            ProjectSaveJournal.FaultInjection = null;
            blockedSave = Save(held, "newer", out blockedError);
        }
        finally
        {
            ProjectSaveJournal.FaultInjection = null;
            holder?.Dispose();
        }

        var pendingNow = JournalLeft(held);
        var middle = Generations(held);
        var recovery = ProjectSaveJournal.Recover(held);
        report.Check("Save journal: a project file held by another program after the commit point: reported, the journal stays",
            !saved && pendingNow && error!.Contains("finished when the project next opens", StringComparison.Ordinal), $"{middle}: {error}");
        report.Check("Save journal: a save while the interrupted one cannot be finished is refused, without an exception",
            !blockedSave && blockedError!.Contains("this save was not made", StringComparison.Ordinal), blockedError);
        report.Check("Save journal: once the file is free, opening the project finishes the interrupted save",
            recovery.Outcome == ProjectRecoveryOutcome.Completed && AllFrom(held, "new") && !JournalLeft(held), recovery.Message);

        // Simultaneous saves: the second is refused while the first holds the lock.
        var locked = NewProject(dir, "Locked");
        Save(locked, "old", out _);
        bool refused;
        using (new FileStream(Path.Combine(Wisko(locked), "save.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            refused = !Save(locked, "new", out error);
        }

        report.Check("Save journal: a save while another save of the project is in progress is refused and changes nothing",
            refused && error!.Contains("another Wisko window", StringComparison.Ordinal) && AllFrom(locked, "old"), error);

        var racing = NewProject(dir, "Racing");
        Save(racing, "start", out _);
        var refusals = 0;
        var otherErrors = new List<string>();
        Parallel.For(0, 2, writer =>
        {
            for (var i = 0; i < 40; i++)
            {
                if (!Save(racing, $"w{writer}-{i}", out var raceError))
                {
                    if (raceError!.Contains("another Wisko window", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref refusals);
                    }
                    else
                    {
                        lock (otherErrors)
                        {
                            otherErrors.Add(raceError);
                        }
                    }
                }
            }
        });
        var final = Names.Select(name => GenerationOf(racing, name)).Distinct().ToList();
        report.Check("Save journal: two writers saving 40 times each at once: every file from the same save, failures only as refusals",
            final.Count == 1 && otherErrors.Count == 0 && !JournalLeft(racing), $"{refusals} refusals; files from {string.Join("/", final)}");
    }

    public static void Safeguards(TestReport report, string dir)
    {
        foreach (var (name, journal) in new (string, string)[]
                 {
                     ("a path outside the project", JournalWith("../Escape.json")),
                     ("an absolute path", JournalWith(Path.Combine(dir, "Escape.json").Replace("\\", "\\\\"))),
                     ("a file other than the project's four", JournalWith("Notes.txt")),
                     ("a checksum that is not SHA-256", "{\"FormatVersion\":1,\"Files\":[{\"Name\":\"Scene.json\",\"Old\":null,\"New\":\"abc\",\"Length\":3}]}"),
                     ("a negative length", "{\"FormatVersion\":1,\"Files\":[{\"Name\":\"Scene.json\",\"Old\":null,\"New\":\"" + new string('a', 64) + "\",\"Length\":-1}]}"),
                     ("the same file twice", "{\"FormatVersion\":1,\"Files\":[" + Entry("Scene.json") + "," + Entry("Scene.json") + "]}"),
                     ("an unknown format version", "{\"FormatVersion\":2,\"Files\":[" + Entry("Scene.json") + "]}"),
                     ("text that is not JSON", "not a journal"),
                 })
        {
            var folder = NewProject(dir, "Hostile " + name);
            Save(folder, "old", out _);
            var before = Snapshot(folder);
            File.WriteAllText(Path.Combine(Wisko(folder), ProjectSaveJournal.JournalFileName), journal);
            var recovery = ProjectSaveJournal.Recover(folder);
            report.Check($"Save journal: a journal with {name} is refused; nothing changes and nothing outside the project is written",
                recovery.Outcome == ProjectRecoveryOutcome.Inconsistent && Snapshot(folder) == before && !File.Exists(Path.Combine(dir, "Escape.json"))
                && JournalLeft(folder), recovery.Message);
        }

        // A file changed after the interruption (neither the old nor the new contents) is never overwritten.
        var edited = Crashed(dir, "Edited after crash", "committed");
        File.WriteAllText(Path.Combine(edited, "Scene.json"), "{ \"Generation\": \"edited by hand\" }");
        var editedBefore = Snapshot(edited);
        var recoveryEdited = ProjectSaveJournal.Recover(edited);
        report.Check("Save journal: recovery never overwrites a file changed after the interruption; nothing changes, everything is kept",
            recoveryEdited.Outcome == ProjectRecoveryOutcome.Inconsistent && Snapshot(edited) == editedBefore
            && GenerationOf(edited, "Scene.json") == "edited by hand" && JournalLeft(edited), recoveryEdited.Message);

        // A damaged new file while nothing was replaced yet: the project stays the previous save.
        var damagedEarly = Crashed(dir, "Damaged before apply", "committed");
        File.WriteAllText(Path.Combine(Wisko(damagedEarly), "pending", "UI.json"), "damaged");
        var recoveryEarly = ProjectSaveJournal.Recover(damagedEarly);
        report.Check("Save journal: a damaged new file before anything was replaced: the project stays the last save, the interrupted save kept aside",
            recoveryEarly.Outcome == ProjectRecoveryOutcome.NotApplied && AllFrom(damagedEarly, "old") && !JournalLeft(damagedEarly)
            && Directory.EnumerateDirectories(Wisko(damagedEarly), "interrupted-*").Any(), recoveryEarly.Message);

        // A damaged new file after part of the save was applied: no older backup goes over the newer files.
        var damagedLate = Crashed(dir, "Damaged after apply", "applied Scene.json");
        File.WriteAllText(Path.Combine(Wisko(damagedLate), "pending", "UI.json"), "damaged");
        var lateBefore = Snapshot(damagedLate);
        var recoveryLate = ProjectSaveJournal.Recover(damagedLate);
        report.Check("Save journal: a damaged new file after part of the save was applied: nothing is restored over the newer files; reported, everything kept",
            recoveryLate.Outcome == ProjectRecoveryOutcome.Inconsistent && Snapshot(damagedLate) == lateBefore && GenerationOf(damagedLate, "Scene.json") == "new"
            && File.Exists(Path.Combine(Wisko(damagedLate), "backup", "Scene.json")), recoveryLate.Message);
        var resolved = Save(damagedLate, "resolved", out var resolvedError);
        report.Check("Save journal: the next save keeps the unfinished save aside, intact, and writes a complete new one",
            resolved && AllFrom(damagedLate, "resolved") && Directory.EnumerateDirectories(Wisko(damagedLate), "interrupted-*")
                .Any(aside => File.Exists(Path.Combine(aside, ProjectSaveJournal.JournalFileName))), resolvedError);

        // Links: the pending folder replaced by a link to somewhere else is never followed.
        var linked = Crashed(dir, "Linked pending", "committed");
        var outside = Path.Combine(dir, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "UI.json"), "outside");
        var pending = Path.Combine(Wisko(linked), "pending");
        Directory.Delete(pending, recursive: true);
        if (TryLink(pending, outside, out var linkProblem))
        {
            var linkedBefore = Snapshot(linked);
            var recoveryLinked = ProjectSaveJournal.Recover(linked);
            var saveLinked = Save(linked, "through link", out var linkError);
            report.Check("Save journal: a pending folder that is a link is not followed, by recovery or by a save",
                recoveryLinked.Outcome == ProjectRecoveryOutcome.Inconsistent && Snapshot(linked) == linkedBefore && !saveLinked
                && File.ReadAllText(Path.Combine(outside, "UI.json")) == "outside", $"{recoveryLinked.Message} | {linkError}");

            // Removed by itself: a recursive delete of a folder holding a junction reports access denied on Windows.
            Directory.Delete(pending);

            // The whole .wisko folder as a link.
            var linkedWisko = NewProject(dir, "Linked wisko");
            Save(linkedWisko, "old", out _);
            var outsideWisko = Path.Combine(dir, "Outside wisko");
            Directory.CreateDirectory(outsideWisko);
            Directory.Delete(Wisko(linkedWisko), recursive: true);
            if (TryLink(Wisko(linkedWisko), outsideWisko, out linkProblem))
            {
                var wiskoBefore = Snapshot(linkedWisko);
                var recoveryWisko = ProjectSaveJournal.Recover(linkedWisko);
                var saveWisko = Save(linkedWisko, "through link", out var wiskoError);
                report.Check("Save journal: a .wisko folder that is a link is not used, by recovery or by a save",
                    recoveryWisko.Outcome == ProjectRecoveryOutcome.Inconsistent && !saveWisko && Snapshot(linkedWisko) == wiskoBefore
                    && !Directory.EnumerateFileSystemEntries(outsideWisko).Any(), $"{recoveryWisko.Message} | {wiskoError}");
                Directory.Delete(Wisko(linkedWisko));
            }
            else
            {
                report.Check("Save journal: a link could be made to test a linked .wisko folder", false, linkProblem);
            }
        }
        else
        {
            TestReport.Info($"Save journal: the link check could not run here ({linkProblem})");
            report.Check("Save journal: a link could be made to test link refusal", false, linkProblem);
        }

        // A file called .wisko where the folder belongs.
        var blocked = NewProject(dir, "Blocked");
        File.WriteAllText(Path.Combine(blocked, ProjectSaveJournal.FolderName), "a file");
        var blockedSave = Save(blocked, "x", out var blockedError);
        report.Check("Save journal: a file named .wisko in the project stops the save, which changes nothing",
            !blockedSave && Names.All(n => !File.Exists(Path.Combine(blocked, n))), blockedError);
    }

    /// <summary>A real crash: a process saving four files over and over, killed at random moments, many times.</summary>
    public static void CrashWhileSaving(TestReport report, string dir)
    {
        var folder = NewProject(dir, "Crash");
        var random = new Random(20261025);
        var consistent = 0;
        var outcomes = new Dictionary<ProjectRecoveryOutcome, int>();
        var problems = new List<string>();
        var watch = Stopwatch.StartNew();
        for (var kill = 0; kill < CrashKills; kill++)
        {
            using var child = StartChild(folder);
            if (child.StandardOutput.ReadLine() != "ready")
            {
                problems.Add($"kill {kill}: the saving process did not start");
                child.Kill(entireProcessTree: true);
                break;
            }

            Thread.Sleep(random.Next(0, 30));
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            var recovery = ProjectSaveJournal.Recover(folder);
            outcomes[recovery.Outcome] = outcomes.GetValueOrDefault(recovery.Outcome) + 1;
            var generations = Names.Select(name => GenerationOf(folder, name)).Distinct().ToList();
            if (generations.Count == 1 && generations[0] is not null and not "<damaged>" && !JournalLeft(folder))
            {
                consistent++;
            }
            else
            {
                problems.Add($"kill {kill}: {Generations(folder)} after {recovery.Outcome} ({recovery.Message})");
            }
        }

        var summary = string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key} {o.Value}"));
        report.Check($"Save journal: a process saving four files killed {CrashKills} times at random moments: after recovery every file is from one save",
            problems.Count == 0 && consistent == CrashKills, problems.Count == 0 ? summary : string.Join(" | ", problems.Take(4)));
        TestReport.Info($"save journal crash test: {CrashKills} kills in {watch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s; recovery outcomes: {summary}");
    }

    /// <summary>The child side: Wisko.Tests --crash-journal-child folder saves generations until killed.</summary>
    public static int RunCrashChild(string folder)
    {
        var generation = 0;
        ProjectSaveJournal.Recover(folder);
        ProjectSaveJournal.TrySave(folder, Generation($"g{generation++}", 60_000), out _);
        Console.Out.WriteLine("ready");
        Console.Out.Flush();
        while (true)
        {
            if (!ProjectSaveJournal.TrySave(folder, Generation($"g{generation++}", 60_000), out var error))
            {
                Console.Error.WriteLine(error);
            }
        }
    }

    private static Process StartChild(string folder)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var start = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(ProjectSaveJournalChecks).Assembly.Location);
        }

        start.ArgumentList.Add("--crash-journal-child");
        start.ArgumentList.Add(folder);
        return Process.Start(start) ?? throw new InvalidOperationException("the saving process did not start");
    }

    // A project with an "old" save, then a "new" save that crashed at the stage.
    private static string Crashed(string dir, string name, string stage)
    {
        var folder = NewProject(dir, name);
        Save(folder, "old", out _);
        ProjectSaveJournal.FaultInjection = reached =>
        {
            if (reached == stage)
            {
                throw new SimulatedCrash(stage);
            }
        };
        try
        {
            Save(folder, "new", out _);
        }
        catch (SimulatedCrash)
        {
        }
        finally
        {
            ProjectSaveJournal.FaultInjection = null;
        }

        return folder;
    }

    private static string Entry(string name) =>
        "{\"Name\":\"" + name + "\",\"Old\":null,\"New\":\"" + new string('a', 64) + "\",\"Length\":3}";

    private static string JournalWith(string name) => "{\"FormatVersion\":1,\"Files\":[" + Entry(name) + "]}";

    // Every file in the project (outside .wisko) with its contents, for "nothing changed".
    private static string Snapshot(string folder) => string.Join("\n", Directory.EnumerateFiles(folder)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + "=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));

    private static bool TryLink(string link, string target, out string problem)
    {
        problem = string.Empty;
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Windows without the symbolic-link privilege: a directory junction needs none.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        mklink!.WaitForExit();
        if (Directory.Exists(link))
        {
            return true;
        }

        problem += "; mklink /J: " + mklink.StandardError.ReadToEnd().Trim();
        return false;
    }
}
