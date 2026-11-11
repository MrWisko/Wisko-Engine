namespace Wisko.Engine.Chat;

/// <summary>
/// Where chat messages go once the <see cref="ChatService"/> accepted them, and where messages from elsewhere come in.
/// Wisko has no networking: the only transport it ships is <see cref="LocalChatTransport"/>, which hands every message
/// straight back to this session. The interface is the seam a future networked transport would fill.
/// <para>
/// Trust: a networked transport must be server-authoritative. The server, not the sending client, decides who a message
/// is from and whether it is a <see cref="ChatMessageKind.System"/> message; it re-applies the length, rate and
/// cleaning rules (a client can be modified), and enforces blocks. Messages a transport delivers are cleaned again by
/// the service before anyone sees them.
/// </para>
/// </summary>
public interface IChatTransport
{
    /// <summary>A short name for diagnostics ("Local").</summary>
    string Name { get; }

    /// <summary>Whether messages leave this machine (false for <see cref="LocalChatTransport"/>).</summary>
    bool IsNetworked { get; }

    /// <summary>The service starts using this transport: <paramref name="deliver"/> is how messages come back in.</summary>
    void Attach(Action<ChatMessage> deliver);

    /// <summary>Sends a message this session accepted (the local player's, or the game's own).</summary>
    void Send(ChatMessage message);

    /// <summary>
    /// The local player blocked (true) or unblocked (false) a sender. A networked transport would stop that sender's
    /// messages reaching this player and this player's reaching them; locally there is nobody to tell.
    /// </summary>
    void SetBlocked(string senderId, bool blocked);

    /// <summary>The service is done with this transport (the Play session ended).</summary>
    void Detach();
}

/// <summary>
/// The transport of a single-machine game: every sent message is delivered straight back to the same session (the
/// service then delivers it to scripts at the start of the next frame). Nothing leaves the process.
/// </summary>
public sealed class LocalChatTransport : IChatTransport
{
    private Action<ChatMessage>? _deliver;

    public string Name => "Local";

    public bool IsNetworked => false;

    public void Attach(Action<ChatMessage> deliver) => _deliver = deliver;

    public void Send(ChatMessage message) => _deliver?.Invoke(message);

    public void SetBlocked(string senderId, bool blocked)
    {
    }

    public void Detach() => _deliver = null;
}
