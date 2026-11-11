using Wisko.Engine.Assets;
using Wisko.Engine.World;

namespace Wisko.Engine.ScriptEditor;

public enum ScriptSaveResult
{
    Saved,
    Failed,

    /// <summary>The file changed on disk since it was opened; not overwritten.</summary>
    Conflict
}

/// <summary>A file that changed on disk since the editor last read or wrote it.</summary>
public readonly record struct ExternalScriptChange(ScriptDocument Document, ScriptFileBaseline Disk);

/// <summary>
/// The open script documents of the current project, in tab order. Opens each project script at most once, saves
/// through <see cref="ScriptFileStore"/>, and detects files changed by other programs (size/time stamp first, content
/// hash to confirm) without ever overwriting such a change silently.
/// </summary>
public sealed class ScriptDocumentManager
{
    private readonly List<ScriptDocument> _documents = [];

    public string? ProjectRoot { get; private set; }

    public IReadOnlyList<ScriptDocument> Documents => _documents;

    public IEnumerable<ScriptDocument> DirtyDocuments => _documents.Where(document => document.IsDirty);

    /// <summary>Switches project; all documents are closed (unsaved changes must have been handled first).</summary>
    public void SetProject(string? projectRoot)
    {
        _documents.Clear();
        ProjectRoot = projectRoot;
    }

    public ScriptDocument? Find(ScriptReference reference) =>
        _documents.FirstOrDefault(document => string.Equals(document.Reference.AssetPath, reference.AssetPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The open document for the script, or a newly opened one (appended to the tabs).</summary>
    public bool TryOpen(ScriptReference reference, out ScriptDocument? document, out string? error)
    {
        error = null;
        document = Find(reference);
        if (document is not null)
        {
            return true;
        }

        if (ProjectRoot is null || !ProjectAssetPaths.TryResolve(ProjectRoot, reference.AssetPath, out var fullPath))
        {
            error = "The script is not inside this project";
            return false;
        }

        if (!ScriptFileStore.TryLoad(fullPath, out var loaded, out error) || loaded is null)
        {
            return false;
        }

        document = new ScriptDocument(reference, fullPath, loaded);
        _documents.Add(document);
        Console.WriteLine($"[ScriptEditor] Opened {reference.AssetPath} ({document.Buffer.LineCount} lines)");
        return true;
    }

    public void Close(ScriptDocument document) => _documents.Remove(document);

    /// <summary>
    /// Writes the document atomically. Refuses (Conflict) when the file changed on disk since it was read, unless
    /// <paramref name="overwriteExternalChanges"/> or the user already chose to keep the editor version.
    /// </summary>
    public ScriptSaveResult Save(ScriptDocument document, out string? error, bool overwriteExternalChanges = false)
    {
        error = null;
        if (!overwriteExternalChanges)
        {
            var disk = ScriptFileStore.ReadStat(document.FullPath);
            if (!disk.SameStat(document.Baseline) && disk.Exists)
            {
                var hashed = ScriptFileStore.ReadBaseline(document.FullPath);
                if (hashed.Hash != document.Baseline.Hash && hashed.Hash != document.AcknowledgedDiskHash)
                {
                    error = $"{document.FileName} changed on disk";
                    return ScriptSaveResult.Conflict;
                }
            }
        }

        if (!ScriptFileStore.TrySave(document.FullPath, document.Buffer.GetText(), document.Newline, document.HasBom, out var baseline, out error))
        {
            return ScriptSaveResult.Failed;
        }

        document.MarkSaved(baseline);
        Console.WriteLine($"[ScriptEditor] Saved {document.Reference.AssetPath}");
        return ScriptSaveResult.Saved;
    }

    /// <summary>Documents whose file changed on disk (content differs from what the editor knows) since the last check.</summary>
    public List<ExternalScriptChange> DetectExternalChanges()
    {
        var changes = new List<ExternalScriptChange>();
        foreach (var document in _documents)
        {
            var stat = ScriptFileStore.ReadStat(document.FullPath);
            if (stat.SameStat(document.Baseline))
            {
                continue;
            }

            var disk = stat.Exists ? ScriptFileStore.ReadBaseline(document.FullPath) : ScriptFileBaseline.Missing;
            if (disk.Exists && disk.Hash == document.Baseline.Hash)
            {
                // Touched but identical (e.g. saved unchanged elsewhere): remember the new time stamp only.
                document.Baseline = disk;
                continue;
            }

            if (disk.Exists && (disk.Hash == document.AcknowledgedDiskHash || disk.Hash == document.DismissedDiskHash))
            {
                continue;
            }

            if (!disk.Exists && !document.Baseline.Exists)
            {
                continue;
            }

            changes.Add(new ExternalScriptChange(document, disk));
        }

        return changes;
    }

    /// <summary>Loads the disk version into the document (discarding its unsaved edits).</summary>
    public bool TryReload(ScriptDocument document, out string? error)
    {
        if (!ScriptFileStore.TryLoad(document.FullPath, out var loaded, out error) || loaded is null)
        {
            return false;
        }

        document.Reload(loaded);
        Console.WriteLine($"[ScriptEditor] Reloaded {document.Reference.AssetPath} from disk");
        return true;
    }

    /// <summary>Asked about this disk version and left undecided: no repeated prompt for it; saving still refuses.</summary>
    public static void DismissExternalChange(ScriptDocument document, ScriptFileBaseline disk) =>
        document.DismissedDiskHash = disk.Exists ? disk.Hash : null;

    /// <summary>Keep editing over this disk version: no further prompt for it; the next save overwrites it.</summary>
    public static void KeepEditorVersion(ScriptDocument document, ScriptFileBaseline disk)
    {
        document.AcknowledgedDiskHash = disk.Exists ? disk.Hash : null;
        document.Baseline = disk.Exists ? disk : document.Baseline with { Exists = false };
    }
}
