namespace Wisko.Engine.Chat;

/// <summary>
/// A named chat channel ("General", "Team", "Trade"...): its limits and its recent messages, oldest first. The history
/// keeps at most <see cref="HistorySize"/> messages; older ones are let go.
/// </summary>
public sealed class ChatChannel
{
    private readonly Queue<ChatMessage> _history = new();

    internal ChatChannel(string name, int maxLength, int historySize, bool playersCanSend)
    {
        Name = name;
        MaxLength = maxLength;
        HistorySize = historySize;
        PlayersCanSend = playersCanSend;
    }

    public string Name { get; }

    /// <summary>Longest message text, in Unicode scalar values (longer text is cut).</summary>
    public int MaxLength { get; }

    /// <summary>How many recent messages the channel keeps.</summary>
    public int HistorySize { get; }

    /// <summary>Whether players may send here (false: only the game's own scripts post, e.g. an announcements channel).</summary>
    public bool PlayersCanSend { get; }

    public int Count => _history.Count;

    /// <summary>The last <paramref name="count"/> messages (all kept ones when larger), oldest first.</summary>
    public IReadOnlyList<ChatMessage> Recent(int count)
    {
        var skip = global::System.Math.Max(0, _history.Count - global::System.Math.Max(0, count));
        return _history.Skip(skip).ToList();
    }

    internal void Add(ChatMessage message)
    {
        _history.Enqueue(message);
        while (_history.Count > HistorySize)
        {
            _history.Dequeue();
        }
    }

    internal void Clear() => _history.Clear();
}
