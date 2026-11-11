using System.Text.RegularExpressions;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Gameplay;

/// <summary>
/// Where game saves live (Lua: GameSave): one file per slot, <c>&lt;project&gt;/Saves/&lt;slot&gt;.json</c>, and nowhere
/// else. A slot is a plain name (letters, digits, spaces, '_' and '-', 1 to 64 characters, not a Windows device name), so
/// a script can never name a path: no folders, "..", drive letters or other extensions. The Saves folder and the slot
/// files are refused when they are links (reparse points). Writes go through AtomicFile (a temporary file in the same
/// folder, flushed to disk, which then replaces the slot in one step); the previous version is kept as
/// <c>&lt;slot&gt;.json.bak</c>. This class only moves bytes; what is in them (and checking it) is the Lua runtime's
/// save codec.
/// </summary>
public sealed partial class SaveStore
{
    public const string FolderName = "Saves";
    public const string Extension = ".json";
    public const string BackupExtension = ".json.bak";
    public const int MaxSlotLength = 64;
    public const long MaxFileBytes = 16L * 1024 * 1024;
    public const int MaxSlots = 1000;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string? _folder;

    public SaveStore(string? projectRoot)
    {
        _folder = string.IsNullOrEmpty(projectRoot) ? null : Path.Combine(Path.GetFullPath(projectRoot), FolderName);
    }

    /// <summary>Saving is possible (a project folder exists to save in).</summary>
    public bool Available => _folder is not null;

    /// <summary>The Saves folder (null without a project).</summary>
    public string? Folder => _folder;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _-]*$")]
    private static partial Regex SlotPattern();

    /// <summary>Null when <paramref name="slot"/> is a usable slot name, else why not (for the script).</summary>
    public static string? CheckSlot(string slot)
    {
        if (slot.Length is 0 or > MaxSlotLength || !SlotPattern().IsMatch(slot) || slot.EndsWith(' '))
        {
            return $"a save slot is a name of 1 to {MaxSlotLength} letters, digits, spaces, '_' or '-' (starting with a letter or digit), such as \"Slot1\" or \"Autosave\"";
        }

        if (DeviceNames.Contains(slot))
        {
            return $"\"{slot}\" cannot be a save slot name (Windows reserves it)";
        }

        return null;
    }

    public bool Exists(string slot) => TryPaths(slot, out var file, out _) is null && File.Exists(file);

    public bool BackupExists(string slot) => TryPaths(slot, out _, out var backup) is null && File.Exists(backup);

    /// <summary>Writes a slot safely (temporary file, flush, replace; the old version becomes the backup). Null when done.</summary>
    public string? Write(string slot, ReadOnlySpan<byte> bytes)
    {
        if (TryPaths(slot, out var file, out var backup) is { } problem)
        {
            return problem;
        }

        if (bytes.Length > MaxFileBytes)
        {
            return $"the save is too big ({bytes.Length / 1024 / 1024} MB; at most {MaxFileBytes / 1024 / 1024} MB)";
        }

        try
        {
            Directory.CreateDirectory(_folder!);
            if (IsLink(_folder!))
            {
                return "the project's Saves folder is a link; saves are only written to a real folder";
            }

            if (Directory.GetFiles(_folder!, "*" + Extension).Length >= MaxSlots && !File.Exists(file))
            {
                return $"there can be at most {MaxSlots} save slots: delete some first";
            }

            if (File.Exists(file) && IsLink(file))
            {
                return $"the save file for \"{slot}\" is a link; it is not overwritten";
            }

            var data = bytes.ToArray();
            AtomicFile.WriteWithBackup(file, backup, stream => stream.Write(data));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return $"the save could not be written ({ex.Message})";
        }
    }

    /// <summary>The bytes of a slot (or of its backup). Null bytes and a reason when there are none.</summary>
    public string? Read(string slot, bool backup, out byte[]? bytes)
    {
        bytes = null;
        if (TryPaths(slot, out var file, out var backupFile) is { } problem)
        {
            return problem;
        }

        var path = backup ? backupFile : file;
        try
        {
            if (!File.Exists(path))
            {
                return $"there is no save called \"{slot}\"";
            }

            if (IsLink(path))
            {
                return $"the save file for \"{slot}\" is a link; it is not read";
            }

            var length = new FileInfo(path).Length;
            if (length > MaxFileBytes)
            {
                return $"the save \"{slot}\" is too big to read ({length / 1024 / 1024} MB)";
            }

            bytes = File.ReadAllBytes(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return $"the save \"{slot}\" could not be read ({ex.Message})";
        }
    }

    /// <summary>Removes a slot and its backup. Null when done (also when there was nothing); else why not.</summary>
    public string? Delete(string slot, out bool existed)
    {
        existed = false;
        if (TryPaths(slot, out var file, out var backup) is { } problem)
        {
            return problem;
        }

        try
        {
            existed = File.Exists(file) || File.Exists(backup);
            if (File.Exists(file) && IsLink(file) || File.Exists(backup) && IsLink(backup))
            {
                return $"the save file for \"{slot}\" is a link; it is not deleted";
            }

            File.Delete(file);
            File.Delete(backup);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"the save \"{slot}\" could not be deleted ({ex.Message})";
        }
    }

    /// <summary>The slots that have a save file, by name.</summary>
    public List<string> ListSlots()
    {
        var slots = new List<string>();
        if (_folder is null || !Directory.Exists(_folder) || IsLink(_folder))
        {
            return slots;
        }

        try
        {
            foreach (var file in Directory.GetFiles(_folder, "*" + Extension))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var slot = name[..^Extension.Length];
                if (CheckSlot(slot) is null && !IsLink(file))
                {
                    slots.Add(slot);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return slots;
        }

        slots.Sort(StringComparer.OrdinalIgnoreCase);
        return slots;
    }

    private string? TryPaths(string slot, out string file, out string backup)
    {
        file = backup = string.Empty;
        if (_folder is null)
        {
            return "saving needs a project (this game has no project folder)";
        }

        if (CheckSlot(slot) is { } problem)
        {
            return problem;
        }

        file = Path.Combine(_folder, slot + Extension);
        backup = Path.Combine(_folder, slot + BackupExtension);

        // Belt and braces: the name pattern already keeps every path directly inside the folder.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), _folder, StringComparison.OrdinalIgnoreCase))
        {
            return "that save slot name is not allowed";
        }

        if (Directory.Exists(_folder) && IsLink(_folder))
        {
            return "the project's Saves folder is a link; saves are only kept in a real folder";
        }

        return null;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
