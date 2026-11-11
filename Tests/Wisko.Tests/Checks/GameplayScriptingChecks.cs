using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Lua Capabilities V3 through the editor's PlayModeController: camera and movement (LateUpdate, GetAxis, mouse lock,
/// RotationLocked, IsGrounded, jumping, landing speed), script communication (GetScript, Shared, table events,
/// OnDestroyed, Disconnect, cleanup), physics filters, gravity and projectiles, and Play-only attachment.
/// </summary>
internal static class GameplayScriptingChecks
{
    public static void CameraAndMovement(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Player.lua", ChkFunction + """
            local wasGrounded, updateY, lateChecked = false, nil, false
            function Start()
              self.RotationLocked = true
              chk("rotation_locked_reads", self.RotationLocked == true)
              chk("mouse_lock", Input.SetMouseLocked(true) == true and Input.MouseLocked == true)
              Camera.Forward = Vector3.new(0, 0, 1)
              chk("camera_forward_set", math.abs(Camera.Rotation.y - 90) < 0.5 and math.abs(Camera.Rotation.x) < 0.5)
              chk("camera_forward_rejects_zero", not pcall(function() Camera.Forward = Vector3.new(0, 0, 0) end))
              chk("mouse_lock_rejects_non_boolean", not pcall(Input.SetMouseLocked, "yes"))
              chk("not_grounded_in_air", self:IsGrounded() == false)
              chk("grounded_bad_distance_errors", not pcall(self.IsGrounded, self, -1))
            end
            function Update(dt)
              updateY = self.Transform.Position.y
              local grounded, ground = self:IsGrounded()
              if grounded and not wasGrounded then
                chk("grounded_on_floor", ground ~= nil and ground.IsEnvironment == false and ground.Object ~= nil
                  and ground.Object.Name == "Floor" and ground.Normal.y > 0.9 and ground.Distance >= 0 and ground.Distance <= 0.15)
              end
              wasGrounded = grounded
              local axis = Input.GetAxis("A", "D")
              if axis ~= 0 then
                chk("axis_d_positive", axis == 1)
              end
              if grounded then
                -- Camera-relative: the camera looks along +Z, so its right is world -X... use world X for the test.
                local v = self.LinearVelocity
                self.LinearVelocity = Vector3.new(axis * 8, v.y, 0)
                if Input.IsKeyPressed("Space") then
                  self.LinearVelocity = Vector3.new(axis * 8, 12, 0)
                  chk("jump_from_ground", true)
                end
              end
            end
            function LateUpdate(dt)
              chk("late_update_gets_dt", type(dt) == "number" and dt > 0)
              if not lateChecked and updateY ~= nil and self.Transform.Position.y < updateY - 0.01 then
                lateChecked = true
                chk("late_update_after_physics", true)
              end
              -- A follow camera: behind and above, looking at the player.
              local p = self.Transform.Position
              Camera.Position = p + Vector3.new(0, 4, -12)
              Camera.LookAt(p)
            end
            function OnCollisionEnter(other, contact)
              if other ~= nil and other.Name == "Floor" and contact ~= nil and Shared.impact == nil then
                Shared.impact = math.abs(contact.RelativeVelocity.y)
                print("INFO landing impact speed " .. Shared.impact)
                chk("landing_impact_speed", Shared.impact > 5)
              end
            end
            """);

        var floor = MakePart("Floor", new Vector3(0, 0, 0), new Vector3(200, 2, 200), true);
        var player = MakePart("Player", new Vector3(0, 10, 0), new Vector3(2, 4, 2), false, "Assets/Scripts/Player.lua");
        var workspace = new Workspace();
        workspace.AddPart(floor);
        workspace.AddPart(player);

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, null, log, faults);
        var camera = (StubCamera)play.CameraHost!;
        Step(play, 150, faults);
        report.Check("Mouse lock: Input.SetMouseLocked reaches the Play camera", camera.MouseLocked);
        var restY = player.Position.Y;
        report.Check("Movement: player lands and rests on the floor (Y ~= 3)", MathF.Abs(restY - 3f) < 0.15f, $"Y = {restY:F3}");
        var startX = player.Position.X;
        StepHolding(play, 30, faults, "D");
        var movedX = player.Position.X - startX;
        report.Check("Movement: holding D (GetAxis) moves the player +X at ~8 studs/s", movedX is > 3f and < 5f, $"moved {movedX:F2} in 0.5 s");
        StepHolding(play, 1, faults, "Space");
        var peak = player.Position.Y;
        for (var i = 0; i < 60; i++)
        {
            Step(play, 1, faults);
            peak = MathF.Max(peak, player.Position.Y);
        }

        report.Check("Movement: Space jumps (peak well above rest)", peak > restY + 1.5f, $"rest {restY:F2}, peak {peak:F2}");
        Step(play, 90, faults);
        report.Check("Movement: lands again after the jump", MathF.Abs(player.Position.Y - 3f) < 0.15f, $"Y = {player.Position.Y:F3}");
        report.Check("RotationLocked: the sliding, jumping player never tipped over",
            MathF.Abs(player.Rotation.X) < 0.5f && MathF.Abs(player.Rotation.Z) < 0.5f, $"rotation {player.Rotation.X:F2}, {player.Rotation.Y:F2}, {player.Rotation.Z:F2}");
        var cameraOffset = camera.Position - player.Position;
        report.Check("Follow camera: LateUpdate keeps the camera at its offset from the final position",
            MathF.Abs(cameraOffset.X) < 0.01f && MathF.Abs(cameraOffset.Y - 4f) < 0.01f && MathF.Abs(cameraOffset.Z + 12f) < 0.01f,
            $"offset {cameraOffset.X:F3}, {cameraOffset.Y:F3}, {cameraOffset.Z:F3}");

        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: player restored to its edit position", player.Position.X == 0f && player.Position.Y == 10f && player.Position.Z == 0f);
        AssertNoFaults(report, "Camera and movement", faults);
        AssertLuaChecks(report, "Camera and movement", log,
        [
            "rotation_locked_reads", "mouse_lock", "camera_forward_set", "camera_forward_rejects_zero", "mouse_lock_rejects_non_boolean",
            "not_grounded_in_air", "grounded_bad_distance_errors", "grounded_on_floor", "axis_d_positive", "jump_from_ground",
            "late_update_gets_dt", "late_update_after_physics", "landing_impact_speed"
        ]);
    }

    public static void Communication(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Health.lua", """
            local M = {}
            hp = 100 -- a global: this script's own, never another script's
            function M.Damage(amount, attacker)
              hp = hp - amount
              Shared.score = (Shared.score or 0) + amount
              if hp <= 0 then
                local info = { name = self.Name, position = self.Transform.Position, killer = attacker,
                  loot = { "coin", "gem" }, stats = { hp = 0, nested = { deep = true } } }
                Events.Emit("Died", info)
                info.name = "mutated" -- after Emit: receivers never see this
                info.loot[1] = "mutated"
                self:Destroy()
              end
            end
            function M.GetHealth() return hp end
            function M.Ping(n) if n <= 0 then return "done" end return Shared.combat.Pong(n - 1) end
            function M.SharedRef() return Shared end
            function M.Self() return self end
            return M
            """);
        WriteScript(root, "NoExports.lua", "local x = 1\n");
        WriteScript(root, "Observer.lua", """
            function Start()
              Events.On("Died", function(info)
                print("CHECK observer_got_died " .. tostring(info.name == "Target" and info.loot[1] == "coin"))
                info.loot[1] = "changed by the observer" -- its own copy
              end)
            end
            """);
        WriteScript(root, "Listener.lua", """
            function Start()
              Events.On("Tick2", function() Shared.tick2 = (Shared.tick2 or 0) + 1 end)
            end
            """);
        WriteScript(root, "Combat.lua", ChkFunction + """
            local M = {}
            local frame, health, target, died, destroyedSeen, tickConnection, destroyedConnection = 0
            function M.Pong(n) return health.Ping(n) end
            Shared.combat = M

            function Start()
              target = Scene.Find("Target")
              health = target:GetScript("Health")
              chk("getscript_exports", type(health) == "table" and type(health.Damage) == "function")
              chk("getscript_with_extension", target:GetScript("Health.lua") == health)
              chk("getscript_with_folder", target:GetScript("Assets/Scripts/Health.lua") == health)
              local none, reason = target:GetScript("Nope")
              chk("getscript_missing_reason", none == nil and type(reason) == "string" and #reason > 0)
              local nothing, why = target:GetScript("NoExports")
              chk("getscript_no_exports_reason", nothing == nil and type(why) == "string" and why:find("exports nothing", 1, true) ~= nil)
              local _, plain = Scene.Find("Plain"):GetScript("Health")
              chk("getscript_object_without_scripts", type(plain) == "string")
              chk("getscript_bad_name_errors", not pcall(target.GetScript, target, 5))
              chk("own_globals_private", hp == nil)
              chk("mutual_calls", health.Ping(10) == "done")
              chk("shared_same_table", Shared == health.SharedRef())
              chk("wrappers_equal_across_scripts", health.Self() == target and not rawequal(health.Self(), target) and health.Self() ~= self)
              Shared.score = 0

              local ok1, e1 = pcall(Events.Emit, "Bad", { a = { f = function() end } })
              chk("emit_rejects_function_with_path", not ok1 and tostring(e1):find("argument #2.a.f", 1, true) ~= nil)
              local cycle = {}
              cycle.me = cycle
              local ok2, e2 = pcall(Events.Emit, "Bad", cycle)
              chk("emit_rejects_cycle", not ok2 and tostring(e2):find("itself", 1, true) ~= nil)
              local ok3, e3 = pcall(Events.Emit, "Bad", setmetatable({}, {}))
              chk("emit_rejects_metatable", not ok3 and tostring(e3):find("metatable", 1, true) ~= nil)
              local deep, current = {}, nil
              current = deep
              for _ = 1, 20 do current.n = {} current = current.n end
              local ok4, e4 = pcall(Events.Emit, "Bad", deep)
              chk("emit_rejects_too_deep", not ok4 and tostring(e4):find("levels deep", 1, true) ~= nil)
              local big = {}
              for i = 1, 10001 do big[i] = i end
              chk("emit_rejects_too_many_values", not pcall(Events.Emit, "Bad", big))

              Events.On("Died", function(info) died = info end)
              Events.On("Vec", function(v)
                chk("event_vector_value", v ~= nil and (v + Vector3.new(1, 1, 1)).x == 2 and v.z == 3)
              end)
              Events.Emit("Vec", Vector3.new(1, 2, 3))
              tickConnection = Events.On("Tick", function() Shared.ticks = (Shared.ticks or 0) + 1 end)
              destroyedConnection = target:OnDestroyed(function(object)
                destroyedSeen = object ~= nil and object.Exists == false and object.Name == "Target"
              end)
              chk("on_destroyed_connection", destroyedConnection.Connected == true)
              chk("on_destroyed_bad_argument_errors", not pcall(target.OnDestroyed, target, 5))
            end

            function Update(dt)
              frame = frame + 1
              if frame == 1 then
                health.Damage(30, self)
                health.Damage(30, self)
                chk("exported_call_changes_state", health.GetHealth() == 40)
                Events.Emit("Tick")
              elseif frame == 2 then
                chk("tick_delivered", Shared.ticks == 1)
                tickConnection:Disconnect()
                chk("disconnect_reports", tickConnection.Connected == false)
                Events.Emit("Tick")
                Events.Emit("Tick2")
              elseif frame == 3 then
                chk("disconnected_not_delivered", Shared.ticks == 1)
                chk("listener_got_tick2", Shared.tick2 == 1)
                Scene.Find("Temp"):Destroy()
                Events.Emit("Tick2")
                health.Damage(50, self) -- 40 - 50: dies, emits Died and destroys the target
                chk("shared_score", Shared.score == 110)
              elseif frame == 4 then
                chk("died_event_table", died ~= nil and died.name == "Target" and died.loot[1] == "coin" and died.loot[2] == "gem"
                  and died.stats.nested.deep == true and died.stats.hp == 0)
                chk("died_event_object_rewrapped", died ~= nil and died.killer == self)
                chk("died_event_vector_copied", died ~= nil and getmetatable(died.position) == "Vector3" and math.abs(died.position.x - 5) < 0.01)
                chk("on_destroyed_fired", destroyedSeen == true)
                chk("on_destroyed_fires_once", destroyedConnection.Connected == false)
                chk("getscript_after_destroy_fails", not pcall(target.GetScript, target, "Health"))
              elseif frame == 6 then
                chk("destroyed_listener_removed", Shared.tick2 == 1)
              end
            end
            return M
            """);

        var workspace = new Workspace();
        workspace.AddPart(MakePart("Shooter", new Vector3(-5, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Combat.lua"));
        workspace.AddPart(MakePart("Target", new Vector3(5, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Health.lua", "Assets/Scripts/NoExports.lua"));
        workspace.AddPart(MakePart("Watcher", new Vector3(0, 5, 5), new Vector3(2, 2, 2), true, "Assets/Scripts/Observer.lua"));
        workspace.AddPart(MakePart("Temp", new Vector3(0, 5, -5), new Vector3(2, 2, 2), true, "Assets/Scripts/Listener.lua"));
        workspace.AddPart(MakePart("Plain", new Vector3(0, 5, 10), new Vector3(2, 2, 2), true));

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, null, log, faults);
        Step(play, 8, faults);
        report.Check("Destroy from an exported function: Target and Temp left the scene", workspace.Parts.All(p => p.Name is not ("Target" or "Temp")));
        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: destroyed Target and Temp come back", workspace.Parts.Count == 5 && workspace.Parts.Any(p => p.Name == "Target") && workspace.Parts.Any(p => p.Name == "Temp"));

        // A new Play session starts with an empty Shared table (nothing leaks between sessions).
        WriteScript(root, "Fresh.lua", ChkFunction + "function Start() chk(\"shared_fresh_each_session\", next(Shared) == nil) end\n");
        var fresh = new Workspace();
        fresh.AddPart(MakePart("Fresh", Vector3.Zero, new Vector3(1, 1, 1), true, "Assets/Scripts/Fresh.lua"));
        var freshLog = new OutputLog();
        var freshPlay = StartPlay(root, fresh, null, freshLog, faults);
        Step(freshPlay, 1, faults);
        freshPlay.Stop(fresh);
        freshPlay.Dispose();

        AssertNoFaults(report, "Script communication", faults);
        AssertLuaChecks(report, "Script communication", log,
        [
            "getscript_exports", "getscript_with_extension", "getscript_with_folder", "getscript_missing_reason", "getscript_no_exports_reason",
            "getscript_object_without_scripts", "getscript_bad_name_errors", "own_globals_private", "mutual_calls", "shared_same_table",
            "wrappers_equal_across_scripts",
            "emit_rejects_function_with_path", "emit_rejects_cycle", "emit_rejects_metatable", "emit_rejects_too_deep",
            "emit_rejects_too_many_values", "event_vector_value", "on_destroyed_connection", "on_destroyed_bad_argument_errors",
            "exported_call_changes_state", "tick_delivered", "disconnect_reports", "disconnected_not_delivered", "listener_got_tick2",
            "shared_score", "observer_got_died", "died_event_table", "died_event_object_rewrapped", "died_event_vector_copied",
            "on_destroyed_fired", "on_destroyed_fires_once", "getscript_after_destroy_fails", "destroyed_listener_removed"
        ]);
        AssertLuaChecks(report, "Script communication", freshLog, ["shared_fresh_each_session"]);
    }

    public static void PhysicsAndProjectiles(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Gun.lua", ChkFunction + """
            local frame = 0
            function Start()
              chk("gravity_default_down", Physics.Gravity.y < 0)
              local glass, wall, enemy = Scene.Find("Glass"), Scene.Find("Wall"), Scene.Find("Enemy")
              glass:AddTag("Glass")
              enemy:AddTag("Enemy")
              local origin, dir = Vector3.new(0, 5, 0), Vector3.new(0, 0, 1)
              local first = Physics.Raycast(origin, dir, 100, self)
              print("INFO default raycast first hit: " .. tostring(first and first.Object and first.Object.Name))
              local h1 = Physics.Raycast(origin, dir, 100, { Ignore = self, IgnoreTriggers = true })
              chk("filter_ignore_triggers", h1 ~= nil and h1.Object == glass)
              local h2 = Physics.Raycast(origin, dir, 100, { Ignore = { self }, IgnoreTriggers = true, IgnoreTags = "Glass" })
              chk("filter_ignore_tags", h2 ~= nil and h2.Object == wall)
              local h3 = Physics.Raycast(origin, dir, 100, { self, IgnoreTriggers = true, OnlyTags = { "Enemy" } })
              chk("filter_only_tags", h3 ~= nil and h3.Object == enemy)
              local h4 = Physics.Raycast(origin, dir, 100, { Ignore = { self, glass, wall, enemy }, IgnoreTriggers = true })
              chk("filter_ignore_list", h4 == nil)
              local all = Physics.RaycastAll(origin, dir, 100, { Ignore = self, IgnoreTriggers = true })
              chk("raycastall_filter", #all == 3 and all[1].Object == glass and all[3].Object == enemy)
              local zoneHit = Physics.RaycastAll(origin, dir, 100, self)
              chk("raycastall_object_filter_still_works", #zoneHit >= 3)
              local sweep = Physics.SphereCast(origin, dir, 0.5, 100, { Ignore = self, IgnoreTriggers = true, IgnoreTags = "Glass" })
              chk("spherecast_filter", sweep ~= nil and sweep.Object == wall)
              local inside = Physics.OverlapSphere(0, 5, 10, 1, { OnlyTags = "Glass" })
              chk("overlap_only_tags", #inside == 1 and inside[1] == glass)
              local boxed = Physics.OverlapBox(0, 5, 10, 2, 2, 2, { IgnoreTags = "Glass" })
              chk("overlapbox_filter_after_size", #boxed == 0)
              local ok, err = pcall(Physics.Raycast, origin, dir, 100, { Bogus = true })
              chk("filter_unknown_option_errors", not ok and tostring(err):find("is not an option", 1, true) ~= nil)
              chk("filter_bad_ignore_errors", not pcall(Physics.Raycast, origin, dir, 100, { Ignore = 5 }))
              chk("filter_bad_flag_errors", not pcall(Physics.Raycast, origin, dir, 100, { IgnoreTriggers = "yes" }))
              chk("filter_environment_option", pcall(Physics.Raycast, origin, dir, 100, { IgnoreEnvironment = true }))
              Physics.Gravity = Vector3.new(0, -20, 0)
              chk("gravity_settable", math.abs(Physics.Gravity.y + 20) < 1e-4)
              chk("gravity_rejects_non_vector", not pcall(function() Physics.Gravity = 5 end))
              chk("physics_functions_read_only", not pcall(function() Physics.Raycast = nil end))
            end
            function Update(dt)
              frame = frame + 1
              if frame == 1 then
                local bullet = Scene.Create("Sphere", { Name = "Bullet", Anchored = false, GravityEnabled = false,
                  Position = Vector3.new(0, 5, 14), Size = Vector3.new(0.4, 0.4, 0.4), Mass = 0.05,
                  LinearVelocity = Vector3.new(0, 0, 120), Tags = { "Bullet" }, Attributes = { Owner = self.Id } })
                chk("projectile_created_with_velocity", math.abs(bullet.LinearVelocity.z - 120) < 0.01)
                Scene.Create("Sphere", { Name = "FastBullet", Anchored = false, GravityEnabled = false,
                  Position = Vector3.new(10, 5, 14), Size = Vector3.new(0.4, 0.4, 0.4), Mass = 0.05,
                  LinearVelocity = Vector3.new(0, 0, 600), Tags = { "Bullet" }, Attributes = { Owner = self.Id } })
                chk("create_rejects_bad_velocity", not pcall(Scene.Create, "Sphere", { LinearVelocity = 5 }))
              end
            end
            """);
        WriteScript(root, "Wall.lua", """
            function OnCollisionEnter(other, contact)
              if other ~= nil and other:HasTag("Bullet") then
                local owner = Scene.FindById(other:GetAttribute("Owner"))
                print("CHECK hit_" .. self.Name .. "_by_" .. other.Name .. " " .. tostring(owner ~= nil and owner.Name == "Gun"))
                other:Destroy()
              end
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Gun", new Vector3(0, 5, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Gun.lua"));
        var zone = MakePart("Zone", new Vector3(0, 5, 5), new Vector3(4, 4, 1), true);
        zone.IsTrigger = true;
        workspace.AddPart(zone);
        workspace.AddPart(MakePart("Glass", new Vector3(0, 5, 10), new Vector3(4, 4, 0.5f), true));
        workspace.AddPart(MakePart("Wall", new Vector3(0, 5, 20), new Vector3(4, 4, 1), true, "Assets/Scripts/Wall.lua"));
        workspace.AddPart(MakePart("Enemy", new Vector3(0, 5, 30), new Vector3(2, 2, 2), true));
        workspace.AddPart(MakePart("ThinWall", new Vector3(10, 5, 20), new Vector3(4, 4, 1), true, "Assets/Scripts/Wall.lua"));

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, null, log, faults);
        Step(play, 30, faults);
        var gravity = play.Physics!.Gravity;
        report.Check("Physics.Gravity = v reaches the physics world", MathF.Abs(gravity.Y + 20f) < 1e-4f, $"{gravity.Y}");
        var fast = workspace.Parts.FirstOrDefault(p => p.Name == "FastBullet");
        var fastHit = LuaReports(log).ContainsKey("hit_ThinWall_by_FastBullet");
        TestReport.Info(fastHit
            ? "Fast projectile (600 studs/s, 10 studs per frame) hit the 1-stud wall"
            : $"Fast projectile (600 studs/s) passed through the 1-stud wall (tunnelling); now at z = {fast?.Position.Z:F1}");
        play.Stop(workspace);
        play.Dispose();
        report.Check("Projectiles: created bullets are gone after Stop", workspace.Parts.Count == 6 && workspace.Parts.All(p => !p.Name.Contains("Bullet", StringComparison.Ordinal)));
        var again = StartPlay(root, new Workspace(), null, new OutputLog(), faults);
        var defaultGravity = again.Physics!.Gravity.Y;
        again.Stop(new Workspace());
        again.Dispose();
        report.Check("Physics.Gravity: the next Play session has the project's gravity again", defaultGravity < 0f && MathF.Abs(defaultGravity + 20f) > 0.01f, $"{defaultGravity}");
        AssertNoFaults(report, "Physics and projectiles", faults);
        AssertLuaChecks(report, "Physics and projectiles", log,
        [
            "gravity_default_down", "filter_ignore_triggers", "filter_ignore_tags", "filter_only_tags", "filter_ignore_list",
            "raycastall_filter", "raycastall_object_filter_still_works", "spherecast_filter", "overlap_only_tags",
            "overlapbox_filter_after_size", "filter_unknown_option_errors", "filter_bad_ignore_errors", "filter_bad_flag_errors",
            "filter_environment_option", "gravity_settable", "gravity_rejects_non_vector", "physics_functions_read_only",
            "projectile_created_with_velocity", "create_rejects_bad_velocity", "hit_Wall_by_Bullet"
        ]);
    }

    public static void Attachment(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Attach.lua", ChkFunction + """
            local tool
            local frame = 0
            function Start()
              tool = Scene.Find("Tool")
              chk("parent_self_rejected", not pcall(self.SetParent, self, self))
              tool:SetParent(self, Vector3.new(1, 0, 0))
              chk("parent_reads", tool.Parent == self and self.Parent == nil)
              local kids = self:GetChildren()
              chk("children_list", #kids == 1 and kids[1] == tool)
              chk("attached_is_anchored", tool.Anchored == true)
              chk("parent_loop_rejected", not pcall(self.SetParent, self, tool))
              chk("parent_bad_offset_rejected", not pcall(tool.SetParent, tool, self, 5))
              chk("parent_bad_target_rejected", not pcall(tool.SetParent, tool, {}))
            end
            function Update(dt)
              frame = frame + 1
              if frame == 2 then
                self.Transform.Rotation = Vector3.new(0, 90, 0)
              end
            end
            function LateUpdate()
              if frame == 3 then
                local hp, tp = self.Transform.Position, tool.Transform.Position
                chk("follows_turned_parent", math.abs(Vector3.distance(hp, tp) - 1) < 0.02 and math.abs(tp.x - hp.x) < 0.05)
                chk("follows_parent_rotation", math.abs(tool.Transform.Rotation.y - 90) < 0.5)
              elseif frame == 30 then
                chk("child_never_pushes_parent", math.abs(self.Transform.Position.x) < 0.05)
                tool.Parent = nil
                chk("unparent_restores", tool.Anchored == false and tool.Parent == nil and #self:GetChildren() == 0)
              elseif frame == 40 then
                local gem = Scene.Find("Gem")
                chk("destroyed_parent_lets_child_go", gem ~= nil and gem.Exists and gem.Parent == nil and gem.Anchored == false)
              end
            end
            """);
        WriteScript(root, "Carry.lua", ChkFunction + """
            local gem
            local frame = 0
            function Start()
              gem = Scene.Find("Gem")
              gem.Parent = self
            end
            function Update(dt)
              frame = frame + 1
              if frame <= 10 then
                self.Transform.Position = self.Transform.Position + Vector3.new(0.5, 0, 0)
              end
            end
            function LateUpdate()
              if frame == 10 then
                local p = gem.Transform.Position
                chk("keeps_relative_pose", math.abs(p.x - 25) < 0.01 and math.abs(p.y - 8) < 0.01)
              elseif frame == 11 then
                self:Destroy()
              end
            end
            """);
        var floor = MakePart("Floor", new Vector3(0, 0, 0), new Vector3(100, 2, 100), true);
        var holder = MakePart("Holder", new Vector3(0, 2, 0), new Vector3(2, 2, 2), false, "Assets/Scripts/Attach.lua");
        var tool = MakePart("Tool", new Vector3(5, 5, 0), new Vector3(1, 1, 3), false);
        var carrier = MakePart("Carrier", new Vector3(20, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Carry.lua");
        var gem = MakePart("Gem", new Vector3(20, 8, 0), new Vector3(1, 1, 1), false);
        var workspace = new Workspace();
        foreach (var part in new[] { floor, holder, tool, carrier, gem })
        {
            workspace.AddPart(part);
        }

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, null, log, faults);
        Step(play, 60, faults);
        var gemFell = gem.Position.Y < 8f - 1f;
        report.Check("Attachment: a child let go by a destroyed parent falls again (physics restored)", gemFell, $"Gem Y = {gem.Position.Y:F2}");
        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: attached objects back at their edit poses, nothing saved about the attachment",
            tool.Position.X == 5f && tool.Position.Y == 5f && tool.Rotation.Y == 0f && !tool.Anchored
            && gem.Position.X == 20f && gem.Position.Y == 8f && !gem.Anchored && workspace.Parts.Count == 5 && workspace.Parts.Contains(carrier));
        AssertNoFaults(report, "Attachment", faults);
        AssertLuaChecks(report, "Attachment", log,
        [
            "parent_self_rejected", "parent_reads", "children_list", "attached_is_anchored", "parent_loop_rejected", "parent_bad_offset_rejected",
            "parent_bad_target_rejected", "follows_turned_parent", "follows_parent_rotation", "child_never_pushes_parent", "unparent_restores",
            "keeps_relative_pose", "destroyed_parent_lets_child_go"
        ]);
    }
}
