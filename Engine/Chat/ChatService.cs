using System.Globalization;

namespace Wisko.Engine.Chat;

/// <summary>
/// The text chat of one Play session (made when Play starts, gone at Stop: no message, channel, mute or block outlives
/// the session). It owns the channels and their bounded histories, the local player's identity, the send rate limit,
/// mutes and blocks, and the transport.
/// <para>
/// Flow: <see cref="Send"/> (the local player; always stamped with the local player's id and name, kind
/// <see cref="ChatMessageKind.Player"/>) and <see cref="Post"/> (the game's own scripts; kind
/// <see cref="ChatMessageKind.System"/>) clean and check the text and hand the message to the transport. What the
/// transport delivers waits in <see cref="TakePending"/> until the next frame, when the scripting runtime runs the
/// filters (Chat.SetFilter), <see cref="Commit"/>s each surviving message to its channel's history and calls the
/// Chat.OnMessage listeners. A player can never produce a System message: nothing a player-message path takes
/// chooses the kind, the sender id or the sender name, and player names cannot be reserved names ("System", "Admin"...).
/// </para>
/// <para>Wisko has no networking: with <see cref="LocalChatTransport"/> everything stays on this machine and every
/// protection here is a local safety measure, not a defence against a modified client (see <see cref="IChatTransport"/>).</para>
/// </summary>
public sealed class ChatService : IDisposable
{
    public const string DefaultChannelName = "General";
    public const string LocalPlayerId = "local";
    public const string SystemSenderId = "system";
    public const string NpcSenderPrefix = "npc:";
    public const string DefaultLocalPlayerName = "Player";
    public const string DefaultSystemName = "System";
    public const int MaxChannels = 32;
    public const int MaxChannelNameLength = 32;
    public const int MaxSenderNameLength = 32;
    public const int MaxSenderIdLength = 64;
    public const int DefaultMaxLength = 200;
    public const int MaxMessageLength = 1000;
    public const int DefaultHistorySize = 100;
    public const int MaxHistorySize = 500;

    /// <summary>Messages waiting for the next frame's delivery; beyond this, new ones are refused (a runaway loop).</summary>
    public const int MaxPending = 1000;

    public const int MaxHiddenSenders = 1000;
    public const int DefaultRateBurst = 5;
    public const double DefaultRatePerSecond = 1d;

    private readonly Dictionary<string, ChatChannel> _channels = new(StringComparer.Ordinal);
    private readonly List<ChatChannel> _channelOrder = [];
    private readonly Queue<ChatMessage> _pending = new();
    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly Func<double> _clock;
    private readonly IChatTransport _transport;
    private long _nextId;
    private double _tokens = DefaultRateBurst;
    private double _lastRefill;
    private bool _disposed;

    public ChatService(IChatTransport? transport = null, Func<double>? clock = null)
    {
        var started = global::System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => started.Elapsed.TotalSeconds);
        _transport = transport ?? new LocalChatTransport();
        _transport.Attach(Receive);
        _lastRefill = _clock();
        CreateChannel(DefaultChannelName);
    }

    public IChatTransport Transport => _transport;

    public string LocalPlayerName { get; private set; } = DefaultLocalPlayerName;

    /// <summary>The channels, in the order they were made ("General" first).</summary>
    public IReadOnlyList<ChatChannel> Channels => _channelOrder;

    public int PendingCount => _pending.Count;

    /// <summary>How many sends the rate limit refused this session.</summary>
    public int RateLimitedCount { get; private set; }

    /// <summary>How many messages the transport delivered that were dropped (muted or blocked sender, no such channel, flood).</summary>
    public int DroppedCount { get; private set; }

    public int RateBurst { get; private set; } = DefaultRateBurst;

    public double RatePerSecond { get; private set; } = DefaultRatePerSecond;

    public ChatChannel? FindChannel(string? name) => name is not null && _channels.TryGetValue(name, out var channel) ? channel : null;

    /// <summary>A new channel; null when made, else why not. Names: 1 to 32 letters, digits, spaces, '_' or '-'.</summary>
    public string? CreateChannel(string name, int? maxLength = null, int? historySize = null, bool playersCanSend = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(name) || name.Length > MaxChannelNameLength || name.Trim() != name
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-'))
        {
            return $"a channel name is 1 to {MaxChannelNameLength} letters, digits, spaces, '_' or '-' (no leading or trailing space)";
        }

        if (_channels.ContainsKey(name))
        {
            return $"the channel \"{name}\" already exists";
        }

        if (_channels.Count >= MaxChannels)
        {
            return $"a game can have at most {MaxChannels} chat channels";
        }

        var length = maxLength ?? DefaultMaxLength;
        if (length is < 1 or > MaxMessageLength)
        {
            return $"MaxLength must be 1 to {MaxMessageLength}";
        }

        var history = historySize ?? DefaultHistorySize;
        if (history is < 1 or > MaxHistorySize)
        {
            return $"HistorySize must be 1 to {MaxHistorySize}";
        }

        var channel = new ChatChannel(name, length, history, playersCanSend);
        _channels.Add(name, channel);
        _channelOrder.Add(channel);
        return null;
    }

    /// <summary>The local player's display name (cleaned, 1 to 32 long, not a reserved name); null when set, else why not.</summary>
    public string? SetLocalPlayerName(string? name)
    {
        var cleaned = ChatTextSanitizer.CleanName(name, MaxSenderNameLength + 1);
        if (cleaned.Length == 0 || ChatTextSanitizer.Length(cleaned) > MaxSenderNameLength)
        {
            return $"a player name is 1 to {MaxSenderNameLength} visible characters";
        }

        if (ChatTextSanitizer.IsReservedName(cleaned))
        {
            return $"\"{cleaned}\" is reserved for the game itself";
        }

        LocalPlayerName = cleaned;
        return null;
    }

    /// <summary>Burst: messages a player may send at once; then <paramref name="perSecond"/> more each second. Null when set.</summary>
    public string? SetRateLimit(int burst, double perSecond)
    {
        if (burst is < 1 or > 100 || !double.IsFinite(perSecond) || perSecond < 0.05d || perSecond > 100d)
        {
            return "the rate limit is a burst of 1 to 100 messages and 0.05 to 100 messages per second";
        }

        Refill();
        RateBurst = burst;
        RatePerSecond = perSecond;
        _tokens = global::System.Math.Min(_tokens, burst);
        return null;
    }

    /// <summary>
    /// The local player says <paramref name="text"/> in <paramref name="channel"/> (default "General"): the only way a
    /// Player message is made. Null when sent (delivered at the start of the next frame), else why not.
    /// </summary>
    public string? Send(string? text, string? channel, out ChatMessage? message) => Send(text, channel, null, out message);

    /// <summary>As <see cref="Send(string?, string?, out ChatMessage?)"/>, with the developer's own <see cref="ChatData"/>.</summary>
    public string? Send(string? text, string? channel, IEnumerable<KeyValuePair<string, object?>>? data, out ChatMessage? message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        message = null;
        if (ChatData.TryClean(data, out var cleanData) is { } dataProblem)
        {
            return dataProblem;
        }

        var target = FindChannel(channel ?? DefaultChannelName);
        if (target is null)
        {
            return $"there is no chat channel \"{channel}\"";
        }

        if (!target.PlayersCanSend)
        {
            return $"players cannot send in \"{target.Name}\"";
        }

        var cleaned = ChatTextSanitizer.Clean(text, target.MaxLength);
        if (cleaned.Length == 0)
        {
            return "the message is empty";
        }

        if (_pending.Count >= MaxPending)
        {
            return "too many messages are waiting; try again next frame";
        }

        Refill();
        if (_tokens < 1d)
        {
            RateLimitedCount++;
            return "you are sending messages too quickly; wait a moment";
        }

        _tokens -= 1d;
        message = NewMessage(target.Name, cleaned, LocalPlayerId, LocalPlayerName, ChatMessageKind.Player) with { Data = cleanData };
        _transport.Send(message);
        return null;
    }

    /// <summary>
    /// The game's own message (an announcement; with <paramref name="senderName"/>, an NPC's line): kind System, sender
    /// "system" or "npc:Name". Scripts only; players cannot reach it. Null when posted, else why not.
    /// </summary>
    public string? Post(string? text, string? channel, string? senderName, out ChatMessage? message) => Post(text, channel, senderName, null, out message);

    /// <summary>As <see cref="Post(string?, string?, string?, out ChatMessage?)"/>, with the developer's own <see cref="ChatData"/>.</summary>
    public string? Post(string? text, string? channel, string? senderName, IEnumerable<KeyValuePair<string, object?>>? data, out ChatMessage? message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        message = null;
        if (ChatData.TryClean(data, out var cleanData) is { } dataProblem)
        {
            return dataProblem;
        }

        var target = FindChannel(channel ?? DefaultChannelName);
        if (target is null)
        {
            return $"there is no chat channel \"{channel}\"";
        }

        var cleaned = ChatTextSanitizer.Clean(text, target.MaxLength);
        if (cleaned.Length == 0)
        {
            return "the message is empty";
        }

        var name = senderName is null ? DefaultSystemName : ChatTextSanitizer.CleanName(senderName, MaxSenderNameLength);
        if (name.Length == 0)
        {
            return $"SenderName must have 1 to {MaxSenderNameLength} visible characters";
        }

        if (_pending.Count >= MaxPending)
        {
            return $"more than {MaxPending} chat messages in one frame; a script is probably posting in a loop";
        }

        var senderId = senderName is null ? SystemSenderId : NpcSenderPrefix + name;
        message = NewMessage(target.Name, cleaned, senderId, name, ChatMessageKind.System) with { Data = cleanData };
        _transport.Send(message);
        return null;
    }

    /// <summary>
    /// A message from the transport. It is cleaned again (a transport's sender may not be trusted to have done it),
    /// dropped when its channel does not exist, its sender is muted or blocked, or too many are waiting; otherwise it
    /// waits for the next frame's delivery.
    /// </summary>
    public void Receive(ChatMessage message)
    {
        if (_disposed)
        {
            return;
        }

        var channel = FindChannel(message.Channel);
        var text = channel is null ? string.Empty : ChatTextSanitizer.Clean(message.Text, channel.MaxLength);
        var name = ChatTextSanitizer.CleanName(message.SenderName, MaxSenderNameLength);
        // A player's message never carries the game's own sender ids or a reserved name (a transport that let one
        // through is not trusted with it).
        var dataProblem = ChatData.TryClean(message.Data.Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)), out var data);
        var forged = message.Kind == ChatMessageKind.Player
            && (message.SenderId == SystemSenderId || message.SenderId.StartsWith(NpcSenderPrefix, StringComparison.Ordinal) || ChatTextSanitizer.IsReservedName(name));
        if (dataProblem is not null || channel is null || text.Length == 0 || name.Length == 0 || string.IsNullOrEmpty(message.SenderId) || message.SenderId.Length > MaxSenderIdLength
            || forged || IsHidden(message.SenderId) || _pending.Count >= MaxPending)
        {
            DroppedCount++;
            return;
        }

        _pending.Enqueue(message with { Text = text, SenderName = name, Data = data });
    }

    /// <summary>The messages waiting for delivery, oldest first; the queue is empty afterwards.</summary>
    public IReadOnlyList<ChatMessage> TakePending()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var taken = _pending.ToList();
        _pending.Clear();
        return taken;
    }

    /// <summary>A delivered message joins its channel's history (it passed the filters).</summary>
    public void Commit(ChatMessage message) => FindChannel(message.Channel)?.Add(message);

    /// <summary>Whether this sender's messages are hidden from the local player (muted or blocked).</summary>
    public bool IsHidden(string senderId) => _muted.Contains(senderId) || _blocked.Contains(senderId);

    public bool IsMuted(string senderId) => _muted.Contains(senderId);

    public bool IsBlocked(string senderId) => _blocked.Contains(senderId);

    /// <summary>Hides (true) or shows again (false) a sender's messages for the local player; null when done, else why not.</summary>
    public string? SetMuted(string? senderId, bool muted) => SetHidden(_muted, senderId, muted, "mute");

    /// <summary>As <see cref="SetMuted"/>, and the transport is told (a networked one would also stop the sender seeing ours).</summary>
    public string? SetBlocked(string? senderId, bool blocked)
    {
        var error = SetHidden(_blocked, senderId, blocked, "block");
        if (error is null)
        {
            _transport.SetBlocked(senderId!, blocked);
        }

        return error;
    }

    /// <summary>Everything the session said is forgotten (history, waiting messages, mutes and blocks).</summary>
    public void Clear()
    {
        foreach (var channel in _channelOrder)
        {
            channel.Clear();
        }

        _pending.Clear();
        _muted.Clear();
        _blocked.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _transport.Detach();
        _disposed = true;
    }

    private string? SetHidden(HashSet<string> set, string? senderId, bool hidden, string verb)
    {
        if (string.IsNullOrEmpty(senderId) || senderId.Length > MaxSenderIdLength)
        {
            return $"{verb} expects a sender id (message.Sender.Id)";
        }

        if (senderId == LocalPlayerId)
        {
            return $"the local player cannot {verb} themselves";
        }

        if (senderId == SystemSenderId || senderId.StartsWith(NpcSenderPrefix, StringComparison.Ordinal))
        {
            return $"the game's own messages cannot be {(verb == "mute" ? "muted" : "blocked")} (filter them with Chat.SetFilter)";
        }

        if (hidden)
        {
            if (set.Count >= MaxHiddenSenders && !set.Contains(senderId))
            {
                return $"at most {MaxHiddenSenders} senders can be {(verb == "mute" ? "muted" : "blocked")}";
            }

            set.Add(senderId);
        }
        else
        {
            set.Remove(senderId);
        }

        return null;
    }

    private ChatMessage NewMessage(string channel, string text, string senderId, string senderName, ChatMessageKind kind) =>
        new(++_nextId, channel, text, senderId, senderName, kind, _clock(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d);

    private void Refill()
    {
        var now = _clock();
        var elapsed = now - _lastRefill;
        _lastRefill = now;
        if (elapsed > 0d && double.IsFinite(elapsed))
        {
            _tokens = global::System.Math.Min(RateBurst, _tokens + elapsed * RatePerSecond);
        }
    }

    /// <summary>For logs: "#12 [General] Player: hello".</summary>
    public static string Describe(ChatMessage message) =>
        string.Create(CultureInfo.InvariantCulture, $"#{message.Id} [{message.Channel}] {(message.Kind == ChatMessageKind.System ? "(system) " : string.Empty)}{message.SenderName}: {message.Text}");
}
