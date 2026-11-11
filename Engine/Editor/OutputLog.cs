namespace Wisko.Engine.Editor;

public enum OutputKind
{
    Info,
    Warning,
    Error,

    /// <summary>A divider, e.g. the start of a Play session.</summary>
    Separator
}

/// <summary>
/// One Output row: message plus, when it came from a script, the script's project path, the line and the object it
/// runs on (so the row can open the script at that line).
/// </summary>
public sealed record OutputEntry(OutputKind Kind, string Message, string? ScriptPath = null, int? Line = null, string? ObjectName = null)
{
    public DateTime Time { get; init; } = DateTime.Now;

    public bool HasSource => ScriptPath is not null;
}

/// <summary>
/// The editor session's structured output (Lua print, script warnings and errors, Play markers). Bounded: at most
/// <see cref="MaxEntries"/> rows and about <see cref="MaxCharacters"/> characters; the oldest rows go first, so print
/// spam cannot grow editor memory without limit. Console/AppLog logging is separate and unchanged.
/// </summary>
public sealed class OutputLog
{
    public const int MaxEntries = 5000;
    public const long MaxCharacters = 2_000_000;

    private readonly OutputEntry[] _ring = new OutputEntry[MaxEntries];
    private int _start;
    private long _characters;

    public int Count { get; private set; }

    /// <summary>Changes on every add/clear (views refresh when it moves).</summary>
    public long Version { get; private set; }

    /// <summary>Rows discarded to stay within the bounds since the last clear.</summary>
    public long Dropped { get; private set; }

    public int ErrorCount { get; private set; }

    public long Characters => _characters;

    public OutputEntry this[int index] => _ring[(_start + index) % MaxEntries];

    public void Add(OutputEntry entry)
    {
        while (Count > 0 && (Count >= MaxEntries || _characters + entry.Message.Length > MaxCharacters))
        {
            RemoveOldest();
            Dropped++;
        }

        _ring[(_start + Count) % MaxEntries] = entry;
        Count++;
        _characters += entry.Message.Length;
        if (entry.Kind == OutputKind.Error)
        {
            ErrorCount++;
        }

        Version++;
    }

    public void Clear()
    {
        Array.Clear(_ring);
        _start = 0;
        Count = 0;
        _characters = 0;
        ErrorCount = 0;
        Dropped = 0;
        Version++;
    }

    private void RemoveOldest()
    {
        var oldest = _ring[_start];
        _ring[_start] = null!;
        _start = (_start + 1) % MaxEntries;
        Count--;
        _characters -= oldest.Message.Length;
        if (oldest.Kind == OutputKind.Error)
        {
            ErrorCount--;
        }
    }
}
