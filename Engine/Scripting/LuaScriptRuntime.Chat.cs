using KeraLua;
using Wisko.Engine.Chat;
using Wisko.Engine.Gameplay;
using LuaState = KeraLua.Lua;

namespace Wisko.Engine.Scripting;

/// <summary>
/// The Lua Chat API's host side: hostGameplay("chat...") requests against the Play session's <see cref="ChatService"/>,
/// and <see cref="DispatchChat"/>, which delivers the messages sent since the last frame. Delivery, per message in
/// order: every Chat.SetFilter filter runs (in connection order, each as its script) and may keep the message, rewrite
/// its text (cleaned again) or withhold it; a surviving message joins its channel's history, then every Chat.OnMessage
/// listener for its channel (or for all channels) runs with its own copy of the message.
/// <para>
/// Filters fail closed: when a filter raises an error (its script faults, as any failing callback), that message and
/// every later one in the session are withheld, and the Output says so once. A broken moderation filter therefore never
/// lets unfiltered text through.
/// </para>
/// </summary>
public sealed partial class LuaScriptRuntime
{
    private const string ChatMessageEventName = "chat:message";
    private const string ChatFilterEventName = "chat:filter";

    // The filter running now, so only its own verdict (hostGameplay "chatverdict") is taken.
    private LuaScriptInstance? _chatFilterOwner;
    private long _chatFilterMessage;
    private (bool Withhold, string? Text, string? Reason)? _chatVerdict;

    // A filter failed: chat is closed for the rest of the session.
    private bool _chatFilterBroken;

    // The script that turned the default chat UI on (one at a time).
    private LuaScriptInstance? _chatUiOwner;

    private readonly List<EventHandlerEntry> _chatHandlers = [];

    /// <summary>Messages a filter withheld this session (diagnostics, tests).</summary>
    public int ChatWithheldCount { get; private set; }

    /// <summary>
    /// Delivers the chat messages sent since the last call (once per frame, after Events): filters, history, listeners,
    /// as the class summary describes.
    /// </summary>
    internal void DispatchChat()
    {
        ThrowIfUnusable();
        if (Gameplay is not { } session || session.Chat.PendingCount == 0)
        {
            return;
        }

        var chat = session.Chat;
        foreach (var sent in chat.TakePending())
        {
            if (chat.IsHidden(sent.SenderId) || chat.FindChannel(sent.Channel) is not { } channel)
            {
                continue;
            }

            if (!TryFilter(sent, channel, out var message))
            {
                ChatWithheldCount++;
                continue;
            }

            chat.Commit(message);
            var forChannel = ChatMessageEventName + ":" + message.Channel;
            Collect(handler => handler.Name == ChatMessageEventName || handler.Name == forChannel);
            if (_chatHandlers.Count == 0)
            {
                continue;
            }

            var call = MessageEvent(ChatMessageEventName, message);
            foreach (var handler in _chatHandlers)
            {
                if (handler.Active && IsRunnable(handler.Owner))
                {
                    CallEventHandler(handler, call, ScriptPhase.ChatMessage);
                }
            }

            _chatHandlers.Clear();
        }
    }

    // The filters' verdicts on one message: false when it is withheld; otherwise the message (its text perhaps rewritten).
    private bool TryFilter(ChatMessage sent, ChatChannel channel, out ChatMessage message)
    {
        message = sent;
        if (_chatFilterBroken)
        {
            return false;
        }

        Collect(handler => handler.Name == ChatFilterEventName);
        var filters = _chatHandlers.ToArray();
        _chatHandlers.Clear();
        foreach (var filter in filters)
        {
            if (!filter.Active || !IsRunnable(filter.Owner))
            {
                continue;
            }

            _chatFilterOwner = filter.Owner;
            _chatFilterMessage = message.Id;
            _chatVerdict = null;
            ScriptExecutionResult result;
            try
            {
                result = CallEventHandler(filter, MessageEvent(ChatFilterEventName, message), ScriptPhase.ChatFilter);
            }
            finally
            {
                _chatFilterOwner = null;
                _chatFilterMessage = 0;
            }

            if (!result.Succeeded)
            {
                _chatFilterBroken = true;
                Emit(new ScriptLogMessage(ScriptLogKind.Warning, filter.Owner.Name,
                    "Chat.SetFilter: a filter failed, so chat is closed for the rest of this session (messages are withheld rather than shown unfiltered); fix the filter and play again"));
                return false;
            }

            if (_chatVerdict is not { } verdict)
            {
                continue;
            }

            if (verdict.Withhold)
            {
                Console.WriteLine($"[Chat] Withheld {ChatService.Describe(message)} ({verdict.Reason ?? "a filter"})");
                return false;
            }

            var text = ChatTextSanitizer.Clean(verdict.Text, channel.MaxLength);
            if (text.Length == 0)
            {
                Console.WriteLine($"[Chat] Withheld {ChatService.Describe(message)} (a filter left no text)");
                return false;
            }

            message = message with { Text = text };
        }

        return true;
    }

    // The active internal handlers matching, in connection order, into the reused list.
    private void Collect(Func<EventHandlerEntry, bool> match)
    {
        _chatHandlers.Clear();
        foreach (var handler in _eventHandlers)
        {
            if (handler.Active && handler.Internal && match(handler))
            {
                _chatHandlers.Add(handler);
            }
        }
    }

    // A message as the Lua side's wrapper takes it: id, channel, text, sender id, sender name, kind, time, timestamp, then
    // its Data as key, value pairs (by key).
    private static QueuedEvent MessageEvent(string name, ChatMessage message)
    {
        var values = MessageValues(message, withDataCount: false);
        return new QueuedEvent(name, values, new string('v', values.Length), Internal: true);
    }

    // The eight fixed values, then (for history lists) the number of Data pairs, then the pairs.
    private static object?[] MessageValues(ChatMessage message, bool withDataCount)
    {
        var values = new List<object?>(9 + message.Data.Count * 2)
        {
            message.Id, message.Channel, message.Text, message.SenderId, message.SenderName, message.Kind.ToString(), message.Time, message.Timestamp
        };
        if (withDataCount)
        {
            values.Add((long)message.Data.Count);
        }

        foreach (var (key, value) in message.Data.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            values.Add(key);
            values.Add(value);
        }

        return [.. values];
    }

    // The developer's Data table at a hostGameplay argument (none: nil) as pairs; ChatData checks and cleans them.
    private static string? ReadChatData(LuaState lua, int index, out List<KeyValuePair<string, object?>>? data)
    {
        data = null;
        if (lua.Type(index) != LuaType.Table)
        {
            return null;
        }

        data = [];
        lua.PushNil();
        while (lua.Next(index))
        {
            if (lua.Type(-2) != LuaType.String || data.Count > ChatData.MaxEntries)
            {
                lua.Pop(2);
                return data.Count > ChatData.MaxEntries ? $"Data can hold at most {ChatData.MaxEntries} values" : "Data keys are names (strings)";
            }

            var key = lua.ToString(-2, false) ?? string.Empty;
            object? value = lua.Type(-1) switch
            {
                LuaType.String => lua.ToString(-1, false),
                LuaType.Boolean => lua.ToBoolean(-1),
                LuaType.Number => lua.ToNumber(-1),
                _ => null
            };
            lua.Pop(1);
            if (value is null)
            {
                lua.Pop(1);
                return $"Data.{key} must be a string, a number or a boolean";
            }

            data.Add(new KeyValuePair<string, object?>(key, value));
        }

        return null;
    }

    // hostGameplay("chat...", ...): see the Chat library in gameplay.lua for what each answers.
    private int ChatRequest(LuaState lua, GameplaySession session, string request)
    {
        var chat = session.Chat;
        switch (request)
        {
            case "chatsend":
            case "chatpost":
            {
                var text = lua.ToString(2, false);
                var channel = lua.Type(3) == LuaType.String ? lua.ToString(3, false) : null;
                if (ReadChatData(lua, 5, out var data) is { } dataProblem)
                {
                    return Answer(lua, dataProblem);
                }

                var error = request == "chatsend"
                    ? chat.Send(text, channel, data, out var message)
                    : chat.Post(text, channel, lua.Type(4) == LuaType.String ? lua.ToString(4, false) : null, data, out message);
                if (error is not null || message is null)
                {
                    return Answer(lua, error ?? "the message was not sent");
                }

                lua.PushInteger(message.Id);
                return 1;
            }

            case "chatlisten":
            {
                string name = ChatMessageEventName;
                if (lua.Type(2) == LuaType.String)
                {
                    var channel = lua.ToString(2, false) ?? string.Empty;
                    if (chat.FindChannel(channel) is null)
                    {
                        return Answer(lua, $"there is no chat channel \"{channel}\" (Chat.CreateChannel makes one)");
                    }

                    name += ":" + channel;
                }

                return ConnectInternal(lua, 3, name, "Chat.OnMessage");
            }

            case "chatfilter":
                return ConnectInternal(lua, 2, ChatFilterEventName, "Chat.SetFilter");
            case "chatverdict":
                // Only the filter running now, about the message it was given.
                if (_chatFilterOwner is not null && ReferenceEquals(_running, _chatFilterOwner) && lua.ToInteger(2) == _chatFilterMessage)
                {
                    _chatVerdict = lua.Type(3) == LuaType.String
                        ? (false, lua.ToString(3, false), null)
                        : (true, null, lua.Type(4) == LuaType.String ? lua.ToString(4, false) : null);
                }

                return 0;
            case "chatcreate":
                return Answer(lua, chat.CreateChannel(lua.ToString(2, false) ?? string.Empty,
                    lua.Type(3) == LuaType.Number ? ClampToInt(lua.ToNumber(3)) : null,
                    lua.Type(4) == LuaType.Number ? ClampToInt(lua.ToNumber(4)) : null,
                    lua.Type(5) != LuaType.Boolean || lua.ToBoolean(5)));
            case "chatchannel":
            {
                if (chat.FindChannel(lua.ToString(2, false)) is not { } channel)
                {
                    return 0;
                }

                lua.PushString(channel.Name);
                lua.PushInteger(channel.MaxLength);
                lua.PushInteger(channel.HistorySize);
                lua.PushBoolean(channel.PlayersCanSend);
                lua.PushInteger(channel.Count);
                return 5;
            }

            case "chatchannels":
                lua.CreateTable(chat.Channels.Count, 0);
                for (var i = 0; i < chat.Channels.Count; i++)
                {
                    lua.PushString(chat.Channels[i].Name);
                    lua.RawSetInteger(-2, i + 1);
                }

                return 1;
            case "chathistory":
            {
                // The last count messages of a channel, oldest first, in one flat list: per message its eight values, the
                // number of its Data pairs and the pairs.
                var name = lua.Type(2) == LuaType.String ? lua.ToString(2, false) : ChatService.DefaultChannelName;
                if (chat.FindChannel(name) is not { } channel)
                {
                    return Answer(lua, $"there is no chat channel \"{name}\"");
                }

                var count = lua.Type(3) == LuaType.Number ? ClampToInt(lua.ToNumber(3)) : channel.HistorySize;
                var messages = channel.Recent(count);
                lua.PushBoolean(true);
                lua.CreateTable(messages.Count * 9, 0);
                var index = 0;
                foreach (var message in messages)
                {
                    foreach (var value in MessageValues(message, withDataCount: true))
                    {
                        PushValue(lua, value);
                        lua.RawSetInteger(-2, ++index);
                    }
                }

                return 2;
            }

            case "chatmute":
                return Answer(lua, chat.SetMuted(lua.ToString(2, false), lua.ToBoolean(3)));
            case "chatblock":
                return Answer(lua, chat.SetBlocked(lua.ToString(2, false), lua.ToBoolean(3)));
            case "chatismuted":
                lua.PushBoolean(lua.ToString(2, false) is { } muted && chat.IsMuted(muted));
                return 1;
            case "chatisblocked":
                lua.PushBoolean(lua.ToString(2, false) is { } blocked && chat.IsBlocked(blocked));
                return 1;
            case "chatplayer":
                lua.PushString(ChatService.LocalPlayerId);
                lua.PushString(chat.LocalPlayerName);
                return 2;
            case "chatsetname":
                return Answer(lua, chat.SetLocalPlayerName(lua.ToString(2, false)));
            case "chatrate":
                return Answer(lua, chat.SetRateLimit(ClampToInt(lua.ToNumber(2)), lua.ToNumber(3)));
            case "chattransport":
                lua.PushString(chat.Transport.Name);
                lua.PushBoolean(chat.Transport.IsNetworked);
                return 2;
            case "chatui":
                return ChatUiRequest(lua, lua.ToBoolean(2));
            default:
                return Answer(lua, "unknown chat request");
        }
    }

    // The default chat UI is on in at most one script: the first to turn it on keeps it until it turns it off or ends.
    private int ChatUiRequest(LuaState lua, bool enable)
    {
        if (_running is not { } script)
        {
            return Answer(lua, "the default chat UI is not available here");
        }

        if (enable)
        {
            if (_chatUiOwner is { } owner && !ReferenceEquals(owner, script) && IsLive(owner))
            {
                return Answer(lua, $"the default chat UI is already on (turned on by {owner.Name})");
            }

            _chatUiOwner = script;
            return Answer(lua, null);
        }

        if (ReferenceEquals(_chatUiOwner, script))
        {
            _chatUiOwner = null;
        }

        return Answer(lua, null);
    }

    private static int ClampToInt(double value) =>
        double.IsFinite(value) ? (int)global::System.Math.Clamp(global::System.Math.Round(value), int.MinValue, int.MaxValue) : 0;
}
