using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;

namespace Wisko.Tests.Checks;

/// <summary>
/// Animation System V1: import of real rigged models (examples/animation-demo/Models), skeleton and skin data, pose
/// evaluation, playback, blending and bad-data handling.
/// </summary>
internal static class AnimationChecks
{
    /// <summary>examples/animation-demo/Models in this checkout (found by walking up to Wisko.sln).</summary>
    public static string ModelsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var models = Path.Combine(dir.FullName, "examples", "animation-demo", "Models");
            if (File.Exists(Path.Combine(dir.FullName, "Wisko.sln")) && Directory.Exists(models))
            {
                return models;
            }
        }

        throw new DirectoryNotFoundException("examples/animation-demo/Models not found above " + AppContext.BaseDirectory);
    }

    public static string ModelFile(string folder, string file) => Path.Combine(ModelsFolder(), folder, file);

    /// <summary>Decodes a demo model straight from the examples folder (no project) as the editor would load it.</summary>
    public static ModelAsset LoadAsset(string folder, string file)
    {
        using var importer = new AssimpModelImporter();
        var model = importer.Import(ModelFile(folder, file));
        return new ModelAsset($"Assets/Models/{folder}/{file}", model);
    }

    public static void Import(TestReport report)
    {
        // The binding (Silk.NET.Assimp 2.23) and every check below expect the Assimp of Ultz.Native.Assimp 6.0.2; on
        // macOS the binding's own default would load the package's leftover 5.4.1 build instead.
        using (var native = new AssimpModelImporter())
        {
            report.Check("Assimp: the native library is 6.0.2 (revision fb375dd8)",
                native.NativeVersion == "6.0.2" && native.NativeRevision == "fb375dd8",
                $"{native.NativeVersion} revision {native.NativeRevision} on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        }

        // Test A: the simplest rig.
        var simple = LoadAsset("RiggedSimple", "RiggedSimple.glb");
        var simpleSkin = simple.Animation.Skins[0];
        report.Check("RiggedSimple: one skinned mesh with 2 bones", simpleSkin is not null && simpleSkin.Source.BoneNames.Count == 2 && simple.Animation.Skeleton.BoneCount == 2,
            $"skins {simple.Animation.Skins.Count(skin => skin is not null)}, bones {simple.Animation.Skeleton.BoneCount}");
        var skeleton = simple.Animation.Skeleton;
        var bone = skeleton.IndexOf("Bone");
        var child = skeleton.IndexOf("Bone.001");
        report.Check("RiggedSimple: bone hierarchy kept (Bone.001's parent is Bone)", bone >= 0 && child >= 0 && skeleton.ParentOf(child) == bone,
            $"Bone #{bone}, Bone.001 #{child}, parent {(child >= 0 ? skeleton.ParentOf(child) : -2)}");
        report.Check("RiggedSimple: one unnamed clip, named \"Animation 1\", 2.083 s",
            simple.Animation.Clips.Count == 1 && simple.Animation.Clips[0].Name == "Animation 1" && simple.Animation.Clips[0].FileName == ""
            && MathF.Abs(simple.Animation.Clips[0].Duration - 2.0833f) < 0.001f,
            string.Join(", ", simple.Animation.Clips.Select(clip => $"'{clip.Name}' {clip.Duration:F4}s")));

        // Test B material: a humanoid with many clips.
        var robot = LoadAsset("RobotExpressive", "RobotExpressive.glb");
        var robotClips = robot.Animation.Clips.Select(clip => clip.Name).ToArray();
        report.Check("RobotExpressive: 14 clips including Idle, Walking, Running, Jump, Wave",
            robotClips.Length == 14 && new[] { "Idle", "Walking", "Running", "Jump", "Wave" }.All(robotClips.Contains), string.Join(", ", robotClips));
        var walking = robot.Animation.FindClip("Walking")!;
        report.Check("RobotExpressive: Walking lasts 0.958 s and moves 16 nodes", MathF.Abs(walking.Duration - 0.9583f) < 0.001f && walking.TrackCount == 16,
            $"{walking.Duration:F4}s, {walking.TrackCount} tracks");
        var rigidOnBones = robot.Draws.Count(draw => !draw.Skinned && MovedByAnimation(robot, draw.Node));
        report.Check("RobotExpressive: its rigid parts hang under animated bones (they move with them)", rigidOnBones >= 12,
            $"{rigidOnBones} of {robot.Draws.Count(draw => !draw.Skinned)} rigid draws under animated nodes, {robot.Draws.Count(draw => draw.Skinned)} skinned");
        var robotSkeleton = robot.Animation.Skeleton;
        var torso = robotSkeleton.IndexOf("Torso");
        var neck = robotSkeleton.IndexOf("Neck");
        report.Check("Duplicate node names: \"Torso\" (a joint and a mesh node) resolves to the joint, the one without a mesh",
            torso >= 0 && neck >= 0 && robotSkeleton.ParentOf(neck) == torso && robot.Draws.All(draw => draw.Node != torso),
            $"Torso #{torso}, Neck #{neck} (parent #{(neck >= 0 ? robotSkeleton.ParentOf(neck) : -2)})");
        report.Check("RobotExpressive: its hands are skinned (43 bones each)", robot.Animation.Skins.Count(skin => skin is { Source.BoneNames.Count: 43 }) == 4);
        report.Check("RobotExpressive: unnormalized hand weights are reported and renormalized",
            robot.Animation.Notes.Any(note => note.Contains("did not add up to 1", StringComparison.Ordinal)), string.Join(" | ", robot.Animation.Notes));
        report.Check("RobotExpressive: its morph targets are reported as not supported (base shape drawn)",
            robot.Model.Warnings.Any(warning => warning.Contains("morph target", StringComparison.Ordinal)));
        report.Check("Clip lookup: exact name, then ignoring case; unknown names find nothing",
            robot.Animation.FindClip("Idle")?.Name == "Idle" && robot.Animation.FindClip("idle")?.Name == "Idle" && robot.Animation.FindClip("Idel") is null);

        var fox = LoadAsset("Fox", "Fox.glb");
        report.Check("Fox: 3 clips Survey 3.417 s, Walk 0.708 s, Run 1.158 s, 24 bones",
            fox.Animation.Clips.Count == 3
            && fox.Animation.FindClip("Survey") is { Duration: > 3.41f and < 3.42f }
            && fox.Animation.FindClip("Walk") is { Duration: > 0.70f and < 0.71f }
            && fox.Animation.FindClip("Run") is { Duration: > 1.15f and < 1.16f }
            && fox.Animation.Skeleton.BoneCount == 24,
            string.Join(", ", fox.Animation.Clips.Select(clip => $"{clip.Name} {clip.Duration:F3}s")) + $", {fox.Animation.Skeleton.BoneCount} bones");

        var cesium = LoadAsset("CesiumMan", "CesiumMan.glb");
        report.Check("CesiumMan: 19 bones, one 2 s walk", cesium.Animation.Skeleton.BoneCount == 19 && cesium.Animation.Clips.Count == 1
            && MathF.Abs(cesium.Animation.Clips[0].Duration - 2f) < 0.001f);

        var boxAnimated = LoadAsset("BoxAnimated", "BoxAnimated.glb");
        report.Check("BoxAnimated: node animation without a skeleton (no skins, one clip)",
            !boxAnimated.Animation.HasSkins && boxAnimated.Animation.Clips.Count == 1 && boxAnimated.Draws.Any(draw => boxAnimated.Animation.Clips[0].Animates(draw.Node)));

        // FBX: Assimp exposes skinning and animation; this file exceeds 4 influences per vertex.
        var fbx = LoadAsset("AnimationWithSkeletonFbx", "animation_with_skeleton.fbx");
        var fbxSkin = fbx.Animation.Skins.FirstOrDefault(skin => skin is not null);
        report.Check("FBX: skinned mesh with 14 bones and one clip of 0.833 s (20 frames at 24 fps)",
            fbxSkin is { Source.BoneNames.Count: 14 } && fbx.Animation.Clips.Count == 1 && MathF.Abs(fbx.Animation.Clips[0].Duration - 20f / 24f) < 0.001f,
            $"{fbxSkin?.Source.BoneNames.Count} bones, {string.Join(", ", fbx.Animation.Clips.Select(clip => $"'{clip.Name}' {clip.Duration:F4}s"))}");
        report.Check("FBX: vertices with more than 4 influences keep the 4 strongest, reported",
            fbxSkin is { Source.TruncatedVertices: > 2000 } && fbx.Animation.Notes.Any(note => note.Contains("more than 4 bone influences", StringComparison.Ordinal)),
            $"{fbxSkin?.Source.TruncatedVertices} truncated");

        // Weights: at most 4, strongest first, summing to 1, for every skinned vertex of every model.
        var worstSum = 0f;
        var unsorted = 0;
        foreach (var asset in new[] { simple, robot, fox, cesium, fbx })
        {
            foreach (var mesh in asset.Model.Meshes)
            {
                if (mesh.Skin is not { } skin)
                {
                    continue;
                }

                for (var v = 0; v < mesh.VertexCount; v++)
                {
                    var sum = 0f;
                    for (var i = 0; i < ImportedSkin.MaxInfluences; i++)
                    {
                        sum += skin.Weights[v * 4 + i];
                        if (i > 0 && skin.Weights[v * 4 + i] > skin.Weights[v * 4 + i - 1])
                        {
                            unsorted++;
                        }
                    }

                    worstSum = MathF.Max(worstSum, MathF.Abs(sum - 1f));
                }
            }
        }

        report.Check("Bone weights: every skinned vertex has at most 4, strongest first, summing to 1", worstSum < 1e-5f && unsorted == 0,
            $"worst |sum - 1| {worstSum:E2}, unsorted {unsorted}");

        // Inverse bind matrices against the rest pose. These files' rest pose is their bind pose, with inverse binds relative to
        // the mesh's node: the rest pose skins every vertex exactly where that node draws the unskinned mesh.
        foreach (var (name, asset) in new[] { ("CesiumMan", cesium), ("Fox", fox), ("RiggedFigure", LoadAsset("RiggedFigure", "RiggedFigure.glb")) })
        {
            var error = RestPoseError(asset, out var size);
            report.Check($"Inverse bind matrices ({name}): the rest pose reproduces the bind pose", error < size * 1e-4f, $"max offset {error:E2} for a model {size:F3} across");
        }

        // RiggedSimple's inverse binds are relative to its Z_UP root (glTF ignores a skinned mesh's own node transform): at
        // rest both bones agree (the mesh is not deformed) and Z_UP stands the cylinder upright (+Y), its length along Y.
        var simplePalette = simple.Animation.RestPose.PaletteOf(0);
        var simpleBounds = simple.Bounds.Size;
        report.Check("RiggedSimple: undeformed at rest (both bone matrices agree) and upright after Z_UP (tallest along Y)",
            MatrixDistance(simplePalette[0], simplePalette[1]) < 1e-4f && simpleBounds.Y > simpleBounds.X * 2f && simpleBounds.Y > simpleBounds.Z * 2f,
            $"bone matrix difference {MatrixDistance(simplePalette[0], simplePalette[1]):E2}, size ({simpleBounds.X:F2}, {simpleBounds.Y:F2}, {simpleBounds.Z:F2})");

        // The FBX's bind pose is the mesh node's placement (Assimp's offset = inverse bone bind * mesh bind); its rest pose (the
        // pose saved in the file) differs from it for some bones, as FBX exports often do.
        var fbxDraw = fbx.Draws.First(draw => draw.Skinned);
        var fbxPalette = fbx.Animation.RestPose.PaletteOf(fbxDraw.MeshIndex);
        var fbxNode = fbx.Animation.Skeleton.RestGlobal(fbxDraw.Node);
        var atBind = 0;
        for (var slot = 0; slot < fbxPalette.Length; slot++)
        {
            atBind += MatrixDistance(fbxPalette[slot], fbxNode) < 1e-2f ? 1 : 0;
        }

        TestReport.Info($"FBX rest pose: {atBind} of {fbxPalette.Length} bones are at their bind pose");
        report.Check("FBX: bone matrices at rest are finite and most bones sit at the mesh's bind placement",
            atBind * 2 > fbxPalette.Length && fbx.Bounds is { IsEmpty: false } && float.IsFinite(fbx.Bounds.Size.Length), $"{atBind} of {fbxPalette.Length}");

        // Static compatibility: a model without animation keeps exactly its former draws and bounds.
        var box = LoadAsset("Box", "Box.glb");
        report.Check("Static model (Box.glb): no clips, no skins, bounds as decoded, draws on nodes",
            !box.Animation.HasClips && !box.Animation.HasSkins && box.Bounds == box.Model.Bounds && box.Draws.All(draw => !draw.Skinned && draw.Node >= 0));
    }

    // ============================================================================================ evaluation

    public static void Evaluation(TestReport report)
    {
        // Compose / decompose: translation, rotation and a non-uniform scale survive a round trip.
        var q = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f));
        var m = AnimationMath.Compose(new Vector3(1.5f, -2f, 3f), q, new Vector3(2f, 0.5f, 3f));
        var back = AnimationMath.Decompose(m);
        report.Check("TRS: compose then decompose returns translation, rotation and non-uniform scale",
            (back.Translation - new Vector3(1.5f, -2f, 3f)).Length < 1e-5f && (back.Scale - new Vector3(2f, 0.5f, 3f)).Length < 1e-5f
            && MathF.Abs(MathF.Abs(System.Numerics.Quaternion.Dot(back.Rotation, q)) - 1f) < 1e-5f);
        var mirrored = AnimationMath.Decompose(AnimationMath.Compose(Vector3.Zero, q, new Vector3(-1f, 1f, 1f)));
        report.Check("TRS: a mirrored matrix decomposes to a negative scale that recomposes to it",
            MatrixDistance(AnimationMath.Compose(mirrored), AnimationMath.Compose(Vector3.Zero, q, new Vector3(-1f, 1f, 1f))) < 1e-4f);

        // Quaternion -> matrix agrees with the engine's own rotation (90 degrees about +Y turns +X to -Z).
        var yaw90 = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2f);
        var turned = AnimationMath.TransformPoint(AnimationMath.Compose(Vector3.Zero, yaw90, new Vector3(1f, 1f, 1f)), new Vector3(1f, 0f, 0f));
        report.Check("Rotation: a quaternion of 90 degrees about +Y turns +X into -Z (right-handed, column vectors)",
            (turned - new Vector3(0f, 0f, -1f)).Length < 1e-5f, $"({turned.X:F3}, {turned.Y:F3}, {turned.Z:F3})");

        // Sampling a synthetic clip: A moves 0 -> 2 in X, turns 0 -> 90 degrees about Y and scales 1 -> 3 over one second;
        // B steps; C has a single key; D has keys only from 0.5 s.
        var data = ModelAnimationData.Build(SyntheticModel());
        var clip = data.FindClip("Move")!;
        var a = data.Skeleton.IndexOf("A");
        var rest = data.Skeleton.RestTransform(a);
        var half = clip.Sample(a, 0.5f, rest);
        report.Check("Sampling: translation interpolates linearly (x = 1 at half time)", MathF.Abs(half.Translation.X - 1f) < 1e-5f, $"{half.Translation.X}");
        report.Check("Sampling: scale interpolates linearly (2 at half time)", (half.Scale - new Vector3(2f, 2f, 2f)).Length < 1e-5f);
        var expectedRotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 4f);
        report.Check("Sampling: rotation interpolates spherically (45 degrees at half time)",
            MathF.Abs(MathF.Abs(System.Numerics.Quaternion.Dot(half.Rotation, expectedRotation)) - 1f) < 1e-5f);
        report.Check("Sampling: before the first key and after the last the end values hold",
            clip.Sample(a, -1f, rest).Translation.X == 0f && clip.Sample(a, 5f, rest).Translation.X == 2f);
        var b = data.Skeleton.IndexOf("B");
        var stepped = clip.Sample(b, 0.99f, data.Skeleton.RestTransform(b));
        report.Check("Sampling: step keys hold until the next key", stepped.Translation.Y == 0f && clip.Sample(b, 1f, data.Skeleton.RestTransform(b)).Translation.Y == 5f);
        var c = data.Skeleton.IndexOf("C");
        report.Check("Sampling: a channel with one key is constant; channels without keys stay at rest",
            clip.Sample(c, 0.3f, data.Skeleton.RestTransform(c)).Translation.Z == 7f && clip.Sample(c, 0.3f, data.Skeleton.RestTransform(c)).Scale.X == 1f);
        var shortest = AnimationMath.Slerp(System.Numerics.Quaternion.Identity, -yaw90, 0.5f);
        report.Check("Quaternion interpolation takes the shorter arc (q and -q are the same rotation)",
            MathF.Abs(MathF.Abs(System.Numerics.Quaternion.Dot(shortest, expectedRotation)) - 1f) < 1e-5f);

        // Pose: parent before child; the child's global includes the parent's animated transform.
        var animator = new ModelAnimator(data);
        animator.Play(clip);
        animator.Advance(0.5);
        var pose = animator.Pose;
        var bGlobal = pose.GlobalOf(b);
        var expectedB = Matrix4.Multiply(pose.GlobalOf(a), AnimationMath.Compose(clip.Sample(b, 0.5f, data.Skeleton.RestTransform(b))));
        report.Check("Pose: a child's global transform is its parent's (animated) global times its own (animated) local", MatrixDistance(bGlobal, expectedB) < 1e-4f);
        var d = data.Skeleton.IndexOf("D");
        report.Check("Pose: a node no clip animates keeps its exact rest transform", MatrixDistance(pose.GlobalOf(d), data.Skeleton.RestGlobal(d)) == 0f);
        var skin = data.Skins[0]!;
        report.Check("Skin matrices: bone global x inverse bind for every palette slot",
            Enumerable.Range(0, skin.PaletteSize).All(slot => MatrixDistance(pose.PaletteOf(0)[slot],
                Matrix4.Multiply(pose.GlobalOf(slot == 0 ? a : b), skin.Source.InverseBindMatrices[slot])) < 1e-4f));
    }

    // ============================================================================================ playback

    public static void Playback(TestReport report)
    {
        var robot = LoadAsset("RobotExpressive", "RobotExpressive.glb").Animation;
        var walking = robot.FindClip("Walking")!;

        // The same 2.5 s of playback at 30, 60 and 144 frames per second ends at the same time with the same pose.
        var results = new List<(double Time, Matrix4[] Globals)>();
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var animator = new ModelAnimator(robot);
            animator.Play(walking);
            var frames = (int)System.Math.Round(2.5 * fps);
            for (var i = 0; i < frames; i++)
            {
                animator.Advance(1.0 / fps);
            }

            results.Add((animator.Time, Enumerable.Range(0, robot.Skeleton.NodeCount).Select(n => animator.Pose.GlobalOf(n)).ToArray()));
        }

        var worst = 0f;
        for (var n = 0; n < robot.Skeleton.NodeCount; n++)
        {
            worst = MathF.Max(worst, MatrixDistance(results[0].Globals[n], results[1].Globals[n]));
            worst = MathF.Max(worst, MatrixDistance(results[0].Globals[n], results[2].Globals[n]));
        }

        var expectedTime = 2.5 % walking.Duration;
        report.Check("Timing: 2.5 s at 30, 60 and 144 fps ends at the same clip time (wrapped) and the same pose",
            results.All(r => System.Math.Abs(r.Time - expectedTime) < 1e-6) && worst < 1e-3f,
            $"times {string.Join(", ", results.Select(r => r.Time.ToString("F6")))} (expected {expectedTime:F6}), worst matrix difference {worst:E2}");

        var jump = robot.FindClip("Jump")!;
        var once = new ModelAnimator(robot);
        var finished = new List<string>();
        once.Finished = (_, clip) => finished.Add(clip.Name);
        once.Play(jump, loop: false);
        for (var i = 0; i < 60; i++)
        {
            once.Advance(1.0 / 60);
        }

        report.Check("One-shot: Jump (0.708 s) ends after 1 s: finished, held at its end, reported once",
            once.IsFinished && !once.IsPlaying && once.Time == jump.Duration && finished.SequenceEqual(["Jump"]), $"time {once.Time}, events {finished.Count}");
        once.Advance(1.0);
        report.Check("One-shot: no further Finished while it holds", finished.Count == 1);
        once.Restart();
        report.Check("Restart: back to 0 and playing", once.Time == 0d && once.IsPlaying);

        var paused = new ModelAnimator(robot);
        paused.Play(walking);
        paused.Advance(0.3);
        paused.Pause();
        var heldTime = paused.Time;
        paused.Advance(1.0);
        report.Check("Pause: time does not advance; Resume continues from there", paused.Time == heldTime && paused.IsPaused && !paused.IsPlaying);
        paused.Resume();
        paused.Advance(0.1);
        report.Check("Resume: time advances again", System.Math.Abs(paused.Time - (heldTime + 0.1) % walking.Duration) < 1e-9);

        var fast = new ModelAnimator(robot);
        fast.Play(walking, speed: 2f);
        fast.Advance(0.2);
        var backwards = new ModelAnimator(robot);
        backwards.Play(walking, speed: -1f);
        backwards.Advance(0.25);
        report.Check("Speed: 2x advances twice as fast; -1 plays backwards from the end",
            System.Math.Abs(fast.Time - 0.4) < 1e-9 && System.Math.Abs(backwards.Time - (walking.Duration - 0.25)) < 1e-6, $"{fast.Time}, {backwards.Time}");
        fast.SetSpeed(0f);
        fast.Advance(0.5);
        report.Check("Speed 0 holds the pose", System.Math.Abs(fast.Time - 0.4) < 1e-9);

        var seek = new ModelAnimator(robot);
        seek.Play(walking);
        seek.Seek(walking.Duration + 0.1);
        var seekLoop = seek.Time;
        var seekOnce = new ModelAnimator(robot);
        var seekEvents = 0;
        seekOnce.Finished = (_, _) => seekEvents++;
        seekOnce.Play(jump, loop: false);
        seekOnce.Seek(10);
        report.Check("Seek: wraps a looping clip, clamps a one-shot (finished, without a Finished event)",
            System.Math.Abs(seekLoop - 0.1) < 1e-6 && seekOnce.Time == jump.Duration && seekOnce.IsFinished && seekEvents == 0, $"{seekLoop}, {seekOnce.Time}");

        var again = new ModelAnimator(robot);
        again.Play(walking);
        again.Advance(0.4);
        again.Play(walking, speed: 1.5f);
        report.Check("Play of the clip already playing keeps its place (only loop and speed change); Restart = true starts over",
            System.Math.Abs(again.Time - 0.4) < 1e-9 && again.Speed == 1.5f);
        again.Play(walking, restart: true);
        report.Check("Play with restart starts from 0", again.Time == 0d);

        var stop = new ModelAnimator(robot);
        stop.Play(walking);
        stop.Advance(0.3);
        stop.Stop();
        report.Check("Stop: no clip, the rest pose (every node at rest)", stop.IsStopped && stop.CurrentClip is null
            && Enumerable.Range(0, robot.Skeleton.NodeCount).All(n => MatrixDistance(stop.Pose.GlobalOf(n), robot.Skeleton.RestGlobal(n)) < 1e-6f));
    }

    // ============================================================================================ blending

    public static void Blending(TestReport report)
    {
        var robot = LoadAsset("RobotExpressive", "RobotExpressive.glb").Animation;
        var walking = robot.FindClip("Walking")!;
        var running = robot.FindClip("Running")!;
        var idle = robot.FindClip("Idle")!;
        var jump = robot.FindClip("Jump")!;

        var animator = new ModelAnimator(robot);
        animator.Play(walking);
        animator.Advance(0.5);
        var before = Snapshot(robot, animator);
        animator.Play(running, fade: 0.3f);
        report.Check("Crossfade: at its start the pose is exactly what showed before (no snap)", MaxNodeJump(robot, before, Snapshot(robot, animator)) < 1e-5f);
        animator.Advance(0.15);
        report.Check("Crossfade: half way the new clip weighs 0.5, both layers blend", MathF.Abs(animator.CurrentWeight - 0.5f) < 1e-4f && animator.LayerCount == 2,
            $"weight {animator.CurrentWeight}, layers {animator.LayerCount}");
        animator.Advance(0.2);
        report.Check("Crossfade: done after its time: one layer (Running) left", animator.LayerCount == 1 && animator.CurrentClip == running && animator.CurrentWeight == 1f);

        // A crossfade interrupted by another: still continuous, then settles on the last clip.
        var interrupted = new ModelAnimator(robot);
        interrupted.Play(walking);
        interrupted.Advance(0.3);
        interrupted.Play(running, fade: 0.4f);
        interrupted.Advance(0.2);
        var mid = Snapshot(robot, interrupted);
        interrupted.Play(idle, fade: 0.4f);
        report.Check("Interrupted crossfade: no snap when a new crossfade starts mid-way (3 layers blend)",
            MaxNodeJump(robot, mid, Snapshot(robot, interrupted)) < 1e-5f && interrupted.LayerCount == 3);
        interrupted.Advance(0.45);
        report.Check("Interrupted crossfade: settles on the newest clip (Idle) alone", interrupted.LayerCount == 1 && interrupted.CurrentClip == idle);

        // Per-frame movement during a 0.25 s crossfade stays far below the jump of an instant switch.
        var smooth = new ModelAnimator(robot);
        smooth.Play(walking);
        smooth.Advance(0.37);
        var hard = new ModelAnimator(robot);
        hard.Play(walking);
        hard.Advance(0.37);
        var hardBefore = Snapshot(robot, hard);
        hard.Play(running);
        hard.Advance(1.0 / 60);
        var hardJump = MaxNodeJump(robot, hardBefore, Snapshot(robot, hard));
        smooth.Play(running, fade: 0.25f);
        var smoothJump = 0f;
        var previous = Snapshot(robot, smooth);
        for (var i = 0; i < 20; i++)
        {
            smooth.Advance(1.0 / 60);
            var next = Snapshot(robot, smooth);
            smoothJump = MathF.Max(smoothJump, MaxNodeJump(robot, previous, next));
            previous = next;
        }

        report.Check("No visible snapping: per-frame bone movement in a crossfade is well below an instant switch",
            smoothJump < hardJump * 0.5f, $"crossfade max {smoothJump:F4} per frame, instant switch {hardJump:F4}");

        // Missing channels: a node the newer clip does not key blends towards its rest value.
        var data = ModelAnimationData.Build(SyntheticModel());
        var move = data.FindClip("Move")!;
        var onlyB = data.FindClip("OnlyB")!;
        // Move plays once and holds A at x = 2; OnlyB does not key A, so A blends towards its rest x = 0.
        var mixer = new ModelAnimator(data);
        mixer.Play(move, loop: false);
        mixer.Advance(1.0);
        mixer.Play(onlyB, fade: 1f);
        mixer.Advance(0.5);
        var a = data.Skeleton.IndexOf("A");
        report.Check("Missing channels: a node the new clip does not animate blends towards its rest pose (A half way back)",
            MathF.Abs(mixer.Pose.GlobalOf(a).M14 - 1f) < 1e-3f, $"x = {mixer.Pose.GlobalOf(a).M14}");

        // Looping into a one-shot: plays once, reports, holds.
        var toJump = new ModelAnimator(robot);
        var ended = 0;
        toJump.Finished = (_, clip) => ended += clip == jump ? 1 : 0;
        toJump.Play(walking);
        toJump.Advance(0.2);
        toJump.Play(jump, loop: false, fade: 0.1f);
        for (var i = 0; i < 90; i++)
        {
            toJump.Advance(1.0 / 60);
        }

        report.Check("Looping clip -> one-shot: the one-shot plays once, holds its end and is reported once", toJump.IsFinished && ended == 1 && toJump.LayerCount == 1);
        toJump.Stop(0.2f);
        toJump.Advance(0.1);
        report.Check("Stop with a fade blends back to the rest pose, then stops", !toJump.IsStopped && toJump.CurrentClip is null);
        toJump.Advance(0.15);
        report.Check("Stop with a fade: stopped once the fade is done", toJump.IsStopped);
    }

    // ============================================================================================ instances

    public static void Instances(TestReport report)
    {
        var asset = LoadAsset("Fox", "Fox.glb");
        var walk = asset.Animation.FindClip("Walk")!;
        var run = asset.Animation.FindClip("Run")!;
        var first = new ModelAnimator(asset.Animation);
        var second = new ModelAnimator(asset.Animation);
        var third = new ModelAnimator(asset.Animation);
        first.Play(walk);
        second.Play(run);
        third.Play(walk, speed: 2f);
        for (var i = 0; i < 20; i++)
        {
            first.Advance(1.0 / 60);
            second.Advance(1.0 / 60);
            third.Advance(1.0 / 60);
        }

        var firstPose = first.Pose.PaletteOf(0).ToArray();
        second.Advance(0.3);
        report.Check("Instances: one asset, three animators: their own clip, time and speed",
            first.CurrentClip == walk && second.CurrentClip == run && System.Math.Abs(third.Time - 2 * first.Time % walk.Duration) < 1e-6);
        report.Check("Instances: advancing one never changes another's pose",
            first.Pose.PaletteOf(0).ToArray().Zip(firstPose).All(pair => MatrixDistance(pair.First, pair.Second) == 0f)
            && MatrixDistance(first.Pose.PaletteOf(0)[3], second.Pose.PaletteOf(0)[3]) > 1e-3f);
        report.Check("Instances share the asset data (one skeleton, clips and meshes; separate pose buffers)",
            ReferenceEquals(first.Data, second.Data) && !ReferenceEquals(first.Pose, second.Pose));
    }

    // ============================================================================================ bad data

    public static void BadData(TestReport report, string dir)
    {
        var missingBone = ModelAnimationData.Build(SyntheticModel(boneNames: ["A", "Nowhere"]));
        report.Check("Invalid bone reference: reported, the mesh is drawn rigidly (no skin), nothing crashes",
            missingBone.Skins[0] is null && missingBone.Notes.Any(note => note.Contains("'Nowhere' is not a node", StringComparison.Ordinal)));
        var asset = new ModelAsset("Assets/Models/Bad/Bad.glb", SyntheticModel(boneNames: ["A", "Nowhere"]));
        report.Check("Invalid bone reference: the asset still loads and draws its mesh by its node", asset.Draws.Count == 1 && !asset.Draws[0].Skinned);

        var singular = ModelAnimationData.Build(SyntheticModel(inverseBind: new Matrix4(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1)));
        report.Check("Invalid bind pose (an inverse bind matrix that cannot be inverted): reported, rest pose",
            singular.Skins[0] is null && singular.Notes.Any(note => note.Contains("bind pose", StringComparison.Ordinal)));

        var tooMany = ModelAnimationData.Build(SyntheticModel(boneNames: Enumerable.Repeat("A", SkinningLimits.MaxPaletteSize + 1).ToArray()));
        report.Check($"Too many bones (more than {SkinningLimits.MaxPaletteSize} per mesh): reported, rest pose",
            tooMany.Skins[0] is null && tooMany.Notes.Any(note => note.Contains("at most 256", StringComparison.Ordinal)));

        var data = ModelAnimationData.Build(SyntheticModel());
        report.Check("A track naming a node the model does not have: reported, ignored (the clip keeps its other tracks)",
            data.FindClip("Move") is { TrackCount: 3 } && data.Notes.Any(note => note.Contains("name a node the model does not have", StringComparison.Ordinal)));
        report.Check("A clip without keyframes: kept in the list (as in the file), reported",
            data.FindClip("Empty") is not null && data.Notes.Any(note => note.Contains("'Empty' has no keyframes", StringComparison.Ordinal)));
        report.Check("Duplicate clip names: reported; by name the first plays, by number any",
            data.Clips.Count(clip => clip.Name == "Twin") == 2 && data.FindClip("Twin")!.Index < data.Clips.Last(clip => clip.Name == "Twin").Index
            && data.Notes.Any(note => note.Contains("are called 'Twin'", StringComparison.Ordinal)));
        report.Check("An unnamed clip is called \"Animation N\" (its number)", data.Clips.Any(clip => clip.Name == $"Animation {clip.Index + 1}" && clip.FileName == ""));

        // A glTF whose keyframe times are out of order: Assimp's validation refuses it, and Import says why in words a user
        // understands (Wisko's own sorting of out-of-order keys only matters for files Assimp lets through).
        Directory.CreateDirectory(dir);
        var unsorted = Path.Combine(dir, "unsorted.gltf");
        File.WriteAllText(unsorted, UnsortedGltf());
        using (var importer = new AssimpModelImporter())
        {
            var importProject = Path.Combine(dir, "ImportProject");
            Directory.CreateDirectory(importProject);
            var imported = new ProjectModelImporter(importer).TryImport(unsorted, importProject, out _, out var importError);
            report.Check("Invalid keyframe timing (glTF keys out of order): the import is refused with an understandable message, nothing is copied",
                !imported && importError == "unsorted.gltf has invalid animation data (keyframes or bones; see the log)"
                && !Directory.Exists(Path.Combine(importProject, "Assets", "Models", "unsorted")), importError);

            // A damaged GLB: refused with a message (no crash); the cache answers null with the reason.
            var broken = Path.Combine(dir, "Broken.glb");
            var bytes = File.ReadAllBytes(ModelFile("Fox", "Fox.glb"));
            File.WriteAllBytes(broken, bytes[..(bytes.Length / 3)]);
            string? message = null;
            try
            {
                importer.Import(broken);
            }
            catch (ModelImportException ex)
            {
                message = ex.Message;
            }

            report.Check("A damaged model file is refused with a message (ModelImportException), the editor keeps running", message is not null, message);
        }

        var project = Path.Combine(dir, "Project");
        Directory.CreateDirectory(Path.Combine(project, "Assets", "Models", "Broken"));
        File.Copy(Path.Combine(dir, "Broken.glb"), Path.Combine(project, "Assets", "Models", "Broken", "Broken.glb"));
        using var cache = new ModelAssetCache();
        cache.SetProject(project);
        var loaded = cache.Load("Assets/Models/Broken/Broken.glb", out var error);
        var missing = cache.Load("Assets/Models/Gone/Gone.glb", out var missingError);
        report.Check("Project assets: a damaged or missing model file loads as null with a reason (its instances become placeholders)",
            loaded is null && error is not null && missing is null && missingError == "file not found", $"{error} | {missingError}");
    }

    // A model of four nodes (Root > A > B, Root > C, and D), one mesh skinned to A and B, and clips: "Move" (A moves, turns
    // and scales; B steps; C one key; plus a track for a node that does not exist), "OnlyB", "Empty", "Twin" twice and an
    // unnamed one.
    public static ImportedModel SyntheticModel(string[]? boneNames = null, Matrix4? inverseBind = null)
    {
        static ImportedNode Node(string name, Vector3 t, IReadOnlyList<int> meshes, params ImportedNode[] children) =>
            new() { Name = name, LocalTransform = Matrix4.CreateTranslation(t), MeshIndices = meshes, Children = children };

        var root = Node("Root", Vector3.Zero, [0], Node("A", new Vector3(0f, 1f, 0f), [], Node("B", new Vector3(0f, 1f, 0f), [])), Node("C", Vector3.Zero, []), Node("D", Vector3.Zero, []));
        boneNames ??= ["A", "B"];
        var positions = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 2f, 0f) };
        var skin = new ImportedSkin
        {
            BoneNames = boneNames,
            InverseBindMatrices = boneNames.Select((_, i) => inverseBind ?? Matrix4.CreateTranslation(new Vector3(0f, -1f - i, 0f))).ToArray(),
            Joints = [0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0],
            Weights = [1f, 0, 0, 0, 0.5f, 0.5f, 0, 0, 1f, 0, 0, 0]
        };
        var mesh = new ImportedMesh
        {
            Name = "Skinned",
            Positions = positions,
            Normals = [new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 1f)],
            Indices = [0, 1, 2],
            MaterialIndex = 0,
            Bounds = positions.Aggregate(ImportedBounds.Empty, (bounds, p) => bounds.Include(p)),
            Skin = skin
        };

        static ImportedChannel<Vector3> Vectors(bool step, params (float T, Vector3 V)[] keys) => new(keys.Select(k => k.T).ToArray(), keys.Select(k => k.V).ToArray(), step);
        static ImportedChannel<System.Numerics.Quaternion> Rotations(params (float T, System.Numerics.Quaternion Q)[] keys) => new(keys.Select(k => k.T).ToArray(), keys.Select(k => k.Q).ToArray(), false);
        static ImportedNodeTrack Track(string node, ImportedChannel<Vector3>? t = null, ImportedChannel<System.Numerics.Quaternion>? r = null, ImportedChannel<Vector3>? s = null) =>
            new() { NodeName = node, Translation = t ?? ImportedChannel<Vector3>.Empty, Rotation = r ?? ImportedChannel<System.Numerics.Quaternion>.Empty, Scale = s ?? ImportedChannel<Vector3>.Empty };

        var quarter = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2f);
        var move = new ImportedAnimation
        {
            Name = "Move",
            DurationSeconds = 1d,
            Tracks =
            [
                Track("A", Vectors(false, (0f, Vector3.Zero), (1f, new Vector3(2f, 0f, 0f))), Rotations((0f, System.Numerics.Quaternion.Identity), (1f, quarter)),
                    Vectors(false, (0f, new Vector3(1f, 1f, 1f)), (1f, new Vector3(3f, 3f, 3f)))),
                Track("B", Vectors(true, (0f, Vector3.Zero), (1f, new Vector3(0f, 5f, 0f)))),
                Track("C", Vectors(false, (0f, new Vector3(0f, 0f, 7f)))),
                Track("Ghost", Vectors(false, (0f, Vector3.Zero)))
            ]
        };
        var onlyB = new ImportedAnimation { Name = "OnlyB", DurationSeconds = 1d, Tracks = [Track("B", Vectors(false, (0f, new Vector3(0f, 1f, 0f))))] };
        var empty = new ImportedAnimation { Name = "Empty", DurationSeconds = 1d, Tracks = [] };
        var twin = new ImportedAnimation { Name = "Twin", DurationSeconds = 0.5d, Tracks = [Track("C", Vectors(false, (0f, Vector3.Zero)))] };
        var unnamed = new ImportedAnimation { Name = "", DurationSeconds = 0.5d, Tracks = [Track("C", Vectors(false, (0f, Vector3.Zero)))] };
        return new ImportedModel
        {
            SourcePath = "synthetic.glb",
            SourceFormat = "glb",
            Meshes = [mesh],
            Materials = [new ImportedMaterial { Name = "M", BaseColor = ColorRgba.White }],
            EmbeddedTextures = [],
            RootNode = root,
            Bounds = mesh.Bounds,
            Warnings = [],
            Animations = [move, onlyB, empty, twin, twin, unnamed]
        };
    }

    // A one-triangle glTF whose node is animated with translation keys at 0, 2 and 1 seconds (out of order).
    private static string UnsortedGltf()
    {
        var floats = new List<float>();
        floats.AddRange([0, 0, 0, 1, 0, 0, 0, 1, 0]);      // positions: 3 x vec3 (36 bytes)
        floats.AddRange([0, 2, 1]);                        // times: 3 floats (12 bytes)
        floats.AddRange([0, 0, 0, 2, 0, 0, 1, 0, 0]);      // translations: 3 x vec3 (36 bytes)
        var bytes = new byte[floats.Count * 4];
        Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, bytes.Length);
        var data = Convert.ToBase64String(bytes);
        return $$"""
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [ { "nodes": [ 0 ] } ],
              "nodes": [ { "name": "Mover", "mesh": 0 } ],
              "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 } } ] } ],
              "animations": [ { "name": "Slide", "samplers": [ { "input": 1, "output": 2, "interpolation": "LINEAR" } ],
                                "channels": [ { "sampler": 0, "target": { "node": 0, "path": "translation" } } ] } ],
              "buffers": [ { "byteLength": {{bytes.Length}}, "uri": "data:application/octet-stream;base64,{{data}}" } ],
              "bufferViews": [ { "buffer": 0, "byteOffset": 0, "byteLength": 36 }, { "buffer": 0, "byteOffset": 36, "byteLength": 12 },
                               { "buffer": 0, "byteOffset": 48, "byteLength": 36 } ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                { "bufferView": 1, "componentType": 5126, "count": 3, "type": "SCALAR", "min": [0], "max": [2] },
                { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3" }
              ]
            }
            """;
    }

    private static Vector3[] Snapshot(ModelAnimationData data, ModelAnimator animator) =>
        Enumerable.Range(0, data.Skeleton.NodeCount).Select(n => new Vector3(animator.Pose.GlobalOf(n).M14, animator.Pose.GlobalOf(n).M24, animator.Pose.GlobalOf(n).M34)).ToArray();

    // The largest distance any node moved between two snapshots, relative to the model's rest size.
    private static float MaxNodeJump(ModelAnimationData data, Vector3[] a, Vector3[] b)
    {
        var jump = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            jump = MathF.Max(jump, (a[i] - b[i]).Length);
        }

        return jump;
    }

    // Whether a node or one of its ancestors is moved by some clip.
    private static bool MovedByAnimation(ModelAsset asset, int node)
    {
        for (var n = node; n >= 0; n = asset.Animation.Skeleton.ParentOf(n))
        {
            if (asset.Animation.Clips.Any(clip => clip.Animates(n)))
            {
                return true;
            }
        }

        return false;
    }

    public static float MatrixDistance(Matrix4 a, Matrix4 b) =>
        MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13) + MathF.Abs(a.M14 - b.M14)
        + MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M24 - b.M24)
        + MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) + MathF.Abs(a.M34 - b.M34);

    // The largest distance between a skinned vertex at rest and where its node would put the same unskinned vertex, and the
    // model's size.
    private static float RestPoseError(ModelAsset asset, out float size)
    {
        size = asset.Bounds.Size.Length;
        var worst = 0f;
        foreach (var draw in asset.Draws.Where(draw => draw.Skinned))
        {
            var mesh = asset.Model.Meshes[draw.MeshIndex];
            var node = asset.Animation.Skeleton.RestGlobal(draw.Node);
            var rest = asset.RestPositionsOf(draw);
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                worst = MathF.Max(worst, (rest[v] - AnimationMath.TransformPoint(node, mesh.Positions[v])).Length);
            }
        }

        return worst;
    }
}
