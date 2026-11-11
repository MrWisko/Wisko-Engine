using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// V4 editor features without a window: World panel groups (operations, undo / redo, Scene.json round trip, older
/// files, Play Mode leaves them alone) and GUI Image elements in UI.json.
/// </summary>
internal static class EditorV4Checks
{
    public static void Groups(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var workspace = new Workspace();
        var a = MakePart("A", new Vector3(0, 1, 0), new Vector3(1, 1, 1), true, "Assets/Scripts/Spin.lua");
        var b = MakePart("B", new Vector3(2, 1, 0), new Vector3(1, 1, 1), false);
        var c = MakePart("C", new Vector3(4, 1, 0), new Vector3(1, 1, 1), true);
        var d = MakePart("D", new Vector3(6, 1, 0), new Vector3(1, 1, 1), true);
        b.MassOverride = 7f;
        b.CanCollide = false;
        foreach (var part in new[] { a, b, c, d })
        {
            workspace.AddPart(part);
        }

        var history = new EditorHistory();
        var renderer = new PartRenderer();
        var selection = new EditorSelection();
        selection.SelectMany([a, b, c]);

        var groupCommand = WorldGrouping.Group(workspace, [a, b, c], selection.SelectedParts, out var group, out var error);
        report.Check("Group: three objects share a new group named \"Group\"",
            groupCommand is not null && group is { Name: "Group", Parent: null } && a.Group == group && b.Group == group && c.Group == group && d.Group is null,
            error);
        history.Push(groupCommand!);
        report.Check("Group: identities, scripts, physics and transforms untouched",
            workspace.Parts.Count == 4 && workspace.Parts[0] == a && a.Scripts.Count == 1 && b.MassOverride == 7f && !b.CanCollide && b.Position.X == 2f);

        var nested = WorldGrouping.Group(workspace, [b, c], selection.SelectedParts, out var inner, out error);
        history.Push(nested!);
        report.Check("Nested group: grouping objects that share a group makes a group inside it",
            inner is { Name: "Group", Parent: { } outer } && outer == group && b.Group == inner && a.Group == group && inner!.Path == "Group/Group", inner?.Path);

        var rename = WorldGrouping.Rename(workspace, group!, "Props", selection.SelectedParts, out error);
        history.Push(rename!);
        report.Check("Rename: the group's name changes and its path follows", group!.Name == "Props" && inner!.Path == "Props/Group", inner!.Path);
        report.Check("Rename: empty, '/' and over-long names are refused",
            WorldGrouping.Rename(workspace, group, "  ", [], out _) is null && WorldGrouping.Rename(workspace, group, "a/b", [], out _) is null
            && WorldGrouping.Rename(workspace, group, new string('x', 49), [], out _) is null);

        var second = WorldGrouping.Group(workspace, [d], selection.SelectedParts, out var other, out _);
        history.Push(second!);
        report.Check("Unique names: a second top-level group is \"Group\" (the first is \"Props\")", other is { Name: "Group", Parent: null }, other?.Name);
        report.Check("Rename: a sibling's name is refused", WorldGrouping.Rename(workspace, other!, "props", [], out error) is null && error is not null, error);

        var intoSelf = WorldGrouping.MoveGroup(workspace, group, inner, [], out error);
        report.Check("Move: a group cannot go inside itself or its own groups", intoSelf is null && error is not null, error);
        var moveGroup = WorldGrouping.MoveGroup(workspace, inner!, other, [], out error);
        history.Push(moveGroup!);
        report.Check("Move: a whole group moves into another group (with its objects)", inner!.Parent == other && b.Group == inner && inner.Path == "Group/Group",
            inner.Path);
        var moveParts = WorldGrouping.MoveParts(workspace, [c], null, [], out error);
        history.Push(moveParts!);
        report.Check("Move: an object moves back to the Workspace", c.Group is null && b.Group == inner);

        var ordered = PartGroups.InOrder(workspace.Parts);
        report.Check("Tree: groups listed parents first, in Workspace order",
            ordered.Count == 3 && ordered[0] == group && ordered[1] == other && ordered[2] == inner, string.Join(", ", ordered.Select(g => g.Path)));

        // Save and reload: memberships come back as shared group objects with the same paths.
        var scenePath = Path.Combine(dir, "Scene.json");
        report.Check("Scene.json: saved with groups", SceneFile.TrySave(workspace, scenePath, out error), error);
        var json = File.ReadAllText(scenePath);
        report.Check("Scene.json: each grouped Part stores its group path; ungrouped Parts store none",
            json.Contains("\"Group\": \"Props\"", StringComparison.Ordinal) && json.Contains("\"Group\": \"Group/Group\"", StringComparison.Ordinal)
            && CountOf(json, "\"Group\":") == 3);
        SceneFile.TryLoad(scenePath, out var loaded, out _);
        var la = loaded.First(p => p.Name == "A");
        var lb = loaded.First(p => p.Name == "B");
        var lc = loaded.First(p => p.Name == "C");
        var ld = loaded.First(p => p.Name == "D");
        report.Check("Reload: hierarchy preserved (Props / Group / Group, C ungrouped)",
            la.Group?.Path == "Props" && lb.Group?.Path == "Group/Group" && ld.Group?.Path == "Group" && lc.Group is null && lb.Group.Parent == ld.Group,
            $"{la.Group?.Path}, {lb.Group?.Path}, {lc.Group?.Path}, {ld.Group?.Path}");
        report.Check("Reload: scripts, physics and transforms preserved",
            la.Scripts.Count == 1 && lb.MassOverride == 7f && !lb.CanCollide && lb.Position.X == 2f && !lb.Anchored);

        // An older Scene.json (no Group fields) still loads, ungrouped; a broken path loads ungrouped.
        File.WriteAllText(Path.Combine(dir, "Old.json"), "[{\"Name\":\"Old\",\"ShapeType\":\"Block\",\"Position\":[0,1,0],\"Rotation\":[0,0,0],\"Size\":[1,1,1],\"Color\":[200,200,200],\"Anchored\":true}," +
            "{\"Name\":\"Odd\",\"ShapeType\":\"Block\",\"Position\":[0,1,0],\"Rotation\":[0,0,0],\"Size\":[1,1,1],\"Color\":[200,200,200],\"Anchored\":true,\"Group\":\"A//B\"}]");
        var oldLoaded = SceneFile.TryLoad(Path.Combine(dir, "Old.json"), out var oldParts, out error);
        report.Check("Older Scene.json without groups loads ungrouped; an unusable path loads ungrouped",
            oldLoaded && oldParts.Count == 2 && oldParts.All(p => p.Group is null), error);

        // Undo walks every step back (memberships, nesting, names); redo replays them.
        var steps = history.UndoCount;
        for (var i = 0; i < steps; i++)
        {
            history.TryUndo(workspace, renderer, selection, out _);
        }

        report.Check("Undo: every grouping step undone (no groups left, names restored)",
            workspace.Parts.All(p => p.Group is null) && group.Name == "Group", $"{a.Group?.Path}, {b.Group?.Path}, {c.Group?.Path}, {d.Group?.Path}");
        for (var i = 0; i < steps; i++)
        {
            history.TryRedo(workspace, renderer, selection, out _);
        }

        report.Check("Redo: every step replayed",
            a.Group?.Path == "Props" && b.Group?.Path == "Group/Group" && c.Group is null && d.Group?.Path == "Group",
            $"{a.Group?.Path}, {b.Group?.Path}, {c.Group?.Path}, {d.Group?.Path}");

        // Ungroup moves the contents up (an inner group that would clash with a sibling is renamed).
        var ungroup = WorldGrouping.Ungroup(workspace, other!, [], out error);
        report.Check("Ungroup: the group's objects and inner groups move up to where it was",
            ungroup is not null && d.Group is null && inner.Parent is null && inner.Name == "Group" && b.Group == inner, $"{inner.Path}, {error}");
        report.Check("Ungroup: nothing in it is refused", WorldGrouping.Ungroup(workspace, new PartGroup("Empty"), [], out error) is null && error is not null);

        // Duplicating keeps the group (Ctrl+D inside a group stays in it).
        var duplicate = PartDuplicatorProxy.Duplicate(a, workspace);
        report.Check("Duplicate: a duplicate stays in its source's group", duplicate.Group == a.Group);

        // Play Mode: grouping changes nothing about physics, and Stop leaves memberships as they were.
        var root = Path.Combine(dir, "Proj");
        var play = new Workspace();
        var floor = MakePart("Floor", new Vector3(0, 0, 0), new Vector3(40, 2, 40), true);
        var grouped = MakePart("Grouped", new Vector3(-3, 10, 0), new Vector3(2, 2, 2), false);
        var loose = MakePart("Loose", new Vector3(3, 10, 0), new Vector3(2, 2, 2), false);
        var playGroup = new PartGroup("Falling");
        grouped.Group = playGroup;
        floor.Group = playGroup;
        foreach (var part in new[] { floor, grouped, loose })
        {
            play.AddPart(part);
        }

        var faults = new List<PlayScriptFault>();
        var controller = StartPlay(root, play, null, new OutputLog(), faults);
        Step(controller, 120, faults);
        report.Check("Play: a grouped Part falls exactly like an ungrouped one (groups do nothing to physics)",
            MathF.Abs(grouped.Position.Y - loose.Position.Y) < 1e-4f && MathF.Abs(grouped.Position.Y - 2f) < 0.1f, $"{grouped.Position.Y:F4} vs {loose.Position.Y:F4}");
        controller.Stop(play);
        controller.Dispose();
        report.Check("Play: Stop restores the scene with memberships intact", grouped.Group == playGroup && floor.Group == playGroup && grouped.Position.Y == 10f);
    }

    public static void GuiImages(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var gui = new GuiDocument();
        var image = gui.Add(new GuiImage { Name = "Coin", Image = "Assets/Images/Coin.png" });
        var path = Path.Combine(dir, "UI.json");
        report.Check("UI.json: an Image element saves", GuiFile.TrySave(path, gui, out var error), error);
        var back = GuiFile.Load(path, out var guiReport);
        var loaded = back.Elements.OfType<GuiImage>().FirstOrDefault();
        report.Check("UI.json: the Image element loads back with its picture path",
            guiReport.Status != GuiLoadStatus.Invalid && loaded is { Name: "Coin", Image: "Assets/Images/Coin.png" } && image.Type == GuiElementType.Image);

        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Hud.lua", ChkFunction + """
            function Start()
              local panel = UI.Create("Frame", { Name = "Panel", Position = { 0, 10, 0, 10 }, Size = { 0, 300, 0, 120 }, Color = "#202830", Opacity = 0.8 })
              chk("create_with_options", panel.Name == "Panel" and panel.Color == "#202830" and math.abs(panel.Opacity - 0.8) < 1e-6)
              local px, po = panel:GetPosition()
              chk("create_options_position", px == 0 and po == 10)
              local icon = UI.Create("Image", { Name = "Icon", Parent = panel, Image = "Assets/Images/Coin.png", Size = { 0, 32, 0, 32 } })
              chk("image_element", icon.Type == "Image" and icon.Image == "Assets/Images/Coin.png" and icon.Parent == panel)
              chk("image_path_validated", not pcall(function() icon.Image = "C:/Windows/evil.png" end)
                and not pcall(function() icon.Image = "Assets/Images/Coin.exe" end) and pcall(function() icon.Image = "" end))
              chk("create_unknown_option_errors", not pcall(UI.Create, "Frame", { Bogus = 1 }))
              local count = #UI.Root:GetChildren()
              chk("create_failed_option_removes_element", not pcall(UI.Create, "Text", { Name = "Bad", FontSize = "big" }) and #UI.Root:GetChildren() == count and UI.Find("Bad") == nil)
              local fill = UI.Create("Frame", { Name = "Fill", Parent = panel, Size = { 0.5, 0, 0, 10 } })
              fill:SetSize(0.75, 0, 0, 10)
              local xs = fill:GetSize()
              chk("progress_bar_by_size", xs == 0.75)
              local button = UI.Create("Button", { Name = "Go", Parent = panel, Text = "Go", Enabled = false })
              chk("button_options", button.Text == "Go" and button.Enabled == false)
            end
            """);
        var workspace = new Workspace();
        workspace.AddPart(MakePart("Holder", Vector3.Zero, new Vector3(1, 1, 1), true, "Assets/Scripts/Hud.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var authored = new GuiDocument();
        var play = StartPlay(root, workspace, authored, log, faults);
        Step(play, 2, faults);
        var runtimeCount = play.Gui!.Document.Count;
        play.Stop(workspace);
        play.Dispose();
        // Panel, Icon, Fill and Go (the failed "Bad" was taken away again).
        report.Check("Lua-created GUI lives on the Play copy only; the authored GUI stays empty", runtimeCount == 4 && authored.Count == 0, $"runtime {runtimeCount}");
        var again = StartPlay(root, new Workspace(), authored, new OutputLog(), faults);
        report.Check("A new Play session starts with the authored GUI again (no leftovers, no handlers)",
            again.Gui!.Document.Count == 0 && (again.Runtime?.ClickHandlerCount ?? 0) == 0);
        again.Stop(new Workspace());
        again.Dispose();
        AssertNoFaults(report, "GUI images and options", faults);
        AssertLuaChecks(report, "GUI images and options", log,
        [
            "create_with_options", "create_options_position", "image_element", "image_path_validated", "create_unknown_option_errors",
            "create_failed_option_removes_element", "progress_bar_by_size", "button_options"
        ]);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // PartDuplicator is internal to the engine (visible to the tests).
    private static class PartDuplicatorProxy
    {
        public static Part Duplicate(Part source, Workspace workspace) => PartDuplicator.Duplicate(source, workspace);
    }
}
