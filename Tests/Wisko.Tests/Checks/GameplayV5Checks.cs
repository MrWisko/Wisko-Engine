using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Lua Capabilities V5: the engine primitives (Time.OnUpdate / SetTimeScale, Random, Noise, Path, GameTime, GameSave,
/// DebugDraw, Particles, Cameras, Teleport, bones, trigger connections, Navigation, Perception), played headless through
/// PlayModeController with small probe scripts that report what they see. Each probe only uses the public Lua API.
/// </summary>
internal static partial class GameplayV5Checks
{
    internal sealed record Session(PlayModeController Play, OutputLog Log, List<PlayScriptFault> Faults, StubCamera Camera);

    internal static Session Begin(string root, Workspace workspace, StubCamera? camera = null, bool feeds = false,
        Wisko.Engine.Graphics.GraphicsSettings? graphics = null)
    {
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        camera ??= new StubCamera();
        var play = new PlayModeController { Output = log, CameraHost = camera, PhysicsEnvironment = LuaV4Demo.Baseplate(), CameraFeedsSupported = feeds };
        if (graphics is not null)
        {
            play.GraphicsSource = graphics;
        }
        if (!play.TryStart(root, workspace, [], null, out var error))
        {
            throw new InvalidOperationException($"PlayModeController.TryStart failed: {error}");
        }

        faults.AddRange(play.TakeNewFaults());
        return new Session(play, log, faults, camera);
    }

    internal static void End(Session session, Workspace workspace)
    {
        session.Play.Stop(workspace);
        session.Play.Dispose();
    }

    internal static Part Add(Workspace workspace, string name, Vector3 position, Vector3 size, bool anchored, params string[] scripts)
    {
        var part = MakePart(name, position, size, anchored, scripts.Select(s => $"Assets/Scripts/{s}.lua").ToArray());
        workspace.AddPart(part);
        return part;
    }

    internal static Part AddProbe(Workspace workspace, string script = "Probe")
    {
        var probe = Add(workspace, script, new Vector3(0, 0.5f, 200), new Vector3(1, 1, 1), true, script);
        probe.CanCollide = false;
        probe.Visible = false;
        return probe;
    }

    internal static string Project(string dir, params (string Name, string Source)[] scripts)
    {
        var root = Path.Combine(dir, "Proj");
        Directory.CreateDirectory(root);
        foreach (var (name, source) in scripts)
        {
            WriteScript(root, name + ".lua", name.StartsWith("Probe", StringComparison.Ordinal) ? ChkFunction + source : source);
        }

        return root;
    }

    internal static void Run(Session session, int frames) => Step(session.Play, frames, session.Faults);

    internal static List<string> Lines(OutputLog log, string prefix)
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

    internal static string? Last(OutputLog log, string prefix) => Lines(log, prefix).LastOrDefault();

    // ---- Time, Random, Noise, Path ------------------------------------------------------------------------------

    private const string TimeProbe = """
        local updates, afterUpdate, seenOrder = 0, false, {}
        local connection
        function Start()
          connection = Time.OnUpdate(function(dt)
            updates = updates + 1
            seenOrder[#seenOrder + 1] = "callback"
            chk("onupdate_dt_is_frame_dt", math.abs(dt - Time.deltaTime) < 1e-9)
          end)
          chk("onupdate_returns_connection", connection.Connected == true)
          chk("time_scale_starts_at_1", Time.timeScale == 1)
          chk("onupdate_needs_function", not pcall(Time.OnUpdate, 3))
          chk("timescale_rejects_negative", not pcall(Time.SetTimeScale, -1))
          chk("timescale_rejects_string", not pcall(Time.SetTimeScale, "fast"))
          -- GameTime is built on first use: a typo on that first use still names this script's line.
          local okTypo, typo = pcall(function() return GameTime.Hourz end)
          chk("lazy_library_error_names_script", not okTypo and string.find(typo, "ProbeTime", 1, true) ~= nil
            and string.find(typo, "Hourz is not a valid member of GameTime", 1, true) ~= nil and string.find(typo, "gameplay", 1, true) == nil)
          print("TYPO " .. tostring(typo))
        end
        local frame = 0
        local elapsedAtPause
        function Update(dt)
          frame = frame + 1
          seenOrder[#seenOrder + 1] = "update"
          if frame == 3 then
            chk("onupdate_runs_after_update", seenOrder[#seenOrder - 1] == "callback" and seenOrder[#seenOrder - 2] == "update")
          end
          if frame == 10 then
            Time.SetTimeScale(0.5)
          elseif frame == 12 then
            chk("half_speed_dt", math.abs(dt - Time.unscaledDeltaTime * 0.5) < 1e-9 and Time.timeScale == 0.5)
            Time.SetTimeScale(0)
          elseif frame == 13 then
            elapsedAtPause = Time.elapsedTime
          elseif frame == 20 then
            chk("paused_dt_zero", dt == 0 and Time.unscaledDeltaTime > 0)
            chk("paused_elapsed_frozen", Time.elapsedTime == elapsedAtPause)
            chk("realtime_moves_while_paused", Time.realTime > 0)
            Time.SetTimeScale(1)
          elseif frame == 30 then
            local before = updates
            connection:Disconnect()
            chk("disconnect_stops_callbacks", not connection.Connected)
            Timer.After(0, function() chk("no_callback_after_disconnect", updates == before) end)
          end
        end
        """;

    private const string RandomProbe = """
        local function sequence(seed, n)
          local r = Random.new(seed)
          local out = {}
          for i = 1, n do out[i] = r:Integer(1, 1000000) end
          return table.concat(out, ",")
        end
        function Start()
          chk("same_seed_same_sequence", sequence(42, 50) == sequence(42, 50))
          chk("other_seed_other_sequence", sequence(42, 50) ~= sequence(43, 50))
          chk("string_seed_reproducible", sequence("world-7", 20) == sequence("world-7", 20))
          -- A known first value pins the algorithm (xoshiro256** seeded by splitmix64): the same on every platform.
          local r = Random.new(1)
          local first = r:Integer(1, 1000000)
          print("RANDOM first(1) = " .. first)
          local inRange, floats = true, true
          for _ = 1, 1000 do
            local v = r:Integer(-3, 3)
            if v < -3 or v > 3 or math.type(v) ~= "integer" then inRange = false end
            local f = r:Number()
            if f < 0 or f >= 1 then floats = false end
          end
          chk("integer_in_range", inRange)
          chk("number_in_0_1", floats)
          local counts = { a = 0, b = 0 }
          local w = Random.new(7)
          for _ = 1, 4000 do
            local pick = w:Weighted({ { "a", 3 }, { "b", 1 } })
            counts[pick] = counts[pick] + 1
          end
          chk("weighted_roughly_3_to_1", counts.a > 2700 and counts.a < 3300)
          chk("weighted_map_reproducible", Random.new(5):Weighted({ Tree = 2, Rock = 1, Bush = 1 }) == Random.new(5):Weighted({ Bush = 1, Rock = 1, Tree = 2 }))
          local list = { 1, 2, 3, 4, 5, 6, 7, 8 }
          Random.new(9):Shuffle(list)
          local sum = 0
          for _, v in ipairs(list) do sum = sum + v end
          chk("shuffle_keeps_items", sum == 36 and #list == 8)
          local s = Random.new(11)
          s:Number()
          local state = s:GetState()
          local a = s:Number()
          s:SetState(state)
          chk("state_round_trip", s:Number() == a)
          local clone = Random.new(12)
          clone:Number()
          local c2 = clone:Clone()
          chk("clone_continues", clone:Number() == c2:Number())
          local u = Random.new(3):UnitVector()
          chk("unit_vector_length_1", math.abs(Vector3.magnitude(u) - 1) < 1e-9)
          local disc = Random.new(3):PointInCircle(5)
          chk("point_in_circle", disc.y == 0 and (disc.x * disc.x + disc.z * disc.z) <= 25)
          chk("integer_rejects_fraction", not pcall(function() Random.new(1):Integer(1.5, 3) end))
          chk("colon_needed", not pcall(function() local g = Random.new(1); return g.Number() end))
          chk("noise_reproducible", Noise.Perlin(1.3, 2.7, 0.5, 4) == Noise.Perlin(1.3, 2.7, 0.5, 4))
          chk("noise_seed_changes_field", Noise.Perlin(1.3, 2.7, 0.5, 4) ~= Noise.Perlin(1.3, 2.7, 0.5, 5))
          local low, high = 1, -1
          for i = 1, 400 do
            local v = Noise.Fractal(i * 0.137, i * 0.071, 0, { Octaves = 4, Seed = 2 })
            low, high = math.min(low, v), math.max(high, v)
          end
          chk("fractal_in_range", low >= -1 and high <= 1 and high - low > 0.3)
          chk("perlin_zero_at_integer_lattice", math.abs(Noise.Perlin(3, 4, 5)) < 1e-12)
        end
        """;

    private const string PathProbe = """
        function Start()
          local line = Path.new({ Vector3.new(0, 0, 0), Vector3.new(10, 0, 0), Vector3.new(10, 0, 10) }, { Type = "Linear" })
          chk("linear_length", math.abs(line.Length - 20) < 1e-6)
          local mid = line:GetPointAtDistance(15)
          chk("linear_point_at_distance", Vector3.distance(mid, Vector3.new(10, 0, 5)) < 1e-6)
          local t = line:GetTangentAtDistance(15)
          chk("linear_tangent", Vector3.distance(t, Vector3.new(0, 0, 1)) < 1e-6)
          chk("point_at_t", Vector3.distance(line:GetPointAt(0.25), Vector3.new(5, 0, 0)) < 1e-6)
          local d, p = line:GetClosestDistance(Vector3.new(12, 3, 4))
          chk("closest_distance", math.abs(d - 14) < 0.01 and Vector3.distance(p, Vector3.new(10, 0, 4)) < 0.01)
          local back = line:Reverse()
          chk("reverse_starts_at_end", Vector3.distance(back:GetPointAtDistance(0), Vector3.new(10, 0, 10)) < 1e-6)
          local curve = Path.new({ Vector3.new(0, 0, 0), Vector3.new(10, 0, 0), Vector3.new(10, 0, 10), Vector3.new(0, 0, 10) }, { Type = "CatmullRom" })
          chk("catmull_passes_points", Vector3.distance(curve:GetPointAtDistance(0), Vector3.new(0, 0, 0)) < 1e-6
            and Vector3.distance(curve:GetPointAtDistance(curve.Length), Vector3.new(0, 0, 10)) < 1e-6)
          chk("catmull_longer_than_chords", curve.Length > 30)
          -- Distance-based: equal distance steps are equally far apart along the curve (within sampling error).
          local a = curve:GetPointAtDistance(10)
          local b = curve:GetPointAtDistance(10.5)
          chk("arc_length_steps", math.abs(Vector3.distance(a, b) - 0.5) < 0.02)
          local loop = Path.new({ Vector3.new(0, 0, 0), Vector3.new(10, 0, 0), Vector3.new(10, 0, 10), Vector3.new(0, 0, 10) }, { Closed = true })
          chk("closed_wraps", Vector3.distance(loop:GetPointAtDistance(loop.Length + 1), loop:GetPointAtDistance(1)) < 1e-6)
          local bezier = Path.new({ Vector3.new(0, 0, 0), Vector3.new(0, 0, 5), Vector3.new(10, 0, 5), Vector3.new(10, 0, 0) }, { Type = "Bezier" })
          chk("bezier_ends", Vector3.distance(bezier:GetPointAtDistance(bezier.Length), Vector3.new(10, 0, 0)) < 1e-6)
          chk("bezier_bulges", bezier:GetPointAt(0.5).z > 3)
          chk("needs_two_points", not pcall(Path.new, { Vector3.new(0, 0, 0) }))
          chk("bad_type", not pcall(Path.new, { Vector3.new(0, 0, 0), Vector3.new(1, 0, 0) }, { Type = "Spline" }))
          chk("bad_bezier_count", not pcall(Path.new, { Vector3.new(0, 0, 0), Vector3.new(1, 0, 0), Vector3.new(2, 0, 0) }, { Type = "Bezier" }))
          chk("typo_option", not pcall(Path.new, { Vector3.new(0, 0, 0), Vector3.new(1, 0, 0) }, { Close = true }))
          chk("draw_makes_lines", curve:Draw("#00FFFF", 5) == true and DebugDraw.Count > 0)
        end
        """;

    public static void Foundations(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeTime", TimeProbe), ("ProbeRandom", RandomProbe), ("ProbePath", PathProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeTime");
        AddProbe(workspace, "ProbeRandom");
        AddProbe(workspace, "ProbePath");
        var s = Begin(root, workspace);
        Run(s, 40);
        AssertNoFaults(report, "V5 foundations", s.Faults);
        AssertLuaChecks(report, "V5 Time", s.Log, ["lazy_library_error_names_script", "onupdate_returns_connection", "time_scale_starts_at_1", "onupdate_needs_function", "timescale_rejects_negative",
            "timescale_rejects_string", "onupdate_dt_is_frame_dt", "onupdate_runs_after_update", "half_speed_dt", "paused_dt_zero", "paused_elapsed_frozen",
            "realtime_moves_while_paused", "disconnect_stops_callbacks", "no_callback_after_disconnect"]);
        AssertLuaChecks(report, "V5 Random/Noise", s.Log, ["same_seed_same_sequence", "other_seed_other_sequence", "string_seed_reproducible", "integer_in_range",
            "number_in_0_1", "weighted_roughly_3_to_1", "weighted_map_reproducible", "shuffle_keeps_items", "state_round_trip", "clone_continues",
            "unit_vector_length_1", "point_in_circle", "integer_rejects_fraction", "colon_needed", "noise_reproducible", "noise_seed_changes_field",
            "fractal_in_range", "perlin_zero_at_integer_lattice"]);
        var first = Last(s.Log, "RANDOM first(1) = ");
        TestReport.Info("Random.new(1):Integer(1, 1000000) = " + first?["RANDOM first(1) = ".Length..]);
        // An independent implementation (xoshiro256** seeded by splitmix64, as documented) gives the same number: the
        // sequence does not depend on Lua, the platform or the build.
        report.Check("V5 Random: matches an independent xoshiro256** implementation", first?["RANDOM first(1) = ".Length..] == ReferenceFirstInteger(1).ToString(),
            $"Lua {first}, reference {ReferenceFirstInteger(1)}");
        AssertLuaChecks(report, "V5 Path", s.Log, ["linear_length", "linear_point_at_distance", "linear_tangent", "point_at_t", "closest_distance",
            "reverse_starts_at_end", "catmull_passes_points", "catmull_longer_than_chords", "arc_length_steps", "closed_wraps", "bezier_ends", "bezier_bulges",
            "needs_two_points", "bad_type", "bad_bezier_count", "typo_option", "draw_makes_lines"]);

        // The physics really paused: a falling body did not move while the time scale was 0 (checked separately below).
        End(s, workspace);
        TimeScalePhysics(report, dir);
        TimeScaleEverywhere(report, dir);
        PathFollow(report, dir);
    }

    // Time.SetTimeScale(0.5) halves timers, animation and the game clock too, not only Update's dt.
    private static void TimeScaleEverywhere(TestReport report, string dir)
    {
        const string probe = """
            local robot, length, lastAnim
            local frame, setAt, firedAt = 0, nil, nil
            local normalAnim, slowAnim, normalHours, slowHours = 0, 0, 0, 0
            local hoursAt = {}
            function Start()
              robot = Scene.Find("Robot")
              chk("walk_plays", robot:PlayAnimation("Walking", { Loop = true }) == true)
              length = robot:GetAnimationLength()
            end
            local function animationStep()
              local now = robot:GetAnimationTime()
              local step = 0
              if lastAnim ~= nil then
                step = now - lastAnim
                if step < 0 then step = step + length end
              end
              lastAnim = now
              return step
            end
            function Update()
              frame = frame + 1
              local step = animationStep()
              hoursAt[frame] = GameTime.TotalHours
              if frame > 6 and frame <= 26 then normalAnim = normalAnim + step end
              if frame == 30 then
                Time.SetTimeScale(0.5)
                setAt = frame
                Timer.After(0.5, function() firedAt = frame end)
              end
              if frame > 40 and frame <= 60 then slowAnim = slowAnim + step end
              if frame == 130 then
                normalHours = hoursAt[26] - hoursAt[6]
                slowHours = hoursAt[60] - hoursAt[40]
                print("SCALE animation " .. normalAnim .. " -> " .. slowAnim .. ", clock " .. normalHours .. " -> " .. slowHours
                  .. ", timer of 0.5 s after " .. tostring(firedAt and firedAt - setAt) .. " frames")
                chk("animation_half_speed", normalAnim > 0.2 and math.abs(slowAnim / normalAnim - 0.5) < 0.02)
                chk("game_clock_half_speed", normalHours > 0 and math.abs(slowHours / normalHours - 0.5) < 0.02)
                -- 0.5 s of scaled time at half speed is 60 frames of 1/60 s (30 unscaled).
                chk("timer_half_speed", firedAt ~= nil and firedAt - setAt >= 58 and firedAt - setAt <= 62)
                Time.SetTimeScale(1)
              end
            end
            """;
        var root = Project(Path.Combine(dir, "scale-all"), ("ProbeScale", probe));
        var robotAsset = AnimationChecks.LoadAsset("RobotExpressive", "RobotExpressive.glb");
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeScale");
        workspace.AddPart(ModelPart("Robot", robotAsset, new Vector3(0, 0, 0), 1f));
        var s = Begin(root, workspace);
        Run(s, 135);
        AssertNoFaults(report, "V5 time scale", s.Faults);
        AssertLuaChecks(report, "V5 time scale", s.Log, ["walk_plays", "animation_half_speed", "game_clock_half_speed", "timer_half_speed"]);
        TestReport.Info(Last(s.Log, "SCALE ") ?? "no SCALE line");
        End(s, workspace);
    }

    // Path.Follow: an anchored platform driven by velocity along a path carries a rider, pauses, resumes and finishes
    // exactly at the end; a ping-pong follower in Transform mode turns round; wrong arguments are errors.
    private static void PathFollow(TestReport report, string dir)
    {
        const string probe = """
            local platform, rider, shuttle, follower, pong, odd
            local frame, finishedAt, loops, pausedAt = 0, nil, 0, nil
            local start, riderStart
            local line = Path.new({ Vector3.new(0, 1, 0), Vector3.new(8, 1, 0) }, { Type = "Linear" })
            function Start()
              platform, rider, shuttle = Scene.Find("Platform"), Scene.Find("Rider"), Scene.Find("Shuttle")
              chk("follow_needs_object", not pcall(Path.Follow, 3, line))
              chk("follow_needs_path", not pcall(Path.Follow, platform, {}))
              chk("follow_bad_mode", not pcall(Path.Follow, platform, line, { Mode = "Teleport" }))
              chk("follow_typo_option", not pcall(Path.Follow, platform, line, { Sped = 3 }))
            end
            function Update()
              frame = frame + 1
              if frame == 30 then
                start = platform.Transform.Position + Vector3.new(0, 0, 0)
                riderStart = rider.Transform.Position + Vector3.new(0, 0, 0)
                follower = Path.Follow(platform, line, { Speed = 4 })
                follower:OnFinished(function() finishedAt = frame end)
                chk("follower_state", follower.Speed == 4 and follower.Direction == 1 and not follower.Paused and not follower.Finished
                  and follower.Object == platform and follower.Path == line)
                pong = Path.Follow(shuttle, Path.new({ Vector3.new(0, 1, 20), Vector3.new(4, 1, 20) }, { Type = "Linear" }),
                  { Speed = 8, PingPong = true })
                pong:OnLooped(function() loops = loops + 1 end)
                -- A speed whose steps do not land on the end: it still finishes exactly there.
                odd = Path.Follow(Scene.Find("Platform2"), Path.new({ Vector3.new(0, 1, -20), Vector3.new(5, 1, -20) }, { Type = "Linear" }), { Speed = 3.7 })
              elseif frame == 60 then
                -- 30 frames at 4 studs per second: 2 studs.
                local moved = platform.Transform.Position.x - start.x
                print("FOLLOW moved " .. moved .. " distance " .. follower.Distance)
                chk("platform_moves_at_speed", math.abs(moved - 2) < 0.1 and math.abs(follower.Distance - 2) < 0.1)
                follower:Pause()
                pausedAt = platform.Transform.Position.x
              elseif frame == 75 then
                chk("pause_holds", follower.Paused and math.abs(platform.Transform.Position.x - pausedAt) < 0.01)
                follower:Resume()
              elseif frame == 220 then
                local p = platform.Transform.Position
                local r = rider.Transform.Position
                print("FOLLOW end " .. p.x .. " finished at frame " .. tostring(finishedAt) .. ", rider moved " .. (r.x - riderStart.x)
                  .. " at height " .. r.y .. ", ping-pong turns " .. loops .. ", shuttle " .. tostring(shuttle.Transform.Position))
                chk("finished_at_end", finishedAt ~= nil and follower.Finished and math.abs(p.x - 8) < 0.01 and Vector3.magnitude(platform.LinearVelocity) == 0)
                chk("rider_carried", r.x - riderStart.x > 7 and r.y > 1.9)
                local o = Scene.Find("Platform2").Transform.Position
                print("FOLLOW odd speed end " .. o.x)
                chk("odd_speed_ends_exactly", odd.Finished and math.abs(o.x - 5) < 1e-3 and math.abs(o.z + 20) < 1e-3)
                local q = shuttle.Transform.Position
                chk("pingpong_turns", loops >= 4 and pong.Direction ~= 0)
                chk("transform_mode_stays_on_path", math.abs(q.z - 20) < 1e-4 and math.abs(q.y - 1) < 1e-4 and q.x >= -1e-4 and q.x <= 4.0001)
                pong:Stop()
                chk("stopped", pong.Finished)
              end
            end
            """;
        var root = Project(Path.Combine(dir, "follow"), ("ProbeFollow", probe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeFollow");
        Add(workspace, "Platform", new Vector3(0, 1, 0), new Vector3(4, 1, 4), true);
        Add(workspace, "Rider", new Vector3(0, 2.6f, 0), new Vector3(1, 1, 1), false);
        Add(workspace, "Platform2", new Vector3(0, 1, -20), new Vector3(2, 1, 2), true);
        var shuttle = Add(workspace, "Shuttle", new Vector3(0, 1, 20), new Vector3(1, 1, 1), true);
        shuttle.CanCollide = false;
        var s = Begin(root, workspace);
        Run(s, 225);
        AssertNoFaults(report, "V5 Path.Follow", s.Faults);
        AssertLuaChecks(report, "V5 Path.Follow", s.Log, ["follow_needs_object", "follow_needs_path", "follow_bad_mode", "follow_typo_option", "follower_state",
            "platform_moves_at_speed", "pause_holds", "finished_at_end", "rider_carried", "odd_speed_ends_exactly", "pingpong_turns", "transform_mode_stays_on_path", "stopped"]);
        foreach (var line in Lines(s.Log, "FOLLOW "))
        {
            TestReport.Info(line);
        }

        End(s, workspace);
    }

    // Random.new(seed):Integer(1, 1000000) computed outside Lua.
    private static long ReferenceFirstInteger(ulong seed)
    {
        static (ulong State, ulong Value) SplitMix(ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (state, z ^ (z >> 31));
        }

        static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));
        var (mix, s0) = SplitMix(seed);
        (mix, var s1) = SplitMix(mix);
        (mix, var s2) = SplitMix(mix);
        (_, var s3) = SplitMix(mix);
        var result = Rotl(s1 * 5, 7) * 9;
        var number = (result >> 11) / 9007199254740992.0;
        return 1 + (long)Math.Floor(number * 1000000);
    }

    // Time.SetTimeScale(0) stops physics too; 1 lets it go on.
    private static void TimeScalePhysics(TestReport report, string dir)
    {
        const string pauser = """
            local frame = 0
            function Update()
              frame = frame + 1
              if frame == 5 then Time.SetTimeScale(0) elseif frame == 35 then Time.SetTimeScale(1) end
            end
            """;
        var root = Project(Path.Combine(dir, "scale"), ("Pauser", pauser));
        var workspace = new Workspace();
        AddProbe(workspace, "Pauser");
        var ball = Add(workspace, "Ball", new Vector3(0, 40, 0), new Vector3(2, 2, 2), false);
        var s = Begin(root, workspace);
        Run(s, 8);
        var y0 = ball.Position.Y;
        Run(s, 20);
        var y1 = ball.Position.Y;
        Run(s, 20);
        var y2 = ball.Position.Y;
        report.Check("V5 Time: physics stops at time scale 0", MathF.Abs(y1 - y0) < 1e-4f, $"moved {y0 - y1:0.0000} while paused");
        report.Check("V5 Time: physics goes on at time scale 1", y2 < y1 - 1f, $"fell {y1 - y2:0.00}");
        End(s, workspace);
    }
}
