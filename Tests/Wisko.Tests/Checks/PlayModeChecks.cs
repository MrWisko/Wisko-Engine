using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>Play Mode through the editor's own PlayModeController: physics, gameplay APIs, GUI scripting, Stop, faults.</summary>
internal static class PlayModeChecks
{
    public static void Gameplay(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Faller.lua", ChkFunction + """
            local collided = false
            function Start()
              chk("start_runs", true)
              chk("self_name", self.Name == "Box")
              local hit = Physics.Raycast(0, 50, 0, 0, -1, 0, 200, self)
              chk("raycast_hits_floor", hit ~= nil and hit.Object ~= nil and hit.Object.Name == "Floor" and math.abs(hit.Point.y - 1) < 0.01)
              Timer.After(0.5, function(v) chk("timer_after_with_args", v == "x") end, "x")
              Events.Emit("ping", 42, self)
              local s = Scene.Create("Sphere", { Name = "Spawned", Position = Vector3.new(30, 5, 0), Anchored = true })
              chk("scene_create", s ~= nil and Scene.Find("Spawned") ~= nil and Scene.Count() == 3)
              local v = Vector3.new(1, 2, 3) + Vector3.new(1, 1, 1)
              chk("vector_ops", v.x == 2 and v.y == 3 and v.z == 4 and Vector3.dot(Vector3.new(1, 0, 0), Vector3.new(0, 1, 0)) == 0)
              self:AddTag("falling")
              self:SetAttribute("hp", 100)
              chk("tags_attributes", Scene.FindByTag("falling") ~= nil and self:GetAttribute("hp") == 100)
              local label = UI.Find("Status")
              chk("ui_find", label ~= nil)
              if label then label.Text = "Lua says hi" end
              chk("ui_create", UI.Create("Frame", "Made") ~= nil and UI.Find("Made") ~= nil)
              chk("gravity_down", Physics.Gravity.y < 0)
            end
            function OnCollisionEnter(other, contact)
              if not collided then
                collided = true
                chk("collision_enter_floor", other ~= nil and other.Name == "Floor" and contact ~= nil and contact.Normal ~= nil)
              end
            end
            """);
        WriteScript(root, "Floor.lua", """
            function Start()
              Events.On("ping", function(n, sender)
                print("CHECK event_cross_script " .. tostring(n == 42 and sender ~= nil and sender.Name == "Box"))
              end)
            end
            """);

        var gui = new GuiDocument();
        gui.Add(new GuiText { Name = "Status", Text = "Waiting" });
        var floor = MakePart("Floor", new Vector3(0, 0, 0), new Vector3(200, 2, 200), true, "Assets/Scripts/Floor.lua");
        var box = MakePart("Box", new Vector3(0, 20, 0), new Vector3(4, 4, 4), false, "Assets/Scripts/Faller.lua");
        var workspace = new Workspace();
        workspace.AddPart(floor);
        workspace.AddPart(box);

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, gui, log, faults);
        var startY = box.Position.Y;
        Step(play, 30, faults);
        var midY = box.Position.Y;
        Step(play, 210, faults);
        var restY = box.Position.Y;
        TestReport.Info($"Box Y: start {startY:F3}, after 0.5 s {midY:F3}, after 4 s {restY:F3} (floor top = 1, half size = 2)");
        report.Check("Physics: unanchored Box falls under gravity", midY < startY - 0.5f, $"{startY:F3} -> {midY:F3}");
        report.Check("Physics: Box comes to rest on the Floor (Y ~= 3)", MathF.Abs(restY - 3f) < 0.15f, $"Y = {restY:F3}");
        report.Check("Physics: anchored Floor did not move", floor.Position.Y == 0f && floor.Position.X == 0f);
        var runtimeText = play.Gui?.Document.Elements.OfType<GuiText>().FirstOrDefault(e => e.Name == "Status")?.Text;
        report.Check("GUI scripting: Lua changed the runtime label", runtimeText == "Lua says hi", $"runtime text '{runtimeText}'");
        report.Check("GUI scripting: authored GUI untouched during Play", gui.Elements.OfType<GuiText>().Single().Text == "Waiting");
        report.Check("Scene.Create: spawned Part is in the workspace during Play", workspace.Parts.Any(p => p.Name == "Spawned"));

        var stop = play.Stop(workspace);
        report.Check("Stop: Box restored to its exact edit position", box.Position.X == 0f && box.Position.Y == 20f && box.Position.Z == 0f,
            $"{box.Position.X},{box.Position.Y},{box.Position.Z}");
        report.Check("Stop: spawned Part removed, authored Parts kept",
            workspace.Parts.Count == 2 && workspace.Parts.Contains(box) && workspace.Parts.Contains(floor) && stop.MembershipChanged);
        report.Check("Stop: back in Edit mode", play.Mode == EditorMode.Edit && play.Gui is null && play.Physics is null);

        AssertNoFaults(report, "Play Mode", faults);
        AssertLuaChecks(report, "Play Mode", log,
        [
            "start_runs", "self_name", "raycast_hits_floor", "timer_after_with_args", "scene_create", "vector_ops", "tags_attributes",
            "ui_find", "ui_create", "gravity_down", "collision_enter_floor", "event_cross_script"
        ]);

        // A second session from the restored scene behaves the same (no drift across Play/Stop cycles).
        var faults2 = new List<PlayScriptFault>();
        var play2 = StartPlay(root, workspace, gui, new OutputLog(), faults2);
        Step(play2, 240, faults2);
        var restY2 = box.Position.Y;
        play2.Stop(workspace);
        play2.Dispose();
        play.Dispose();
        report.Check("Second Play session: same resting height (deterministic, no drift)", MathF.Abs(restY2 - restY) < 0.01f, $"{restY:F4} vs {restY2:F4}");
        report.Check("Second Stop: Box restored again", box.Position.Y == 20f);
    }

    public static void Faults(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Bad.lua", "function Update(dt)\n  local t = nil\n  return t.x\nend\n");
        WriteScript(root, "Syntax.lua", "function Start()\n  print('x'\nend\n");
        WriteScript(root, "Loop.lua", "function Start()\n  while true do end\nend\n");
        var workspace = new Workspace();
        workspace.AddPart(MakePart("BadPart", new Vector3(0, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Bad.lua"));
        workspace.AddPart(MakePart("SyntaxPart", new Vector3(3, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Syntax.lua"));
        workspace.AddPart(MakePart("LoopPart", new Vector3(6, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Loop.lua"));
        workspace.AddPart(MakePart("MissingPart", new Vector3(9, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Missing.lua"));

        var faults = new List<PlayScriptFault>();
        var task = Task.Run(() =>
        {
            var play = StartPlay(root, workspace, null, new OutputLog(), faults);
            Step(play, 3, faults);
            play.Stop(workspace);
            play.Dispose();
        });
        var finished = task.Wait(TimeSpan.FromSeconds(60));
        report.Check("Runaway script: Play does not hang on 'while true do end'", finished, finished ? null : "timed out after 60 s");
        if (!finished)
        {
            return;
        }

        foreach (var fault in faults)
        {
            TestReport.Info($"fault: {fault.Diagnostic.ScriptName} {fault.Diagnostic.Phase} {fault.Diagnostic.Kind} line {fault.Diagnostic.Line}: {fault.Diagnostic.Message}");
        }

        ScriptDiagnostic? Find(string script) =>
            faults.FirstOrDefault(f => f.Script.Reference.AssetPath.EndsWith(script, StringComparison.Ordinal)).Diagnostic;
        var bad = Find("Bad.lua");
        report.Check("Runtime error: reported in Update with the right line", bad is { Kind: ScriptDiagnosticKind.Runtime, Phase: ScriptPhase.Update, Line: 3 }, bad?.ToString() ?? "no fault");
        var syntax = Find("Syntax.lua");
        report.Check("Syntax error: reported at load with a line", syntax is { Kind: ScriptDiagnosticKind.Syntax } && syntax.Line is not null, syntax?.ToString() ?? "no fault");
        var loop = Find("Loop.lua");
        report.Check("Runaway script: stopped by the instruction budget", loop is { Kind: ScriptDiagnosticKind.InstructionBudgetExceeded }, loop?.ToString() ?? "no fault");
        var missing = Find("Missing.lua");
        report.Check("Missing script file: reported", missing is { Kind: ScriptDiagnosticKind.MissingScript }, missing?.ToString() ?? "no fault");
        report.Check("Faults are each reported once", faults.Count == 4, $"{faults.Count} faults");
    }
}
