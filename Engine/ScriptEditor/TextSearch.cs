namespace Wisko.Engine.ScriptEditor;

/// <summary>
/// Plain-text find within one document (no regular expressions): all matches line by line, recomputed only when the
/// query, the case option or the text changes. Ordinal comparison on UTF-16 text, so any Unicode query matches
/// exactly (case-insensitive mode uses invariant case mapping).
/// </summary>
public sealed class TextSearch
{
    /// <summary>Matches beyond this many are not listed (the count shows the cap).</summary>
    public const int MaxMatches = 10_000;

    private readonly List<TextRange> _matches = [];
    private string _searchedQuery = string.Empty;
    private bool _searchedCaseSensitive;
    private long _searchedVersion = -1;
    private TextBuffer? _searchedBuffer;

    public string Query { get; set; } = string.Empty;

    public bool CaseSensitive { get; set; }

    public IReadOnlyList<TextRange> Matches => _matches;

    /// <summary>Index of the current match, or -1.</summary>
    public int Current { get; private set; } = -1;

    public bool IsCapped => _matches.Count >= MaxMatches;

    /// <summary>Recomputes the matches when anything they depend on changed.</summary>
    public void Refresh(TextBuffer buffer)
    {
        if (ReferenceEquals(buffer, _searchedBuffer) && buffer.Version == _searchedVersion
            && Query == _searchedQuery && CaseSensitive == _searchedCaseSensitive)
        {
            return;
        }

        _searchedBuffer = buffer;
        _searchedVersion = buffer.Version;
        _searchedQuery = Query;
        _searchedCaseSensitive = CaseSensitive;
        _matches.Clear();
        Current = -1;
        if (Query.Length == 0 || Query.Contains('\n'))
        {
            return;
        }

        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var line = 0; line < buffer.LineCount && _matches.Count < MaxMatches; line++)
        {
            var text = buffer.GetLine(line);
            var index = text.IndexOf(Query, comparison);
            while (index >= 0 && _matches.Count < MaxMatches)
            {
                _matches.Add(new TextRange(new TextPosition(line, index), new TextPosition(line, index + Query.Length)));
                index = text.IndexOf(Query, index + System.Math.Max(1, Query.Length), comparison);
            }
        }
    }

    /// <summary>Selects the first match at or after <paramref name="from"/> (wrapping); false when there is none.</summary>
    public bool SelectFrom(TextPosition from)
    {
        if (_matches.Count == 0)
        {
            Current = -1;
            return false;
        }

        Current = 0;
        for (var i = 0; i < _matches.Count; i++)
        {
            if (_matches[i].Start >= from)
            {
                Current = i;
                return true;
            }
        }

        return true;
    }

    public bool Next()
    {
        if (_matches.Count == 0)
        {
            return false;
        }

        Current = (Current + 1) % _matches.Count;
        return true;
    }

    public bool Previous()
    {
        if (_matches.Count == 0)
        {
            return false;
        }

        Current = Current <= 0 ? _matches.Count - 1 : Current - 1;
        return true;
    }
}

/// <summary>
/// Conversions between buffer columns (UTF-16 units) and visual columns (character cells): a tab advances to the next
/// tab stop and a surrogate pair takes one cell.
/// </summary>
public static class TextColumns
{
    public const int TabSize = 4;

    public static int VisualColumn(string line, int column)
    {
        var visual = 0;
        var end = System.Math.Min(column, line.Length);
        for (var i = 0; i < end; i++)
        {
            var ch = line[i];
            if (ch == '\t')
            {
                visual += TabSize - visual % TabSize;
            }
            else if (!char.IsLowSurrogate(ch))
            {
                visual++;
            }
        }

        return visual;
    }

    /// <summary>The column whose cell is nearest to <paramref name="visual"/> (rounding to the closer edge).</summary>
    public static int ColumnAtVisual(string line, float visual)
    {
        var current = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (char.IsLowSurrogate(ch))
            {
                continue;
            }

            var width = ch == '\t' ? TabSize - current % TabSize : 1;
            if (visual < current + width * 0.5f)
            {
                return i;
            }

            current += width;
        }

        return line.Length;
    }

    /// <summary>Visual width of the line's leading whitespace.</summary>
    public static int IndentWidth(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] is ' ' or '\t')
        {
            count++;
        }

        return VisualColumn(line, count);
    }

    public static int LeadingWhitespaceLength(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] is ' ' or '\t')
        {
            count++;
        }

        return count;
    }
}
