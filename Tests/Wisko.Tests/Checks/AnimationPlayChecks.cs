using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>
/// Animation System V1 in headless Play sessions (PlayModeController, as the editor runs them) on real model instances:
/// the Lua animation API, Play on start, Stop restoring everything, destroyed and cloned objects, saved settings (scene
/// file, duplicates, reimport, a missing model file) and the demonstration scene with its character script.
/// </summary>
internal static class AnimationPlayChecks
{
    private static Part ModelPart(string name, ModelAsset asset, Vector3 origin, float scale, ModelAnimationSettings? settings = null, params string[] scripts)
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

    private static PlayModeController Start(string root, Workspace workspace, OutputLog log, List<PlayScriptFault> faults)
    {
        var play = new PlayModeController { Output = log, CameraHost = new StubCamera(), PhysicsEnvironment = LuaV4Demo.Baseplate() };
        if (!play.TryStart(root, workspace, [], null, out var error))
        {
            throw new InvalidOperationException($"PlayModeController.TryStart failed: {error}");
        }

        faults.AddRange(play.TakeNewFaults());
        return play;
    }

    public static void LuaApi(TestReport report, string dir)
    {
        var assets = AnimationDemo.ExampleAssets();
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "RobotProbe.lua", ChkFunction + RobotProbe);
        WriteScript(root, "BlockProbe.lua", ChkFunction + """
            function Start()
              chk("primitive_has_no_animations", self:HasAnimations() == false and #self:GetAnimations() == 0)
              local ok, reason = self:PlayAnimation("Idle")
              chk("primitive_play_answers_false", ok == false and string.find(reason, "only imported models", 1, true) ~= nil)
              chk("primitive_stop_is_harmless", pcall(function() self:StopAnimation() end))
              chk("primitive_current_nil", self:GetCurrentAnimation() == nil and self:IsAnimationPlaying() == false)
              chk("primitive_speed_error", not pcall(function() self:SetAnimationSpeed(1) end))
            end
            """);

        var workspace = new Workspace();
        var robot = ModelPart("Robot", assets("RobotExpressive"), new Vector3(0f, 0f, 0f), 1f, null, "RobotProbe");
        var victim = ModelPart("Victim", assets("Fox"), new Vector3(10f, 0f, 0f), 0.05f, new ModelAnimationSettings { Clip = "Walk", PlayOnStart = true });
        var block = MakePart("Block", new Vector3(-10f, 1f, 0f), new Vector3(2f, 2f, 2f), true, "Assets/Scripts/BlockProbe.lua");
        workspace.AddPart(robot);
        workspace.AddPart(victim);
        workspace.AddPart(block);
        var sceneBefore = SceneFile.ToJson(workspace.Parts);

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = Start(root, workspace, log, faults);
        Step(play, 240, faults);
        AssertLuaChecks(report, "Lua animation API", log, [
            "has_animations", "list_14", "unknown_name_false", "unknown_number_false", "bad_type_error", "bad_option_error", "bad_option_type_error",
            "bad_speed_error", "bad_fade_error", "no_current_yet", "play_by_name", "play_by_number", "length", "time_advances", "pause_holds",
            "resume_and_speed_2", "seek", "crossfade_current", "finished_once", "finished_object", "finished_not_playing", "restart",
            "looping_no_event", "stopped", "stop_again_ok", "speed_needs_clip", "options", "clone_independent", "destroyed_errors",
            "primitive_has_no_animations", "primitive_play_answers_false", "primitive_stop_is_harmless", "primitive_current_nil", "primitive_speed_error"]);
        AssertNoFaults(report, "Lua animation API", faults);
        report.Check("Destroyed object: its animator is detached at once (no stale playback)", victim.Animator is null && !workspace.Parts.Contains(victim));
        var clone = workspace.Parts.FirstOrDefault(p => p.Name == "RobotCopy");
        report.Check("Clone: a cloned model has its own animator (independent playback)",
            clone?.Animator is { } cloneAnimator && !ReferenceEquals(cloneAnimator, robot.Animator) && cloneAnimator.CurrentClip?.Name == "Dance");

        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: every animator is detached; the scene is exactly as before Play (destroyed restored, clone gone)",
            workspace.Parts.All(p => p.Animator is null) && SceneFile.ToJson(workspace.Parts) == sceneBefore && workspace.Parts.Count == 3);
    }

    // On a RobotExpressive instance: every animation method, its answers and its errors.
    private const string RobotProbe = """
        local finished = {}
        local frame, paused, t0 = 0, nil, nil
        local victim

        function Start()
          victim = Scene.Find("Victim")
          chk("has_animations", self:HasAnimations() == true)
          local list = self:GetAnimations()
          chk("list_14", #list == 14 and list[1].Name == "Dance" and list[1].Index == 1 and math.abs(list[1].Duration - 3.3333) < 0.01)
          local ok, reason = self:PlayAnimation("Nope")
          chk("unknown_name_false", ok == false and type(reason) == "string" and string.find(reason, "no animation called", 1, true) ~= nil)
          chk("unknown_number_false", (self:PlayAnimation(99)) == false)
          chk("bad_type_error", not pcall(function() self:PlayAnimation(true) end))
          chk("bad_option_error", not pcall(function() self:PlayAnimation("Idle", { Lop = true }) end))
          chk("bad_option_type_error", not pcall(function() self:PlayAnimation("Idle", { Loop = "yes" }) end))
          chk("bad_speed_error", not pcall(function() self:SetAnimationSpeed(0 / 0) end))
          chk("bad_fade_error", not pcall(function() self:CrossFadeAnimation("Idle", -1) end))
          chk("no_current_yet", self:GetCurrentAnimation() == nil and self:IsAnimationPlaying() == false)
          chk("play_by_name", self:PlayAnimation("Idle") == true and self:GetCurrentAnimation() == "Idle" and self:IsAnimationPlaying())
          chk("play_by_number", self:PlayAnimation(3) == true and self:GetCurrentAnimation() == "Idle")
          chk("length", math.abs(self:GetAnimationLength() - 3.3333) < 0.01 and math.abs(self:GetAnimationLength("Jump") - 0.7083) < 0.01
            and self:GetAnimationLength("Nope") == nil)
          self:OnAnimationFinished(function(name, object)
            finished[#finished + 1] = name
            chk("finished_object", object == self)
          end)
        end

        function Update(dt)
          frame = frame + 1
          if frame == 10 then
            chk("time_advances", self:GetAnimationTime() > 0.1)
            self:PauseAnimation()
            paused = self:GetAnimationTime()
          elseif frame == 20 then
            chk("pause_holds", self:GetAnimationTime() == paused and not self:IsAnimationPlaying())
            self:ResumeAnimation()
            self:SetAnimationSpeed(2)
            t0 = self:GetAnimationTime()
          elseif frame == 50 then
            chk("resume_and_speed_2", math.abs(self:GetAnimationTime() - t0 - 1.0) < 0.05)
            self:SetAnimationTime(1.5)
            chk("seek", math.abs(self:GetAnimationTime() - 1.5) < 1e-6)
            self:CrossFadeAnimation("Jump", 0.1, { Loop = false })
            chk("crossfade_current", self:GetCurrentAnimation() == "Jump" and self:IsAnimationPlaying("Jump") and not self:IsAnimationPlaying("Idle"))
          elseif frame == 60 then
            victim:Destroy()
            chk("destroyed_errors", not pcall(function() victim:PlayAnimation("Walk") end))
          elseif frame == 110 then
            chk("finished_once", #finished == 1 and finished[1] == "Jump")
            chk("finished_not_playing", not self:IsAnimationPlaying() and self:GetCurrentAnimation() == "Jump")
            self:RestartAnimation()
            chk("restart", self:IsAnimationPlaying() and self:GetAnimationTime() == 0)
            self:SetAnimationLooping(true)
          elseif frame == 170 then
            chk("looping_no_event", #finished == 1)
            self:StopAnimation()
            chk("stopped", self:GetCurrentAnimation() == nil and not self:IsAnimationPlaying())
            chk("stop_again_ok", pcall(function() self:StopAnimation(0.2) end))
            chk("speed_needs_clip", not pcall(function() self:SetAnimationSpeed(1) end))
            self:PlayAnimation("Walking", { FadeTime = 0.2, Speed = 1.5, StartTime = 0.3 })
            chk("options", self:GetCurrentAnimation() == "Walking")
            local copy = self:Clone("RobotCopy")
            copy:PlayAnimation("Dance")
            chk("clone_independent", copy:GetCurrentAnimation() == "Dance" and self:GetCurrentAnimation() == "Walking")
          end
        end
        """;

    public static void PlayOnStartAndPersistence(TestReport report, string dir)
    {
        var assets = AnimationDemo.ExampleAssets();
        var fox = assets("Fox");
        var workspace = new Workspace();
        var runner = ModelPart("Runner", fox, new Vector3(0f, 0f, 0f), 0.05f, new ModelAnimationSettings { Clip = "Run", PlayOnStart = true, Speed = 0.5f, Loop = false });
        var lost = ModelPart("Lost", fox, new Vector3(8f, 0f, 0f), 0.05f, new ModelAnimationSettings { Clip = "Gallop", PlayOnStart = true });
        var plain = ModelPart("Plain", fox, new Vector3(-8f, 0f, 0f), 0.05f);
        workspace.AddPart(runner);
        workspace.AddPart(lost);
        workspace.AddPart(plain);

        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = Start(Path.Combine(dir, "Proj"), workspace, log, faults);
        Step(play, 30, faults);
        report.Check("Play on start: the saved clip plays with its saved speed and loop", runner.Animator is { CurrentClip.Name: "Run", Speed: 0.5f, Loop: false }
            && System.Math.Abs(runner.Animator.Time - 0.25) < 0.02, $"{runner.Animator?.CurrentClip?.Name} {runner.Animator?.Time:F3}");
        report.Check("Play on start: a model without the setting does not animate by itself", plain.Animator is null);
        var warning = Enumerable.Range(0, log.Count).Select(i => log[i].Message).FirstOrDefault(m => m.Contains("Gallop", StringComparison.Ordinal));
        report.Check("Play on start naming a clip the model does not have: a warning in Output, nothing plays, nothing crashes",
            lost.Animator is null && warning is not null && warning.Contains("Survey, Walk, Run", StringComparison.Ordinal), warning);
        play.Stop(workspace);
        play.Dispose();
        report.Check("Stop: animators detached", workspace.Parts.All(p => p.Animator is null));

        // The scene file: settings round trip; defaults are not written; primitives never get them.
        var path = Path.Combine(dir, "Scene.json");
        workspace.AddPart(new Part("Cube", new Vector3(0f, 1f, 5f), new Vector3(2f, 2f, 2f), new Vector3(0.5f, 0.5f, 0.5f)));
        SceneFile.TrySave(workspace, path, out _);
        var json = File.ReadAllText(path);
        SceneFile.TryLoad(path, assetPath => assetPath == fox.AssetPath ? fox : null, out var loaded, out _);
        var loadedRunner = loaded.First(p => p.Name == "Runner");
        report.Check("Scene file: animation settings are saved and load back equal", loadedRunner.Animation == runner.Animation && loaded.First(p => p.Name == "Lost").Animation == lost.Animation,
            $"{loadedRunner.Animation}");
        report.Check("Scene file: \"Animation\" only on models with settings (none for defaults or primitives)",
            json.Split("\"Animation\"").Length - 1 == 2 && loaded.First(p => p.Name == "Plain").Animation is null && loaded.First(p => p.Name == "Cube").Animation is null);
        report.Check("Scene file: only values that differ from the defaults are written", json.Contains("\"Speed\": 0.5", StringComparison.Ordinal)
            && json.Contains("\"Loop\": false", StringComparison.Ordinal) && !json.Contains("\"Loop\": true", StringComparison.Ordinal));
        report.Check("Scene file: playback state is never saved (no time, pose or current clip)",
            !json.Contains("\"Time\"", StringComparison.Ordinal) && !json.Contains("Animator", StringComparison.Ordinal));

        // Duplicate: the same settings, its own (no) playback; a snapshot restores settings (undo and Stop use them).
        var duplicate = PartDuplicator.Duplicate(runner, workspace);
        report.Check("Duplicate: copies the saved settings, not the playback", duplicate.Animation == runner.Animation && duplicate.Animator is null);
        var snapshot = PartSnapshot.FromPart(runner);
        runner.Animation = null;
        snapshot.ApplyAll(runner);
        report.Check("Snapshots (undo, Play's restore) carry the settings", runner.Animation == snapshot.Animation && runner.Animation is not null);

        // A missing model file: the instance loads as a placeholder that keeps its settings; Play warns and runs.
        SceneFile.TryLoad(path, _ => null, out var placeholders, out _);
        var placeholder = placeholders.First(p => p.Name == "Runner");
        var placeholderWorkspace = new Workspace();
        foreach (var part in placeholders)
        {
            placeholderWorkspace.AddPart(part);
        }

        var placeholderLog = new OutputLog();
        var placeholderPlay = Start(Path.Combine(dir, "Proj"), placeholderWorkspace, placeholderLog, faults);
        Step(placeholderPlay, 5, faults);
        var notLoaded = Enumerable.Range(0, placeholderLog.Count).Any(i => placeholderLog[i].Message.Contains("is not loaded", StringComparison.Ordinal));
        placeholderPlay.Stop(placeholderWorkspace);
        placeholderPlay.Dispose();
        report.Check("Missing model file: the placeholder keeps its settings; Play warns and does not crash",
            placeholder.Model?.Asset is null && placeholder.Animation == runner.Animation && notLoaded);
        SceneFile.TrySave(placeholderWorkspace, Path.Combine(dir, "Scene2.json"), out _);
        report.Check("Missing model file: saving keeps the settings as they were", File.ReadAllText(Path.Combine(dir, "Scene2.json")).Contains("\"Clip\": \"Run\"", StringComparison.Ordinal));

        // Reimport: the same file imported again is a second asset with the same clips; the first one is untouched.
        var project = Path.Combine(dir, "Reimport");
        Directory.CreateDirectory(project);
        using var cache = new ModelAssetCache();
        cache.SetProject(project);
        var first = cache.TryImport(AnimationChecks.ModelFile("Fox", "Fox.glb"), out var firstImport, out _);
        var second = cache.TryImport(AnimationChecks.ModelFile("Fox", "Fox.glb"), out var secondImport, out _);
        report.Check("Reimport: a second import is its own asset (Fox-2) with the same 3 clips; the first stays",
            first && second && secondImport.Asset.AssetPath == "Assets/Models/Fox-2/Fox.glb" && secondImport.Asset.Animation.Clips.Count == 3
            && cache.Load(firstImport.Asset.AssetPath, out _) is { Animation.Clips.Count: 3 }, secondImport?.Asset.AssetPath);
        using var fresh = new ModelAssetCache();
        fresh.SetProject(project);
        report.Check("Reopen: the copied asset loads from the project with its skeleton and clips (project-relative path)",
            fresh.Load("Assets/Models/Fox/Fox.glb", out _) is { Animation: { Clips.Count: 3, Skeleton.BoneCount: 24 } });
    }

    public static void Demo(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        AnimationDemo.CopyScripts(root);
        var workspace = new Workspace();
        foreach (var part in AnimationDemo.BuildScene(AnimationDemo.ExampleAssets()))
        {
            workspace.AddPart(part);
        }

        var before = SceneFile.ToJson(workspace.Parts);
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = Start(root, workspace, log, faults);
        Step(play, 60, faults);
        var robot = workspace.Parts.First(p => p.Name == "Robot");
        var body = workspace.Parts.First(p => p.Name == "PlayerBody");
        report.Check("Demo: the character idles; every Play on start model and every Dancer plays",
            robot.Animator?.CurrentClip?.Name == "Idle" && workspace.Parts.Where(p => p.Model is not null && p.Name != "Static Box").All(p => p.Animator?.IsPlaying == true),
            string.Join(", ", workspace.Parts.Where(p => p.Model is not null).Select(p => $"{p.Name}={p.Animator?.CurrentClip?.Name}")));
        var start = body.Position;
        StepHolding(play, 60, faults, "W");
        report.Check("Demo: W walks the body forward and the Robot plays Walking", robot.Animator?.CurrentClip?.Name == "Walking" && body.Position.Z < start.Z - 3f,
            $"{robot.Animator?.CurrentClip?.Name}, moved {start.Z - body.Position.Z:F1}");
        StepHolding(play, 60, faults, "W", "LeftShift");
        report.Check("Demo: Shift + W runs (Running, clip speed follows the real speed)", robot.Animator is { CurrentClip.Name: "Running", Speed: > 0.8f },
            $"{robot.Animator?.CurrentClip?.Name} at {robot.Animator?.Speed:F2}");
        Step(play, 60, faults);
        report.Check("Demo: standing still returns to Idle", robot.Animator?.CurrentClip?.Name == "Idle");
        StepHolding(play, 1, faults, "Space");
        Step(play, 4, faults);
        report.Check("Demo: Space jumps (Jump, once)", robot.Animator is { CurrentClip.Name: "Jump", Loop: false });
        Step(play, 90, faults);
        StepHolding(play, 1, faults, "E");
        Step(play, 20, faults);
        var waved = robot.Animator?.CurrentClip?.Name == "Wave";
        Step(play, 150, faults);
        report.Check("Demo: E waves once, then the character idles again", waved && robot.Animator?.CurrentClip?.Name == "Idle");
        var dancers = workspace.Parts.Where(p => p.Name.StartsWith("Dancer", StringComparison.Ordinal)).Select(p => p.Animator!).ToList();
        report.Check("Demo: three Dancers on one model, each its own clip and tempo",
            dancers.Count == 3 && dancers.Select(d => d.Speed).Distinct().Count() == 3);
        AssertNoFaults(report, "Demo", faults);
        play.Stop(workspace);
        play.Dispose();
        report.Check("Demo: Stop restores the scene exactly and detaches every animator",
            SceneFile.ToJson(workspace.Parts) == before && workspace.Parts.All(p => p.Animator is null));
    }
}
