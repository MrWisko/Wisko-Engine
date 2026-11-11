namespace Wisko.Engine.Chat;

/// <summary>Who a chat message comes from, as far as the engine can vouch for it.</summary>
public enum ChatMessageKind
{
    /// <summary>Typed by a player (the local player here; with a networked transport, a player the server vouches for).</summary>
    Player,

    /// <summary>Posted by the game's own scripts (announcements, NPC lines): never something a player typed.</summary>
    System
}

/// <summary>
/// One chat message, as delivered: its number (unique in the session), channel, cleaned text, sender, kind and when it
/// was sent (<see cref="Time"/>: seconds since Play started; <see cref="Timestamp"/>: Unix seconds, UTC), plus the
/// developer's own <see cref="Data"/>. Immutable: a filter that rewrites the text makes a new record.
/// </summary>
public sealed record ChatMessage(long Id, string Channel, string Text, string SenderId, string SenderName, ChatMessageKind Kind, double Time, double Timestamp)
{
    /// <summary>The developer's own values for styling (<see cref="ChatData"/>); never the sender or the kind.</summary>
    public IReadOnlyDictionary<string, object> Data { get; init; } = ChatData.Empty;
}
