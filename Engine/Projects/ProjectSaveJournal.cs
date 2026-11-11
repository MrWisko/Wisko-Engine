using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Projects;

/// <summary>What <see cref="ProjectSaveJournal.Recover"/> found and did.</summary>
public enum ProjectRecoveryOutcome
{
    /// <summary>No interrupted save: nothing to do.</summary>
    Nothing,

    /// <summary>A save was interrupted after it was committed; it was finished (every file is from that save).</summary>
    Completed,

    /// <summary>A save was interrupted before any project file changed; the project is as it was, the partial files kept.</summary>
    NotApplied,

    /// <summary>The project's files could not be shown to belong to one save: nothing was changed, everything was kept.</summary>
    Inconsistent
}

/// <summary>What recovery found, for the log and the person.</summary>
public sealed record ProjectRecoveryReport(ProjectRecoveryOutcome Outcome, string Message);

/// <summary>
/// Saves a project's files (Scene.json, Atmosphere.json, UI.json, Graphics.json) as one transaction, so an interruption
/// never leaves files from different saves, and finishes or reports an interrupted save when the project opens. The
/// project files keep their formats; the transaction lives in the project's hidden <c>.wisko</c> folder:
/// <list type="number">
/// <item>The save holds <c>.wisko/save.lock</c> exclusively (a second save of the same project is refused).</item>
/// <item>Every new file is written, complete and flushed, to <c>.wisko/pending</c>.</item>
/// <item>The current files are copied to <c>.wisko/backup</c>.</item>
/// <item>The journal (<c>.wisko/save-journal.json</c>) is written: each file's name and the SHA-256 of its old and new
/// contents. This is the commit point.</item>
/// <item>Each pending file is renamed over its project file (one step each).</item>
/// <item>The backup becomes <c>.wisko/previous</c> (the last save before this one) and the journal is deleted.</item>
/// </list>
/// Recovery only ever completes a committed save from its verified pending files, and only over files that still hold
/// exactly their old contents: it never puts an older file over a newer one, and never restores a backup by itself. A
/// journal is read strictly (only the four file names, well-formed checksums and sizes; links are refused). When the
/// files cannot be shown to belong to one save, nothing is changed, everything is kept and the problem is reported.
/// </summary>
public static partial class ProjectSaveJournal
{
    public const string FolderName = ".wisko";
    public const string JournalFileName = "save-journal.json";
    public const int FormatVersion = 1;

    private const string LockFileName = "save.lock";
    private const string PendingFolderName = "pending";
    private const string BackupFolderName = "backup";
    private const string PreviousFolderName = "previous";
    private const string IncompleteFolderName = "incomplete";
    private const long MaxFileBytes = 1L << 30;

    /// <summary>The only files a project save (and so a journal) may name.</summary>
    public static IReadOnlyList<string> FileNames { get; } = ["Scene.json", "Atmosphere.json", "UI.json", "Graphics.json"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Tests only: called at each stage of a save ("staged", "backed up", "committed", "applied Scene.json", ..., "rotated").</summary>
    internal static Action<string>? FaultInjection { get; set; }

    /// <summary>
    /// Saves <paramref name="files"/> (names from <see cref="FileNames"/>) into <paramref name="projectFolder"/> as one
    /// transaction. False and the reason when the save did not happen; a save that failed before its commit point
    /// changed no project file, and one that failed after it is finished at once when possible, else when the project
    /// next opens or saves.
    /// </summary>
    public static bool TrySave(string projectFolder, IReadOnlyList<(string Name, byte[] Contents)> files, out string? error)
    {
        error = null;
        if (files.Count == 0)
        {
            return true;
        }

        if (files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != files.Count
            || files.Any(file => !FileNames.Contains(file.Name, StringComparer.Ordinal)))
        {
            error = "a project save names only Scene.json, Atmosphere.json, UI.json and Graphics.json, each once";
            return false;
        }

        string folder;
        try
        {
            folder = Path.GetFullPath(projectFolder);
            if (PrepareFolder(folder) is { } problem)
            {
                error = problem;
                return false;
            }
        }
        catch (Exception ex) when (AtomicFile.IsWriteFailure(ex))
        {
            error = AtomicFile.Describe(ex);
            return false;
        }

        var wisko = Path.Combine(folder, FolderName);
        FileStream? lockStream;
        try
        {
            lockStream = new FileStream(Path.Combine(wisko, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "the project is being saved by another Wisko window (or its .wisko/save.lock cannot be opened); nothing was changed";
            return false;
        }

        using (lockStream)
        {
            // An earlier save that never finished: complete it if it is safe; otherwise keep it aside, intact.
            try
            {
                if (File.Exists(Path.Combine(wisko, JournalFileName)))
                {
                    var earlier = RecoverLocked(folder);
                    Console.WriteLine($"[Save] Before saving: {earlier.Message}");
                    if (earlier.Outcome == ProjectRecoveryOutcome.Inconsistent)
                    {
                        SetAside(wisko, "interrupted");
                    }
                }

                KeepIncompleteParts(wisko);
            }
            catch (Exception ex) when (AtomicFile.IsWriteFailure(ex))
            {
                error = $"an earlier interrupted save could not be finished or kept aside ({AtomicFile.Describe(ex)}); this save was not made";
                return false;
            }

            return Commit(folder, wisko, files, out error);
        }
    }

    /// <summary>
    /// Run when a project opens, before its files are read: finishes a save that was interrupted after its commit
    /// point, keeps the parts of one interrupted before it, and changes nothing when the files cannot be shown to belong
    /// to one save.
    /// </summary>
    public static ProjectRecoveryReport Recover(string projectFolder)
    {
        string folder;
        try
        {
            folder = Path.GetFullPath(projectFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent, $"the project folder cannot be used ({ex.Message})");
        }

        var wisko = Path.Combine(folder, FolderName);
        if (!Directory.Exists(wisko))
        {
            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Nothing, "no save journal");
        }

        if (AtomicFile.IsLink(wisko) || AtomicFile.IsLink(folder))
        {
            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent, "the project's .wisko folder (or the project folder) is a link; Wisko does not use it");
        }

        try
        {
            using var lockStream = new FileStream(Path.Combine(wisko, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!File.Exists(Path.Combine(wisko, JournalFileName)))
            {
                return KeepIncompleteParts(wisko)
                    ? new ProjectRecoveryReport(ProjectRecoveryOutcome.NotApplied,
                        "a save was interrupted before it changed anything: the project is as it was saved last; its partial files are kept in .wisko/incomplete")
                    : new ProjectRecoveryReport(ProjectRecoveryOutcome.Nothing, "no interrupted save");
            }

            return RecoverLocked(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent,
                $"the save journal could not be checked ({ex.Message}); nothing was changed");
        }
    }

    private static string? PrepareFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "the project folder does not exist";
        }

        var wisko = Path.Combine(folder, FolderName);
        if (AtomicFile.IsLink(folder) || AtomicFile.IsLink(wisko))
        {
            return "the project folder or its .wisko folder is a link; Wisko does not save through links";
        }

        if (File.Exists(wisko))
        {
            return "the project has a file named .wisko where Wisko keeps its save journal; nothing was changed";
        }

        var created = !Directory.Exists(wisko);
        Directory.CreateDirectory(wisko);
        if (created && OperatingSystem.IsWindows())
        {
            File.SetAttributes(wisko, File.GetAttributes(wisko) | FileAttributes.Hidden);
        }

        foreach (var name in new[] { PendingFolderName, BackupFolderName, PreviousFolderName })
        {
            if (AtomicFile.IsLink(Path.Combine(wisko, name)))
            {
                return $"the project's .wisko/{name} is a link; Wisko does not save through links";
            }
        }

        return null;
    }

    private static bool Commit(string folder, string wisko, IReadOnlyList<(string Name, byte[] Contents)> files, out string? error)
    {
        error = null;
        var pending = Path.Combine(wisko, PendingFolderName);
        var backup = Path.Combine(wisko, BackupFolderName);
        var entries = new List<JournalEntry>();
        try
        {
            Directory.CreateDirectory(pending);
            foreach (var (name, contents) in files)
            {
                AtomicFile.WriteAllBytes(Path.Combine(pending, name), contents);
            }

            FaultInjection?.Invoke("staged");
            Directory.CreateDirectory(backup);
            foreach (var (name, contents) in files)
            {
                var target = Path.Combine(folder, name);
                string? oldHash = null;
                if (File.Exists(target))
                {
                    if (AtomicFile.IsLink(target))
                    {
                        throw new IOException($"{name} is a link; Wisko does not save through links");
                    }

                    var current = File.ReadAllBytes(target);
                    AtomicFile.WriteAllBytes(Path.Combine(backup, name), current);
                    oldHash = Hash(current);
                }

                entries.Add(new JournalEntry(name, oldHash, Hash(contents), contents.LongLength));
            }

            FaultInjection?.Invoke("backed up");
            var journal = new JournalDocument(FormatVersion, DateTime.UtcNow, entries);
            AtomicFile.WriteAllBytes(Path.Combine(wisko, JournalFileName), JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
            FaultInjection?.Invoke("committed");
        }
        catch (Exception ex) when (AtomicFile.IsWriteFailure(ex))
        {
            // Before the commit point: no project file changed. The partial files go.
            TryDeleteFolder(pending);
            TryDeleteFolder(backup);
            error = AtomicFile.Describe(ex);
            return false;
        }

        try
        {
            Apply(folder, wisko, entries);
            return true;
        }
        catch (Exception ex) when (AtomicFile.IsWriteFailure(ex))
        {
            // After the commit point: finish now if possible, else on the next open or save.
            ProjectRecoveryReport recovered;
            try
            {
                recovered = RecoverLocked(folder);
            }
            catch (Exception again) when (AtomicFile.IsWriteFailure(again))
            {
                recovered = new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent, again.Message);
            }

            if (recovered.Outcome == ProjectRecoveryOutcome.Completed)
            {
                Console.WriteLine($"[Save] The save hit a problem ({ex.Message}) and was finished: {recovered.Message}");
                return true;
            }

            error = $"{AtomicFile.Describe(ex)}; the save will be finished when the project next opens or saves ({recovered.Message})";
            return false;
        }
    }

    // Renames every pending file whose project file still holds its old contents (or already holds the new ones), then
    // rotates the backup and deletes the journal. Callers have checked the entries.
    private static void Apply(string folder, string wisko, IReadOnlyList<JournalEntry> entries)
    {
        var pending = Path.Combine(wisko, PendingFolderName);
        foreach (var entry in entries)
        {
            var target = Path.Combine(folder, entry.Name);
            if (HashOfFile(target) == entry.New)
            {
                continue;
            }

            AtomicFile.MoveReplace(Path.Combine(pending, entry.Name), target);
            FaultInjection?.Invoke("applied " + entry.Name);
        }

        var backup = Path.Combine(wisko, BackupFolderName);
        var previous = Path.Combine(wisko, PreviousFolderName);
        if (Directory.Exists(backup))
        {
            TryDeleteFolder(previous);
            Directory.Move(backup, previous);
        }

        FaultInjection?.Invoke("rotated");
        File.Delete(Path.Combine(wisko, JournalFileName));
        TryDeleteFolder(pending);
    }

    private static ProjectRecoveryReport RecoverLocked(string folder)
    {
        var wisko = Path.Combine(folder, FolderName);
        if (ReadJournal(wisko) is not { } entries)
        {
            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent,
                "the save journal is malformed (only Scene.json, Atmosphere.json, UI.json and Graphics.json with valid checksums are accepted); nothing was changed and every file was kept");
        }

        var pending = Path.Combine(wisko, PendingFolderName);
        if (AtomicFile.IsLink(pending))
        {
            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent, "the journal's pending folder is a link; nothing was changed");
        }

        var applied = new List<string>();
        var unapplied = new List<string>();
        var conflicts = new List<string>();
        var allOld = true;
        foreach (var entry in entries)
        {
            var target = Path.Combine(folder, entry.Name);
            if (File.Exists(target) && AtomicFile.IsLink(target))
            {
                conflicts.Add($"{entry.Name} is a link");
                allOld = false;
                continue;
            }

            var current = HashOfFile(target);
            if (current == entry.New)
            {
                applied.Add(entry.Name);
                allOld &= entry.Old == entry.New;
                continue;
            }

            if (current != entry.Old)
            {
                // Neither the old nor the new contents: changed by someone after the interruption. Never overwritten.
                conflicts.Add($"{entry.Name} holds contents from neither save");
                allOld = false;
                continue;
            }

            var pendingFile = Path.Combine(pending, entry.Name);
            if (!File.Exists(pendingFile) || AtomicFile.IsLink(pendingFile) || new FileInfo(pendingFile).Length != entry.Length || HashOfFile(pendingFile) != entry.New)
            {
                conflicts.Add($"the new {entry.Name} is missing or damaged");
                continue;
            }

            unapplied.Add(entry.Name);
        }

        if (conflicts.Count > 0)
        {
            if (allOld)
            {
                // No project file was replaced yet: the project is the last complete save. The transaction is kept aside.
                SetAside(wisko, "interrupted");
                return new ProjectRecoveryReport(ProjectRecoveryOutcome.NotApplied,
                    $"an interrupted save had not changed any project file ({string.Join("; ", conflicts)}): the project is as it was saved last; the interrupted save is kept in .wisko");
            }

            return new ProjectRecoveryReport(ProjectRecoveryOutcome.Inconsistent,
                $"an interrupted save could not be finished safely ({string.Join("; ", conflicts)}); nothing was changed and every file was kept (the new files in .wisko/pending, the previous ones in .wisko/backup)");
        }

        Apply(folder, wisko, entries);
        return new ProjectRecoveryReport(ProjectRecoveryOutcome.Completed, unapplied.Count == 0
            ? "an interrupted save had written every file; it was finished"
            : $"an interrupted save was finished ({string.Join(", ", unapplied)} completed from its verified new files)");
    }

    private static IReadOnlyList<JournalEntry>? ReadJournal(string wisko)
    {
        var path = Path.Combine(wisko, JournalFileName);
        try
        {
            if (AtomicFile.IsLink(path) || new FileInfo(path).Length > 64 * 1024)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(nameof(JournalDocument.FormatVersion), out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != FormatVersion
                || !root.TryGetProperty(nameof(JournalDocument.Files), out var files) || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() is 0 or > 4)
            {
                return null;
            }

            var entries = new List<JournalEntry>();
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object
                    || !file.TryGetProperty(nameof(JournalEntry.Name), out var name) || name.ValueKind != JsonValueKind.String
                    || !FileNames.Contains(name.GetString(), StringComparer.Ordinal) || entries.Any(e => e.Name == name.GetString())
                    || !file.TryGetProperty(nameof(JournalEntry.New), out var newHash) || !IsHash(newHash)
                    || !file.TryGetProperty(nameof(JournalEntry.Old), out var oldHash) || !(oldHash.ValueKind == JsonValueKind.Null || IsHash(oldHash))
                    || !file.TryGetProperty(nameof(JournalEntry.Length), out var length) || !length.TryGetInt64(out var bytes) || bytes is < 0 or > MaxFileBytes)
                {
                    return null;
                }

                entries.Add(new JournalEntry(name.GetString()!, oldHash.ValueKind == JsonValueKind.Null ? null : oldHash.GetString(), newHash.GetString()!, bytes));
            }

            return entries;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsHash(JsonElement value) => value.ValueKind == JsonValueKind.String && HashPattern().IsMatch(value.GetString()!);

    // Partial files of a save interrupted before its journal: kept (the last set only) in .wisko/incomplete.
    private static bool KeepIncompleteParts(string wisko)
    {
        var found = false;
        foreach (var name in new[] { PendingFolderName, BackupFolderName })
        {
            var part = Path.Combine(wisko, name);
            if (Directory.Exists(part) && !AtomicFile.IsLink(part))
            {
                if (!found)
                {
                    TryDeleteFolder(Path.Combine(wisko, IncompleteFolderName));
                    Directory.CreateDirectory(Path.Combine(wisko, IncompleteFolderName));
                    found = true;
                }

                Directory.Move(part, Path.Combine(wisko, IncompleteFolderName, name));
            }
        }

        return found;
    }

    // A transaction that could not be finished: its journal, pending and backup files move, intact, into
    // .wisko/<label>-<UTC time> so the next save starts clean and nothing is lost.
    private static void SetAside(string wisko, string label)
    {
        var aside = Path.Combine(wisko, $"{label}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}");
        Directory.CreateDirectory(aside);
        foreach (var name in new[] { JournalFileName, PendingFolderName, BackupFolderName })
        {
            var source = Path.Combine(wisko, name);
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(aside, name));
            }
            else if (Directory.Exists(source))
            {
                Directory.Move(source, Path.Combine(aside, name));
            }
        }
    }

    private static string? HashOfFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Hash(byte[] contents) => Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && !AtomicFile.IsLink(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Save] Could not remove {path}: {ex.Message}");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    private sealed record JournalEntry(string Name, string? Old, string New, long Length);

    private sealed record JournalDocument(int FormatVersion, DateTime StartedUtc, IReadOnlyList<JournalEntry> Files);
}
