using Wisko.Tests.Checks;
using Wisko.Tests.Gui;

namespace Wisko.Tests;

/// <summary>
/// Runs Wisko's checks against the real engine. No arguments: the headless suite. Options:
/// <c>--gui-smoke</c> the real editor, scripted (Windows; needs a desktop and a GPU);
/// <c>--screenshots &lt;dir&gt;</c> PNGs from the GUI smoke test; <c>--work &lt;dir&gt;</c> where test projects are written
/// (default: a new temporary folder, removed afterwards); <c>--keep</c> keeps the work folder.
/// Exit code 0 when every check passed.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var gui = args.Contains("--gui-smoke");
        var keep = args.Contains("--keep");
        var work = ValueOf(args, "--work") ?? Path.Combine(Path.GetTempPath(), $"wisko-tests-{Guid.NewGuid():N}");
        var screenshots = ValueOf(args, "--screenshots");
        var only = ValueOf(args, "--only");
        var unknown = args.Where((arg, i) => arg.StartsWith("--", StringComparison.Ordinal)
            && arg is not ("--gui-smoke" or "--keep" or "--work" or "--screenshots" or "--only" or "--make-demo" or "--make-anim-demo" or "--make-v5-demo" or "--make-graphics-demo" or "--bench-graphics" or "--mode"
                or "--make-template-previews" or "--make-release-demo" or "--crash-save-child" or "--crash-journal-child" or "--frame-pacing-probe")).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Unknown option {unknown[0]}. Options: --gui-smoke, --screenshots <dir>, --work <dir>, --keep, --only <section text>, "
                + "--make-demo <projects folder>, --make-anim-demo <projects folder>, --make-v5-demo <projects folder>, --make-graphics-demo <projects folder>, "
                + "--make-release-demo <projects folder>, --make-template-previews [folder], "
                + "--bench-graphics <result file> [--mode classic|low|medium|high|ultra]");
            return 2;
        }

        // --crash-save-child: internal to the save-safety crash test (saves a scene until killed).
        if (ValueOf(args, "--crash-save-child") is { } crashPath)
        {
            return SaveSafetyChecks.RunCrashChild(crashPath);
        }

        // --crash-journal-child: internal to the project-save journal crash test (saves four files until killed).
        if (ValueOf(args, "--crash-journal-child") is { } journalFolder)
        {
            return ProjectSaveJournalChecks.RunCrashChild(journalFolder);
        }

        // --frame-pacing-probe: internal to the real DwmFlush check (measures 60 paced frames in a fresh process).
        if (ValueOf(args, "--frame-pacing-probe") is { } probeMode)
        {
            return FramePacingChecks.RunProbeProcess(probeMode);
        }

        // --bench-graphics: the Graphics V1 benchmark (nine scenes, one renderer mode) into a result file; no tests run.
        if (args.Contains("--bench-graphics"))
        {
            var result = ValueOf(args, "--bench-graphics");
            if (result is null)
            {
                Console.Error.WriteLine("--bench-graphics needs the result file");
                return 2;
            }

            return GraphicsBenchmark.Run(Path.GetFullPath(result), work, (ValueOf(args, "--mode") ?? "medium").ToLowerInvariant());
        }

        // --make-template-previews: renders the New World screen's template pictures with Wisko (Engine/Assets/TemplatePreviews
        // unless a folder is given); opens a window. No tests run.
        if (args.Contains("--make-template-previews"))
        {
            var folder = ValueOf(args, "--make-template-previews") is { } given && !given.StartsWith("--", StringComparison.Ordinal)
                ? given
                : EditorFlowSmokeTests.DefaultTemplatePreviewFolder();
            if (folder is null)
            {
                Console.Error.WriteLine("--make-template-previews: give the folder (the repository was not found above the test binaries)");
                return 2;
            }

            return EditorFlowSmokeTests.MakeTemplatePreviews(Path.GetFullPath(folder));
        }

        // --make-release-demo: writes the first public release's showcase project (drag, typed rotation, chat); never
        // replaces an existing project. No tests run.
        if (args.Contains("--make-release-demo"))
        {
            var projects = ValueOf(args, "--make-release-demo");
            if (projects is null)
            {
                Console.Error.WriteLine("--make-release-demo needs the projects folder (e.g. %USERPROFILE%\\Documents\\Wisko\\Projects)");
                return 2;
            }

            return ReleaseShowcase.Make(projects);
        }

        // --make-graphics-demo: writes the Graphics V1 showcase project (examples/graphics-demo); no tests run.
        if (args.Contains("--make-graphics-demo"))
        {
            var projects = ValueOf(args, "--make-graphics-demo");
            if (projects is null)
            {
                Console.Error.WriteLine("--make-graphics-demo needs the projects folder (e.g. %USERPROFILE%\\Documents\\Wisko\\Projects)");
                return 2;
            }

            return GraphicsShowcase.Make(projects);
        }

        // --make-v5-demo: writes the Lua Capabilities V5 showcase project (examples/gameplay-systems); no tests run.
        if (args.Contains("--make-v5-demo"))
        {
            var projects = ValueOf(args, "--make-v5-demo");
            if (projects is null)
            {
                Console.Error.WriteLine("--make-v5-demo needs the projects folder (e.g. %USERPROFILE%\\Documents\\Wisko\\Projects)");
                return 2;
            }

            return LuaV5Demo.Make(projects);
        }

        // --make-anim-demo: writes the Animation System V1 demonstration project (examples/animation-demo); no tests run.
        if (args.Contains("--make-anim-demo"))
        {
            var projects = ValueOf(args, "--make-anim-demo");
            if (projects is null)
            {
                Console.Error.WriteLine("--make-anim-demo needs the projects folder (e.g. %USERPROFILE%\\Documents\\Wisko\\Projects)");
                return 2;
            }

            return AnimationDemo.Make(projects);
        }

        // --make-demo: writes the Lua V4 demonstration project (examples/gameplay-basics) into a projects folder; no tests run.
        if (args.Contains("--make-demo"))
        {
            var projects = ValueOf(args, "--make-demo");
            if (projects is null)
            {
                Console.Error.WriteLine("--make-demo needs the projects folder (e.g. %USERPROFILE%\\Documents\\Wisko\\Projects)");
                return 2;
            }

            return LuaV4Demo.Make(projects);
        }

        Directory.CreateDirectory(work);
        var report = new TestReport();

        // --only: just the headless sections whose title contains the text (case-insensitive).
        void Section(string title, Action body)
        {
            if (only is null || title.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                report.Section(title, body);
            }
        }

        try
        {
            if (gui)
            {
                GuiSmokeTest.Run(report, work, screenshots, only);
            }
            else
            {
                Section("Lua runtime and sandbox", () => LuaChecks.Sandbox(report, Path.Combine(work, "lua")));
                Section("Script API catalog vs live sandbox", () => LuaChecks.Catalog(report, Path.Combine(work, "catalog")));
                Section("Play Mode physics, gameplay callbacks, GUI scripting, Stop restore", () => PlayModeChecks.Gameplay(report, Path.Combine(work, "play")));
                Section("Script faults and runaway protection", () => PlayModeChecks.Faults(report, Path.Combine(work, "faults")));
                Section("Gameplay scripting: camera and movement", () => GameplayScriptingChecks.CameraAndMovement(report, Path.Combine(work, "movement")));
                Section("Gameplay scripting: script communication", () => GameplayScriptingChecks.Communication(report, Path.Combine(work, "communication")));
                Section("Gameplay scripting: physics filters, gravity, projectiles", () => GameplayScriptingChecks.PhysicsAndProjectiles(report, Path.Combine(work, "projectiles")));
                Section("Gameplay scripting: Play-only attachment", () => GameplayScriptingChecks.Attachment(report, Path.Combine(work, "attachment")));
                Section("V4: gravity scale, damping, moving and spinning Parts", () => GameplayV4Checks.MotionPrimitives(report, Path.Combine(work, "v4-motion")));
                Section("V4: conveyor belts", () => GameplayV4Checks.Conveyors(report, Path.Combine(work, "v4-conveyors")));
                Section("V4: collision groups and pass-through", () => GameplayV4Checks.CollisionRules(report, Path.Combine(work, "v4-collision")));
                Section("V4: joints (weld, hinge motor and limits, ball socket)", () => GameplayV4Checks.Joints(report, Path.Combine(work, "v4-joints")));
                Section("V4: object space, rotation helpers, attribute signals, math", () => GameplayV4Checks.ObjectSpaceAndSignals(report, Path.Combine(work, "v4-space")));
                Section("V4: flight and a raycast car (Lua-only examples)", () => GameplayV4Checks.FlightAndVehicle(report, Path.Combine(work, "v4-vehicle")));
                Section("V4: World panel groups (operations, undo, Scene.json, Play)", () => EditorV4Checks.Groups(report, Path.Combine(work, "v4-groups")));
                Section("V4: GUI Image elements and UI.Create options", () => EditorV4Checks.GuiImages(report, Path.Combine(work, "v4-gui")));
                Section("V4 systems: mining, resource drops, regrowth (Lua only)", () => GameplaySystemsV4Checks.Resources(report, Path.Combine(work, "v4-resources")));
                Section("V4 systems: stats, coins, objectives and a Lua HUD", () => GameplaySystemsV4Checks.SurvivalHud(report, Path.Combine(work, "v4-hud")));
                Section("V4 systems: damage, knockback, timed effects, X-ray targeting", () => GameplaySystemsV4Checks.StatusEffects(report, Path.Combine(work, "v4-effects")));
                Section("V4 systems: collision acceptance (solid, pass-through, selective, trigger)", () => GameplaySystemsV4Checks.CollisionAcceptance(report, Path.Combine(work, "v4-collision-acceptance")));
                Section("V4 systems: eating, drinking and feeding", () => GameplaySystemsV4Checks.Consumables(report, Path.Combine(work, "v4-consumables")));
                Section("V4 systems: getting in and out of the car, chase camera", () => GameplaySystemsV4Checks.CarSeatAndCamera(report, Path.Combine(work, "v4-carseat")));
                Section("V4 demo: the whole demonstration scene, headless", () => GameplaySystemsV4Checks.Demo(report, Path.Combine(work, "v4-demo")));
                Section("Project persistence and deletion", () => ProjectChecks.Persistence(report, Path.Combine(work, "projects")));
                Section("Script assets and script file store", () => ProjectChecks.ScriptAssets(report, Path.Combine(work, "scripts")));
                Section("Assimp model import", () => ModelImportChecks.Run(report, Path.Combine(work, "models")));
                Section("Visual C++ runtime (Windows): detection, explanation, no crash", () => VisualCppRuntimeChecks.Run(report, Path.Combine(work, "vc-runtime")));
                Section("Animation: import of real rigged models (skeletons, skins, clips)", () => AnimationChecks.Import(report));
                Section("Animation: transforms, keyframe sampling, pose and skin matrices", () => AnimationChecks.Evaluation(report));
                Section("Animation: playback timing (30/60/144 fps), loop, one-shot, pause, speed, seek", () => AnimationChecks.Playback(report));
                Section("Animation: crossfades, interrupted transitions, missing channels", () => AnimationChecks.Blending(report));
                Section("Animation: independent instances of one model", () => AnimationChecks.Instances(report));
                Section("Animation: bad data (bones, bind poses, limits, tracks, files)", () => AnimationChecks.BadData(report, Path.Combine(work, "anim-bad")));
                Section("Animation: Lua API on real models (Play, errors, events, destroy, clone, Stop)", () => AnimationPlayChecks.LuaApi(report, Path.Combine(work, "anim-lua")));
                Section("Animation: Play on start, scene file, duplicates, missing files, reimport", () => AnimationPlayChecks.PlayOnStartAndPersistence(report, Path.Combine(work, "anim-persist")));
                Section("Animation demo: the demonstration scene and its character, headless", () => AnimationPlayChecks.Demo(report, Path.Combine(work, "anim-demo")));
                Section("V5: Time.OnUpdate, time scale, Random, Noise, Path", () => GameplayV5Checks.Foundations(report, Path.Combine(work, "v5-foundations")));
                Section("V5: game clock, schedules and Wisko.DayNight", () => GameplayV5Checks.Clock(report, Path.Combine(work, "v5-clock")));
                Section("V5: game saves (types, errors, slots, damage, safe writes, next session)", () => GameplayV5Checks.Saves(report, Path.Combine(work, "v5-saves")));
                Section("V5: debug lines and particles", () => GameplayV5Checks.DrawingAndParticles(report, Path.Combine(work, "v5-draw")));
                Section("V5: cameras (switching, blends, shake, paths, events)", () => GameplayV5Checks.Cameras(report, Path.Combine(work, "v5-cameras")));
                Section("V5: trigger connections, Teleport, pads and checkpoints", () => GameplayV5Checks.TriggersAndTeleport(report, Path.Combine(work, "v5-teleport")));
                Section("V5: bones and equipment on an animated model", () => GameplayV5Checks.BonesAndEquipment(report, Path.Combine(work, "v5-bones")));
                Section("V5: navigation (paths, reachability, rebakes, agents: patrol, follow)", () => GameplayV5Checks.Navigation(report, Path.Combine(work, "v5-nav")));
                Section("V5: perception (view, sight, sensors, noise)", () => GameplayV5Checks.Perception(report, Path.Combine(work, "v5-sight")));
                Section("V5 modules: dialogue, quests, crafting, building, logic, racing, generation, weather", () => GameplayV5Checks.Modules(report, Path.Combine(work, "v5-modules")));
                Section("V5 showcase: the whole demonstration scene headless, save and reopen", () => GameplayV5Checks.Demo(report, Path.Combine(work, "v5-demo")));
                Section("Graphics V1: colour spaces, materials, presets, lights, model overrides", () => GraphicsDataChecks.ColorsAndMaterials(report));
                Section("Graphics V1: Graphics.json, quality presets, editor preferences", () => GraphicsDataChecks.SettingsFiles(report, Path.Combine(work, "gfx-settings")));
                Section("Graphics V1: Scene.json materials and lights, snapshots, duplicates", () => GraphicsDataChecks.ScenePersistence(report, Path.Combine(work, "gfx-scene")));
                Section("Graphics V1: Atmosphere environment lighting (ambient mode, ground, reflections)", () => GraphicsDataChecks.AtmosphereEnvironment(report, Path.Combine(work, "gfx-atmosphere")));
                Section("Graphics V1: Khronos sample models import (alpha modes, sides, wrap modes, emission, tangents)", () => GraphicsImportChecks.SampleModels(report, Path.Combine(work, "gfx-import")));
                Section("Graphics V1: Lua (object.Material, object.Light, AddLight, Graphics, Atmosphere environment, Stop)", () => GraphicsLuaChecks.Run(report, Path.Combine(work, "gfx-lua")));
                Section("Graphics V1: editor panels (Material, Light and Rendering edits and rows)", () => GraphicsEditingChecks.Sheets(report));
                Section("Graphics V1: Environment & Graphics panel (every Atmosphere and Graphics setting in exactly one tab, edits as before)",
                    () => GraphicsEditingChecks.EnvironmentPanel(report));
                Section("First release: template previews (pictures, fingerprints, densities, layout)", () => ReleasePrepChecks.TemplatePreviews(report));
                Section("First release: deletable baseplate (Scene.json, editor rules, undo, Play, Lua raycasts)", () => ReleasePrepChecks.Baseplate(report, Path.Combine(work, "release-baseplate")));
                Section("First release: numeric rotation (input, normalization, orientation, undo, save)", () => ReleasePrepChecks.Rotation(report, Path.Combine(work, "release-rotation")));
                Section("First release: dragging a Part's body (no jump, grab offset, level plane, groups, overlap, cancel)", () => ReleasePrepChecks.BodyDrag(report));
                Section("First release: chat text cleaning (line breaks, controls, bidi, invisible, combining marks, length, reserved names)", () => ChatChecks.Sanitizer(report));
                Section("First release: chat service (rate limit, channels, history, system/NPC, names, simulated remote transport: mute, block, forgeries, flood)", () => ChatChecks.Service(report));
                Section("First release: chat Lua API (next-frame delivery, kinds, impersonation, channels, history, filters, rate limit, failing listener, Stop)", () => ChatChecks.LuaApi(report, Path.Combine(work, "release-chat")));
                Section("First release: chat filters fail closed", () => ChatChecks.FilterFailsClosed(report, Path.Combine(work, "release-chat-filter")));
                Section("First release: GUI TextBox (focus, typing, caret, paste, Enter, Escape, pointer, UI.json)", () => ChatChecks.TextBox(report, Path.Combine(work, "release-textbox")));
                Section("First release: TextBox from Lua (UI.Create, OnSubmit, OnFocusLost, Input.IsTyping, UI.MeasureText)", () => ChatChecks.TextBoxLua(report, Path.Combine(work, "release-textbox-lua")));
                Section("First release: the default chat window (Wisko.ChatUI: lines, wrapping, colours, focus key, sending, notices, one owner)", () => ChatChecks.DefaultUi(report, Path.Combine(work, "release-chat-ui")));
                Section("First release: chat stress (12,000 messages, bounded history)", () => ChatChecks.Stress(report, Path.Combine(work, "release-chat-stress")));
                Section("Chat: message Data (checks, cleaning, Send, Post, transports)", () => ChatRefinementChecks.Data(report));
                Section("Chat: message Data from Lua (styling values, history, copies, refusals; never the sender or kind)",
                    () => ChatRefinementChecks.DataLua(report, Path.Combine(work, "chat-data")));
                Section("Chat: the default window's placement and colours, and Data.Color", () => ChatRefinementChecks.DefaultUiOptions(report, Path.Combine(work, "chat-styled")));
                Section("Chat: no chat interface unless a script asks (new projects, games that never use Chat)",
                    () => ChatRefinementChecks.NoChatUnlessAsked(report, Path.Combine(work, "chat-none")));
                Section("Chat: the custom chat example (examples/chat/CustomChat.lua) as it ships",
                    () => ChatRefinementChecks.CustomChatExample(report, Path.Combine(work, "chat-custom")));
                Section("First release: stress (1,000 Parts: group body drag, drag among them and cancel, typed rotation, save without a baseplate)",
                    () => ReleasePrepIntegrationChecks.Stress(report, Path.Combine(work, "release-stress")));
                Section("First release: all together in Play (no baseplate, typed rotation, chat; Stop; save and read back)",
                    () => ReleasePrepIntegrationChecks.AllFeatures(report, Path.Combine(work, "release-all")));
                Section("First release: the release showcase (made once, refused twice, its chat script in Play)",
                    () => ReleasePrepIntegrationChecks.Showcase(report, Path.Combine(work, "release-showcase")));
                Section("Save safety: atomic file writes (failures at every stage, disk full, held and read-only files, stale temporary files, repeated and overlapping saves)",
                    () => SaveSafetyChecks.FileWrites(report, Path.Combine(work, "save-safety")));
                Section("Save safety: every project and user file writer (Scene, Atmosphere, UI, Graphics, preferences, scripts, game saves, project.json, thumbnails)",
                    () => SaveSafetyChecks.Writers(report, Path.Combine(work, "save-writers")));
                Section("Save safety: a saving process killed at random moments (real crashes)",
                    () => SaveSafetyChecks.CrashWhileSaving(report, Path.Combine(work, "save-crash")));
                Section("Project save journal: one transaction for every project file (normal saves, refusals, compatibility)",
                    () => ProjectSaveJournalChecks.Basics(report, Path.Combine(work, "journal-basics")));
                Section("Project save journal: interrupted at every stage, then recovered (all old or all new)",
                    () => ProjectSaveJournalChecks.Interruptions(report, Path.Combine(work, "journal-interrupt")));
                Section("Project save journal: full disk, failures after the commit point, simultaneous saves",
                    () => ProjectSaveJournalChecks.Failures(report, Path.Combine(work, "journal-failures")));
                Section("Project save journal: malformed and hostile journals, files changed after a crash, links",
                    () => ProjectSaveJournalChecks.Safeguards(report, Path.Combine(work, "journal-safeguards")));
                Section("Project save journal: a process saving four files killed at random moments (real crashes)",
                    () => ProjectSaveJournalChecks.CrashWhileSaving(report, Path.Combine(work, "journal-crash")));
                Section("Lua memory limit: runaway tables, huge strings, caught errors, coroutines, Update; memory comes back",
                    () => LuaMemoryChecks.Enforcement(report));
                Section("Lua memory limit: host calls at the limit (events, GUI, queries) in Play",
                    () => LuaMemoryChecks.HostCallsAtTheLimit(report, Path.Combine(work, "lua-memory-host")));
                Section("Lua memory limit: sessions and scripts created and released (no leaks, reclamation)", () => LuaMemoryChecks.Lifecycle(report));
                Section("Lua memory limit: the per-computer preference (default, clamping, broken files)",
                    () => LuaMemoryChecks.Preferences(report, Path.Combine(work, "lua-memory-prefs")));
                Section("Lua memory limit: the default against every demo and showcase (20 s of Play each)",
                    () => LuaMemoryChecks.DefaultAgainstDemos(report, Path.Combine(work, "lua-memory-demos")));
                Section("Lua memory limit: the accounting's cost (information)", () => LuaMemoryChecks.Cost(report, Path.Combine(work, "lua-memory-cost")));
                Section("Graphics V1: texture import into a project (copy, reuse, refusals)", () => GraphicsEditingChecks.TextureImport(report, Path.Combine(work, "gfx-textures")));
                Section("Lua syntax validator", () => LuaChecks.SyntaxValidator(report));
                Section("Branding: the icon pipeline on synthetic shapes (backgrounds, halos, sizes, .ico, .icns, refusals)",
                    () => BrandingPipelineChecks.Run(report, Path.Combine(work, "branding")));
                Section("Windows frame pacing (compositor wait)", () => FramePacingChecks.Run(report));
                Section("macOS fix pass: editor shortcuts (Windows Ctrl unchanged, macOS Command, layout letters, Save)",
                    () => MacFixPassChecks.Shortcuts(report));
                Section("macOS fix pass: World panel rename takes the OS's text (AZERTY, accents, Unicode, editing keys)",
                    () => MacFixPassChecks.RenameText(report));
                Section("macOS fix pass: starting without a display (bounded wait, clean refusal)", () => MacFixPassChecks.NoDisplay(report));
                Section("macOS fix pass: renderer capabilities (Metal Classic labels, settings kept, notice, New World note)",
                    () => MacFixPassChecks.Capabilities(report));
                Section("macOS fix pass: bgfx vertex budget, MSAA flags, OpenGL frame pacing, texture formats",
                    () => MacFixPassChecks.BudgetPacingFormats(report, Path.Combine(work, "fixpass-formats")));
            }
        }
        finally
        {
            if (!keep)
            {
                TryDelete(work);
            }
            else
            {
                Console.WriteLine($"\nWork folder kept: {work}");
            }
        }

        return report.PrintSummary() ? 0 : 1;
    }

    private static string? ValueOf(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INFO] Could not remove the work folder {folder}: {ex.Message}");
        }
    }
}
