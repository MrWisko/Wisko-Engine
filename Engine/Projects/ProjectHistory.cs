using System.Text.Json;
using System.Text.Json.Nodes;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Projects;

/// <summary>Whether a History world still has its project (deleting it through Wisko makes it Deleted).</summary>
public enum ProjectHistoryStatus
{
    Active,
    Deleted
}

/// <summary>
/// One world in History, by its project's stable Id (never its name: two worlds with the same name are two records).
/// It keeps what the Hub knew about the world, and no path: a deleted world's record stays displayable on its own.
/// </summary>
public sealed class ProjectHistoryRecord
{
    internal ProjectHistoryRecord(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public string Name { get; internal set; } = string.Empty;

    public WorldTemplate WorldTemplate { get; internal set; } = WorldTemplate.Classic;

    public DateTime CreatedUtc { get; internal set; }

    public DateTime LastOpenedUtc { get; internal set; }

    /// <summary>When Wisko deleted its project; null while it is active.</summary>
    public DateTime? DeletedUtc { get; internal set; }

    public ProjectHistoryStatus Status { get; internal set; } = ProjectHistoryStatus.Active;

    /// <summary>The world's last preview, kept in History's own thumbnails folder when its project was deleted.</summary>
    public string? ThumbnailFile { get; internal set; }

    /// <summary>Its latest event (created, opened or deleted): History shows the most recent first.</summary>
    public DateTime LastActivityUtc
    {
        get
        {
            var latest = CreatedUtc > LastOpenedUtc ? CreatedUtc : LastOpenedUtc;
            return DeletedUtc is { } deleted && deleted > latest ? deleted : latest;
        }
    }
}

/// <summary>
/// The Hub's permanent record of every world the user has had: one record per project Id, kept when the project is
/// deleted (then Deleted, with its last preview copied into History's own <c>thumbnails</c> folder first). Stored in
/// <c>history.json</c> in <see cref="FolderPath"/> (outside the projects root), written whole to a temp file and
/// renamed. Records are never removed. A history.json that cannot be read is kept aside as history.json.invalid (a
/// fresh one starts); one from a newer Wisko is left untouched and History is not saved this session; an unreadable
/// record inside an otherwise good file is written back unchanged.
/// </summary>
public sealed class ProjectHistory
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "history.json";
    public const string ThumbnailsFolderName = "thumbnails";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, ProjectHistoryRecord> _records = new(StringComparer.Ordinal);
    private readonly List<JsonNode> _unreadable = [];
    private List<ProjectHistoryRecord>? _sorted;
    private bool _writable = true;

    public ProjectHistory(string folderPath)
    {
        FolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        Load();
    }

    public string FolderPath { get; }

    public string FilePath => Path.Combine(FolderPath, FileName);

    public string ThumbnailsPath => Path.Combine(FolderPath, ThumbnailsFolderName);

    /// <summary>A problem found while loading, for the Hub to mention once (null: none).</summary>
    public string? Problem { get; private set; }

    /// <summary>Every record, latest activity first (then created, then Id).</summary>
    public IReadOnlyList<ProjectHistoryRecord> Records => _sorted ??= _records.Values
        .OrderByDescending(record => record.LastActivityUtc)
        .ThenByDescending(record => record.CreatedUtc)
        .ThenBy(record => record.Id, StringComparer.Ordinal)
        .ToList();

    public ProjectHistoryRecord? Find(string id) => _records.GetValueOrDefault(id);

    /// <summary>The kept preview of a record, if it has one.</summary>
    public string? ThumbnailPathOf(ProjectHistoryRecord record) =>
        record.ThumbnailFile is { } file ? Path.Combine(ThumbnailsPath, file) : null;

    /// <summary>
    /// Every existing project has its record (created when missing; name, template and times kept up to date; a Deleted
    /// one whose project is back, e.g. restored by hand, is active again). Saved only when something changed.
    /// </summary>
    public void Sync(IEnumerable<ProjectInfo> projects)
    {
        var changed = false;
        foreach (var project in projects)
        {
            changed |= Apply(project);
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>One project was created or opened: its record exists and is up to date (no duplicate).</summary>
    public void NoteActive(ProjectInfo project)
    {
        if (Apply(project))
        {
            Save();
        }
    }

    /// <summary>
    /// Copies the project's current preview (its custom image, else its automatic one) into History's thumbnails folder
    /// as &lt;Id&gt;.png, before the project is deleted. True with the file name (null: it has no preview); false when the
    /// copy failed (the deletion may go on without it).
    /// </summary>
    public bool TryPreserveThumbnail(ProjectInfo project, out string? fileName, out string? error)
    {
        fileName = null;
        error = null;
        if (!ProjectStore.IsValidId(project.Id))
        {
            error = "the project has no valid Id";
            return false;
        }

        var source = File.Exists(project.CustomThumbnailPath) ? project.CustomThumbnailPath
            : File.Exists(project.ThumbnailPath) ? project.ThumbnailPath
            : null;
        if (source is null)
        {
            return true;
        }

        var name = project.Id + ".png";
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(source);
        }
        catch (Exception ex) when (AtomicFile.IsWriteFailure(ex))
        {
            error = AtomicFile.Describe(ex);
            return false;
        }

        if (!AtomicFile.TryWriteAllBytes(Path.Combine(ThumbnailsPath, name), bytes, out error))
        {
            return false;
        }

        fileName = name;
        return true;
    }

    /// <summary>The project was deleted: its record (created if somehow missing) becomes Deleted and is saved.</summary>
    public bool TryMarkDeleted(ProjectInfo project, DateTime deletedUtc, string? thumbnailFile, out string? error)
    {
        error = null;
        Apply(project);
        var record = _records[project.Id];
        record.Status = ProjectHistoryStatus.Deleted;
        record.DeletedUtc = deletedUtc;
        record.ThumbnailFile = thumbnailFile ?? record.ThumbnailFile;
        _sorted = null;
        return Save(out error);
    }

    // Brings the project's record up to date; true when anything changed.
    private bool Apply(ProjectInfo project)
    {
        if (!ProjectStore.IsValidId(project.Id))
        {
            return false;
        }

        var changed = false;
        if (!_records.TryGetValue(project.Id, out var record))
        {
            record = new ProjectHistoryRecord(project.Id);
            _records.Add(project.Id, record);
            changed = true;
        }

        if (record.Status != ProjectHistoryStatus.Active)
        {
            record.Status = ProjectHistoryStatus.Active;
            record.DeletedUtc = null;
            changed = true;
        }

        if (record.Name != project.Name)
        {
            record.Name = project.Name;
            changed = true;
        }

        if (record.WorldTemplate != project.WorldTemplate)
        {
            record.WorldTemplate = project.WorldTemplate;
            changed = true;
        }

        if (record.CreatedUtc != project.CreatedUtc)
        {
            record.CreatedUtc = project.CreatedUtc;
            changed = true;
        }

        if (record.LastOpenedUtc != project.LastOpenedUtc)
        {
            record.LastOpenedUtc = project.LastOpenedUtc;
            changed = true;
        }

        if (changed)
        {
            _sorted = null;
        }

        return changed;
    }

    private void Load()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            KeepAside($"could not be read ({ex.Message})");
            return;
        }

        if (root is not JsonObject document || document["Worlds"] is not JsonArray worlds)
        {
            KeepAside("is not a History file");
            return;
        }

        var version = document["FormatVersion"] is JsonValue versionValue && versionValue.TryGetValue<int>(out var number) ? number : 0;
        if (version != CurrentFormatVersion)
        {
            // From a newer (or unknown) Wisko: left exactly as it is.
            _writable = false;
            Problem = "History was saved by a different Wisko version: it is not shown or changed";
            Console.WriteLine($"[History] {FilePath}: format version {version} is not supported; History is not saved this session");
            return;
        }

        foreach (var node in worlds)
        {
            if (node is JsonObject world && TryParse(world, out var record) && !_records.ContainsKey(record.Id))
            {
                _records.Add(record.Id, record);
            }
            else if (node is not null)
            {
                _unreadable.Add(node.DeepClone());
            }
        }

        if (_unreadable.Count > 0)
        {
            Console.WriteLine($"[History] {_unreadable.Count} unreadable record(s) kept as they are");
        }
    }

    // A History file that cannot be used is moved aside (never deleted), and History starts again empty.
    private void KeepAside(string reason)
    {
        var aside = FilePath + ".invalid";
        for (var i = 2; File.Exists(aside); i++)
        {
            aside = $"{FilePath}.invalid{i}";
        }

        try
        {
            File.Move(FilePath, aside);
            Problem = $"History {reason}: kept as {Path.GetFileName(aside)}";
        }
        catch (Exception ex)
        {
            _writable = false;
            Problem = $"History {reason}, and it could not be kept aside: History is not saved this session";
            Console.WriteLine($"[History] Could not move {FilePath} aside: {ex.Message}");
        }

        Console.WriteLine($"[History] {Problem}");
    }

    private static bool TryParse(JsonObject world, out ProjectHistoryRecord record)
    {
        record = null!;
        var id = Text(world, "Id");
        var name = Text(world, "Name");
        if (!ProjectStore.IsValidId(id) || string.IsNullOrWhiteSpace(name)
            || !Date(world, "CreatedUtc", out var created) || !Date(world, "LastOpenedUtc", out var opened))
        {
            return false;
        }

        var status = Enum.TryParse<ProjectHistoryStatus>(Text(world, "Status"), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : ProjectHistoryStatus.Active;
        var thumbnail = Text(world, "Thumbnail");
        record = new ProjectHistoryRecord(id!)
        {
            Name = name!,
            WorldTemplate = Enum.TryParse<WorldTemplate>(Text(world, "WorldTemplate"), ignoreCase: true, out var template) && Enum.IsDefined(template)
                ? template
                : WorldTemplate.Classic,
            CreatedUtc = created,
            LastOpenedUtc = opened,
            DeletedUtc = Date(world, "DeletedUtc", out var deleted) ? deleted : null,
            Status = status,

            // Only a plain file name inside the thumbnails folder (the record's own Id), never a path.
            ThumbnailFile = thumbnail == id + ".png" ? thumbnail : null
        };
        return true;
    }

    private static string? Text(JsonObject world, string key) =>
        world[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Date(JsonObject world, string key, out DateTime date)
    {
        date = default;
        return world[key] is JsonValue value && value.TryGetValue<DateTime>(out date);
    }

    private void Save() => Save(out _);

    private bool Save(out string? error)
    {
        error = null;
        if (!_writable)
        {
            error = "History is not saved this session";
            return false;
        }

        var worlds = new JsonArray();
        foreach (var record in _records.Values.OrderBy(record => record.CreatedUtc).ThenBy(record => record.Id, StringComparer.Ordinal))
        {
            var world = new JsonObject
            {
                ["Id"] = record.Id,
                ["Name"] = record.Name,
                ["WorldTemplate"] = record.WorldTemplate.ToString(),
                ["CreatedUtc"] = record.CreatedUtc,
                ["LastOpenedUtc"] = record.LastOpenedUtc,
                ["Status"] = record.Status.ToString()
            };
            if (record.DeletedUtc is { } deleted)
            {
                world["DeletedUtc"] = deleted;
            }

            if (record.ThumbnailFile is { } thumbnail)
            {
                world["Thumbnail"] = thumbnail;
            }

            worlds.Add(world);
        }

        foreach (var node in _unreadable)
        {
            worlds.Add(node.DeepClone());
        }

        var document = new JsonObject { ["FormatVersion"] = CurrentFormatVersion, ["Worlds"] = worlds };
        if (!AtomicFile.TryWriteAllText(FilePath, document.ToJsonString(WriteOptions), out error))
        {
            Console.WriteLine($"[History] Could not save {FilePath}: {error}");
            return false;
        }

        return true;
    }
}
