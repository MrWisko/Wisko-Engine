namespace Wisko.Engine.ScriptEditor;

/// <summary>
/// A place in a text buffer: zero-based line and zero-based column, the column counted in UTF-16 code units of that
/// line (the buffer never puts a position between the two halves of a surrogate pair).
/// </summary>
public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
{
    public static readonly TextPosition Zero = new(0, 0);

    public int CompareTo(TextPosition other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;

    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;

    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;

    public static TextPosition Min(TextPosition a, TextPosition b) => a <= b ? a : b;

    public static TextPosition Max(TextPosition a, TextPosition b) => a >= b ? a : b;

    public override string ToString() => $"{Line + 1}:{Column + 1}";
}

/// <summary>A span of text from <see cref="Start"/> (inclusive) to <see cref="End"/> (exclusive), Start &lt;= End.</summary>
public readonly record struct TextRange
{
    public TextRange(TextPosition a, TextPosition b)
    {
        Start = TextPosition.Min(a, b);
        End = TextPosition.Max(a, b);
    }

    public TextPosition Start { get; }

    public TextPosition End { get; }

    public bool IsEmpty => Start == End;

    public bool Contains(TextPosition position) => position >= Start && position < End;
}
