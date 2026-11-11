using Wisko.Engine.Input;
using Wisko.Engine.Platform;

namespace Wisko.Engine.ScriptEditor;

/// <summary>What a key press asked the host (the editor view) to do beyond editing the text.</summary>
public enum CodeEditorCommand
{
    /// <summary>Not a code editor key.</summary>
    None,

    /// <summary>Handled here (text or caret changed, or deliberately ignored).</summary>
    Handled,
    Save,
    OpenFind,
    FindNext,
    FindPrevious,
    TriggerCompletion
}

/// <summary>
/// The text-editing behaviour of the code editor, independent of rendering and of the platform: caret movement and
/// selection, typing, Lua-aware indentation, clipboard, undo/redo and mouse selection on a
/// <see cref="ScriptDocument"/>. Keys arrive as engine <see cref="EditorKeyEvent"/>s and are interpreted with the
/// platform's <see cref="ShortcutConvention"/> (Cmd vs Ctrl), so this class never checks the operating system.
/// Typed characters arrive separately as text (the OS text-input event), never derived from key codes.
/// </summary>
public sealed class CodeEditorController(ShortcutConvention conventions, IClipboard? clipboard)
{
    public const int IndentSize = 4;

    private static readonly string Indent = new(' ', IndentSize);
    private static readonly HashSet<string> BlockOpeners = new(StringComparer.Ordinal) { "function", "do", "then", "repeat" };
    private static readonly HashSet<string> BlockClosers = new(StringComparer.Ordinal) { "end", "until" };
    private static readonly HashSet<string> DedentWords = new(StringComparer.Ordinal) { "end", "else", "elseif", "until" };

    public ShortcutConvention Conventions { get; } = conventions;

    // ---- Keys ----

    /// <summary>
    /// Applies a navigation/editing/shortcut key. <paramref name="pageLines"/> is how many lines one page holds.
    /// Escape is never handled here (the host closes popups or stops Play with it).
    /// </summary>
    public CodeEditorCommand HandleKey(ScriptDocument document, EditorKeyEvent key, int pageLines, double time)
    {
        var primary = Conventions.IsPrimary(key);
        var shift = key.Shift;

        if (primary)
        {
            switch (key.Key)
            {
                case EditorKey.A:
                    document.Select(TextPosition.Zero, document.Buffer.End);
                    return CodeEditorCommand.Handled;
                case EditorKey.C:
                    Copy(document);
                    return CodeEditorCommand.Handled;
                case EditorKey.X:
                    Cut(document, time);
                    return CodeEditorCommand.Handled;
                case EditorKey.V:
                    Paste(document, time);
                    return CodeEditorCommand.Handled;
                case EditorKey.Z when shift:
                    document.Redo(time);
                    return CodeEditorCommand.Handled;
                case EditorKey.Z:
                    document.Undo(time);
                    return CodeEditorCommand.Handled;
                case EditorKey.Y when !Conventions.IsMacStyle:
                    document.Redo(time);
                    return CodeEditorCommand.Handled;
                case EditorKey.S:
                    return CodeEditorCommand.Save;
                case EditorKey.F:
                    return CodeEditorCommand.OpenFind;
                case EditorKey.G:
                    return shift ? CodeEditorCommand.FindPrevious : CodeEditorCommand.FindNext;
            }
        }

        // Control+Space requests suggestions on every platform.
        if (key.Key == EditorKey.Space && key.Control)
        {
            return CodeEditorCommand.TriggerCompletion;
        }

        var lineEdgeMove = Conventions.IsMacStyle && key.Super;
        var documentEdgeMove = Conventions.IsMacStyle ? key.Super : key.Control;
        var wordMove = Conventions.IsWordMove(key);
        var buffer = document.Buffer;
        switch (key.Key)
        {
            case EditorKey.Left:
                if (!shift && document.HasSelection && !wordMove && !lineEdgeMove)
                {
                    document.SetCaret(document.Selection.Start);
                }
                else
                {
                    Move(document, lineEdgeMove ? LineHome(document, document.Caret) : wordMove ? WordLeft(buffer, document.Caret) : buffer.PreviousPosition(document.Caret), shift);
                }

                return CodeEditorCommand.Handled;

            case EditorKey.Right:
                if (!shift && document.HasSelection && !wordMove && !lineEdgeMove)
                {
                    document.SetCaret(document.Selection.End);
                }
                else
                {
                    Move(document, lineEdgeMove ? LineEnd(buffer, document.Caret) : wordMove ? WordRight(buffer, document.Caret) : buffer.NextPosition(document.Caret), shift);
                }

                return CodeEditorCommand.Handled;

            case EditorKey.Up:
                if (Conventions.IsMacStyle && key.Super)
                {
                    Move(document, TextPosition.Zero, shift);
                }
                else
                {
                    MoveVertically(document, -1, shift);
                }

                return CodeEditorCommand.Handled;

            case EditorKey.Down:
                if (Conventions.IsMacStyle && key.Super)
                {
                    Move(document, buffer.End, shift);
                }
                else
                {
                    MoveVertically(document, 1, shift);
                }

                return CodeEditorCommand.Handled;

            case EditorKey.Home:
                Move(document, documentEdgeMove ? TextPosition.Zero : LineHome(document, document.Caret), shift);
                return CodeEditorCommand.Handled;

            case EditorKey.End:
                Move(document, documentEdgeMove ? buffer.End : LineEnd(buffer, document.Caret), shift);
                return CodeEditorCommand.Handled;

            case EditorKey.PageUp:
                MoveVertically(document, -System.Math.Max(1, pageLines), shift);
                return CodeEditorCommand.Handled;

            case EditorKey.PageDown:
                MoveVertically(document, System.Math.Max(1, pageLines), shift);
                return CodeEditorCommand.Handled;

            case EditorKey.Backspace:
                Backspace(document, wordMove, lineEdgeMove, time);
                return CodeEditorCommand.Handled;

            case EditorKey.Delete:
                DeleteForward(document, wordMove, time);
                return CodeEditorCommand.Handled;

            case EditorKey.Enter:
                NewLine(document, time);
                return CodeEditorCommand.Handled;

            case EditorKey.Tab:
                if (shift)
                {
                    Unindent(document, time);
                }
                else
                {
                    TabIndent(document, time);
                }

                return CodeEditorCommand.Handled;
        }

        // Other primary-modified letters are not editor commands; plain letters/space are typed via text input.
        return primary ? CodeEditorCommand.Handled : CodeEditorCommand.None;
    }

    // ---- Typing ----

    /// <summary>Inserts typed text (from the OS text-input event) over the selection, then applies auto-dedent.</summary>
    public void TypeText(ScriptDocument document, string text, double time)
    {
        if (text.Length == 0)
        {
            return;
        }

        document.Edit(TextEditKind.Typing, document.Selection, text, time);
        if (text.Length == 1 && (char.IsAsciiLetter(text[0]) || text[0] == '}'))
        {
            AutoDedent(document, time);
        }
    }

    // ---- Clipboard ----

    /// <summary>Copies the selection (or the whole current line when nothing is selected).</summary>
    public void Copy(ScriptDocument document)
    {
        if (clipboard is null)
        {
            return;
        }

        var text = document.HasSelection ? document.Buffer.GetText(document.Selection) : document.Buffer.GetLine(document.Caret.Line) + "\n";
        clipboard.SetText(text);
    }

    public void Cut(ScriptDocument document, double time)
    {
        if (clipboard is null)
        {
            return;
        }

        Copy(document);
        var range = document.HasSelection ? document.Selection : WholeLine(document.Buffer, document.Caret.Line);
        document.Edit(TextEditKind.Cut, range, string.Empty, time);
        document.History.SealCurrent();
    }

    public void Paste(ScriptDocument document, double time)
    {
        var text = clipboard?.GetText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        document.Edit(TextEditKind.Paste, document.Selection, string.Join('\n', TextBuffer.SplitLines(text)), time);
        document.History.SealCurrent();
    }

    // ---- Mouse ----

    /// <summary>A click: 1 = place caret (shift extends), 2 = select word, 3 = select line.</summary>
    public void Click(ScriptDocument document, TextPosition position, int clickCount, bool shift)
    {
        position = document.Buffer.Clamp(position);
        document.PreferredVisualColumn = -1;
        switch (clickCount)
        {
            case 2:
                var (wordStart, wordEnd) = WordAt(document.Buffer, position);
                document.Select(wordStart, wordEnd);
                break;
            case >= 3:
                var line = WholeLine(document.Buffer, position.Line);
                document.Select(line.Start, line.End);
                break;
            default:
                document.SetCaret(position, shift);
                break;
        }
    }

    /// <summary>Dragging with the button held: the caret follows, the anchor stays.</summary>
    public void DragTo(ScriptDocument document, TextPosition position)
    {
        document.SetCaret(position, extendSelection: true);
        document.PreferredVisualColumn = -1;
    }

    // ---- Indentation ----

    /// <summary>Enter: a new line indented like this one, one level deeper after a block opener.</summary>
    public void NewLine(ScriptDocument document, double time)
    {
        var buffer = document.Buffer;
        var start = document.Selection.Start;
        var line = buffer.GetLine(start.Line);
        var leading = line[..TextColumns.LeadingWhitespaceLength(line)];
        var indent = leading.Contains('\t') ? new string(' ', TextColumns.IndentWidth(line)) : leading;
        var beforeCaret = line[..System.Math.Min(start.Column, line.Length)];
        if (OpensBlock(beforeCaret))
        {
            indent += Indent;
        }

        // Caret inside the leading whitespace: do not push the indentation further right.
        if (beforeCaret.Trim().Length == 0)
        {
            indent = beforeCaret.Contains('\t') ? new string(' ', TextColumns.VisualColumn(beforeCaret, beforeCaret.Length)) : beforeCaret;
        }

        document.Edit(TextEditKind.NewLine, document.Selection, "\n" + indent, time);
        document.History.SealCurrent();
    }

    /// <summary>Tab: indent every selected line (multi-line selection), else insert spaces to the next tab stop.</summary>
    public void TabIndent(ScriptDocument document, double time)
    {
        var selection = document.Selection;
        if (document.HasSelection && selection.Start.Line != selection.End.Line)
        {
            ShiftLines(document, indent: true, time);
            return;
        }

        var line = document.Buffer.GetLine(selection.Start.Line);
        var visual = TextColumns.VisualColumn(line, selection.Start.Column);
        var spaces = IndentSize - visual % IndentSize;
        document.Edit(TextEditKind.Indent, selection, new string(' ', spaces), time);
        document.History.SealCurrent();
    }

    /// <summary>Shift+Tab: remove one indentation level from the current/selected lines (never more than exists).</summary>
    public void Unindent(ScriptDocument document, double time) => ShiftLines(document, indent: false, time);

    /// <summary>True when the line (code before the caret) leaves a block open: function/do/then/repeat/else or "{".</summary>
    public static bool OpensBlock(string code)
    {
        var tokens = new List<LuaToken>();
        LuaLexer.LexLine(code, LuaLineState.Start, tokens);
        var balance = 0;
        var first = true;
        var startsWithElse = false;
        foreach (var token in tokens)
        {
            if (token.Kind is LuaTokenKind.Whitespace or LuaTokenKind.Comment)
            {
                continue;
            }

            var text = code.Substring(token.Start, token.Length);
            if (first)
            {
                startsWithElse = token.Kind == LuaTokenKind.Keyword && text == "else";
                first = false;
            }

            if (token.Kind == LuaTokenKind.Keyword)
            {
                if (BlockOpeners.Contains(text))
                {
                    balance++;
                }
                else if (BlockClosers.Contains(text))
                {
                    balance--;
                }
            }
            else if (token.Kind == LuaTokenKind.Operator)
            {
                balance += text switch
                {
                    "{" => 1,
                    "}" => -1,
                    _ => 0
                };
            }
        }

        return balance > 0 || (startsWithElse && balance >= 0);
    }

    // A line that became exactly "end"/"else"/"elseif"/"until"/"}" moves back to its block's indentation.
    private static void AutoDedent(ScriptDocument document, double time)
    {
        var buffer = document.Buffer;
        var lineIndex = document.Caret.Line;
        var line = buffer.GetLine(lineIndex);
        var trimmed = line.Trim();
        if (!DedentWords.Contains(trimmed) && trimmed != "}")
        {
            return;
        }

        var previous = lineIndex - 1;
        while (previous >= 0 && buffer.GetLine(previous).Trim().Length == 0)
        {
            previous--;
        }

        if (previous < 0)
        {
            return;
        }

        var previousLine = buffer.GetLine(previous);
        var previousIndent = TextColumns.IndentWidth(previousLine);
        var expected = OpensBlock(previousLine) ? previousIndent : System.Math.Max(0, previousIndent - IndentSize);
        var current = TextColumns.IndentWidth(line);
        if (current <= expected)
        {
            return;
        }

        var leading = TextColumns.LeadingWhitespaceLength(line);
        var caretColumn = document.Caret.Column - leading + expected;
        document.EditGroup(
            TextEditKind.Indent,
            [(new TextRange(new TextPosition(lineIndex, 0), new TextPosition(lineIndex, leading)), new string(' ', expected))],
            new CaretState(new TextPosition(lineIndex, caretColumn), new TextPosition(lineIndex, caretColumn)),
            time);
    }

    private static void ShiftLines(ScriptDocument document, bool indent, double time)
    {
        var buffer = document.Buffer;
        var selection = document.Selection;
        var firstLine = selection.Start.Line;
        var lastLine = selection.End.Line;
        if (document.HasSelection && selection.End.Column == 0 && lastLine > firstLine)
        {
            lastLine--; // a selection ending at the start of a line does not include that line
        }

        var edits = new List<(TextRange, string)>();
        var deltas = new Dictionary<int, int>();
        for (var lineIndex = firstLine; lineIndex <= lastLine; lineIndex++)
        {
            var line = buffer.GetLine(lineIndex);
            if (indent)
            {
                if (line.Length == 0 && firstLine != lastLine)
                {
                    continue;
                }

                edits.Add((new TextRange(new TextPosition(lineIndex, 0), new TextPosition(lineIndex, 0)), Indent));
                deltas[lineIndex] = IndentSize;
            }
            else
            {
                var remove = 0;
                if (line.StartsWith('\t'))
                {
                    remove = 1;
                }
                else
                {
                    while (remove < IndentSize && remove < line.Length && line[remove] == ' ')
                    {
                        remove++;
                    }
                }

                if (remove > 0)
                {
                    edits.Add((new TextRange(new TextPosition(lineIndex, 0), new TextPosition(lineIndex, remove)), string.Empty));
                    deltas[lineIndex] = -remove;
                }
            }
        }

        if (edits.Count == 0)
        {
            return;
        }

        TextPosition Shift(TextPosition position) =>
            deltas.TryGetValue(position.Line, out var delta)
                ? new TextPosition(position.Line, System.Math.Max(0, position.Column + delta))
                : position;

        document.EditGroup(TextEditKind.Indent, edits, new CaretState(Shift(document.Caret), Shift(document.Anchor)), time);
    }

    // ---- Deletion ----

    private static void Backspace(ScriptDocument document, bool word, bool toLineStart, double time)
    {
        var buffer = document.Buffer;
        if (document.HasSelection)
        {
            document.Edit(TextEditKind.Backspace, document.Selection, string.Empty, time);
            document.History.SealCurrent();
            return;
        }

        var caret = document.Caret;
        if (caret == TextPosition.Zero)
        {
            return;
        }

        TextPosition from;
        var line = buffer.GetLine(caret.Line);
        if (toLineStart)
        {
            from = new TextPosition(caret.Line, caret.Column > 0 ? 0 : caret.Column);
            if (from == caret)
            {
                from = buffer.PreviousPosition(caret);
            }
        }
        else if (word)
        {
            from = WordLeft(buffer, caret);
        }
        else if (caret.Column > 0 && line[..caret.Column].All(ch => ch == ' '))
        {
            // Inside indentation: back to the previous indent stop.
            var stop = (caret.Column - 1) / IndentSize * IndentSize;
            from = new TextPosition(caret.Line, stop);
        }
        else
        {
            from = buffer.PreviousPosition(caret);
        }

        document.Edit(TextEditKind.Backspace, new TextRange(from, caret), string.Empty, time);
    }

    private static void DeleteForward(ScriptDocument document, bool word, double time)
    {
        var buffer = document.Buffer;
        if (document.HasSelection)
        {
            document.Edit(TextEditKind.DeleteForward, document.Selection, string.Empty, time);
            document.History.SealCurrent();
            return;
        }

        var caret = document.Caret;
        var to = word ? WordRight(buffer, caret) : buffer.NextPosition(caret);
        if (to == caret)
        {
            return;
        }

        document.Edit(TextEditKind.DeleteForward, new TextRange(caret, to), string.Empty, time);
    }

    // ---- Caret movement helpers ----

    private static void Move(ScriptDocument document, TextPosition position, bool extend)
    {
        document.SetCaret(position, extend);
        document.PreferredVisualColumn = -1;
    }

    private static void MoveVertically(ScriptDocument document, int lines, bool extend)
    {
        var buffer = document.Buffer;
        var caret = document.Caret;
        if (!extend && document.HasSelection)
        {
            caret = lines < 0 ? document.Selection.Start : document.Selection.End;
        }

        var visual = document.PreferredVisualColumn >= 0
            ? document.PreferredVisualColumn
            : TextColumns.VisualColumn(buffer.GetLine(caret.Line), caret.Column);
        var target = caret.Line + lines;
        TextPosition position;
        if (target < 0)
        {
            position = TextPosition.Zero;
        }
        else if (target >= buffer.LineCount)
        {
            position = buffer.End;
        }
        else
        {
            position = new TextPosition(target, TextColumns.ColumnAtVisual(buffer.GetLine(target), visual));
        }

        document.SetCaret(position, extend);
        document.PreferredVisualColumn = visual;
    }

    // Home: first non-blank character, or column 0 when already there.
    private static TextPosition LineHome(ScriptDocument document, TextPosition caret)
    {
        var line = document.Buffer.GetLine(caret.Line);
        var firstText = TextColumns.LeadingWhitespaceLength(line);
        return new TextPosition(caret.Line, caret.Column == firstText ? 0 : firstText);
    }

    private static TextPosition LineEnd(TextBuffer buffer, TextPosition caret) => new(caret.Line, buffer.GetLineLength(caret.Line));

    private static TextRange WholeLine(TextBuffer buffer, int line) =>
        line + 1 < buffer.LineCount
            ? new TextRange(new TextPosition(line, 0), new TextPosition(line + 1, 0))
            : new TextRange(new TextPosition(line, 0), new TextPosition(line, buffer.GetLineLength(line)));

    private enum CharClass
    {
        Space,
        Word,
        Punctuation
    }

    private static CharClass Classify(char ch) =>
        char.IsWhiteSpace(ch) ? CharClass.Space : LuaLexer.IsNamePart(ch) || char.IsSurrogate(ch) ? CharClass.Word : CharClass.Punctuation;

    // Word moves skip whitespace, then one run of the same class; line ends count as a stop.
    private static TextPosition WordLeft(TextBuffer buffer, TextPosition caret)
    {
        if (caret.Column == 0)
        {
            return buffer.PreviousPosition(caret);
        }

        var line = buffer.GetLine(caret.Line);
        var i = caret.Column;
        while (i > 0 && char.IsWhiteSpace(line[i - 1]))
        {
            i--;
        }

        if (i > 0)
        {
            var kind = Classify(line[i - 1]);
            while (i > 0 && Classify(line[i - 1]) == kind)
            {
                i--;
            }
        }

        return buffer.Clamp(new TextPosition(caret.Line, i));
    }

    private static TextPosition WordRight(TextBuffer buffer, TextPosition caret)
    {
        var line = buffer.GetLine(caret.Line);
        if (caret.Column >= line.Length)
        {
            return buffer.NextPosition(caret);
        }

        var i = caret.Column;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        if (i < line.Length)
        {
            var kind = Classify(line[i]);
            while (i < line.Length && Classify(line[i]) == kind)
            {
                i++;
            }
        }

        return buffer.Clamp(new TextPosition(caret.Line, i));
    }

    /// <summary>The word (or whitespace / punctuation run) around a position, for double-click selection.</summary>
    public static (TextPosition Start, TextPosition End) WordAt(TextBuffer buffer, TextPosition position)
    {
        var line = buffer.GetLine(position.Line);
        if (line.Length == 0)
        {
            return (position, position);
        }

        var index = System.Math.Min(position.Column, line.Length - 1);
        var kind = Classify(line[index]);
        var start = index;
        while (start > 0 && Classify(line[start - 1]) == kind)
        {
            start--;
        }

        var end = index;
        while (end < line.Length && Classify(line[end]) == kind)
        {
            end++;
        }

        return (buffer.Clamp(new TextPosition(position.Line, start)), buffer.Clamp(new TextPosition(position.Line, end)));
    }
}
