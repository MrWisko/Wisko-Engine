using System.Diagnostics;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// First public release: the five improvements together and under load, without a window. A thousand Parts dragged,
/// cancelled, rotated by number and saved without a baseplate; and one world that uses all of it at once in Play
/// (no baseplate, a typed rotation, chat), saved and read back. Timings are reported as information, measured on this
/// machine; the checks are about results.
/// </summary>
internal static class ReleasePrepIntegrationChecks
{
    private static Workspace Grid(int count, bool baseplate)
    {
        var workspace = new Workspace();
        if (!baseplate)
        {
            workspace.SetEnvironmentPresent(Workspace.BaseplateName, false);
        }

        var side = (int)MathF.Ceiling(MathF.Sqrt(count));
        for (var i = 0; i < count; i++)
        {
            workspace.AddPart(new Part($"P{i}", new Vector3((i % side) * 3f, 1f, (i / side) * 3f), new Vector3(2f, 2f, 2f), new Vector3(0.5f, 0.5f, 0.5f)) { Anchored = true });
        }

        return workspace;
    }

    public static void Stress(TestReport report, string dir)
    {
        const int count = 1000;
        var camera = new EditorCamera { Position = new Vector3(45f, 60f, 140f) };
        Ray Through(Vector3 point) => new(camera.Position, (point - camera.Position).Normalized());

        // All 1,000 selected, dragged by one of them (grab on its top face) over 60 updates, then dropped.
        var world = Grid(count, baseplate: true);
        var parts = world.Parts.ToList();
        var starts = parts.Select(p => p.Position).ToList();
        var leader = parts[0];
        var grab = leader.Position + new Vector3(0.3f, 1f, 0.3f);
        var dragger = new PartDragger();
        dragger.BeginDrag(leader, Through(grab), grab, 100f, 100f, parts);
        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= 60; i++)
        {
            dragger.UpdateDrag(Through(grab + new Vector3(i * 0.1f, 0f, 0f)), world, 100f + i, 100f);
        }

        watch.Stop();
        dragger.EndDrag();
        var offset = leader.Position - starts[0];
        var together = parts.Select((p, i) => (p.Position - starts[i] - offset).Length).Max();
        report.Check($"Stress: {count:N0} selected Parts dragged by one body move together (same offset for all)",
            offset.Length > 1f && together < 1e-3f, $"offset {offset.X:F2} {offset.Y:F2} {offset.Z:F2}, largest difference {together:F5}");
        TestReport.Info($"stress: {count:N0}-Part group body drag: {watch.Elapsed.TotalMilliseconds / 60:F2} ms per update (60 updates)");

        // One Part dragged among 999 others (each move checked against them), then cancelled: everything as it was.
        var moved = parts.Select(p => p.Position).ToList();
        var single = parts[count / 2];
        var singleGrab = single.Position + new Vector3(0f, 1f, 0f);
        dragger.BeginDrag(single, Through(singleGrab), singleGrab, 100f, 100f);
        watch.Restart();
        for (var i = 1; i <= 60; i++)
        {
            dragger.UpdateDrag(Through(singleGrab + new Vector3(0f, 0f, i * 0.05f)), world, 100f, 100f + i);
        }

        watch.Stop();
        var cancelled = dragger.CancelDrag();
        report.Check($"Stress: one Part dragged among {count - 1:N0} others, then cancelled: every Part exactly where it was",
            cancelled && parts.Select((p, i) => (p.Position - moved[i]).Length).Max() == 0f);
        TestReport.Info($"stress: single body drag among {count - 1:N0} Parts: {watch.Elapsed.TotalMilliseconds / 60:F2} ms per update (60 updates)");

        // Typed rotation on all of them: one undo step's worth of snapshots, values as typed (normalized).
        watch.Restart();
        var before = parts.Select(PartSnapshot.FromPart).ToList();
        foreach (var part in parts)
        {
            part.Rotation = RotationInput.WithAxis(part.Rotation, 1, RotationInput.Normalize(-22.5f));
        }

        var after = parts.Select(PartSnapshot.FromPart).ToList();
        var command = EditorCommand.GroupRotate(parts, before, after);
        var shown = RotationInput.FormatAxis(parts, 1);
        watch.Stop();
        report.Check($"Stress: Y = -22.5 typed for {count:N0} Parts: one Rotate step holding them all, each at 337.5, the field shows 337.5",
            command.Kind == EditorActionKind.GroupRotate && parts.All(p => p.Rotation.Y == 337.5f) && shown == "337.5", $"field '{shown}'");
        TestReport.Info($"stress: typed rotation for {count:N0} Parts (snapshots, command, field text): {watch.Elapsed.TotalMilliseconds:F1} ms");

        // Saved without a baseplate and read back.
        world.SetEnvironmentPresent(Workspace.BaseplateName, false);
        var path = Path.Combine(dir, "stress-scene", "Scene.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        watch.Restart();
        var saved = SceneFile.TrySave(world, path, out var saveError);
        var loaded = SceneFile.TryLoadScene(path, null, out var contents, out var loadError);
        watch.Stop();
        report.Check($"Stress: {count:N0} Parts without a baseplate saved and read back (positions, rotations, no baseplate)",
            saved && loaded && contents.Parts.Count == count && contents.RemovedEnvironment.SequenceEqual([Workspace.BaseplateName])
            && contents.Parts.Select((p, i) => (p.Position - parts[i].Position).Length).Max() < 1e-4f && contents.Parts.All(p => p.Rotation.Y == 337.5f),
            saveError ?? loadError);
        TestReport.Info($"stress: Scene.json for {count:N0} Parts ({new FileInfo(path).Length / 1024} KB) saved and read in {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private const string AllTogether = """
        local frame = 0
        local heard = {}
        function Start()
          Chat.OnMessage(function(m) heard[#heard + 1] = m end)
          local turned = Scene.Find("Turned")
          chk("typed_rotation_in_play", math.abs(turned.Transform.Rotation.y - 337.5) < 1e-3)
          local hit = Physics.Raycast(Vector3.new(30, 10, 30), Vector3.new(0, -1, 0), 100)
          chk("no_baseplate_under_empty_ground", hit == nil)
          Chat.Post("The baseplate is gone; watch the ball fall.")
        end
        function Update()
          frame = frame + 1
          if frame == 1 then
            chk("chat_in_a_world_without_baseplate", heard[1] ~= nil and heard[1].Kind == "System")
            Chat.Send("Ball at y=" .. string.format("%.1f", Scene.Find("Ball").Transform.Position.y))
          elseif frame == 60 then
            chk("ball_falls_without_baseplate", Scene.Find("Ball").Transform.Position.y < -5)
            chk("player_message_delivered", heard[2] ~= nil and heard[2].Kind == "Player" and string.find(heard[2].Text, "Ball at y=", 1, true) == 1)
          end
        end
        """;

    public static void AllFeatures(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "AllTogether");
        WriteScript(root, "Together.lua", ChkFunction + AllTogether);
        var workspace = new Workspace();
        workspace.SetEnvironmentPresent(Workspace.BaseplateName, false);
        var turned = MakePart("Turned", new Vector3(0f, 2f, 0f), new Vector3(2f, 2f, 2f), true, "Assets/Scripts/Together.lua");
        turned.Rotation = RotationInput.WithAxis(turned.Rotation, 1, RotationInput.Normalize(-22.5f));
        workspace.AddPart(turned);
        workspace.AddPart(MakePart("Ball", new Vector3(10f, 5f, 0f), new Vector3(2f, 2f, 2f), false));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        Step(play, 61, faults);
        AssertLuaChecks(report, "All together", log,
            ["typed_rotation_in_play", "no_baseplate_under_empty_ground", "chat_in_a_world_without_baseplate", "ball_falls_without_baseplate", "player_message_delivered"]);
        AssertNoFaults(report, "All together", faults);
        play.Stop(workspace);
        play.Dispose();
        var ball = workspace.Parts.First(p => p.Name == "Ball");
        report.Check("All together: Stop puts the world back (the ball where it was, still no baseplate)",
            (ball.Position - new Vector3(10f, 5f, 0f)).Length < 1e-4f && !workspace.HasBaseplate);

        var path = Path.Combine(root, "Scene.json");
        var saved = SceneFile.TrySave(workspace, path, out var error);
        var loaded = SceneFile.TryLoadScene(path, null, out var contents, out var loadError);
        report.Check("All together: saved and read back with no baseplate, the typed rotation and the script attached",
            saved && loaded && contents.RemovedEnvironment.Contains(Workspace.BaseplateName)
            && contents.Parts.First(p => p.Name == "Turned") is { Rotation.Y: 337.5f, Scripts.Count: 1 },
            error ?? loadError);
    }

    /// <summary>The release showcase: made once (never over an existing project), and its chat script works in Play.</summary>
    public static void Showcase(TestReport report, string dir)
    {
        var projects = Path.Combine(dir, "Projects");
        var made = ReleaseShowcase.Make(projects);
        var project = new Engine.Projects.ProjectStore(projects).ListProjects().FirstOrDefault(p => p.Name == ReleaseShowcase.ProjectName);
        var sceneBefore = project is null ? null : File.ReadAllBytes(project.ScenePath);
        var again = ReleaseShowcase.Make(projects);
        report.Check("Showcase: created once; a second run refuses and changes nothing",
            made == 0 && project is not null && again == 1 && sceneBefore is not null && File.ReadAllBytes(project.ScenePath).SequenceEqual(sceneBefore));
        if (project is null)
        {
            return;
        }

        SceneFile.TryLoadScene(project.ScenePath, null, out var contents, out _);
        var workspace = new Workspace();
        foreach (var part in contents.Parts)
        {
            workspace.AddPart(part);
        }

        report.Check("Showcase: the drag, rotation and chat groups are there, the typed rotations as typed, the baseplate present",
            workspace.Parts.Count == 11 && workspace.HasBaseplate
            && workspace.Parts.First(p => p.Name == "Y -45 (shown as 315)").Rotation.Y == 315f
            && workspace.Parts.Select(p => p.Group?.Name).Distinct().Count() == 3);
        report.Check("Showcase: it uses the Universal renderer, as a world made in the editor does",
            File.Exists(project.GraphicsPath) && Engine.Graphics.GraphicsFile.Load(project.GraphicsPath, out _, out _) is { Renderer: Engine.Graphics.RendererKind.Universal });
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(project.FolderPath, workspace, new GuiDocument(), log, faults);
        Step(play, 2, faults);
        var chat = play.Gameplay!.Chat;
        chat.Send("hello there", null, out _);
        Step(play, 50, faults);
        chat.Send("darn it", null, out _);
        chat.Send("THIS IS VERY LOUD", null, out _);
        Step(play, 2, faults);
        var general = chat.FindChannel("General")!.Recent(20).Select(m => $"{m.SenderName}: {m.Text}").ToList();
        report.Check("Showcase: the chat script runs (welcome, guide answer, filter rewrites, shouting held back, News channel)",
            faults.Count == 0 && play.Gui!.Document.Elements.Any(e => e.Name == "WiskoChat")
            && general.Any(line => line.StartsWith("Guide: Hello, Player!", StringComparison.Ordinal))
            && general.Contains("Player: **** it") && !general.Any(line => line.Contains("LOUD", StringComparison.Ordinal))
            && chat.FindChannel("News") is { PlayersCanSend: false, Count: 1 },
            string.Join(" | ", general));
        play.Stop(workspace);
        play.Dispose();
    }
}
