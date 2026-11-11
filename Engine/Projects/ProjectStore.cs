using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisko.Engine.Platform;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;

namespace Wisko.Engine.Projects;

/// <summary>
/// Projects live in one folder each under <see cref="RootPath"/>:
/// <c>&lt;Root&gt;/&lt;Name&gt;/project.json</c> + <c>Scene.json</c> (+ optional <c>Thumbnail.png</c>,
/// <c>CustomThumbnail.png</c>). A folder only counts as a project once a
/// valid project.json exists, and project.json is always written last (temp file + rename), so a
/// half-created project never shows up in the hub.
/// <para>
/// Every project has a stable Id in project.json. New projects get a random one; a project from before Ids (or a
/// copied folder whose Id another project already has) gets one the first time it is listed, written back to its
/// project.json (an older project's is derived from its folder name and creation time, so it is the same on every
/// listing even if it cannot be written).
/// </para>
/// <para>
/// Deleting (<see cref="TryDeleteProject"/>) is the only way a project folder is removed: the folder must be a valid,
/// real (not linked) direct child of the root whose project.json still has the project's Id; it is first moved into
/// the root's hidden <c>.wisko-deleting</c> folder (one rename: the project leaves the library at once, or nothing
/// changes), then removed from there. Anything left there is removed at the next start
/// (<see cref="PurgeDeletedProjects"/>).
/// </para>
/// </summary>
public sealed class ProjectStore
{
    public const string MetadataFileName = "project.json";
    public const string SceneFileName = "Scene.json";
    public const string ThumbnailFileName = "Thumbnail.png";
    public const string CustomThumbnailFileName = "CustomThumbnail.png";

    // Card preview size (16:9), shared by the automatic viewport capture and custom images.
    public const int ThumbnailWidth = 320;
    public const int ThumbnailHeight = 180;
    public const int MaxNameLength = 40;

    /// <summary>Hidden folder inside the root that deleted projects pass through (never listed: no project.json).</summary>
    public const string DeletingFolderName = ".wisko-deleting";

    private const int MaxIdLength = 64;

    // Rejected on every platform so a project folder stays portable (Windows-unsafe chars included).
    private static readonly char[] UnsafeNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public ProjectStore(string rootPath)
    {
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    public string RootPath { get; }

    /// <summary>
    /// All valid projects, most recently opened first (then most recently created, then by name and Id: never in
    /// file-system order). Each has a unique Id (one that had none, or a duplicate, gets one written back).
    /// </summary>
    public List<ProjectInfo> ListProjects()
    {
        var projects = new List<ProjectInfo>();
        if (!Directory.Exists(RootPath))
        {
            return projects;
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(RootPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectStore] Could not list {RootPath}: {ex.Message}");
            return projects;
        }

        // In a fixed order, so which of two folders with the same Id keeps it never depends on the file system.
        Array.Sort(folders, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!TryReadMetadata(folder, out var project))
            {
                continue;
            }

            if (project.Id.Length == 0 || ids.Contains(project.Id))
            {
                project = WithNewId(project, ids);
            }

            ids.Add(project.Id);
            projects.Add(project);
        }

        projects.Sort(CompareRecency);
        return projects;
    }

    /// <summary>Most recently opened first, then most recently created, then name, then Id.</summary>
    public static int CompareRecency(ProjectInfo a, ProjectInfo b)
    {
        var order = b.LastOpenedUtc.CompareTo(a.LastOpenedUtc);
        if (order == 0)
        {
            order = b.CreatedUtc.CompareTo(a.CreatedUtc);
        }

        if (order == 0)
        {
            order = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>A usable project Id: 8 to 64 ASCII letters, digits or '-' (it also names History's thumbnail files).</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: >= 8 and <= MaxIdLength } && id.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');

    /// <summary>
    /// Trims the name and checks it can become a new project folder. Never transforms the name beyond
    /// trimming: anything unsafe is rejected with a short user-facing reason.
    /// </summary>
    public bool TryValidateNewName(string rawName, out string name, out string? error)
    {
        name = rawName.Trim();
        error = null;

        if (name.Length == 0)
        {
            error = "Enter a project name";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"Name must be {MaxNameLength} characters or fewer";
            return false;
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        foreach (var ch in name)
        {
            if (char.IsControl(ch) || Array.IndexOf(invalidFileNameChars, ch) >= 0)
            {
                error = "Name contains an invalid character";
                return false;
            }

            if (Array.IndexOf(UnsafeNameChars, ch) >= 0)
            {
                error = $"Name can't contain {ch}";
                return false;
            }
        }

        if (name[0] == '.')
        {
            error = "Name can't start with a dot";
            return false;
        }

        if (name[^1] == '.')
        {
            error = "Name can't end with a dot";
            return false;
        }

        if (ReservedNames.Contains(name))
        {
            error = "That name is reserved";
            return false;
        }

        // Defense in depth: the resolved folder must be a direct child of the projects root.
        var folder = Path.GetFullPath(Path.Combine(RootPath, name));
        if (!string.Equals(Path.GetDirectoryName(folder), RootPath, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(folder), name, StringComparison.Ordinal))
        {
            error = "Name contains an invalid character";
            return false;
        }

        if (NameExists(name))
        {
            error = "A project with that name already exists";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates the project folder, writes Scene.json from the workspace, then project.json. On any
    /// failure the just-created folder is removed so no partial project is left behind.
    /// </summary>
    public bool TryCreateProject(
        string rawName,
        WorldTemplate template,
        Workspace workspace,
        out ProjectInfo project,
        out string? error)
    {
        project = null!;
        if (!TryValidateNewName(rawName, out var name, out error))
        {
            return false;
        }

        var folder = Path.Combine(RootPath, name);
        try
        {
            Directory.CreateDirectory(RootPath);
            if (Directory.Exists(folder) || File.Exists(folder))
            {
                error = "A project with that name already exists";
                return false;
            }

            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            error = $"Could not create project folder: {ex.Message}";
            return false;
        }

        var now = DateTime.UtcNow;
        var created = new ProjectInfo
        {
            Id = NewId(),
            Name = name,
            FolderPath = folder,
            CreatedUtc = now,
            LastOpenedUtc = now,
            WorldTemplate = template
        };

        if (!SceneFile.TrySave(workspace, created.ScenePath, out var sceneError))
        {
            error = $"Could not save scene: {sceneError}";
            TryDeleteNewFolder(folder);
            return false;
        }

        if (!TryWriteMetadata(created, out var metadataError))
        {
            error = $"Could not save project: {metadataError}";
            TryDeleteNewFolder(folder);
            return false;
        }

        project = created;
        return true;
    }

    /// <summary>Stamps LastOpenedUtc = now and persists it.</summary>
    public bool TryMarkOpened(ProjectInfo project, out string? error)
    {
        project.LastOpenedUtc = DateTime.UtcNow;
        return TryWriteMetadata(project, out error);
    }

    /// <summary>Writes the project's Thumbnail.png (atomically) from top-down RGBA pixels.</summary>
    public bool TryWriteThumbnail(ProjectInfo project, byte[] rgba, int width, int height, out string? error) =>
        TryWritePngAtomically(project.ThumbnailPath, rgba, width, height, out error);

    /// <summary>
    /// Builds CustomThumbnail.png (320x180, center-cropped to 16:9) from a user image. The source file
    /// is only read; an existing custom image is replaced only once the new one has been fully written.
    /// Thumbnail.png (the automatic viewport capture) is never touched.
    /// </summary>
    public bool TrySetCustomThumbnail(ProjectInfo project, string sourcePath, out string? error)
    {
        if (!CustomThumbnailImage.TryCreate(sourcePath, ThumbnailWidth, ThumbnailHeight, out var rgba, out error))
        {
            return false;
        }

        return TryWritePngAtomically(project.CustomThumbnailPath, rgba, ThumbnailWidth, ThumbnailHeight, out error);
    }

    /// <summary>Removes CustomThumbnail.png only, so the automatic Thumbnail.png shows again.</summary>
    public bool TryResetCustomThumbnail(ProjectInfo project, out string? error)
    {
        error = null;
        try
        {
            File.Delete(project.CustomThumbnailPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="project"/> may be deleted, and its folder: a real (not linked) folder directly inside the
    /// root (never the root itself, nor a path with "..", nor the home, Documents or file-system root) whose
    /// project.json still has this project's Id. False with a short reason otherwise; nothing is touched either way.
    /// </summary>
    public bool TryValidateDeletion(ProjectInfo project, out string folder, out string? error)
    {
        folder = string.Empty;
        error = null;
        var name = string.IsNullOrWhiteSpace(project.Name) ? "this project" : $"\"{project.Name}\"";
        var raw = project.FolderPath;
        if (string.IsNullOrWhiteSpace(raw) || !IsValidId(project.Id))
        {
            error = $"Couldn't delete {name}: it is not a known project";
            return false;
        }

        // A path that climbs out anywhere is refused outright, even if it would resolve back inside.
        if (raw.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment == ".."))
        {
            error = $"Couldn't delete {name}: its path is not inside the Projects folder";
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        }
        catch (Exception)
        {
            error = $"Couldn't delete {name}: its path is not valid";
            return false;
        }

        var home = TrimmedFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var protectedPaths = new[]
        {
            RootPath,
            Path.GetPathRoot(full) is { Length: > 0 } fileSystemRoot ? TrimmedFullPath(fileSystemRoot) : null,
            home,
            home is null ? null : Path.Combine(home, "Documents")
        };
        var folderName = Path.GetFileName(full);
        if (protectedPaths.Any(path => path is not null && string.Equals(path, full, StringComparison.OrdinalIgnoreCase))
            || !string.Equals(Path.GetDirectoryName(full), RootPath, StringComparison.Ordinal)
            || folderName.Length == 0 || folderName[0] == '.')
        {
            error = $"Couldn't delete {name}: its path is not inside the Projects folder";
            return false;
        }

        try
        {
            var info = new DirectoryInfo(full);
            if (!info.Exists)
            {
                error = $"Couldn't delete {name}: it is no longer in the Projects folder";
                return false;
            }

            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = $"Couldn't delete {name}: its folder is a link";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Couldn't delete {name}: {ex.Message}";
            return false;
        }

        // The folder must still be this project (its own project.json, with the same Id).
        if (!TryReadMetadata(full, out var onDisk) || !string.Equals(onDisk.Id, project.Id, StringComparison.Ordinal))
        {
            error = $"Couldn't delete {name}: its folder no longer matches this project";
            return false;
        }

        folder = full;
        return true;
    }

    /// <summary>
    /// Deletes the project's whole folder after <see cref="TryValidateDeletion"/>: first one rename into the root's
    /// <see cref="DeletingFolderName"/> folder (if that fails, e.g. a permission or locked file, nothing has changed and
    /// false is returned), then the recursive delete there (a failure at that point is only logged: the project is
    /// already out of the library, and the leftover is removed at the next start). True once it left the library.
    /// </summary>
    public bool TryDeleteProject(ProjectInfo project, out string? error)
    {
        if (!TryValidateDeletion(project, out var folder, out error))
        {
            return false;
        }

        string staged;
        try
        {
            var staging = Path.Combine(RootPath, DeletingFolderName);
            Directory.CreateDirectory(staging);
            staged = Path.Combine(staging, $"{project.Id}-{DateTime.UtcNow.Ticks}");
            Directory.Move(folder, staged);
        }
        catch (Exception ex)
        {
            error = $"Couldn't delete \"{project.Name}\": {ex.Message}";
            return false;
        }

        Console.WriteLine($"[ProjectStore] Deleted \"{project.Name}\" ({project.Id}): {folder}");
        TryDeleteStaged(staged);
        return true;
    }

    /// <summary>Removes whatever an earlier delete left in <see cref="DeletingFolderName"/> (call at startup).</summary>
    public void PurgeDeletedProjects()
    {
        var staging = Path.Combine(RootPath, DeletingFolderName);
        try
        {
            var info = new DirectoryInfo(staging);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return;
            }

            foreach (var leftover in info.GetFileSystemInfos())
            {
                if (leftover is DirectoryInfo directory && directory.LinkTarget is null)
                {
                    TryDeleteStaged(directory.FullName);
                }
                else
                {
                    leftover.Delete(); // a stray file or link: removes the entry itself, never what a link points to
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectStore] Could not clean {staging}: {ex.Message}");
        }
    }

    private static void TryDeleteStaged(string staged)
    {
        try
        {
            Directory.Delete(staged, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectStore] Could not remove {staged} yet (removed at the next start): {ex.Message}");
        }
    }

    private static string? TrimmedFullPath(string path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    // A project from before Ids: derived from its folder name and creation time (stable across listings, distinct for a
    // later project with the same name), unless taken; then (and for a copied folder's duplicate) a random one.
    private static ProjectInfo WithNewId(ProjectInfo project, HashSet<string> taken)
    {
        var id = project.Id.Length == 0 ? DerivedId(project) : NewId();
        while (taken.Contains(id))
        {
            id = NewId();
        }

        var updated = new ProjectInfo
        {
            Id = id,
            Name = project.Name,
            FolderPath = project.FolderPath,
            CreatedUtc = project.CreatedUtc,
            LastOpenedUtc = project.LastOpenedUtc,
            FormatVersion = project.FormatVersion,
            WorldTemplate = project.WorldTemplate
        };
        if (TryWriteMetadata(updated, out var error))
        {
            Console.WriteLine($"[ProjectStore] Gave \"{project.Name}\" the project Id {id}");
        }
        else
        {
            Console.WriteLine($"[ProjectStore] Could not write the Id of \"{project.Name}\": {error}");
        }

        return updated;
    }

    private static string DerivedId(ProjectInfo project)
    {
        var key = $"Wisko project|{Path.GetFileName(project.FolderPath)}|{project.CreatedUtc.ToUniversalTime().Ticks}";
        return "w" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..31].ToLowerInvariant();
    }

    private static bool TryWritePngAtomically(string path, byte[] rgba, int width, int height, out string? error)
    {
        byte[] png;
        try
        {
            png = PngEncoder.EncodeRgba(rgba, width, height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            error = ex.Message;
            return false;
        }

        return AtomicFile.TryWriteAllBytes(path, png, out error);
    }

    private bool NameExists(string name)
    {
        if (!Directory.Exists(RootPath))
        {
            return false;
        }

        // Case-insensitive on purpose (macOS APFS default is case-insensitive). Any existing entry,
        // including an incomplete folder or a file, blocks the name rather than being overwritten.
        foreach (var entry in Directory.EnumerateFileSystemEntries(RootPath))
        {
            if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMetadata(string folder, out ProjectInfo project)
    {
        project = null!;
        var path = Path.Combine(folder, MetadataFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ProjectMetadataDto>(File.ReadAllText(path), JsonOptions);
            if (dto is null
                || dto.FormatVersion != ProjectInfo.CurrentFormatVersion
                || string.IsNullOrWhiteSpace(dto.Name))
            {
                Console.WriteLine($"[ProjectStore] Skipping {folder}: unsupported or incomplete {MetadataFileName}");
                return false;
            }

            project = new ProjectInfo
            {
                Id = IsValidId(dto.Id) ? dto.Id! : string.Empty,
                Name = dto.Name,
                FolderPath = folder,
                CreatedUtc = dto.CreatedUtc,
                LastOpenedUtc = dto.LastOpenedUtc,
                FormatVersion = dto.FormatVersion,
                WorldTemplate = ParseTemplate(dto.WorldTemplate)
            };
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectStore] Skipping {folder}: {ex.Message}");
            return false;
        }
    }

    private static bool TryWriteMetadata(ProjectInfo project, out string? error)
    {
        var dto = new ProjectMetadataDto
        {
            Name = project.Name,
            CreatedUtc = project.CreatedUtc,
            LastOpenedUtc = project.LastOpenedUtc,
            FormatVersion = project.FormatVersion,
            WorldTemplate = project.WorldTemplate.ToString(),
            Id = IsValidId(project.Id) ? project.Id : null
        };
        return AtomicFile.TryWriteAllText(Path.Combine(project.FolderPath, MetadataFileName), JsonSerializer.Serialize(dto, JsonOptions), out error);
    }

    // Milestone-1 projects have no WorldTemplate field; missing or unknown values mean Classic.
    private static WorldTemplate ParseTemplate(string? value) =>
        Enum.TryParse<WorldTemplate>(value, ignoreCase: true, out var template) && Enum.IsDefined(template)
            ? template
            : WorldTemplate.Classic;

    private static void TryDeleteNewFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectStore] Could not remove incomplete project folder {folder}: {ex.Message}");
        }
    }

    private sealed class ProjectMetadataDto
    {
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime LastOpenedUtc { get; set; }
        public int FormatVersion { get; set; }
        public string? WorldTemplate { get; set; }

        // Last, so an older project.json only gains a line; omitted when there is none (never "null").
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }
    }
}
