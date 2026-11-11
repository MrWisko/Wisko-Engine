using System.Text;
using System.Text.Json;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

// V5 session services: GameTime (and Wisko.DayNight), GameSave, DebugDraw, Particles, Cameras.
internal static partial class GameplayV5Checks
{
    private const string ClockProbe = """
        local hours, days, atSeven, after, every = {}, {}, 0, 0, 0
        function Start()
          chk("clock_starts_day1_0800", GameTime.Day == 1 and math.abs(GameTime.Hour - 8) < 1e-9 and GameTime.Clock() == "08:00")
          GameTime.DayLength = 24          -- one game hour per real second
          chk("day_length_set", GameTime.DayLength == 24)
          chk("day_length_rejects_zero", not pcall(function() GameTime.DayLength = 0 end))
          chk("scale_rejects_negative", not pcall(function() GameTime.Scale = -1 end))
          chk("set_rejects_hour_24", not pcall(GameTime.Set, 1, 24))
          GameTime.OnHourChanged(function(hour, day) hours[#hours + 1] = hour .. "@" .. day end)
          GameTime.OnDayChanged(function(day) days[#days + 1] = day end)
          GameTime.At(7, function(day) atSeven = atSeven + 1 end)
          GameTime.After(2, function() after = after + 1 end)
          GameTime.Every(3, function() every = every + 1 end)
          chk("is_between_day", GameTime.IsBetween(6, 18) and not GameTime.IsBetween(20, 6))
        end
        local frame = 0
        function Update(dt)
          frame = frame + 1
          if frame == 61 then -- (checks at 1 s)
            -- 1 s at 60 fps: 1 game hour.
            chk("one_hour_per_second", math.abs(GameTime.Hour - 9) < 0.05)
            chk("hour_changed_fired", hours[1] == "9@1")
            GameTime.Pause()
          elseif frame == 90 then
            chk("paused_clock_stops", math.abs(GameTime.Hour - 9) < 0.05 and GameTime.Paused)
            GameTime.Resume()
            GameTime.Scale = 10      -- 10 game hours per second
          elseif frame == 162 then
            -- about 12 game hours later: past midnight, 07:00 not yet... (9 + 12 = 21)
            chk("scale_speeds_up", math.abs(GameTime.Hour - 21) < 0.5)
            chk("after_fired_once", after == 1)
            chk("every_fired_repeatedly", every >= 3)
          elseif frame == 232 then
            -- 9 + 23.7 -> day 2, after 07:00
            chk("day_changed", days[1] == 2 and GameTime.Day == 2)
            chk("at_seven_fired_on_day2", atSeven == 1)
            local state = GameTime.GetState()
            GameTime.Set(5, 12.5)
            chk("set_jumps", GameTime.Day == 5 and math.abs(GameTime.Hour - 12.5) < 1e-9 and GameTime.Minute == 30)
            GameTime.SetState(state)
            chk("state_round_trip", GameTime.Day == state.Day and math.abs(GameTime.Hour - state.Hour) < 1e-9 and GameTime.Scale == 10)
            GameTime.Scale = 1
          end
        end
        """;

    private const string DayNightProbe = """
        local DayNight = require("Wisko.DayNight")
        local elevations = {}
        function Start()
          GameTime.DayLength = 24
          GameTime.Set(1, 5)
          DayNight.Start()
          elevations.dawn = Atmosphere.SunElevation
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 420 then       -- 7 game hours later: 12:00
            elevations.noon = Atmosphere.SunElevation
            chk("sun_up_at_noon", elevations.noon > 60)
            chk("sun_down_before_dawn", elevations.dawn < 0)
            chk("sky_changed_with_time", Atmosphere.SkyColor ~= "#0B1430")
            print("DAYNIGHT dawn " .. elevations.dawn .. " noon " .. elevations.noon .. " sky " .. Atmosphere.SkyColor)
          end
        end
        """;

    public static void Clock(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeClock", ClockProbe), ("ProbeDayNight", DayNightProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeClock");
        var s = Begin(root, workspace);
        Run(s, 240);
        AssertNoFaults(report, "V5 GameTime", s.Faults);
        AssertLuaChecks(report, "V5 GameTime", s.Log, ["clock_starts_day1_0800", "day_length_set", "day_length_rejects_zero", "scale_rejects_negative",
            "set_rejects_hour_24", "is_between_day", "one_hour_per_second", "hour_changed_fired", "paused_clock_stops", "scale_speeds_up", "after_fired_once",
            "every_fired_repeatedly", "day_changed", "at_seven_fired_on_day2", "set_jumps", "state_round_trip"]);
        End(s, workspace);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeDayNight");
        s = Begin(root, workspace);
        Run(s, 425);
        AssertNoFaults(report, "V5 DayNight", s.Faults);
        AssertLuaChecks(report, "V5 DayNight", s.Log, ["sun_up_at_noon", "sun_down_before_dawn", "sky_changed_with_time"]);
        report.Check("V5 DayNight: the drawn Atmosphere changed (the session's, not the project's)", s.Play.PlayAtmosphere is { SunElevation: > 60f });
        End(s, workspace);
        report.Check("V5 DayNight: Stop gives the project's Atmosphere back", s.Play.PlayAtmosphere is null);
    }

    // ---- GameSave ----------------------------------------------------------------------------------------------

    private const string SaveProbe = """
        function Start()
          chk("available", GameSave.Available)
          local data = {
            Coins = 42, Health = 87.5, Name = "Robin", Alive = true,
            Position = Vector3.new(1.5, 2, -3.25),
            Inventory = { "Wood", "Stone", "Wood" },
            Counts = { Wood = 3, Stone = 1 },
            Nested = { Deep = { Deeper = { Value = 7 } } },
            Mixed = { [1] = "a", [3] = "c", key = "k", [true] = 1 },
            Dollar = { ["$vector"] = "not a vector", ["$$x"] = 2 },
            Big = 9007199254740993,
            Bytes = "\0\255binary",
            Empty = {},
          }
          local ok, problem = GameSave.Save("Slot1", data, { Version = 3, Metadata = { Level = "Forest", Minutes = 12 } })
          chk("save_ok", ok == true)
          if not ok then print("SAVE problem " .. tostring(problem)) end
          chk("exists_after_save", GameSave.Exists("Slot1"))
          local loaded, info = GameSave.Load("Slot1")
          chk("load_ok", loaded ~= nil)
          chk("integer_kept", loaded.Coins == 42 and math.type(loaded.Coins) == "integer")
          chk("float_kept", loaded.Health == 87.5 and math.type(loaded.Health) == "float")
          chk("big_integer_exact", loaded.Big == 9007199254740993)
          chk("string_and_bool", loaded.Name == "Robin" and loaded.Alive == true)
          chk("vector_round_trip", getmetatable(loaded.Position) == "Vector3" and loaded.Position == Vector3.new(1.5, 2, -3.25))
          chk("array_round_trip", #loaded.Inventory == 3 and loaded.Inventory[3] == "Wood")
          chk("map_round_trip", loaded.Counts.Wood == 3 and loaded.Counts.Stone == 1)
          chk("nested_round_trip", loaded.Nested.Deep.Deeper.Value == 7)
          chk("mixed_keys_round_trip", loaded.Mixed[1] == "a" and loaded.Mixed[3] == "c" and loaded.Mixed.key == "k" and loaded.Mixed[true] == 1)
          chk("dollar_keys_round_trip", loaded.Dollar["$vector"] == "not a vector" and loaded.Dollar["$$x"] == 2)
          chk("binary_string_round_trip", loaded.Bytes == "\0\255binary")
          chk("empty_table", type(loaded.Empty) == "table" and next(loaded.Empty) == nil)
          chk("info_version_metadata", info.Version == 3 and info.Metadata.Level == "Forest" and info.Metadata.Minutes == 12 and info.Recovered == false)
          chk("info_saved_at", type(info.SavedAt) == "string" and #info.SavedAt > 10)
          local list = GameSave.List()
          chk("list_has_slot", #list == 1 and list[1].Slot == "Slot1" and list[1].Metadata.Level == "Forest")
          chk("get_info", GameSave.GetInfo("Slot1").Version == 3)
          -- Refused values (errors naming the place).
          local okF, errF = pcall(GameSave.Save, "Slot2", { a = { b = function() end } })
          chk("function_refused", not okF and string.find(errF, "data.a.b is a function value", 1, true) ~= nil)
          local cyclic = {}
          cyclic.self = cyclic
          local okC, errC = pcall(GameSave.Save, "Slot2", cyclic)
          chk("cycle_refused", not okC and string.find(errC, "contains itself", 1, true) ~= nil)
          local okN, errN = pcall(GameSave.Save, "Slot2", { x = 0 / 0 })
          chk("nan_refused", not okN and string.find(errN, "not a finite number", 1, true) ~= nil)
          local okO, errO = pcall(GameSave.Save, "Slot2", { who = self })
          chk("object_refused", not okO and string.find(errO, "scene object", 1, true) ~= nil)
          local okM, errM = pcall(GameSave.Save, "Slot2", { m = setmetatable({}, {}) })
          chk("metatable_refused", not okM and string.find(errM, "metatable", 1, true) ~= nil)
          chk("deep_refused", not pcall(function()
            local t = {}
            local cur = t
            for i = 1, 70 do cur.n = {} cur = cur.n end
            return GameSave.Save("Slot2", t)
          end))
          local many = {}
          for i = 1, 200001 do many[i] = i end
          local okMany, errMany = pcall(GameSave.Save, "Slot2", many)
          chk("too_many_values_refused", not okMany and string.find(errMany, "more than 200000 values", 1, true) ~= nil and not GameSave.Exists("Slot2"))
          -- Slot names: no paths.
          for _, bad in ipairs({ "../evil", "..\\evil", "C:\\Windows\\x", "a/b", "", "CON", " lead", "trail ", string.rep("a", 65), "x.json" }) do
            if pcall(GameSave.Save, bad, { a = 1 }) then
              chk("bad_slot_" .. bad, false)
            end
          end
          chk("bad_slots_refused", true)
          chk("missing_slot_answer", select(1, GameSave.Load("Nope")) == nil and string.find(select(2, GameSave.Load("Nope")), "no save", 1, true) ~= nil)
          chk("delete", GameSave.Delete("Slot1") == true and not GameSave.Exists("Slot1"))
          chk("delete_missing_ok", GameSave.Delete("Slot1") == true)
          -- Saved again for the next session.
          GameSave.Save("Persist", { Position = Vector3.new(4, 5, 6), Quest = { wood = 2 }, Hour = 13.25 })
        end
        """;

    private const string ReloadProbe = """
        function Start()
          local data, info = GameSave.Load("Persist")
          chk("next_session_loads", data ~= nil and data.Position == Vector3.new(4, 5, 6) and data.Quest.wood == 2 and data.Hour == 13.25)
          local damaged, reason = GameSave.Load("Damaged")
          chk("damaged_reported", damaged == nil and string.find(reason, "damaged", 1, true) ~= nil)
          local recovered, rinfo = GameSave.Load("Recover")
          chk("backup_recovered", recovered ~= nil and recovered.Value == 1 and rinfo.Recovered == true)
          local foreign, why = GameSave.Load("Foreign")
          chk("foreign_json_refused", foreign == nil and string.find(why, "not a Wisko game save", 1, true) ~= nil)
          local newer, newerWhy = GameSave.Load("Newer")
          chk("newer_format_refused", newer == nil and string.find(newerWhy, "newer Wisko", 1, true) ~= nil)
        end
        """;

    public static void Saves(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeSave", SaveProbe), ("ProbeReload", ReloadProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeSave");
        var s = Begin(root, workspace);
        Run(s, 2);
        AssertNoFaults(report, "V5 GameSave", s.Faults);
        AssertLuaChecks(report, "V5 GameSave", s.Log, ["available", "save_ok", "exists_after_save", "load_ok", "integer_kept", "float_kept", "big_integer_exact",
            "string_and_bool", "vector_round_trip", "array_round_trip", "map_round_trip", "nested_round_trip", "mixed_keys_round_trip", "dollar_keys_round_trip",
            "binary_string_round_trip", "empty_table", "info_version_metadata", "info_saved_at", "list_has_slot", "get_info", "function_refused", "cycle_refused",
            "nan_refused", "object_refused", "metatable_refused", "deep_refused", "too_many_values_refused", "bad_slots_refused", "missing_slot_answer", "delete", "delete_missing_ok"]);
        var leaked = LuaReports(s.Log).Keys.Where(k => k.StartsWith("bad_slot_", StringComparison.Ordinal)).ToList();
        report.Check("V5 GameSave: every path-like slot name refused", leaked.Count == 0, string.Join(", ", leaked));
        End(s, workspace);

        // On disk: only <project>/Saves/<slot>.json (+ .bak), no temporary files, nothing outside the folder.
        var saves = Path.Combine(root, "Saves");
        var files = Directory.Exists(saves) ? Directory.GetFiles(saves).Select(Path.GetFileName).Order().ToList() : [];
        report.Check("V5 GameSave: files only in the project's Saves folder", files.SequenceEqual(["Persist.json"]), string.Join(", ", files));
        report.Check("V5 GameSave: nothing written next to the project or above it",
            !File.Exists(Path.Combine(dir, "evil.json")) && !Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Contains("evil")));
        var persisted = File.ReadAllText(Path.Combine(saves, "Persist.json"));
        using (var document = JsonDocument.Parse(persisted))
        {
            var envelope = document.RootElement;
            report.Check("V5 GameSave: file is JSON with format, versions and data",
                envelope.GetProperty("format").GetString() == "wisko-save" && envelope.GetProperty("formatVersion").GetInt32() == 1
                && envelope.GetProperty("version").GetInt64() == 1 && envelope.GetProperty("data").GetProperty("Hour").GetDouble() == 13.25);
        }

        // Damaged files, a recoverable one (its .bak intact), a foreign JSON and a newer format.
        File.WriteAllText(Path.Combine(saves, "Damaged.json"), "{ \"format\": \"wisko-save\", \"formatVersion\": 1, \"data\": { \"a\": ");
        File.WriteAllText(Path.Combine(saves, "Recover.json"), "garbage");
        File.WriteAllText(Path.Combine(saves, "Recover.json.bak"), "{ \"format\": \"wisko-save\", \"formatVersion\": 1, \"version\": 1, \"savedAt\": \"x\", \"data\": { \"Value\": 1 } }");
        File.WriteAllText(Path.Combine(saves, "Foreign.json"), "{ \"hello\": 1 }");
        File.WriteAllText(Path.Combine(saves, "Newer.json"), "{ \"format\": \"wisko-save\", \"formatVersion\": 99, \"data\": {} }");

        // A new Play session (as after closing and reopening the game) reads the saves.
        workspace = new Workspace();
        AddProbe(workspace, "ProbeReload");
        s = Begin(root, workspace);
        Run(s, 2);
        AssertNoFaults(report, "V5 GameSave (next session)", s.Faults);
        AssertLuaChecks(report, "V5 GameSave (next session)", s.Log, ["next_session_loads", "damaged_reported", "backup_recovered", "foreign_json_refused", "newer_format_refused"]);
        End(s, workspace);

        // Safe writes: overwriting keeps the previous version as .bak and never leaves a .tmp behind.
        var store = new SaveStore(root);
        report.Check("V5 GameSave: overwrite keeps the previous version", store.Write("Persist", Encoding.UTF8.GetBytes("{}")) is null && File.Exists(Path.Combine(saves, "Persist.json.bak"))
            && File.ReadAllText(Path.Combine(saves, "Persist.json.bak")).Contains("wisko-save"));
        report.Check("V5 GameSave: no temporary file left", !Directory.GetFiles(saves, "*.tmp").Any());
        report.Check("V5 GameSave: store refuses traversal itself", store.Write("../x", [1]) is not null && store.Write("a\\b", [1]) is not null && !File.Exists(Path.Combine(root, "x.json")));
        report.Check("V5 GameSave: no project, no saving", new SaveStore(null).Write("Slot1", [1]) is { } noProject && noProject.Contains("project"));
        SavesFolderLink(report, dir);
    }

    // A project whose Saves folder is a link (a junction, which needs no administrator rights) to somewhere else: saves
    // are refused rather than written through it.
    private static void SavesFolderLink(TestReport report, string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            TestReport.Info("V5 GameSave: the Saves-folder link check runs on Windows only (junctions)");
            return;
        }

        var project = Path.Combine(dir, "linked", "Proj");
        var elsewhere = Path.Combine(dir, "linked", "Elsewhere");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(elsewhere);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(project, "Saves")}\" \"{elsewhere}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var junction = System.Diagnostics.Process.Start(start)!;
        junction.WaitForExit();
        var link = Path.Combine(project, "Saves");
        if (!Directory.Exists(link) || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
        {
            report.Check("V5 GameSave: a junction could be made for the link check", false, $"mklink exit {junction.ExitCode}");
            return;
        }

        var write = new SaveStore(project).Write("Slot1", Encoding.UTF8.GetBytes("{}"));
        report.Check("V5 GameSave: a Saves folder that is a link is refused, nothing written through it",
            write is not null && write.Contains("link", StringComparison.Ordinal) && Directory.GetFiles(elsewhere).Length == 0, write);
        Directory.Delete(link); // the junction only, not its target
    }

    // ---- DebugDraw, Particles ------------------------------------------------------------------------------------

    private const string DrawProbe = """
        local rain
        function Start()
          chk("line", DebugDraw.Line(Vector3.new(0, 1, 0), Vector3.new(5, 1, 0), "#FF0000", 0.5))
          DebugDraw.Sphere(Vector3.new(0, 2, 0), 1)
          DebugDraw.Box(Vector3.new(0, 2, 0), Vector3.new(2, 2, 2), "#00FF00", 0, Vector3.new(0, 45, 0))
          DebugDraw.Ray(Vector3.new(0, 0, 0), Vector3.new(0, 3, 0))
          DebugDraw.Point(Vector3.new(1, 1, 1))
          chk("counts_lines", DebugDraw.Count == 1 + 72 + 12 + 1 + 3)
          chk("bad_colour", not pcall(DebugDraw.Line, Vector3.new(0, 0, 0), Vector3.new(1, 0, 0), "red"))
          chk("bad_vector", not pcall(DebugDraw.Line, 1, Vector3.new(1, 0, 0)))
          rain = Particles.CreateEmitter({ Position = Vector3.new(0, 20, 0), Area = Vector3.new(20, 0, 20), Rate = 600, Lifetime = 0.5,
            Velocity = Vector3.new(0, -40, 0), Color = "#A0B0D0", Size = 0.05, Length = 1 })
          chk("emitter_props", rain.Rate == 600 and rain.Color == "#A0B0D0" and rain.Exists)
          chk("emitter_bad_option", not pcall(Particles.CreateEmitter, { Rat = 3 }))
          chk("emitter_bad_rate", not pcall(Particles.CreateEmitter, { Rate = -1 }))
          chk("burst", rain:Burst(50) == 50)
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 2 then
            chk("frame_lines_gone_timed_stay", DebugDraw.Count == 1)
          elseif frame == 60 then
            chk("particles_alive", rain.Count > 200 and rain.Count < 400)
            chk("total_count", Particles.Count == rain.Count)
            rain.Enabled = false
          elseif frame == 100 then
            chk("disabled_emitter_drains", rain.Count == 0)
            chk("timed_line_expired", DebugDraw.Count == 0)
            rain:Destroy()
            chk("destroyed", not rain.Exists and not pcall(function() rain.Rate = 3 end))
          end
        end
        """;

    public static void DrawingAndParticles(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeDraw", DrawProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeDraw");
        var s = Begin(root, workspace);
        // Lines drawn in Start are what the first frame shows.
        var gameplay = s.Play.Gameplay!;
        var segments = new List<DebugSegment>();
        gameplay.Debug.CopySegments(segments);
        report.Check("V5 DebugDraw: lines reach the renderer's list", segments.Count == 89 && segments[0].Color.X == 1f && segments[0].Color.Y == 0f, $"{segments.Count}");
        Run(s, 60);
        var emitter = gameplay.Particles.Emitters.Single();
        var positions = emitter.Positions.ToArray();
        report.Check("V5 Particles: particles move down from the spawn box", positions.Length > 0 && positions.All(p => p.Y < 20.5f && p.Y > -5f && MathF.Abs(p.X) <= 15f));
        Run(s, 45);
        AssertNoFaults(report, "V5 DebugDraw/Particles", s.Faults);
        AssertLuaChecks(report, "V5 DebugDraw/Particles", s.Log, ["line", "counts_lines", "bad_colour", "bad_vector", "emitter_props", "emitter_bad_option",
            "emitter_bad_rate", "burst", "frame_lines_gone_timed_stay", "particles_alive", "total_count", "disabled_emitter_drains", "timed_line_expired", "destroyed"]);
        End(s, workspace);
        report.Check("V5 Particles/DebugDraw: nothing left after Stop", gameplay.Particles.Emitters.Count == 0 && gameplay.Debug.Count == 0);
    }

    // ---- Cameras -----------------------------------------------------------------------------------------------

    private const string CameraProbe = """
        local security, activated
        function Start()
          Camera.Position = Vector3.new(0, 10, 30)
          security = Cameras.Create("Security", { Position = Vector3.new(20, 15, 20), LookAt = Vector3.new(0, 0, 0), FieldOfView = 50 })
          chk("created", security.Name == "Security" and security.FieldOfView == 50 and Cameras.Find("Security") == security)
          chk("duplicate_name_refused", not pcall(Cameras.Create, "Security"))
          chk("look_at_turns", security.Rotation.x < -20)
          chk("not_active_yet", Cameras.GetActive() == nil and not security.Active)
          Cameras.OnActivated(function(camera, previous) activated = (camera and camera.Name or "gameplay") .. "<-" .. (previous and previous.Name or "gameplay") end)
          local ok, why = security:CreateFeed({ Width = 320, Height = 180 })
          chk("feed_refused_without_renderer", ok == false and string.find(why, "not available", 1, true) ~= nil)
          chk("bad_fov", not pcall(function() security.FieldOfView = 500 end))
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 2 then
            Cameras.SetActive(security)
          elseif frame == 4 then
            chk("gameplay_camera_kept_for_scripts", Camera.Position == Vector3.new(0, 10, 30))
            chk("active_reported", Cameras.GetActive() == security and security.Active)
            chk("activated_event", activated == "Security<-gameplay")
            Camera.Position = Vector3.new(1, 10, 30)    -- scripts still move the gameplay camera
            Cameras.SetActive(nil, { Blend = 1 })
          elseif frame == 40 then
            print("CAMERA blending")
          elseif frame == 70 then
            chk("back_to_gameplay_event", activated == "gameplay<-Security")
            local path = Path.new({ Vector3.new(0, 5, 0), Vector3.new(10, 8, 0), Vector3.new(20, 5, 10) })
            local playback = security:PlayPath(path, { Duration = 1, LookAt = Vector3.new(0, 0, 0) })
            Cameras.SetActive(security)
            playback:OnFinished(function() chk("path_finished", Vector3.distance(security.Position, Vector3.new(20, 5, 10)) < 1e-3) end)
          elseif frame == 140 then
            Cameras.Shake(1, 0.5)
          elseif frame == 200 then
            security:Destroy()
            chk("destroy_ends_active", Cameras.GetActive() == nil and not security.Exists)
          end
        end
        """;

    public static void Cameras(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeCamera", CameraProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeCamera");
        var camera = new StubCamera();
        var s = Begin(root, workspace, camera);
        Run(s, 3);
        report.Check("V5 Cameras: the view shows the active camera after the frame", Dist(camera.Position, new Vector3(20, 15, 20)) < 1e-4f
            && MathF.Abs(camera.FieldOfView - 50f) < 1e-3f, $"{camera.Position}");
        Run(s, 2);
        var during = new List<Vector3>();
        for (var i = 0; i < 64; i++)
        {
            Run(s, 1);
            during.Add(camera.Position);
        }

        var mid = during[28];
        report.Check("V5 Cameras: a blend passes between the two poses (smooth, no jump)",
            mid.X > 2f && mid.X < 19f && during.Zip(during.Skip(1)).All(pair => Dist(pair.First, pair.Second) < 1.5f), $"mid {mid}");
        report.Check("V5 Cameras: back on the gameplay camera after the blend", Dist(during[^1], new Vector3(1, 10, 30)) < 1e-3f, $"{during[^1]}");
        Run(s, 70);
        var beforeShake = camera.Position;
        Run(s, 5);
        var shaking = camera.Position;
        Run(s, 60);
        report.Check("V5 Cameras: shake moves what is shown, then settles", Dist(beforeShake, shaking) > 0.05f && s.Play.Gameplay!.Cameras.IsShaking == false);
        Run(s, 10);
        AssertNoFaults(report, "V5 Cameras", s.Faults);
        AssertLuaChecks(report, "V5 Cameras", s.Log, ["created", "duplicate_name_refused", "look_at_turns", "not_active_yet", "feed_refused_without_renderer",
            "bad_fov", "gameplay_camera_kept_for_scripts", "active_reported", "activated_event", "back_to_gameplay_event", "path_finished", "destroy_ends_active"]);
        End(s, workspace);
        CameraFollowAndFeeds(report, dir);
    }

    // camera:Follow (offset in the target's axes, looking at it, StopFollowing) and feed requests with a renderer that
    // supports feeds (at most four, sizes checked). Headless, so the feeds are requested but not drawn.
    private static void CameraFollowAndFeeds(TestReport report, string dir)
    {
        const string probe = """
            local cam, target
            local frame, held = 0, nil
            function Start()
              target = Scene.Find("Target")
              cam = Cameras.Create("Chase")
              chk("follow_needs_object", not pcall(function() cam:Follow(3) end))
              chk("follow_typo_option", not pcall(function() cam:Follow(target, { Ofset = Vector3.new(0, 1, 0) }) end))
              cam:Follow(target, { Offset = Vector3.new(0, 4, 10) })
              chk("following", cam.Following)
              local made = 0
              for i = 1, 4 do
                if Cameras.Create("Feed" .. i):CreateFeed({ Width = 64, Height = 64 }) == true then made = made + 1 end
              end
              local fifth = Cameras.Create("Feed5")
              local ok, why = fifth:CreateFeed()
              print("CAMERA fifth feed: " .. tostring(why))
              chk("four_feeds", made == 4 and Cameras.Find("Feed1").HasFeed and Cameras.FeedsSupported)
              chk("fifth_feed_refused", ok == false and string.find(why, "at most 4", 1, true) ~= nil and not fifth.HasFeed)
              chk("feed_size_checked", not pcall(function() fifth:CreateFeed({ Width = 4000 }) end)
                and not pcall(function() fifth:CreateFeed({ Height = 8 }) end))
            end
            function Update()
              frame = frame + 1
              if frame <= 30 then
                target.Transform.Position = Vector3.new(frame * 0.2, 2, 0)
              end
              if frame == 20 then
                -- The camera follows after LateUpdate: now it shows where the target was last frame.
                local expected = Vector3.new(19 * 0.2, 6, 10)
                chk("follows_with_offset", Vector3.distance(cam.Position, expected) < 1e-3)
                local toTarget = Vector3.normalize(Vector3.new(19 * 0.2, 2, 0) - cam.Position)
                chk("looks_at_target", Vector3.dot(cam.Forward, toTarget) > 0.999)
              elseif frame == 31 then
                target.Transform.Rotation = Vector3.new(0, 90, 0)
              elseif frame == 33 then
                local d = cam.Position - target.Transform.Position
                print("CAMERA offset after a 90 degree turn " .. tostring(d))
                chk("offset_turns_with_target", math.abs(math.abs(d.x) - 10) < 1e-3 and math.abs(d.y - 4) < 1e-3 and math.abs(d.z) < 1e-3)
                cam:StopFollowing()
              elseif frame == 35 then
                held = cam.Position
                target.Transform.Position = Vector3.new(-20, 2, -20)
              elseif frame == 40 then
                chk("stop_following_holds", not cam.Following and Vector3.distance(cam.Position, held) < 1e-6)
              end
            end
            """;
        var root = Project(Path.Combine(dir, "follow"), ("ProbeCamFollow", probe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeCamFollow");
        var target = Add(workspace, "Target", new Vector3(0, 2, 0), new Vector3(1, 1, 1), true);
        target.CanCollide = false;
        var s = Begin(root, workspace, feeds: true);
        Run(s, 42);
        AssertNoFaults(report, "V5 camera follow/feeds", s.Faults);
        AssertLuaChecks(report, "V5 camera follow/feeds", s.Log, ["follow_needs_object", "follow_typo_option", "following", "four_feeds", "fifth_feed_refused",
            "feed_size_checked", "follows_with_offset", "looks_at_target", "offset_turns_with_target", "stop_following_holds"]);
        foreach (var line in Lines(s.Log, "CAMERA "))
        {
            TestReport.Info(line);
        }

        End(s, workspace);
    }

    internal static float Dist(Vector3 a, Vector3 b) => (a - b).Length;
}
