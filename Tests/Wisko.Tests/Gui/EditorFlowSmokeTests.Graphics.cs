using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.UI;
using Wisko.Engine.World;
using Wisko.Tests.Checks;

namespace Wisko.Tests.Gui;

// Graphics V1 in the real editor (OpenGL): a Universal test project (roughness and metal rows, a textured block with a
// normal map, point and spot lights, emission, toon with an outline, glass, shadow casters) opened from the Hub and
// rendered at each quality preset, with bloom, a selection, Play and Stop. Screenshots are taken for a visual check.
internal static partial class EditorFlowSmokeTests
{
    private static void RunGraphics(Action<string, Action> section, string work, string? screenshotDir, TestReport report)
    {
        section("GUI: Graphics V1 (Universal renderer, presets, bloom, selection, Play), scripted input", () =>
        {
            var root = Path.Combine(work, "graphics");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            GraphicsFlow.CreateProject(Path.Combine(root, "Projects"));
            RunSession(report, root, "graphics flow", (app, window) => new GraphicsFlow(report, app, window, screenshotDir), (_, _, _) => { });
        });
        section("GUI: Graphics V1 editor: Environment & Graphics panel (every tab, Atmosphere, Materials, lights, Rendering, Post-Processing, undo, save, reopen), scripted input", () =>
        {
            var root = Path.Combine(work, "graphics-editor");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            GraphicsFlow.CreateProject(Path.Combine(root, "Projects"));
            RunSession(report, root, "graphics editor flow", (app, window) => new GraphicsEditorFlow(report, app, window, screenshotDir), GraphicsEditorFlow.After);
        });
        section("GUI: Graphics V1 shader failure falls back to the Classic renderer (no crash), scripted input", () =>
        {
            var root = Path.Combine(work, "graphics-fallback");
            Directory.CreateDirectory(Path.Combine(root, "Projects"));
            GraphicsFlow.CreateProject(Path.Combine(root, "Projects"));
            Wisko.Engine.Rendering.Universal.ShaderLibrary.BreakForTests = "surface.frag";
            try
            {
                RunSession(report, root, "fallback flow", (app, window) => new FallbackFlow(report, app, window, screenshotDir), (_, _, _) => { });
            }
            finally
            {
                Wisko.Engine.Rendering.Universal.ShaderLibrary.BreakForTests = null;
            }
        });
    }

    private sealed class GraphicsFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        public const string ProjectName = "Graphics Test";

        private EditorCamera ViewCamera => (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(Renderer)!;


        public static void CreateProject(string projectsRoot)
        {
            var workspace = new Workspace();
            var white = new Vector3(0.9f, 0.9f, 0.9f);
            var gold = new Vector3(1f, 0.78f, 0.34f);
            for (var i = 0; i < 5; i++)
            {
                var x = -12f + i * 6f;
                workspace.AddPart(new Part($"Rough {i}", new Vector3(x, 3f, 0f), new Vector3(4f, 4f, 4f), white, shapeType: ShapeType.Sphere)
                {
                    Material = SurfaceMaterial.Default with { Roughness = 0.05f + i * 0.225f }
                });
                workspace.AddPart(new Part($"Metal {i}", new Vector3(x, 3f, -8f), new Vector3(4f, 4f, 4f), gold, shapeType: ShapeType.Sphere)
                {
                    Material = SurfaceMaterial.Default with { Metallic = 1f, Roughness = 0.05f + i * 0.225f }
                });
            }

            workspace.AddPart(new Part("Textured", new Vector3(-16f, 3f, 9f), new Vector3(5f, 5f, 5f), new Vector3(1f, 1f, 1f))
            {
                Material = SurfaceMaterial.Default with { BaseColorTexture = "Assets/Textures/checker.png", NormalTexture = "Assets/Textures/bumps.png", Roughness = 0.4f }
            });
            workspace.AddPart(new Part("Neon", new Vector3(16f, 2f, 9f), new Vector3(6f, 1f, 1f), new Vector3(1f, 0.2f, 0.8f))
            {
                Material = MaterialPresets.Find("Neon")!.Apply(SurfaceMaterial.Default, new Vector3(1f, 0.2f, 0.8f))
            });
            workspace.AddPart(new Part("Toon", new Vector3(-6f, 3f, 9f), new Vector3(4f, 4f, 4f), new Vector3(0.3f, 0.8f, 0.4f), shapeType: ShapeType.Sphere)
            {
                Material = MaterialPresets.Find("Toon")!.Apply(SurfaceMaterial.Default, new Vector3(0.3f, 0.8f, 0.4f)) with { Outline = true }
            });
            workspace.AddPart(new Part("Glass", new Vector3(4f, 3f, 9f), new Vector3(4f, 4f, 4f), new Vector3(0.7f, 0.9f, 1f))
            {
                Material = MaterialPresets.Find("Glass")!.Apply(SurfaceMaterial.Default, new Vector3(0.7f, 0.9f, 1f))
            });
            workspace.AddPart(new Part("Pillar", new Vector3(10f, 6f, 4f), new Vector3(1.5f, 12f, 1.5f), new Vector3(0.8f, 0.3f, 0.25f)));
            workspace.AddPart(new Part("Point Light", new Vector3(-10f, 4f, 5f), new Vector3(0.6f, 0.6f, 0.6f), new Vector3(1f, 0.6f, 0.3f), shapeType: ShapeType.Sphere)
            {
                CanCollide = false,
                Material = SurfaceMaterial.Default with { Mode = ShadingMode.Unlit, CastShadows = false },
                Light = PartLight.Create(LightType.Point) with { Color = new Vector3(1f, 0.6f, 0.3f), Intensity = 120f, Range = 18f }
            });
            workspace.AddPart(new Part("Spot Light", new Vector3(10f, 14f, 9f), new Vector3(0.6f, 0.6f, 0.6f), new Vector3(0.4f, 0.7f, 1f), new Vector3(90f, 0f, 0f), shapeType: ShapeType.Sphere)
            {
                CanCollide = false,
                Material = SurfaceMaterial.Default with { Mode = ShadingMode.Unlit, CastShadows = false },
                Light = PartLight.Create(LightType.Spot) with { Color = new Vector3(0.4f, 0.7f, 1f), Intensity = 600f, Range = 30f }
            });

            var store = new ProjectStore(projectsRoot);
            if (!store.TryCreateProject(ProjectName, WorldTemplate.Classic, workspace, out var project, out var error))
            {
                throw new InvalidOperationException($"could not create the graphics test project: {error}");
            }

            var textures = Path.Combine(project.FolderPath, "Assets", "Textures");
            Directory.CreateDirectory(textures);
            File.WriteAllBytes(Path.Combine(textures, "checker.png"), PngEncoder.EncodeRgba(Checker(256), 256, 256));
            File.WriteAllBytes(Path.Combine(textures, "bumps.png"), PngEncoder.EncodeRgba(Bumps(256), 256, 256));

            // The sample models (Khronos glTF Sample Assets) through the editor's import, in a row behind the spheres.
            var models = GraphicsDemo.ImportAll(project.FolderPath);
            workspace.AddPart(GraphicsDemo.ModelPart("Mirror Test", models["NormalTangentMirrorTest"], new Vector3(-16f, 1f, -22f), 3f));
            workspace.AddPart(GraphicsDemo.ModelPart("Water Bottle", models["WaterBottle"], new Vector3(-6f, 3.5f, -22f), 20f));
            workspace.AddPart(GraphicsDemo.ModelPart("Alpha Test", models["AlphaBlendModeTest"], new Vector3(4f, 1f, -22f), 1.5f));
            workspace.AddPart(GraphicsDemo.ModelPart("Emissive Test", models["EmissiveStrengthTest"], new Vector3(16f, 3f, -22f), 1.2f));
            workspace.AddPart(GraphicsDemo.ModelPart("Texture Test", models["TextureSettingsTest"], new Vector3(0f, 6f, -32f), 1f));
            if (!SceneFile.TrySave(workspace, project.ScenePath, out error))
            {
                throw new InvalidOperationException($"could not save the graphics test scene: {error}");
            }
            if (!GraphicsFile.TrySave(project.GraphicsPath, GraphicsSettings.Default, out var saveError))
            {
                throw new InvalidOperationException($"could not write Graphics.json: {saveError}");
            }
        }

        // An 8 x 8 checker of warm white and slate (sRGB).
        private static byte[] Checker(int size)
        {
            var rgba = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var on = ((x / (size / 8)) + (y / (size / 8))) % 2 == 0;
                    var i = (y * size + x) * 4;
                    rgba[i] = on ? (byte)235 : (byte)60;
                    rgba[i + 1] = on ? (byte)225 : (byte)70;
                    rgba[i + 2] = on ? (byte)205 : (byte)90;
                    rgba[i + 3] = 255;
                }
            }

            return rgba;
        }

        // A tangent-space normal map (OpenGL convention, +Y up) of 4 x 4 round bumps.
        private static byte[] Bumps(int size)
        {
            var rgba = new byte[size * size * 4];
            var cell = size / 4f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x % cell) / cell * 2f - 1f;
                    // Image rows go down; +Y in the normal map points up the image.
                    var v = 1f - (y % cell) / cell * 2f;
                    var r2 = u * u + v * v;
                    var n = r2 < 0.64f ? new Vector3(u, v, MathF.Sqrt(MathF.Max(0.0001f, 0.64f - r2))).Normalized() : Vector3.UnitZ;
                    var i = (y * size + x) * 4;
                    rgba[i] = (byte)MathF.Round((n.X * 0.5f + 0.5f) * 255f);
                    rgba[i + 1] = (byte)MathF.Round((n.Y * 0.5f + 0.5f) * 255f);
                    rgba[i + 2] = (byte)MathF.Round((n.Z * 0.5f + 0.5f) * 255f);
                    rgba[i + 3] = 255;
                }
            }

            return rgba;
        }

        private void Info(string label)
        {
            var u = Renderer.Universal;
            var stats = u?.Stats;
            TestReport.Info($"Graphics {label}: universal={Renderer.UsingUniversal} healthy={u?.Healthy} reason={u?.FailureReason ?? "-"} "
                + $"items={stats?.Items} drawn={stats?.ItemsDrawn} culled={stats?.ItemsCulled} lights={stats?.Lights} "
                + $"surface={stats?.SurfaceDrawCalls} shadow={stats?.ShadowDrawCalls} post={stats?.PostPasses} tris={stats?.Triangles} "
                + $"maps={stats?.ShadowMapsRendered} gpu={(u?.GpuBytes ?? 0) / 1048576.0:F1} MB shaders={u?.Shaders.CompiledCount}");
            if (u is not null)
            {
                foreach (var error in u.Shaders.Errors.Take(6))
                {
                    TestReport.Info($"Graphics shader error: {error}");
                }
            }
        }

        private IEnumerable<int> Quality(ViewportQuality quality, string shot)
        {
            Step = $"viewport quality {quality}";
            Renderer.EditorGraphics = Renderer.EditorGraphics with { ViewportQuality = quality };
            yield return 20;
            Info(quality.ToString());
            foreach (var wait in Shot(shot))
            {
                yield return wait;
            }
        }

        protected override IEnumerable<int> Steps()
        {
            // GPU objects before any project: the Universal renderer starts when a project first needs it.
            yield return 10;
            var hubTextures = Wisko.Engine.Rendering.Universal.GlTexture.LiveCount;
            var hubMeshes = Wisko.Engine.Rendering.Universal.UniversalMesh.LiveCount;
            foreach (var wait in OpenCard(ProjectName, "open the graphics test"))
            {
                yield return wait;
            }

            yield return 20;
            ViewCamera.Position = new Vector3(0f, 14f, 34f);
            ViewCamera.SetOrientation(-90f, -18f);
            yield return 10;
            Check("Graphics: the test project draws with the Universal renderer", Renderer.UsingUniversal,
                Renderer.Universal?.FailureReason ?? $"renderer {Renderer.Graphics.Renderer}");
            Info("open");
            foreach (var wait in Shot("gfx-01-medium"))
            {
                yield return wait;
            }

            foreach (var wait in Quality(ViewportQuality.Low, "gfx-02-low"))
            {
                yield return wait;
            }

            foreach (var wait in Quality(ViewportQuality.High, "gfx-03-high"))
            {
                yield return wait;
            }

            foreach (var wait in Quality(ViewportQuality.Ultra, "gfx-04-ultra"))
            {
                yield return wait;
            }

            foreach (var wait in Quality(ViewportQuality.Project, "gfx-05-project"))
            {
                yield return wait;
            }

            Step = "bloom and vignette on, filmic";
            Renderer.SetGraphics(Renderer.Graphics with { BloomEnabled = true, VignetteEnabled = true, ToneMapping = ToneMapping.Filmic });
            yield return 10;
            Info("bloom");
            foreach (var wait in Shot("gfx-06-bloom-filmic"))
            {
                yield return wait;
            }

            Step = "select the textured block (World panel row) with the Move tool";
            Click(Find((x, y) => Toolbar.TryHitControl(x, y, Width, out var control) && control == ToolbarControl.Move, 0f, ToolbarRenderer.ToolbarHeight));
            yield return 4;
            Click(PartRow("Textured"));
            yield return 6;
            Check("Graphics: the World panel row selects the textured block", SelectedNames == "Textured", SelectedNames);
            foreach (var wait in Shot("gfx-07-selected"))
            {
                yield return wait;
            }

            Step = "select the toon sphere";
            Click(PartRow("Toon"));
            yield return 6;
            foreach (var wait in Shot("gfx-07b-selected-toon"))
            {
                yield return wait;
            }

            Step = "deselect (click the empty sky)";
            Click((500f, 100f));
            yield return 4;
            Step = "close-up of the textured block and toon sphere";
            ViewCamera.Position = new Vector3(-11f, 6f, 20f);
            ViewCamera.SetOrientation(-100f, -12f);
            yield return 10;
            foreach (var wait in Shot("gfx-08-closeup"))
            {
                yield return wait;
            }

            Step = "the sample models";
            ViewCamera.Position = new Vector3(0f, 6f, -4f);
            ViewCamera.SetOrientation(-90f, -6f);
            yield return 10;
            Info("models");
            foreach (var wait in Shot("gfx-08b-models"))
            {
                yield return wait;
            }

            Step = "the normal map mirror test, face-on";
            ViewCamera.Position = new Vector3(-16f, 4f, -12f);
            ViewCamera.SetOrientation(-90f, -8f);
            yield return 10;
            foreach (var wait in Shot("gfx-08c-mirror-test"))
            {
                yield return wait;
            }

            Step = "the water bottle, close";
            ViewCamera.Position = new Vector3(-6f, 6f, -14f);
            ViewCamera.SetOrientation(-90f, -10f);
            yield return 10;
            foreach (var wait in Shot("gfx-08d-water-bottle"))
            {
                yield return wait;
            }

            Step = "the texture settings test, face-on";
            ViewCamera.Position = new Vector3(0f, 6.5f, -21f);
            ViewCamera.SetOrientation(-90f, -2f);
            yield return 10;
            foreach (var wait in Shot("gfx-08e-texture-settings"))
            {
                yield return wait;
            }

            Step = "the alpha modes test, face-on";
            ViewCamera.Position = new Vector3(4f, 3f, -14f);
            ViewCamera.SetOrientation(-90f, -6f);
            yield return 10;
            foreach (var wait in Shot("gfx-08f-alpha-modes"))
            {
                yield return wait;
            }

            Step = "the emissive strength test";
            ViewCamera.Position = new Vector3(16f, 4f, -12f);
            ViewCamera.SetOrientation(-90f, -6f);
            yield return 10;
            foreach (var wait in Shot("gfx-08g-emissive-strength"))
            {
                yield return wait;
            }

            ViewCamera.Position = new Vector3(0f, 14f, 34f);
            ViewCamera.SetOrientation(-90f, -18f);
            foreach (var wait in PlayStop())
            {
                yield return wait;
            }

            Info("after play");
            foreach (var wait in Shot("gfx-09-after-play"))
            {
                yield return wait;
            }

            Step = "back to the saved settings";
            Renderer.SetGraphics(GraphicsSettings.Default);
            yield return 4;
            var openTextures = Wisko.Engine.Rendering.Universal.GlTexture.LiveCount;
            var openMeshes = Wisko.Engine.Rendering.Universal.UniversalMesh.LiveCount;
            foreach (var wait in LeaveToHub("leave"))
            {
                yield return wait;
            }

            yield return 10;
            Check("Graphics: leaving to the Hub releases the project's textures and meshes (only the renderer's 2 fallback textures stay)",
                Wisko.Engine.Rendering.Universal.GlTexture.LiveCount == hubTextures + 2 && Wisko.Engine.Rendering.Universal.UniversalMesh.LiveCount == hubMeshes
                && openTextures > hubTextures && openMeshes > hubMeshes,
                $"textures {hubTextures} -> {openTextures} -> {Wisko.Engine.Rendering.Universal.GlTexture.LiveCount}, meshes {hubMeshes} -> {openMeshes} -> {Wisko.Engine.Rendering.Universal.UniversalMesh.LiveCount}");
            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }

    // A Universal project while the surface shader cannot compile (ShaderLibrary.BreakForTests): the editor draws it
    // with the Classic renderer, says so once, and works as usual.
    private sealed class FallbackFlow(TestReport report, EngineApplication app, EngineWindow window, string? screenshotDir)
        : EditorFlow(report, app, window, screenshotDir)
    {
        protected override IEnumerable<int> Steps()
        {
            foreach (var wait in OpenCard(GraphicsFlow.ProjectName, "open the graphics test"))
            {
                yield return wait;
            }

            yield return 20;
            var universal = Renderer.Universal;
            Check("Fallback: the broken shader is reported and the project draws with the Classic renderer",
                universal is { Healthy: false } && (universal.FailureReason ?? string.Empty).Contains("surface", StringComparison.Ordinal)
                && Renderer.Graphics.Renderer == RendererKind.Universal && !Renderer.UsingUniversal,
                universal?.FailureReason);
            var pixels = WindowCapture.ReadGlFrontBuffer(Window.FramebufferSize.X, Window.FramebufferSize.Y);
            var distinct = new HashSet<int>();
            for (var i = 0; i < pixels.Length; i += 4 * 97)
            {
                distinct.Add(pixels[i] << 16 | pixels[i + 1] << 8 | pixels[i + 2]);
            }

            Check("Fallback: the scene is drawn (not a blank or frozen view)", distinct.Count > 50, $"{distinct.Count} colours");
            foreach (var wait in Shot("gfx-fallback-classic"))
            {
                yield return wait;
            }

            foreach (var wait in PlayStop())
            {
                yield return wait;
            }

            foreach (var wait in QuitClean())
            {
                yield return wait;
            }
        }
    }
}
