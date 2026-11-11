namespace Wisko.Engine.Rendering.Backend;

/// <summary>Which rendering backend owns the window and its render/present path.</summary>
public enum RenderBackendKind
{
    /// <summary>OpenTK window with an OpenGL 3.3 Core context; frames are presented with SwapBuffers.</summary>
    OpenGL,

    /// <summary>OpenTK window without a graphics context (ContextAPI.NoAPI); bgfx presents frames.</summary>
    Bgfx
}
