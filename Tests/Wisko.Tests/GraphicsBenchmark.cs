// --bench-graphics: the Graphics V1 benchmark (docs/graphics.md, "Performance"). Builds nine repeatable scenes as
// ordinary projects, opens each in the real editor (EngineApplication, OpenGL, 1280 x 720), lets it settle, then times
// frames back to back inside one window update: Update + Render per frame, CPU with a stopwatch and GPU with
// GL_TIME_ELAPSED queries, without presenting (a sleeping display throttles presentation, so presented FPS would measure
// the display, not the engine). "frame" = wall time per frame over the whole run (GL finished), i.e. what the engine
// sustains, CPU and GPU overlapped. For the comparison with an older build, the same scene code (without materials,
// lights and Graphics.json) was compiled against that build.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Core;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.World;
using Wisko.Engine.Graphics;

namespace Wisko.Tests;

internal static class GraphicsBenchmark
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int Warmup = 8;
    private const int Frames = 120;

    // Unmeasured frames rendered back to back first: the GPU leaves its low-power clocks (the throttled warm-up frames
    // keep it there) before anything is timed.
    private const int Burst = 200;

    private sealed record Scene(string Name, bool Play, Vector3 From, Vector3 At, Func<string, Workspace> Build, string[] Scripts);

    private static string _examples = "";
    private static string _mode = "classic";

    /// <summary>Runs the nine scenes in <paramref name="mode"/> (classic, low, medium, high, ultra) and writes the table.</summary>
    public static int Run(string outFile, string work, string mode)
    {
        if (mode is not ("classic" or "low" or "medium" or "high" or "ultra"))
        {
            Console.Error.WriteLine("--bench-graphics: the mode must be classic, low, medium, high or ultra");
            return 2;
        }

        var label = "this build";
        _examples = Path.GetDirectoryName(Path.GetDirectoryName(Checks.GraphicsDemo.ModelsFolder()))!;
        _mode = mode;

        // Its own folder inside the work folder: only that is ever replaced.
        work = Path.Combine(work, "graphics-bench");
        var projects = Path.Combine(work, "Projects");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Directory.CreateDirectory(projects);
        var scenes = Scenes();
        var infos = new List<ProjectInfo>();
        var store = new ProjectStore(projects);
        foreach (var scene in scenes)
        {
            if (!store.TryCreateProject(scene.Name, WorldTemplate.Classic, new Workspace(), out var project, out var error))
            {
                throw new InvalidOperationException(error);
            }

            var workspace = scene.Build(project.FolderPath);
            foreach (var (name, source) in ScriptSources().Where(s => scene.Scripts.Contains(s.Name)))
            {
                var scripts = Path.Combine(project.FolderPath, "Assets", "Scripts");
                Directory.CreateDirectory(scripts);
                File.WriteAllText(Path.Combine(scripts, name + ".lua"), source, new UTF8Encoding(false));
            }

            if (!SceneFile.TrySave(workspace, project.ScenePath, out error))
            {
                throw new InvalidOperationException(error);
            }

            if (_mode != "classic")
            {
                var quality = Enum.Parse<QualityLevel>(_mode, ignoreCase: true);
                GraphicsFile.TrySave(project.GraphicsPath, GraphicsSettings.Default with { Quality = quality }, out _);
            }
            infos.Add(project);
        }

        var settings = new EngineApplicationSettings
        {
            Title = $"GfxBench {label} {_mode}",
            Width = 1280,
            Height = 720,
            ProjectsRootPath = projects,
            HistoryPath = Path.Combine(work, "history"),
            RenderBackend = RenderBackendKind.OpenGL
        };
        var gameType = Type.GetType("Wisko.Editor.WiskoEditorModule, Wisko.Editor", throwOnError: true)!;
        var game = (IGameModule)Activator.CreateInstance(gameType, nonPublic: true)!;
        var lines = new List<string>
        {
            $"# GfxBench {label} mode={_mode} {DateTime.Now:yyyy-MM-dd HH:mm} | {Environment.ProcessorCount} logical CPUs | .NET {Environment.Version} | frames {Frames} after {Warmup} warm-up and a {Burst}-frame unmeasured burst",
            "scene|cpu_mean_ms|cpu_p95_ms|gpu_mean_ms|gpu_p95_ms|frame_ms|fps_unpresented|load_ms|working_set_mb|private_mb|gc_heap_mb|alloc_per_frame_kb|extra"
        };

        using var app = new EngineApplication(settings, game);
        var window = (EngineWindow)typeof(EngineApplication).GetField("_window", Hidden)!.GetValue(app)!;
        var renderer = (Renderer)typeof(EngineApplication).GetField("_renderer", Hidden)!.GetValue(app)!;
        var input = (InputManager)typeof(EngineApplication).GetField("_input", Hidden)!.GetValue(app)!;
        var camera = (EditorCamera)typeof(Renderer).GetField("_camera", Hidden)!.GetValue(renderer)!;
        var open = typeof(EngineApplication).GetMethod("OpenProject", Hidden)!;
        var showHub = typeof(EngineApplication).GetMethod("ShowHub", Hidden)!;
        var startPlay = typeof(Renderer).GetMethod("StartPlay", Hidden)!;
        var steps = Run().GetEnumerator();
        var wait = 0;
        window.UpdateFrame += _ =>
        {
            if (wait-- > 0)
            {
                return;
            }

            if (!steps.MoveNext())
            {
                typeof(EngineApplication).GetField("_closeApproved", Hidden)!.SetValue(app, true);
                window.Close();
                return;
            }

            wait = steps.Current;
        };
        app.Run();
        File.WriteAllLines(outFile, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return 0;

        IEnumerable<int> Run()
        {
            yield return 10;
            for (var s = 0; s < scenes.Count; s++)
            {
                var scene = scenes[s];
                var load = Stopwatch.StartNew();
                open.Invoke(app, [infos[s]]);
                var loadMs = load.Elapsed.TotalMilliseconds;
                yield return 2;
                var f = (scene.At - scene.From).Normalized();
                camera.Position = scene.From;
                camera.SetOrientation(MathF.Atan2(f.Z, f.X) * 180f / MathF.PI, MathF.Asin(Math.Clamp(f.Y, -1f, 1f)) * 180f / MathF.PI);
                if (scene.Play)
                {
                    startPlay.Invoke(renderer, [input]);
                }

                yield return Warmup;
                lines.Add(Measure(scene.Name, loadMs, app, renderer, window, work));
                if (scene.Play)
                {
                    renderer.StopPlay(false);
                    yield return 2;
                }

                showHub.Invoke(app, null);
                yield return 4;
            }

            lines.Add($"# after all scenes, back in the Hub: textures {Wisko.Engine.Rendering.Universal.GlTexture.LiveCount}, meshes {Wisko.Engine.Rendering.Universal.UniversalMesh.LiveCount}, programs {Wisko.Engine.Rendering.Universal.GlProgram.LiveCount}, shader compile {renderer.Universal?.Shaders.CompileMilliseconds:0} ms for {renderer.Universal?.Shaders.CompiledCount} programs (once, at start-up)");
        }
    }

    private static string Measure(string name, double loadMs, EngineApplication app, Renderer renderer, EngineWindow window, string work)
    {
        for (var i = 0; i < Burst; i++)
        {
            app.Update(1f / 60f);
            app.Render();
        }

        GL.Finish();
        var queries = new int[Frames];
        GL.GenQueries(Frames, queries);
        var cpu = new double[Frames];
        var stopwatch = new Stopwatch();
        GL.Finish();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var wall = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            stopwatch.Restart();
            GL.BeginQuery(QueryTarget.TimeElapsed, queries[i]);
            app.Update(1f / 60f);
            app.Render();
            GL.EndQuery(QueryTarget.TimeElapsed);
            cpu[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        GL.Finish();
        var frameMs = wall.Elapsed.TotalMilliseconds / Frames;
        var allocPerFrame = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)Frames / 1024d;
        var gpu = new double[Frames];
        for (var i = 0; i < Frames; i++)
        {
            GL.GetQueryObject(queries[i], GetQueryObjectParam.QueryResult, out long nanoseconds);
            gpu[i] = nanoseconds / 1e6;
        }

        GL.DeleteQueries(Frames, queries);

        // The last frame's image (back buffer, never presented) for a visual check.
        var width = window.FramebufferSize.X;
        var height = window.FramebufferSize.Y;
        var pixels = new byte[width * height * 4];
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(pixels, (height - 1 - y) * width * 4, flipped, y * width * 4, width * 4);
        }

        File.WriteAllBytes(Path.Combine(work, $"{name}-{_mode}.png"), PngEncoder.EncodeRgba(flipped, width, height));

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var extra = "";
        if (renderer.UsingUniversal && renderer.Universal is { } u)
        {
            var st = u.Stats;
            extra = $"draws {st.DrawCalls} (surface {st.SurfaceDrawCalls}, shadow {st.ShadowDrawCalls}, post {st.PostPasses}); items {st.Items} drawn {st.ItemsDrawn}; tris {st.Triangles}; lights {st.Lights}; shadow maps/frame {st.ShadowMapsRendered}; gpu memory {u.GpuBytes / 1048576.0:0.0} MB (textures {Wisko.Engine.Rendering.Universal.GlTexture.TotalBytes / 1048576.0:0.0} MB)";
        }
        else
        {
            extra = "Classic renderer";
        }

        Array.Sort(cpu);
        Array.Sort(gpu);
        static double P95(double[] sorted) => sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        var inv = CultureInfo.InvariantCulture;
        return string.Join('|', name,
            cpu.Average().ToString("0.00", inv), P95(cpu).ToString("0.00", inv),
            gpu.Average().ToString("0.00", inv), P95(gpu).ToString("0.00", inv),
            frameMs.ToString("0.00", inv), (1000.0 / frameMs).ToString("0", inv),
            loadMs.ToString("0", inv),
            (process.WorkingSet64 / 1048576.0).ToString("0", inv), (process.PrivateMemorySize64 / 1048576.0).ToString("0", inv),
            (GC.GetTotalMemory(false) / 1048576.0).ToString("0", inv), allocPerFrame.ToString("0.0", inv), extra);
    }

    // ---------------------------------------------------------------------------------------------- scenes

    private static List<Scene> Scenes() =>
    [
        new("01-basic", false, new Vector3(0f, 25f, 60f), new Vector3(0f, 2f, 0f), Basic, []),
        new("02-textured-pbr", false, new Vector3(0f, 12f, 34f), new Vector3(0f, 3f, 0f), TexturedPbr, []),
        new("03-multiple-lights", false, new Vector3(0f, 22f, 50f), new Vector3(0f, 0f, 0f), MultipleLights, []),
        new("04-shadow-heavy", false, new Vector3(0f, 40f, 90f), new Vector3(0f, 0f, 0f), ShadowHeavy, []),
        new("05-animated-characters", true, new Vector3(0f, 14f, 40f), new Vector3(0f, 0f, 0f), Animated, []),
        new("06-weather", true, new Vector3(0f, 12f, 45f), new Vector3(0f, 3f, 0f), Weather, ["Weather"]),
        new("07-large-object-count", false, new Vector3(0f, 70f, 140f), new Vector3(0f, 0f, 0f), LargeCount, []),
        new("08-camera-feed", true, new Vector3(0f, 12f, 45f), new Vector3(0f, 3f, 0f), CameraFeed, ["Feed"]),
        new("09-combined-stress", true, new Vector3(0f, 45f, 110f), new Vector3(0f, 0f, 0f), Stress, ["Weather", "Feed"]),
    ];

    private static (string Name, string Source)[] ScriptSources() =>
    [
        ("Weather", "local Weather = require(\"Wisko.Weather\")\nlocal w\nfunction Start()\n  w = Weather.new({ Initial = \"Rain\" })\nend\n"),
        ("Feed", "function Start()\n  local cam = Cameras.Create(\"BenchCam\", { Position = Vector3.new(30, 20, 30), FieldOfView = 60 })\n  cam:LookAt(Vector3.new(0, 0, 0))\n  UI.Create(\"Image\", { Name = \"FeedImage\", Anchor = { 1, 1 }, Position = { 1, -16, 1, -16 }, Size = { 0, 384, 0, 216 }, Image = \"camera:BenchCam\" })\nend\n")
    ];

    private static Part P(string name, Vector3 position, Vector3 size, Vector3 color, ShapeType shape = ShapeType.Block) =>
        new(name, position, size, color, null, anchored: true, shapeType: shape);

    private static readonly ShapeType[] Shapes = [ShapeType.Block, ShapeType.Sphere, ShapeType.Cylinder, ShapeType.Wedge, ShapeType.Pyramid];

    private static Vector3 Palette(int i) => (i % 6) switch
    {
        0 => new Vector3(0.85f, 0.3f, 0.25f),
        1 => new Vector3(0.3f, 0.65f, 0.35f),
        2 => new Vector3(0.25f, 0.45f, 0.85f),
        3 => new Vector3(0.9f, 0.8f, 0.35f),
        4 => new Vector3(0.8f, 0.8f, 0.82f),
        _ => new Vector3(0.6f, 0.4f, 0.8f)
    };

    private static void Grid(Workspace w, int columns, int rows, float spacing, Vector3 size, string prefix)
    {
        var i = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var x = (c - (columns - 1) * 0.5f) * spacing;
                var z = (r - (rows - 1) * 0.5f) * spacing;
                w.AddPart(P($"{prefix} {i}", new Vector3(x, size.Y * 0.5f, z), size, Palette(i), Shapes[i % Shapes.Length]));
                i++;
            }
        }
    }

    private static Workspace Basic(string root)
    {
        var w = new Workspace();
        Grid(w, 10, 5, 7f, new Vector3(4f, 4f, 4f), "Basic");
        return w;
    }

    private static ModelAsset Import(string root, string file)
    {
        using var decoder = new AssimpModelImporter();
        if (!new ProjectModelImporter(decoder).TryImport(file, root, out var import, out var error))
        {
            throw new InvalidOperationException($"import {file}: {error}");
        }

        return import.Asset;
    }

    private static Part Model(string name, ModelAsset asset, Vector3 origin, float scale, float yaw = 0f)
    {
        var part = new Part(name, Vector3.Zero, new Vector3(1f, 1f, 1f), ModelReference.DefaultTint, anchored: true)
        {
            Model = new ModelReference(asset.AssetPath, asset)
        };
        ModelTransform.SetInstanceTransform(part, origin, new Vector3(0f, yaw, 0f), new Vector3(scale, scale, scale));
        return part;
    }

    private static string Gfx(string folder) => Path.Combine(_examples, "graphics-demo", "Models", folder, folder + ".glb");

    private static Workspace TexturedPbr(string root)
    {
        var w = new Workspace();
        var bottle = Import(root, Gfx("WaterBottle"));
        var mirror = Import(root, Gfx("NormalTangentMirrorTest"));
        var alpha = Import(root, Gfx("AlphaBlendModeTest"));
        for (var i = 0; i < 6; i++)
        {
            w.AddPart(Model($"Bottle {i}", bottle, new Vector3(-15f + i * 6f, 3.2f, 6f), 18f));
        }

        w.AddPart(Model("Mirror", mirror, new Vector3(-8f, 4.6f, -6f), 3f));
        w.AddPart(Model("Alpha", alpha, new Vector3(8f, 1f, -4f), 1.5f));
        for (var i = 0; i < 12; i++)
        {
            var part = P($"Sphere {i}", new Vector3(-16.5f + i * 3f, 1.5f, 14f), new Vector3(2.5f, 2.5f, 2.5f), new Vector3(0.95f, 0.7f, 0.42f), ShapeType.Sphere);
            part.Material = SurfaceMaterial.Default with { Metallic = i % 2, Roughness = i / 11f };
            w.AddPart(part);
        }

        return w;
    }

    private static void AddLights(Workspace w, int count, float radius, float height, bool shadows, int shadowLimit)
    {
        for (var i = 0; i < count; i++)
        {
            var a = i * MathF.PI * 2f / count;
            var part = P($"Light {i}", new Vector3(MathF.Cos(a) * radius, height, MathF.Sin(a) * radius), new Vector3(0.8f, 0.8f, 0.8f), Palette(i), ShapeType.Sphere);
            part.CanCollide = false;
            part.Material = SurfaceMaterial.Default with { Mode = ShadingMode.Unlit, CastShadows = false };
            part.Light = (i % 3 == 0
                ? PartLight.Create(LightType.Spot) with { Range = 30f, Intensity = 300f }
                : PartLight.Create(LightType.Point) with { Range = 18f, Intensity = 60f }) with { Color = Palette(i), CastShadows = shadows && i < shadowLimit };
            if (part.Light.Type == LightType.Spot)
            {
                part.Rotation = new Vector3(90f, 0f, 0f);
            }
            w.AddPart(part);
        }
    }

    private static Workspace MultipleLights(string root)
    {
        var w = new Workspace();
        Grid(w, 8, 5, 6f, new Vector3(3f, 3f, 3f), "Lit");
        AddLights(w, 24, 18f, 5f, shadows: false, 0);
        return w;
    }

    private static Workspace ShadowHeavy(string root)
    {
        var w = new Workspace();
        Grid(w, 20, 15, 5f, new Vector3(1.5f, 10f, 1.5f), "Pillar");
        AddLights(w, 6, 25f, 14f, shadows: true, 6);
        return w;
    }

    private static Workspace Animated(string root)
    {
        var w = new Workspace();
        var fox = Import(root, Path.Combine(_examples, "animation-demo", "Models", "Fox", "Fox.glb"));
        var clips = new[] { "Run", "Walk", "Survey" };
        for (var i = 0; i < 30; i++)
        {
            var part = Model($"Fox {i}", fox, new Vector3(-20f + (i % 10) * 4.5f, 0.5f, -8f + (i / 10) * 8f), 0.05f, i * 12f);
            part.Animation = new ModelAnimationSettings { Clip = clips[i % 3], PlayOnStart = true, Loop = true, Speed = 1f };
            w.AddPart(part);
        }

        return w;
    }

    private static Workspace Weather(string root)
    {
        var w = new Workspace();
        Grid(w, 8, 4, 7f, new Vector3(4f, 4f, 4f), "Town");
        w.AddPart(P("Weather", new Vector3(0f, 0.5f, 60f), new Vector3(1f, 1f, 1f), new Vector3(0.5f, 0.5f, 0.5f)));
        AttachScript(w.Parts[^1], "Weather");
        return w;
    }

    private static Workspace LargeCount(string root)
    {
        var w = new Workspace();
        Grid(w, 50, 40, 4f, new Vector3(2f, 2f, 2f), "Block");
        return w;
    }

    private static Workspace CameraFeed(string root)
    {
        var w = new Workspace();
        Grid(w, 8, 4, 7f, new Vector3(4f, 4f, 4f), "Room");
        w.AddPart(P("Feed", new Vector3(0f, 0.5f, 60f), new Vector3(1f, 1f, 1f), new Vector3(0.5f, 0.5f, 0.5f)));
        AttachScript(w.Parts[^1], "Feed");
        return w;
    }

    private static Workspace Stress(string root)
    {
        var w = new Workspace();
        Grid(w, 30, 30, 4f, new Vector3(2f, 3f, 2f), "Stress");
        AddLights(w, 16, 30f, 8f, shadows: true, 6);
        var fox = Import(root, Path.Combine(_examples, "animation-demo", "Models", "Fox", "Fox.glb"));
        for (var i = 0; i < 10; i++)
        {
            var part = Model($"Fox {i}", fox, new Vector3(-20f + i * 4.5f, 3.5f, 66f), 0.05f);
            part.Animation = new ModelAnimationSettings { Clip = "Run", PlayOnStart = true, Loop = true, Speed = 1f };
            w.AddPart(part);
        }

        var bottle = Import(root, Gfx("WaterBottle"));
        for (var i = 0; i < 4; i++)
        {
            w.AddPart(Model($"Bottle {i}", bottle, new Vector3(-9f + i * 6f, 3.2f, 72f), 18f));
        }

        w.AddPart(P("Scripts", new Vector3(0f, 0.5f, 90f), new Vector3(1f, 1f, 1f), new Vector3(0.5f, 0.5f, 0.5f)));
        AttachScript(w.Parts[^1], "Weather");
        AttachScript(w.Parts[^1], "Feed");
        return w;
    }

    private static void AttachScript(Part part, string name)
    {
        ScriptReference.TryCreate($"Assets/Scripts/{name}.lua", out var reference);
        part.Scripts.Add(reference!);
    }
}
