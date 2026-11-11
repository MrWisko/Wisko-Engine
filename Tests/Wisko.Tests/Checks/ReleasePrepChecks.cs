using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// First public release preparation, without a window: the New World screen's template pictures (present at every
/// density, real renders, matching the templates as they are now, the right density for a display scale, a layout that
/// fits any window), and the other release improvements' engine logic.
/// </summary>
internal static class ReleasePrepChecks
{
    public static void TemplatePreviews(TestReport report)
    {
        var manifest = Engine.Projects.TemplatePreviews.LoadManifest();
        report.Check("Template previews: the manifest is embedded (version 1, 320 x 180, densities 1x 2x 3x, both templates)",
            manifest is { Version: 1, Width: 320, Height: 180 } && manifest.Densities.SequenceEqual([1, 2, 3])
            && Engine.Projects.TemplatePreviews.Templates.All(t => manifest.Templates.ContainsKey(t.ToString())),
            manifest is null ? "missing" : string.Join(", ", manifest.Templates.Keys));

        // Every picture: decodes, at exactly its density's size, and is a real render (not one colour, not blank).
        var problems = new List<string>();
        foreach (var template in Engine.Projects.TemplatePreviews.Templates)
        {
            foreach (var density in Engine.Projects.TemplatePreviews.Densities)
            {
                var name = Engine.Projects.TemplatePreviews.FileName(template, density);
                if (!Engine.Projects.TemplatePreviews.TryLoad(template, density, out var rgba, out var width, out var height))
                {
                    problems.Add($"{name} missing");
                    continue;
                }

                if (width != 320 * density || height != 180 * density)
                {
                    problems.Add($"{name} is {width} x {height}");
                }

                var (mean, deviation) = LuminanceStats(rgba);
                if (deviation < 8.0 || mean is < 20.0 or > 245.0)
                {
                    problems.Add($"{name} looks blank (mean {mean:F0}, deviation {deviation:F1})");
                }
            }
        }

        report.Check("Template previews: six pictures (Classic and Testing World at 1x, 2x, 3x), each its exact size and a real render",
            problems.Count == 0, string.Join("; ", problems));

        // What the pictures show must still be what the templates give: a changed template without new pictures fails.
        var classic = Engine.Projects.TemplatePreviews.ContentFingerprint([]);
        var testing = Engine.Projects.TemplatePreviews.ContentFingerprint(TestingWorldParts());
        report.Check("Template previews: they show the templates as they are now (Classic: no parts; Testing World: its parts; a new world's settings)",
            manifest is not null && manifest.Templates["Classic"].Content == classic && manifest.Templates["Testing"].Content == testing,
            $"classic {classic} (pictures {manifest?.Templates["Classic"].Content}), testing {testing} (pictures {manifest?.Templates["Testing"].Content}); "
                + "regenerate with dotnet run --project Tests/Wisko.Tests -- --make-template-previews");

        report.Check("Template previews: the density for a display scale is the smallest at least as large (100% 1x, 125-200% 2x, above 3x)",
            Engine.Projects.TemplatePreviews.DensityFor(1f) == 1 && Engine.Projects.TemplatePreviews.DensityFor(1.25f) == 2
            && Engine.Projects.TemplatePreviews.DensityFor(1.5f) == 2 && Engine.Projects.TemplatePreviews.DensityFor(2f) == 2
            && Engine.Projects.TemplatePreviews.DensityFor(2.5f) == 3 && Engine.Projects.TemplatePreviews.DensityFor(3f) == 3
            && Engine.Projects.TemplatePreviews.DensityFor(4f) == 3);

        // The cards fit the window at every size: inside it, not overlapping, the picture never larger than drawn.
        var layoutProblems = new List<string>();
        foreach (var (w, h) in new[] { (1920, 1080), (1280, 720), (1024, 640), (800, 600), (700, 500), (640, 480) })
        {
            var layout = ProjectHubRenderer.TemplateLayoutFor(w, h);
            var cards = Enumerable.Range(0, 2).Select(i => CardOf(layout, i)).ToArray();
            var inside = cards.All(c => c.X >= 0f && c.Y >= 0f && c.X + c.Width <= w + 0.5f && c.Y + c.Height <= h + 0.5f);
            var apart = !Overlaps(cards[0], cards[1]);
            if (!inside || !apart || layout.Scale > 1f)
            {
                layoutProblems.Add($"{w} x {h}: {layout.Columns} columns at {layout.Scale:F2}, inside {inside}, apart {apart}");
            }
        }

        var wide = ProjectHubRenderer.TemplateLayoutFor(1280, 720);
        report.Check("Template previews: the cards fit every window from 1920 x 1080 to 640 x 480 (side by side at full size on 1280 x 720)",
            layoutProblems.Count == 0 && wide is { Columns: 2, Scale: 1f }, string.Join("; ", layoutProblems));
    }

    /// <summary>
    /// The deletable baseplate: the world's record of it, Scene.json (old and new forms, saved byte-identically while the
    /// baseplate is there), the editor's rules that assumed it (staying above it, resting on it, spawning above it), undo
    /// and redo, and Play (no collider: a falling Part keeps falling, a Lua raycast finds nothing; with it, IsEnvironment).
    /// </summary>
    public static void Baseplate(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        var world = new Workspace();
        var version = world.Version;
        var deleted = world.SetEnvironmentPresent(Workspace.BaseplateName, false);
        var again = world.SetEnvironmentPresent(Workspace.BaseplateName, false);
        report.Check("Baseplate: a world has it until deleted; deleting is recorded once and moves the workspace version",
            deleted && !again && !world.HasBaseplate && world.Version > version && world.RemovedEnvironment.SequenceEqual([Workspace.BaseplateName]));
        world.SetEnvironmentPresent(Workspace.BaseplateName, true);
        report.Check("Baseplate: restoring it makes the world whole again", world.HasBaseplate && world.RemovedEnvironment.Count == 0);

        // Scene.json: a world with its baseplate saves exactly as before (the plain list); without it, the Environment form.
        var scene = new Workspace();
        scene.AddPart(new Part("Crate", new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 4f), new Vector3(0.6f, 0.4f, 0.2f)));
        var withPath = Path.Combine(dir, "with.json");
        var withoutPath = Path.Combine(dir, "without.json");
        SceneFile.TrySave(scene, withPath, out _);
        var sameAsBefore = File.ReadAllText(withPath) == SceneFile.ToJson(scene.Parts) && File.ReadAllText(withPath).TrimStart().StartsWith('[');
        scene.SetEnvironmentPresent(Workspace.BaseplateName, false);
        SceneFile.TrySave(scene, withoutPath, out _);
        var withoutText = File.ReadAllText(withoutPath);
        var loadedWith = SceneFile.TryLoadScene(withPath, null, out var withContents, out _);
        var loadedWithout = SceneFile.TryLoadScene(withoutPath, null, out var withoutContents, out var withoutError);
        var plainLoad = SceneFile.TryLoad(withoutPath, out var plainParts, out _);
        report.Check("Baseplate: Scene.json keeps the plain list while the baseplate is there (byte-identical to before)", sameAsBefore);
        report.Check("Baseplate: a world without it saves { Environment: { Baseplate: false }, Parts } and reopens without it, Parts intact",
            withoutText.Contains("\"Environment\"") && withoutText.Contains("\"Baseplate\": false") && loadedWithout
            && withoutContents.RemovedEnvironment.SequenceEqual([Workspace.BaseplateName]) && withoutContents.Parts is [{ Name: "Crate" }]
            && plainLoad && plainParts is [{ Name: "Crate" }], withoutError ?? withoutText);
        report.Check("Baseplate: an old Scene.json (a plain list) opens with its baseplate", loadedWith && withContents.RemovedEnvironment.Count == 0 && withContents.Parts.Count == 1);
        var emptyPath = Path.Combine(dir, "empty.json");
        var empty = new Workspace();
        empty.SetEnvironmentPresent(Workspace.BaseplateName, false);
        SceneFile.TrySave(empty, emptyPath, out _);
        report.Check("Baseplate: an entirely empty world (no Parts, no baseplate) saves and reopens empty",
            SceneFile.TryLoadScene(emptyPath, null, out var emptyContents, out _) && emptyContents.Parts.Count == 0 && emptyContents.RemovedEnvironment.Count == 1);

        // The editor's rules that assumed a baseplate.
        var below = new Workspace();
        var low = new Part("Low", new Vector3(0f, -5f, 0f), new Vector3(2f, 2f, 2f), new Vector3(0.5f, 0.5f, 0.5f)) { Anchored = true };
        below.AddPart(low);
        var lifted = PartCollision.TryApplyPosition(low, new Vector3(0f, -5f, 0f), below, out var withFloor);
        below.SetEnvironmentPresent(Workspace.BaseplateName, false);
        var free = PartCollision.TryApplyPosition(low, new Vector3(0f, -5f, 0f), below, out var withoutFloor);
        report.Check("Baseplate: the editor keeps Parts above it while it is there; without it a Part may go below Y 0",
            lifted && MathF.Abs(withFloor.Y - 1f) < 1e-3f && free && MathF.Abs(withoutFloor.Y + 5f) < 1e-3f, $"{withFloor.Y} / {withoutFloor.Y}");
        var down = new Ray(new Vector3(30f, 20f, 30f), new Vector3(0f, -1f, 0f));
        var surfaceWith = SurfaceRaycast.TryCast(down, new Workspace(), (Part?)null, out var groundHit);
        var surfaceWithout = SurfaceRaycast.TryCast(down, below, (Part?)null, out _);
        report.Check("Baseplate: a drag rests on it while it is there; without it there is no ground to rest on",
            surfaceWith && groundHit.Kind == SurfaceKind.Baseplate && !surfaceWithout);

        // Undo and redo of the deletion (the editor's command).
        var history = new EditorHistory();
        var undoWorld = new Workspace();
        undoWorld.SetEnvironmentPresent(Workspace.BaseplateName, false);
        history.Push(EditorCommand.EnvironmentChange(Workspace.BaseplateName, present: false));
        var renderer = new PartRenderer();
        var selection = new EditorSelection();
        history.TryUndo(undoWorld, renderer, selection, out var undoMessage);
        var restored = undoWorld.HasBaseplate;
        history.TryRedo(undoWorld, renderer, selection, out var redoMessage);
        report.Check("Baseplate: undo brings it back, redo deletes it again (\"Delete Baseplate\")",
            restored && !undoWorld.HasBaseplate && undoMessage == "Delete Baseplate" && redoMessage == "Delete Baseplate", $"{undoMessage} / {redoMessage}");

        // Play: the collider follows the world; a Lua raycast reports the environment only when it is there.
        foreach (var present in new[] { true, false })
        {
            var root = GameplayV5Checks.Project(Path.Combine(dir, present ? "play-with" : "play-without"), ("ProbeGround", """
                function Start()
                  local hit = Physics.Raycast(Vector3.new(40, 30, 40), Vector3.new(0, -1, 0), 100)
                  chk("ground_" .. tostring(hit ~= nil and hit.IsEnvironment == true), true)
                end
                """));
            var playWorld = new Workspace();
            var ball = GameplayV5Checks.Add(playWorld, "Ball", new Vector3(0f, 3f, 0f), new Vector3(2f, 2f, 2f), anchored: false);
            GameplayV5Checks.AddProbe(playWorld, "ProbeGround");
            playWorld.SetEnvironmentPresent(Workspace.BaseplateName, present);
            var session = GameplayV5Checks.Begin(root, playWorld);
            GameplayV5Checks.Run(session, 90);
            var y = ball.Position.Y;
            var reports = TestSupport.LuaReports(session.Log);
            GameplayV5Checks.End(session, playWorld);
            report.Check(present
                    ? "Baseplate in Play: a falling Part lands on it; a downward Lua raycast hits the environment"
                    : "Baseplate deleted, in Play: a falling Part keeps falling (below Y 0); a downward Lua raycast hits nothing",
                (present ? MathF.Abs(y - 1f) < 0.1f : y < -1f) && reports.ContainsKey(present ? "ground_true" : "ground_false"),
                $"ball Y {y:F2}; {string.Join(", ", reports.Keys)}");
        }
    }

    /// <summary>
    /// Numeric rotation: typed degrees (decimal, negative, comma, degree sign; refused text), normalization to the gizmo's
    /// [0, 360) without changing the orientation, display, exact 90 degree turns, typing again replacing rather than adding,
    /// every axis, the gizmo readout, undo / redo on several Parts, and saving and reloading.
    /// </summary>
    public static void Rotation(TestReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        float Parsed(string text) => RotationInput.TryParse(text, out var v) ? v : float.NaN;
        report.Check("Rotation input: 22.5, -90, +45, 1,5 (comma), 90° and 1e3 are degrees",
            Parsed("22.5") == 22.5f && Parsed("-90") == -90f && Parsed("+45") == 45f && Parsed("1,5") == 1.5f && Parsed("90°") == 90f && Parsed(" 180 ") == 180f
            && Parsed("1e3") == 1000f);
        report.Check("Rotation input: empty text, letters, NaN, infinity, \"--\" and 1e9 are refused",
            new[] { "", "abc", "NaN", "Infinity", "--", "1e9", "4.5.6" }.All(t => !RotationInput.TryParse(t, out _)));
        report.Check("Rotation: stored as the gizmo stores it, in [0, 360): -90 -> 270, 360 -> 0, 720.5 -> 0.5, -22.5 -> 337.5, 22.5 kept",
            RotationInput.Normalize(-90f) == 270f && RotationInput.Normalize(360f) == 0f && RotationInput.Normalize(720.5f) == 0.5f
            && RotationInput.Normalize(-22.5f) == 337.5f && RotationInput.Normalize(22.5f) == 22.5f && RotationInput.Normalize(-1e-7f) is >= 0f and < 360f);
        report.Check("Rotation display: 0, 22.5, 37.28 (two decimals), 359.999 shows 0, never -0",
            RotationInput.Format(0f) == "0" && RotationInput.Format(22.5f) == "22.5" && RotationInput.Format(37.28461f) == "37.28"
            && RotationInput.Format(359.999f) == "0" && RotationInput.Format(-0.001f) == "0");

        // Normalizing never changes the orientation: the same matrix (to float precision) for every axis.
        var same = true;
        foreach (var degrees in new[] { -90f, -22.5f, -180f, 450f, -0.25f })
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var a = Matrix4.CreateRotationEuler(RotationInput.WithAxis(new Vector3(10f, 20f, 30f), axis, degrees));
                var b = Matrix4.CreateRotationEuler(RotationInput.WithAxis(new Vector3(10f, 20f, 30f), axis, RotationInput.Normalize(degrees)));
                same &= MaxDifference(a, b) < 1e-5f;
            }
        }

        report.Check("Rotation: a normalized value is the same orientation on every axis (no jump when -90 is stored as 270)", same);

        // Exact quarter turns: 90 about Y turns +X into -Z (Wisko's convention) with no drift; typing 22.5 again stays 22.5.
        var quarter = TransformDirection(Matrix4.CreateRotationEuler(new Vector3(0f, 90f, 0f)), new Vector3(1f, 0f, 0f));
        var repeated = new Vector3(0f, 0f, 0f);
        for (var i = 0; i < 10; i++)
        {
            repeated = RotationInput.WithAxis(repeated, 1, RotationInput.Normalize(22.5f));
        }

        report.Check("Rotation: exactly 90 degrees about Y is a clean quarter turn; typing 22.5 ten times leaves 22.5 (a value, not a step)",
            MathF.Abs(quarter.X) < 1e-6f && MathF.Abs(MathF.Abs(quarter.Z) - 1f) < 1e-6f && repeated.Equals(new Vector3(0f, 22.5f, 0f)), $"{quarter}");
        report.Check("Rotation: setting one axis keeps the other two exactly",
            RotationInput.WithAxis(new Vector3(1.25f, 2.5f, 3.75f), 0, 9f).Equals(new Vector3(9f, 2.5f, 3.75f))
            && RotationInput.WithAxis(new Vector3(1.25f, 2.5f, 3.75f), 2, 9f).Equals(new Vector3(1.25f, 2.5f, 9f)));

        var a1 = new Part("A", new Vector3(0f, 2f, 0f), new Vector3(2f, 2f, 2f), new Vector3(1f, 0f, 0f), new Vector3(0f, 45f, 0f));
        var b1 = new Part("B", new Vector3(5f, 2f, 0f), new Vector3(2f, 2f, 2f), new Vector3(0f, 1f, 0f), new Vector3(10f, 45f, 0f));
        report.Check("Rotation fields: one value when the Parts agree, — when they differ",
            RotationInput.FormatAxis([a1, b1], 1) == "45" && RotationInput.FormatAxis([a1, b1], 0) == "—");
        report.Check("Rotation readout near the gizmo: \"Y 45°\" for a ring, both axes for a diagonal arc",
            DimensionLabelRenderer.RotateAngleLabel(new Vector3(30f, 45f, 0f), RotateHandle.Y) == "Y 45°"
            && DimensionLabelRenderer.RotateAngleLabel(new Vector3(30f, 45f, 0f), RotateHandle.Purple) == "X 30°  Y 45°");

        // A typed value on two Parts is one command holding both Parts' transforms before and after (the editor's real
        // Ctrl+Z / Ctrl+Y on it are in the GUI flow: undoing redraws the selection, which needs a window).
        var world = new Workspace();
        world.AddPart(a1);
        world.AddPart(b1);
        var before = new[] { PartSnapshot.FromPart(a1), PartSnapshot.FromPart(b1) };
        a1.Rotation = RotationInput.WithAxis(a1.Rotation, 2, RotationInput.Normalize(-22.5f));
        b1.Rotation = RotationInput.WithAxis(b1.Rotation, 2, RotationInput.Normalize(-22.5f));
        var after = new[] { PartSnapshot.FromPart(a1), PartSnapshot.FromPart(b1) };
        var command = EditorCommand.GroupRotate([a1, b1], before, after);
        command.GroupMoveBefore![0].ApplyTransform(a1);
        command.GroupMoveBefore[1].ApplyTransform(b1);
        var undone = a1.Rotation.Z == 0f && b1.Rotation.Z == 0f && a1.Rotation.Y == 45f;
        command.GroupMoveAfter![0].ApplyTransform(a1);
        command.GroupMoveAfter[1].ApplyTransform(b1);
        report.Check("Rotation: a typed value on two Parts is one step (\"Rotate 2 parts\") restoring both before, both 337.5 after",
            command.GetActionLabel() == "Rotate" && command.PartName == "2 parts" && command.GroupMoveParts!.Count == 2
            && undone && a1.Rotation.Z == 337.5f && b1.Rotation.Z == 337.5f, $"{command.GetActionLabel()} {command.PartName}");
        var path = Path.Combine(dir, "Scene.json");
        a1.Rotation = new Vector3(0.25f, 22.5f, 337.5f);
        SceneFile.TrySave(world, path, out _);
        var reloaded = SceneFile.TryLoad(path, out var parts, out _) ? parts.First(p => p.Name == "A").Rotation : default;
        report.Check("Rotation: saving and reloading keeps the exact numbers (0.25, 22.5, 337.5)", reloaded.Equals(new Vector3(0.25f, 22.5f, 337.5f)), $"{reloaded}");
    }

    /// <summary>
    /// Dragging a Part's body: no jump when the drag begins (grabbed off-centre, or in the air), the grab offset kept while
    /// following the cursor, the level plane when nothing is under the cursor (no baseplate), groups, refusal on overlap,
    /// and Escape putting everything back.
    /// </summary>
    public static void BodyDrag(TestReport report)
    {
        var camera = new EditorCamera { Position = new Vector3(0f, 10f, 10f) };
        Ray Through(Vector3 point) => new(camera.Position, (point - camera.Position).Normalized());
        static float Gap(Vector3 a, Vector3 b) => (a - b).Length;

        // Resting on the baseplate, grabbed off-centre on its top face: the first move does not jump.
        var world = new Workspace();
        var crate = new Part("Crate", new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f), new Vector3(0.6f, 0.4f, 0.2f)) { Anchored = true };
        world.AddPart(crate);
        var grab = new Vector3(0.6f, 2f, 0.4f);
        var dragger = new PartDragger();
        var oldTarget = SurfaceRaycast.TryCast(Through(grab), world, crate, out var underPress) ? SurfaceRaycast.ComputePlacementPosition(crate, underPress) : crate.Position;
        dragger.BeginDrag(crate, Through(grab), grab, 100f, 100f);
        dragger.UpdateDrag(Through(grab), world, 100.5f, 100f);
        var stayed = Gap(crate.Position, new Vector3(0f, 1f, 0f));
        report.Check("Body drag: grabbed off-centre, the first move keeps the Part where it was (before: it jumped to rest under the cursor)",
            stayed < 1e-3f && Gap(oldTarget, new Vector3(0f, 1f, 0f)) > 1f, $"moved {stayed:F4}; the old placement was {Gap(oldTarget, new Vector3(0f, 1f, 0f)):F2} away");

        // Following the cursor: aiming 4 studs further along X moves it along X, still resting on the ground.
        dragger.UpdateDrag(Through(grab + new Vector3(4f, 0f, 0f)), world, 140f, 100f);
        report.Check("Body drag: it follows the cursor, keeping its grab point and resting on the ground",
            crate.Position.X > 3f && MathF.Abs(crate.Position.Y - 1f) < 0.01f, $"{crate.Position.X:F2} {crate.Position.Y:F2} {crate.Position.Z:F2}");
        var cancelled = dragger.CancelDrag();
        report.Check("Body drag: Escape (cancel) puts it back where the drag began", cancelled && Gap(crate.Position, new Vector3(0f, 1f, 0f)) < 1e-6f && !dragger.IsDragging);

        // In the air: it keeps its height above what is under the cursor (no drop on the first move).
        var floating = new Workspace();
        var balloon = new Part("Balloon", new Vector3(0f, 6f, 0f), new Vector3(2f, 2f, 2f), new Vector3(1f, 0f, 0f)) { Anchored = true };
        floating.AddPart(balloon);
        var top = new Vector3(0.2f, 7f, 0.2f);
        dragger.BeginDrag(balloon, Through(top), top, 100f, 100f);
        dragger.UpdateDrag(Through(top), floating, 100.5f, 100f);
        var first = balloon.Position;
        dragger.UpdateDrag(Through(top + new Vector3(2f, 0f, 0f)), floating, 120f, 100f);
        dragger.EndDrag();
        report.Check("Body drag: a Part in the air does not drop when grabbed; it keeps its height while moved",
            Gap(first, new Vector3(0f, 6f, 0f)) < 1e-3f && MathF.Abs(balloon.Position.Y - 6f) < 0.01f && balloon.Position.X > 0.5f,
            $"first {first.X:F2} {first.Y:F2}, then {balloon.Position.X:F2} {balloon.Position.Y:F2}");

        // No baseplate, nothing under the cursor: the grab point follows the cursor on its own level.
        var empty = new Workspace();
        empty.SetEnvironmentPresent(Workspace.BaseplateName, false);
        var block = new Part("Block", new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f), new Vector3(0f, 0f, 1f)) { Anchored = true };
        empty.AddPart(block);
        dragger.BeginDrag(block, Through(grab), grab, 100f, 100f);
        dragger.UpdateDrag(Through(grab + new Vector3(3f, 0f, -1f)), empty, 130f, 90f);
        dragger.EndDrag();
        report.Check("Body drag without a baseplate: the grab point follows the cursor on its own level (exactly +3 X, -1 Z, same height)",
            Gap(block.Position, new Vector3(3f, 1f, -1f)) < 1e-3f, $"{block.Position.X:F3} {block.Position.Y:F3} {block.Position.Z:F3}");

        // A group moves together; a move into another Part is refused (the last good place kept).
        var pair = new Workspace();
        var left = new Part("Left", new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f), new Vector3(1f, 1f, 1f)) { Anchored = true };
        var right = new Part("Right", new Vector3(3f, 1f, 0f), new Vector3(2f, 2f, 2f), new Vector3(1f, 1f, 1f)) { Anchored = true };
        var wall = new Part("Wall", new Vector3(0f, 1f, -8f), new Vector3(10f, 2f, 1f), new Vector3(0.5f, 0.5f, 0.5f)) { Anchored = true };
        pair.AddPart(left);
        pair.AddPart(right);
        pair.AddPart(wall);
        dragger.BeginDrag(left, Through(grab), grab, 100f, 100f, [left, right]);
        dragger.UpdateDrag(Through(grab + new Vector3(1f, 0f, 2f)), pair, 110f, 120f);
        var together = Gap(right.Position - left.Position, new Vector3(3f, 0f, 0f)) < 1e-4f && left.Position.Z > 1f;
        var beforeWall = left.Position;
        dragger.UpdateDrag(Through(new Vector3(0.6f, 0f, -8f)), pair, 100f, 60f);
        dragger.EndDrag();
        report.Check("Body drag: a selection moves together; a place that would overlap another Part is refused (the last good one kept)",
            together && !left.Position.Equals(new Vector3(0.6f, 1f, -8f)) && !PartCollision.Overlaps(left, wall) && !PartCollision.Overlaps(right, wall),
            $"left {beforeWall.X:F2},{beforeWall.Z:F2} -> {left.Position.X:F2},{left.Position.Z:F2}");
    }

    private static float MaxDifference(Matrix4 a, Matrix4 b) =>
        new[] { a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23, a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33 }
            .Max(MathF.Abs);

    private static Vector3 TransformDirection(Matrix4 m, Vector3 v) =>
        new(m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z, m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z, m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

    // The Testing World's Parts as the editor host builds them.
    internal static List<Part> TestingWorldParts()
    {
        var gameType = Type.GetType("Wisko.Editor.WiskoEditorModule, Wisko.Editor", throwOnError: true)!;
        var provider = (IWorkspaceProvider)Activator.CreateInstance(gameType, nonPublic: true)!;
        var workspace = new Workspace();
        provider.BuildWorkspace(workspace);
        return workspace.Parts.ToList();
    }

    private static UiBox CardOf(ProjectHubRenderer.TemplateLayout layout, int index) =>
        (UiBox)typeof(ProjectHubRenderer).GetMethod("TemplateCardFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [index, layout])!;

    private static bool Overlaps(UiBox a, UiBox b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static (double Mean, double Deviation) LuminanceStats(byte[] rgba)
    {
        double sum = 0, squares = 0;
        var count = rgba.Length / 4;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var y = 0.2126 * rgba[i] + 0.7152 * rgba[i + 1] + 0.0722 * rgba[i + 2];
            sum += y;
            squares += y * y;
        }

        var mean = sum / count;
        return (mean, Math.Sqrt(Math.Max(0.0, squares / count - mean * mean)));
    }
}
