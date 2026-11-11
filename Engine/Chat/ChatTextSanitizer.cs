using System.Globalization;
using System.Text;

namespace Wisko.Engine.Chat;

/// <summary>
/// Makes chat text safe to show on one line: every kind of line break and tab becomes a space, runs of spaces become
/// one, the ends are trimmed, and characters that can hide or disguise text are removed: control characters (C0, C1,
/// DEL), bidirectional overrides and isolates (which can make text read backwards), zero-width and other invisible
/// formatting characters, Unicode tag characters, noncharacters and unpaired surrogates. A pile of combining marks on
/// one letter (stacked "Zalgo" text) is cut to <see cref="MaxCombiningMarks"/>. The result is cut to a length in
/// Unicode scalar values without splitting a surrogate pair.
/// <para>A zero-width joiner stays only between two non-ASCII characters (emoji sequences, Indic scripts), where it is
/// part of the text; elsewhere it is removed like the other invisible characters.</para>
/// </summary>
public static class ChatTextSanitizer
{
    /// <summary>Combining marks kept on one base character.</summary>
    public const int MaxCombiningMarks = 3;

    // Names nobody may take as a player name (compared on letters only, ignoring case): they read as the game itself.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "system", "server", "admin", "administrator", "moderator", "mod", "wisko", "console", "game", "host", "official"
    };

    /// <summary>The cleaned text, at most <paramref name="maxLength"/> scalar values long (empty when nothing is left).</summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(global::System.Math.Min(text.Length, maxLength * 2));
        var length = 0;
        var pendingSpace = false;
        var combining = 0;
        Rune? previous = null;
        var runes = EnumerateRunesLeniently(text).ToList();
        for (var i = 0; i < runes.Count && length < maxLength; i++)
        {
            var rune = runes[i];
            if (IsSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                combining = 0;
                continue;
            }

            if (rune.Value == 0x200D)
            {
                // Zero-width joiner: kept only inside non-ASCII text (emoji sequences, Indic scripts).
                var next = i + 1 < runes.Count ? runes[i + 1] : (Rune?)null;
                if (pendingSpace || previous is not { } before || before.Value < 0x80 || next is not { } after || after.Value < 0x80 || IsSpace(after) || IsRemoved(after))
                {
                    continue;
                }
            }
            else if (IsRemoved(rune))
            {
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark)
            {
                if (builder.Length == 0 || pendingSpace || ++combining > MaxCombiningMarks)
                {
                    continue;
                }
            }
            else
            {
                combining = 0;
            }

            if (pendingSpace)
            {
                if (length + 1 >= maxLength)
                {
                    break;
                }

                builder.Append(' ');
                length++;
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
            length++;
            previous = rune;
        }

        return builder.ToString();
    }

    /// <summary>A sender's display name: cleaned like text, at most <paramref name="maxLength"/> long.</summary>
    public static string CleanName(string? name, int maxLength) => Clean(name, maxLength);

    /// <summary>
    /// Whether a name reads as the game itself ("System", "S y s t e m", "ADMIN!"): its letters and digits, lower-cased,
    /// are one of the reserved names. Players cannot take these names.
    /// </summary>
    public static bool IsReservedName(string name)
    {
        var letters = new StringBuilder(name.Length);
        foreach (var rune in EnumerateRunesLeniently(name))
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                letters.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return ReservedNames.Contains(letters.ToString());
    }

    /// <summary>Length in Unicode scalar values (what the limits count).</summary>
    public static int Length(string text) => EnumerateRunesLeniently(text).Count();

    // Every whitespace character (line and paragraph separators, tabs, no-break spaces...) counts as a space.
    private static bool IsSpace(Rune rune) =>
        Rune.IsWhiteSpace(rune) || rune.Value is 0x2028 or 0x2029 || (rune.Value < 0x20 && rune.Value is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D);

    private static bool IsRemoved(Rune rune)
    {
        var value = rune.Value;
        return value < 0x20 || value is 0x7F or (>= 0x80 and <= 0x9F)
            // Bidirectional marks, embeddings, overrides and isolates.
            || value is 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069)
            // Invisible formatting: zero-width space / non-joiner, word joiner, invisible operators, BOM, soft hyphen,
            // Mongolian vowel separator, Hangul fillers, interlinear annotation.
            || value is 0x200B or 0x200C or (>= 0x2060 and <= 0x2064) or 0xFEFF or 0x00AD or 0x180E or 0x115F or 0x1160 or 0x3164 or 0xFFA0
            || value is (>= 0xFFF9 and <= 0xFFFB)
            // Tag characters and noncharacters.
            || value is (>= 0xE0000 and <= 0xE007F) or (>= 0xFDD0 and <= 0xFDEF) || (value & 0xFFFE) == 0xFFFE;
    }

    // Runes of the text; an unpaired surrogate is dropped (it is not text).
    private static IEnumerable<Rune> EnumerateRunesLeniently(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed) == global::System.Buffers.OperationStatus.Done)
            {
                yield return rune;
            }

            i += global::System.Math.Max(1, consumed);
        }
    }
}
