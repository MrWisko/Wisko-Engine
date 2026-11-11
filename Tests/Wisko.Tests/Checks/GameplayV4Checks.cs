using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Lua Capabilities V4 through the editor's PlayModeController: gravity scale and damping (flight), anchored Parts moving
/// on their own (platforms, spinning machinery), conveyor surfaces, collision groups and pair rules, joints (weld, hinge
/// with motor and limits, ball socket), object-space helpers, attribute signals and the math helpers.
/// </summary>
internal static class GameplayV4Checks
{
    private static Part Block(string name, float x, float y, float z, float sx, float sy, float sz, bool anchored, params string[] scripts) =>
        MakePart(name, new Vector3(x, y, z), new Vector3(sx, sy, sz), anchored, scripts);

    private static (PlayModeController Play, OutputLog Log, List<PlayScriptFault> Faults) Start(string root, Workspace workspace)
    {
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        return (StartPlay(root, workspace, null, log, faults), log, faults);
    }

    public static void MotionPrimitives(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Motion.lua", ChkFunction + """
            function Start()
              local floater, half, dragged = Scene.Find("Floater"), Scene.Find("Half"), Scene.Find("Dragged")
              floater.GravityScale = 0
              half.GravityScale = 0.5
              chk("gravity_scale_reads", floater.GravityScale == 0 and half.GravityScale == 0.5)
              chk("gravity_scale_rejects_text", not pcall(function() floater.GravityScale = "no" end))
              dragged.GravityEnabled = false
              dragged.LinearDamping = 2
              dragged.LinearVelocity = Vector3.new(10, 0, 0)
              chk("damping_reads", dragged.LinearDamping == 2 and dragged.AngularDamping == 0)
              chk("damping_rejects_negative", not pcall(function() dragged.LinearDamping = -1 end))
              local platform, spinner, belt = Scene.Find("Platform"), Scene.Find("Spinner"), Scene.Find("Belt")
              platform.LinearVelocity = Vector3.new(2, 0, 0)
              chk("anchored_velocity_reads", platform.LinearVelocity.x == 2)
              spinner.AngularVelocity = Vector3.new(0, 90, 0)
              belt.SurfaceVelocity = Vector3.new(4, 0, 0)
              chk("surface_velocity_reads", belt.SurfaceVelocity.x == 4)
              chk("surface_velocity_unanchored_errors", not pcall(function() floater.SurfaceVelocity = Vector3.new(1, 0, 0) end))
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(Block("Ground", 0, -1, 0, 200, 2, 200, true, "Assets/Scripts/Motion.lua"));
        var floater = Block("Floater", -40, 20, 0, 2, 2, 2, false);
        var half = Block("Half", -36, 20, 0, 2, 2, 2, false);
        var full = Block("Full", -32, 20, 0, 2, 2, 2, false);
        var dragged = Block("Dragged", -40, 40, 20, 2, 2, 2, false);
        var platform = Block("Platform", 0, 5, -30, 6, 1, 6, true);
        var rider = Block("Rider", 0, 6.5f, -30, 1, 2, 1, false);
        var spinner = MakePart("Spinner", new Vector3(30, 1, 0), new Vector3(10, 1, 10), true);
        var spinRider = Block("SpinRider", 33, 2.5f, 0, 1, 2, 1, false);
        var belt = Block("Belt", 0, 0.5f, 30, 20, 1, 3, true);
        var crate = Block("Crate", -6, 2, 30, 1.5f, 1.5f, 1.5f, false);
        foreach (var part in new[] { floater, half, full, dragged, platform, rider, spinner, spinRider, belt, crate })
        {
            workspace.AddPart(part);
        }

        var (play, log, faults) = Start(root, workspace);
        Step(play, 30, faults);
        var fallFull = 20f - full.Position.Y;
        var fallHalf = 20f - half.Position.Y;
        report.Check("GravityScale 0: the Part floats (flight)", MathF.Abs(floater.Position.Y - 20f) < 0.01f, $"Y = {floater.Position.Y:F3}");
        report.Check("GravityScale 0.5: falls half as far as gravity 1 in the same time", fallFull > 3f && MathF.Abs(fallHalf / fallFull - 0.5f) < 0.05f,
            $"full fell {fallFull:F2}, half fell {fallHalf:F2}");
        Step(play, 30, faults);
        var draggedSpeed = play.Runtime is not null ? MathF.Abs(dragged.Position.X - (-40f)) : 0f;
        report.Check("LinearDamping 2: 10 studs/s coasts about (1 - e^-2) / 2 x 10 = 4.3 studs in 1 s", MathF.Abs(draggedSpeed - 4.32f) < 0.3f,
            $"travelled {draggedSpeed:F2}");
        report.Check("Anchored Part with LinearVelocity moves on its own (2 studs in 1 s)", MathF.Abs(platform.Position.X - 2f) < 0.1f,
            $"platform X = {platform.Position.X:F3}");
        report.Check("A box resting on the moving platform rides along by friction", rider.Position.X > 1.5f && MathF.Abs(rider.Position.Y - 6.5f) < 0.2f,
            $"rider at {rider.Position.X:F2}, {rider.Position.Y:F2}");
        report.Check("Anchored Part with AngularVelocity spins (90 degrees in 1 s)", MathF.Abs(MathF.Abs(spinner.Rotation.Y) - 90f) < 3f,
            $"spinner rotation Y = {spinner.Rotation.Y:F2}");
        var riderAngle = MathF.Atan2(spinRider.Position.Z - 0f, spinRider.Position.X - 30f) * 180f / MathF.PI;
        report.Check("A box on the spinning platform is carried around it", MathF.Abs(riderAngle) > 45f && spinRider.Position.Y > 1.5f,
            $"rider angle {riderAngle:F1} deg at {spinRider.Position.X:F2}, {spinRider.Position.Z:F2}");
        report.Check("Conveyor surface carries the crate (+X) while the belt stays put",
            crate.Position.X > -4f && belt.Position.X == 0f && belt.Position.Y == 0.5f, $"crate X {crate.Position.X:F2}, belt at {belt.Position.X},{belt.Position.Y}");
        var crateSpeed = play.Physics!.LinearVelocityOf(crate).X;
        report.Check("Conveyor brings the crate up to the belt speed (4 studs/s)", MathF.Abs(crateSpeed - 4f) < 0.3f, $"{crateSpeed:F2} studs/s");

        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: platform, spinner, belt and riders back where they were",
            platform.Position.X == 0f && spinner.Rotation.Y == 0f && belt.Position.X == 0f && crate.Position.X == -6f && rider.Position.X == 0f);
        AssertNoFaults(report, "Motion primitives", faults);
        AssertLuaChecks(report, "Motion primitives", log,
        [
            "gravity_scale_reads", "gravity_scale_rejects_text", "damping_reads", "damping_rejects_negative", "anchored_velocity_reads",
            "surface_velocity_reads", "surface_velocity_unanchored_errors"
        ]);
    }

    public static void Conveyors(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Belts.lua", ChkFunction + """
            local frame = 0
            local a, b, c
            function Start()
              a, b, c = Scene.Find("BeltA"), Scene.Find("BeltB"), Scene.Find("BeltC")
              a.SurfaceVelocity = a.Right * 5    -- along +X
              b.SurfaceVelocity = Vector3.new(0, 0, 5) -- along +Z
              c.SurfaceVelocity = Vector3.new(-2, 0, 0)
            end
            function Update(dt)
              frame = frame + 1
              if frame == 240 then
                c.SurfaceVelocity = Vector3.new(2, 0, 0) -- reversed
              elseif frame == 360 then
                c.SurfaceVelocity = Vector3.new(0, 0, 0) -- stopped
              end
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(Block("Ground", 0, -1, 0, 200, 2, 200, true, "Assets/Scripts/Belts.lua"));
        // BeltA along X ends where BeltB (along Z) begins: a box rides A, transfers to B.
        var beltA = Block("BeltA", 0, 0.5f, 0, 12, 1, 3, true);
        var beltB = Block("BeltB", 7.5f, 0.5f, 7.5f, 3, 1, 18, true);
        var beltC = Block("BeltC", -30, 0.5f, 0, 20, 1, 3, true);
        var box1 = Block("Box1", -4, 1.75f, 0, 1.5f, 1.5f, 1.5f, false);
        var box2 = Block("Box2", -30, 1.75f, 0, 1.5f, 1.5f, 1.5f, false);
        var walker = Block("Walker", -30, 2.5f, 0.0f, 1, 3, 1, false);
        walker.Position = new Vector3(-26, 2.5f, 0);
        foreach (var part in new[] { beltA, beltB, beltC, box1, box2, walker })
        {
            workspace.AddPart(part);
        }

        var (play, log, faults) = Start(root, workspace);
        Step(play, 238, faults);
        report.Check("Belt A then belt B: the box transfers and travels along +Z", box1.Position.Z > 3f && box1.Position.X > 5f,
            $"box1 at {box1.Position.X:F2}, {box1.Position.Y:F2}, {box1.Position.Z:F2}");
        var box2AtReverse = box2.Position.X;
        report.Check("Belt C (-X) carries its box toward -X", box2AtReverse < -36f, $"box2 X = {box2AtReverse:F2}");
        report.Check("A tall player-like body standing on belt C is carried too", walker.Position.X < -30f && walker.Position.Y > 2f,
            $"walker at {walker.Position.X:F2}, {walker.Position.Y:F2}");
        Step(play, 120, faults);
        report.Check("Reversed belt C brings its box back toward +X", box2.Position.X > box2AtReverse + 2f, $"{box2AtReverse:F2} -> {box2.Position.X:F2}");
        Step(play, 30, faults);
        var stoppedAt = box2.Position.X;
        Step(play, 60, faults);
        report.Check("Stopped belt: the box comes to rest by friction", MathF.Abs(box2.Position.X - stoppedAt) < 0.05f,
            $"{stoppedAt:F3} -> {box2.Position.X:F3}");
        report.Check("Belts never moved themselves", beltA.Position.X == 0f && beltB.Position.Z == 7.5f && beltC.Position.X == -30f);
        play.Stop(workspace);
        play.Dispose();
        AssertNoFaults(report, "Conveyors", faults);
    }

    public static void Joints(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Joints.lua", ChkFunction + """
            local frame = 0
            local mill, door, weld, socket
            function Start()
              local blade, panel, a, b, bob = Scene.Find("Blade"), Scene.Find("Door"), Scene.Find("WeldA"), Scene.Find("WeldB"), Scene.Find("Bob")
              mill = Physics.CreateHinge(blade, nil, Vector3.new(0, 10, 0), Vector3.new(0, 0, 1))
              mill.MotorEnabled = true
              mill.MotorSpeed = 90
              mill.MotorMaxTorque = 100000
              chk("hinge_reads", mill.Type == "Hinge" and mill.Active and mill.PartA == blade and mill.PartB == nil and mill.MotorSpeed == 90)
              door = Physics.CreateHinge(panel, nil, Vector3.new(18, 3, 0), Vector3.new(0, 1, 0))
              door.LimitsEnabled = true
              door.LowerAngle = -90
              door.UpperAngle = 0
              weld = Physics.CreateWeld(a, b)
              chk("weld_reads", weld.Type == "Weld" and weld.PartB == b and weld.CollideConnected == false)
              socket = Physics.CreateBallSocket(bob, nil, Vector3.new(40, 10, 0))
              bob.LinearVelocity = Vector3.new(6, 0, 0)
              chk("joint_both_anchored_errors", not pcall(Physics.CreateWeld, Scene.Find("Ground"), Scene.Find("Post")))
              chk("joint_zero_axis_errors", not pcall(Physics.CreateHinge, blade, nil, Vector3.new(0, 10, 0), Vector3.new(0, 0, 0)))
              chk("joint_non_object_errors", not pcall(Physics.CreateWeld, blade, "nope"))
              chk("motor_on_weld_errors", not pcall(function() weld.MotorSpeed = 5 end))
              chk("joint_unknown_member_errors", not pcall(function() return weld.Bogus end))
              chk("joint_read_only_errors", not pcall(function() weld.Angle = 3 end))
            end
            function Update(dt)
              frame = frame + 1
              local panel = Scene.Find("Door")
              if frame <= 60 then
                panel:ApplyForceAtPoint(Vector3.new(0, 0, -400), Vector3.new(21.5, 3, 0)) -- keeps pushing the far edge
              end
              if frame == 60 then
                chk("motor_turns_blade", math.abs(math.abs(mill.Angle) - 90) < 15)
                chk("limits_hold_door", door.Angle >= -92 and door.Angle <= 2)
                Scene.Find("WeldA"):ApplyImpulse(0, 400, 0)
              elseif frame == 61 then
                -- An impulse at A's centre is off the pair's centre of mass: the welded pair rises and turns as one
                -- body (centre of mass at 400 / 16 = 25 studs/s up), B lifted with it.
                local a, b = Scene.Find("WeldA"), Scene.Find("WeldB")
                local average = (a.LinearVelocity.y + b.LinearVelocity.y) / 2
                chk("weld_moves_together", b.LinearVelocity.y > 5 and math.abs(average - 25) < 3)
              elseif frame == 75 then
                local a, b = Scene.Find("WeldA"), Scene.Find("WeldB")
                chk("weld_stays_rigid", math.abs(Vector3.distance(a.Transform.Position, b.Transform.Position) - 2) < 0.05)
              elseif frame == 90 then
                weld:Destroy()
                chk("destroyed_joint_reads", weld.Destroyed == true and weld.Active == false)
                chk("destroyed_joint_write_errors", not pcall(function() weld.Enabled = true end))
                local bob = Scene.Find("Bob")
                local d = Vector3.distance(bob.Transform.Position, Vector3.new(40, 10, 0))
                chk("ball_socket_keeps_length", math.abs(d - 5) < 0.15)
              end
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(Block("Ground", 0, -1, 0, 200, 2, 200, true, "Assets/Scripts/Joints.lua"));
        workspace.AddPart(Block("Post", 0, 4, -2, 1, 8, 1, true));
        var blade = Block("Blade", 0, 10, 0, 8, 1, 1, false);
        var door = Block("Door", 20, 3, 0, 4, 6, 0.5f, false);
        var weldA = Block("WeldA", -20, 1, 0, 2, 2, 2, false);
        var weldB = Block("WeldB", -18, 1, 0, 2, 2, 2, false);
        var bob = Block("Bob", 40, 5, 0, 1, 1, 1, false);
        foreach (var part in new[] { blade, door, weldA, weldB, bob })
        {
            workspace.AddPart(part);
        }

        var (play, log, faults) = Start(root, workspace);
        Step(play, 61, faults);
        report.Check("Hinge motor: the windmill blade turns about its pivot (its centre stays put)",
            MathF.Abs(blade.Position.X) < 0.1f && MathF.Abs(blade.Position.Y - 10f) < 0.1f && MathF.Abs(blade.Rotation.Z) > 60f,
            $"blade at {blade.Position.X:F2}, {blade.Position.Y:F2}, rotation Z {blade.Rotation.Z:F1}");
        report.Check("Hinged door swings about its hinge line (x = 18 edge stays)", MathF.Abs(door.Position.Y - 3f) < 0.2f && MathF.Abs(door.Rotation.Y) > 20f,
            $"door at {door.Position.X:F2}, {door.Position.Z:F2}, rotation Y {door.Rotation.Y:F1}");
        Step(play, 60, faults);
        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: jointed Parts back at their edit poses", blade.Rotation.Z == 0f && door.Rotation.Y == 0f && bob.Position.X == 40f);
        AssertNoFaults(report, "Joints", faults);
        AssertLuaChecks(report, "Joints", log,
        [
            "hinge_reads", "weld_reads", "joint_both_anchored_errors", "joint_zero_axis_errors", "joint_non_object_errors", "motor_on_weld_errors",
            "joint_unknown_member_errors", "joint_read_only_errors", "motor_turns_blade", "limits_hold_door", "weld_moves_together", "weld_stays_rigid",
            "destroyed_joint_reads", "destroyed_joint_write_errors", "ball_socket_keeps_length"
        ]);
    }

    public static void ObjectSpaceAndSignals(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Space.lua", ChkFunction + """
            local function near(a, b, tolerance) return Vector3.distance(a, b) < (tolerance or 1e-3) end
            local frame, changes, connection = 0, {}, nil
            function Start()
              local probe, turner, spinner = Scene.Find("Probe"), Scene.Find("Turner"), Scene.Find("Spinner")
              chk("forward_default", near(probe.Forward, Vector3.new(0, 0, -1)) and near(probe.Right, Vector3.new(1, 0, 0)) and near(probe.Up, Vector3.new(0, 1, 0)))
              chk("directions_read_only", not pcall(function() probe.Forward = Vector3.new(1, 0, 0) end))
              turner.Transform.Rotation = Vector3.new(0, 37, 0)
              local f, r, u = turner.Forward, turner.Right, turner.Up
              chk("directions_orthonormal", math.abs(Vector3.dot(f, r)) < 1e-4 and math.abs(Vector3.magnitude(f) - 1) < 1e-4 and near(Vector3.cross(r, u), Vector3.new(-f.x, -f.y, -f.z)))
              chk("forward_matches_vector_to_world", near(turner:VectorToWorld(Vector3.new(0, 0, -1)), f))
              local local_point = Vector3.new(1, 2, 3)
              chk("point_round_trip", near(turner:PointToLocal(turner:PointToWorld(local_point)), local_point))
              probe:Rotate(0, 90, 0)
              chk("rotate_local", math.abs(probe.Transform.Rotation.y - 90) < 1e-3)
              probe:Rotate(Vector3.new(0, -90, 0), "World")
              chk("rotate_world", math.abs(probe.Transform.Rotation.y) < 1e-3)
              local orbit = Scene.Find("Orbiter")
              orbit:RotateAround(Vector3.new(0, 5, 0), Vector3.new(0, 1, 0), 180)
              chk("rotate_around", near(orbit.Transform.Position, Vector3.new(-2, 5, 0), 1e-3))
              probe:LookAt(Vector3.new(10, 5, 0))
              chk("look_at", near(probe.Forward, Vector3.new(1, 0, 0), 1e-3))
              chk("get_mass_auto", math.abs(probe:GetMass() - 8) < 1e-3 and math.abs(spinner:GetMass() - 8) < 1e-3)
              spinner.GravityEnabled = false
              spinner.AngularVelocity = Vector3.new(0, 180, 0)
              local edge = spinner:GetVelocityAtPoint(spinner.Transform.Position + Vector3.new(1, 0, 0))
              chk("velocity_at_point", math.abs(Vector3.magnitude(edge) - math.pi) < 0.01)
              local pusher = Scene.Find("Pusher")
              pusher.GravityEnabled = false
              pusher.Transform.Rotation = Vector3.new(0, 90, 0)
              pusher:ApplyImpulse(Vector3.new(0, 0, -16), "Local") -- along its Forward
              chk("local_impulse_goes_forward", near(Vector3.normalize(pusher.LinearVelocity), pusher.Forward, 1e-3))
              chk("space_argument_checked", not pcall(pusher.ApplyImpulse, pusher, Vector3.new(1, 0, 0), "Sideways"))
              chk("rotate_bad_args", not pcall(probe.Rotate, probe, "x"))
              chk("look_at_bad_args", not pcall(probe.LookAt, probe, nil))
              chk("point_to_world_bad_args", not pcall(probe.PointToWorld, probe, "bad"))
              connection = probe:OnAttributeChanged("Coins", function(value, object) changes[#changes + 1] = { value = value, object = object } end)
              chk("attribute_signal_bad_args", not pcall(probe.OnAttributeChanged, probe, 5, print))
              probe:SetAttribute("Coins", 5)
              chk("math_helpers", math.clamp(5, 0, 3) == 3 and math.clamp(-1, 0, 3) == 0 and math.lerp(2, 4, 0.5) == 3
                and math.sign(-2) == -1 and math.sign(0) == 0 and math.round(2.5) == 3 and math.round(-2.5) == -3
                and math.type(math.round(1.2)) == "integer")
              chk("math_clamp_checks", not pcall(math.clamp, 1, 3, 0) and not pcall(math.lerp, "a", 1, 0))
              chk("vector_helpers", near(Vector3.clampMagnitude(Vector3.new(10, 0, 0), 3), Vector3.new(3, 0, 0))
                and near(Vector3.moveTowards(Vector3.new(0, 0, 0), Vector3.new(10, 0, 0), 4), Vector3.new(4, 0, 0))
                and near(Vector3.moveTowards(Vector3.new(0, 0, 0), Vector3.new(1, 0, 0), 4), Vector3.new(1, 0, 0))
                and near(Vector3.project(Vector3.new(3, 4, 0), Vector3.new(2, 0, 0)), Vector3.new(3, 0, 0))
                and math.abs(Vector3.angle(Vector3.new(1, 0, 0), Vector3.new(0, 1, 0)) - 90) < 1e-6)
            end
            function Update(dt)
              frame = frame + 1
              local probe = Scene.Find("Probe")
              if frame == 1 then
                chk("attribute_signal_fires_next_frame", #changes == 1 and changes[1].value == 5 and changes[1].object == probe)
                probe:SetAttribute("Coins", 5) -- same value: no event
                probe:SetAttribute("Coins", 7)
              elseif frame == 2 then
                chk("attribute_signal_only_on_change", #changes == 2 and changes[2].value == 7)
                probe:RemoveAttribute("Coins")
              elseif frame == 3 then
                chk("attribute_signal_on_remove", #changes == 3 and changes[3].value == nil)
                connection:Disconnect()
                probe:SetAttribute("Coins", 9)
              elseif frame == 4 then
                chk("attribute_signal_disconnected", #changes == 3)
              end
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(Block("Ground", 0, -1, 0, 200, 2, 200, true, "Assets/Scripts/Space.lua"));
        workspace.AddPart(Block("Probe", 0, 5, 0, 2, 2, 2, true));
        workspace.AddPart(Block("Turner", 5, 5, 0, 2, 2, 2, true));
        workspace.AddPart(Block("Orbiter", 2, 5, 0, 1, 1, 1, true));
        workspace.AddPart(Block("Spinner", 0, 20, 10, 2, 2, 2, false));
        workspace.AddPart(Block("Pusher", 10, 20, 10, 2, 2, 2, false));
        var (play, log, faults) = Start(root, workspace);
        Step(play, 6, faults);
        play.Stop(workspace);
        play.Dispose();
        AssertNoFaults(report, "Object space and signals", faults);
        AssertLuaChecks(report, "Object space and signals", log,
        [
            "forward_default", "directions_read_only", "directions_orthonormal", "forward_matches_vector_to_world", "point_round_trip",
            "rotate_local", "rotate_world", "rotate_around", "look_at", "get_mass_auto", "velocity_at_point", "local_impulse_goes_forward",
            "space_argument_checked", "rotate_bad_args", "look_at_bad_args", "point_to_world_bad_args", "attribute_signal_bad_args",
            "math_helpers", "math_clamp_checks", "vector_helpers", "attribute_signal_fires_next_frame", "attribute_signal_only_on_change",
            "attribute_signal_on_remove", "attribute_signal_disconnected"
        ]);
    }

    // The flight example (docs/lua-api.md, V4 demo): an ordinary state built from GravityScale, velocity and input.
    public const string FlightScript = """
        -- Flight.lua: flying is an ordinary state: gravity off, velocity steered toward camera-relative input,
        -- accelerated smoothly and limited. G, the Fly button, or touching a FlightOrb (it calls SetFlying) toggle it.
        local SPEED, ACCEL = 20, 40
        local flying = false
        local button

        local function setFlying(on)
          flying = on
          self.GravityScale = on and 0 or 1   -- off: normal physics again (it falls)
          button.Text = on and "Land" or "Fly"
          print("[Flight] " .. (on and "flying" or "landed"))
          Events.Emit("FlightChanged", { object = self, flying = on })
        end

        function Start()
          self.RotationLocked = true
          button = UI.Create("Button", "FlyButton")
          button:SetAnchor(1, 0)
          button:SetPosition(1, -16, 0, 16)
          button:SetSize(0, 120, 0, 40)
          button.Text = "Fly"
          button:OnClick(function() setFlying(not flying) end)
        end

        function Update(dt)
          if Input.IsKeyPressed("G") then
            setFlying(not flying)
          end
          if not flying then
            return
          end
          local move = Camera.Forward * Input.GetAxis("S", "W") + Camera.Right * Input.GetAxis("A", "D")
            + Vector3.new(0, 1, 0) * Input.GetAxis("LeftControl", "Space")
          local target = Vector3.clampMagnitude(move * SPEED, SPEED)                     -- no faster than SPEED
          self.LinearVelocity = Vector3.moveTowards(self.LinearVelocity, target, ACCEL * dt) -- frame-rate independent
        end

        return { SetFlying = setFlying, IsFlying = function() return flying end }
        """;

    // The car example: a raycast vehicle made of general calls (no built-in vehicle).
    public const string CarScript = """
        -- Car.lua: a raycast car from general physics calls. Four suspension rays push the body up (springs), each tyre
        -- grips sideways, the rear wheels drive and every wheel brakes. W/S throttle and reverse, A/D steer, Space
        -- brakes, only while Shared.vehicle is this car (someone is driving it).
        local REST, RADIUS = 0.6, 0.5        -- spring length below each mount, wheel radius
        local MAX_STEER, MAX_SPEED = 30, 40
        local GRIP = 10                        -- how quickly a tyre stops sliding sideways (per second)
        local stiffness, damping, engine, brake, mass
        local wheels = {}
        local steer, throttle = 0, 0

        function Start()
          mass = self:GetMass()
          local gravity = -Physics.Gravity.y
          stiffness = mass * gravity / 4 / (REST * 0.5)       -- at rest each spring is half compressed
          damping = 2 * math.sqrt(stiffness * mass / 4) * 0.7  -- a little under critical: no bouncing
          engine = mass * 14 / 2                             -- 14 studs/s^2 from the two driven wheels
          brake = mass * 25 / 4
          self.AngularDamping = 1.5
          local size = self.Transform.Scale
          local x, y, z = size.x / 2 - 0.4, -size.y / 2, size.z / 2 - 0.9
          wheels = {
            { mount = Vector3.new(-x, y, -z), front = true }, { mount = Vector3.new(x, y, -z), front = true },
            { mount = Vector3.new(-x, y, z) }, { mount = Vector3.new(x, y, z) },
          }
        end

        function Update(dt)
          local driving = Shared.vehicle == self
          throttle = driving and Input.GetAxis("S", "W") or 0
          local braking = driving and Input.IsKeyDown("Space") or not driving
          local steerTarget = (driving and Input.GetAxis("A", "D") or 0) * MAX_STEER
          steer = steer + (steerTarget - steer) * math.min(1, 8 * dt)
          local up, forward, right = self.Up, self.Forward, self.Right
          local speed = Vector3.dot(self.LinearVelocity, forward)
          local filter = { Ignore = self, IgnoreTriggers = true, IgnoreTags = "CarPart" }
          for _, wheel in ipairs(wheels) do
            local origin = self:PointToWorld(wheel.mount)
            local hit = Physics.Raycast(origin, -up, REST + RADIUS, filter)
            wheel.grounded = hit ~= nil
            if hit then
              local compression = REST + RADIUS - hit.Distance
              local spring = stiffness * compression - damping * Vector3.dot(self:GetVelocityAtPoint(origin), up)
              self:ApplyForceAtPoint(up * math.max(0, spring), origin)
              local wheelForward, wheelRight = forward, right
              if wheel.front then
                local a = math.rad(steer)
                wheelForward = forward * math.cos(a) + right * math.sin(a)
                wheelRight = right * math.cos(a) - forward * math.sin(a)
              end
              local velocity = self:GetVelocityAtPoint(origin)
              local load = mass / 4
              local force = wheelRight * (-Vector3.dot(velocity, wheelRight) * load * GRIP)
              if braking then
                local along = Vector3.dot(velocity, wheelForward)
                force = force + wheelForward * -math.clamp(along * load * 8, -brake, brake)
              elseif not wheel.front and throttle ~= 0 and (math.abs(speed) < MAX_SPEED or speed * throttle < 0) then
                force = force + wheelForward * (throttle * engine)
              end
              self:ApplyForceAtPoint(force, origin)   -- at the mount: grip does not roll the car over
            end
          end
        end

        return {
          Speed = function() return Vector3.dot(self.LinearVelocity, self.Forward) end,
          Steer = function() return steer end,
          GroundedWheels = function()
            local count = 0
            for _, wheel in ipairs(wheels) do if wheel.grounded then count = count + 1 end end
            return count
          end,
        }
        """;

    public static void FlightAndVehicle(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        static string Normalized(string text) => text.Replace("\r\n", "\n").TrimEnd();
        report.Check("Examples: examples/gameplay-basics Flight.lua and Car.lua are exactly the scripts tested here",
            Normalized(LuaV4Demo.Script("Flight")) == Normalized(FlightScript) && Normalized(LuaV4Demo.Script("Car")) == Normalized(CarScript));
        WriteScript(root, "Flight.lua", FlightScript);
        WriteScript(root, "FlightOrb.lua", """
            -- Touching the orb starts flight for whoever has a Flight script.
            function OnTriggerEnter(other)
              local flight = other and other:GetScript("Flight")
              if flight and not flight.IsFlying() then
                flight.SetFlying(true)
                print("ORB flight on")
              end
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(Block("Ground", 0, -1, 0, 400, 2, 400, true));
        var flyer = Block("Flyer", 0, 20, 0, 2, 4, 2, false, "Assets/Scripts/Flight.lua");
        var orb = Block("Orb", 0, 12, 0, 3, 3, 3, true, "Assets/Scripts/FlightOrb.lua");
        orb.IsTrigger = true;
        workspace.AddPart(flyer);
        workspace.AddPart(orb);
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, null, log, faults);
        Step(play, 60, faults);
        var hoverY = flyer.Position.Y;
        report.Check("Flight: touching the orb starts flying (it stops falling mid-air)", hoverY > 6f && LuaReports(log).Count >= 0 && LogContains(log, "ORB flight on"),
            $"Y = {hoverY:F2}");
        Step(play, 60, faults);
        report.Check("Flight: with no input it hovers in place", MathF.Abs(flyer.Position.Y - hoverY) < 0.3f, $"{hoverY:F2} -> {flyer.Position.Y:F2}");
        StepHolding(play, 90, faults, "W");
        var velocity = play.Physics!.LinearVelocityOf(flyer);
        // Read on the frame W was released (it has begun to slow by 40 x 1/60 studs/s): capped at 20, never more.
        report.Check("Flight: W flies along the camera's forward, accelerated to and capped at 20 studs/s",
            velocity.Z < -19f && velocity.Z >= -20.01f && flyer.Position.Z < -15f, $"velocity {velocity.X:F2}, {velocity.Y:F2}, {velocity.Z:F2}");
        var beforeRise = flyer.Position.Y;
        StepHolding(play, 30, faults, "Space");
        report.Check("Flight: Space climbs", flyer.Position.Y > beforeRise + 3f, $"{beforeRise:F2} -> {flyer.Position.Y:F2}");
        Step(play, 30, faults);
        ClickButton(play, "FlyButton");
        Step(play, 120, faults);
        report.Check("Flight: the GUI Fly button toggles flight off: normal physics, it falls and lands", MathF.Abs(flyer.Position.Y - 2f) < 0.1f,
            $"Y = {flyer.Position.Y:F2}");
        StepHolding(play, 1, faults, "G");
        StepHolding(play, 30, faults, "Space");
        report.Check("Flight: G turns it on again (it climbs off the ground)", flyer.Position.Y > 3f, $"Y = {flyer.Position.Y:F2}");
        play.Stop(workspace);
        play.Dispose();
        report.Check("Flight: Stop restores the flyer", flyer.Position.Y == 20f && flyer.Position.Z == 0f);
        AssertNoFaults(report, "Flight", faults);

        // The car at three frame rates: the same drive gives the same speed.
        WriteScript(root, "Car.lua", CarScript);
        WriteScript(root, "Driver.lua", "function Start() Shared.vehicle = Scene.Find(\"Car\") end\n");
        var speeds = new List<(double Dt, float Speed)>();
        foreach (var dt in new[] { 1.0 / 30, 1.0 / 60, 1.0 / 144 })
        {
            var (car, carPlay, carLog, carFaults, track) = StartCar(root);
            StepDt(carPlay, 1.0, dt, carFaults);                         // settle on the springs
            StepDt(carPlay, 2.0, dt, carFaults, "W");                    // accelerate for 2 s
            speeds.Add((dt, -carPlay.Physics!.LinearVelocityOf(car).Z));
            if (dt == 1.0 / 60)
            {
                var forwardSpeed = speeds[^1].Speed;
                report.Check("Car: W accelerates it forward (its Forward, -Z)", forwardSpeed > 15f && car.Position.Z < -10f,
                    $"speed {forwardSpeed:F1} studs/s, Z {car.Position.Z:F1}");
                report.Check("Car: rides level on its four springs", MathF.Abs(car.Rotation.X) < 5f && MathF.Abs(car.Rotation.Z) < 5f && car.Position.Y > 0.9f,
                    $"rotation {car.Rotation.X:F1}, {car.Rotation.Z:F1}, Y {car.Position.Y:F2}");
                // 25 studs/s² of braking stops 27 studs/s in about 1.1 s.
                StepDt(carPlay, 1.5, dt, carFaults, "Space");
                report.Check("Car: Space brakes it to a stop", MathF.Abs(carPlay.Physics.LinearVelocityOf(car).Z) < 1f,
                    $"speed after braking {carPlay.Physics.LinearVelocityOf(car).Z:F2}");
                var heading = car.Rotation.Y;
                StepDt(carPlay, 1.5, dt, carFaults, "W", "D");
                var turned = DeltaAngle(car.Rotation.Y, heading);
                report.Check("Car: D steers right while driving (heading turns clockwise seen from above)", turned < -20f,
                    $"heading {heading:F1} -> {car.Rotation.Y:F1} ({turned:F1})");
                StepDt(carPlay, 1.0, dt, carFaults, "Space");
                var reverseStart = car.Position;
                StepDt(carPlay, 1.5, dt, carFaults, "S");
                var backwards = Vector3.Dot(car.Position - reverseStart, ForwardOf(car));
                report.Check("Car: S from a stop reverses it", backwards < -3f, $"moved {backwards:F2} along its forward");
                report.Check("Car: never tipped over while driving and turning", MathF.Abs(car.Rotation.X) < 15f && MathF.Abs(car.Rotation.Z) < 15f,
                    $"rotation {car.Rotation.X:F1}, {car.Rotation.Z:F1}");
            }

            carPlay.Stop(track);
            carPlay.Dispose();
            AssertNoFaults(report, $"Car at {1 / dt:F0} fps", carFaults);
        }

        var spread = speeds.Max(s => s.Speed) - speeds.Min(s => s.Speed);
        report.Check("Car: the same 2 s drive gives the same speed at 30, 60 and 144 fps (within 10%)", spread < speeds.Average(s => s.Speed) * 0.1f,
            string.Join(", ", speeds.Select(s => $"{1 / s.Dt:F0} fps: {s.Speed:F2}")));

        // Driving into a wall: it stops at the wall, does not pass through or blow up.
        var (wallCar, wallPlay, _, wallFaults, wallTrack) = StartCar(root, wall: true);
        StepDt(wallPlay, 1.0, 1.0 / 60, wallFaults);
        StepDt(wallPlay, 4.0, 1.0 / 60, wallFaults, "W");
        report.Check("Car: driving into a wall stops it at the wall (no tunnelling, no explosion)",
            wallCar.Position.Z > -27f && wallCar.Position.Z < -20f && MathF.Abs(wallCar.Position.Y) < 6f,
            $"car at {wallCar.Position.X:F2}, {wallCar.Position.Y:F2}, {wallCar.Position.Z:F2}");
        wallPlay.Stop(wallTrack);
        wallPlay.Dispose();
        AssertNoFaults(report, "Car into a wall", wallFaults);
    }

    private static (Part Car, PlayModeController Play, OutputLog Log, List<PlayScriptFault> Faults, Workspace Track) StartCar(string root, bool wall = false)
    {
        var track = new Workspace();
        track.AddPart(Block("Ground", 0, -1, 0, 600, 2, 600, true, "Assets/Scripts/Driver.lua"));
        var car = Block("Car", 0, 2, 0, 4, 1, 7, false, "Assets/Scripts/Car.lua");
        track.AddPart(car);
        if (wall)
        {
            track.AddPart(Block("Wall", 0, 3, -30, 20, 6, 2, true));
        }

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        return (car, StartPlay(root, track, null, log, faults), log, faults, track);
    }

    // Plays `seconds` of game time in frames of `dt`, holding the keys (pressed on the first frame, released after).
    private static void StepDt(PlayModeController play, double seconds, double dt, List<PlayScriptFault> faults, params string[] keys)
    {
        var frames = (int)global::System.Math.Round(seconds / dt);
        var input = new ScriptInputState();
        for (var i = 0; i <= frames; i++)
        {
            input.Clear();
            foreach (var key in keys)
            {
                ScriptInputState.TryGetKeyIndex(key, out var index);
                input.SetKey(index, down: i < frames, pressed: i == 0, released: i == frames);
            }

            if (!play.TryUpdate(i == frames ? 0.0 : dt, input, out var error))
            {
                throw new InvalidOperationException(error);
            }

            faults.AddRange(play.TakeNewFaults());
        }
    }

    // Clicks a game GUI button by name, as the editor's pointer would (press, then release on the next frame).
    private static void ClickButton(PlayModeController play, string name)
    {
        var canvas = new Wisko.Engine.Gui.GuiRect(0, 0, 1280, 720);
        var entry = play.Gui!.Update(canvas).First(e => e.Element.Name == name);
        var x = entry.Rect.X + entry.Rect.Width / 2;
        var y = entry.Rect.Y + entry.Rect.Height / 2;
        play.Gui.UpdatePointer(canvas, x, y, true, true, true, false);
        play.Gui.UpdatePointer(canvas, x, y, true, false, false, true);
    }

    private static bool LogContains(OutputLog log, string text)
    {
        for (var i = 0; i < log.Count; i++)
        {
            if (log[i].Message.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static float DeltaAngle(float to, float from)
    {
        var delta = (to - from) % 360f;
        return delta > 180f ? delta - 360f : delta < -180f ? delta + 360f : delta;
    }

    private static Vector3 ForwardOf(Part part)
    {
        var q = Wisko.Engine.Physics.PhysicsMath.EulerDegreesToQuaternion(part.Rotation);
        var f = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0, 0, -1), q);
        return new Vector3(f.X, f.Y, f.Z);
    }

    public static void CollisionRules(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Rules.lua", ChkFunction + """
            function Start()
              local ghost, floor, slab, passer = Scene.Find("Ghost"), Scene.Find("GhostFloor"), Scene.Find("Slab"), Scene.Find("Passer")
              chk("default_group", ghost.CollisionGroup == "Default")
              ghost.CollisionGroup = "Ghosts"
              floor.CollisionGroup = "Walls"
              Physics.SetGroupsCollide("Ghosts", "Walls", false)
              chk("groups_collide_reads", Physics.GroupsCollide("Ghosts", "Walls") == false and Physics.GroupsCollide("Ghosts", "Default") == true)
              local groups = Physics.GetCollisionGroups()
              chk("groups_listed", groups[1] == "Default" and #groups == 3)
              passer:IgnoreCollisionWith(slab)
              chk("ignore_rejects_non_object", not pcall(passer.IgnoreCollisionWith, passer, 5))
              chk("group_name_validated", not pcall(function() ghost.CollisionGroup = "" end))
              chk("set_groups_validated", not pcall(Physics.SetGroupsCollide, "A", "B", "no"))
              -- Queries can ask as a group would: the ghost's ray passes through walls.
              local down = Vector3.new(0, -1, 0)
              local asGhost = Physics.Raycast(Vector3.new(-13, 30, -3), down, 100, { CollisionGroup = "Ghosts" })
              local plain = Physics.Raycast(Vector3.new(-13, 30, -3), down, 100)
              chk("query_as_group_passes_walls", plain ~= nil and plain.Object == floor and (asGhost == nil or asGhost.Object ~= floor))
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(Block("Ground", 0, -1, 0, 200, 2, 200, true, "Assets/Scripts/Rules.lua"));
        var ghostFloor = Block("GhostFloor", -10, 5, 0, 8, 1, 8, true);
        var ghost = Block("Ghost", -10, 10, 0, 2, 2, 2, false);
        var solid = Block("Solid", -10, 10, 3, 1, 1, 1, false);
        var slab = Block("Slab", 10, 5, 0, 8, 1, 8, true);
        var passer = Block("Passer", 10, 10, 0, 2, 2, 2, false);
        var stopped = Block("Stopped", 10, 10, 3, 1, 1, 1, false);
        foreach (var part in new[] { ghostFloor, ghost, solid, slab, passer, stopped })
        {
            workspace.AddPart(part);
        }

        var (play, log, faults) = Start(root, workspace);
        Step(play, 120, faults);
        report.Check("Collision groups: a Ghosts body falls through a Walls floor", ghost.Position.Y < 2f, $"ghost Y = {ghost.Position.Y:F2}");
        report.Check("Collision groups: a Default body still lands on that floor", MathF.Abs(solid.Position.Y - 6f) < 0.1f, $"solid Y = {solid.Position.Y:F2}");
        report.Check("IgnoreCollisionWith: the pair passes through each other", passer.Position.Y < 2f, $"passer Y = {passer.Position.Y:F2}");
        report.Check("IgnoreCollisionWith: other objects still land on the slab", MathF.Abs(stopped.Position.Y - 6f) < 0.1f, $"Y = {stopped.Position.Y:F2}");
        play.Stop(workspace);
        play.Dispose();
        AssertNoFaults(report, "Collision rules", faults);
        AssertLuaChecks(report, "Collision rules", log,
        [
            "default_group", "groups_collide_reads", "groups_listed", "ignore_rejects_non_object", "group_name_validated", "set_groups_validated",
            "query_as_group_passes_walls"
        ]);
    }
}
