using System.Diagnostics;
using System.Globalization;
using System.Text;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Graphics;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Platform;
using Wisko.Engine.Projects;
using Wisko.Engine.ScriptEditor;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// Saving can never damage a file: every writer goes through <see cref="AtomicFile"/>, so a failure or a crash at any
/// point leaves the old contents or the new ones. Failures are injected at each stage (serialization, writing, disk
/// full, before and after the rename); a held or read-only destination, leftover temporary files, repeated and
/// overlapping saves are covered; and a real crash test kills a process that is saving a scene, many times over.
/// </summary>
internal static class SaveSafetyChecks
{
    private const int CrashKills = 200;
    private const int CrashParts = 1500;

    private sealed class InjectedFailure(string message) : IOException(message);

    public static void FileWrites(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "File.json");

        // Serialization fails halfway through the new contents.
        File.WriteAllText(path, "old contents");
        var failed = !AtomicFile.TryWrite(path, stream =>
        {
            stream.Write("{\"half\":"u8);
            throw new InjectedFailure("the serializer stopped");
        }, out var error);
        report.Check("Safe write: a writer that fails halfway leaves the old file byte for byte, and no temporary file",
            failed && File.ReadAllText(path) == "old contents" && TemporaryFiles(dir) == 0 && error == "the serializer stopped", error);

        // The disk fills up while writing (ERROR_DISK_FULL; ENOSPC surfaces as the same IOException on macOS).
        failed = !AtomicFile.TryWrite(path, stream =>
        {
            stream.Write(new byte[64 * 1024]);
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        }, out error);
        report.Check("Safe write: a full disk leaves the old file and no temporary file; the reason is reported",
            failed && File.ReadAllText(path) == "old contents" && TemporaryFiles(dir) == 0 && error!.Contains("space", StringComparison.Ordinal), error);

        // Each stage before the rename.
        foreach (var stage in new[] { AtomicWriteStage.Written, AtomicWriteStage.Flushed })
        {
            var ok = WithFault(path, stage, () => AtomicFile.TryWriteAllText(path, "new contents", out _));
            report.Check($"Safe write: a failure at '{stage}' leaves the old file and no temporary file",
                !ok && File.ReadAllText(path) == "old contents" && TemporaryFiles(dir) == 0);
        }

        // After the rename the new file is complete, even though the write reports the failure.
        var reported = !WithFault(path, AtomicWriteStage.Replaced, () => AtomicFile.TryWriteAllText(path, "new contents", out _));
        report.Check("Safe write: a failure after the rename leaves the complete new file (never a mixture)",
            reported && File.ReadAllText(path) == "new contents" && TemporaryFiles(dir) == 0);

        // The next save after a failure works normally.
        var recovered = AtomicFile.TryWriteAllText(path, "after recovery", out error);
        report.Check("Safe write: the next save after a failure succeeds", recovered && File.ReadAllText(path) == "after recovery", error);

        // A new file; a folder that does not exist yet.
        var nested = Path.Combine(dir, "a", "b", "New.json");
        report.Check("Safe write: creates a missing folder and a new file",
            AtomicFile.TryWriteAllText(nested, "fresh", out error) && File.ReadAllText(nested) == "fresh", error);

        // WriteNew never replaces; WriteWithBackup keeps the previous contents.
        var refused = false;
        try
        {
            AtomicFile.WriteNew(nested, stream => stream.Write("other"u8));
        }
        catch (IOException)
        {
            refused = true;
        }

        report.Check("Safe write: WriteNew refuses an existing file and leaves it and no temporary file",
            refused && File.ReadAllText(nested) == "fresh" && TemporaryFiles(Path.GetDirectoryName(nested)!) == 0);
        var backup = path + ".bak";
        AtomicFile.WriteWithBackup(path, backup, stream => stream.Write("second"u8));
        AtomicFile.WriteWithBackup(path, backup, stream => stream.Write("third"u8));
        report.Check("Safe write: WriteWithBackup keeps the previous version as the backup",
            File.ReadAllText(path) == "third" && File.ReadAllText(backup) == "second");

        // Repeated saving: many complete versions, nothing left behind.
        for (var i = 0; i < 300; i++)
        {
            AtomicFile.WriteAllText(path, $"version {i}");
        }

        report.Check("Safe write: 300 saves in a row end with the last version and no temporary files",
            File.ReadAllText(path) == "version 299" && TemporaryFiles(dir) == 0);

        // Overlapping saves from several threads: the file is one writer's complete contents.
        var contents = Enumerable.Range(0, 8).Select(t => new string((char)('A' + t), 200_000)).ToArray();
        var errors = 0;
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            for (var i = 0; i < 25; i++)
            {
                if (!AtomicFile.TryWriteAllText(path, contents[t], out _))
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });
        var final = File.ReadAllText(path);
        report.Check("Safe write: 8 threads saving 25 times each at once leave one writer's complete contents",
            contents.Contains(final) && TemporaryFiles(dir) == 0, $"{errors} of 200 writes reported a failure (retried sharing conflicts)");

        // Temporary files that a crash left behind are never read, and only they are cleaned up.
        var stale = Path.Combine(dir, $".File.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(stale, "{ truncated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        var unrelated = Path.Combine(dir, ".notes.tmp");
        var legacy = Path.Combine(dir, "UI.json.tmp");
        File.WriteAllText(unrelated, "keep");
        File.WriteAllText(legacy, "keep");
        var fresh = Path.Combine(dir, $".Other.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(fresh, "in progress");
        var removed = AtomicFile.RemoveStaleTemporaryFiles(dir, TimeSpan.FromMinutes(10));
        report.Check("Safe write: stale temporary files are removed; a recent one and other files are kept",
            removed == 1 && !File.Exists(stale) && File.Exists(fresh) && File.Exists(unrelated) && File.Exists(legacy));
        File.Delete(fresh);
        File.Delete(unrelated);
        File.Delete(legacy);

        HeldDestination(report, dir);
    }

    // A destination another program has open. Windows refuses to replace it (after retrying); macOS replaces it (POSIX
    // rename). Either way the old file or the new one is whole and nothing is left behind.
    private static void HeldDestination(TestReport report, string dir)
    {
        var path = Path.Combine(dir, "Held.json");
        File.WriteAllText(path, "old");
        bool saved;
        string? error;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saved = AtomicFile.TryWriteAllText(path, "new", out error);
        }

        var text = File.ReadAllText(path);
        if (OperatingSystem.IsWindows())
        {
            report.Check("Safe write (Windows): a destination held open by another program is not replaced; the old file stays and the reason is given",
                !saved && text == "old" && TemporaryFiles(dir) == 0 && !string.IsNullOrEmpty(error), error);
        }
        else
        {
            report.Check("Safe write (macOS): a destination held open is replaced by the complete new file (POSIX rename)",
                saved && text == "new" && TemporaryFiles(dir) == 0, error);
        }

        // Released during the retries: the save goes through.
        File.WriteAllText(path, "old");
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(80);
            holder.Dispose();
        });
        saved = AtomicFile.TryWriteAllText(path, "new", out error);
        release.Wait();
        report.Check("Safe write: a destination released while the save retries is replaced",
            saved && File.ReadAllText(path) == "new" && TemporaryFiles(dir) == 0, error);

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            saved = AtomicFile.TryWriteAllText(path, "changed", out error);
            report.Check("Safe write (Windows): a read-only file is not replaced; the reason is given",
                !saved && File.ReadAllText(path) == "new" && TemporaryFiles(dir) == 0, error);
            File.SetAttributes(path, FileAttributes.Normal);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            AtomicFile.WriteAllText(path, "mode");
            report.Check("Safe write (macOS): the file keeps its permissions",
                File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead));
        }
    }

    /// <summary>Every project and user file writer goes through the safe write: a failure leaves the old file.</summary>
    public static void Writers(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var workspace = new Workspace();
        workspace.AddPart(new Part("Kept", new Vector3(1f, 2f, 3f), new Vector3(2f, 2f, 2f), new Vector3(0.5f, 0.5f, 0.5f)));

        var cases = new List<(string Name, string Path, Func<bool> Save)>
        {
            ("Scene.json", Path.Combine(dir, "Scene.json"), () => SceneFile.TrySave(workspace, Path.Combine(dir, "Scene.json"), out _)),
            ("Atmosphere.json", Path.Combine(dir, "Atmosphere.json"), () => AtmosphereFile.TrySave(Path.Combine(dir, "Atmosphere.json"), AtmosphereSettings.Default, out _)),
            ("UI.json", Path.Combine(dir, "UI.json"), () => GuiFile.TrySave(Path.Combine(dir, "UI.json"), new GuiDocument(), out _)),
            ("Graphics.json", Path.Combine(dir, "Graphics.json"), () => GraphicsFile.TrySave(Path.Combine(dir, "Graphics.json"), GraphicsSettings.Default, out _)),
            ("editor preferences", Path.Combine(dir, "Preferences.json"), () => EditorGraphicsPreferences.Default.TrySave(Path.Combine(dir, "Preferences.json"), out _)),
            ("a Lua script", Path.Combine(dir, "Script.lua"), () => ScriptFileStore.TrySave(Path.Combine(dir, "Script.lua"), "print(1)\n", "\n", false, out _, out _)),
        };

        var saves = new SaveStore(dir);
        cases.Add(("a game save slot", Path.Combine(dir, SaveStore.FolderName, "Slot1.json"), () => saves.Write("Slot1", "{\"new\":true}"u8) is null));

        foreach (var (name, path, save) in cases)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "ORIGINAL");
            var ok = WithFault(path, AtomicWriteStage.Flushed, save);
            var intact = File.ReadAllText(path) == "ORIGINAL" && TemporaryFiles(Path.GetDirectoryName(path)!) == 0;
            var savedAfter = save() && File.ReadAllText(path) != "ORIGINAL";
            report.Check($"Safe write: {name} — a failed save leaves the old file; the next save replaces it", !ok && intact && savedAfter);
        }

        // Project metadata, thumbnails and History through the project store.
        var store = new ProjectStore(Path.Combine(dir, "Projects"));
        if (store.TryCreateProject("Safe Save", WorldTemplate.Classic, workspace, out var project, out var createError))
        {
            var metadata = Path.Combine(project.FolderPath, "project.json");
            var before = File.ReadAllText(metadata);
            var ok = WithFault(metadata, AtomicWriteStage.Flushed, () => store.TryMarkOpened(project, out _));
            report.Check("Safe write: project.json — a failed write leaves the old file", !ok && File.ReadAllText(metadata) == before && TemporaryFiles(project.FolderPath) == 0);
            var pixels = new byte[320 * 180 * 4];
            var thumbnailBefore = File.Exists(project.ThumbnailPath) ? File.ReadAllBytes(project.ThumbnailPath) : null;
            ok = WithFault(project.ThumbnailPath, AtomicWriteStage.Flushed, () => store.TryWriteThumbnail(project, pixels, 320, 180, out _));
            var thumbnailAfter = File.Exists(project.ThumbnailPath) ? File.ReadAllBytes(project.ThumbnailPath) : null;
            report.Check("Safe write: Thumbnail.png — a failed write leaves the thumbnail as it was and no temporary file",
                !ok && (thumbnailBefore is null ? thumbnailAfter is null : thumbnailAfter is not null && thumbnailAfter.SequenceEqual(thumbnailBefore))
                && TemporaryFiles(project.FolderPath) == 0);
        }
        else
        {
            report.Check("Safe write: a project to test project.json and the thumbnail", false, createError);
        }
    }

    /// <summary>
    /// A real crash: a separate process saves a large scene over and over and is killed at a random moment, many times.
    /// Afterwards Scene.json must always read back as one complete version.
    /// </summary>
    public static void CrashWhileSaving(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Scene.json");
        var random = new Random(20261024);
        var whole = 0;
        var midWrite = 0;
        var problems = new List<string>();
        var watch = Stopwatch.StartNew();
        for (var kill = 0; kill < CrashKills; kill++)
        {
            using var child = StartChild(path);
            var ready = child.StandardOutput.ReadLine();
            if (ready != "ready")
            {
                problems.Add($"kill {kill}: the saving process did not start ({ready ?? "no output"})");
                child.Kill(entireProcessTree: true);
                break;
            }

            Thread.Sleep(random.Next(0, 40));
            midWrite += TemporaryFiles(dir) > 0 ? 1 : 0;
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            if (CheckScene(path, out var problem))
            {
                whole++;
            }
            else
            {
                problems.Add($"kill {kill}: {problem}");
            }

            AtomicFile.RemoveStaleTemporaryFiles(dir, TimeSpan.Zero);
        }

        report.Check($"Safe write: a process saving a {CrashParts.ToString("N0", CultureInfo.InvariantCulture)}-part scene killed {CrashKills} times at random moments: Scene.json is always one complete version",
            problems.Count == 0 && whole == CrashKills, problems.Count == 0
                ? $"{whole} complete after {CrashKills} kills; a save was in progress at {midWrite} of them"
                : string.Join(" | ", problems.Take(5)));
        TestReport.Info($"crash test: {CrashKills} kills in {watch.Elapsed.TotalSeconds:F1} s, {midWrite} with a temporary file present at the kill");
    }

    /// <summary>The child side of <see cref="CrashWhileSaving"/>: saves forever (until killed). Wisko.Tests --crash-save-child path.</summary>
    public static int RunCrashChild(string path)
    {
        var generation = 0;
        Save(path, generation++);
        Console.Out.WriteLine("ready");
        Console.Out.Flush();
        while (true)
        {
            Save(path, generation++);
        }
    }

    private static void Save(string path, int generation)
    {
        var workspace = new Workspace();
        for (var i = 0; i < CrashParts; i++)
        {
            workspace.AddPart(new Part($"G{generation}-P{i}", new Vector3(i, generation % 100, 0f), new Vector3(1f, 1f, 1f), new Vector3(0.5f, 0.5f, 0.5f)));
        }

        if (!SceneFile.TrySave(workspace, path, out var error))
        {
            Console.Error.WriteLine($"save failed: {error}");
        }
    }

    private static bool CheckScene(string path, out string problem)
    {
        if (!SceneFile.TryLoadScene(path, null, out var contents, out var error))
        {
            problem = $"Scene.json does not read back: {error}";
            return false;
        }

        if (contents.Parts.Count != CrashParts)
        {
            problem = $"{contents.Parts.Count} parts instead of {CrashParts}";
            return false;
        }

        var prefix = contents.Parts[0].Name[..(contents.Parts[0].Name.IndexOf('-') + 1)];
        if (contents.Parts.Any(p => !p.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            problem = "parts from different saves in one file";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static Process StartChild(string path)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var start = new ProcessStartInfo(self)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        // Run as "dotnet Wisko.Tests.dll" when there is no app host.
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(SaveSafetyChecks).Assembly.Location);
        }

        start.ArgumentList.Add("--crash-save-child");
        start.ArgumentList.Add(path);
        return Process.Start(start) ?? throw new InvalidOperationException("the saving process did not start");
    }

    private static bool WithFault(string path, AtomicWriteStage stage, Func<bool> save)
    {
        var full = Path.GetFullPath(path);
        AtomicFile.FaultInjection = (target, reached) =>
        {
            if (reached == stage && string.Equals(target, full, StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedFailure($"injected failure at {stage}");
            }
        };
        try
        {
            return save();
        }
        finally
        {
            AtomicFile.FaultInjection = null;
        }
    }

    private static int TemporaryFiles(string dir) =>
        Directory.EnumerateFiles(dir).Count(file => AtomicFile.IsTemporaryFile(Path.GetFileName(file)));
}
