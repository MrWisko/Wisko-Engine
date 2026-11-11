using System.Reflection;
using System.Text;
using Wisko.Engine.Assets;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Core;
using Wisko.Engine.Editor;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Graphics;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Rendering;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.Rendering.Backend.Bgfx;
using Wisko.Engine.UI;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// The macOS fix pass (0.1.0): editor shortcuts per platform, the World panel's rename text, starting without a display,
/// the renderer's capabilities in the panels, bgfx's per-frame vertex budget and MSAA flags, OpenGL frame pacing and the
/// texture formats. Everything here runs headless; what needs a window, a GPU or a person is in docs/macos-real-mac-checklist.md.
/// </summary>
internal static class MacFixPassChecks
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    // ------------------------------------------------------------------------------------------------- F1 shortcuts

    public static void Shortcuts(TestReport report)
    {
        static EditorShortcutKeys Pc(bool ctrl, bool super, bool shift, params char[] keys) =>
            EditorShortcuts.Evaluate(ShortcutConvention.PcStyle, ctrl, super, shift, keys.Contains);
        static EditorShortcutKeys Mac(bool ctrl, bool super, bool shift, params char[] keys) =>
            EditorShortcuts.Evaluate(ShortcutConvention.MacStyle, ctrl, super, shift, keys.Contains);
        static bool Nothing(EditorShortcutKeys k) => !k.Undo && !k.Redo && !k.Duplicate && !k.Group && !k.Ungroup && !k.Save;

        // Windows / Linux: exactly the shortcuts the editor had (Ctrl, physical keys, Ctrl+W undo for AZERTY), plus Ctrl+S.
        report.Check("Windows shortcuts unchanged: Ctrl+Z and Ctrl+W undo, Ctrl+Shift+Z still undoes, Ctrl+Y redoes",
            Pc(true, false, false, 'Z').Undo && Pc(true, false, false, 'W').Undo && Pc(true, false, true, 'Z').Undo && !Pc(true, false, true, 'Z').Redo
            && Pc(true, false, false, 'Y').Redo && !Pc(true, false, false, 'Y').Undo);
        report.Check("Windows shortcuts unchanged: Ctrl+D duplicates, Ctrl+G groups, Ctrl+Shift+G ungroups",
            Pc(true, false, false, 'D').Duplicate && Pc(true, false, false, 'G') is { Group: true, Ungroup: false }
            && Pc(true, false, true, 'G') is { Group: false, Ungroup: true });
        report.Check("Windows: Ctrl+S saves the project (new); Ctrl+Shift+S does not",
            Pc(true, false, false, 'S').Save && !Pc(true, false, true, 'S').Save);
        report.Check("Windows: no shortcut without Ctrl, nor with the Windows key; only Ctrl stops the fly keys",
            Nothing(Pc(false, false, false, 'Z', 'Y', 'D', 'G', 'S')) && Nothing(Pc(false, true, false, 'Z', 'S'))
            && Pc(true, false, false).CommandHeld && !Pc(false, true, false).CommandHeld && !Pc(false, false, true).CommandHeld);

        report.Check("macOS: Command+Z undoes, Command+Shift+Z and Command+Y redo",
            Mac(false, true, false, 'Z') is { Undo: true, Redo: false } && Mac(false, true, true, 'Z') is { Undo: false, Redo: true }
            && Mac(false, true, false, 'Y') is { Redo: true, Undo: false });
        report.Check("macOS: Command+D duplicates, Command+G groups, Command+Shift+G ungroups, Command+S saves",
            Mac(false, true, false, 'D').Duplicate && Mac(false, true, false, 'G') is { Group: true, Ungroup: false }
            && Mac(false, true, true, 'G') is { Group: false, Ungroup: true } && Mac(false, true, false, 'S').Save && !Mac(false, true, true, 'S').Save);
        report.Check("macOS: Command+W does nothing (never Undo), and Command stops the fly keys",
            Nothing(Mac(false, true, false, 'W')) && Mac(false, true, false, 'W').CommandHeld);
        report.Check("macOS: Control+Z, Control+S and Control+Command+Z are not editor shortcuts; Control still stops the fly keys",
            Nothing(Mac(true, false, false, 'Z', 'S', 'D', 'G', 'Y')) && Nothing(Mac(true, true, false, 'Z', 'S'))
            && Mac(true, false, false).CommandHeld && !Mac(false, false, true).CommandHeld && Nothing(Mac(false, false, false, 'Z', 'S')));

        // macOS letters follow the keyboard layout (InputManager asks GLFW which letter the layout puts on the key). On
        // AZERTY the key labelled Z sits where QWERTY has W, and the key labelled W where QWERTY has Z.
        var azerty = new Dictionary<char, char> { ['W'] = 'Z', ['Z'] = 'W', ['A'] = 'Q', ['Q'] = 'A' };
        EditorShortcutKeys MacAzerty(params char[] physical) => EditorShortcuts.Evaluate(ShortcutConvention.MacStyle, false, true, false,
            letter => physical.Any(key => (azerty.TryGetValue(key, out var label) ? label : key) == letter));
        report.Check("macOS AZERTY: Command + the key labelled Z (physical W) undoes; Command + the key labelled W (physical Z) does not",
            MacAzerty('W').Undo && Nothing(MacAzerty('Z')));

        // The Save shortcut goes through the same path as the project menu's Save Project, and not while a name is typed.
        var renderer = new Renderer(1280, 720, RenderBackendKind.Bgfx);
        var input = new InputManager();
        var saves = 0;
        renderer.SaveRequested = () => saves++;
        typeof(InputManager).GetProperty(nameof(InputManager.SaveProjectPressed))!.SetValue(input, true);
        var handleSave = typeof(Renderer).GetMethod("HandleSaveShortcut", Hidden)!;
        handleSave.Invoke(renderer, [input]);
        var explorer = (WorldExplorerRenderer)typeof(Renderer).GetField("_worldExplorer", Hidden)!.GetValue(renderer)!;
        explorer.BeginRename(new Part("Crate", Vector3.Zero, new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, 1f)));
        handleSave.Invoke(renderer, [input]);
        var escape = new InputManager();
        escape.SetEditingKeysForTest(escape: true);
        explorer.HandleRenameKeyboard(escape);
        typeof(InputManager).GetProperty(nameof(InputManager.SaveProjectPressed))!.SetValue(input, false);
        handleSave.Invoke(renderer, [input]);
        report.Check("Save shortcut: asks the application to save (the project menu's path), never while a name is being typed",
            saves == 1, $"{saves} save request(s)");
    }

    // ------------------------------------------------------------------------------------------------- F4 rename text

    public static void RenameText(TestReport report)
    {
        var explorer = new WorldExplorerRenderer();
        var part = new Part("Crate", Vector3.Zero, new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, 1f));
        string? committed = null;
        explorer.RenameCommitted = (_, _, name) => committed = name;

        void Frame(string text = "", bool enter = false, bool escape = false, bool backspace = false)
        {
            var input = new InputManager();
            foreach (var rune in text.EnumerateRunes())
            {
                input.OnTextInput(rune.Value);
            }

            input.SetEditingKeysForTest(enter, escape, backspace);
            explorer.HandleRenameKeyboard(input);
        }

        explorer.BeginRename(part);
        for (var i = 0; i < 5; i++)
        {
            Frame(backspace: true);
        }

        var cleared = explorer.RenameText.Length == 0;
        Frame("Azerty");
        var azerty = explorer.RenameText;
        Frame(" \u00E9 \u00E0 \u00E7");
        var accents = explorer.RenameText;
        Frame("/:*");
        var refused = explorer.RenameText == accents;
        report.Check("Rename: the OS's characters are typed as they are (AZERTY's \"Azerty\", accents, spaces), never physical keys",
            cleared && azerty == "Azerty" && accents == "Azerty \u00E9 \u00E0 \u00E7" && refused, $"\"{azerty}\" then \"{accents}\"");

        Frame("\u65E5\u672C e\u0301 \U0001D504");
        var unicode = explorer.RenameText;
        Frame(backspace: true);
        var afterBackspace = explorer.RenameText;
        report.Check("Rename: other scripts, combining accents and characters outside the BMP; Backspace removes a whole character",
            unicode == "Azerty \u00E9 \u00E0 \u00E7\u65E5\u672C e\u0301 \U0001D504" && afterBackspace == "Azerty \u00E9 \u00E0 \u00E7\u65E5\u672C e\u0301 ", $"\"{unicode}\"");

        Frame(new string('x', 80));
        var capped = explorer.RenameText.Length;
        Frame(enter: true);
        report.Check("Rename: at most 48 characters; Enter renames (trimmed) and closes the field",
            capped == 48 && committed is { Length: > 0 } && part.Name == committed && !explorer.IsRenaming, $"{capped} chars, \"{committed}\"");

        explorer.BeginRename(part);
        Frame("Other");
        Frame(escape: true);
        report.Check("Rename: Escape cancels (the name stays)", part.Name == committed && !explorer.IsRenaming);
        report.Check("Input: the physical-key typed-character list is gone (every text field reads the OS's text input)",
            typeof(InputManager).GetProperty("TypedCharacters") is null);
    }

    // ------------------------------------------------------------------------------------------------- F2 no display

    public static void NoDisplay(TestReport report)
    {
        var interval = TimeSpan.FromMilliseconds(500);
        var timeout = TimeSpan.FromSeconds(15);

        (bool Ok, int Checks, TimeSpan Slept, List<string> Log) Run(Func<int, bool> displayAtCheck, TimeSpan t, TimeSpan i)
        {
            var slept = TimeSpan.Zero;
            var log = new List<string>();
            var n = 0;
            var ok = DisplayAvailability.WaitForDisplay(() => displayAtCheck(++n), t, i, d => slept += d, log.Add, out var checks);
            return (ok, checks, slept, log);
        }

        var now = Run(_ => true, timeout, interval);
        report.Check("No display: with a display, one check, no waiting, nothing logged",
            now.Ok && now.Checks == 1 && now.Slept == TimeSpan.Zero && now.Log.Count == 0);
        var later = Run(n => n >= 4, timeout, interval);
        report.Check("No display: a display that comes back is used (3 sleeps of 0.5 s, two log lines)",
            later.Ok && later.Checks == 4 && later.Slept == TimeSpan.FromSeconds(1.5) && later.Log.Count == 2, string.Join(" | ", later.Log));
        var never = Run(_ => false, timeout, interval);
        report.Check("No display: none within 15 s is refused after exactly 15 s of sleeping and 31 checks (bounded, no spinning)",
            !never.Ok && never.Checks == 31 && never.Slept == timeout && never.Log.Count == 2, string.Join(" | ", never.Log));
        var odd = Run(_ => false, TimeSpan.FromSeconds(1.2), interval);
        var zero = Run(_ => false, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        report.Check("No display: the last sleep is shortened to the timeout; a zero interval cannot spin",
            !odd.Ok && odd.Slept == TimeSpan.FromSeconds(1.2) && odd.Checks == 4 && !zero.Ok && zero.Checks == 3);
        var thrown = false;
        try
        {
            DisplayAvailability.WaitForDisplay(() => throw new InvalidOperationException("glfw"), timeout, interval, _ => { }, _ => { }, out _);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        report.Check("No display: a failing check is not hidden; the refusal explains what to do",
            thrown && new DisplayUnavailableException(DisplayAvailability.Problem).Message.Contains("Wake or connect a display"));
    }

    // ------------------------------------------------------------------------------------------------- F3 capabilities

    public static void Capabilities(TestReport report)
    {
        var metal = RendererCapabilities.For(RenderBackendKind.Bgfx, universalCanStart: true);
        var gl = RendererCapabilities.For(RenderBackendKind.OpenGL, universalCanStart: true);
        var failed = RendererCapabilities.For(RenderBackendKind.OpenGL, universalCanStart: false);
        report.Check("Capabilities: Metal draws Classic only (no shadows, materials, lights, post-processing, feeds); OpenGL draws everything",
            metal == RendererCapabilities.MetalClassic && !metal.UniversalRenderer && !metal.Shadows && !metal.MaterialsAndLights
            && !metal.PostProcessing && !metal.CameraFeeds && gl == RendererCapabilities.Full && gl.Shadows && gl.CameraFeeds
            && failed == RendererCapabilities.UniversalFailed && !failed.Shadows && failed.CameraFeeds);

        var universal = GraphicsSettings.Default with { ShadowsEnabled = true, BloomEnabled = true, Quality = QualityLevel.High };
        var atmosphere = AtmosphereSettings.Default;
        List<SheetRow> Tab(EnvironmentTab tab, RendererCapabilities caps, GraphicsSettings g, bool readOnly = false) =>
            EnvironmentSheet.Rows(tab, atmosphere, g, EditorGraphicsPreferences.Default, caps, null, readOnly);
        static bool IsValue(SheetRow r) => r.Kind is not (SheetRowKind.Section or SheetRowKind.Note);

        var lighting = Tab(EnvironmentTab.Lighting, metal, universal);
        report.Check("Metal, Lighting tab: says this Mac draws Classic; every setting muted but still editable; Shadows still On",
            lighting.Any(r => r.Kind == SheetRowKind.Note && r.Label.Contains("This Mac draws the Classic renderer"))
            && lighting.Where(IsValue).All(r => r.Muted && r.Enabled)
            && lighting.Single(r => r.Key == "r.shadows").On);
        var rendering = Tab(EnvironmentTab.Rendering, metal, universal);
        report.Check("Metal, Rendering tab: the renderer choice stays normal (Universal kept); quality and AA muted",
            rendering.Single(r => r.Key == "r.renderer") is { Muted: false, Enabled: true, Selected: (int)RendererKind.Universal }
            && rendering.Single(r => r.Key == "r.quality").Muted && rendering.Single(r => r.Key == "r.aa").Muted
            && rendering.Any(r => r.Label.Contains("kept for Windows")));
        var post = Tab(EnvironmentTab.PostProcessing, metal, universal);
        report.Check("Metal, Post-Processing tab: Exposure, Saturation and Contrast (drawn by Classic) normal; bloom, tone mapping muted",
            post.Where(r => r.Key.StartsWith("a.", StringComparison.Ordinal)).All(r => !r.Muted)
            && post.Single(r => r.Key == "r.bloom").Muted && post.Single(r => r.Key == "r.tone").Muted
            && post.Any(r => r.Label.Contains("Exposure, Saturation and Contrast")));

        var allTabs = new[] { EnvironmentTab.Atmosphere, EnvironmentTab.Lighting, EnvironmentTab.Rendering, EnvironmentTab.PostProcessing };
        report.Check("OpenGL (Windows): no row is muted and no Mac note appears (the panels are as before)",
            allTabs.SelectMany(t => Tab(t, gl, universal)).All(r => !r.Muted && !r.Label.Contains("This Mac")));
        // Muting only changes how a row looks: which rows can be edited (in Edit and in Play) is exactly as on OpenGL.
        bool SameEnabled(bool readOnly) => allTabs.All(tab =>
        {
            var m = Tab(tab, metal, universal, readOnly).Where(IsValue).ToDictionary(r => r.Key, r => r.Enabled);
            var g = Tab(tab, gl, universal, readOnly).Where(IsValue).ToDictionary(r => r.Key, r => r.Enabled);
            return m.Count == g.Count && m.All(kv => g.TryGetValue(kv.Key, out var e) && e == kv.Value);
        });
        report.Check("Metal: the same rows as on OpenGL, each exactly as editable as there, in Edit and in Play (muting only dims)",
            SameEnabled(readOnly: false) && SameEnabled(readOnly: true));

        var target = new MaterialSheetTarget("Crate", false, SurfaceMaterial.Default, new Vector3(0.5f, 0.5f, 0.5f), [], 0, false, null, true);
        var materialRows = MaterialSheet.Rows(target, null, universal: false, readOnly: false, metal);
        var glClassicRows = MaterialSheet.Rows(target, null, universal: false, readOnly: false, gl);
        report.Check("Materials tab: on Metal it says the materials and lights are kept for Windows (no switch to suggest); OpenGL unchanged",
            materialRows.Any(r => r.Label.Contains("kept for Windows")) && !materialRows.Any(r => r.Label.Contains("Environment & Graphics > Rendering"))
            && glClassicRows.Any(r => r.Label.Contains("Environment & Graphics > Rendering")));

        // Opening a Universal project on Metal keeps every setting (what Save writes), with one short notice per project.
        var metalRenderer = new Renderer(1280, 720, RenderBackendKind.Bgfx);
        var custom = universal with { ShadowDistance = 321f, ShadowBias = 2.5f, PointLightShadows = false, VignetteEnabled = true };
        metalRenderer.LoadGraphics(custom);
        var console = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            metalRenderer.ShowGraphicsCompatibilityNotice("project-a");
            metalRenderer.ShowGraphicsCompatibilityNotice("project-a");
            metalRenderer.ShowGraphicsCompatibilityNotice("project-b");
            metalRenderer.LoadGraphics(GraphicsSettings.LegacyDefault);
            metalRenderer.ShowGraphicsCompatibilityNotice("classic-project");
        }
        finally
        {
            Console.SetOut(console);
        }

        var notices = captured.ToString().Split('\n').Count(l => l.Contains("[Graphics] This Mac draws the Classic renderer"));
        metalRenderer.LoadGraphics(custom);
        var toast = typeof(ToastRenderer).GetField("_message", Hidden)!.GetValue(typeof(Renderer).GetField("_toast", Hidden)!.GetValue(metalRenderer));
        report.Check("Metal: a Universal project's settings are kept exactly as loaded (Save writes them unchanged)",
            metalRenderer.Graphics == custom && metalRenderer.Graphics.Renderer == RendererKind.Universal);
        report.Check("Metal: one notice per Universal project per session (none for a Classic project), shown as a toast",
            notices == 2 && toast is string t && t.Contains("no shadows"), $"{notices} notice(s)");
        report.Check("OpenGL: no notice for a Universal project",
            gl.CompatibilityNotice(custom) is null && metal.CompatibilityNotice(GraphicsSettings.LegacyDefault) is null);

        var path = Path.Combine(Path.GetTempPath(), $"wisko-fixpass-{Guid.NewGuid():N}", GraphicsFile.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var savedOk = GraphicsFile.TrySave(path, metalRenderer.Graphics, out _);
        var reloaded = GraphicsFile.Load(path, out _, out _);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        report.Check("Metal: Graphics.json saved from the Mac reads back identical (no setting dropped for Windows)",
            savedOk && reloaded == custom);
        report.Check("New World screen: on Metal a note says the pictures show the Universal renderer; none on OpenGL",
            metal.TemplatePreviewNote is { } note && note.Contains("without shadows") && gl.TemplatePreviewNote is null);
    }

    // ------------------------------------------------------------------------------------------------- F6, F8, F9, F10

    public static void BudgetPacingFormats(TestReport report, string dir)
    {
        const uint stride = 36; // position3 + colour3 + normal3 floats, as BgfxEditorGraphics packs per-frame geometry
        var reserve = TransientBudget.ReserveBytes / stride;
        var defaultPool = (6u << 20) / stride;
        var pool = BgfxDevice.TransientVertexBufferBytes / stride;

        // Around the investigated threshold, at bgfx's default pool: never all-or-nothing any more.
        var at28k = TransientBudget.Affordable(28_000 * 6, TransientBudget.Query(28_000 * 6, reserve) > defaultPool ? defaultPool : 28_000 * 6 + reserve, reserve, 6);
        var at30k = TransientBudget.Affordable(30_000 * 6, defaultPool, reserve, 6);
        report.Check("Particle budget (bgfx default 6 MiB): 30,000 particles draw as many as fit (not none) and keep 1 MiB for the UI",
            at30k > 0 && at30k % 6 == 0 && defaultPool - at30k >= reserve && at28k > 0, $"28k -> {at28k / 6} drawn, 30k -> {at30k / 6} drawn");

        // At the documented maximum, with the raised pool: every particle and debug line fits, and the UI keeps its reserve.
        var maxParticles = (uint)ParticleSystem.MaxTotalParticles * 6;
        var maxLines = (uint)DebugDraw.MaxSegments * 2;
        var particles = TransientBudget.Affordable(maxParticles, Math.Min(pool, TransientBudget.Query(maxParticles, reserve)), reserve, 6);
        var left = pool - particles;
        var lines = TransientBudget.Affordable(maxLines, Math.Min(left, TransientBudget.Query(maxLines, reserve)), reserve, 2);
        report.Check("Particle budget (16 MiB): 60,000 particles and 20,000 debug lines all draw, and 1 MiB stays free for the UI",
            particles == maxParticles && lines == maxLines && left - lines >= reserve,
            $"pool {pool:N0} vertices, particles {particles:N0}, lines {lines:N0}, left {left - lines:N0}");
        report.Check("Particle budget: nothing asked, nothing left, odd counts and huge requests are safe",
            TransientBudget.Affordable(0, pool, reserve, 6) == 0 && TransientBudget.Affordable(600, reserve, reserve, 6) == 0
            && TransientBudget.Affordable(100, 1000 + reserve, reserve, 6) == 96 && TransientBudget.Query(uint.MaxValue - 5, reserve) == uint.MaxValue);

        report.Check("MSAA (bgfx): 2x and 4x set the swap chain's MSAA flag, anything else none; reset keeps only vsync; default 4x",
            BgfxDevice.SwapChainFlagsFor(0) == 0 && BgfxDevice.SwapChainFlagsFor(3) == 0 && BgfxDevice.SwapChainFlagsFor(8) == 0
            && BgfxDevice.SwapChainFlagsFor(4) == (uint)global::Bgfx.bgfx.SwapChainFlags.MsaaX4
            && BgfxDevice.SwapChainFlagsFor(2) == (uint)global::Bgfx.bgfx.SwapChainFlags.MsaaX2
            && BgfxDevice.DeviceResetFlags == (uint)global::Bgfx.bgfx.ResetFlags.Vsync
            && new EngineApplicationSettings { Title = "t", ProjectsRootPath = dir }.BgfxMsaaSamples == 4);

        report.Check("Frame pacing: OpenGL on macOS presents with vsync; Windows OpenGL (compositor pacer) and bgfx are unchanged",
            EngineWindow.UsesSwapIntervalVsync(RenderBackendKind.OpenGL, isMacOS: true)
            && !EngineWindow.UsesSwapIntervalVsync(RenderBackendKind.OpenGL, isMacOS: false)
            && !EngineWindow.UsesSwapIntervalVsync(RenderBackendKind.Bgfx, isMacOS: true));

        // Texture formats: the material picker offers what the importer takes; TGA and BMP really import and decode.
        var project = Path.Combine(dir, "project");
        var outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "red.tga"), Tga(3, 2, 255, 0, 0));
        File.WriteAllBytes(Path.Combine(outside, "blue.bmp"), Bmp(3, 2, 0, 0, 255));
        var tga = ProjectTextureImporter.TryImport(Path.Combine(outside, "red.tga"), project, out var tgaPath, out var tgaError);
        var bmp = ProjectTextureImporter.TryImport(Path.Combine(outside, "blue.bmp"), project, out var bmpPath, out var bmpError);
        var tgaImage = TextureImage.LoadFile(Path.Combine(project, tgaPath ?? string.Empty), out _);
        var bmpImage = TextureImage.LoadFile(Path.Combine(project, bmpPath ?? string.Empty), out _);
        report.Check("Textures: TGA and BMP (offered by the material picker) import into Assets/Textures and decode to the right pixels",
            ProjectTextureImporter.Extensions.Contains("tga") && ProjectTextureImporter.Extensions.Contains("bmp")
            && tga && bmp && tgaImage is { Width: 3, Height: 2 } && tgaImage.Pixels[0] == 255 && tgaImage.Pixels[2] == 0
            && bmpImage is { Width: 3, Height: 2 } && bmpImage.Pixels[0] == 0 && bmpImage.Pixels[2] == 255,
            $"{tgaPath} {tgaError} {bmpPath} {bmpError}");
        var homeFilter = ((Array)typeof(TextureImage).Assembly.GetType("Wisko.Engine.Platform.ImageFilePicker")!
            .GetField("Filters", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Cast<object>()
            .SelectMany(f => (IReadOnlyList<string>)f.GetType().GetProperty("Extensions")!.GetValue(f)!).ToList();
        report.Check("Home project picture: the picker offers only what a project picture accepts (PNG, JPEG)",
            homeFilter.All(e => CustomThumbnailImage.SupportedExtensions.Contains("." + e)) && homeFilter.Count == 3, string.Join(",", homeFilter));
    }

    // An uncompressed 32-bit TGA (top-left origin) of one colour.
    private static byte[] Tga(int width, int height, byte r, byte g, byte b)
    {
        var data = new List<byte> { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 32, 0x28 };
        for (var i = 0; i < width * height; i++)
        {
            data.AddRange([b, g, r, 255]);
        }

        return data.ToArray();
    }

    // A 24-bit BMP (bottom-up rows padded to 4 bytes) of one colour.
    private static byte[] Bmp(int width, int height, byte r, byte g, byte b)
    {
        var row = (width * 3 + 3) & ~3;
        var size = 54 + row * height;
        var data = new byte[size];
        Encoding.ASCII.GetBytes("BM").CopyTo(data, 0);
        BitConverter.GetBytes(size).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);
        BitConverter.GetBytes(row * height).CopyTo(data, 34);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = 54 + y * row + x * 3;
                data[o] = b;
                data[o + 1] = g;
                data[o + 2] = r;
            }
        }

        return data;
    }
}
