using Wisko.Engine.Graphics;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.Rendering.Universal;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The renderers in the editor: the open project's Graphics.json settings, the person's editor preference (viewport
/// quality), and which renderer draws each frame. The Universal renderer draws when the project asks for it, the
/// backend is OpenGL and it started healthily; otherwise the Classic renderer draws (the macOS/bgfx build, projects
/// without a Graphics.json, or a start-up failure, which is logged and shown once).
/// </summary>
public sealed partial class Renderer
{
    private UniversalRenderer? _universal;
    private readonly GraphicsState _graphicsState = new();
    private bool _universalFrame;
    private bool _universalFailureShown;
    private readonly List<SceneObject> _environmentObjects = [];

    /// <summary>The open project's rendering settings (saved as Graphics.json).</summary>
    internal GraphicsSettings Graphics => _graphicsState.Settings;

    /// <summary>The person's editor preferences (not part of the project).</summary>
    internal EditorGraphicsPreferences EditorGraphics { get; set; } = EditorGraphicsPreferences.Default;

    /// <summary>Called when the person changes an editor preference (the host saves it).</summary>
    internal Action<EditorGraphicsPreferences>? EditorGraphicsChanged { get; set; }

    /// <summary>What is drawn: while playing, what scripts made of the project's settings (Lua Graphics; until Stop).</summary>
    internal GraphicsSettings EffectiveGraphics => _playMode.PlayGraphics ?? _graphicsState.Settings;

    /// <summary>The Universal renderer (null on bgfx); exposed for diagnostics and tests.</summary>
    internal UniversalRenderer? Universal => _universal;

    /// <summary>Whether this frame is drawn by the Universal renderer.</summary>
    internal bool UsingUniversal =>
        _backend == RenderBackendKind.OpenGL && _universal is { Healthy: true } && EffectiveGraphics.Renderer == RendererKind.Universal;

    /// <summary>The quality level the 3D View renders at: the editor preference, else the game's (scripts may change it).</summary>
    internal QualityLevel EffectiveQualityLevel => EditorGraphics.EffectiveLevel(EffectiveGraphics.Quality);

    /// <summary>Entering a project: its saved Graphics settings (Classic when it has none); not an undo step.</summary>
    internal void LoadGraphics(GraphicsSettings settings)
    {
        _graphicsState.Set(settings);
        _universal?.ResetProject();
    }

    // The projects (by key) whose Classic-only notice this session has shown: once each, so reopening stays quiet.
    private readonly HashSet<string> _compatibilityNoticeShown = new(StringComparer.Ordinal);

    /// <summary>
    /// Entering a project (<paramref name="sessionKey"/>: its Id, or one key for every new world): when it asks for the
    /// Universal renderer and only Classic draws here (the macOS app), a short notice, once per project per session. The
    /// project's settings are not changed.
    /// </summary>
    internal void ShowGraphicsCompatibilityNotice(string sessionKey)
    {
        if (Capabilities.CompatibilityNotice(_graphicsState.Settings) is not { } notice || !_compatibilityNoticeShown.Add(sessionKey))
        {
            return;
        }

        Console.WriteLine($"[Graphics] {notice}");
        ShowToast(notice, 7f);
    }

    /// <summary>Back in the Hub: the project's textures and meshes are released (render targets stay for the next one).</summary>
    internal void ReleaseProjectGraphics() => _universal?.ResetProject();

    /// <summary>A Graphics panel edit (the caller records undo).</summary>
    internal void SetGraphics(GraphicsSettings settings) => _graphicsState.Set(settings);

    // The Universal renderer starts (compiles its shaders, creates its resources) the first time a project needs it, so
    // a session with only Classic projects pays nothing for it.
    private void InitializeUniversalGl() => _universal = new UniversalRenderer();

    // Configures and collects the frame for the Universal renderer (true when it draws this frame).
    private bool BeginUniversalFrame()
    {
        _universalFrame = false;
        if (_universal is null || _workspace is null || _selection is null
            || _backend != RenderBackendKind.OpenGL || EffectiveGraphics.Renderer != RendererKind.Universal)
        {
            return false;
        }

        if (!_universal.Started)
        {
            if (_universal.Initialize())
            {
                Console.WriteLine($"[Graphics] Universal renderer ready: {_universal.Shaders.CompiledCount} programs compiled in {_universal.Shaders.CompileMilliseconds:0} ms");
            }
            else
            {
                Console.WriteLine($"[Graphics] The Universal renderer could not start; every project draws with the Classic renderer this session ({_universal.FailureReason})");
            }
        }

        if (!_universal.Healthy)
        {
            if (EffectiveGraphics.Renderer == RendererKind.Universal && !_universalFailureShown)
            {
                _universalFailureShown = true;
                ShowToast("Graphics: the Universal renderer could not start; showing the Classic renderer (see the log)", 6f);
            }

            return false;
        }

        if (!UsingUniversal)
        {
            return false;
        }

        var graphics = EffectiveGraphics;
        _universal.Configure(graphics, graphics.QualityFor(EffectiveQualityLevel), EffectiveAtmosphere, ProjectRoot);
        _environmentObjects.Clear();
        foreach (var drawable in _drawables)
        {
            if (IsEnvironmentDrawn(drawable))
            {
                _environmentObjects.Add(drawable);
            }
        }
        _universal.BeginFrame(_workspace.Parts, _environmentObjects, _selection.SelectedParts, _camera.Position);
        _universalFrame = true;
        return true;
    }

    private void EndUniversalFrame()
    {
        if (_universalFrame)
        {
            _universal!.EndFrame();
        }

        _universalFrame = false;
    }

    // The world through the Universal renderer, directly into the bound framebuffer (camera feeds, thumbnails).
    private void RenderWorldUniversalDirect(float aspect, EditorCamera camera, bool selection)
    {
        GL_Viewport(out var width, out var height);
        var view = ViewSetup.FromCamera(camera, aspect, width, height);
        _universal!.RenderDirect(view, _ => DrawUniversalParticles(camera), selection);
    }

    private static void GL_Viewport(out int width, out int height)
    {
        var viewport = new int[4];
        OpenTK.Graphics.OpenGL4.GL.GetInteger(OpenTK.Graphics.OpenGL4.GetPName.Viewport, viewport);
        width = global::System.Math.Max(1, viewport[2]);
        height = global::System.Math.Max(1, viewport[3]);
    }

    private void ShutdownUniversalGl()
    {
        _universal?.Dispose();
        _universal = null;
    }
}
