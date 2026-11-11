using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;

namespace Wisko.Tests.Gui;

// First public release preparation in the real editor, scripted: the New World screen's template pictures (loaded,
// matching the templates, keyboard and mouse, cancel, opened again and again, a small window), and the release
// improvements' editor workflows.
internal static partial class EditorFlowSmokeTests
{
    private static void RunReleasePrep(Action<string, Action> section, string work, string? screenshotDir, TestReport report)
    {
        section("GUI: First release: New World template previews (pictures, keyboard, create, cancel, reopen, small window), scripted input", () =>
        {
            var root = Path.Combine(work, "release-templates");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            RunSession(report, root, "template screen flow", (app, window) => new TemplateScreenFlow(report, app, window, screenshotDir, Path.Combine(root, "Projects")), (_, _, _) => { });
        });
        section("GUI: First release: deleting the baseplate (World panel, Delete, Ctrl+click, context menu, undo, Play, save, reopen, old project), scripted input", () =>
        {
            var root = Path.Combine(work, "release-baseplate");
            var old = CreateProject(Path.Combine(root, "Projects"), BaseplateFlow.OldProjectName, [("Old Crate", new Engine.Math.Vector3(0f, 1.5f, 0f))]);
            RunSession(report, root, "baseplate flow", (app, window) => new BaseplateFlow(report, app, window, screenshotDir), BaseplateFlow.After);
            report.Check("Baseplate: the old project's Scene.json was never rewritten (still the plain list)", File.ReadAllText(old.ScenePath).TrimStart().StartsWith('['));
        });
        section("GUI: First release: numeric rotation (World panel fields, typing, Tab, Escape, refusal, undo, gizmo sync, two Parts, save, reopen), scripted input", () =>
        {
            var root = Path.Combine(work, "release-rotation");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            RunSession(report, root, "rotation flow", (app, window) => new RotationFlow(report, app, window, screenshotDir), RotationFlow.After);
        });
        section("GUI: First release: moving objects with Rotate and Resize active (body drag, handles, undo, Escape, groups, camera, no baseplate, Move unchanged, save), scripted input", () =>
        {
            var root = Path.Combine(work, "release-drag");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            RunSession(report, root, "drag tools flow", (app, window) => new DragToolsFlow(report, app, window, screenshotDir), (_, _, _) => { });
        });
        section("GUI: First release: text chat in Play (default chat window, / to type, OS text input, keys held back while typing, Enter, Escape, click), scripted input", () =>
        {
            var root = Path.Combine(work, "release-chat");
            ChatFlow.CreateProject(Path.Combine(root, "Projects"));
            RunSession(report, root, "chat flow", (app, window) => new ChatFlow(report, app, window, screenshotDir), (_, _, _) => { });
        });
    }

    private sealed class ChatFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string ProjectName = "Chat Test";

        private const string Script = """
            local wPresses = 0
            function Start()
              Chat.EnableDefaultUI()
              Chat.Post("Welcome to the chat test. Press / to talk.")
              Chat.Post("Halt! Who goes there?", { SenderName = "Guard" })
              Chat.OnMessage(function(m) print("[ChatTest] " .. m.Kind .. " " .. m.Sender.Name .. ": " .. m.Text) end)
            end
            function Update()
              if Input.IsKeyPressed("W") then
                wPresses = wPresses + 1
                print("[ChatTest] W pressed " .. wPresses)
              end
            end
            """;

        public static void CreateProject(string projectsRoot)
        {
            Directory.CreateDirectory(projectsRoot);
            var store = new ProjectStore(projectsRoot);
            var workspace = new Engine.World.Workspace();
            var host = new Engine.World.Part("ChatHost", new Engine.Math.Vector3(0f, 1.5f, 0f), new Engine.Math.Vector3(3f, 3f, 3f), new Engine.Math.Vector3(0.3f, 0.6f, 0.9f));
            if (!Engine.World.ScriptReference.TryCreate("Assets/Scripts/Chat.lua", out var reference))
            {
                throw new InvalidOperationException("script reference");
            }

            host.Scripts.Add(reference);
            workspace.AddPart(host);
            if (!store.TryCreateProject(ProjectName, WorldTemplate.Classic, workspace, out var project, out var error))
            {
                throw new InvalidOperationException($"could not create the chat test project: {error}");
            }

            var scripts = Path.Combine(project.FolderPath, "Assets", "Scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "Chat.lua"), Script, new System.Text.UTF8Encoding(false));
        }

        private PlayModeController PlayMode => (PlayModeController)typeof(Renderer).GetField("_playMode", Hidden)!.GetValue(Renderer)!;

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private (float X, float Y) PlayButton => Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight);

        private Engine.Gui.GuiTextBox? Box => PlayMode.Gui?.Document.Elements.OfType<Engine.Gui.GuiTextBox>().FirstOrDefault();

        private bool Typing => PlayMode.Gui?.FocusedTextBoxId is not null;

        private List<string> ScriptLines(string prefix)
        {
            var lines = new List<string>();
            if (PlayMode.Output is { } log)
            {
                for (var i = 0; i < log.Count; i++)
                {
                    if (log[i].Message.Contains(prefix, StringComparison.Ordinal))
                    {
                        lines.Add(log[i].Message);
                    }
                }
            }

            return lines;
        }

        private IReadOnlyList<Engine.Chat.ChatMessage> History => PlayMode.Gameplay?.Chat.FindChannel("General")?.Recent(100) ?? [];

        private (float X, float Y) BoxCenter()
        {
            foreach (var entry in PlayMode.Gui!.Layout.Entries)
            {
                if (entry.Element is Engine.Gui.GuiTextBox)
                {
                    return (entry.Rect.X + entry.Rect.Width * 0.5f, entry.Rect.Y + entry.Rect.Height * 0.5f);
                }
            }

            throw new InvalidOperationException("the chat box is not laid out");
        }

        private IEnumerable<int> TypeText(string text)
        {
            Step = $"type \"{text}\" (OS text input)";
            foreach (var ch in text)
            {
                Input.OnTextInput(ch);
                yield return 1;
            }
        }

        private IEnumerable<int> PressEnter()
        {
            Step = "Enter";
            Input.OnKeyDown(Keys.Enter, 0, default, false);
            PressKey(Keys.Enter);
            yield return 6;
        }

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            foreach (var wait in OpenCard(ProjectName, "open the chat test world"))
            {
                yield return wait;
            }

            yield return 20;
            Step = "Play";
            Click(PlayButton);
            yield return 40;
            Check("Chat: Play shows the default chat window with the game's welcome and the NPC line",
                PlayMode.IsPlaying && Box is not null && History.Count == 2 && History[1].SenderName == "Guard",
                $"playing {PlayMode.IsPlaying}, box {Box is not null}, history {History.Count}");
            foreach (var wait in Shot("rel-e-01-chat-window"))
            {
                yield return wait;
            }

            // "/" starts typing.
            Step = "press /";
            PressKey(Keys.Slash);
            yield return 6;
            Check("Chat: / gives the chat box the keyboard", Typing && Box!.Text.Length == 0, $"typing {Typing}, text '{Box?.Text}'");

            // Typing: the text goes into the box; W (a game key) reaches neither the script nor the camera.
            var camera = ViewCamera.Position;
            foreach (var wait in TypeText("Hello Wisko "))
            {
                yield return wait;
            }

            Step = "W while typing";
            SetKey(Keys.W, true);
            Input.OnTextInput('w');
            yield return 12;
            SetKey(Keys.W, false);
            foreach (var wait in TypeText("ow"))
            {
                yield return wait;
            }

            Check("Chat: OS text input goes into the box (W types a w)", Box!.Text == "Hello Wisko wow", $"'{Box.Text}'");
            Check("Chat: while typing, game keys reach neither the scripts nor the camera",
                ScriptLines("[ChatTest] W pressed").Count == 0 && camera.Equals(ViewCamera.Position), string.Join(" | ", ScriptLines("[ChatTest] W pressed")));
            foreach (var wait in Shot("rel-e-02-typing"))
            {
                yield return wait;
            }

            foreach (var wait in PressEnter())
            {
                yield return wait;
            }

            yield return 6;
            var sent = History.LastOrDefault();
            Check("Chat: Enter sends it as the local player (next frame), clears the box and lets go of the keyboard",
                sent is { Text: "Hello Wisko wow", Kind: Engine.Chat.ChatMessageKind.Player, SenderId: "local" } && Box!.Text.Length == 0 && !Typing
                && ScriptLines("[ChatTest] Player Player: Hello Wisko wow").Count == 1,
                $"{sent?.Text} / box '{Box?.Text}' / typing {Typing}");
            foreach (var wait in Shot("rel-e-03-sent"))
            {
                yield return wait;
            }

            // Escape while typing: the box lets go, Play goes on, nothing is sent.
            Step = "press / again";
            PressKey(Keys.Slash);
            yield return 6;
            foreach (var wait in TypeText("never sent"))
            {
                yield return wait;
            }

            var before = History.Count;
            Step = "Escape while typing";
            PressKey(Keys.Escape);
            yield return 8;
            Check("Chat: Escape while typing lets go of the box and Play goes on (nothing sent)", PlayMode.IsPlaying && !Typing && History.Count == before,
                $"playing {PlayMode.IsPlaying}, typing {Typing}");

            // W reaches the script again.
            Step = "W while not typing";
            PressKey(Keys.W);
            yield return 6;
            Check("Chat: not typing, W reaches the game again", ScriptLines("[ChatTest] W pressed").Count >= 1);

            // A click on the box starts typing; a click in the world lets go.
            Step = "click the chat box";
            Click(BoxCenter());
            yield return 6;
            var clickedIn = Typing;
            Step = "click the world";
            Click((420f, 300f));
            yield return 6;
            Check("Chat: a click on the box starts typing, a click elsewhere stops", clickedIn && !Typing, $"in {clickedIn}, after {Typing}");

            // The box keeps what was not sent: typing it again continues it; Escape twice: release, then stop Play.
            Step = "Escape: stop Play";
            PressKey(Keys.Escape);
            yield return 20;
            Check("Chat: Escape without a focused box stops Play, and the chat is gone with the session", !PlayMode.IsPlaying && PlayMode.Gameplay is null);
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }

    private sealed class DragToolsFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string SavedName = "Drag Test";

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private EditorToolMode Tool => ((EditorToolState)typeof(Renderer).GetField("_toolState", Hidden)!.GetValue(Renderer)!).ActiveTool;

        private int UndoCount => ((EditorHistory)typeof(Renderer).GetField("_editorHistory", Hidden)!.GetValue(Renderer)!).UndoCount;

        private RotateGizmoController Rotator => (RotateGizmoController)typeof(Renderer).GetField("_rotateGizmoController", Hidden)!.GetValue(Renderer)!;

        private ResizeGizmoController Resizer => (ResizeGizmoController)typeof(Renderer).GetField("_resizeGizmoController", Hidden)!.GetValue(Renderer)!;

        private MoveGizmoController Mover => (MoveGizmoController)typeof(Renderer).GetField("_moveGizmoController", Hidden)!.GetValue(Renderer)!;

        private Engine.World.Part PartNamed(string name) => Workspace.Parts.First(p => p.Name == name);

        private Engine.Math.Ray RayAt(float x, float y) => MouseRay.FromScreen(x, y, Width, Height, ViewCamera);

        private bool OverHandle(Engine.World.Part part, float x, float y)
        {
            var ray = RayAt(x, y);
            switch (Tool)
            {
                case EditorToolMode.Rotate:
                    return Rotator.PickHover(ray, part, ViewCamera, x, y, Width, Height) != RotateHandle.None;
                case EditorToolMode.Resize:
                    Resizer.PickHover(ray, part, ViewCamera, x, y, Width, Height, true, out var handle, out var corner);
                    return handle != ResizeHandle.None || corner != ResizeCornerHandle.None;
                case EditorToolMode.Move:
                    return Mover.PickHover(ray, part, ViewCamera, x, y, Width, Height) != GizmoAxis.None;
                default:
                    return false;
            }
        }

        // A point on the Part's body, near its middle on screen, where no gizmo handle is (the press drags the body).
        private (float X, float Y) BodyPoint(Engine.World.Part part)
        {
            var view = ViewCamera.ViewMatrix;
            var projection = ViewCamera.GetProjectionMatrix(Width / (float)Height);
            if (!GizmoMath.TryWorldToScreen(part.Position, view, projection, Width, Height, out var cx, out var cy))
            {
                throw new InvalidOperationException($"{part.Name} is not on screen");
            }

            for (var radius = 0f; radius < 140f; radius += 4f)
            {
                for (var angle = 0f; angle < 360f; angle += 15f)
                {
                    var x = cx + radius * MathF.Cos(angle * MathF.PI / 180f);
                    var y = cy + radius * MathF.Sin(angle * MathF.PI / 180f);
                    if (Engine.World.PartPicker.Pick(RayAt(x, y), Workspace, out _, skipLocked: true) == part && !OverHandle(part, x, y))
                    {
                        return (x, y);
                    }
                }
            }

            throw new InvalidOperationException($"no body point of {part.Name} free of handles");
        }

        // A point on one of the tool's handles for the Part (scanned through the gizmo's own picking).
        private (float X, float Y) HandlePoint(Engine.World.Part part)
        {
            for (var y = 80f; y < Height - 40f; y += 3f)
            {
                for (var x = 10f; x < Width - WorldExplorerRenderer.SidebarWidth - 20f; x += 3f)
                {
                    if (OverHandle(part, x, y))
                    {
                        return (x, y);
                    }
                }
            }

            throw new InvalidOperationException($"no {Tool} handle of {part.Name} on screen");
        }

        private IEnumerable<int> ChooseTool(ToolbarControl control)
        {
            Step = $"toolbar {control}";
            Click(Find((x, y) => Toolbar.TryHitControl(x, y, Width, out var hit) && hit == control, 0f, ToolbarRenderer.ToolbarHeight));
            yield return 6;
        }

        private IEnumerable<int> SelectRow(string name)
        {
            Step = $"World: select {name}";
            Click(PartRow(name));
            yield return 6;
        }

        private static float Distance(Engine.Math.Vector3 a, Engine.Math.Vector3 b) => (a - b).Length;

        private void Aim(Engine.Math.Vector3 position, Engine.Math.Vector3 target)
        {
            ViewCamera.Position = position;
            var (yaw, pitch) = LookAt(position, target);
            ViewCamera.SetOrientation(yaw, pitch);
        }

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            Step = "Hub New World";
            Click(Center(Hub.NewWorldButton()));
            yield return 6;
            Step = "Testing World";
            Click(Center((UiBox)typeof(ProjectHubRenderer).GetMethod("GetTemplateCard", Hidden)!.Invoke(Hub, [1])!));
            yield return 30;
            var red = PartNamed("BaseCube");
            var green = PartNamed("GreenCube");
            var blue = PartNamed("BlueCube");

            // Close enough that each cube is larger on screen than its handles.
            var middle = (red.Position + green.Position + blue.Position) * (1f / 3f);
            Aim(middle + new Engine.Math.Vector3(0f, 7f, 16f), middle);
            yield return 6;

            // Rotate: the body drags (the tool stays Rotate, nothing turns); one undo step.
            foreach (var wait in ChooseTool(ToolbarControl.Rotate))
            {
                yield return wait;
            }

            foreach (var wait in SelectRow("BaseCube"))
            {
                yield return wait;
            }

            var redStart = red.Position;
            var undoBefore = UndoCount;
            var body = BodyPoint(red);
            foreach (var wait in DragRow(body, (body.X + 90f, body.Y + 10f), "Rotate tool: drag the red cube's body"))
            {
                yield return wait;
            }

            var movedInRotate = Distance(red.Position, redStart) > 1f && red.Rotation.Equals(Engine.Math.Vector3.Zero) && Tool == EditorToolMode.Rotate;
            var oneStep = UndoCount == undoBefore + 1;
            foreach (var wait in Shot("rel-d-01-rotate-body-drag"))
            {
                yield return wait;
            }

            Check("Rotate tool: dragging the body moves the cube (not turned), the tool stays Rotate, one undo step",
                movedInRotate && oneStep, $"moved {Distance(red.Position, redStart):F2}, rotation {red.Rotation.X} {red.Rotation.Y} {red.Rotation.Z}, tool {Tool}, undo +{UndoCount - undoBefore}");
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Rotate tool: undo puts the dragged cube back", Distance(red.Position, redStart) < 1e-3f, $"{Distance(red.Position, redStart):F4}");

            // A ring still turns it and does not move it.
            var ring = HandlePoint(red);
            foreach (var wait in DragRow(ring, (ring.X + 50f, ring.Y + 30f), "Rotate tool: drag a ring"))
            {
                yield return wait;
            }

            Check("Rotate tool: a handle still rotates (the cube turned, not moved)",
                !red.Rotation.Equals(Engine.Math.Vector3.Zero) && Distance(red.Position, redStart) < 1e-3f, $"{red.Rotation.X} {red.Rotation.Y} {red.Rotation.Z}");

            // Resize: the body drags (size kept); a handle still resizes.
            foreach (var wait in ChooseTool(ToolbarControl.Resize))
            {
                yield return wait;
            }

            foreach (var wait in SelectRow("GreenCube"))
            {
                yield return wait;
            }

            var greenStart = green.Position;
            var greenSize = green.Size;
            body = BodyPoint(green);
            foreach (var wait in DragRow(body, (body.X + 70f, body.Y - 20f), "Resize tool: drag the green cube's body"))
            {
                yield return wait;
            }

            Check("Resize tool: dragging the body moves the cube (same size), the tool stays Resize",
                Distance(green.Position, greenStart) > 1f && green.Size.Equals(greenSize) && Tool == EditorToolMode.Resize,
                $"moved {Distance(green.Position, greenStart):F2}, size {green.Size.X} {green.Size.Y} {green.Size.Z}, tool {Tool}");
            var handle = HandlePoint(green);
            foreach (var wait in DragRow(handle, (handle.X + 40f, handle.Y - 40f), "Resize tool: drag a handle"))
            {
                yield return wait;
            }

            Check("Resize tool: a handle still resizes", !green.Size.Equals(greenSize), $"{green.Size.X} {green.Size.Y} {green.Size.Z}");

            // Escape during a body drag: back where it began, nothing recorded.
            foreach (var wait in SelectRow("BlueCube"))
            {
                yield return wait;
            }

            var blueStart = blue.Position;
            undoBefore = UndoCount;
            body = BodyPoint(blue);
            Step = "Resize tool: drag the blue cube, Escape while holding";
            Input.OnMouseMove(0d, 0d, body.X, body.Y);
            yield return 2;
            Input.OnMouseButton(MouseButton.Left, true, body.X, body.Y);
            yield return 2;
            for (var i = 1; i <= 6; i++)
            {
                Input.OnMouseMove(0d, 0d, body.X, body.Y + i * 12f);
                yield return 1;
            }

            var midDrag = Distance(blue.Position, blueStart);
            PressKey(Keys.Escape);
            yield return 3;
            Input.OnMouseButton(MouseButton.Left, false, body.X, body.Y + 72f);
            yield return 6;
            Check("Escape during a body drag: the cube goes back where it began, nothing to undo",
                midDrag > 0.5f && Distance(blue.Position, blueStart) < 1e-4f && UndoCount == undoBefore && Screen == "Editor",
                $"mid {midDrag:F2}, after {Distance(blue.Position, blueStart):F4}, undo +{UndoCount - undoBefore}");

            // Two selected cubes drag together (Resize tool).
            foreach (var wait in SelectRow("BaseCube"))
            {
                yield return wait;
            }

            SetKey(Keys.LeftControl, true);
            yield return 1;
            Click(PartRow("BlueCube"));
            yield return 4;
            SetKey(Keys.LeftControl, false);
            yield return 2;
            var gap = blue.Position - red.Position;
            var redBefore = red.Position;
            body = BodyPoint(red);
            foreach (var wait in DragRow(body, (body.X - 60f, body.Y + 25f), "Resize tool: drag two selected cubes"))
            {
                yield return wait;
            }

            Check("Two selected cubes drag together (same offset), still Resize",
                Distance(red.Position, redBefore) > 1f && Distance(blue.Position - red.Position, gap) < 1e-3f && Tool == EditorToolMode.Resize && SelectedNames == "BaseCube|BlueCube",
                $"moved {Distance(red.Position, redBefore):F2}, {SelectedNames}");

            // Another camera angle: high above, looking down.
            middle = (red.Position + green.Position + blue.Position) * (1f / 3f);
            Aim(middle + new Engine.Math.Vector3(0f, 20f, 6f), middle);
            yield return 6;
            foreach (var wait in ChooseTool(ToolbarControl.Rotate))
            {
                yield return wait;
            }

            foreach (var wait in SelectRow("GreenCube"))
            {
                yield return wait;
            }

            greenStart = green.Position;
            body = BodyPoint(green);
            foreach (var wait in DragRow(body, (body.X + 60f, body.Y + 60f), "top view: drag the green cube"))
            {
                yield return wait;
            }

            Check("From above: dragging the body moves the cube across the ground (no jump up or down)",
                Distance(green.Position, greenStart) > 1f && MathF.Abs(green.Position.Y - greenStart.Y) < 1e-3f,
                $"{green.Position.X:F2} {green.Position.Y:F2} {green.Position.Z:F2}");

            // Without the baseplate: the cube moves on its own level.
            Step = "World: select the Baseplate row";
            Click(Row(h => h.Kind == ExplorerRowKind.Environment));
            yield return 4;
            PressKey(Keys.Delete);
            yield return 6;
            foreach (var wait in SelectRow("GreenCube"))
            {
                yield return wait;
            }

            greenStart = green.Position;
            body = BodyPoint(green);
            foreach (var wait in DragRow(body, (body.X - 80f, body.Y + 10f), "no baseplate: drag the green cube"))
            {
                yield return wait;
            }

            Check("No baseplate: dragging the body moves the cube on its own level (the level plane)",
                !Workspace.HasBaseplate && Distance(green.Position, greenStart) > 1f && MathF.Abs(green.Position.Y - greenStart.Y) < 1e-3f,
                $"{green.Position.X:F2} {green.Position.Y:F2} {green.Position.Z:F2}");
            foreach (var wait in Shot("rel-d-02-no-baseplate-drag"))
            {
                yield return wait;
            }

            // The Move tool keeps its own behaviour: a body press selects, it does not drag.
            foreach (var wait in ChooseTool(ToolbarControl.Move))
            {
                yield return wait;
            }

            greenStart = green.Position;
            var press = BodyPoint(green);
            foreach (var wait in DragRow(press, (press.X + 80f, press.Y), "Move tool: press the body and drag"))
            {
                yield return wait;
            }

            Check("Move tool: unchanged (a body press does not drag the cube)", Distance(green.Position, greenStart) < 1e-4f, $"{Distance(green.Position, greenStart):F4}");

            // Save, reopen: where the drags left them.
            var expected = Workspace.Parts.ToDictionary(p => p.Name, p => p.Position);
            foreach (var wait in LeaveToHub("leave to save"))
            {
                yield return wait;
            }

            foreach (var wait in Answer("Save"))
            {
                yield return wait;
            }

            foreach (var wait in TypeName(SavedName))
            {
                yield return wait;
            }

            foreach (var wait in OpenCard(SavedName, "reopen"))
            {
                yield return wait;
            }

            yield return 20;
            Check("Reopened: every cube where the drags left it (and still no baseplate)",
                !Workspace.HasBaseplate && Workspace.Parts.All(p => expected.TryGetValue(p.Name, out var at) && Distance(p.Position, at) < 1e-4f),
                string.Join("; ", Workspace.Parts.Select(p => $"{p.Name} {Distance(p.Position, expected.GetValueOrDefault(p.Name)):F4}")));
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }

        private static (float X, float Y) Center(UiBox box) => (box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f);
    }

    private sealed class RotationFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string SavedName = "Rotation Test";

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var project = new ProjectStore(Path.Combine(root, "Projects")).ListProjects().FirstOrDefault(p => p.Name == SavedName);
            var parts = project is not null && Engine.World.SceneFile.TryLoad(project.ScenePath, out var loaded, out _) ? loaded : [];
            var part = parts.FirstOrDefault(p => p.Name == "Part1");
            report.Check("Rotation: the saved world's Scene.json has the typed rotation", part is not null && part.Rotation.Y == 45f && part.Rotation.Z == 45f,
                part is null ? "no Part1" : $"{part.Rotation.X} {part.Rotation.Y} {part.Rotation.Z}");
        }

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;

        private ScriptsPanelView Scripts => (ScriptsPanelView)typeof(Renderer).GetMethod("BuildScriptsPanelView", Hidden)!.Invoke(Renderer, null)!;

        private RotationFieldsView? Fields => Scripts.Rotation;

        private Engine.World.Part PartNamed(string name) => Workspace.Parts.First(p => p.Name == name);

        private (float X, float Y) Field(int axis) =>
            Find((x, y) => Explorer.TryHitScriptsPanel(x, y, Width, Height, Scripts, out var hit) && hit.Action == ScriptsPanelAction.RotationField && hit.ScriptIndex == axis,
                ToolbarRenderer.ToolbarHeight, Height, Width - WorldExplorerRenderer.SidebarWidth - 8f);

        private IEnumerable<int> TypeText(string text, string label)
        {
            Step = label;
            foreach (var ch in text)
            {
                Input.OnTextInput(ch);
                yield return 2;
            }
        }

        private IEnumerable<int> Enter(int axis, string text, Keys finish = Keys.Enter)
        {
            Step = $"Rotation {"XYZ"[axis]}: click";
            Click(Field(axis));
            yield return 4;
            foreach (var wait in TypeText(text, $"Rotation {"XYZ"[axis]}: type {text}"))
            {
                yield return wait;
            }

            Step = $"Rotation {"XYZ"[axis]}: {finish}";
            PressKey(finish);
            yield return 6;
        }

        // A point on the Rotate gizmo's ring for this axis (scanned through the gizmo's own picking).
        private (float X, float Y)? RingPoint(Engine.World.Part part, RotateHandle handle)
        {
            var controller = (RotateGizmoController)typeof(Renderer).GetField("_rotateGizmoController", Hidden)!.GetValue(Renderer)!;
            for (var y = 80f; y < Height - 40f; y += 3f)
            {
                for (var x = 10f; x < Width - WorldExplorerRenderer.SidebarWidth - 20f; x += 3f)
                {
                    var ray = MouseRay.FromScreen(x, y, Width, Height, ViewCamera);
                    if (controller.PickHover(ray, part, ViewCamera, x, y, Width, Height) == handle)
                    {
                        return (x, y);
                    }
                }
            }

            return null;
        }

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            foreach (var wait in NewClassicWorld("a new world"))
            {
                yield return wait;
            }

            foreach (var wait in AddCube("a cube"))
            {
                yield return wait;
            }

            var part = PartNamed("Part1");
            Check("Rotation fields: shown for the selected Part (0, 0, 0)", Fields is { Values: ["0", "0", "0"], ReadOnly: false }, string.Join(",", Fields?.Values ?? []));

            // X: 22.5, kept exactly as typed until Enter; one undo step.
            Step = "Rotation X: click";
            Click(Field(0));
            yield return 4;
            foreach (var wait in TypeText("22.5", "Rotation X: type 22.5"))
            {
                yield return wait;
            }

            var whileTyping = (Fields?.EditingText, part.Rotation.X);
            foreach (var wait in Shot("rel-c-01-typing"))
            {
                yield return wait;
            }

            PressKey(Keys.Enter);
            yield return 6;
            Check("Rotation X: the typed 22.5 stays as typed (the Part unchanged) until Enter, then X is 22.5",
                whileTyping is ("22.5", 0f) && part.Rotation.X == 22.5f && Fields?.Values[0] == "22.5", $"{whileTyping} -> {part.Rotation.X}");
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var undone = part.Rotation.X;
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            Check("Rotation X: undo back to 0, redo 22.5", undone == 0f && part.Rotation.X == 22.5f, $"undo {undone}, redo {part.Rotation.X}");

            // Y: a negative value is stored as the gizmo stores it (270 is -90).
            foreach (var wait in Enter(1, "-90"))
            {
                yield return wait;
            }

            Check("Rotation Y: -90 is stored as 270 (the same orientation) and shown so", part.Rotation.Y == 270f && Fields?.Values[1] == "270", $"{part.Rotation.Y}");

            // Z: Tab commits and moves to the next field; Escape drops the typing there.
            foreach (var wait in Enter(2, "45", Keys.Tab))
            {
                yield return wait;
            }

            var tabbed = (part.Rotation.Z, Fields?.EditingAxis);
            foreach (var wait in TypeText("7", "Rotation X: type 7 (then Escape)"))
            {
                yield return wait;
            }

            PressKey(Keys.Escape);
            yield return 6;
            Check("Rotation Z: Tab commits 45 and moves to X; Escape there drops the typing (X still 22.5)",
                tabbed is (45f, 0) && Fields?.EditingAxis == -1 && part.Rotation.X == 22.5f, $"{tabbed}, X {part.Rotation.X}");

            // Refused text keeps the field open; typing never moves the camera.
            Step = "Rotation X: refused text";
            Click(Field(0));
            yield return 4;
            foreach (var wait in TypeText("--", "Rotation X: type --"))
            {
                yield return wait;
            }

            PressKey(Keys.Enter);
            yield return 6;
            var refused = Fields?.EditingAxis == 0 && part.Rotation.X == 22.5f;
            var cameraBefore = ViewCamera.Position;
            Step = "hold W while typing";
            SetKey(Keys.W, true);
            yield return 12;
            SetKey(Keys.W, false);
            yield return 2;
            var cameraStill = ViewCamera.Position.Equals(cameraBefore);
            PressKey(Keys.Escape);
            yield return 6;
            Check("Rotation: \"--\" is refused (the field stays open, nothing changes); keys while typing do not fly the camera",
                refused && cameraStill && part.Rotation.X == 22.5f, $"refused {refused}, camera still {cameraStill}");

            // The Rotate gizmo: the fields follow a ring drag live, and the readout shows the angle.
            Step = "Rotate tool";
            Click(Find((x, y) => Toolbar.TryHitControl(x, y, Width, out var control) && control == ToolbarControl.Rotate, 0f, ToolbarRenderer.ToolbarHeight));
            yield return 6;
            var ring = RingPoint(part, RotateHandle.Y);
            if (ring is { } start)
            {
                Step = "drag the Y ring";
                Input.OnMouseMove(0d, 0d, start.X, start.Y);
                yield return 2;
                Input.OnMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left, true, start.X, start.Y);
                yield return 2;
                for (var i = 1; i <= 6; i++)
                {
                    Input.OnMouseMove(0d, 0d, start.X + i * 10f, start.Y + i * 6f);
                    yield return 1;
                }

                var midValue = part.Rotation.Y;
                var midField = Fields?.Values[1];
                foreach (var wait in Shot("rel-c-02-gizmo-readout"))
                {
                    yield return wait;
                }

                Input.OnMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left, false, start.X + 60f, start.Y + 36f);
                yield return 6;
                Check("Rotation: dragging the Y ring changes Y, and the field shows the same value live during the drag",
                    midValue != 270f && midField == RotationInput.Format(midValue), $"Y {midValue}, field {midField}");
                foreach (var wait in CtrlKey(Keys.Z))
                {
                    yield return wait;
                }

                Check("Rotation: undoing the ring drag puts Y back to 270 (the field too)", part.Rotation.Y == 270f && Fields?.Values[1] == "270", $"{part.Rotation.Y}");
            }
            else
            {
                Check("Rotation: the Y ring of the Rotate gizmo was found on screen", false);
            }

            // Two Parts: — where they differ, one typed value sets both (one undo step).
            foreach (var wait in AddCube("a second cube"))
            {
                yield return wait;
            }

            Step = "select both";
            Click(PartRow("Part1"));
            yield return 4;
            SetKey(Keys.LeftControl, true);
            yield return 1;
            Click(PartRow("Part2"));
            yield return 4;
            SetKey(Keys.LeftControl, false);
            yield return 2;
            var mixed = Fields?.Values.ToArray() ?? [];
            foreach (var wait in Enter(1, "45"))
            {
                yield return wait;
            }

            var both = (part.Rotation.Y, PartNamed("Part2").Rotation.Y);
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var bothUndone = (part.Rotation.Y, PartNamed("Part2").Rotation.Y);
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            Check("Rotation, two Parts: — where they differ; Y 45 sets both, one undo puts both back",
                mixed.SequenceEqual(["—", "—", "—"]) && both is (45f, 45f) && bothUndone is (270f, 0f) && part.Rotation.Y == 45f,
                $"{string.Join(",", mixed)}; {both}; undo {bothUndone}");

            // Save, leave, reopen.
            foreach (var wait in LeaveToHub("leave to save"))
            {
                yield return wait;
            }

            foreach (var wait in Answer("Save"))
            {
                yield return wait;
            }

            foreach (var wait in TypeName(SavedName))
            {
                yield return wait;
            }

            foreach (var wait in OpenCard(SavedName, "reopen"))
            {
                yield return wait;
            }

            yield return 20;
            var reopened = PartNamed("Part1").Rotation;
            Check("Rotation: the reopened world has the typed rotation (22.5, 45, 45)", reopened.X == 22.5f && reopened.Y == 45f && reopened.Z == 45f,
                $"{reopened.X} {reopened.Y} {reopened.Z}");
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }

    private sealed class BaseplateFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string OldProjectName = "Old Baseplate World";
        public const string SavedName = "No Baseplate";

        // A point on the ground in the 3D View (below the middle, where nothing was added).
        private static readonly (float X, float Y) Ground = (380f, 640f);

        public static void After(TestReport report, EditorFlow flow, string root)
        {
            var scene = new ProjectStore(Path.Combine(root, "Projects")).ListProjects().FirstOrDefault(p => p.Name == SavedName)?.ScenePath ?? "(no project)";
            var text = File.Exists(scene) ? File.ReadAllText(scene) : string.Empty;
            report.Check("Baseplate: the saved world's Scene.json says it has no baseplate (Environment: Baseplate false), with its cube",
                text.Contains("\"Baseplate\": false", StringComparison.Ordinal) && text.Contains("\"Part1\"", StringComparison.Ordinal), scene);
        }

        private bool Has => Workspace.HasBaseplate;

        private string? SelectedEnvironment => Explorer.SelectedEnvironment?.Name;

        private int EnvironmentPhysics()
        {
            var playMode = typeof(Renderer).GetField("_playMode", Hidden)!.GetValue(Renderer)!;
            return playMode.GetType().GetField("_physics", Hidden)!.GetValue(playMode) is Engine.Physics.PhysicsWorld physics ? physics.EnvironmentCount : -1;
        }

        private IEnumerable<int> CtrlClick((float X, float Y) point, string label)
        {
            Step = label;
            SetKey(Keys.LeftControl, true);
            yield return 2;
            Click(point);
            yield return 4;
            SetKey(Keys.LeftControl, false);
            yield return 4;
        }

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            foreach (var wait in NewClassicWorld("a new Classic world"))
            {
                yield return wait;
            }

            foreach (var wait in AddCube("a cube to keep"))
            {
                yield return wait;
            }

            Check("A new world has its baseplate (World panel row, drawn, collided with)", Has && Explorer.Environment.Count == 1);

            // The World panel row, then Delete.
            Step = "World: select the Baseplate row";
            Click(Row(h => h.Kind == ExplorerRowKind.Environment));
            yield return 6;
            var rowSelected = SelectedEnvironment == "Baseplate" && SelectedNames.Length == 0;
            foreach (var wait in Shot("rel-b-01-selected"))
            {
                yield return wait;
            }

            Step = "Delete";
            PressKey(Keys.Delete);
            yield return 6;
            var gone = !Has && Explorer.Environment.Count == 0 && PartCount == 1;
            foreach (var wait in Shot("rel-b-02-deleted"))
            {
                yield return wait;
            }

            Check("Baseplate: its World panel row selects it; Delete deletes it (row gone, the cube kept)", rowSelected && gone,
                $"selected {rowSelected}, present {Has}, rows {Explorer.Environment.Count}, parts {PartCount}");

            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            var undone = Has && Explorer.Environment.Count == 1;
            foreach (var wait in CtrlKey(Keys.Y))
            {
                yield return wait;
            }

            var redone = !Has && Explorer.Environment.Count == 0;
            foreach (var wait in CtrlKey(Keys.Z))
            {
                yield return wait;
            }

            Check("Baseplate: undo brings it back, redo deletes it again, undo again", undone && redone && Has, $"undo {undone}, redo {redone}, now {Has}");

            // In the 3D View: Ctrl+click selects it (outlined); a plain click on the ground clears the selection.
            foreach (var wait in CtrlClick(Ground, "Ctrl+click the ground"))
            {
                yield return wait;
            }

            var ctrlSelected = SelectedEnvironment == "Baseplate" && SelectedNames.Length == 0;
            foreach (var wait in Shot("rel-b-03-ctrl-click"))
            {
                yield return wait;
            }

            Step = "plain click on the ground";
            Click(Ground);
            yield return 6;
            Check("Baseplate: Ctrl+click on the ground selects it; a plain click there clears the selection",
                ctrlSelected && SelectedEnvironment is null && SelectedNames.Length == 0, $"ctrl {ctrlSelected}, now {SelectedEnvironment}");

            // Ctrl+drag is still a box selection (it takes the cube, not the baseplate).
            Step = "Ctrl+drag a box over the middle";
            SetKey(Keys.LeftControl, true);
            yield return 2;
            foreach (var wait in DragRow((120f, 110f), (960f, 640f), "Ctrl+drag box"))
            {
                yield return wait;
            }

            SetKey(Keys.LeftControl, false);
            yield return 4;
            Check("Baseplate: Ctrl+drag still box-selects (the cube, not the baseplate)", SelectedNames == "Part1" && SelectedEnvironment is null,
                $"{SelectedNames} / {SelectedEnvironment}");

            // The context menu.
            Step = "World: right-click the Baseplate row";
            RightClick(Row(h => h.Kind == ExplorerRowKind.Environment));
            yield return 6;
            var items = ContextItems;
            Step = "context menu: Delete";
            Click(ContextItem("Delete"));
            yield return 8;
            Check("Baseplate: its row's context menu offers Delete, which deletes it", items == "Delete" && !Has, $"{items}, present {Has}");

            // Play without it: no environment collider; Stop keeps it deleted.
            Step = "Play";
            var play = Find((x, y) => Toolbar.TryHitPlay(x, y, Width), 0f, ToolbarRenderer.ToolbarHeight);
            Click(play);
            yield return 40;
            var inPlay = EnvironmentPhysics();
            Step = "Stop";
            Click(play);
            yield return 20;
            Check("Baseplate deleted, Play: no environment collider; Stop does not bring it back", inPlay == 0 && !Has, $"environment colliders {inPlay}, present {Has}");
            foreach (var wait in Shot("rel-b-04-after-play"))
            {
                yield return wait;
            }

            // Save under a name, leave, reopen: still without it.
            foreach (var wait in LeaveToHub("leave to save"))
            {
                yield return wait;
            }

            foreach (var wait in Answer("Save"))
            {
                yield return wait;
            }

            foreach (var wait in TypeName(SavedName))
            {
                yield return wait;
            }

            foreach (var wait in OpenCard(SavedName, "reopen"))
            {
                yield return wait;
            }

            yield return 20;
            Check("Baseplate: the saved world reopens without it (no row, not collided with), its cube kept", Screen == "Editor" && !Has && Explorer.Environment.Count == 0 && PartCount == 1,
                $"{Screen}, present {Has}, rows {Explorer.Environment.Count}, parts {PartCount}");
            foreach (var wait in Shot("rel-b-05-reopened"))
            {
                yield return wait;
            }

            foreach (var wait in LeaveToHub("leave the saved world"))
            {
                yield return wait;
            }

            // A project from before: its baseplate is there.
            foreach (var wait in OpenCard(OldProjectName, "open the old project"))
            {
                yield return wait;
            }

            yield return 20;
            Check("Baseplate: a project from before this release opens with its baseplate", Screen == "Editor" && Has && Explorer.Environment.Count == 1 && PartCount == 1,
                $"{Screen}, present {Has}, parts {PartCount}");
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }

    private sealed class TemplateScreenFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir, string projectsRoot)
        : EditorFlow(report, app, window, screenshotDir)
    {
        private Dictionary<(WorldTemplate Template, int Density), UiTexture?> Pictures =>
            (Dictionary<(WorldTemplate, int), UiTexture?>)typeof(ProjectHubRenderer).GetField("_templatePreviews", Hidden)!.GetValue(Hub)!;

        private ProjectHubView View => Hub.View;

        private UiBox Card(int index) => (UiBox)typeof(ProjectHubRenderer).GetMethod("GetTemplateCard", Hidden)!.Invoke(Hub, [index])!;

        private static (float X, float Y) Middle(UiBox box) => (box.X + box.Width * 0.5f, box.Y + box.Height * 0.5f);

        private IEnumerable<int> OpenNewWorld(string label)
        {
            Step = $"{label}: Hub New World";
            Click(Middle(Hub.NewWorldButton()));
            yield return 8;
        }

        private IEnumerable<int> Leave(string label)
        {
            foreach (var wait in LeaveToHub(label))
            {
                yield return wait;
            }

            yield return 6;
        }

        protected override IEnumerable<int> Steps()
        {
            yield return 30;
            foreach (var wait in OpenNewWorld("first look"))
            {
                yield return wait;
            }

            yield return 4;
            var loaded = Pictures.Where(p => p.Value is not null).Select(p => $"{p.Key.Template}@{p.Key.Density}x").ToList();
            Check("New World: both template pictures are loaded and drawn (no icon fallback)",
                View == ProjectHubView.Templates && loaded.Count == 2 && Pictures.Values.All(t => t is not null), string.Join(", ", loaded));
            foreach (var wait in Shot("rel-01-new-world"))
            {
                yield return wait;
            }

            // The pictures show this host's environment (the baseplate), as recorded when they were made.
            var manifest = TemplatePreviews.LoadManifest();
            var environment = TemplatePreviews.EnvironmentFingerprint(PlayModeControllerAccess.Environment(Renderer).Boxes);
            Check("New World: the pictures were made with this editor's environment (the baseplate)",
                manifest is not null && manifest.Templates.Values.All(r => r.Environment == environment), environment);

            // Cancel, and open again several times: the pictures are loaded once, nothing is created.
            var parts = PartCount;
            for (var round = 0; round < 3; round++)
            {
                Step = $"Escape back to Home ({round + 1})";
                PressKey(Keys.Escape);
                yield return 6;
                var home = View == ProjectHubView.Home && Screen == "Hub";
                foreach (var wait in OpenNewWorld($"open again ({round + 1})"))
                {
                    yield return wait;
                }

                Check($"New World: Escape cancels to Home and the screen opens again ({round + 1}), pictures kept",
                    home && View == ProjectHubView.Templates && Pictures.Count == 2 && Screen == "Hub", $"{View}, {Pictures.Count} textures");
            }

            // The keyboard: Right moves to Testing World (its ring shows), Enter creates it.
            Step = "keyboard: Right";
            PressKey(Keys.Right);
            yield return 4;
            foreach (var wait in Shot("rel-02-keyboard-focus"))
            {
                yield return wait;
            }

            Step = "keyboard: Enter";
            PressKey(Keys.Enter);
            yield return 30;
            var names = string.Join("|", Workspace.Parts.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Check("New World by keyboard: Right then Enter creates the Testing World (its three cubes)",
                Screen == "Editor" && names == "BaseCube|BlueCube|GreenCube", $"{Screen}: {names}");
            foreach (var wait in Leave("leave the Testing World"))
            {
                yield return wait;
            }

            // The mouse: the Classic card creates an empty world.
            foreach (var wait in OpenNewWorld("Classic by mouse"))
            {
                yield return wait;
            }

            Step = "click the Classic card";
            Click(Middle(Card(0)));
            yield return 30;
            Check("New World by mouse: the Classic card creates an empty world", Screen == "Editor" && PartCount == 0, $"{Screen}, {PartCount} parts");
            foreach (var wait in Leave("leave the Classic world"))
            {
                yield return wait;
            }

            // A small window: the cards shrink to fit (never stretched, never overlapping) and still work.
            Step = "small window";
            Window.ClientSize = new Vector2i(760, 520);
            yield return 20;
            foreach (var wait in OpenNewWorld("small window"))
            {
                yield return wait;
            }

            var a = Card(0);
            var b = Card(1);
            var fits = a.X + a.Width <= Width + 0.5f && b.X + b.Width <= Width + 0.5f && a.Y + a.Height <= Height + 0.5f && b.Y + b.Height <= Height + 0.5f
                && (a.X + a.Width <= b.X || a.Y + a.Height <= b.Y);
            foreach (var wait in Shot("rel-03-small-window"))
            {
                yield return wait;
            }

            Step = "small window: click the Testing World card";
            Click(Middle(b));
            yield return 30;
            Check("New World in a 760 x 520 window: both cards fit side by side or stacked, and the Testing World card creates it",
                fits && Screen == "Editor" && PartCount == 3, $"{Width} x {Height}: {a} {b}, {Screen}, {PartCount} parts");
            foreach (var wait in Leave("leave the small-window world"))
            {
                yield return wait;
            }

            Window.ClientSize = new Vector2i(1280, 720);
            yield return 20;
            Check("No world was saved by any of this (Projects folder empty)", Directory.GetDirectories(projectsRoot).Length == 0,
                string.Join(", ", Directory.GetDirectories(projectsRoot).Select(Path.GetFileName)));
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }
}
