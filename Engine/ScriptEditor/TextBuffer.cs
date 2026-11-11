using System.Text;

namespace Wisko.Engine.ScriptEditor;

/// <summary>
/// The text of one document as a list of lines (without their line breaks). An edit rebuilds only the lines it
/// touches, so typing costs the length of the current line, not of the document; inserting or removing lines splices
/// the line list. Line breaks are always "\n" inside the buffer; the file's own style is applied on save.
/// Positions are (line, UTF-16 column) and are kept off the middle of surrogate pairs.
/// </summary>
public sealed class TextBuffer
{
    private readonly List<string> _lines = [string.Empty];

    /// <summary>
    /// Raised after every edit: the lines [startLine, startLine + removedLines) were replaced by insertedLines new
    /// lines starting at startLine (an in-line edit is (line, 1, 1)).
    /// </summary>
    public event Action<int, int, int>? Changed;

    public int LineCount => _lines.Count;

    /// <summary>Increases on every change (cheap "has the text changed since" checks).</summary>
    public long Version { get; private set; }

    /// <summary>Total characters including one per line break (diagnostics and limits).</summary>
    public long Length
    {
        get
        {
            long length = _lines.Count - 1;
            foreach (var line in _lines)
            {
                length += line.Length;
            }

            return length;
        }
    }

    public TextPosition End => new(_lines.Count - 1, _lines[^1].Length);

    public string GetLine(int line) => _lines[line];

    public int GetLineLength(int line) => _lines[line].Length;

    /// <summary>Replaces the whole text (any line-break style accepted).</summary>
    public void SetText(string text)
    {
        var removed = _lines.Count;
        _lines.Clear();
        _lines.AddRange(SplitLines(text));
        Version++;
        Changed?.Invoke(0, removed, _lines.Count);
    }

    /// <summary>The whole text with <paramref name="newline"/> between lines.</summary>
    public string GetText(string newline = "\n") => string.Join(newline, _lines);

    public string GetText(TextRange range)
    {
        var start = Clamp(range.Start);
        var end = Clamp(range.End);
        if (start.Line == end.Line)
        {
            return _lines[start.Line][start.Column..end.Column];
        }

        var builder = new StringBuilder();
        builder.Append(_lines[start.Line], start.Column, _lines[start.Line].Length - start.Column);
        for (var line = start.Line + 1; line < end.Line; line++)
        {
            builder.Append('\n').Append(_lines[line]);
        }

        builder.Append('\n').Append(_lines[end.Line], 0, end.Column);
        return builder.ToString();
    }

    /// <summary>Inserts text (line breaks of any style) and returns the position just after it.</summary>
    public TextPosition Insert(TextPosition position, string text) => Replace(new TextRange(position, position), text);

    public void Delete(TextRange range) => Replace(range, string.Empty);

    /// <summary>Replaces a range with text and returns the position just after the inserted text.</summary>
    public TextPosition Replace(TextRange range, string text)
    {
        var start = Clamp(range.Start);
        var end = Clamp(range.End);
        var before = _lines[start.Line][..start.Column];
        var after = _lines[end.Line][end.Column..];
        var removedLines = end.Line - start.Line + 1;

        var parts = SplitLines(text);
        TextPosition result;
        if (parts.Count == 1)
        {
            _lines[start.Line] = before + parts[0] + after;
            if (removedLines > 1)
            {
                _lines.RemoveRange(start.Line + 1, removedLines - 1);
            }

            result = new TextPosition(start.Line, before.Length + parts[0].Length);
        }
        else
        {
            var newLines = new string[parts.Count];
            newLines[0] = before + parts[0];
            for (var i = 1; i < parts.Count - 1; i++)
            {
                newLines[i] = parts[i];
            }

            newLines[^1] = parts[^1] + after;
            _lines[start.Line] = newLines[0];
            if (removedLines > 1)
            {
                _lines.RemoveRange(start.Line + 1, removedLines - 1);
            }

            _lines.InsertRange(start.Line + 1, newLines.Skip(1));
            result = new TextPosition(start.Line + parts.Count - 1, parts[^1].Length);
        }

        Version++;
        Changed?.Invoke(start.Line, removedLines, parts.Count);
        return result;
    }

    /// <summary>The nearest valid position: inside the buffer and never between the halves of a surrogate pair.</summary>
    public TextPosition Clamp(TextPosition position)
    {
        var line = System.Math.Clamp(position.Line, 0, _lines.Count - 1);
        var text = _lines[line];
        var column = System.Math.Clamp(position.Column, 0, text.Length);
        if (column > 0 && column < text.Length && char.IsLowSurrogate(text[column]) && char.IsHighSurrogate(text[column - 1]))
        {
            column--;
        }

        return new TextPosition(line, column);
    }

    /// <summary>The position one character (one code point, or a line break) to the left.</summary>
    public TextPosition PreviousPosition(TextPosition position)
    {
        position = Clamp(position);
        if (position.Column == 0)
        {
            return position.Line == 0 ? position : new TextPosition(position.Line - 1, _lines[position.Line - 1].Length);
        }

        var text = _lines[position.Line];
        var column = position.Column - 1;
        if (column > 0 && char.IsLowSurrogate(text[column]) && char.IsHighSurrogate(text[column - 1]))
        {
            column--;
        }

        return new TextPosition(position.Line, column);
    }

    /// <summary>The position one character (one code point, or a line break) to the right.</summary>
    public TextPosition NextPosition(TextPosition position)
    {
        position = Clamp(position);
        var text = _lines[position.Line];
        if (position.Column >= text.Length)
        {
            return position.Line >= _lines.Count - 1 ? position : new TextPosition(position.Line + 1, 0);
        }

        var column = position.Column + 1;
        if (column < text.Length && char.IsLowSurrogate(text[column]) && char.IsHighSurrogate(text[column - 1]))
        {
            column++;
        }

        return new TextPosition(position.Line, column);
    }

    /// <summary>"\r\n", "\n" and "\r" all end a line.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\n' && ch != '\r')
            {
                continue;
            }

            lines.Add(text[start..i]);
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }
}
