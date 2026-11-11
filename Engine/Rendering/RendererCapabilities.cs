using Wisko.Engine.Graphics;
using Wisko.Engine.Rendering.Backend;

namespace Wisko.Engine.Rendering;

/// <summary>
/// What the renderer drawing this editor can show, decided in one place from the backend (and whether the Universal
/// renderer can start), so panels, notices and the New World screen ask it instead of checking the platform.
/// <list type="bullet">
/// <item>Windows / OpenGL: everything (the Universal renderer when a project asks for it, camera feeds).</item>
/// <item>macOS (bgfx / Metal): the Classic renderer only. No shadows, materials and textures, part lights, post-processing
/// beyond exposure, saturation and contrast, or camera feeds.</item>
/// </list>
/// A project's Universal settings are kept, edited and saved either way: they are simply not drawn here.
/// </summary>
internal sealed record RendererCapabilities
{
    /// <summary>OpenGL with a working (or not yet started) Universal renderer.</summary>
    public static RendererCapabilities Full { get; } = new(true, true, null, null);

    /// <summary>bgfx / Metal (the macOS app): Classic only.</summary>
    public static RendererCapabilities MetalClassic { get; } = new(false, false, "This Mac draws the Classic renderer (Metal)",
        "The pictures show the Universal renderer (Windows); this Mac draws Classic, without shadows.");

    /// <summary>OpenGL whose Universal renderer could not start (the log says why).</summary>
    public static RendererCapabilities UniversalFailed { get; } = new(false, true, "Universal could not start here (see the log)",
        "The pictures show the Universal renderer, which could not start here: worlds draw with Classic.");

    private RendererCapabilities(bool universalRenderer, bool cameraFeeds, string? classicReason, string? templatePreviewNote)
    {
        UniversalRenderer = universalRenderer;
        CameraFeeds = cameraFeeds;
        ClassicReason = classicReason;
        TemplatePreviewNote = templatePreviewNote;
    }

    /// <summary>The Universal renderer can draw a project that asks for it.</summary>
    public bool UniversalRenderer { get; }

    /// <summary>Sun, spot and point light shadows (Universal only).</summary>
    public bool Shadows => UniversalRenderer;

    /// <summary>Materials, textures and part lights (Universal only).</summary>
    public bool MaterialsAndLights => UniversalRenderer;

    /// <summary>Tone mapping, white balance, bloom, ambient occlusion, vignette (Classic has exposure, saturation and contrast).</summary>
    public bool PostProcessing => UniversalRenderer;

    /// <summary>Lua camera feeds in GUI Images (OpenGL only).</summary>
    public bool CameraFeeds { get; }

    /// <summary>Why only the Classic renderer draws here (null when Universal can draw).</summary>
    public string? ClassicReason { get; }

    public static RendererCapabilities For(RenderBackendKind backend, bool universalCanStart) =>
        backend != RenderBackendKind.OpenGL ? MetalClassic : universalCanStart ? Full : UniversalFailed;

    /// <summary>
    /// The short notice when a project set to the Universal renderer is drawn with Classic here (null otherwise).
    /// </summary>
    public string? CompatibilityNotice(GraphicsSettings settings) =>
        UniversalRenderer || settings.Renderer != RendererKind.Universal || ClassicReason is null
            ? null
            : $"{ClassicReason}: no shadows, textures or lights. The project's settings are kept.";

    /// <summary>The New World screen's note under its title: its pictures show the Universal renderer (null when it draws here).</summary>
    public string? TemplatePreviewNote { get; }
}
