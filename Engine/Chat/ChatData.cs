using System.Text.RegularExpressions;

namespace Wisko.Engine.Chat;

/// <summary>
/// A message's Data: a small table of the developer's own values (a colour, a team, an icon name...) that a custom chat
/// interface can style the message with. It is separate from what the engine vouches for (the sender, the kind) and can
/// never change them. At most <see cref="MaxEntries"/> entries; keys are names (a letter, then letters, digits or '_',
/// up to <see cref="MaxKeyLength"/> characters); values are strings (cleaned like message text, up to
/// <see cref="MaxStringLength"/> characters), finite numbers or booleans. A message arriving from a transport has its
/// Data checked again.
/// </summary>
public static partial class ChatData
{
    public const int MaxEntries = 16;
    public const int MaxKeyLength = 32;
    public const int MaxStringLength = 200;

    /// <summary>No values.</summary>
    public static IReadOnlyDictionary<string, object> Empty { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>
    /// The cleaned copy of <paramref name="raw"/> (null: <see cref="Empty"/>). Null when it is usable, else why not
    /// (naming the key); <paramref name="data"/> is then <see cref="Empty"/>.
    /// </summary>
    public static string? TryClean(IEnumerable<KeyValuePair<string, object?>>? raw, out IReadOnlyDictionary<string, object> data)
    {
        data = Empty;
        if (raw is null)
        {
            return null;
        }

        var cleaned = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (!KeyPattern().IsMatch(key))
            {
                return $"Data keys are names (a letter, then letters, digits or '_', at most {MaxKeyLength} characters), not \"{Shorten(key)}\"";
            }

            if (cleaned.Count == MaxEntries)
            {
                return $"Data can hold at most {MaxEntries} values";
            }

            switch (value)
            {
                case string text:
                    cleaned[key] = ChatTextSanitizer.Clean(text, MaxStringLength);
                    break;
                case bool flag:
                    cleaned[key] = flag;
                    break;
                case double or float or int or long:
                    var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!double.IsFinite(number))
                    {
                        return $"Data.{key} must be a finite number";
                    }

                    cleaned[key] = number;
                    break;
                default:
                    return $"Data.{key} must be a string, a number or a boolean";
            }
        }

        data = cleaned.Count == 0 ? Empty : cleaned;
        return null;
    }

    private static string Shorten(string key) => key.Length <= 40 ? key : key[..40] + "...";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
