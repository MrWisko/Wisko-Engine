using Wisko.Engine.Chat;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// First public release: scriptable text chat (local only) and the GUI TextBox. The cleaning rules, the service (rate
/// limit, channels, bounded history, mutes and blocks through a simulated remote transport, forged messages), the Lua
/// Chat API in a real Play session (next-frame delivery, kinds, filters, impersonation, channels, cleanup at Stop,
/// failing callbacks), the TextBox (focus, typing, paste, Enter, Escape, pointer, UI.json) and the default chat window.
/// </summary>
internal static class ChatChecks
{
    // ---- Cleaning ------------------------------------------------------------------------------------------------

    public static void Sanitizer(TestReport report)
    {
        report.Check("Chat text: line breaks and tabs become one space, runs of spaces collapse, ends trimmed",
            ChatTextSanitizer.Clean("  hello\r\n\tworld   again \u2028 ", 200) == "hello world again",
            ChatTextSanitizer.Clean("  hello\r\n\tworld   again \u2028 ", 200));
        report.Check("Chat text: control characters (C0, DEL, C1) are removed",
            ChatTextSanitizer.Clean("a\u0001b\u007Fc\u009Bd", 200) == "abcd", ChatTextSanitizer.Clean("a\u0001b\u007Fc\u009Bd", 200));
        report.Check("Chat text: NEL (a C1 line break) becomes a space like the other line breaks", ChatTextSanitizer.Clean("a\u0085b", 200) == "a b");
        var bidi = ChatTextSanitizer.Clean("user\u202Egnp.exe\u2066x\u2069\u200F", 200);
        report.Check("Chat text: bidirectional overrides and isolates (text that reads backwards) are removed", bidi == "usergnp.exex", bidi);
        var invisible = ChatTextSanitizer.Clean("b\u200Ba\u200Cd\u2060w\uFEFFo\u00ADr\u200Dd", 200);
        report.Check("Chat text: zero-width and invisible characters are removed (also a joiner between ASCII letters)", invisible == "badword", invisible);
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        report.Check("Chat text: an emoji joiner sequence stays whole", ChatTextSanitizer.Clean(family, 200) == family);
        var zalgo = ChatTextSanitizer.Clean("e" + new string('\u0301', 12) + "x", 200);
        report.Check("Chat text: at most 3 combining marks on a letter (stacked text is cut)", zalgo == "e\u0301\u0301\u0301x", $"{zalgo.Length} chars");
        var emoji = ChatTextSanitizer.Clean("a\U0001F600b\U0001F600", 3);
        report.Check("Chat text: the length limit counts characters and never splits an emoji", emoji == "a\U0001F600b", emoji);
        report.Check("Chat text: an unpaired surrogate is dropped", ChatTextSanitizer.Clean("a\uD800b", 200) == "ab");
        report.Check("Chat text: tag characters and noncharacters are removed", ChatTextSanitizer.Clean("a\U000E0041b\uFFFEc", 200) == "abc");
        report.Check("Chat text: only spaces and invisible characters leave nothing", ChatTextSanitizer.Clean(" \u200B\n\t ", 200).Length == 0);
        report.Check("Chat names: reserved names are recognised however they are spaced or decorated",
            ChatTextSanitizer.IsReservedName("System") && ChatTextSanitizer.IsReservedName("S y s t e m") && ChatTextSanitizer.IsReservedName("ADMIN!")
            && ChatTextSanitizer.IsReservedName("[Server]") && !ChatTextSanitizer.IsReservedName("Systematic") && !ChatTextSanitizer.IsReservedName("Ava"));
    }

    // ---- The service ---------------------------------------------------------------------------------------------

    // A stand-in for a networked transport: records what was sent and blocked, and can deliver "remote" messages.
    internal sealed class SimulatedTransport : IChatTransport
    {
        private Action<ChatMessage>? _deliver;

        public List<ChatMessage> Sent { get; } = [];

        public List<(string Id, bool Blocked)> BlockCalls { get; } = [];

        public bool Detached { get; private set; }

        public string Name => "Simulated";

        public bool IsNetworked => true;

        public void Attach(Action<ChatMessage> deliver) => _deliver = deliver;

        public void Send(ChatMessage message)
        {
            Sent.Add(message);
            _deliver?.Invoke(message);
        }

        public void SetBlocked(string senderId, bool blocked) => BlockCalls.Add((senderId, blocked));

        public void Detach()
        {
            Detached = true;
            _deliver = null;
        }

        public void Remote(long id, string senderId, string name, string text, ChatMessageKind kind = ChatMessageKind.Player, string channel = "General") =>
            _deliver?.Invoke(new ChatMessage(id, channel, text, senderId, name, kind, 0d, 0d));

        /// <summary>A remote message with Data (as a networked transport would hand it over, unchecked).</summary>
        public void Remote(long id, string senderId, string name, string text, ChatMessageKind kind, IReadOnlyDictionary<string, object> data) =>
            _deliver?.Invoke(new ChatMessage(id, "General", text, senderId, name, kind, 0d, 0d) { Data = data });
    }

    public static void Service(TestReport report)
    {
        var now = 0d;
        var transport = new SimulatedTransport();
        var chat = new ChatService(transport, () => now);

        var error = chat.Send("  hi\nthere ", null, out var sent);
        report.Check("Chat service: Send makes a Player message from the local player in General, cleaned",
            error is null && sent is { Kind: ChatMessageKind.Player, SenderId: ChatService.LocalPlayerId, SenderName: "Player", Channel: "General", Text: "hi there" },
            error ?? sent?.ToString());
        report.Check("Chat service: the transport carries it and it waits for the next frame's delivery",
            transport.Sent.Count == 1 && chat.PendingCount == 1 && chat.FindChannel("General")!.Count == 0, $"pending {chat.PendingCount}");
        chat.TakePending();

        // Rate limit: a burst of 5 (one is used), then one per second.
        var accepted = 0;
        string? refusal = null;
        for (var i = 0; i < 10; i++)
        {
            if (chat.Send($"m{i}", null, out _) is { } why)
            {
                refusal ??= why;
            }
            else
            {
                accepted++;
            }
        }

        report.Check("Chat service: the rate limit lets a burst through (5 in all), then refuses", accepted == 4 && refusal is not null && chat.RateLimitedCount == 6,
            $"accepted {accepted}, refused {chat.RateLimitedCount}: {refusal}");
        now += 1.05d;
        report.Check("Chat service: a second later one more message may go", chat.Send("again", null, out _) is null && chat.Send("too soon", null, out _) is not null);
        chat.TakePending();

        // Channels.
        report.Check("Chat service: channel names are checked",
            chat.CreateChannel("") is not null && chat.CreateChannel("bad/name") is not null && chat.CreateChannel(" Team") is not null
            && chat.CreateChannel(new string('x', 33)) is not null && chat.CreateChannel("Team", maxLength: 0) is not null && chat.CreateChannel("Team", historySize: 501) is not null);
        report.Check("Chat service: a channel is made once", chat.CreateChannel("Team", 10, 3) is null && chat.CreateChannel("Team") is not null);
        report.Check("Chat service: News (players cannot send) refuses Send but takes Post",
            chat.CreateChannel("News", playersCanSend: false) is null && chat.Send("x", "News", out _) is not null && chat.Post("Patch notes", "News", null, out _) is null);
        report.Check("Chat service: no such channel is refused", chat.Send("x", "Nope", out _) is not null && chat.Post("x", "Nope", null, out _) is not null);
        now += 10d;
        chat.Send("0123456789ABCDEF", "Team", out var cut);
        report.Check("Chat service: text longer than the channel's MaxLength is cut", cut?.Text == "0123456789", cut?.Text);
        foreach (var message in chat.TakePending())
        {
            chat.Commit(message);
        }

        for (var i = 0; i < 5; i++)
        {
            chat.Post($"t{i}", "Team", null, out var posted);
            chat.Commit(posted!);
        }

        chat.TakePending();
        var team = chat.FindChannel("Team")!;
        report.Check("Chat service: the history keeps only the newest HistorySize messages",
            team.Count == 3 && string.Join(",", team.Recent(10).Select(m => m.Text)) == "t2,t3,t4", string.Join(",", team.Recent(10).Select(m => m.Text)));
        for (var i = chat.Channels.Count; i < ChatService.MaxChannels; i++)
        {
            chat.CreateChannel($"C{i}");
        }

        report.Check($"Chat service: at most {ChatService.MaxChannels} channels", chat.Channels.Count == ChatService.MaxChannels && chat.CreateChannel("OneTooMany") is not null);

        // System and NPC messages.
        chat.Post("Server restarts soon", null, null, out var system);
        chat.Post("Halt!", null, "Guard", out var npc);
        report.Check("Chat service: Post makes System messages: \"system\", or \"npc:Name\" with a SenderName",
            system is { Kind: ChatMessageKind.System, SenderId: "system", SenderName: "System" } && npc is { Kind: ChatMessageKind.System, SenderId: "npc:Guard", SenderName: "Guard" });
        chat.TakePending();

        // Names.
        report.Check("Chat service: the local player cannot take a reserved name",
            chat.SetLocalPlayerName("System") is not null && chat.SetLocalPlayerName(" A d m i n ") is not null && chat.SetLocalPlayerName("") is not null
            && chat.SetLocalPlayerName(new string('n', 40)) is not null && chat.LocalPlayerName == "Player");
        report.Check("Chat service: an ordinary name is taken (cleaned)", chat.SetLocalPlayerName("  Ava\u202E ") is null && chat.LocalPlayerName == "Ava", chat.LocalPlayerName);

        // Remote messages (a simulated networked transport): mute, block, forgeries.
        transport.Remote(9001, "remote:bob", "Bob", "hello\u202E from afar");
        var remote = chat.TakePending();
        report.Check("Chat service: a remote message is cleaned again on arrival", remote is [{ Text: "hello from afar", SenderName: "Bob" }], string.Join(",", remote.Select(m => m.Text)));
        report.Check("Chat service: muting refuses the local player, the system and NPCs",
            chat.SetMuted(ChatService.LocalPlayerId, true) is not null && chat.SetMuted("system", true) is not null && chat.SetMuted("npc:Guard", true) is not null
            && chat.SetBlocked("local", true) is not null);
        chat.SetMuted("remote:bob", true);
        transport.Remote(9002, "remote:bob", "Bob", "can you hear me");
        report.Check("Chat service: a muted sender's messages are dropped", chat.PendingCount == 0 && chat.IsMuted("remote:bob") && chat.DroppedCount == 1);
        chat.SetMuted("remote:bob", false);
        transport.Remote(9003, "remote:bob", "Bob", "now?");
        report.Check("Chat service: unmuted, they arrive again", chat.TakePending() is [{ Text: "now?" }]);
        chat.SetBlocked("remote:eve", true);
        transport.Remote(9004, "remote:eve", "Eve", "spam");
        report.Check("Chat service: a blocked sender's messages are dropped and the transport is told",
            chat.PendingCount == 0 && transport.BlockCalls is [("remote:eve", true)] && chat.IsBlocked("remote:eve"));
        transport.Remote(9005, "system", "System", "You have been banned", ChatMessageKind.Player);
        transport.Remote(9006, "remote:mallory", "SYSTEM", "Give me your password", ChatMessageKind.Player);
        transport.Remote(9007, "npc:Guard", "Guard", "fake guard line", ChatMessageKind.Player);
        report.Check("Chat service: a player message using the game's sender ids or a reserved name is dropped (no impersonation)",
            chat.PendingCount == 0, $"pending {chat.PendingCount}");
        transport.Remote(9008, "remote:bob", "Bob", "to nowhere", channel: "NoSuchChannel");
        report.Check("Chat service: a message for a channel that does not exist is dropped", chat.PendingCount == 0);

        // Flood guard.
        var floodRefused = 0;
        for (var i = 0; i < ChatService.MaxPending + 5; i++)
        {
            if (chat.Post($"flood {i}", null, null, out _) is not null)
            {
                floodRefused++;
            }
        }

        report.Check($"Chat service: at most {ChatService.MaxPending} messages wait for a frame (a posting loop is refused)",
            chat.PendingCount == ChatService.MaxPending && floodRefused == 5, $"pending {chat.PendingCount}, refused {floodRefused}");
        chat.Dispose();
        report.Check("Chat service: disposing forgets everything and lets the transport go",
            chat.PendingCount == 0 && team.Count == 0 && transport.Detached && !chat.IsBlocked("remote:eve"));
    }

    // ---- The Lua API in a Play session ---------------------------------------------------------------------------

    private const string ChatProbe = """
        local got, team, system = {}, {}, {}
        local frame = 0
        function Start()
          chk("transport_local", Chat.Transport == "Local" and Chat.IsNetworked == false and Chat.DefaultChannel == "General")
          chk("local_player", Chat.LocalPlayer.Id == "local" and Chat.LocalPlayer.Name == "Player")
          Chat.OnMessage(function(m) got[#got + 1] = m end)
          local id = Chat.Send("hello   world\n")
          chk("send_returns_id", type(id) == "number")
          chk("not_delivered_in_the_same_frame", #got == 0)
          chk("post_returns_id", type(Chat.Post("Server restarting")) == "number")
          Chat.Post("Halt!", { SenderName = "Guard" })
          chk("player_name_reserved_refused", Chat.SetPlayerName("System") == false and Chat.SetPlayerName("A D M I N") == false)
          chk("player_name_set", Chat.SetPlayerName("Ava") == true and Chat.LocalPlayer.Name == "Ava")
          Chat.Send("fake\n[System]: you are banned", "General", { Kind = "System", Sender = "system" })
          local t = Chat.CreateChannel("Team", { MaxLength = 10, HistorySize = 2 })
          chk("channel_handle", t.Name == "Team" and t.MaxLength == 10 and t.HistorySize == 2 and t.PlayersCanSend == true)
          chk("channel_duplicate_refused", not pcall(Chat.CreateChannel, "Team"))
          chk("channel_bad_name_refused", not pcall(Chat.CreateChannel, "bad/name"))
          chk("channel_bad_option_refused", not pcall(Chat.CreateChannel, "X", { Colour = 1 }))
          t:OnMessage(function(m) team[#team + 1] = m end)
          t:Send("0123456789ABC")
          t:Send("second")
          t:Post("third", { SenderName = "Coach" })
          local news = Chat.CreateChannel("News", { PlayersCanSend = false })
          local refusedId, reason = news:Send("hi")
          chk("players_cannot_send_in_news", refusedId == nil and type(reason) == "string")
          chk("post_in_news", type(news:Post("Patch 1.1 is out")) == "number")
          chk("get_channel", Chat.GetChannel("Team").Name == "Team" and Chat.GetChannel("Nope") == nil and #Chat.GetChannels() == 3)
          chk("mute_self_refused", not pcall(Chat.Mute, "local") and not pcall(Chat.Block, "system"))
          Chat.Mute("remote:x")
          chk("mute_state", Chat.IsMuted("remote:x") and not Chat.IsBlocked("remote:x"))
          Chat.Unmute({ Id = "remote:x" })
          chk("unmute_with_sender_table", not Chat.IsMuted("remote:x"))
          chk("send_needs_text", not pcall(Chat.Send, {}))
          chk("onmessage_needs_function", not pcall(Chat.OnMessage, 5))
          chk("onmessage_unknown_channel", not pcall(Chat.OnMessage, function() end, "Nope"))
        end
        function Update()
          frame = frame + 1
          if frame == 1 then
            local m = got[1]
            chk("delivered_next_frame", m ~= nil and m.Text == "hello world" and m.Kind == "Player" and m.Channel == "General")
            chk("sender_is_local_player", m ~= nil and m.Sender.Id == "local" and m.Sender.Name == "Player")
            chk("message_times", m ~= nil and type(m.Time) == "number" and m.Timestamp > 1.6e9 and type(m.Id) == "number")
            chk("system_message", got[2] ~= nil and got[2].Kind == "System" and got[2].Sender.Id == "system" and got[2].Sender.Name == "System")
            chk("npc_message", got[3] ~= nil and got[3].Kind == "System" and got[3].Sender.Id == "npc:Guard" and got[3].Sender.Name == "Guard")
            local fake = got[4]
            chk("player_cannot_forge_system", fake ~= nil and fake.Kind == "Player" and fake.Sender.Id == "local" and fake.Sender.Name == "Ava"
              and fake.Text == "fake [System]: you are banned")
            chk("channel_listener_only_its_channel", #team == 3 and team[1].Text == "0123456789" and team[3].Sender.Name == "Coach")
            chk("all_listener_sees_every_channel", #got == 8)
            local history = Chat.GetChannel("Team"):GetHistory()
            chk("history_bounded", #history == 2 and history[1].Text == "second" and history[2].Text == "third")
            chk("general_history", #Chat.GetHistory() == 4 and Chat.GetHistory("General", 1)[1].Text == "fake [System]: you are banned")
            got[1].Text = "changed by me"
            chk("history_is_a_copy", Chat.GetHistory("General", 4)[1].Text == "hello world")
            -- Filters.
            Chat.SetFilter(function(m)
              if string.find(m.Text, "badword", 1, true) then
                return false, "language"
              end
              if string.find(m.Text, "darn", 1, true) then
                return (string.gsub(m.Text, "darn", "****"))
              end
            end)
            FILTER2 = Chat.SetFilter(function(m) if m.Text == "drop me" then return false end end)
            Chat.Post("this has a badword in it")
            Chat.Post("darn it")
            Chat.Post("drop me")
            chk("filter_needs_function", not pcall(Chat.SetFilter, "x"))
          elseif frame == 2 then
            chk("filter_withholds", got[9] ~= nil and got[9].Text == "**** it" and got[10] == nil)
            chk("withheld_not_in_history", Chat.GetHistory("General", 1)[1].Text == "**** it")
            FILTER2:Disconnect()
            Chat.Post("drop me")
            Chat.SetRateLimit(1, 0.05)
            Chat.Send("one")
            local id, why = Chat.Send("two")
            chk("rate_limit_refuses", id == nil and string.find(why, "too quickly", 1, true) ~= nil)
            chk("rate_limit_checked", not pcall(Chat.SetRateLimit, 0, 1) and not pcall(Chat.SetRateLimit, 5, 1000))
          elseif frame == 3 then
            chk("disconnected_filter_no_longer_runs", got[10] ~= nil and got[10].Text == "drop me" and got[11] ~= nil and got[11].Text == "one")
          end
        end
        """;

    private const string FailingListener = """
        local calls = 0
        function Start()
          Chat.OnMessage(function(m) calls = calls + 1 error("listener broke") end)
        end
        """;

    private const string SecondChatUi = """
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 2 then
            local ok, message = pcall(Chat.EnableDefaultUI)
            chk("second_default_ui_refused", not ok and string.find(tostring(message), "already on", 1, true) ~= nil)
          end
        end
        """;

    public static void LuaApi(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "ChatLua");
        WriteScript(root, "ChatProbe.lua", ChkFunction + ChatProbe);
        WriteScript(root, "FailingListener.lua", FailingListener);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Chatter", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/ChatProbe.lua"));
        workspace.AddPart(MakePart("Broken", new Vector3(4, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/FailingListener.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        Step(play, 5, faults);
        AssertLuaChecks(report, "Chat (Lua)", log,
        [
            "transport_local", "local_player", "send_returns_id", "not_delivered_in_the_same_frame", "post_returns_id", "player_name_reserved_refused",
            "player_name_set", "channel_handle", "channel_duplicate_refused", "channel_bad_name_refused", "channel_bad_option_refused",
            "players_cannot_send_in_news", "post_in_news", "get_channel", "mute_self_refused", "mute_state", "unmute_with_sender_table", "send_needs_text",
            "onmessage_needs_function", "onmessage_unknown_channel", "delivered_next_frame", "sender_is_local_player", "message_times", "system_message",
            "npc_message", "player_cannot_forge_system", "channel_listener_only_its_channel", "all_listener_sees_every_channel", "history_bounded",
            "general_history", "history_is_a_copy", "filter_needs_function", "filter_withholds", "withheld_not_in_history", "rate_limit_refuses",
            "rate_limit_checked", "disconnected_filter_no_longer_runs",
        ]);
        var listenerFault = faults.Where(f => f.Diagnostic.ScriptName.Contains("FailingListener", StringComparison.Ordinal)).ToList();
        report.Check("Chat (Lua): a failing OnMessage listener is reported (Chat.OnMessage) and faults only its own script",
            listenerFault.Count == 1 && listenerFault[0].Diagnostic.Phase == Engine.Scripting.ScriptPhase.ChatMessage
            && faults.Count == 1 && LuaReports(log).GetValueOrDefault("disconnected_filter_no_longer_runs") == "true",
            string.Join(" | ", faults.Select(f => $"{f.Diagnostic.ScriptName} {Engine.Scripting.ScriptDiagnostic.PhaseName(f.Diagnostic.Phase)}: {f.Diagnostic.Message}")));
        var chat = play.Gameplay!.Chat;
        var general = string.Join("|", chat.FindChannel("General")!.Recent(100).Select(m => m.Text));
        report.Check("Chat (Lua): General's history holds exactly what was delivered there (withheld messages are not in it)",
            general == "hello world|Server restarting|Halt!|fake [System]: you are banned|**** it|drop me|one", general);
        play.Stop(workspace);
        play.Dispose();
        report.Check("Chat (Lua): Stop forgets the session's chat (history, waiting messages)", chat.FindChannel("General")!.Count == 0 && chat.PendingCount == 0);

        // A fresh Play starts with an empty chat.
        WriteScript(root, "Fresh.lua", ChkFunction + """
            function Start()
              chk("fresh_history_empty", #Chat.GetHistory() == 0 and Chat.GetChannel("Team") == nil and Chat.LocalPlayer.Name == "Player")
            end
            """);
        var fresh = new Workspace();
        fresh.AddPart(MakePart("Fresh", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Fresh.lua"));
        var freshLog = new OutputLog();
        var freshFaults = new List<PlayScriptFault>();
        var second = StartPlay(root, fresh, new GuiDocument(), freshLog, freshFaults);
        Step(second, 1, freshFaults);
        AssertLuaChecks(report, "Chat (Lua)", freshLog, ["fresh_history_empty"]);
        second.Stop(fresh);
        second.Dispose();
    }

    // Posts before the filter exists, then twice after it breaks.
    private const string ChatPoster = """
        local frame = 0
        function Start()
          Chat.Post("before the filter")
        end
        function Update()
          frame = frame + 1
          if frame == 3 then
            Chat.Post("first after the bug")
          elseif frame == 5 then
            Chat.Post("second after the bug")
          end
        end
        """;

    private const string FilterThatBreaks = """
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 2 then
            Chat.SetFilter(function(m) error("the filter has a bug") end)
          end
        end
        """;

    public static void FilterFailsClosed(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "ChatFilter");
        WriteScript(root, "Listener.lua", ChatPoster);
        WriteScript(root, "Filter.lua", FilterThatBreaks);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Listener", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Listener.lua"));
        workspace.AddPart(MakePart("Filter", new Vector3(3, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Filter.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        Step(play, 8, faults);
        var chat = play.Gameplay!.Chat;
        var history = chat.FindChannel("General")!.Recent(10).Select(m => m.Text).ToList();
        var warning = Enumerable.Range(0, log.Count).Select(i => log[i].Message).FirstOrDefault(m => m.Contains("chat is closed", StringComparison.Ordinal));
        report.Check("Chat filters fail closed: a filter error withholds that message and every later one, and says so once",
            history.SequenceEqual(["before the filter"]) && warning is not null && faults.Count == 1 && faults[0].Diagnostic.Phase == Engine.Scripting.ScriptPhase.ChatFilter,
            $"history [{string.Join("|", history)}], warning: {warning ?? "none"}, faults {faults.Count}");
        play.Stop(workspace);
        play.Dispose();
    }

    // ---- TextBox -------------------------------------------------------------------------------------------------

    private static readonly GuiRect Canvas = new(0f, 0f, 800f, 600f);

    private static EditorKeyEvent Key(EditorKey key, KeyModifierFlags modifiers = KeyModifierFlags.None) => new(key, modifiers, false);

    public static void TextBox(TestReport report, string dir)
    {
        var document = new GuiDocument();
        var box = document.Add(new GuiTextBox { Name = "Box", Position = new GuiDim2(0f, 100f, 0f, 100f), Size = new GuiDim2(0f, 300f, 0f, 36f), MaxLength = 12 });
        var button = document.Add(new GuiButton { Name = "Under", Position = new GuiDim2(0f, 100f, 0f, 300f), Size = new GuiDim2(0f, 200f, 0f, 50f) });
        document.Add(new GuiTextBox { Name = "Cover", Position = new GuiDim2(0f, 100f, 0f, 300f), Size = new GuiDim2(0f, 100f, 0f, 50f), ZOrder = 5 });
        var runtime = new GuiRuntime(document);
        var events = new List<string>();
        var clicks = 0;
        runtime.TextBoxSubmitted += (id, text) => events.Add($"submit:{text}");
        runtime.TextBoxFocusLost += (id, submitted) => events.Add($"lost:{submitted}");
        runtime.TextBoxFocused += id => events.Add("focused");
        runtime.ButtonClicked += id => clicks++;
        var copy = (GuiTextBox)runtime.Document.Find(box.Id)!;
        runtime.Update(Canvas);

        var consumed = runtime.UpdatePointer(Canvas, 150f, 118f, true, true, true, false);
        runtime.UpdatePointer(Canvas, 150f, 118f, true, false, false, true);
        report.Check("TextBox: a click focuses it and is used up (nothing behind it gets the press)", consumed && runtime.FocusedTextBoxId == box.Id && events.SequenceEqual(["focused"]),
            string.Join(",", events));
        runtime.HandleKeyboard("helo".ToCharArray(), [Key(EditorKey.Left), Key(EditorKey.Left)], null);
        runtime.HandleKeyboard(['l'], [], null);
        report.Check("TextBox: typing goes in at the caret (Left moves it)", copy.Text == "hello" && runtime.CaretIndex == 3, $"{copy.Text} caret {runtime.CaretIndex}");
        runtime.HandleKeyboard([], [Key(EditorKey.End), Key(EditorKey.Backspace), Key(EditorKey.Home), Key(EditorKey.Delete)], null);
        report.Check("TextBox: Backspace, Delete, Home and End edit the text", copy.Text == "ell" && runtime.CaretIndex == 0, $"{copy.Text} caret {runtime.CaretIndex}");
        runtime.HandleKeyboard([], [Key(EditorKey.End), Key(EditorKey.V, KeyModifierFlags.Control)], () => " o\nwor\u0007ld and more");
        report.Check("TextBox: paste is one line, without control characters, cut to MaxLength", copy.Text == "ell o world " && copy.Text.Length == 12, $"'{copy.Text}'");
        runtime.HandleKeyboard("xyz".ToCharArray(), [], null);
        report.Check("TextBox: nothing more fits once MaxLength is reached", copy.Text == "ell o world ", copy.Text);
        copy.ClearOnSubmit = true;
        events.Clear();
        runtime.HandleKeyboard([], [Key(EditorKey.Enter)], null);
        report.Check("TextBox: Enter submits the text, ClearOnSubmit empties it, and the box lets go",
            events.SequenceEqual(["submit:ell o world ", "lost:True"]) && copy.Text.Length == 0 && runtime.FocusedTextBoxId is null, string.Join(",", events));
        report.Check("TextBox: without focus the keyboard is not the GUI's", !runtime.HandleKeyboard("a".ToCharArray(), [], null) && copy.Text.Length == 0);

        // Escape lets go; scripts cannot refocus at once.
        runtime.Focus(box.Id, byScript: false);
        events.Clear();
        runtime.ReleaseFocus(submitted: false, byEscape: true);
        var refused = runtime.Focus(box.Id, byScript: true);
        report.Check("TextBox: after Escape a script cannot take the keyboard back at once (the next Escape stops Play)",
            events.SequenceEqual(["lost:False"]) && refused is not null && runtime.FocusedTextBoxId is null && runtime.Focus(box.Id, byScript: false) is null,
            refused ?? "not refused");
        runtime.UpdatePointer(Canvas, 700f, 500f, true, true, true, false);
        runtime.UpdatePointer(Canvas, 700f, 500f, true, false, false, true);
        report.Check("TextBox: a click elsewhere lets go of it", runtime.FocusedTextBoxId is null);
        runtime.Focus(box.Id, byScript: false);
        copy.Visible = false;
        runtime.ReleaseUnclickable();
        report.Check("TextBox: hiding it lets go of the keyboard", runtime.FocusedTextBoxId is null);
        copy.Visible = true;
        runtime.Update(Canvas);
        runtime.UpdatePointer(Canvas, 150f, 320f, true, true, true, false);
        runtime.UpdatePointer(Canvas, 150f, 320f, true, false, false, true);
        report.Check("TextBox: a box over a Button takes the click (the Button underneath is not clicked)",
            clicks == 0 && runtime.FocusedTextBoxId is not null && runtime.FocusedTextBoxId != button.Id);
        runtime.UpdatePointer(Canvas, 250f, 320f, true, true, true, false);
        runtime.UpdatePointer(Canvas, 250f, 320f, true, false, false, true);
        report.Check("TextBox: the Button beside it still clicks", clicks == 1);
        runtime.Dispose();

        // UI.json keeps a TextBox (never read back as a Button).
        var saved = new GuiDocument();
        saved.Add(new GuiTextBox { Name = "Field", Text = "draft", PlaceholderText = "Type here", MaxLength = 40, FontSize = 22, ClearOnSubmit = true, TextColor = new GuiColor(1, 2, 3) });
        var path = Path.Combine(dir, "TextBoxGui", "UI.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ok = GuiFile.TrySave(path, saved, out var saveError);
        var loaded = GuiFile.Load(path, out var loadReport);
        report.Check("TextBox: UI.json saves and reads it back as a TextBox with its properties",
            ok && loaded.Canvas.Children is [GuiTextBox { Name: "Field", Text: "draft", PlaceholderText: "Type here", MaxLength: 40, FontSize: 22, ClearOnSubmit: true } field]
            && field.TextColor == new GuiColor(1, 2, 3) && loadReport.Repairs.Count == 0,
            saveError ?? string.Join("; ", loadReport.Repairs));
        report.Check("TextBox: text is kept one line and within MaxLength", new GuiTextBox { MaxLength = 5, Text = "ab\ncdefgh" }.Text == "ab cd");
    }

    private const string TextBoxProbe = """
        local submitted, lost, focusedEvents = {}, {}, 0
        local frame = 0
        function Start()
          BOX = UI.Create("TextBox", { Name = "Field", PlaceholderText = "Say something", MaxLength = 50, ClearOnSubmit = true,
            Position = { 0, 10, 0, 10 }, Size = { 0, 300, 0, 36 } })
          chk("created", BOX.Type == "TextBox" and BOX.PlaceholderText == "Say something" and BOX.MaxLength == 50 and BOX.ClearOnSubmit == true and BOX.Text == "")
          chk("focused_read_only", not pcall(function() BOX.Focused = true end))
          chk("textbox_has_no_onclick", not pcall(function() return BOX.OnClick end))
          chk("button_has_no_focus", not pcall(function() return UI.Create("Button").Focus end))
          BOX:OnSubmit(function(text) submitted[#submitted + 1] = text end)
          BOX:OnFocusLost(function(wasSubmitted) lost[#lost + 1] = wasSubmitted end)
          BOX:OnFocused(function() focusedEvents = focusedEvents + 1 end)
          chk("not_typing_yet", Input.IsTyping == false and BOX.Focused == false)
          chk("focus", BOX:Focus() == true and BOX.Focused == true and Input.IsTyping == true)
          local width, height = UI.MeasureText("Hello", 20)
          chk("measure_text", width > 0 and height > 0 and UI.MeasureText("Hello world", 20) > width)
          chk("measure_text_checks", not pcall(UI.MeasureText, {}))
        end
        function Update()
          frame = frame + 1
          if frame == 3 then
            chk("submitted_text", submitted[1] == "hi there" and lost[1] == true and focusedEvents == 1)
            chk("cleared_and_released", BOX.Text == "" and BOX.Focused == false and Input.IsTyping == false)
            BOX.Text = "line one\nline two"
            chk("script_text_one_line", BOX.Text == "line one line two")
            BOX:Focus()
            BOX:Release()
            chk("release", BOX.Focused == false)
          end
        end
        """;

    public static void TextBoxLua(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "TextBoxLua");
        WriteScript(root, "Box.lua", ChkFunction + TextBoxProbe);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Holder", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Box.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        Step(play, 1, faults);
        // The player types while the box has focus (as the editor routes the keyboard), then presses Enter.
        var typed = play.Gui!.HandleKeyboard("hi there".ToCharArray(), [Key(EditorKey.Enter)], null);
        Step(play, 3, faults);
        report.Check("TextBox (Lua): the focused box took the keyboard", typed);
        AssertLuaChecks(report, "TextBox (Lua)", log,
            ["created", "focused_read_only", "textbox_has_no_onclick", "button_has_no_focus", "not_typing_yet", "focus", "measure_text", "measure_text_checks",
             "submitted_text", "cleared_and_released", "script_text_one_line", "release"]);
        AssertNoFaults(report, "TextBox (Lua)", faults);
        play.Stop(workspace);
        play.Dispose();
    }

    // ---- The default chat window ---------------------------------------------------------------------------------

    private const string DefaultUiProbe = """
        local frame = 0
        local function rows()
          local result = {}
          for _, child in ipairs(UI.Find("WiskoChat"):GetChildren()) do
            if child.Type == "Text" and child.Visible then
              result[#result + 1] = child
            end
          end
          return result
        end
        function Start()
          Chat.Post("Welcome to the showcase")
          Chat.EnableDefaultUI({ Width = 360, Height = 160, FontSize = 16 })
          chk("window_made", UI.Find("WiskoChat") ~= nil and UI.FindPath("WiskoChat/Input").Type == "TextBox")
          chk("input_limits", UI.FindPath("WiskoChat/Input").MaxLength == 200 and UI.FindPath("WiskoChat/Input").PlaceholderText == "Press / to chat")
        end
        function Update()
          frame = frame + 1
          if frame == 1 then
            local shown = rows()
            chk("system_line_gold", #shown == 1 and shown[1].Text == "Welcome to the showcase" and shown[1].TextColor == "#FFD166")
            Chat.Post("Halt! Who goes there?", { SenderName = "Guard" })
            Chat.Send(string.rep("long ", 30) .. "end")
          elseif frame == 2 then
            local shown = rows()
            chk("npc_line", shown[2] ~= nil and shown[2].Text == "Guard: Halt! Who goes there?")
            chk("long_message_wraps", #shown >= 5 and string.sub(shown[3].Text, 1, 8) == "Player: " and shown[3].TextColor == "#F2F4F8")
            chk("continuation_indented", select(2, shown[4]:GetPosition()) > select(2, shown[3]:GetPosition()))
            local fits = true
            for _, row in ipairs(shown) do
              local _, offset = row:GetPosition()
              if UI.MeasureText(row.Text, 16) > 360 - 16 - (offset - 8) + 0.01 then
                fits = false
              end
            end
            chk("lines_fit_the_window", fits)
          elseif frame == 5 then
            chk("focus_key_starts_typing", Input.IsTyping == true and UI.FindPath("WiskoChat/Input").Focused == true)
          elseif frame == 8 then
            local shown = rows()
            chk("typed_message_shown", shown[#shown].Text == "Player: typed in the box")
            chk("box_cleared", UI.FindPath("WiskoChat/Input").Text == "")
            Chat.SetRateLimit(1, 0.05)
            Chat.Send("uses the burst")
          elseif frame == 10 then
            local shown = rows()
            local tail = shown[#shown - 1].Text .. " " .. shown[#shown].Text
            chk("rate_notice_shown", string.find(tail, "too quickly", 1, true) ~= nil and shown[#shown].TextColor == "#FF8A80")
            Chat.DisableDefaultUI()
            chk("window_gone", UI.Find("WiskoChat") == nil)
            Chat.EnableDefaultUI()
            chk("window_back_with_history", UI.Find("WiskoChat") ~= nil and #rows() > 0)
          end
        end
        """;

    public static void DefaultUi(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "ChatUi");
        WriteScript(root, "ChatWindow.lua", ChkFunction + DefaultUiProbe);
        WriteScript(root, "Second.lua", ChkFunction + SecondChatUi);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("ChatHost", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/ChatWindow.lua"));
        workspace.AddPart(MakePart("Other", new Vector3(3, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Second.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        play.Gui!.Update(Canvas);
        Step(play, 3, faults);
        StepHolding(play, 1, faults, "Slash");
        Step(play, 1, faults);
        var focused = play.Gui.FocusedTextBoxId is not null;
        play.Gui.HandleKeyboard("typed in the box".ToCharArray(), [Key(EditorKey.Enter)], null);
        Step(play, 2, faults);
        // The rate-limit notice: the box submits a second message within the 1-message burst.
        Step(play, 1, faults);
        play.Gui.Focus(play.Gui.Document.Elements.First(e => e is GuiTextBox).Id, byScript: false);
        play.Gui.HandleKeyboard("too fast".ToCharArray(), [Key(EditorKey.Enter)], null);
        Step(play, 3, faults);
        report.Check("Chat window: the focus key (/) gave its box the keyboard", focused);
        AssertLuaChecks(report, "Chat window", log,
            ["window_made", "input_limits", "system_line_gold", "npc_line", "long_message_wraps", "continuation_indented", "lines_fit_the_window",
             "focus_key_starts_typing", "typed_message_shown", "box_cleared", "rate_notice_shown", "window_gone", "window_back_with_history", "second_default_ui_refused"]);
        AssertNoFaults(report, "Chat window", faults);
        play.Stop(workspace);
        play.Dispose();
    }

    // ---- Stress ----------------------------------------------------------------------------------------------------

    private const string ChatSpam = """
        local received = 0
        local frame = 0
        function Start()
          Chat.CreateChannel("Spam", { HistorySize = 50 })
          Chat.OnMessage(function() received = received + 1 end)
        end
        function Update()
          frame = frame + 1
          if frame <= 20 then
            for i = 1, 600 do
              Chat.Post("spam " .. frame .. "/" .. i, "Spam")
            end
          elseif frame == 22 then
            print("RECEIVED " .. received .. " HISTORY " .. #Chat.GetHistory("Spam"))
          end
        end
        """;

    public static void Stress(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "ChatSpam");
        WriteScript(root, "Spam.lua", ChatSpam);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Spammer", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Spam.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Step(play, 23, faults);
        watch.Stop();
        var line = Enumerable.Range(0, log.Count).Select(i => log[i].Message).FirstOrDefault(m => m.StartsWith("RECEIVED", StringComparison.Ordinal)) ?? "none";
        var chat = play.Gameplay!.Chat;
        report.Check("Chat stress: 12,000 posts over 20 frames are all delivered, the history stays bounded (50) and nothing waits",
            line == "RECEIVED 12000 HISTORY 50" && chat.PendingCount == 0 && faults.Count == 0, $"{line}; {watch.Elapsed.TotalMilliseconds:F0} ms for 23 frames");
        TestReport.Info($"chat stress: 12,000 messages (600 per frame) through Post, delivery and one listener took {watch.Elapsed.TotalMilliseconds:F0} ms over 23 frames");
        play.Stop(workspace);
        play.Dispose();
    }
}
