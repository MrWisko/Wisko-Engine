using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Lua V4 gameplay systems built only from the public Lua API: the example scripts in examples/gameplay-basics/Scripts,
/// played headless through PlayModeController with scripted keyboard input (ScriptInputState), on the editor's
/// Baseplate. Each scenario adds a small test-only Probe.lua that reports what it sees; nothing here is engine code.
/// </summary>
internal static class GameplaySystemsV4Checks
{
    private static readonly GuiRect Canvas = new(0, 0, 1280, 720);

    private sealed record Session(PlayModeController Play, OutputLog Log, List<PlayScriptFault> Faults);

    private static Session Begin(string root, Workspace workspace, StubCamera? camera = null)
    {
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = new PlayModeController { Output = log, CameraHost = camera ?? new StubCamera(), PhysicsEnvironment = LuaV4Demo.Baseplate() };
        if (!play.TryStart(root, workspace, [], null, out var error))
        {
            throw new InvalidOperationException($"PlayModeController.TryStart failed: {error}");
        }

        faults.AddRange(play.TakeNewFaults());
        return new Session(play, log, faults);
    }

    private static void End(Session session, Workspace workspace)
    {
        session.Play.Stop(workspace);
        session.Play.Dispose();
    }

    private static string NewProject(string dir, string probe)
    {
        var root = Path.Combine(dir, "Proj");
        LuaV4Demo.CopyScripts(root);
        WriteScript(root, "Probe.lua", ChkFunction + probe);
        return root;
    }

    private static Part Add(Workspace workspace, string name, Vector3 position, Vector3 size, bool anchored, params string[] scripts)
    {
        var part = MakePart(name, position, size, anchored, scripts.Select(s => $"Assets/Scripts/{s}.lua").ToArray());
        workspace.AddPart(part);
        return part;
    }

    private static Part AddProbe(Workspace workspace)
    {
        var probe = Add(workspace, "Probe", new Vector3(0, 0.5f, 120), new Vector3(1, 1, 1), true, "Probe");
        probe.CanCollide = false;
        probe.Visible = false;
        return probe;
    }

    // Keys held for every one of these frames (pressed on the first); call Step afterwards to release them.
    private static void Hold(Session s, int frames, params string[] keys)
    {
        var input = new ScriptInputState();
        for (var i = 0; i < frames; i++)
        {
            foreach (var key in keys)
            {
                ScriptInputState.TryGetKeyIndex(key, out var index);
                input.SetKey(index, down: true, pressed: i == 0, released: false);
            }

            if (!s.Play.TryUpdate(Frame, input, out var error))
            {
                throw new InvalidOperationException(error);
            }

            s.Faults.AddRange(s.Play.TakeNewFaults());
        }
    }

    // One key press (down for one frame, released on the next), then some frames to let it take effect.
    private static void Press(Session s, string key, int after = 10)
    {
        StepHolding(s.Play, 1, s.Faults, key);
        Step(s.Play, after, s.Faults);
    }

    private static List<string> Lines(OutputLog log, string prefix)
    {
        var lines = new List<string>();
        for (var i = 0; i < log.Count; i++)
        {
            if (log[i].Message.StartsWith(prefix, StringComparison.Ordinal))
            {
                lines.Add(log[i].Message);
            }
        }

        return lines;
    }

    private static string? Last(OutputLog log, string prefix) => Lines(log, prefix).LastOrDefault();

    private static GuiElement? Element(Session s, string name) => s.Play.Gui?.Document.Elements.FirstOrDefault(e => e.Name == name);

    private static string? TextOf(Session s, string name) => Element(s, name) switch
    {
        GuiText text => text.Text,
        GuiButton button => button.Text,
        _ => null
    };

    private static float HorizontalSpeed(Session s, Part part)
    {
        var v = s.Play.Physics!.LinearVelocityOf(part);
        return MathF.Sqrt(v.X * v.X + v.Z * v.Z);
    }

    // Clicks a game GUI button by name, as the editor's pointer would (press, then release).
    private static void Click(Session s, string name)
    {
        var entry = s.Play.Gui!.Update(Canvas).First(e => e.Element.Name == name);
        var x = entry.Rect.X + entry.Rect.Width / 2;
        var y = entry.Rect.Y + entry.Rect.Height / 2;
        s.Play.Gui.UpdatePointer(Canvas, x, y, true, true, true, false);
        s.Play.Gui.UpdatePointer(Canvas, x, y, true, false, false, true);
    }

    // ------------------------------------------------------------------------------------------------ Phase 4

    public static void Resources(TestReport report, string dir)
    {
        var root = NewProject(dir, """
            local prompts, dropped, regrown = {}, nil, false
            Events.On("PromptChanged", function(info) if info.text then prompts[#prompts + 1] = info.text end end)
            Events.On("ResourceHit", function(info) print("HIT " .. info.health) end)
            Events.On("ResourceDropped", function(info)
              dropped = info
              local drops = Scene.FindAllByTag("Drop")
              chk("drops_are_real_parts_with_an_item", #drops == 3 and drops[1]:GetAttribute("Item") == "Wood"
                and not drops[1].Anchored)
            end)
            Events.On("ResourceRegrown", function() regrown = true end)
            Events.On("InventoryChanged", function(info) print("INV Wood " .. (info.items.Wood or 0)) end)
            function Update()
              if prompts[1] then chk("prompt_names_the_tree", prompts[1] == "chop the Tree (3 hits left)") end
              if dropped then chk("dropped_three_wood", dropped.count == 3 and dropped.item == "Wood") end
              if dropped and Scene.CountByTag("Drop") == 0 then chk("drops_collected_and_destroyed", true) end
              if regrown then chk("regrown_with_full_health", Scene.Find("Tree"):GetAttribute("Health") == 3) end
            end
            """);
        var workspace = new Workspace();
        var player = Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false, "Player", "Interact", "Inventory", "Collector", "Survival", "Status");
        var tree = Add(workspace, "Tree", new Vector3(0, 4, -4.5f), new Vector3(2, 8, 2), true, "ResourceNode");
        AddProbe(workspace);
        var s = Begin(root, workspace);
        Step(s.Play, 20, s.Faults);
        for (var hit = 0; hit < 3; hit++)
        {
            Press(s, "E", 15);
        }

        report.Check("Mining: 3 presses of E (scripted input) take the Tree's 3 hits", Lines(s.Log, "HIT ").SequenceEqual(["HIT 2", "HIT 1", "HIT 0"]),
            string.Join(", ", Lines(s.Log, "HIT ")));
        report.Check("Mining: at 0 the Tree disappears (invisible, no collision)", !tree.Visible && !tree.CanCollide);
        Step(s.Play, 120, s.Faults);
        report.Check("Drops: the 3 Wood drops fly to the player and land in the inventory", Last(s.Log, "INV Wood ") == "INV Wood 3", Last(s.Log, "INV Wood "));
        Press(s, "E", 10);
        report.Check("Mining: a depleted Tree cannot be hit (no prompt, no hit)", Lines(s.Log, "HIT ").Count == 3);
        Step(s.Play, 8 * 60, s.Faults);
        report.Check("Regeneration: 8 s later the Tree is back (visible, solid)", tree.Visible && tree.CanCollide);
        Press(s, "E", 10);
        report.Check("Regeneration: it can be harvested again", Last(s.Log, "HIT ") == "HIT 2");
        AssertLuaChecks(report, "Resources", s.Log, ["prompt_names_the_tree", "drops_are_real_parts_with_an_item", "dropped_three_wood",
            "drops_collected_and_destroyed", "regrown_with_full_health"]);
        End(s, workspace);
        report.Check("Resources: Stop restores the Tree and removes the drops", tree.Visible && tree.CanCollide && workspace.Parts.Count == 3
            && player.Position.X == 0f && player.Position.Y == 2f && player.Position.Z == 0f);
        AssertNoFaults(report, "Resources", s.Faults);
    }

    // ------------------------------------------------------------------------------------------------ Phases 6 and 7

    public static void SurvivalHud(TestReport report, string dir)
    {
        var root = NewProject(dir, """
            function Update()
              local player = Shared.player or Scene.Find("Player")
              if Input.IsKeyPressed("F2") then player:GetScript("Inventory").Add("Wood", 2) end
              if Input.IsKeyPressed("F3") then player:GetScript("Status").Damage(30, "test") end
              if Input.IsKeyPressed("F4") then player:GetScript("Status").Apply("Slow", 1) end
            end
            """);
        var workspace = new Workspace();
        Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false,
            "Player", "Flight", "Interact", "Inventory", "Collector", "Survival", "Status", "Objectives", "HUD");
        for (var i = 0; i < 3; i++)
        {
            var coin = Add(workspace, $"Coin{i + 1}", new Vector3(0, 2, -5 - 3 * i), new Vector3(1.6f, 1.6f, 0.3f), true, "Coin");
            coin.IsTrigger = true;
        }

        Add(workspace, "Well", new Vector3(4, 1.5f, -16), new Vector3(3, 3, 3), true, "Food");
        AddProbe(workspace);

        var s = Begin(root, workspace);
        Step(s.Play, 10, s.Faults);
        var firstCount = s.Play.Gui!.Document.Count;
        report.Check("HUD: Lua built it with UI.Create (stats panel, three bars, coins, objective, inventory, prompt, Fly button)",
            new[] { "StatsPanel", "HealthFill", "HungerFill", "ThirstFill", "Coins", "Objective", "InventoryPanel", "Prompt", "FlyButton" }
                .All(name => Element(s, name) is not null), $"{firstCount} elements");
        report.Check("HUD: starts at Coins: 0, an empty inventory, objective 1/5", TextOf(s, "Coins") == "Coins: 0" && TextOf(s, "ItemNone") == "(empty)"
            && TextOf(s, "Objective") == "Objective 1/5: Collect 3 coins", $"{TextOf(s, "Coins")} | {TextOf(s, "ItemNone")} | {TextOf(s, "Objective")}");
        report.Check("HUD: the prompt is hidden with nothing in reach", Element(s, "Prompt") is { Visible: false });

        // Walk north through the three coins (W, scripted).
        Hold(s, 78, "W");
        Step(s.Play, 40, s.Faults);
        report.Check("Coins: walking through 3 trigger coins (W held) counts 3 on the player's Coins attribute and the HUD",
            TextOf(s, "Coins") == "Coins: 3", TextOf(s, "Coins"));
        report.Check("Objectives: collecting them completes objective 1 (now 2/5)", TextOf(s, "Objective") == "Objective 2/5: Chop the tree for 3 Wood",
            TextOf(s, "Objective"));
        report.Check("HUD: the interaction prompt appears next to the well", Element(s, "Prompt") is { Visible: true } && TextOf(s, "Prompt") == "E: drink from the well",
            TextOf(s, "Prompt"));

        // Hunger and Thirst fall every second (0.5 and 0.8 per second); the bars follow.
        Step(s.Play, 4 * 60, s.Faults);
        var hunger = float.Parse(TextOf(s, "HungerValue")!, System.Globalization.CultureInfo.InvariantCulture);
        var hungerFill = Element(s, "HungerFill")!.Size.X.Scale;
        report.Check("Stats: Hunger falls over time and its bar follows (value / 100)", hunger is >= 95 and < 100 && MathF.Abs(hungerFill * 100 - hunger) < 1f,
            $"Hunger {hunger}, bar {hungerFill:F3}");
        var thirst = float.Parse(TextOf(s, "ThirstValue")!, System.Globalization.CultureInfo.InvariantCulture);
        report.Check("Stats: Thirst falls faster than Hunger", thirst < hunger, $"Thirst {thirst}");

        Press(s, "F3", 5);
        report.Check("Stats: damage shows on the Health bar (70 of 100)", TextOf(s, "HealthValue") == "70" && MathF.Abs(Element(s, "HealthFill")!.Size.X.Scale - 0.7f) < 0.001f,
            $"{TextOf(s, "HealthValue")}, bar {Element(s, "HealthFill")!.Size.X.Scale:F3}");
        Press(s, "F4", 5);
        report.Check("HUD: an active status effect is listed", TextOf(s, "Effects") == "Effects: Slow", TextOf(s, "Effects"));
        Step(s.Play, 70, s.Faults);
        report.Check("HUD: and cleared when it ends (1 s)", TextOf(s, "Effects") == string.Empty, TextOf(s, "Effects"));

        Press(s, "F2", 5);
        report.Check("Inventory panel: lists what the inventory holds (rows recreated on each change)", TextOf(s, "ItemWood") == "Wood  x2" && Element(s, "ItemNone") is null,
            TextOf(s, "ItemWood"));
        Press(s, "I", 2);
        var hidden = Element(s, "InventoryPanel") is { Visible: false };
        Press(s, "I", 2);
        report.Check("Inventory panel: I hides and shows it", hidden && Element(s, "InventoryPanel") is { Visible: true });

        Click(s, "FlyButton");
        Step(s.Play, 2, s.Faults);
        var flying = TextOf(s, "FlyButton");
        Click(s, "FlyButton");
        Step(s.Play, 2, s.Faults);
        report.Check("HUD: the Fly button toggles flight (Fly -> Land -> Fly)", flying == "Land" && TextOf(s, "FlyButton") == "Fly", $"{flying} -> {TextOf(s, "FlyButton")}");
        End(s, workspace);

        // GUI lifecycle: a second session builds the same HUD again, nothing left over from the first.
        var again = Begin(root, workspace);
        Step(again.Play, 10, again.Faults);
        report.Check("GUI lifecycle: a new Play session starts with exactly the same HUD (nothing kept from the last one)",
            again.Play.Gui!.Document.Count == firstCount && TextOf(again, "Coins") == "Coins: 0", $"{again.Play.Gui.Document.Count} vs {firstCount}");
        End(again, workspace);
        AssertNoFaults(report, "Survival HUD", [.. s.Faults, .. again.Faults]);
    }

    // ------------------------------------------------------------------------------------------------ Phase 8

    public static void StatusEffects(TestReport report, string dir)
    {
        const string probe = """
            Events.On("Damaged", function(info) print("DMG " .. info.object.Name .. " " .. info.health .. " " .. tostring(info.source)) end)
            Events.On("AbilityUsed", function(info) print("ABILITY " .. info.ability .. " " .. info.hits) end)
            Events.On("StatusChanged", function(info) print("EFFECT " .. info.object.Name .. " " .. info.effect .. " " .. tostring(info.active)) end)
            Events.On("Died", function(info) print("DIED " .. info.object.Name) end)
            Events.On("Bumped", function(info) print("BUMPED " .. info.object.Name) end)
            local watched = {}
            function Start()
              for _, name in ipairs({ "Player", "DummyBehind", "Runner" }) do
                local object = Scene.Find(name)
                if object then
                  watched[#watched + 1] = object
                  object:OnAttributeChanged("Health", function(value) print("HP " .. name .. " " .. value) end)
                end
              end
            end
            function Update()
              if Input.IsKeyPressed("F5") then Scene.Find("DummyBehind"):GetScript("Status").Damage(500, "test") end
              if Input.IsKeyPressed("F6") then Scene.Find("Player"):SetAttribute("Health", 50) end
              if Input.IsKeyPressed("F7") then Scene.Find("Runner").LinearVelocity = Vector3.new(0, 0, -15) end
            end
            """;
        var root = NewProject(dir, probe);

        // Abilities: V needs line of sight, X ("X-ray") goes through walls but only hits enemies, F shoves.
        var workspace = new Workspace();
        Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false, "Player", "Status", "Pulse");
        var front = Add(workspace, "DummyFront", new Vector3(0, 2.5f, -6), new Vector3(2, 5, 2), false, "Status", "Dummy");
        Add(workspace, "DummyBehind", new Vector3(0, 2.5f, -16), new Vector3(2, 5, 2), false, "Status", "Dummy");
        Add(workspace, "Wall", new Vector3(0, 3, -11), new Vector3(10, 6, 1), true);
        Add(workspace, "Ally", new Vector3(9, 2.5f, -6), new Vector3(2, 5, 2), false, "Status");
        AddProbe(workspace);
        var s = Begin(root, workspace);
        Step(s.Play, 20, s.Faults);
        Press(s, "V", 40);
        report.Check("Blast (V): damages the enemy in sight only (the one behind the wall is not seen)",
            Lines(s.Log, "DMG ").SequenceEqual(["DMG DummyFront 75 Blast"]) && Last(s.Log, "ABILITY ") == "ABILITY Blast 1", string.Join(", ", Lines(s.Log, "DMG ")));
        Press(s, "X", 40);
        var damage = Lines(s.Log, "DMG ");
        report.Check("X-ray blast (X): reaches the enemy behind the wall too (an overlap query: no line of sight)",
            damage.Contains("DMG DummyFront 50 X-ray") && damage.Contains("DMG DummyBehind 75 X-ray") && Last(s.Log, "ABILITY ") == "ABILITY X-ray 2",
            string.Join(", ", damage));
        report.Check("X-ray blast: filtered targeting - the non-enemy (no Enemy tag) is never damaged", !damage.Any(l => l.StartsWith("DMG Ally", StringComparison.Ordinal)));
        Press(s, "F", 6);
        var pushed = s.Play.Physics!.LinearVelocityOf(front);
        report.Check("Knockback (F shove): the dummy in front is thrown away from the player", pushed.Z < -12f && pushed.Y > 2f, $"velocity {pushed.X:F1}, {pushed.Y:F1}, {pushed.Z:F1}");
        Press(s, "F5", 10);
        report.Check("Death: Health reaching 0 emits Died", Lines(s.Log, "DIED ").Contains("DIED DummyBehind") && Lines(s.Log, "DUMMY down").Count == 1);
        Step(s.Play, 200, s.Faults);
        report.Check("Death: Dummy.lua heals it again after 3 s", Last(s.Log, "HP DummyBehind") == "HP DummyBehind 100", Last(s.Log, "HP DummyBehind"));
        End(s, workspace);
        AssertNoFaults(report, "Abilities", s.Faults);

        // Burn: standing in lava, then walking out; the effect lasts 2 s after leaving and then stops.
        workspace = new Workspace();
        Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false, "Player", "Status");
        Add(workspace, "Lava", new Vector3(0, 0.25f, 0), new Vector3(6, 0.5f, 6), true, "Hazard").IsTrigger = true;
        AddProbe(workspace);
        s = Begin(root, workspace);
        Step(s.Play, 60, s.Faults);
        var onLava = Lines(s.Log, "DMG Player").Count;
        Hold(s, 60, "W");
        Step(s.Play, 4 * 60, s.Faults);
        var burns = Lines(s.Log, "DMG Player");
        var log = Enumerable.Range(0, s.Log.Count).Select(i => s.Log[i].Message).ToList();
        var ended = log.IndexOf("EFFECT Player Burn false");
        var lastBurn = log.FindLastIndex(l => l.StartsWith("DMG Player", StringComparison.Ordinal));
        report.Check("Timed effect (Burn): standing in the lava trigger burns 5 every 0.5 s", onLava >= 1 && burns.All(l => l.EndsWith(" Burn", StringComparison.Ordinal)),
            $"{onLava} burns in the first second");
        report.Check("Timed effect (Burn): it ends 2 s after walking out, and the damage stops with it",
            ended > 0 && lastBurn < ended && burns.Count is >= 3 and <= 8, $"{burns.Count} burns, last: {burns.LastOrDefault()}");
        End(s, workspace);
        AssertNoFaults(report, "Burn", s.Faults);

        // Slow: half speed in the mud (and 2 s after), full speed later.
        workspace = new Workspace();
        var walker = Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false, "Player", "Status");
        Add(workspace, "Mud", new Vector3(0, 0.25f, 0), new Vector3(6, 0.5f, 6), true, "Hazard").IsTrigger = true;
        AddProbe(workspace);
        s = Begin(root, workspace);
        Step(s.Play, 10, s.Faults);
        Hold(s, 24, "W");
        var slowSpeed = HorizontalSpeed(s, walker);
        Hold(s, 4 * 60, "W");
        var fullSpeed = HorizontalSpeed(s, walker);
        Step(s.Play, 1, s.Faults);
        report.Check("Timed effect (Slow): the mud halves walking speed, and it wears off (12 studs/s again)",
            slowSpeed is > 5f and < 6.5f && fullSpeed is > 11f and < 12.5f, $"{slowSpeed:F2} then {fullSpeed:F2} studs/s");
        End(s, workspace);
        AssertNoFaults(report, "Slow", s.Faults);

        // Regen: healing over time on the pad.
        workspace = new Workspace();
        Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false, "Player", "Status");
        Add(workspace, "HealPad", new Vector3(0, 0.25f, 0), new Vector3(6, 0.5f, 6), true, "Hazard").IsTrigger = true;
        AddProbe(workspace);
        s = Begin(root, workspace);
        Step(s.Play, 10, s.Faults);
        Press(s, "F6", 1);
        Step(s.Play, 120, s.Faults);
        var hp = Last(s.Log, "HP Player");
        report.Check("Timed effect (Regen): the heal pad restores Health over time", hp is not null && float.Parse(hp.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture) is >= 65 and <= 75, hp);
        End(s, workspace);
        AssertNoFaults(report, "Regen", s.Faults);

        // Knockback from a bumper: something running into it bounces back.
        workspace = new Workspace();
        var runner = Add(workspace, "Runner", new Vector3(0, 2.5f, 0), new Vector3(2, 5, 2), false, "Status");
        Add(workspace, "Bumper", new Vector3(0, 1.5f, -7), new Vector3(4, 3, 4), true, "Bumper");
        AddProbe(workspace);
        s = Begin(root, workspace);
        Step(s.Play, 10, s.Faults);
        Press(s, "F7", 1);
        var bounced = false;
        for (var i = 0; i < 60 && !bounced; i++)
        {
            Step(s.Play, 1, s.Faults);
            bounced = s.Play.Physics!.LinearVelocityOf(runner).Z > 15f;
        }

        Step(s.Play, 2, s.Faults); // the Bumped event arrives at the start of the next frame
        report.Check("Knockback (Bumper): running into it throws you back the way you came", bounced && Lines(s.Log, "BUMPED Runner").Count >= 1,
            $"velocity Z {s.Play.Physics!.LinearVelocityOf(runner).Z:F1}");
        End(s, workspace);
        AssertNoFaults(report, "Bumper", s.Faults);
    }

    // ------------------------------------------------------------------------------------------------ Phase 9

    public static void CollisionAcceptance(TestReport report, string dir)
    {
        var root = NewProject(dir, """
            local late
            local function launch(name, x, group)
              local runner = Scene.Create("Block", { Name = name, Anchored = false, GravityEnabled = false, Size = Vector3.new(1.6, 1.6, 1.6),
                Position = Vector3.new(x, 2, 0), LinearVelocity = Vector3.new(0, 0, -12), CollisionGroup = group })
              return runner
            end
            function Start()
              for _, lane in ipairs({ 0, 12, 24, 36 }) do
                launch("PlayerRunner" .. lane, lane - 1.5, "Player")
                launch("CrateRunner" .. lane, lane + 1.5, nil)
              end
              local door = Physics.Raycast(Vector3.new(24, 2, 5), Vector3.new(0, 0, -1), 40, { IgnoreTriggers = true })
              chk("query_default_hits_the_door", door ~= nil and door.Object.Name == "PlayerDoor")
              local asPlayer = Physics.Raycast(Vector3.new(24, 2, 5), Vector3.new(0, 0, -1), 40, { IgnoreTriggers = true, CollisionGroup = "Player" })
              chk("query_as_player_passes_the_door", asPlayer == nil)
              local ghost = Physics.Raycast(Vector3.new(12, 2, 5), Vector3.new(0, 0, -1), 40, { IgnoreTriggers = true })
              print("GHOSTRAY " .. tostring(ghost and ghost.Object.Name))
              Timer.After(2.5, function()
                Scene.Find("GhostWall").CanCollide = true -- a runtime change: solid from now on
                late = launch("LateRunner", 12, nil)
              end)
              Timer.After(5, function()
                chk("ghost_wall_made_solid_at_runtime_blocks", late.Transform.Position.z > -10)
              end)
            end
            """);
        var workspace = new Workspace();
        Add(workspace, "GameRules", new Vector3(0, 0.5f, 40), new Vector3(1, 1, 1), true, "Rules").CanCollide = false;
        Add(workspace, "Player", new Vector3(60, 2, 30), new Vector3(2, 4, 2), true);
        Add(workspace, "SolidWall", new Vector3(0, 3, -10), new Vector3(8, 6, 1), true);
        Add(workspace, "GhostWall", new Vector3(12, 3, -10), new Vector3(8, 6, 1), true).CanCollide = false;
        Add(workspace, "PlayerDoor", new Vector3(24, 3, -10), new Vector3(8, 6, 1), true);
        Add(workspace, "TriggerZone", new Vector3(36, 2, -10), new Vector3(8, 4, 4), true, "Zone").IsTrigger = true;
        AddProbe(workspace);
        var s = Begin(root, workspace);
        Step(s.Play, 120, s.Faults);
        float Z(string name) => workspace.Parts.FirstOrDefault(p => p.Name == name)?.Position.Z ?? float.NaN;

        report.Check("Solid wall: stops everything", Z("PlayerRunner0") > -10f && Z("CrateRunner0") > -10f, $"{Z("PlayerRunner0"):F1}, {Z("CrateRunner0"):F1}");
        report.Check("Pass-through wall (CanCollide = false): everything goes through", Z("PlayerRunner12") < -12f && Z("CrateRunner12") < -12f,
            $"{Z("PlayerRunner12"):F1}, {Z("CrateRunner12"):F1}");
        report.Check("Selective rule (collision groups): the player-group object passes the PlayerDoor, the other is stopped",
            Z("PlayerRunner24") < -12f && Z("CrateRunner24") > -10f, $"{Z("PlayerRunner24"):F1}, {Z("CrateRunner24"):F1}");
        var zone = Lines(s.Log, "ZONE ");
        report.Check("Trigger zone: both pass through it and it reports each entering and leaving",
            Z("PlayerRunner36") < -14f && zone.Contains("ZONE enter PlayerRunner36") && zone.Contains("ZONE exit CrateRunner36"), string.Join(", ", zone));
        Step(s.Play, 4 * 60, s.Faults);
        AssertLuaChecks(report, "Collision", s.Log, ["query_default_hits_the_door", "query_as_player_passes_the_door", "ghost_wall_made_solid_at_runtime_blocks"]);
        TestReport.Info($"a ray at the CanCollide=false GhostWall: {Last(s.Log, "GHOSTRAY")}");
        End(s, workspace);
        AssertNoFaults(report, "Collision acceptance", s.Faults);
    }

    // ------------------------------------------------------------------------------------------------ Phase 11

    public static void Consumables(TestReport report, string dir)
    {
        var root = NewProject(dir, """
            local player, goat
            function Start()
              player, goat = Scene.Find("Player"), Scene.Find("Goat")
              Timer.After(0.2, function() player:SetAttribute("Hunger", 50) player:SetAttribute("Thirst", 40) end)
            end
            Events.On("InventoryChanged", function(info) print("INV Apple " .. (info.items.Apple or 0)) end)
            Events.On("Ate", function(info) print("ATE " .. info.object.Name .. " " .. info.food) end)
            Events.On("Fed", function(info) print("FED " .. info.animal.Name) end)
            Events.On("Hint", function(text) print("HINT " .. text) end)
            function Update()
              if Input.IsKeyPressed("F1") then player.Transform.Position = Vector3.new(16, 2, 0) end
              if Input.IsKeyPressed("F8") then
                print(string.format("STATS %.1f %.1f %.1f", player:GetAttribute("Hunger"), player:GetAttribute("Thirst"), goat:GetAttribute("Hunger")))
                print("APPLE1 " .. tostring(Scene.Find("Apple1") ~= nil) .. " WELL " .. tostring(Scene.Find("Well") ~= nil))
              end
            end
            """);
        var workspace = new Workspace();
        var player = Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), true, "Interact", "Inventory", "Survival");
        Add(workspace, "Apple1", new Vector3(0, 0.5f, -2.5f), new Vector3(1, 1, 1), true, "Food");
        Add(workspace, "Apple2", new Vector3(1.5f, 0.5f, -3.5f), new Vector3(1, 1, 1), true, "Food");
        Add(workspace, "Well", new Vector3(-4.2f, 1.5f, 0), new Vector3(3, 3, 3), true, "Food");
        Add(workspace, "Goat", new Vector3(20, 1.2f, 0), new Vector3(3, 2.4f, 1.6f), false, "Survival", "Animal");
        AddProbe(workspace);
        var s = Begin(root, workspace);
        (float Hunger, float Thirst, float Goat) Stats()
        {
            Press(s, "F8", 1);
            var p = Last(s.Log, "STATS ")!.Split(' ');
            return (float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture));
        }

        Step(s.Play, 30, s.Faults);
        Press(s, "E");
        Press(s, "E");
        report.Check("Consumables: E picks up the nearest Apple, then the next (into the inventory; the Apples are gone)",
            Lines(s.Log, "INV Apple ").SequenceEqual(["INV Apple 1", "INV Apple 2"]) && Stats().Hunger is <= 50 && Last(s.Log, "APPLE1 ") == "APPLE1 false WELL true",
            string.Join(", ", Lines(s.Log, "INV Apple ")));
        var before = Stats();
        Press(s, "Q");
        var afterEating = Stats();
        report.Check("Eating: Q eats an Apple from the inventory: +25 Hunger, +5 Thirst (FoodDefs)",
            Lines(s.Log, "ATE ").SequenceEqual(["ATE Player Apple"]) && MathF.Abs(afterEating.Hunger - before.Hunger - 25f) < 1.01f
            && Last(s.Log, "INV Apple ") == "INV Apple 1", $"Hunger {before.Hunger} -> {afterEating.Hunger}");
        Press(s, "E");
        var afterDrink = Stats();
        report.Check("Drinking: E at the well restores Thirst (+35) and the well stays", afterDrink.Thirst - afterEating.Thirst is > 33f and < 36f
            && Last(s.Log, "APPLE1 ")!.EndsWith("WELL true", StringComparison.Ordinal), $"Thirst {afterEating.Thirst} -> {afterDrink.Thirst}");
        Press(s, "F1", 3);
        var goatBefore = Stats().Goat;
        Press(s, "E");
        var fed = Stats();
        report.Check("Feeding: E at the goat gives it your Apple: the goat's Hunger rises, yours does not",
            Lines(s.Log, "FED ").SequenceEqual(["FED Goat"]) && fed.Goat - goatBefore is > 24f and < 26f && fed.Hunger <= afterDrink.Hunger
            && Last(s.Log, "INV Apple ") == "INV Apple 0", $"goat {goatBefore} -> {fed.Goat}");
        Press(s, "E");
        Press(s, "Q");
        report.Check("Feeding / eating with no Apple: refused with a hint, nothing changes",
            Lines(s.Log, "FED ").Count == 1
            && Lines(s.Log, "ATE ").SequenceEqual(["ATE Player Apple", "ATE Player Water", "ATE Goat Apple"])
            && Lines(s.Log, "HINT You have no Apple").Count == 1
            && Lines(s.Log, "HINT No Apple to eat").Count == 1, string.Join(", ", Lines(s.Log, "HINT ")));
        End(s, workspace);
        report.Check("Consumables: Stop brings the eaten Apples back", workspace.Parts.Any(p => p.Name == "Apple1")
            && player.Position.X == 0f && player.Position.Y == 2f && player.Position.Z == 0f);
        AssertNoFaults(report, "Consumables", s.Faults);
    }

    // ------------------------------------------------------------------------------------------------ Phase 2: seat and camera

    public static void CarSeatAndCamera(TestReport report, string dir)
    {
        var root = NewProject(dir, """
            Events.On("VehicleChanged", function(info) print("VEHICLE " .. (info.vehicle and info.vehicle.Name or "none")) end)
            """);
        var workspace = new Workspace();
        var player = Add(workspace, "Player", new Vector3(0, 2, 0), new Vector3(2, 4, 2), false, "Player", "Interact", "Inventory", "Survival");
        var car = Add(workspace, "Car", new Vector3(5, 1.6f, 0), new Vector3(4, 1.4f, 7), false, "Car", "CarSeat");
        AddProbe(workspace);
        var camera = new StubCamera();
        var s = Begin(root, workspace, camera);
        Step(s.Play, 60, s.Faults);
        Press(s, "E", 10);
        Hold(s, 90, "W");
        static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt(Vector3.Dot(a - b, a - b));
        var forward = new Vector3(-MathF.Sin(car.Rotation.Y * MathF.PI / 180f), 0, -MathF.Cos(car.Rotation.Y * MathF.PI / 180f)); // local -Z
        var behind = Vector3.Dot(camera.Position - car.Position, forward);
        report.Check("Car seat: E next to the car gets in (Shared.vehicle), the player rides along hidden",
            Last(s.Log, "VEHICLE ") == "VEHICLE Car" && !player.Visible && Distance(player.Position, car.Position) < 3f,
            $"player {player.Position.X:F1}, {player.Position.Y:F1}, {player.Position.Z:F1}; car {car.Position.X:F1}, {car.Position.Y:F1}, {car.Position.Z:F1}");
        report.Check("Camera switch: while driving, the camera chases the car from behind and above",
            behind < -8f && camera.Position.Y > car.Position.Y + 2f, $"{behind:F1} studs along the car's forward, height {camera.Position.Y - car.Position.Y:F1}");
        report.Check("Car: W drives it (Player.lua stays out of the way)", -s.Play.Physics!.LinearVelocityOf(car).Z > 10f,
            $"speed {-s.Play.Physics.LinearVelocityOf(car).Z:F1}");
        Step(s.Play, 1, s.Faults);
        Hold(s, 90, "Space");
        Press(s, "E", 20);
        report.Check("Car seat: E again gets out beside the car (visible, free, camera back on the player)",
            Last(s.Log, "VEHICLE ") == "VEHICLE none" && player.Visible && Distance(player.Position, car.Position) > 2f
            && Distance(camera.Position, player.Position) < 25f, $"player {player.Position.X:F1}, {player.Position.Z:F1}");
        End(s, workspace);
        AssertNoFaults(report, "Car seat", s.Faults);
    }

    // ------------------------------------------------------------------------------------------------ Phase 13

    public static void Demo(TestReport report, string dir)
    {
        var root = NewProject(dir, """
            function Start()
              Timer.After(9, function()
                local crates, furthest = Scene.FindAllByTag("Crate"), 0
                for _, crate in ipairs(crates) do furthest = math.max(furthest, -crate.Transform.Position.z) end
                chk("crates_ride_the_conveyor_line", #crates >= 2 and furthest > 15)
                chk("goat_starts_hungry", Scene.Find("Goat"):GetAttribute("Hunger") < 41)
                chk("dummies_are_enemies", Scene.CountByTag("Enemy") == 2)
                chk("interactables_registered", Scene.CountByTag("Interactable") >= 9)
              end)
            end
            """);
        var parts = LuaV4Demo.BuildScene();
        var workspace = new Workspace();
        foreach (var part in parts)
        {
            workspace.AddPart(part);
        }

        AddProbe(workspace);
        var saved = SceneFile.ToJson(workspace.Parts);
        report.Check("Demo scene: built from placeholder Parts, organised in World panel groups (nested Playground)",
            parts.Count >= 30 && parts.Any(p => p.Group?.Path == "Playground/Hazards") && parts.Count(p => p.Group?.Name == "Coins") == 5, $"{parts.Count} parts");
        var spinner = parts.First(p => p.Name == "Spinner");
        var s = Begin(root, workspace);
        Step(s.Play, 10 * 60, s.Faults);
        report.Check("Demo: every script starts and runs 10 s without a fault", s.Faults.Count == 0);
        report.Check("Demo: the HUD is up (objective 1/5, coins, Fly button)", TextOf(s, "Objective") == "Objective 1/5: Collect 3 coins"
            && TextOf(s, "Coins") == "Coins: 0" && Element(s, "FlyButton") is not null, TextOf(s, "Objective"));
        report.Check("Demo: the spinning platform turns", MathF.Abs(spinner.Rotation.Y) > 20f, $"Y rotation {spinner.Rotation.Y:F1}");
        AssertLuaChecks(report, "Demo", s.Log, ["crates_ride_the_conveyor_line", "goat_starts_hungry", "dummies_are_enemies", "interactables_registered"]);
        End(s, workspace);
        report.Check("Demo: Stop restores the scene exactly (every Part, group and setting)", SceneFile.ToJson(workspace.Parts) == saved);
        AssertNoFaults(report, "Demo", s.Faults);
    }
}
