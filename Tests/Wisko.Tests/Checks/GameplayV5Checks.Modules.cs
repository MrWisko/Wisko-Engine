using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

// V5 optional modules (require("Wisko.*")): Dialogue + Quests, Crafting, Building, Logic, Race, Generate, Weather.
internal static partial class GameplayV5Checks
{
    private const string DialogueProbe = """
        local Dialogue = require("Wisko.Dialogue")
        local Quests = require("Wisko.Quests")
        local inventory = { Wood = 0, Coin = 0 }
        local log = Quests.new()
        log:Define({ Id = "intro", Title = "Say hello", Objectives = { { Id = "talk", Text = "Talk to Mira" } } })
        log:Define({ Id = "wood", Title = "Firewood", Requires = { "intro" }, Objectives = { { Id = "gather", Text = "Gather wood", Target = 3 } },
          Reward = function() inventory.Coin = inventory.Coin + 10 end })
        local ctx = { log = log, inventory = inventory }
        local tree = {
          Start = "hello",
          Nodes = {
            hello = { Speaker = "Mira", Text = function(c) return "Hello! Coins: " .. c.inventory.Coin end, Options = {
              { Text = "Hi!", Next = "offer", Action = function(c) c.log:Progress("intro", "talk") end },
              { Text = "I have the wood", Next = "done", Condition = function(c) return c.log:IsActive("wood") and c.inventory.Wood >= 3 end },
              { Text = "Bye" },
            } },
            offer = { Speaker = "Mira", Text = "Bring me 3 wood?", Options = {
              { Text = "Sure", Next = "thanks", Action = function(c) c.log:Start("wood") end, Condition = function(c) return log:GetState("wood") == "Available" end },
              { Text = "No" },
            } },
            thanks = { Speaker = "Mira", Text = "Thank you!" },
            done = { Speaker = "Mira", Text = "Wonderful!", Action = function(c)
              c.log:Progress("wood", "gather", c.inventory.Wood)
            end },
          },
        }
        local ended
        local tracker
        function Start()
          chk("wood_locked_before_intro", log:GetState("wood") == "Locked" and not log:CanStart("wood"))
          local talk = Dialogue.Start(tree, { Context = ctx, Presenter = Dialogue.Presenter(), OnEnd = function(reason) ended = reason end })
          chk("starts_at_hello", talk.Node == "hello" and talk:GetSpeaker() == "Mira" and talk:GetText() == "Hello! Coins: 0")
          local options = talk:GetOptions()
          chk("condition_hides_option", #options == 2 and options[1].Text == "Hi!" and options[2].Text == "Bye")
          chk("presenter_shows_text", UI.Find("DialogueText").Text == "Hello! Coins: 0" and UI.Find("DialogueOption2") ~= nil)
          log:Start("intro")
          talk:Choose(1)
          chk("intro_completed_by_action", log:IsComplete("intro") and log:GetState("wood") == "Available")
          chk("went_to_offer", talk.Node == "offer")
          talk:Choose(1)
          chk("quest_started", log:IsActive("wood") and talk.Node == "thanks")
          tracker = Quests.Tracker(log)
          chk("tracker_shows_objective", UI.Find("QuestObjective_wood_gather").Text == "[ ] Gather wood 0/3")
          talk:Advance()
          chk("node_without_options_ends", not talk.Active and ended == "finished" and not UI.Find("DialoguePanel").Visible)
          chk("bad_choice", not talk:Choose(5))
          chk("bad_tree", not pcall(Dialogue.Start, {}))
          -- Later: the player has the wood.
          inventory.Wood = 3
          local again = Dialogue.Start(tree, { Context = ctx })
          local shown = again:GetOptions()
          chk("condition_now_true", #shown == 3 and shown[2].Text == "I have the wood")
          again:Choose(2)
          chk("objective_done_quest_complete", log:IsComplete("wood"))
          chk("reward_given_once", inventory.Coin == 10)
          log:Complete("wood")
          chk("reward_not_twice", inventory.Coin == 10)
          chk("tracker_updates", UI.Find("QuestTrackerEmpty") ~= nil)
          local saved = log:Save()
          local other = Quests.new()
          other:Define({ Id = "intro", Objectives = { { Id = "talk" } } })
          other:Define({ Id = "wood", Requires = { "intro" }, Objectives = { { Id = "gather", Target = 3 } }, Reward = function() inventory.Coin = 999 end })
          other:Load(saved)
          chk("quest_save_load", other:IsComplete("wood") and other:Get("wood").Objectives[1].Value == 3)
          other:Complete("wood")
          chk("loaded_reward_not_repeated", inventory.Coin == 10)
          chk("quest_data_saveable", GameSave.Save("Quests", saved) == true)
        end
        """;

    private const string CraftingProbe = """
        local Crafting = require("Wisko.Crafting")
        local items = { Wood = 5, Stone = 1 }
        local inventory = Crafting.TableInventory(items)
        local recipes = Crafting.Recipes({
          { Id = "Plank", Inputs = { Wood = 1 }, Outputs = { Plank = 4 }, Time = 0.5, Station = "Workbench" },
          { Id = "Pickaxe", Inputs = { Plank = 2, Stone = 3 }, Outputs = { Pickaxe = 1 }, Time = 1, Station = "Workbench" },
          { Id = "Sharpen", Inputs = { Stone = 1 }, Outputs = function(ctx) ctx.sharpened = true; return { Sharpness = 1 } end,
            Requires = function(ctx) return ctx.hasAxe, "you need an axe" end },
        })
        local bench = Crafting.Station({ Kind = "Workbench", Recipes = recipes })
        local anvil = Crafting.Station({ Kind = "Anvil", Recipes = recipes })
        local crafted = {}
        local job
        function Start()
          bench:OnCrafted(function(j, made) crafted[#crafted + 1] = j.Recipe.Id end)
          local ok, missing, reason = recipes:CanCraft("Pickaxe", inventory, { station = bench })
          chk("missing_reported", not ok and missing.Plank == 2 and missing.Stone == 2 and string.find(reason, "needs", 1, true) ~= nil)
          chk("wrong_station", select(2, anvil:Start("Plank", inventory)) == "Plank is made at a Workbench")
          job = bench:Start("Plank", inventory, { Count = 2 })
          chk("inputs_taken_at_start", job ~= nil and items.Wood == 3 and items.Plank == nil)
          chk("busy", select(2, bench:Start("Plank", inventory)) == "the Workbench is busy")
          local ctx = { hasAxe = false }
          chk("requires_rule", select(3, recipes:CanCraft("Sharpen", inventory, ctx)) == "you need an axe")
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 20 then
            chk("progress_midway", job:GetProgress() > 0.2 and job:GetProgress() < 0.8 and job.State == "Crafting")
          elseif frame == 70 then
            chk("outputs_after_time", items.Plank == 8 and job.State == "Done" and crafted[1] == "Plank")
            local cancelled = bench:Start("Plank", inventory)
            chk("cancel_refunds", cancelled:Cancel() and items.Wood == 3 and cancelled.State == "Cancelled")
            items.Stone = 3
            local ctx = { hasAxe = true }
            local sharpen = bench:Start("Sharpen", inventory, { Context = ctx })
            chk("function_output_upgrade", sharpen ~= nil and sharpen.State == "Done" and ctx.sharpened == true and items.Stone == 2)
          end
        end
        """;

    private const string BuildingProbe = """
        local Building = require("Wisko.Building")
        local placer
        function Start()
          placer = Building.Placer({ Grid = 2, RotationStep = 90 })
          placer:Begin({ Type = "Block", Size = Vector3.new(4, 1, 2), Color = "#C8A060", Name = "Floor" })
          chk("preview_made", #Scene.FindAllByTag("BuildPreview") == 1)
          local valid = placer:SetTarget(Vector3.new(10.7, 0, 3.2))
          chk("grid_snap", placer.Position.x == 10 and placer.Position.z == 3 and math.abs(placer.Position.y - 0.5) < 1e-6)
          chk("valid_on_open_ground", valid and placer.Valid)
          local preview = Scene.FindByTag("BuildPreview")
          chk("preview_not_solid", preview.CanCollide == false and preview.Color == "#4CD964")
          placer:Rotate(1)
          chk("rotation_snap", placer.Rotation.y == 90)
          local floor = placer:Confirm()
          chk("confirm_creates", floor ~= nil and #floor == 1 and floor[1]:HasTag("Built") and floor[1].CanCollide)
          -- The same place again: blocked by what was just built.
          local again, reason = placer:SetTarget(Vector3.new(10.2, 0, 3))
          chk("collision_blocks", not again and string.find(placer.Reason, "blocked by Floor", 1, true) ~= nil)
          chk("blocked_preview_red", Scene.FindByTag("BuildPreview").Color == "#FF453A")
          chk("confirm_refused_when_blocked", placer:Confirm() == nil)
          -- Stacked on top (resting on the surface hit).
          local top = floor[1].Transform.Position
          local hit = Physics.Raycast(Vector3.new(top.x, 10, top.z), Vector3.new(0, -1, 0), 20, { IgnoreTags = "BuildPreview" })
          local stacked = placer:SetTarget(hit.Point, hit)
          chk("surface_snap", stacked and math.abs(placer.Position.y - 1.5) < 1e-4)
          placer:Confirm()
          -- Grouped placement: a wall of two pieces (unturned).
          placer:Rotate(-1)
          placer:Begin({ { Type = "Block", Size = Vector3.new(2, 3, 1), Name = "WallA" }, { Type = "Block", Size = Vector3.new(2, 3, 1), Name = "WallB", Offset = Vector3.new(2, 0, 0) } })
          placer:SetTarget(Vector3.new(-10, 0, -10))
          local wall = placer:Confirm()
          chk("grouped_placement", wall ~= nil and #wall == 2 and Vector3.distance(wall[2].Transform.Position, wall[1].Transform.Position + Vector3.new(2, 0, 0)) < 1e-4)
          placer:Cancel()
          chk("cancel_removes_preview", #Scene.FindAllByTag("BuildPreview") == 0)
          local custom = Building.Placer({ CanPlace = function(info) return info.Position.x < 0, "only west of the river" end })
          custom:Begin({ Size = Vector3.new(2, 2, 2) })
          chk("custom_rule", not custom:SetTarget(Vector3.new(5, 0, 0)) and custom.Reason == "only west of the river")
          custom:Cancel()
          local built = Scene.FindAllByTag("Built")
          local plan = Building.Describe(built)
          chk("describe", #plan == 4 and plan[1].Type == "Block")
          chk("save_plan", GameSave.Save("Plan", plan) == true)
          chk("remove_built", Building.Remove(built[1]) and not built[1].Exists)
          chk("remove_refuses_unbuilt", not Building.Remove(Scene.Find("Rock")))
          local copy = Building.Duplicate(built[2], Vector3.new(0, 0, 8))
          chk("duplicate", copy.Exists and math.abs(copy.Transform.Position.z - built[2].Transform.Position.z - 8) < 1e-4)
          local rebuilt = Building.Rebuild(GameSave.Load("Plan"))
          chk("rebuild", #rebuilt == 4)
        end
        """;

    private const string PuzzleProbe = """
        local Logic = require("Wisko.Logic")
        local a, b, plate, open, door
        function Start()
          a = Logic.Switch(Scene.Find("SwitchA"))
          b = Logic.Switch(Scene.Find("SwitchB"), { CanUse = function(user) return user ~= nil, "nobody to use it" end })
          plate = Logic.PressurePlate(Scene.Find("Plate"))
          door = Logic.Mover(Scene.Find("Door"), { Offset = Vector3.new(0, 5, 0), Duration = 0.5 })
          open = Logic.All(a, b, Logic.Hold(plate, 2))
          open:OnChanged(function(isOpen) door:Set(isOpen) end)
          chk("starts_closed", open.Value == false and Scene.Find("SwitchA"):GetAttribute("On") == false)
          a:Toggle()
          chk("use_requires", not b:Use(nil) and b.Value == false)
          b:Use(Scene.Find("Plate"))
          chk("switches_on_door_still_closed", a.Value and b.Value and not open.Value)
          local seq = Logic.Sequence({ "red", "green", "blue" })
          chk("sequence", seq:Input("red") == "progress" and seq:Input("blue") == "wrong" and seq:Input("red") == "progress"
            and seq:Input("green") == "progress" and seq:Input("blue") == "complete" and seq.Value)
          local counter = Logic.Counter({ Target = 3 })
          counter:Increment()
          counter:Increment(2)
          chk("counter_reached", counter.Value == 3 and counter.Reached.Value)
          chk("not_signal", Logic.Not(counter.Reached).Value == false)
        end
        local frame = 0
        function Update()
          frame = frame + 1
          local doorY = Scene.Find("Door").Transform.Position.y
          if frame == 5 then
            Scene.Find("Weight"):Teleport(Scene.Find("Plate").Transform.Position + Vector3.new(0, 1, 0))
          elseif frame == 40 then
            chk("plate_pressed", plate.Value and Scene.Find("Plate"):GetAttribute("Pressed") == true)
            chk("door_opens", open.Value and doorY > 6)
            Scene.Find("Weight"):Teleport(Vector3.new(20, 2, 20))
          elseif frame == 100 then
            chk("door_held_open_after_plate", not plate.Value and open.Value and doorY > 6)
          elseif frame == 200 then
            chk("door_closes_after_hold", not open.Value and doorY < 2.5)
          end
        end
        """;

    private const string RaceProbe = """
        local Race = require("Wisko.Race")
        local race, laps, finished, wrong, splits = nil, {}, nil, 0, {}
        local car
        function Start()
          car = Scene.Find("Car")
          race = Race.new({ Checkpoints = { Scene.Find("CP1"), Scene.Find("CP2"), Scene.Find("CP3") }, Laps = 2, Racers = { car } })
          race:OnLap(function(racer, lap, time) laps[#laps + 1] = time end)
          race:OnCheckpoint(function(racer, index, lap, split) splits[#splits + 1] = index end)
          race:OnFinished(function(racer, total, place) finished = { total = total, place = place } end)
          race:OnWrongCheckpoint(function() wrong = wrong + 1 end)
          race:Start()
        end
        local route = { Vector3.new(0, 2, 0), Vector3.new(30, 2, 0), Vector3.new(30, 2, 30), Vector3.new(0, 2, 0) }
        local frame = 0
        local function at(p) car:Teleport(p) end
        function Update()
          frame = frame + 1
          -- Wrong order first: CP2 before CP1 does not count.
          if frame == 10 then at(Vector3.new(30, 2, 0)) end
          if frame == 20 then at(Vector3.new(0, 2, 40)) end
          if frame == 30 then at(Vector3.new(0, 2, 0)) end      -- CP1
          if frame == 40 then at(Vector3.new(30, 2, 0)) end     -- CP2
          if frame == 50 then at(Vector3.new(30, 2, 30)) end    -- CP3: lap 1
          if frame == 60 then at(Vector3.new(0, 2, 40)) end
          if frame == 70 then at(Vector3.new(0, 2, 0)) end
          if frame == 80 then at(Vector3.new(30, 2, 0)) end
          if frame == 95 then at(Vector3.new(30, 2, 30)) end    -- lap 2: finish
          if frame == 110 then
            chk("wrong_checkpoint_reported", wrong >= 1)
            chk("two_laps", #laps == 2)
            chk("splits_in_order", table.concat(splits, ",") == "1,2,3,1,2,3")
            chk("finished_first", finished ~= nil and finished.place == 1 and race.State == "Finished")
            chk("total_time", math.abs(finished.total - (95 - 1 + 1) / 60) < 0.05)
            chk("lap_times_add_up", math.abs(laps[1] + laps[2] - finished.total) < 1e-6)
            chk("rankings", race:GetRankings()[1].Racer == car)
            chk("format_time", Race.FormatTime(65.321) == "1:05.32" and Race.FormatTime(5.5) == "5.50")
            local best = Race.BestTimes("Records", "Course1")
            chk("best_time_saved", best:Submit(finished.total) and best:Get() == finished.total and not best:Submit(999))
            print("RACE total " .. Race.FormatTime(finished.total) .. " laps " .. Race.FormatTime(laps[1]) .. ", " .. Race.FormatTime(laps[2]))
          end
        end
        """;

    private const string GenerateProbe = """
        local Generate = require("Wisko.Generate")
        local function layout(seed, prefix)
          local rng = Random.new(seed)
          local points = Generate.PoissonDisk(rng, { Min = Vector3.new(-40, 0, -40), Max = Vector3.new(40, 0, 40), Radius = 5, Limit = 60 })
          local job = Generate.Spawn(points, function(p, i)
            local kind = rng:Weighted({ { "Block", 3 }, { "Cylinder", 1 } })
            return Scene.Create(kind, { Name = prefix .. i, Position = p + Vector3.new(0, 1, 0), Size = Vector3.new(1, 2, 1) })
          end, { Budget = 0.2 })
          return job, points
        end
        local jobA, jobB, jobC, progressSeen = nil, nil, nil, 0
        function Start()
          jobA = layout(2024, "A")
          jobB = layout(2024, "B")
          jobC = layout(77, "C")
          jobA:OnProgress(function(p) progressSeen = progressSeen + 1 end)
          local big = Generate.Job(function(job)
            for i = 1, 100000 do
              local x = math.sin(i)
              job:Step(i, 100000)
            end
          end, { Budget = 0.5 })
          Timer.After(0.05, function()
            chk("cancel_stops_job", big:Cancel() and not big.Running and big.Progress < 1)
          end)
        end
        local function signature(prefix)
          local parts = {}
          for i = 1, 60 do
            local o = Scene.Find(prefix .. i)
            if o == nil then break end
            local p = o.Transform.Position
            parts[#parts + 1] = string.format("%s:%.3f,%.3f", o.Type, p.x, p.z)
          end
          return table.concat(parts, ";"), #parts
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 1 then
            chk("spread_over_frames", not jobA.Done)
          end
          if frame == 200 then
            chk("jobs_done", jobA.Done and jobB.Done and jobC.Done and jobA.Frames > 1)
            local a, countA = signature("A")
            local b = signature("B")
            local c = signature("C")
            chk("same_seed_identical_layout", a == b and countA > 20)
            chk("other_seed_different_layout", a ~= c)
            chk("progress_reported", progressSeen > 1 and jobA.Progress == 1)
            print("GENERATE " .. countA .. " objects per layout, frames " .. jobA.Frames)
            local grid = Generate.Grid(3, 2, 4, Vector3.new(0, 0, 0))
            chk("grid_cells", #grid == 6 and grid[1] == Vector3.new(2, 0, 2) and grid[6] == Vector3.new(10, 0, 6))
            local scatter = Generate.Scatter(Random.new(1), 10, { Min = Vector3.new(0, 0, 0), Max = Vector3.new(1, 1, 1) })
            chk("scatter", #scatter == 10)
          end
        end
        """;

    private const string WeatherProbe = """
        local Weather = require("Wisko.Weather")
        local DayNight = require("Wisko.DayNight")
        local weather, changes, flashes = nil, {}, 0
        function Start()
          weather = Weather.new({ Initial = "Clear", WindTag = "Wind", Seed = 3 })
          weather:OnChanged(function(state, previous) changes[#changes + 1] = tostring(previous) .. ">" .. state end)
          weather:OnLightning(function() flashes = flashes + 1 end)
          chk("clear_no_fog", Atmosphere.FogEnabled == false)
          chk("unknown_state", not weather:Set("Hail"))
          weather:Set("Rain", 1)
          DayNight.Start({ Weather = weather })
        end
        local frame, fogMid = 0, nil
        function Update()
          frame = frame + 1
          if frame == 30 then
            fogMid = Atmosphere.FogDensity
            chk("transition_in_progress", weather.Transitioning and fogMid > 0.3 and fogMid < 0.55)
          elseif frame == 90 then
            chk("rain_fog_reached", not weather.Transitioning and math.abs(Atmosphere.FogDensity - 0.55) < 0.02 and Atmosphere.FogEnabled)
            chk("rain_particles", Particles.Count > 500)
            chk("wind_vector", Vector3.distance(weather.Wind, Vector3.new(6, 0, 3)) < 1e-6)
            chk("light_dimmed_for_daynight", math.abs(weather.Light - 0.5) < 1e-6)
            weather:Set("Storm", 0)
          elseif frame == 600 then
            chk("storm_lightning", flashes >= 1)
            chk("events", changes[1] == "Clear>Rain" and changes[2] == "Rain>Storm")
            chk("wind_pushes_tagged", Scene.Find("Crate").Transform.Position.x > 2)
            weather:Set("Snow", 0)
          elseif frame == 700 then
            chk("snow_instead_of_rain", Particles.Count > 100)
            weather:Destroy()
          elseif frame == 760 then
            chk("destroy_stops_particles", Particles.Count == 0)
          end
        end
        """;

    public static void Modules(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeDialogue", DialogueProbe), ("ProbeCrafting", CraftingProbe), ("ProbeBuilding", BuildingProbe),
            ("ProbePuzzle", PuzzleProbe), ("ProbeRace", RaceProbe), ("ProbeGenerate", GenerateProbe), ("ProbeWeather", WeatherProbe));

        // Dialogue + quests (with the GUI presenter and tracker on the session's GUI).
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeDialogue");
        var s = Begin(root, workspace);
        Run(s, 2);
        AssertNoFaults(report, "V5 Dialogue/Quests", s.Faults);
        AssertLuaChecks(report, "V5 Dialogue/Quests", s.Log, ["wood_locked_before_intro", "starts_at_hello", "condition_hides_option", "presenter_shows_text",
            "intro_completed_by_action", "went_to_offer", "quest_started", "tracker_shows_objective", "node_without_options_ends", "bad_choice", "bad_tree",
            "condition_now_true", "objective_done_quest_complete", "reward_given_once", "reward_not_twice", "tracker_updates", "quest_save_load",
            "loaded_reward_not_repeated", "quest_data_saveable"]);
        End(s, workspace);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeCrafting");
        s = Begin(root, workspace);
        Run(s, 72);
        AssertNoFaults(report, "V5 Crafting", s.Faults);
        AssertLuaChecks(report, "V5 Crafting", s.Log, ["missing_reported", "wrong_station", "inputs_taken_at_start", "busy", "requires_rule", "progress_midway",
            "outputs_after_time", "cancel_refunds", "function_output_upgrade"]);
        End(s, workspace);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeBuilding");
        Add(workspace, "Rock", new Vector3(-30, 1, 30), new Vector3(2, 2, 2), true);
        s = Begin(root, workspace);
        Run(s, 2);
        AssertNoFaults(report, "V5 Building", s.Faults);
        AssertLuaChecks(report, "V5 Building", s.Log, ["preview_made", "grid_snap", "valid_on_open_ground", "preview_not_solid", "rotation_snap", "confirm_creates",
            "collision_blocks", "blocked_preview_red", "confirm_refused_when_blocked", "surface_snap", "grouped_placement", "cancel_removes_preview", "custom_rule",
            "describe", "save_plan", "remove_built", "remove_refuses_unbuilt", "duplicate", "rebuild"]);
        var count = workspace.Parts.Count;
        End(s, workspace);
        report.Check("V5 Building: Stop removes what was built and every preview", workspace.Parts.Count == 2, $"{count} during Play, {workspace.Parts.Count} after");

        workspace = new Workspace();
        AddProbe(workspace, "ProbePuzzle");
        Add(workspace, "SwitchA", new Vector3(-6, 1, -10), new Vector3(1, 2, 1), true);
        Add(workspace, "SwitchB", new Vector3(6, 1, -10), new Vector3(1, 2, 1), true);
        var plate = Add(workspace, "Plate", new Vector3(0, 0.25f, -14), new Vector3(4, 0.5f, 4), true);
        plate.IsTrigger = true;
        Add(workspace, "Door", new Vector3(0, 2, -20), new Vector3(6, 4, 1), true, "Spin");
        Add(workspace, "Weight", new Vector3(20, 2, 20), new Vector3(1.5f, 1.5f, 1.5f), false);
        WriteScript(root, "Spin.lua", "-- (makes the door a scripted, kinematic Part)\n");
        s = Begin(root, workspace);
        Run(s, 205);
        AssertNoFaults(report, "V5 Logic puzzle", s.Faults);
        AssertLuaChecks(report, "V5 Logic puzzle", s.Log, ["starts_closed", "use_requires", "switches_on_door_still_closed", "sequence", "counter_reached",
            "not_signal", "plate_pressed", "door_opens", "door_held_open_after_plate", "door_closes_after_hold"]);
        End(s, workspace);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeRace");
        foreach (var (name, x, z) in new[] { ("CP1", 0f, 0f), ("CP2", 30f, 0f), ("CP3", 30f, 30f) })
        {
            var checkpoint = Add(workspace, name, new Vector3(x, 2, z), new Vector3(6, 4, 6), true);
            checkpoint.IsTrigger = true;
        }

        Add(workspace, "Car", new Vector3(0, 2, -20), new Vector3(2, 1, 3), false);
        s = Begin(root, workspace);
        Run(s, 112);
        AssertNoFaults(report, "V5 Race", s.Faults);
        AssertLuaChecks(report, "V5 Race", s.Log, ["wrong_checkpoint_reported", "two_laps", "splits_in_order", "finished_first", "total_time", "lap_times_add_up",
            "rankings", "format_time", "best_time_saved"]);
        End(s, workspace);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeGenerate");
        s = Begin(root, workspace);
        Run(s, 202);
        AssertNoFaults(report, "V5 Generate", s.Faults);
        AssertLuaChecks(report, "V5 Generate", s.Log, ["spread_over_frames", "cancel_stops_job", "jobs_done", "same_seed_identical_layout", "other_seed_different_layout",
            "progress_reported", "grid_cells", "scatter"]);
        End(s, workspace);
        report.Check("V5 Generate: Stop removes every generated object", workspace.Parts.Count == 1);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeWeather");
        Add(workspace, "Crate", new Vector3(0, 1, 0), new Vector3(2, 2, 2), false, "WindTagger");
        WriteScript(root, "WindTagger.lua", "self:AddTag(\"Wind\")\n");
        s = Begin(root, workspace);
        Run(s, 762);
        AssertNoFaults(report, "V5 Weather", s.Faults);
        AssertLuaChecks(report, "V5 Weather", s.Log, ["clear_no_fog", "unknown_state", "transition_in_progress", "rain_fog_reached", "rain_particles", "wind_vector",
            "light_dimmed_for_daynight", "storm_lightning", "events", "wind_pushes_tagged", "snow_instead_of_rain", "destroy_stops_particles"]);
        End(s, workspace);
    }
}
