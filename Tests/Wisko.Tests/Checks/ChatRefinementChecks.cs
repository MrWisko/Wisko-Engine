using Wisko.Engine.Chat;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Chat as scripting capabilities for the developer's own chat: a message's Data (the developer's values for styling,
/// checked and cleaned, never the sender or the kind), the default window's placement and colours (and Data.Color),
/// no chat interface unless a script asks for one, and the custom chat example (examples/chat) played as it ships.
/// </summary>
internal static class ChatRefinementChecks
{
    private static readonly GuiRect Canvas = new(0f, 0f, 1280f, 720f);

    private static EditorKeyEvent Key(EditorKey key) => new(key, KeyModifierFlags.None, false);

    public static void Data(TestReport report)
    {
        var good = ChatData.TryClean(
            [new("Color", "#FF0000"), new("Team", "Red‮\nTeam"), new("Level", 3), new("Leader", true), new("Score", 2.5f)], out var data);
        report.Check("Chat Data: strings (cleaned), numbers and booleans are kept",
            good is null && (string)data["Color"] == "#FF0000" && (string)data["Team"] == "Red Team" && (double)data["Level"] == 3d
            && (bool)data["Leader"] && (double)data["Score"] == 2.5d, good);

        foreach (var (name, raw, expected) in new (string, KeyValuePair<string, object?>[], string)[]
                 {
                     ("a key that is not a name", [new("2fast", 1)], "Data keys are names"),
                     ("a key with a space", [new("my key", 1)], "Data keys are names"),
                     ("a 33-character key", [new(new string('k', 33), 1)], "Data keys are names"),
                     ("a table value", [new("Nested", new Dictionary<string, object>())], "must be a string, a number or a boolean"),
                     ("an infinite number", [new("Far", double.PositiveInfinity)], "finite number"),
                     ("17 values", Enumerable.Range(0, 17).Select(i => new KeyValuePair<string, object?>($"K{i}", i)).ToArray(), "at most 16"),
                 })
        {
            var problem = ChatData.TryClean(raw, out var refused);
            report.Check($"Chat Data: {name} is refused with a reason", problem?.Contains(expected, StringComparison.Ordinal) == true && refused.Count == 0, problem);
        }

        var longText = ChatData.TryClean([new("Note", new string('x', 500))], out var cut);
        report.Check("Chat Data: a long string value is cut to 200 characters", longText is null && ((string)cut["Note"]).Length == ChatData.MaxStringLength);

        var transport = new ChatChecks.SimulatedTransport();
        using var chat = new ChatService(transport, () => 0d);
        var sent = chat.Send("hi", null, [new("Team", "Blue")], out var message);
        var refusedSend = chat.Send("hi", null, [new("Bad Key", 1)], out var none);
        report.Check("Chat Data: Send carries it; invalid Data refuses the message (nothing is sent)",
            sent is null && message!.Data["Team"] is "Blue" && message.Kind == ChatMessageKind.Player && message.SenderId == ChatService.LocalPlayerId
            && refusedSend is not null && none is null);
        var posted = chat.Post("halt", null, "Guard", [new("Team", "Red")], out var npc);
        report.Check("Chat Data: Post carries it; the sender and kind stay the engine's (System, npc:Guard)",
            posted is null && npc!.Data["Team"] is "Red" && npc.Kind == ChatMessageKind.System && npc.SenderId == "npc:Guard");

        // From a transport: Data is checked again; a message with bad Data is dropped.
        var before = chat.DroppedCount;
        transport.Remote(9101, "remote:ann", "Ann", "hello", ChatMessageKind.Player, new Dictionary<string, object> { ["Team"] = "Blue​" });
        transport.Remote(9102, "remote:bob", "Bob", "hello", ChatMessageKind.Player, new Dictionary<string, object> { ["Bad Key"] = 1d });
        var pending = chat.TakePending().Where(m => m.Id >= 9101).ToList();
        report.Check("Chat Data: arriving from a transport it is cleaned again; bad Data drops the message",
            pending.Count == 1 && pending[0].Data["Team"] is "Blue" && chat.DroppedCount == before + 1, string.Join(", ", pending.Select(m => m.SenderName)));
    }

    private const string DataProbe = """
        local got = {}
        function Start()
          Chat.OnMessage(function(m) got[#got + 1] = m end)
          Chat.Send("styled", { Data = { Color = "#5AA9FF", Team = "Blue", Level = 3, Leader = true } })
          Chat.Send("plain", "General")
          Chat.Post("npc", { SenderName = "Guard", Data = { Team = "Red", Kind = "Player", Sender = "local" } })
          local general = Chat.GetChannel("General")
          general:Send("via channel", { Data = { Team = "Green" } })
          general:Post("channel post", { SenderName = "Herald", Data = { Team = "Gold" } })
          local function errorOf(f) local ok, e = pcall(f) return not ok and tostring(e) or "" end
          chk("data_bad_key", string.find(errorOf(function() Chat.Send("x", { Data = { ["two words"] = 1 } }) end), "Data keys are names", 1, true) ~= nil)
          chk("data_nested_table", string.find(errorOf(function() Chat.Send("x", { Data = { Inner = {} } }) end), "must be a string, a number or a boolean", 1, true) ~= nil)
          chk("data_infinite", string.find(errorOf(function() Chat.Send("x", { Data = { Far = math.huge } }) end), "finite number", 1, true) ~= nil)
          local many = {}
          for i = 1, 17 do many["K" .. i] = i end
          chk("data_too_many", string.find(errorOf(function() Chat.Send("x", { Data = many }) end), "at most 16", 1, true) ~= nil)
          chk("data_not_a_table", string.find(errorOf(function() Chat.Send("x", { Data = "red" }) end), "Data must be a table", 1, true) ~= nil)
          chk("send_unknown_option", string.find(errorOf(function() Chat.Send("x", { Kind = "System" }) end), "Kind", 1, true) ~= nil)
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 2 then
            local byText = {}
            for _, m in ipairs(got) do byText[m.Text] = m end
            local styled, plain, npc = byText["styled"], byText["plain"], byText["npc"]
            chk("data_delivered", styled ~= nil and styled.Data.Color == "#5AA9FF" and styled.Data.Team == "Blue" and styled.Data.Level == 3 and styled.Data.Leader == true)
            chk("data_empty_table_when_none", plain ~= nil and type(plain.Data) == "table" and next(plain.Data) == nil)
            chk("data_cannot_change_sender_or_kind", npc ~= nil and npc.Kind == "System" and npc.Sender.Id == "npc:Guard" and npc.Data.Kind == "Player" and npc.Data.Sender == "local")
            chk("data_via_channel_handle", byText["via channel"] ~= nil and byText["via channel"].Data.Team == "Green" and byText["channel post"].Data.Team == "Gold")
            local history = Chat.GetHistory()
            local seen = {}
            for _, m in ipairs(history) do seen[m.Text] = m end
            chk("data_in_history", seen["styled"] ~= nil and seen["styled"].Data.Team == "Blue" and seen["styled"].Data.Leader == true
              and seen["plain"] ~= nil and next(seen["plain"].Data) == nil and #history == 5)
            seen["styled"].Data.Team = "Changed"
            chk("data_copies_are_independent", Chat.GetHistory()[1].Data.Team == "Blue")
          end
        end
        """;

    public static void DataLua(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "ChatData");
        WriteScript(root, "Data.lua", ChkFunction + DataProbe);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Host", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Data.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        Step(play, 3, faults);
        AssertLuaChecks(report, "Chat Data (Lua)", log,
            ["data_bad_key", "data_nested_table", "data_infinite", "data_too_many", "data_not_a_table", "send_unknown_option", "data_delivered",
             "data_empty_table_when_none", "data_cannot_change_sender_or_kind", "data_via_channel_handle", "data_in_history", "data_copies_are_independent"]);
        AssertNoFaults(report, "Chat Data (Lua)", faults);
        play.Stop(workspace);
        play.Dispose();
    }

    private const string StyledWindow = """
        function Start()
          local ok, problem = pcall(Chat.EnableDefaultUI, { Colour = "#FF0000" })
          chk("unknown_option_refused", not ok and string.find(tostring(problem), "unknown option Colour", 1, true) ~= nil
            and string.find(tostring(problem), "PlayerColor", 1, true) ~= nil)
          local wrongType, typeProblem = pcall(Chat.EnableDefaultUI, { Width = "wide" })
          chk("wrong_type_refused", not wrongType and string.find(tostring(typeProblem), "Width must be a number", 1, true) ~= nil)
          Chat.EnableDefaultUI({ Position = { 1, -20, 0, 20 }, Anchor = { 1, 0 }, BackgroundColor = "#202830", BackgroundOpacity = 0.5,
            PlayerColor = "#AAFFAA", SystemColor = "#FFAAAA", InputColor = "#303840" })
          local window = UI.Find("WiskoChat")
          local xs, xo, ys, yo = window:GetPosition()
          chk("placed_top_right", xs == 1 and xo == -20 and ys == 0 and yo == 20)
          chk("log_colour", UI.Find("Log") ~= nil and UI.Find("Log").Opacity == 0.5)
          Chat.Post("plain announcement")
          Chat.Post("red alert", { Data = { Color = "#FF3030" } })
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 2 then
            local colours = {}
            for i = 1, 40 do
              local row = UI.Find("Line" .. i)
              if row ~= nil and row.Visible then colours[row.Text] = row.TextColor end
            end
            chk("system_colour_option", colours["plain announcement"] ~= nil)
            chk("data_colour_used", colours["red alert"] ~= nil)
            print("COLOURS " .. tostring(colours["plain announcement"]) .. " | " .. tostring(colours["red alert"]))
          end
        end
        """;

    public static void DefaultUiOptions(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "ChatStyled");
        WriteScript(root, "Styled.lua", ChkFunction + StyledWindow);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Host", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Styled.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        play.Gui!.Update(Canvas);
        Step(play, 3, faults);
        AssertLuaChecks(report, "Chat window options", log,
            ["unknown_option_refused", "wrong_type_refused", "placed_top_right", "log_colour", "system_colour_option", "data_colour_used"]);
        var colours = Enumerable.Range(0, log.Count).Select(i => log[i].Message).FirstOrDefault(t => t.Contains("COLOURS ", StringComparison.Ordinal)) ?? string.Empty;
        var rows = play.Gui.Document.Elements.OfType<GuiText>().Where(t => t.Name.StartsWith("Line", StringComparison.Ordinal)).ToList();
        var plain = rows.FirstOrDefault(r => r.Text == "plain announcement");
        var alert = rows.FirstOrDefault(r => r.Text == "red alert");
        report.Check("Chat window options: SystemColor colours announcements; a message's Data.Color wins over its kind's colour",
            plain is not null && alert is not null && plain.TextColor == ColorFromHex("#FFAAAA") && alert.TextColor == ColorFromHex("#FF3030"), colours);
        AssertNoFaults(report, "Chat window options", faults);
        play.Stop(workspace);
        play.Dispose();
    }

    private const string NoChatScript = """
        function Update() end
        """;

    /// <summary>A new project of either template, and a game whose scripts never touch Chat: no chat interface, and "/" does nothing.</summary>
    public static void NoChatUnlessAsked(TestReport report, string dir)
    {
        var store = new ProjectStore(Path.Combine(dir, "Projects"));
        foreach (var template in new[] { WorldTemplate.Classic, WorldTemplate.Testing })
        {
            var workspace = new Workspace();
            if (!store.TryCreateProject($"Fresh {template}", template, workspace, out var project, out var error))
            {
                report.Check($"No chat unless asked: a new {template} project is made", false, error);
                continue;
            }

            SceneFile.TryLoadScene(project.ScenePath, null, out var contents, out _);
            var world = new Workspace();
            foreach (var part in contents.Parts)
            {
                world.AddPart(part);
            }

            var log = new OutputLog();
            var faults = new List<PlayScriptFault>();
            var play = StartPlay(project.FolderPath, world, GuiFile.Load(project.UiPath, out _), log, faults);
            play.Gui!.Update(Canvas);
            Step(play, 2, faults);
            StepHolding(play, 1, faults, "Slash");
            Step(play, 2, faults);
            report.Check($"No chat unless asked: a new {template} project in Play shows no chat interface, and / focuses nothing",
                play.Gui.Document.Elements.Any() == false && play.Gui.FocusedTextBoxId is null && play.Gameplay!.Chat.FindChannel("General")!.Count == 0,
                $"{play.Gui.Document.Elements.Count()} GUI elements");
            play.Stop(world);
            play.Dispose();
        }

        var root = Path.Combine(dir, "Quiet");
        WriteScript(root, "Quiet.lua", NoChatScript);
        var quiet = new Workspace();
        quiet.AddPart(MakePart("Quiet", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Quiet.lua"));
        var quietLog = new OutputLog();
        var quietFaults = new List<PlayScriptFault>();
        var quietPlay = StartPlay(root, quiet, new GuiDocument(), quietLog, quietFaults);
        quietPlay.Gui!.Update(Canvas);
        Step(quietPlay, 2, quietFaults);
        StepHolding(quietPlay, 1, quietFaults, "Slash");
        Step(quietPlay, 2, quietFaults);
        report.Check("No chat unless asked: a game whose scripts never use Chat has no chat GUI and / does nothing",
            quietPlay.Gui.Document.Elements.Any() == false && quietPlay.Gui.FocusedTextBoxId is null && quietFaults.Count == 0);
        quietPlay.Stop(quiet);
        quietPlay.Dispose();
    }

    /// <summary>examples/chat/CustomChat.lua as it ships: its own panel, T to type, Enter to send, team colours in Data, /me.</summary>
    public static void CustomChatExample(TestReport report, string dir)
    {
        var example = Path.Combine(RepositoryRoot(), "examples", "chat", "CustomChat.lua");
        if (!File.Exists(example))
        {
            report.Check("Custom chat example: examples/chat/CustomChat.lua exists", false, example);
            return;
        }

        var root = Path.Combine(dir, "CustomChat");
        WriteScript(root, "CustomChat.lua", File.ReadAllText(example));
        var workspace = new Workspace();
        workspace.AddPart(MakePart("ChatHost", new Vector3(0, 3, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/CustomChat.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        var gui = play.Gui!;
        gui.Update(Canvas);
        Step(play, 2, faults);
        StepHolding(play, 1, faults, "T");
        Step(play, 1, faults);
        var focused = gui.FocusedTextBoxId is { } id && gui.Document.Elements.First(e => e.Id == id).Name == "CustomChatInput";
        gui.HandleKeyboard("hello there".ToCharArray(), [Key(EditorKey.Enter)], null);
        Step(play, 3, faults);
        gui.Focus(gui.Document.Elements.First(e => e.Name == "CustomChatInput").Id, byScript: false);
        gui.HandleKeyboard("/me waves".ToCharArray(), [Key(EditorKey.Enter)], null);
        Step(play, 3, faults);

        var rows = gui.Document.Elements.OfType<GuiText>().Where(t => t.Name.StartsWith("Row", StringComparison.Ordinal))
            .OrderBy(t => int.Parse(t.Name[3..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var shown = rows.Select(r => r.Text).Where(t => t.Length > 0).ToList();
        GuiText? Row(string text) => rows.FirstOrDefault(r => r.Text == text);
        report.Check("Custom chat example: its own panel and text box, and no default chat window", gui.Document.Elements.Any(e => e.Name == "CustomChat")
            && !gui.Document.Elements.Any(e => e.Name == "WiskoChat"));
        report.Check("Custom chat example: T starts typing in its box", focused);
        report.Check("Custom chat example: the player's message in the team colour, the guard's answer in red, the emote as an action",
            Row("Player: hello there")?.TextColor == ColorFromHex("#5AA9FF") && Row("Red Guard: Halt! Who goes there?")?.TextColor == ColorFromHex("#FF6B6B")
            && Row("* Player waves") is not null && Row("Custom chat ready. Press T to talk.")?.TextColor == ColorFromHex("#FFD166"),
            string.Join(" | ", shown));
        AssertNoFaults(report, "Custom chat example", faults);
        play.Stop(workspace);
        play.Dispose();
    }

    private static GuiColor ColorFromHex(string hex) =>
        new(Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Wisko.sln")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("the repository (Wisko.sln) was not found above the test binaries");
    }
}
