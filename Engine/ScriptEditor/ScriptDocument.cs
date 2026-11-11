using Wisko.Engine.Assets;
using Wisko.Engine.World;

namespace Wisko.Engine.ScriptEditor;

/// <summary>
/// One open Lua script: its project identity, the text with its own undo history and lexer cache, the editing view
/// (caret, selection, scroll), the last known disk state and the latest syntax check. Pure editor state: nothing here
/// is stored in the scene, the script reference or the Lua runtime.
/// </summary>
public sealed class ScriptDocument
{
    private long _savedStateId;

    internal ScriptDocument(ScriptReference reference, string fullPath, LoadedScript loaded)
    {
        Reference = reference;
        FullPath = fullPath;
        Buffer.SetText(loaded.Text);
        Highlighter = new LuaHighlighter(Buffer);
        Newline = loaded.Newline;
        HasBom = loaded.HasBom;
        HadInvalidUtf8 = loaded.HadInvalidUtf8;
        Baseline = loaded.Baseline;
        Buffer.Changed += (_, _, _) => DiagnosticsStale = true;
    }

    public ScriptReference Reference { get; }

    /// <summary>The file on disk (inside the project's Assets/Scripts).</summary>
    public string FullPath { get; }

    public string FileName => Path.GetFileName(Reference.AssetPath);

    /// <summary>Path inside Assets/Scripts (usually just the file name).</summary>
    public string Label =>
        Reference.AssetPath.StartsWith(ProjectAssetPaths.ScriptsFolder + "/", StringComparison.Ordinal)
            ? Reference.AssetPath[(ProjectAssetPaths.ScriptsFolder.Length + 1)..]
            : Reference.AssetPath;

    public TextBuffer Buffer { get; } = new();

    public LuaHighlighter Highlighter { get; }

    public TextEditHistory History { get; } = new();

    /// <summary>"\n" or "\r\n": the file's line breaks, used again on save.</summary>
    public string Newline { get; private set; }

    public bool HasBom { get; private set; }

    /// <summary>The file had invalid UTF-8 (replaced on load); saving writes valid UTF-8.</summary>
    public bool HadInvalidUtf8 { get; private set; }

    public ScriptFileBaseline Baseline { get; internal set; }

    /// <summary>The disk version the user chose to overwrite with the editor version (saving no longer refuses).</summary>
    public string? AcknowledgedDiskHash { get; internal set; }

    /// <summary>A disk version the user was already asked about and dismissed (no repeated prompt; saving still asks).</summary>
    public string? DismissedDiskHash { get; internal set; }

    public bool IsDirty => History.CurrentStateId != _savedStateId;

    public TextPosition Caret { get; private set; }

    /// <summary>The other end of the selection (equal to <see cref="Caret"/> when nothing is selected).</summary>
    public TextPosition Anchor { get; private set; }

    public bool HasSelection => Caret != Anchor;

    public TextRange Selection => new(Anchor, Caret);

    /// <summary>Column (in visual cells) vertical caret moves try to keep; -1 when not set.</summary>
    public int PreferredVisualColumn { get; set; } = -1;

    public float ScrollX { get; set; }

    public float ScrollY { get; set; }

    /// <summary>Latest syntax check result (empty when the source compiles).</summary>
    public IReadOnlyList<SyntaxDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>The text changed since the last syntax check.</summary>
    public bool DiagnosticsStale { get; private set; } = true;

    /// <summary>Editor clock time of the last edit (debounced validation).</summary>
    public double LastEditTime { get; private set; }

    /// <summary>Saved while Play was running: the running scripts still use the code they started with.</summary>
    public bool SavedDuringPlay { get; set; }

    public void SetCaret(TextPosition position, bool extendSelection = false)
    {
        Caret = Buffer.Clamp(position);
        if (!extendSelection)
        {
            Anchor = Caret;
        }
    }

    public void Select(TextPosition anchor, TextPosition caret)
    {
        Anchor = Buffer.Clamp(anchor);
        Caret = Buffer.Clamp(caret);
    }

    public CaretState CaretState => new(Caret, Anchor);

    /// <summary>Replaces a range with text as one recorded edit; the caret ends after the inserted text.</summary>
    public void Edit(TextEditKind kind, TextRange range, string text, double time)
    {
        var before = CaretState;
        var start = Buffer.Clamp(range.Start);
        var removed = Buffer.GetText(new TextRange(start, Buffer.Clamp(range.End)));
        var end = Buffer.Replace(new TextRange(start, range.End), text);
        Caret = Anchor = end;
        PreferredVisualColumn = -1;
        History.Record(kind, new TextChange(start, removed, Normalize(text)), before, CaretState, time);
        LastEditTime = time;
    }

    /// <summary>
    /// Several replacements recorded as one undo step, applied in order (each range in the coordinates left by the
    /// previous ones), then the caret/selection set to <paramref name="after"/>.
    /// </summary>
    public void EditGroup(TextEditKind kind, IReadOnlyList<(TextRange Range, string Text)> edits, CaretState after, double time)
    {
        var before = CaretState;
        var changes = new List<TextChange>(edits.Count);
        foreach (var (range, text) in edits)
        {
            var start = Buffer.Clamp(range.Start);
            var removed = Buffer.GetText(new TextRange(start, Buffer.Clamp(range.End)));
            if (removed.Length == 0 && text.Length == 0)
            {
                continue;
            }

            Buffer.Replace(new TextRange(start, range.End), text);
            changes.Add(new TextChange(start, removed, Normalize(text)));
        }

        Select(after.Anchor, after.Caret);
        PreferredVisualColumn = -1;
        History.RecordGroup(kind, changes, before, CaretState, time);
        LastEditTime = time;
    }

    public bool Undo(double time)
    {
        if (!History.TryUndo(Buffer, out var caret))
        {
            return false;
        }

        Select(caret.Anchor, caret.Caret);
        LastEditTime = time;
        return true;
    }

    public bool Redo(double time)
    {
        if (!History.TryRedo(Buffer, out var caret))
        {
            return false;
        }

        Select(caret.Anchor, caret.Caret);
        LastEditTime = time;
        return true;
    }

    public void SetDiagnostics(IReadOnlyList<SyntaxDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        DiagnosticsStale = false;
    }

    /// <summary>The current text was written to disk: it is the clean state now.</summary>
    internal void MarkSaved(ScriptFileBaseline baseline)
    {
        Baseline = baseline;
        AcknowledgedDiskHash = null;
        DismissedDiskHash = null;
        _savedStateId = History.CurrentStateId;
        History.SealCurrent();
    }

    /// <summary>Replaces the text with the disk version (clean, history cleared), keeping the caret near where it was.</summary>
    internal void Reload(LoadedScript loaded)
    {
        var caret = Caret;
        History.Clear();
        Buffer.SetText(loaded.Text);
        Newline = loaded.Newline;
        HasBom = loaded.HasBom;
        HadInvalidUtf8 = loaded.HadInvalidUtf8;
        Baseline = loaded.Baseline;
        AcknowledgedDiskHash = null;
        DismissedDiskHash = null;
        _savedStateId = History.CurrentStateId;
        SetCaret(caret);
    }

    private static string Normalize(string text) =>
        text.Contains('\r') ? string.Join('\n', TextBuffer.SplitLines(text)) : text;
}
