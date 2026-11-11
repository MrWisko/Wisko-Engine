using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

// V5 scene primitives: trigger connections, Teleport (and Wisko.Teleport), bones (and Wisko.Equipment), Navigation,
// Perception.
internal static partial class GameplayV5Checks
{
    internal static Part ModelPart(string name, ModelAsset asset, Vector3 origin, float scale, ModelAnimationSettings? settings = null, params string[] scripts)
    {
        var part = new Part(name, Vector3.Zero, new Vector3(1f, 1f, 1f), ModelReference.DefaultTint, anchored: true)
        {
            Model = new ModelReference(asset.AssetPath, asset),
            Animation = settings
        };
        ModelTransform.SetInstanceTransform(part, origin, Vector3.Zero, new Vector3(scale, scale, scale));
        foreach (var script in scripts)
        {
            ScriptReference.TryCreate($"Assets/Scripts/{script}.lua", out var reference);
            part.Scripts.Add(reference!);
        }

        return part;
    }

    // ---- Triggers and teleporting ----------------------------------------------------------------------------------

    private const string TriggerProbe = """
        local entered, exited, others = 0, 0, {}
        function Start()
          local zone = Scene.Find("Zone")
          local c = zone:OnTriggerEntered(function(other, trigger)
            entered = entered + 1
            others[#others + 1] = other and other.Name or "environment"
            chk("trigger_arg_is_zone", trigger == zone)
          end)
          zone:OnTriggerExited(function(other) exited = exited + 1 end)
          chk("connection_returned", c.Connected)
          chk("needs_function", not pcall(function() zone:OnTriggerEntered(3) end))
          chk("needs_colon", not pcall(function() zone.OnTriggerEntered(function() end) end))
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 100 then
            chk("box_entered_zone", entered >= 1 and others[1] == "Box")
            -- Teleport the box out (and give it a sideways speed it must keep).
            local box = Scene.Find("Box")
            box.LinearVelocity = Vector3.new(5, 0, 0)
            box:Teleport(Vector3.new(30, 2, 0), { KeepVelocity = true })
          elseif frame == 102 then
            local box = Scene.Find("Box")
            chk("teleported_position", Vector3.distance(box.Transform.Position, Vector3.new(30, 2, 0)) < 0.5)
            chk("velocity_kept", math.abs(box.LinearVelocity.x - 5) < 0.6)
            chk("left_zone", exited >= 1)
            box:Teleport(Vector3.new(30, 2, 10), Vector3.new(0, 90, 0))
          elseif frame == 104 then
            local box = Scene.Find("Box")
            local v = box.LinearVelocity
            chk("velocity_cleared", math.abs(v.x) < 0.3 and math.abs(v.z) < 0.3)
            chk("rotation_set", math.abs(box.Transform.Rotation.y - 90) < 0.5)
            chk("teleport_bad_position", not pcall(function() box:Teleport("here") end))
            chk("teleport_bad_option", not pcall(function() box:Teleport(Vector3.new(0, 5, 0), { Keep = true }) end))
            local tool = Scene.Find("Tool")
            tool:SetParent(box, Vector3.new(0, 2, 0))
            box:Teleport(Vector3.new(-30, 3, 0))
            chk("child_arrives_with_parent", Vector3.distance(tool.Transform.Position, Vector3.new(-30, 5, 0)) < 0.2)
            chk("child_cannot_teleport_alone", not pcall(function() tool:Teleport(Vector3.new(0, 1, 0)) end))
          end
        end
        """;

    private const string PadProbe = """
        local Teleport = require("Wisko.Teleport")
        local a, b, arrivals = nil, nil, 0
        local checkpoints
        function Start()
          a = Teleport.Pad(Scene.Find("PadA"), { Offset = Vector3.new(0, 2.5, 0) })
          b = Teleport.Pad(Scene.Find("PadB"), { Offset = Vector3.new(0, 2.5, 0) })
          Teleport.Link(a, b)
          a:OnTeleported(function(object) arrivals = arrivals + 1 end)
          b:OnTeleported(function(object) arrivals = arrivals + 100 end)
          checkpoints = Teleport.Checkpoints({ Spawn = Vector3.new(0, 3, -40) })
          checkpoints:Watch(Scene.Find("Flag"))
        end
        local frame = 0
        function Update()
          frame = frame + 1
          local walker = Scene.Find("Walker")
          if frame == 2 then
            walker:Teleport(Scene.Find("PadA").Transform.Position + Vector3.new(0, 1.5, 0))
          elseif frame == 60 then
            chk("sent_to_b", Vector3.distance(walker.Transform.Position, Scene.Find("PadB").Transform.Position) < 4)
            chk("one_teleport", arrivals == 1)
          elseif frame == 200 then
            -- Standing on B the whole time: never sent back.
            chk("no_ping_pong", arrivals == 1 and Vector3.distance(walker.Transform.Position, Scene.Find("PadB").Transform.Position) < 4)
            walker:Teleport(Vector3.new(0, 3, 20))   -- off the pad
          elseif frame == 240 then
            walker:Teleport(Scene.Find("PadB").Transform.Position + Vector3.new(0, 1.5, 0))
          elseif frame == 300 then
            chk("b_sends_back_after_leaving", arrivals == 101 and Vector3.distance(walker.Transform.Position, Scene.Find("PadA").Transform.Position) < 4)
            walker:Teleport(Scene.Find("Flag").Transform.Position + Vector3.new(0, 1, 0))
          elseif frame == 340 then
            local respawn, name = checkpoints:GetRespawn(walker)
            chk("checkpoint_activated", name == "Flag")
            walker:Teleport(Vector3.new(50, 3, 50))
            checkpoints:Respawn(walker)
            chk("respawned", Vector3.distance(walker.Transform.Position, respawn) < 0.5)
          end
        end
        """;

    public static void TriggersAndTeleport(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeTrigger", TriggerProbe), ("ProbePad", PadProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeTrigger");
        var zone = Add(workspace, "Zone", new Vector3(0, 2, 0), new Vector3(6, 4, 6), true);
        zone.IsTrigger = true;
        var box = Add(workspace, "Box", new Vector3(0, 8, 0), new Vector3(1, 1, 1), false);
        Add(workspace, "Tool", new Vector3(10, 0.5f, 10), new Vector3(1, 1, 1), true);
        var s = Begin(root, workspace);
        Run(s, 110);
        AssertNoFaults(report, "V5 triggers/teleport", s.Faults);
        AssertLuaChecks(report, "V5 triggers/teleport", s.Log, ["connection_returned", "needs_function", "needs_colon", "trigger_arg_is_zone", "box_entered_zone",
            "teleported_position", "velocity_kept", "left_zone", "velocity_cleared", "rotation_set", "teleport_bad_position", "teleport_bad_option",
            "child_arrives_with_parent", "child_cannot_teleport_alone"]);
        End(s, workspace);
        report.Check("V5 Teleport: Stop restores the teleported Part", Dist(box.Position, new Vector3(0, 8, 0)) < 1e-4f);

        workspace = new Workspace();
        AddProbe(workspace, "ProbePad");
        foreach (var (name, x) in new[] { ("PadA", -20f), ("PadB", 20f) })
        {
            var pad = Add(workspace, name, new Vector3(x, 1, 0), new Vector3(4, 2, 4), true);
            pad.IsTrigger = true;
        }

        var flag = Add(workspace, "Flag", new Vector3(0, 1, 30), new Vector3(3, 2, 3), true);
        flag.IsTrigger = true;
        var walker = Add(workspace, "Walker", new Vector3(0, 2, -20), new Vector3(1.5f, 2, 1.5f), false);
        s = Begin(root, workspace);
        Run(s, 345);
        AssertNoFaults(report, "V5 Wisko.Teleport", s.Faults);
        AssertLuaChecks(report, "V5 Wisko.Teleport", s.Log, ["sent_to_b", "one_teleport", "no_ping_pong", "b_sends_back_after_leaving", "checkpoint_activated", "respawned"]);
        End(s, workspace);
    }

    // ---- Bones and equipment -------------------------------------------------------------------------------------

    private const string BoneProbe = """
        local Equipment = require("Wisko.Equipment")
        local robot, tool, gear, hand
        local samples, maxError = {}, 0
        function Start()
          robot = Scene.Find("Robot")
          tool = Scene.Find("Tool")
          local bones = robot:GetBones()
          chk("model_has_bones", #bones > 10)
          for _, name in ipairs(bones) do
            if name == "Palm2.R" then hand = name end
          end
          print("BONE hand " .. tostring(hand) .. " of " .. #bones .. ": " .. table.concat(bones, ","))
          chk("has_bone", robot:HasBone(hand) and not robot:HasBone("NoSuchBone"))
          local p, r = robot:GetBoneTransform(hand)
          chk("bone_transform", p ~= nil and r ~= nil)
          chk("unknown_bone_answer", select(1, robot:GetBoneTransform("NoSuchBone")) == nil)
          chk("part_has_no_bones", #tool:GetBones() == 0)
          chk("setparent_bad_bone", not pcall(function() tool:SetParent(robot, nil, nil, "NoSuchBone") end))
          gear = Equipment.new(robot, { Slots = { RightHand = { Bone = hand, Offset = Vector3.new(0, 0, 0) } } })
          chk("equip", gear:Equip("RightHand", tool) == true and gear:Get("RightHand") == tool)
          chk("equipped_attributes", tool:GetAttribute("EquippedSlot") == "RightHand" and tool:GetAttribute("EquippedBy") == robot.Id)
          robot:PlayAnimation("Wave")
        end
        local frame = 0
        function LateUpdate()
          frame = frame + 1
          if gear:Get("RightHand") == tool then
            local p = robot:GetBoneTransform(hand)
            local error = Vector3.distance(p, tool.Transform.Position)
            maxError = math.max(maxError, error)
            samples[#samples + 1] = tool.Transform.Position + Vector3.new(0, 0, 0)
          end
          if frame == 90 then
            chk("tool_follows_bone_exactly", maxError < 1e-3)
            local travelled = 0
            for i = 2, #samples do travelled = travelled + Vector3.distance(samples[i], samples[i - 1]) end
            print("BONE travelled " .. travelled .. " max error " .. maxError)
            chk("tool_moves_with_animation", travelled > 1)
            local item = gear:Unequip("RightHand")
            chk("unequip", item == tool and gear:Get("RightHand") == nil and tool.Parent == nil and tool:GetAttribute("EquippedSlot") == nil)
            chk("unequip_restores_anchored", tool.Anchored == false)
          elseif frame == 100 then
            gear:Equip("RightHand", tool)
            tool:Destroy()
          elseif frame == 102 then
            chk("destroyed_item_leaves_slot", gear:Get("RightHand") == nil)
          end
        end
        """;

    public static void BonesAndEquipment(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeBones", BoneProbe));
        var robotAsset = AnimationChecks.LoadAsset("RobotExpressive", "RobotExpressive.glb");
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeBones");
        workspace.AddPart(ModelPart("Robot", robotAsset, new Vector3(0, 0, 0), 1f));
        Add(workspace, "Tool", new Vector3(5, 0.5f, 5), new Vector3(0.3f, 1.2f, 0.3f), false);
        var s = Begin(root, workspace);
        Run(s, 105);
        AssertNoFaults(report, "V5 bones/equipment", s.Faults);
        AssertLuaChecks(report, "V5 bones/equipment", s.Log, ["model_has_bones", "has_bone", "bone_transform", "unknown_bone_answer", "part_has_no_bones",
            "setparent_bad_bone", "equip", "equipped_attributes", "tool_follows_bone_exactly", "tool_moves_with_animation", "unequip", "unequip_restores_anchored",
            "destroyed_item_leaves_slot"]);
        TestReport.Info(string.Join(" | ", Lines(s.Log, "[Lua] Assets/Scripts/ProbeBones.lua (ProbeBones): BONE").Select(l => l[(l.IndexOf("BONE", StringComparison.Ordinal) + 5)..])));
        End(s, workspace);
    }

    // ---- Navigation --------------------------------------------------------------------------------------------

    private const string NavProbe = """
        local info
        function Start()
          info = Navigation.Build({ Min = Vector3.new(-40, -2, -40), Max = Vector3.new(40, 10, 40), CellSize = 1, AgentRadius = 0.8 })
          print("NAV " .. info.Width .. "x" .. info.Depth .. " walkable " .. info.Walkable .. " regions " .. info.Regions .. " ms " .. string.format("%.1f", info.Milliseconds))
          -- Around the long wall (x -15..15 at z = 0).
          local path, why = Navigation.FindPath(Vector3.new(0, 0, -10), Vector3.new(0, 0, 10))
          chk("path_found", path ~= nil and path.Complete)
          if path == nil then print("NAV why " .. tostring(why)) return end
          chk("path_goes_round", path.Length > 30)
          local insideWall = false
          for _, p in ipairs(path.Points) do
            if math.abs(p.x) < 15.5 and math.abs(p.z) < 1.3 then insideWall = true end
          end
          chk("no_point_in_wall", not insideWall)
          local clear = true
          for i = 2, #path.Points do
            if not Navigation.Raycast(path.Points[i - 1], path.Points[i]) then clear = false end
          end
          chk("segments_walkable", clear)
          chk("ends_exact", Vector3.distance(path.Points[1], Vector3.new(0, 0, -10)) < 0.01 and Vector3.distance(path.Points[#path.Points], Vector3.new(0, 0, 10)) < 0.01)
          Navigation.DrawPath(path, "#33FF66", 5)
          -- The walled-in pen at (30, 30) cannot be reached.
          local none, reason = Navigation.FindPath(Vector3.new(0, 0, -10), Vector3.new(30, 0, 30))
          chk("unreachable_nil_reason", none == nil and string.find(reason, "cannot be reached", 1, true) ~= nil)
          chk("is_reachable", Navigation.IsReachable(Vector3.new(0, 0, -10), Vector3.new(20, 0, 20)) and not Navigation.IsReachable(Vector3.new(0, 0, -10), Vector3.new(30, 0, 30)))
          local partial = Navigation.FindPath(Vector3.new(0, 0, -10), Vector3.new(30, 0, 30), { AllowPartial = true })
          chk("partial_path", partial ~= nil and partial.Complete == false)
          chk("walkable_query", Navigation.IsWalkable(Vector3.new(0, 0, -10)) and not Navigation.IsWalkable(Vector3.new(0, 0, 0)))
          chk("nearest_walkable", Navigation.GetNearestWalkable(Vector3.new(0, 0, 0), 5) ~= nil)
          chk("bad_build_option", not pcall(Navigation.Build, { Cell = 1 }))
          chk("bad_findpath_arg", not pcall(Navigation.FindPath, "a", Vector3.new(0, 0, 0)))
          chk("draw_grid", Navigation.DrawGrid(Vector3.new(0, 0, -10), 3) > 0)
          -- A door in the gap at x = 20 (between the wall's end and the right fence): closing it makes the way longer.
          local open = Navigation.FindPath(Vector3.new(25, 0, -10), Vector3.new(25, 0, 10))
          local door = Scene.Create("Block", { Name = "Door", Position = Vector3.new(22.5, 2, 0), Size = Vector3.new(15, 4, 1) })
          Navigation.Update(Vector3.new(22.5, 2, 0), Vector3.new(17, 6, 3))
          local closed = Navigation.FindPath(Vector3.new(25, 0, -10), Vector3.new(25, 0, 10))
          chk("dynamic_rebake_changes_path", open ~= nil and closed ~= nil and closed.Length > open.Length + 10)
          door:Destroy()
          Navigation.Update(Vector3.new(22.5, 2, 0), Vector3.new(17, 6, 3))
          Navigation.SetBlocked(Vector3.new(22.5, 0, 0), Vector3.new(15, 4, 3), true)
          local blocked = Navigation.FindPath(Vector3.new(25, 0, -10), Vector3.new(25, 0, 10))
          chk("set_blocked", blocked ~= nil and blocked.Length > open.Length + 10)
          Navigation.SetBlocked(Vector3.new(22.5, 0, 0), Vector3.new(15, 4, 3), false)
        end
        """;

    private const string AgentProbe = """
        local patrolAgent, followAgent, arrivals, waypoints = nil, nil, 0, {}
        local target, targetStart
        function Start()
          Navigation.Build({ Min = Vector3.new(-40, -2, -40), Max = Vector3.new(40, 10, 40) })
          local guard = Scene.Find("Guard")
          guard.RotationLocked = true
          patrolAgent = Navigation.CreateAgent(guard, { Speed = 10 })
          patrolAgent:Patrol({ Vector3.new(-10, 0, -10), Vector3.new(10, 0, -10), Vector3.new(0, 0, 20) }, { Loop = true })
          patrolAgent:OnArrived(function() arrivals = arrivals + 1 end)
          patrolAgent:OnWaypoint(function(index) waypoints[#waypoints + 1] = index end)
          local chaser = Scene.Find("Chaser")
          chaser.RotationLocked = true
          followAgent = Navigation.CreateAgent(chaser, { Speed = 12 })
          target = Scene.Find("Target")
          targetStart = target.Transform.Position + Vector3.new(0, 0, 0)   -- a copy (Transform.Position is a live view)
          chk("follow_ok", followAgent:Follow(target, 3))
          chk("agent_bad_option", not pcall(Navigation.CreateAgent, guard, { Sped = 3 }))
          chk("agent_bad_move", not pcall(Navigation.CreateAgent, guard, { Move = "Teleport" }))
        end
        local frame, maxGap, chaserStart = 0, 0, nil
        function Update(dt)
          frame = frame + 1
          if patrolAgent.Exists then patrolAgent:Update(dt) end
          local status = followAgent:Update(dt)
          -- The target walks along +X (a moving destination).
          target.Transform.Position = targetStart + Vector3.new(math.min(frame, 600) * 0.05, 0, 0)
          local chaser = Scene.Find("Chaser")
          chaserStart = chaserStart or (chaser.Transform.Position + Vector3.new(0, 0, 0))
          if frame > 300 then
            local c, t = chaser.Transform.Position, target.Transform.Position
            maxGap = math.max(maxGap, math.sqrt((c.x - t.x) ^ 2 + (c.z - t.z) ^ 2))
          end
          if frame == 900 then
            chk("patrol_arrives_repeatedly", arrivals >= 3)
            chk("patrol_waypoints", #waypoints > 0)
            chk("follower_keeps_up", maxGap < 7)
            chk("follower_travelled", Vector3.distance(chaser.Transform.Position, chaserStart) > 20)
            chk("status_strings", status == "Moving" or status == "Arrived")
            print("AGENT arrivals " .. arrivals .. " max gap " .. string.format("%.2f", maxGap) .. " travelled " .. string.format("%.1f", Vector3.distance(chaser.Transform.Position, chaserStart)))
            Scene.Find("Guard"):Destroy()
          elseif frame == 902 then
            chk("agent_gone_with_object", not patrolAgent.Exists and not pcall(function() patrolAgent:Update(0.016) end))
          end
        end
        """;

    // Terrain rules: a block whose top is walkable still keeps agents AgentRadius away from its sides (its faces on cell
    // boundaries, so no cell lacks a surface), steps up to MaxClimb are climbed and higher ones are not, gentle ramps are
    // walkable and steep ones are not, an agent walking into a wall the grid does not know gives up as stuck, and
    // Move = "None" only reports.
    private const string NavTerrainProbe = """
        local walker, ghost, stuckAgent, ghostAgent
        local failure, frame = nil, 0
        local ghostStart
        -- Distance in X/Z from a point to the wall block (x -10..10, z 9..11).
        local function wallDistance(p)
          local dx = math.max(math.abs(p.x) - 10, 0)
          local dz = math.max(math.abs(p.z - 10) - 1, 0)
          return math.sqrt(dx * dx + dz * dz)
        end
        function Start()
          local info = Navigation.Build({ Min = Vector3.new(-30, -2, -30), Max = Vector3.new(30, 12, 30), CellSize = 1, AgentRadius = 1 })
          print("TERRAIN " .. info.Width .. "x" .. info.Depth .. " walkable " .. info.Walkable .. " regions " .. info.Regions)
          chk("wall_side_not_walkable", not Navigation.IsWalkable(Vector3.new(0, 0, 8.5)) and Navigation.IsWalkable(Vector3.new(0, 0, 6.5)))
          local path = Navigation.FindPath(Vector3.new(-14, 0, 5), Vector3.new(14, 0, 15))
          local closest = math.huge
          if path ~= nil then
            for i = 2, #path.Points do
              local a, b = path.Points[i - 1], path.Points[i]
              for s = 0, 20 do
                closest = math.min(closest, wallDistance(Vector3.lerp(a, b, s / 20)))
              end
            end
          end
          print("TERRAIN closest approach to the wall " .. closest)
          chk("path_keeps_clear_of_wall", path ~= nil and path.Complete and closest > 0.75)
          chk("low_step_climbed", Navigation.IsReachable(Vector3.new(0, 0, -5), Vector3.new(14, 0.8, -5)))
          local _, stepHeight = Navigation.IsWalkable(Vector3.new(14, 0.8, -5))
          chk("low_step_height", stepHeight ~= nil and math.abs(stepHeight - 0.8) < 0.05)
          chk("high_step_not_climbed", Navigation.IsWalkable(Vector3.new(-14, 2, -5))
            and not Navigation.IsReachable(Vector3.new(0, 0, -5), Vector3.new(-14, 2, -5)))
          local gentle, gentleHeight = Navigation.IsWalkable(Vector3.new(15, 2.3, 22))
          print("TERRAIN gentle ramp " .. tostring(gentle) .. " at " .. tostring(gentleHeight))
          chk("gentle_ramp_walkable", gentle and math.abs(gentleHeight - 2.32) < 0.2
            and Navigation.IsReachable(Vector3.new(5, 0, 22), Vector3.new(19, 3.8, 22)))
          chk("steep_ramp_not_walkable", not Navigation.IsWalkable(Vector3.new(-15, 3, 22)))
          -- A wall the grid does not know about (made after the bake, no Navigation.Update): the walker cannot pass.
          Scene.Create("Block", { Name = "Surprise", Position = Vector3.new(0, 3, -20), Size = Vector3.new(70, 6, 1) })
          walker = Scene.Find("Walker")
          walker.RotationLocked = true
          stuckAgent = Navigation.CreateAgent(walker, { Speed = 6 })
          stuckAgent:OnFailed(function(reason) failure = reason end)
          chk("destination_set", stuckAgent:SetDestination(Vector3.new(0, 0, -12)))
          ghost = Scene.Find("Ghost")
          ghostStart = ghost.Transform.Position + Vector3.new(0, 0, 0)
          ghostAgent = Navigation.CreateAgent(ghost, { Move = "None" })
          ghostAgent:SetDestination(Vector3.new(10, 0, -10))
        end
        function Update(dt)
          frame = frame + 1
          stuckAgent:Update(dt)
          local status, velocity = ghostAgent:Update(dt)
          if frame == 30 then
            chk("move_none_reports_only", status == "Moving" and Vector3.magnitude(velocity) > 1
              and Vector3.distance(ghost.Transform.Position, ghostStart) < 1e-6)
          end
          if frame == 800 then
            print("TERRAIN stuck agent: " .. tostring(failure) .. ", status " .. stuckAgent.Status)
            chk("stuck_fails", failure ~= nil and string.find(failure, "stuck", 1, true) ~= nil and stuckAgent.Status == "Failed"
              and stuckAgent.FailureReason == failure)
          end
        end
        """;

    private static void NavigationTerrainScene(Workspace workspace)
    {
        // Faces on whole studs, so with CellSize 1 from -30 they lie on cell boundaries.
        Add(workspace, "TallWall", new Vector3(0, 4, 10), new Vector3(20, 8, 2), true);
        Add(workspace, "LowStep", new Vector3(14, 0.4f, -5), new Vector3(6, 0.8f, 6), true);
        Add(workspace, "HighStep", new Vector3(-14, 1, -5), new Vector3(6, 2, 6), true);
        var gentle = Add(workspace, "GentleRamp", new Vector3(15, 2.05f, 22), new Vector3(12, 0.5f, 8), true);
        gentle.Rotation = new Vector3(0, 0, 20);
        var steep = Add(workspace, "SteepRamp", new Vector3(-15, 2.5f, 22), new Vector3(10, 0.5f, 8), true);
        steep.Rotation = new Vector3(0, 0, 60);
        Add(workspace, "Walker", new Vector3(0, 2.3f, -26), new Vector3(2, 4, 2), false);
        var ghost = Add(workspace, "Ghost", new Vector3(-20, 1, -10), new Vector3(1, 1, 1), true);
        ghost.CanCollide = false;
    }

    private static void NavigationScene(Workspace workspace)
    {
        // A long wall across the middle (x -15..15, z 0), a fence on the right with a gap, and a walled-in pen.
        Add(workspace, "Wall", new Vector3(0, 2, 0), new Vector3(30, 4, 1), true);
        Add(workspace, "FenceRight", new Vector3(32.5f, 2, 0), new Vector3(5, 4, 1), true);
        foreach (var (name, center, size) in new[]
                 {
                     ("PenN", new Vector3(30, 2, 34), new Vector3(9, 4, 1)), ("PenS", new Vector3(30, 2, 26), new Vector3(9, 4, 1)),
                     ("PenE", new Vector3(34, 2, 30), new Vector3(1, 4, 9)), ("PenW", new Vector3(26, 2, 30), new Vector3(1, 4, 9))
                 })
        {
            Add(workspace, name, center, size, true);
        }
    }

    public static void Navigation(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeNav", NavProbe), ("ProbeAgents", AgentProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeNav");
        NavigationScene(workspace);
        var s = Begin(root, workspace);
        Run(s, 2);
        AssertNoFaults(report, "V5 Navigation", s.Faults);
        AssertLuaChecks(report, "V5 Navigation", s.Log, ["path_found", "path_goes_round", "no_point_in_wall", "segments_walkable", "ends_exact",
            "unreachable_nil_reason", "is_reachable", "partial_path", "walkable_query", "nearest_walkable", "bad_build_option", "bad_findpath_arg", "draw_grid",
            "dynamic_rebake_changes_path", "set_blocked"]);
        TestReport.Info(string.Join(" | ", Lines(s.Log, "[Lua] Assets/Scripts/ProbeNav.lua (ProbeNav): NAV").Select(l => l[(l.IndexOf("NAV", StringComparison.Ordinal) + 4)..])));
        End(s, workspace);

        workspace = new Workspace();
        AddProbe(workspace, "ProbeAgents");
        NavigationScene(workspace);
        Add(workspace, "Guard", new Vector3(-10, 2, -10), new Vector3(2, 4, 2), false);
        Add(workspace, "Chaser", new Vector3(-20, 2, -20), new Vector3(2, 4, 2), false);
        var target = Add(workspace, "Target", new Vector3(-5, 2, 10), new Vector3(2, 4, 2), true);
        target.CanCollide = false;
        s = Begin(root, workspace);
        Run(s, 905);
        AssertNoFaults(report, "V5 Navigation agents", s.Faults);
        AssertLuaChecks(report, "V5 Navigation agents", s.Log, ["follow_ok", "agent_bad_option", "agent_bad_move", "patrol_arrives_repeatedly", "patrol_waypoints",
            "follower_keeps_up", "follower_travelled", "status_strings", "agent_gone_with_object"]);
        TestReport.Info(string.Join(" | ", Lines(s.Log, "[Lua] Assets/Scripts/ProbeAgents.lua (ProbeAgents): AGENT").Select(l => l[(l.IndexOf("AGENT", StringComparison.Ordinal) + 6)..])));
        End(s, workspace);

        root = Project(Path.Combine(dir, "terrain"), ("ProbeNavTerrain", NavTerrainProbe));
        workspace = new Workspace();
        AddProbe(workspace, "ProbeNavTerrain");
        NavigationTerrainScene(workspace);
        s = Begin(root, workspace);
        Run(s, 805);
        AssertNoFaults(report, "V5 Navigation terrain", s.Faults);
        AssertLuaChecks(report, "V5 Navigation terrain", s.Log, ["wall_side_not_walkable", "path_keeps_clear_of_wall", "low_step_climbed", "low_step_height",
            "high_step_not_climbed", "gentle_ramp_walkable", "steep_ramp_not_walkable", "destination_set", "move_none_reports_only", "stuck_fails"]);
        foreach (var line in Lines(s.Log, "[Lua] Assets/Scripts/ProbeNavTerrain.lua (ProbeNavTerrain): TERRAIN"))
        {
            TestReport.Info(line[(line.IndexOf("TERRAIN", StringComparison.Ordinal) + 8)..]);
        }

        End(s, workspace);
    }

    // ---- Perception --------------------------------------------------------------------------------------------

    private const string SightProbe = """
        local guard, thief, detected, lost, heard = nil, nil, 0, 0, {}
        local sensor
        function Start()
          guard = Scene.Find("Guard")       -- looks along -Z (its Forward)
          thief = Scene.Find("Thief")
          thief:AddTag("Thief")
          chk("in_view_ahead", (Perception.InFieldOfView(guard, Vector3.new(0, 2, -10), 90)))
          chk("not_in_view_behind", not Perception.InFieldOfView(guard, Vector3.new(0, 2, 10), 90))
          local seen, info = Perception.CanSee(guard, thief, { FieldOfView = 90, Range = 30 })
          chk("sees_thief", seen and info.InView and info.InRange and info.Visible)
          chk("line_of_sight_clear", (Perception.HasLineOfSight(guard, thief)))
          chk("blocked_by_wall", not Perception.CanSee(guard, Scene.Find("Hidden"), { FieldOfView = 90, Range = 40 }))
          local clear, hit = Perception.HasLineOfSight(guard, Scene.Find("Hidden"))
          chk("blocker_reported", not clear and hit.Object == Scene.Find("Cover"))
          chk("out_of_range", not Perception.CanSee(guard, thief, { Range = 5 }))
          chk("find_visible", #Perception.FindVisible(guard, { OnlyTags = "Thief", FieldOfView = 90, Range = 40 }) == 1)
          sensor = Perception.CreateSensor(guard, { Range = 40, FieldOfView = 90, OnlyTags = "Thief", Interval = 0.05, Debug = true })
          sensor:OnDetected(function(object) detected = detected + 1 end)
          sensor:OnLost(function(object) lost = lost + 1 end)
          chk("sensor_bad_option", not pcall(Perception.CreateSensor, guard, { Rang = 3 }))
          Perception.OnNoise(guard, function(noise) heard[#heard + 1] = noise.Kind .. ":" .. string.format("%.0f", noise.Distance) end)
          Perception.OnNoise(Scene.Find("FarListener"), function(noise) heard[#heard + 1] = "far" end)
        end
        local frame = 0
        function Update()
          frame = frame + 1
          if frame == 10 then
            chk("sensor_detects", detected == 1 and sensor:IsDetected(thief) and #sensor:GetDetected() == 1)
            thief:Teleport(Vector3.new(6, 2, -32))   -- behind the cover wall
          elseif frame == 20 then
            chk("sensor_loses_behind_wall", lost == 1 and not sensor:IsDetected(thief))
            Perception.EmitNoise(Vector3.new(3, 2, -3), { Radius = 15, Kind = "Footstep", Source = thief })
          elseif frame == 22 then
            chk("noise_heard_nearby", heard[1] == "Footstep:4" and #heard == 1)
            thief:Teleport(Vector3.new(0, 2, -12))
          elseif frame == 30 then
            chk("detected_again", detected == 2)
            sensor:Destroy()
            chk("sensor_destroyed", not sensor.Exists)
          end
        end
        """;

    public static void Perception(TestReport report, string dir)
    {
        var root = Project(dir, ("ProbeSight", SightProbe));
        var workspace = new Workspace();
        AddProbe(workspace, "ProbeSight");
        Add(workspace, "Guard", new Vector3(0, 2, 0), new Vector3(2, 4, 2), true);
        var thief = Add(workspace, "Thief", new Vector3(0, 2, -12), new Vector3(2, 4, 2), false);
        Add(workspace, "Cover", new Vector3(6, 3, -22), new Vector3(6, 6, 1), true);
        Add(workspace, "Hidden", new Vector3(6, 2, -30), new Vector3(2, 4, 2), true);
        var far = Add(workspace, "FarListener", new Vector3(0, 2, 80), new Vector3(1, 1, 1), true);
        far.CanCollide = false;
        var s = Begin(root, workspace);
        Run(s, 32);
        AssertNoFaults(report, "V5 Perception", s.Faults);
        AssertLuaChecks(report, "V5 Perception", s.Log, ["in_view_ahead", "not_in_view_behind", "sees_thief", "line_of_sight_clear", "blocked_by_wall",
            "blocker_reported", "out_of_range", "find_visible", "sensor_bad_option", "sensor_detects", "sensor_loses_behind_wall", "noise_heard_nearby",
            "detected_again", "sensor_destroyed"]);
        End(s, workspace);
    }
}
