namespace Wisko.Engine.ScriptEditor;

/// <summary>What produced an edit; consecutive edits of the same kind may be grouped into one undo step.</summary>
public enum TextEditKind
{
    Other,
    Typing,
    Backspace,
    DeleteForward,
    NewLine,
    Paste,
    Cut,
    Indent,
    Completion
}

/// <summary>One replacement: <see cref="Removed"/> at <see cref="Start"/> became <see cref="Inserted"/> (both "\n" text).</summary>
public readonly record struct TextChange(TextPosition Start, string Removed, string Inserted)
{
    public TextPosition InsertedEnd => EndOf(Start, Inserted);

    public TextPosition RemovedEnd => EndOf(Start, Removed);

    /// <summary>The position after <paramref name="text"/> when it is placed at <paramref name="start"/>.</summary>
    public static TextPosition EndOf(TextPosition start, string text)
    {
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            return new TextPosition(start.Line, start.Column + text.Length);
        }

        var breaks = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                breaks++;
            }
        }

        return new TextPosition(start.Line + breaks, text.Length - lastBreak - 1);
    }
}

/// <summary>Caret and selection anchor (equal when nothing is selected).</summary>
public readonly record struct CaretState(TextPosition Caret, TextPosition Anchor);

/// <summary>
/// Undo/redo for one document's text, independent of the scene's editor history. Typing, Backspace and Delete runs
/// are grouped (contiguous, within a short pause, split at word boundaries) so one undo removes a word, not a letter
/// and not a whole session. Every step has an id; the document is dirty when the current step is not the one it
/// was saved at.
/// </summary>
public sealed class TextEditHistory
{
    private const int MaxSteps = 1000;
    private const double GroupingSeconds = 1.5;

    private readonly List<Step> _steps = [];
    private int _applied;
    private long _nextId = 1;

    public int UndoCount => _applied;

    public int RedoCount => _steps.Count - _applied;

    /// <summary>Identifies the current text state (0 = as loaded).</summary>
    public long CurrentStateId => _applied == 0 ? 0 : _steps[_applied - 1].Id;

    public void Clear()
    {
        _steps.Clear();
        _applied = 0;
    }

    /// <summary>No later edit may be merged into the current step (e.g. it is the saved state).</summary>
    public void SealCurrent()
    {
        if (_applied > 0)
        {
            _steps[_applied - 1].Sealed = true;
        }
    }

    /// <summary>Records an applied change, grouping it into the previous step where that reads as one action.</summary>
    public void Record(TextEditKind kind, TextChange change, CaretState before, CaretState after, double time)
    {
        if (_applied < _steps.Count)
        {
            _steps.RemoveRange(_applied, _steps.Count - _applied);
        }

        if (_applied > 0 && TryMerge(_steps[_applied - 1], kind, change, after, time))
        {
            return;
        }

        _steps.Add(new Step(_nextId++, kind, [change], before, after, time));
        _applied = _steps.Count;
        if (_steps.Count > MaxSteps)
        {
            _steps.RemoveAt(0);
            _applied--;
        }
    }

    /// <summary>Records several applied changes (in application order) as one step.</summary>
    public void RecordGroup(TextEditKind kind, IReadOnlyList<TextChange> changes, CaretState before, CaretState after, double time)
    {
        if (changes.Count == 0)
        {
            return;
        }

        if (_applied < _steps.Count)
        {
            _steps.RemoveRange(_applied, _steps.Count - _applied);
        }

        _steps.Add(new Step(_nextId++, kind, [.. changes], before, after, time) { Sealed = true });
        _applied = _steps.Count;
        if (_steps.Count > MaxSteps)
        {
            _steps.RemoveAt(0);
            _applied--;
        }
    }

    /// <summary>Reverts the last step on <paramref name="buffer"/>; returns the caret state to restore.</summary>
    public bool TryUndo(TextBuffer buffer, out CaretState caret)
    {
        caret = default;
        if (_applied == 0)
        {
            return false;
        }

        var step = _steps[--_applied];
        for (var i = step.Changes.Count - 1; i >= 0; i--)
        {
            var change = step.Changes[i];
            buffer.Replace(new TextRange(change.Start, change.InsertedEnd), change.Removed);
        }

        step.Sealed = true;
        caret = step.Before;
        return true;
    }

    public bool TryRedo(TextBuffer buffer, out CaretState caret)
    {
        caret = default;
        if (_applied >= _steps.Count)
        {
            return false;
        }

        var step = _steps[_applied++];
        foreach (var change in step.Changes)
        {
            buffer.Replace(new TextRange(change.Start, change.RemovedEnd), change.Inserted);
        }

        caret = step.After;
        return true;
    }

    private static bool TryMerge(Step top, TextEditKind kind, TextChange change, CaretState after, double time)
    {
        if (top.Sealed || top.Kind != kind || top.Changes.Count != 1 || time - top.Time > GroupingSeconds)
        {
            return false;
        }

        var last = top.Changes[0];
        TextChange merged;
        switch (kind)
        {
            case TextEditKind.Typing:
                // Contiguous single-line typing; a new word (whitespace after text) starts a new step.
                if (change.Removed.Length != 0 || change.Inserted.Contains('\n') || change.Start != last.InsertedEnd
                    || (IsWhitespace(change.Inserted) && last.Inserted.Length > 0 && !char.IsWhiteSpace(last.Inserted[^1])))
                {
                    return false;
                }

                merged = last with { Inserted = last.Inserted + change.Inserted };
                break;

            case TextEditKind.Backspace:
                if (change.Inserted.Length != 0 || last.Inserted.Length != 0 || change.RemovedEnd != last.Start)
                {
                    return false;
                }

                merged = new TextChange(change.Start, change.Removed + last.Removed, string.Empty);
                break;

            case TextEditKind.DeleteForward:
                if (change.Inserted.Length != 0 || last.Inserted.Length != 0 || change.Start != last.Start)
                {
                    return false;
                }

                merged = last with { Removed = last.Removed + change.Removed };
                break;

            default:
                return false;
        }

        top.Changes[0] = merged;
        top.After = after;
        top.Time = time;
        return true;
    }

    private static bool IsWhitespace(string text) => text.Length > 0 && text.All(char.IsWhiteSpace);

    private sealed class Step(long id, TextEditKind kind, List<TextChange> changes, CaretState before, CaretState after, double time)
    {
        public long Id { get; } = id;

        public TextEditKind Kind { get; } = kind;

        public List<TextChange> Changes { get; } = changes;

        public CaretState Before { get; } = before;

        public CaretState After { get; set; } = after;

        public double Time { get; set; } = time;

        public bool Sealed { get; set; }
    }
}
