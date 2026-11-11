using Wisko.Engine.Scripting;

namespace Wisko.Engine.ScriptEditor;

public enum CompletionItemKind
{
    Keyword,
    Object,
    Function,
    Property,
    Constant,
    Snippet,
    KeyName,
    MouseButton
}

/// <summary>
/// One suggestion. <see cref="InsertText"/> replaces the typed prefix; the caret ends <see cref="CaretOffset"/>
/// characters into it (the end when -1).
/// </summary>
public sealed record CompletionItem(string Label, string InsertText, CompletionItemKind Kind, string Signature, string Summary, int CaretOffset = -1);

/// <summary>Suggestions for the caret: they replace the text between <see cref="ReplaceStart"/> and the caret.</summary>
public sealed record CompletionResult(TextPosition ReplaceStart, string Prefix, IReadOnlyList<CompletionItem> Items);

/// <summary>
/// Deterministic completion from <see cref="ScriptApiCatalog"/>: members after a known chain (<c>self.</c>,
/// <c>self.Transform.</c>, <c>self.Transform.Position.</c>, <c>Time.</c>, <c>Input.</c>, <c>math.</c>, ...), globals
/// and keywords, Start/Update snippets at the top level, and key / mouse button names inside the string argument of
/// the Input functions. Uses only the lexed current line (and a cheap scan for existing lifecycle functions); never
/// guesses: an unknown chain or a comment gives no suggestions.
/// </summary>
public static class LuaCompletion
{
    private const int SnippetIndent = 4;

    public static CompletionResult? Complete(ScriptDocument document, TextPosition caret, bool explicitRequest)
    {
        var buffer = document.Buffer;
        caret = buffer.Clamp(caret);
        var line = buffer.GetLine(caret.Line);
        var column = caret.Column;
        var tokens = document.Highlighter.GetTokens(caret.Line);

        // The token the caret sits in or right after.
        var tokenIndex = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Start < column && column <= tokens[i].End)
            {
                tokenIndex = i;
                break;
            }
        }

        if (tokenIndex >= 0)
        {
            var token = tokens[tokenIndex];
            if (token.Kind == LuaTokenKind.Comment)
            {
                return null;
            }

            if (token.Kind == LuaTokenKind.String)
            {
                // Right after a closed string the caret is outside it; inside (or in an unclosed one) only Input names.
                var closed = token.Length >= 2 && line[token.End - 1] == line[token.Start];
                return closed && column == token.End ? null : CompleteInputName(line, tokens, tokenIndex, caret);
            }
        }

        // The name being typed.
        var prefixStart = column;
        while (prefixStart > 0 && LuaLexer.IsNamePart(line[prefixStart - 1]))
        {
            prefixStart--;
        }

        var prefix = line[prefixStart..column];
        if (prefix.Length > 0 && !LuaLexer.IsNameStart(prefix[0]))
        {
            return null; // a number such as 1e5
        }

        var start = new TextPosition(caret.Line, prefixStart);
        if (prefixStart > 0 && line[prefixStart - 1] == '.')
        {
            if (prefixStart > 1 && line[prefixStart - 2] == '.')
            {
                return null; // ".." concatenation
            }

            return ResolveChain(line, prefixStart - 1) is { } table
                ? Filter(start, prefix, MemberItems(table))
                : null;
        }

        if (prefixStart > 0 && line[prefixStart - 1] == ':')
        {
            return null; // method calls on values: the type is unknown here
        }

        if (prefix.Length == 0 && !explicitRequest)
        {
            return null;
        }

        var items = new List<CompletionItem>();
        if (line[..prefixStart].Trim().Length == 0 && prefixStart == 0)
        {
            items.AddRange(LifecycleSnippets(document));
        }

        foreach (var global in ScriptApiCatalog.Globals)
        {
            items.Add(ToItem(global));
        }

        foreach (var keyword in LuaLexer.Keywords.Order(StringComparer.Ordinal))
        {
            items.Add(new CompletionItem(keyword, keyword, CompletionItemKind.Keyword, keyword, "Lua keyword"));
        }

        return Filter(start, prefix, items);
    }

    /// <summary>Applies a suggestion: replaces the prefix and places the caret.</summary>
    public static void Apply(ScriptDocument document, CompletionResult result, CompletionItem item, double time)
    {
        var range = new TextRange(result.ReplaceStart, document.Caret);
        document.Edit(TextEditKind.Completion, range, item.InsertText, time);
        if (item.CaretOffset >= 0)
        {
            document.SetCaret(TextChange.EndOf(result.ReplaceStart, item.InsertText[..item.CaretOffset]));
        }

        document.History.SealCurrent();
    }

    // "a.b.c" ending at the dot index -> member table of c, or null when any part is unknown.
    private static string? ResolveChain(string line, int dotIndex)
    {
        var names = new List<string>();
        var end = dotIndex;
        while (true)
        {
            var nameStart = end;
            while (nameStart > 0 && LuaLexer.IsNamePart(line[nameStart - 1]))
            {
                nameStart--;
            }

            if (nameStart == end || !LuaLexer.IsNameStart(line[nameStart]))
            {
                return null;
            }

            names.Insert(0, line[nameStart..end]);
            if (nameStart > 0 && line[nameStart - 1] == '.' && !(nameStart > 1 && line[nameStart - 2] == '.'))
            {
                end = nameStart - 1;
                continue;
            }

            if (nameStart > 0 && line[nameStart - 1] == ':')
            {
                return null;
            }

            break;
        }

        if (!ScriptApiCatalog.TryGetGlobal(names[0], out var root) || root.MemberTable is null)
        {
            return null;
        }

        var table = root.MemberTable;
        for (var i = 1; i < names.Count; i++)
        {
            var member = ScriptApiCatalog.GetMembers(table).FirstOrDefault(candidate => candidate.Name == names[i]);
            if (member?.MemberTable is null)
            {
                return null;
            }

            table = member.MemberTable;
        }

        return table;
    }

    // Inside "..." directly after Input.IsKeyDown( / IsMouseButtonDown( etc.: key or button names.
    private static CompletionResult? CompleteInputName(string line, IReadOnlyList<LuaToken> tokens, int stringIndex, TextPosition caret)
    {
        var significant = new List<LuaToken>();
        for (var i = stringIndex - 1; i >= 0 && significant.Count < 4; i--)
        {
            if (tokens[i].Kind != LuaTokenKind.Whitespace)
            {
                significant.Add(tokens[i]);
            }
        }

        if (significant.Count < 4
            || Text(line, significant[0]) != "("
            || significant[1].Kind != LuaTokenKind.Identifier
            || Text(line, significant[2]) != "."
            || Text(line, significant[3]) != "Input")
        {
            return null;
        }

        var function = Text(line, significant[1]);
        IReadOnlyList<string> names;
        CompletionItemKind kind;
        if (ScriptApiCatalog.TakesKeyName(function))
        {
            names = ScriptInputState.KeyNames;
            kind = CompletionItemKind.KeyName;
        }
        else if (ScriptApiCatalog.TakesMouseButtonName(function))
        {
            names = ScriptInputState.MouseButtonNames;
            kind = CompletionItemKind.MouseButton;
        }
        else
        {
            return null;
        }

        var str = tokens[stringIndex];
        var contentStart = str.Start + 1;
        if (caret.Column < contentStart)
        {
            return null;
        }

        var prefix = line[contentStart..caret.Column];
        if (prefix.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            return null;
        }

        var quote = line[str.Start];
        var terminated = str.Length >= 2 && line[str.End - 1] == quote && str.End > caret.Column;
        var items = names.Select(name => new CompletionItem(
            name,
            terminated ? name : name + quote,
            kind,
            $"\"{name}\"",
            kind == CompletionItemKind.KeyName ? "Key name" : "Mouse button name")).ToList();
        return Filter(new TextPosition(caret.Line, contentStart), prefix, items);
    }

    private static IEnumerable<CompletionItem> MemberItems(string table) =>
        ScriptApiCatalog.GetMembers(table).Select(ToItem);

    private static CompletionItem ToItem(ScriptApiMember member)
    {
        var kind = member.Kind switch
        {
            ScriptApiKind.Object => CompletionItemKind.Object,
            ScriptApiKind.Function => CompletionItemKind.Function,
            ScriptApiKind.Constant => CompletionItemKind.Constant,
            _ => CompletionItemKind.Property
        };
        return new CompletionItem(member.Name, member.Name, kind, member.Signature, member.Summary);
    }

    // Start/Update snippets, only those the script does not define yet.
    private static IEnumerable<CompletionItem> LifecycleSnippets(ScriptDocument document)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < document.Buffer.LineCount; i++)
        {
            var text = document.Buffer.GetLine(i).TrimStart();
            if (!text.StartsWith("function ", StringComparison.Ordinal))
            {
                continue;
            }

            var rest = text[9..].TrimStart();
            foreach (var lifecycle in ScriptApiCatalog.Lifecycle)
            {
                if (rest.StartsWith(lifecycle.Name, StringComparison.Ordinal)
                    && rest[lifecycle.Name.Length..].TrimStart().StartsWith('('))
                {
                    defined.Add(lifecycle.Name);
                }
            }
        }

        foreach (var lifecycle in ScriptApiCatalog.Lifecycle)
        {
            if (defined.Contains(lifecycle.Name))
            {
                continue;
            }

            var header = lifecycle.Signature;
            var insert = header + "\n" + new string(' ', SnippetIndent) + "\nend";
            yield return new CompletionItem(lifecycle.Name, insert, CompletionItemKind.Snippet, header, lifecycle.Summary, header.Length + 1 + SnippetIndent);
        }
    }

    // Prefix filter (case-insensitive), exact-case matches first, catalog order kept otherwise.
    private static CompletionResult? Filter(TextPosition start, string prefix, IEnumerable<CompletionItem> items)
    {
        var matches = items
            .Where(item => item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select((item, index) => (item, index))
            .OrderBy(entry => entry.item.Label.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item)
            .ToList();
        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Label == prefix && matches[0].Kind != CompletionItemKind.Snippet))
        {
            return null;
        }

        return new CompletionResult(start, prefix, matches);
    }

    private static string Text(string line, LuaToken token) => line.Substring(token.Start, token.Length);
}
