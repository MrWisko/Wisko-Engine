using Wisko.Engine.Scripting;

namespace Wisko.Engine.ScriptEditor;

public enum LuaTokenKind : byte
{
    Whitespace,
    Identifier,
    Keyword,

    /// <summary>A Wisko engine global: self, Time, Input.</summary>
    EngineGlobal,

    /// <summary>A standard global or library offered by the sandbox: print, pairs, math, string, ...</summary>
    Builtin,
    Number,
    String,
    Comment,
    Operator
}

/// <summary>A token of one line: kind, start column and length (UTF-16 units).</summary>
public readonly record struct LuaToken(LuaTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Lexer state at a line boundary: plain code, or inside a long string / long comment of a given bracket level
/// (<c>[==[</c> has level 2). Lines are lexed independently from their start state, which is what lets the
/// highlighter re-lex only what an edit affects.
/// </summary>
public readonly record struct LuaLineState(byte Mode, int Level)
{
    public const byte Code = 0;
    public const byte LongString = 1;
    public const byte LongComment = 2;

    public static readonly LuaLineState Start = new(Code, 0);
}

/// <summary>
/// A small Lua 5.4 lexer for highlighting, completion context and indentation: keywords, names, numbers, quoted and
/// long strings, line and long comments, operators. Not a parser; malformed code still lexes (an unterminated quoted
/// string ends at the line end, as Lua would report it).
/// </summary>
public static class LuaLexer
{
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil",
        "not", "or", "repeat", "return", "then", "true", "until", "while"
    };

    public static LuaLineState LexLine(string line, LuaLineState state, List<LuaToken> tokens)
    {
        tokens.Clear();
        var i = 0;
        var afterMemberAccess = false;

        if (state.Mode != LuaLineState.Code)
        {
            var close = FindLongClose(line, 0, state.Level);
            var kind = state.Mode == LuaLineState.LongComment ? LuaTokenKind.Comment : LuaTokenKind.String;
            if (close < 0)
            {
                if (line.Length > 0)
                {
                    tokens.Add(new LuaToken(kind, 0, line.Length));
                }

                return state;
            }

            tokens.Add(new LuaToken(kind, 0, close));
            i = close;
        }

        while (i < line.Length)
        {
            var ch = line[i];
            var start = i;

            if (char.IsWhiteSpace(ch))
            {
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                tokens.Add(new LuaToken(LuaTokenKind.Whitespace, start, i - start));
                continue;
            }

            // Comments: "--[==[ ... ]==]" (may continue on later lines) or "-- ..." to the end of the line.
            if (ch == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                if (TryLongOpen(line, i + 2, out var level, out var bodyStart))
                {
                    var close = FindLongClose(line, bodyStart, level);
                    if (close < 0)
                    {
                        tokens.Add(new LuaToken(LuaTokenKind.Comment, start, line.Length - start));
                        return new LuaLineState(LuaLineState.LongComment, level);
                    }

                    tokens.Add(new LuaToken(LuaTokenKind.Comment, start, close - start));
                    i = close;
                    continue;
                }

                tokens.Add(new LuaToken(LuaTokenKind.Comment, start, line.Length - start));
                return LuaLineState.Start;
            }

            // Long strings "[==[ ... ]==]".
            if (ch == '[' && TryLongOpen(line, i, out var stringLevel, out var stringBody))
            {
                var close = FindLongClose(line, stringBody, stringLevel);
                if (close < 0)
                {
                    tokens.Add(new LuaToken(LuaTokenKind.String, start, line.Length - start));
                    return new LuaLineState(LuaLineState.LongString, stringLevel);
                }

                tokens.Add(new LuaToken(LuaTokenKind.String, start, close - start));
                i = close;
                afterMemberAccess = false;
                continue;
            }

            if (ch is '"' or '\'')
            {
                i++;
                while (i < line.Length && line[i] != ch)
                {
                    i += line[i] == '\\' && i + 1 < line.Length ? 2 : 1;
                }

                if (i < line.Length)
                {
                    i++; // closing quote
                }

                tokens.Add(new LuaToken(LuaTokenKind.String, start, i - start));
                afterMemberAccess = false;
                continue;
            }

            if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1])))
            {
                i = ScanNumber(line, i);
                tokens.Add(new LuaToken(LuaTokenKind.Number, start, i - start));
                afterMemberAccess = false;
                continue;
            }

            if (IsNameStart(ch))
            {
                while (i < line.Length && IsNamePart(line[i]))
                {
                    i++;
                }

                var name = line[start..i];
                var kind = Keywords.Contains(name)
                    ? LuaTokenKind.Keyword
                    : afterMemberAccess
                        ? LuaTokenKind.Identifier
                        : ClassifyGlobal(name);
                tokens.Add(new LuaToken(kind, start, i - start));
                afterMemberAccess = false;
                continue;
            }

            // Operators and punctuation (multi-character ones kept together).
            i += OperatorLength(line, i);
            tokens.Add(new LuaToken(LuaTokenKind.Operator, start, i - start));
            afterMemberAccess = line[start] is '.' or ':' && i - start == 1;
        }

        return LuaLineState.Start;
    }

    public static bool IsNameStart(char ch) => ch == '_' || char.IsAsciiLetter(ch) || (ch > 127 && char.IsLetter(ch));

    public static bool IsNamePart(char ch) => IsNameStart(ch) || char.IsAsciiDigit(ch);

    private static LuaTokenKind ClassifyGlobal(string name) =>
        ScriptApiCatalog.IsEngineGlobal(name)
            ? LuaTokenKind.EngineGlobal
            : ScriptApiCatalog.IsBuiltinGlobal(name)
                ? LuaTokenKind.Builtin
                : LuaTokenKind.Identifier;

    // "[" "="* "[" at index; level = number of '='.
    private static bool TryLongOpen(string line, int index, out int level, out int bodyStart)
    {
        level = 0;
        bodyStart = 0;
        if (index >= line.Length || line[index] != '[')
        {
            return false;
        }

        var i = index + 1;
        while (i < line.Length && line[i] == '=')
        {
            level++;
            i++;
        }

        if (i >= line.Length || line[i] != '[')
        {
            return false;
        }

        bodyStart = i + 1;
        return true;
    }

    // Index just after the matching "]" "="*level "]", or -1.
    private static int FindLongClose(string line, int from, int level)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (line[i] != ']')
            {
                continue;
            }

            var j = i + 1;
            var equals = 0;
            while (j < line.Length && line[j] == '=')
            {
                equals++;
                j++;
            }

            if (equals == level && j < line.Length && line[j] == ']')
            {
                return j + 1;
            }
        }

        return -1;
    }

    private static int ScanNumber(string line, int i)
    {
        if (line[i] == '0' && i + 1 < line.Length && line[i + 1] is 'x' or 'X')
        {
            i += 2;
            while (i < line.Length && (char.IsAsciiHexDigit(line[i]) || line[i] == '.'
                       || (line[i] is 'p' or 'P') || (line[i] is '+' or '-' && line[i - 1] is 'p' or 'P')))
            {
                i++;
            }

            return i;
        }

        while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] == '.'
                   || (line[i] is 'e' or 'E') || (line[i] is '+' or '-' && line[i - 1] is 'e' or 'E')))
        {
            i++;
        }

        return i;
    }

    private static int OperatorLength(string line, int i)
    {
        var remaining = line.Length - i;
        if (remaining >= 3 && string.CompareOrdinal(line, i, "...", 0, 3) == 0)
        {
            return 3;
        }

        if (remaining >= 2)
        {
            var pair = line.AsSpan(i, 2);
            if (pair is "==" or "~=" or "<=" or ">=" or ".." or "::" or "//" or "<<" or ">>")
            {
                return 2;
            }
        }

        // A lone surrogate pair counts as one operator symbol so tokens never split it.
        return char.IsHighSurrogate(line[i]) && remaining >= 2 && char.IsLowSurrogate(line[i + 1]) ? 2 : 1;
    }
}

/// <summary>
/// Cached lexing of a <see cref="TextBuffer"/>: tokens and boundary states per line, re-lexed lazily from the first
/// edited line and only as far as needed. Re-lexing stops early when a line's start state comes out unchanged and the
/// line itself was not edited, because everything after it is then still valid.
/// </summary>
public sealed class LuaHighlighter
{
    private readonly TextBuffer _buffer;
    private readonly List<LineEntry> _lines = [];
    private readonly List<LuaToken> _scratch = [];
    private int _validCount;

    public LuaHighlighter(TextBuffer buffer)
    {
        _buffer = buffer;
        for (var i = 0; i < buffer.LineCount; i++)
        {
            _lines.Add(new LineEntry());
        }

        buffer.Changed += OnChanged;
    }

    /// <summary>Lines lexed since creation (performance diagnostics: shows how much work edits cause).</summary>
    public long LinesLexed { get; private set; }

    /// <summary>The tokens of a line (lexing it and any unlexed lines above it first).</summary>
    public IReadOnlyList<LuaToken> GetTokens(int line)
    {
        EnsureValid(line);
        return _lines[line].Tokens;
    }

    /// <summary>The lexer state at the start of a line (whether it begins inside a long string or comment).</summary>
    public LuaLineState GetStartState(int line)
    {
        EnsureValid(line);
        return _lines[line].StartState;
    }

    private void OnChanged(int startLine, int removedLines, int insertedLines)
    {
        _lines.RemoveRange(startLine, removedLines);
        var inserted = new LineEntry[insertedLines];
        for (var i = 0; i < insertedLines; i++)
        {
            inserted[i] = new LineEntry();
        }

        _lines.InsertRange(startLine, inserted);
        _validCount = global::System.Math.Min(_validCount, startLine);
    }

    private void EnsureValid(int line)
    {
        if (line < _validCount)
        {
            return;
        }

        var state = _validCount == 0 ? LuaLineState.Start : _lines[_validCount - 1].EndState;
        for (var i = _validCount; i <= line; i++)
        {
            var entry = _lines[i];
            if (!entry.Dirty && entry.StartState == state)
            {
                // Untouched line with the same start state: it and everything after it still hold.
                _validCount = FirstDirtyAfter(i);
                if (line < _validCount)
                {
                    return;
                }

                i = _validCount - 1;
                state = _lines[i].EndState;
                continue;
            }

            entry.StartState = state;
            state = LuaLexer.LexLine(_buffer.GetLine(i), state, _scratch);
            entry.Tokens = _scratch.ToArray();
            entry.EndState = state;
            entry.Dirty = false;
            LinesLexed++;
            _validCount = i + 1;
        }
    }

    // The first line at or after `from` that still needs lexing (edited lines), or the line count.
    private int FirstDirtyAfter(int from)
    {
        for (var i = from; i < _lines.Count; i++)
        {
            if (_lines[i].Dirty)
            {
                return i;
            }
        }

        return _lines.Count;
    }

    private sealed class LineEntry
    {
        public LuaLineState StartState;
        public LuaLineState EndState;
        public LuaToken[] Tokens = [];
        public bool Dirty = true;
    }
}
