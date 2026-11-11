using System.Text;
using Wisko.Engine.World;

namespace Wisko.Engine.Assets;

/// <summary>
/// Script files of a project, all inside <c>&lt;project&gt;/Assets/Scripts</c>. New scripts and copies get a unique
/// name (<c>Spin.lua</c>, <c>Spin-2.lua</c>, <c>Spin-3.lua</c>, ...) and are written to a temporary file first, then
/// moved into place without overwriting, so an existing script is never replaced and a failure never leaves a
/// half-written script behind.
/// </summary>
public static class ProjectScriptAssets
{
    public const int MaxScriptNameLength = 48;

    /// <summary>Largest file Attach Script copies into a project.</summary>
    public const long MaxScriptFileBytes = 1024 * 1024;

    /// <summary>What a new script contains.</summary>
    public const string NewScriptTemplate =
        "function Start()\n" +
        "    print(\"Started \" .. self.Name)\n" +
        "end\n" +
        "\n" +
        "function Update(dt)\n" +
        "\n" +
        "end\n";

    private static readonly char[] NonPortableChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Checks a name typed for a new script. Trimmed and Unicode-normalized; a typed <c>.lua</c> is dropped (it is
    /// added back on create). Letters, digits, spaces, <c>-</c> and <c>_</c> only, at most
    /// <see cref="MaxScriptNameLength"/> characters. Never transforms the name beyond that: anything else is rejected
    /// with a short reason.
    /// </summary>
    public static bool TryValidateScriptName(string? rawName, out string name, out string? error)
    {
        name = (rawName ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        error = null;
        if (name.EndsWith(ProjectAssetPaths.ScriptExtension, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^ProjectAssetPaths.ScriptExtension.Length].TrimEnd();
        }

        if (name.Length == 0)
        {
            error = "Enter a script name";
            return false;
        }

        if (name.Length > MaxScriptNameLength)
        {
            error = $"Name must be {MaxScriptNameLength} characters or fewer";
            return false;
        }

        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not (' ' or '-' or '_'))
            {
                error = "Use letters, digits, spaces, - and _ only";
                return false;
            }
        }

        if (ReservedNames.Contains(name))
        {
            error = "That name is reserved; choose another";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates a new script from <see cref="NewScriptTemplate"/> as <c>Assets/Scripts/&lt;name&gt;.lua</c> (or the
    /// first free <c>&lt;name&gt;-N.lua</c>). <paramref name="name"/> must have passed <see cref="TryValidateScriptName"/>.
    /// </summary>
    public static bool TryCreateScript(string projectRoot, string name, out ScriptReference? reference, out string? error)
    {
        reference = null;
        if (!TryValidateScriptName(name, out var validName, out error))
        {
            return false;
        }

        return TryPlaceUnique(
            projectRoot,
            validName,
            temp => File.WriteAllText(temp, NewScriptTemplate, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            out reference,
            out error);
    }

    /// <summary>
    /// Brings a picked <c>.lua</c> file into the project: a file already inside <c>Assets/Scripts</c> is referenced
    /// where it is; any other file is copied in (unique name, the original untouched) and the copy is referenced.
    /// </summary>
    public static bool TryAddExistingScript(string projectRoot, string sourcePath, out ScriptReference? reference, out bool copied, out string? error)
    {
        reference = null;
        copied = false;
        error = null;

        string fullSource;
        try
        {
            fullSource = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            error = "That file path can't be used";
            return false;
        }

        if (!fullSource.EndsWith(ProjectAssetPaths.ScriptExtension, StringComparison.OrdinalIgnoreCase))
        {
            error = "Choose a .lua script file";
            return false;
        }

        if (!File.Exists(fullSource))
        {
            error = "That file no longer exists";
            return false;
        }

        var scriptsRoot = Path.GetFullPath(Path.Combine(projectRoot, ProjectAssetPaths.ScriptsFolder.Replace('/', Path.DirectorySeparatorChar)));
        if (ProjectAssetPaths.IsUnder(fullSource, scriptsRoot)
            && ScriptReference.TryCreate(ProjectAssetPaths.ToAssetPath(projectRoot, fullSource), out var inPlace))
        {
            reference = inPlace;
            return true;
        }

        long length;
        try
        {
            length = new FileInfo(fullSource).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Couldn't read the script ({ex.Message})";
            return false;
        }

        if (length > MaxScriptFileBytes)
        {
            error = "Script file is larger than 1 MB";
            return false;
        }

        copied = TryPlaceUnique(
            projectRoot,
            ToScriptFileName(Path.GetFileNameWithoutExtension(fullSource)),
            temp => File.Copy(fullSource, temp, overwrite: false),
            out reference,
            out error);
        return copied;
    }

    /// <summary>
    /// A portable script file name (without extension) from any file name: Unicode-normalized, characters other
    /// platforms reject replaced with '-', no leading/trailing dots or spaces, not a reserved device name, capped.
    /// Spaces and non-ASCII letters are kept.
    /// </summary>
    public static string ToScriptFileName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            builder.Append(char.IsControl(ch) || Array.IndexOf(NonPortableChars, ch) >= 0 ? '-' : ch);
        }

        var name = builder.ToString().Trim().Trim('.', ' ');
        if (name.Length > MaxScriptNameLength)
        {
            name = name[..MaxScriptNameLength].TrimEnd('.', ' ');
        }

        if (name.Length == 0)
        {
            return "Script";
        }

        return ReservedNames.Contains(name) ? name + "-script" : name;
    }

    // Writes the new file next to its destination under a temporary name, then moves it to the first free
    // "<baseName>.lua" / "<baseName>-N.lua" without overwriting (a name taken in the meantime moves on to the next).
    private static bool TryPlaceUnique(string projectRoot, string baseName, Action<string> writeTemp, out ScriptReference? reference, out string? error)
    {
        reference = null;
        error = null;
        var folder = Path.GetFullPath(Path.Combine(projectRoot, ProjectAssetPaths.ScriptsFolder.Replace('/', Path.DirectorySeparatorChar)));
        var temp = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            writeTemp(temp);
            for (var n = 1; n < 10_000; n++)
            {
                var fileName = (n == 1 ? baseName : $"{baseName}-{n}") + ProjectAssetPaths.ScriptExtension;
                var target = Path.Combine(folder, fileName);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    continue;
                }

                try
                {
                    File.Move(temp, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                {
                    continue;
                }

                if (!ScriptReference.TryCreate(ProjectAssetPaths.ToAssetPath(projectRoot, target), out reference))
                {
                    // Cannot happen for names built here; never leave an unreferenced file behind if it does.
                    TryDelete(target);
                    error = "Couldn't create a valid script path";
                    return false;
                }

                return true;
            }

            error = "No free script file name";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.WriteLine($"[Scripts] Could not write a script into {folder}: {ex.Message}");
            error = ex is UnauthorizedAccessException ? "Permission denied" : ex.Message;
            return false;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Scripts] Could not remove temporary file {path}: {ex.Message}");
        }
    }
}
