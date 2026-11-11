using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

// The Lua V5 showcase scene (examples/gameplay-systems), played headless with scripted keys: every system running together,
// and the persistence acceptance (save, close, reopen, load).
internal static partial class GameplayV5Checks
{
    // A probe on its own marker: reports what the showcase's scripts did, and drives what keys alone cannot reach in a
    // short headless run (it uses Shared.game, the public face Game.lua gives other scripts).
    private const string DemoProbe = """
        local frame = 0
        local startGuard, startFox, startLift
        function Start() end
        local function copy(v) return Vector3.new(v.x, v.y, v.z) end
        function Update()
          frame = frame + 1
          local guard, fox, lift = Scene.Find("GuardBody"), Scene.Find("Fox"), Scene.Find("SkyLift")
          if frame == 2 then
            startGuard, startFox, startLift = copy(guard.Transform.Position), copy(fox.Transform.Position), copy(lift.Transform.Position)
            chk("cinematic_starts", Shared.cinematic == true and Cameras.GetActive() ~= nil and Cameras.GetActive().Name == "Flyover")
            chk("navigation_built", Navigation.GetInfo().Walkable > 10000)
          elseif frame == 400 then
            chk("forest_generated", #Scene.FindAllByTag("Tree") > 20 and #Scene.FindAllByTag("Rock") > 3)
            chk("guard_patrols", Vector3.distance(guard.Transform.Position, startGuard) > 10)
            chk("fox_wanders", Vector3.distance(fox.Transform.Position, startFox) > 2)
            chk("lift_moves", Vector3.distance(lift.Transform.Position, startLift) > 3)
            chk("clock_runs", GameTime.Hour > 9)
            chk("hud_built", UI.Find("Clock") ~= nil and UI.Find("QuestTracker") ~= nil)
          elseif frame == 700 then
            chk("cinematic_ended", Shared.cinematic == false and Cameras.GetActive() == nil)
          elseif frame == 720 then
            -- Next to Mira: E talks.
            Shared.player:Teleport(Scene.Find("MiraSpot").Transform.Position + Vector3.new(0, 1, 2))
          elseif frame == 760 then
            chk("dialogue_open", UI.Find("DialoguePanel") ~= nil and UI.Find("DialoguePanel").Visible)
          elseif frame == 800 then
            chk("quest_accepted_with_keys", Shared.game.quests:IsActive("firewood"))
            -- Five wood (as if chopped), then planks at the workbench.
            Shared.game.inventory.Add("Wood", 6)
            Shared.game.inventory.Add("Stone", 3)
            Shared.player:Teleport(Scene.Find("Workbench").Transform.Position + Vector3.new(0, 1.5, 3))
          elseif frame == 905 then
            chk("crafted_planks", Shared.game.inventory.Count("Plank") >= 2)
          elseif frame == 1070 then
            chk("crafted_axe", Shared.game.inventory.Count("Axe") == 1)
          elseif frame == 1120 then
            chk("axe_in_hand", Shared.equipped == true and Scene.Find("AxeHandle") ~= nil and Scene.Find("AxeHandle").Parent == Scene.Find("PlayerRobot"))
            -- Save the game: resources, position, inventory, quest progress and game time.
            Shared.player:Teleport(Vector3.new(30, 3, 30))
          elseif frame == 1130 then
            Shared.savedHour = GameTime.TotalHours
            chk("saved", Shared.game.Save())
            local data = GameSave.Load("Showcase")
            print("DEMO saved hour " .. string.format("%.3f", Shared.savedHour) .. " wood " .. data.Items.Wood .. " quest " .. data.Quests.firewood.State)
          end
        end
        """;

    // The next session (the game closed and opened again): F9 loads.
    private const string ReopenProbe = """
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 5 then
            chk("fresh_session", Shared.game.inventory.Count("Wood") == 0 and not Shared.game.quests:IsActive("firewood"))
          elseif frame == 30 then
            local data, info = GameSave.Load("Showcase")
            local p = Shared.player.Transform.Position
            chk("loaded_position", Vector3.distance(p, data.Player.Position) < 0.6)
            chk("loaded_resources", Shared.game.inventory.Count("Wood") == data.Items.Wood and Shared.game.inventory.Count("Plank") == data.Items.Plank
              and Shared.game.inventory.Count("Axe") == 1)
            chk("loaded_quests", Shared.game.quests:IsActive("firewood") and Shared.game.quests:Get("firewood").Objectives[1].Value == data.Quests.firewood.Objectives.wood)
            chk("loaded_time", math.abs(GameTime.TotalHours - (data.Clock.Day - 1) * 24 - data.Clock.Hour) < 0.05 and GameTime.Day == data.Clock.Day)
            chk("loaded_equipment", Shared.equipped == true)
            chk("save_metadata", info.Metadata.Day == data.Clock.Day and info.Version == 1)
            print("DEMO loaded day " .. GameTime.Day .. " " .. GameTime.Clock() .. " at " .. tostring(p))
          end
        end
        """;

    public static void Demo(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Showcase");
        LuaV5Demo.CopyScripts(root);
        WriteScript(root, "ProbeDemo.lua", ChkFunction + DemoProbe);
        WriteScript(root, "ProbeReopen.lua", ChkFunction + ReopenProbe);
        var assets = LuaV5Demo.ExampleAssets();

        var workspace = new Workspace();
        foreach (var part in LuaV5Demo.BuildScene(assets))
        {
            workspace.AddPart(part);
        }

        AddProbe(workspace, "ProbeDemo");
        var s = Begin(root, workspace, feeds: false);
        Run(s, 715);
        // E at Mira (the probe moved the player there at frame 720), then option 1 ("Can I help?") and 1 ("I'm on it.").
        Run(s, 10);
        Run(s, 30);
        Press(s, "E", 10);
        Press(s, "1", 10);
        Press(s, "1", 10);
        Run(s, 25);
        // At the workbench: E opens it, 1 crafts planks (twice), 2 the axe (2.5 s), E closes; Q equips.
        Run(s, 20);
        Press(s, "E", 5);
        Press(s, "1", 70);
        Press(s, "2", 160);
        Press(s, "E", 5);
        Press(s, "Q", 30);
        Run(s, 40);
        AssertNoFaults(report, "V5 showcase", s.Faults);
        AssertLuaChecks(report, "V5 showcase", s.Log, ["cinematic_starts", "navigation_built", "forest_generated", "guard_patrols", "fox_wanders",
            "lift_moves", "clock_runs", "hud_built", "cinematic_ended", "dialogue_open", "quest_accepted_with_keys", "crafted_planks", "crafted_axe",
            "axe_in_hand", "saved"]);
        foreach (var line in Lines(s.Log, "").Where(l => l.Contains("Navigation:", StringComparison.Ordinal) || l.Contains("Forest:", StringComparison.Ordinal)
                     || l.Contains("SkyLift:", StringComparison.Ordinal) || l.Contains("DEMO", StringComparison.Ordinal)))
        {
            TestReport.Info(line);
        }

        End(s, workspace);
        report.Check("V5 showcase: Stop restores the scene (generated forest and built pieces gone)", workspace.Parts.Count == LuaV5Demo.BuildScene(assets).Count + 1);

        // Reopen: a new session (as after closing the game), F9.
        foreach (var part in workspace.Parts.Where(p => p.Name == "ProbeDemo").ToList())
        {
            workspace.RemovePart(part);
        }

        AddProbe(workspace, "ProbeReopen");
        s = Begin(root, workspace, feeds: false);
        Run(s, 10);
        Press(s, "F9", 15);
        Run(s, 10);
        AssertNoFaults(report, "V5 showcase (reopened)", s.Faults);
        AssertLuaChecks(report, "V5 showcase (reopened)", s.Log, ["fresh_session", "loaded_position", "loaded_resources", "loaded_quests", "loaded_time",
            "loaded_equipment", "save_metadata"]);
        foreach (var line in Lines(s.Log, "").Where(l => l.Contains("DEMO", StringComparison.Ordinal)))
        {
            TestReport.Info(line);
        }

        End(s, workspace);
    }

    // One key press (down for one frame, released on the next), then some frames.
    internal static void Press(Session s, string key, int after)
    {
        StepHolding(s.Play, 1, s.Faults, key);
        Run(s, after);
    }
}
