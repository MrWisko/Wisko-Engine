using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wisko.Engine.Input;
using Wisko.Engine.Rendering;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.Rendering.Backend.Bgfx;
using Wisko.Engine.Rendering.Backend.Bgfx.Editor;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using GLFWMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace Wisko.Engine.Core;

/// <summary>
/// The editor window for every backend. OpenGL: GL 3.3 Core context, presented with SwapBuffers. bgfx:
/// ContextAPI.NoAPI (OpenTK 4.9.4's GameWindow loop is NoAPI-safe: no context created or made current,
/// vsync skipped), the bgfx device attaches Metal to the NSWindow and presents each frame; no GL is used.
/// Input, close gating and right-mouse look are shared.
/// </summary>
internal sealed class EngineWindow : GameWindow
{
    private readonly InputManager _input;
    private readonly Renderer _renderer;
    private readonly EngineApplication _application;
    private readonly RenderBackendKind _backend;
    private readonly int _bgfxMsaaSamples;
    private readonly BgfxDevice? _device;
    private IntPtr _nsWindow;
    private bool _colorSpaceResetPending;
    private Vector2 _savedCursorPosition;
    private bool _rightMouseLookActive;
    private bool _gameCursorLocked;

    // Windows OpenGL: each presented frame waits for the desktop compositor; null on every other platform and backend.
    private readonly CompositorFramePacer? _framePacer;

    // Windows OpenGL: the editor viewport asks for 4x MSAA (0 on every other platform and backend). The driver may
    // provide fewer samples; the count it actually provided is logged at load.
    private const int WindowsOpenGlSamples = 4;
    private readonly int _requestedSamples;

    private EngineWindow(
        EngineApplicationSettings settings,
        InputManager input,
        Renderer renderer,
        EngineApplication application,
        int samples)
        : base(GameWindowSettings.Default, CreateNativeWindowSettings(settings, samples))
    {
        _requestedSamples = samples;
        _backend = settings.RenderBackend;
        _bgfxMsaaSamples = settings.BgfxMsaaSamples;
        _device = _backend == RenderBackendKind.Bgfx ? new BgfxDevice() : null;
        _framePacer = OperatingSystem.IsWindows() && _backend == RenderBackendKind.OpenGL
            ? CompositorFramePacer.CreateForWindows()
            : null;
        _input = input;
        _renderer = renderer;
        _application = application;
    }

    /// <summary>
    /// Creates the editor window. If the window cannot be created with MSAA at all, it is created again without
    /// (and logged), so a missing MSAA mode never stops the editor from starting.
    /// </summary>
    public static EngineWindow Create(EngineApplicationSettings settings, InputManager input, Renderer renderer, EngineApplication application)
    {
        // No display (asleep or disconnected): window creation would crash natively, so stop here with a message.
        DisplayAvailability.EnsureDisplay();

        var samples = OperatingSystem.IsWindows() && settings.RenderBackend == RenderBackendKind.OpenGL ? WindowsOpenGlSamples : 0;
        if (samples == 0)
        {
            return new EngineWindow(settings, input, renderer, application, 0);
        }

        try
        {
            return new EngineWindow(settings, input, renderer, application, samples);
        }
        catch (GLFWException ex)
        {
            Console.WriteLine($"[Renderer] MSAA: could not create a {samples}x window ({ex.Message}); continuing without MSAA");
            return new EngineWindow(settings, input, renderer, application, 0);
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        CursorState = CursorState.Normal;
        if (_device is not null)
        {
            InitializeBgfxDevice(_device);
        }

        if (_framePacer is not null)
        {
            Console.WriteLine("[Renderer] Frame pacing: desktop compositor (DwmFlush)");
        }

        if (_backend == RenderBackendKind.OpenGL)
        {
            LogMultisampling();
        }

        if (UsesSwapIntervalVsync(_backend, OperatingSystem.IsMacOS()))
        {
            // Without it the optional --renderer=opengl on a Mac presents unthrottled (thousands of frames a second, a
            // whole core). SwapBuffers then waits for the display: no busy-waiting, its own refresh rate (120 Hz).
            VSync = VSyncMode.On;
            Console.WriteLine("[Renderer] Frame pacing: vsync (macOS OpenGL)");
        }

        // Seed the framebuffer size so the first rendered frame uses real pixels
        // (2x logical on Retina/HiDPI) before the first framebuffer-resize event.
        _renderer.OnFramebufferResize(FramebufferSize.X, FramebufferSize.Y);
        _application.Initialize();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        if (KeyboardState.IsKeyPressed(Keys.Escape))
        {
            if (!_application.TryConsumeEscape())
            {
                Close();
            }

            return;
        }

        _application.Update((float)args.Time);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        _application.Render();

        // OpenGL presents through the window's context. bgfx owns presentation (bgfx.frame()), so a
        // NoAPI window must never call SwapBuffers.
        if (_backend == RenderBackendKind.OpenGL)
        {
            SwapBuffers();
            _framePacer?.AfterPresent();
            return;
        }

        _device!.Frame();

        // bgfx applies a reset inside frame(), and its swap-chain resize sets the layer's pixel format,
        // which puts CAMetalLayer back on its default sRGB colour space. Re-apply after that frame.
        if (_colorSpaceResetPending)
        {
            _colorSpaceResetPending = false;
            MetalLayerColorSpace.DisableColorMatching(_nsWindow);
        }
    }

    private unsafe void InitializeBgfxDevice(BgfxDevice device)
    {
        _nsWindow = GLFW.GetCocoaWindow(WindowPtr);
        if (_nsWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("GLFW did not return a Cocoa NSWindow for the bgfx backend.");
        }

        var framebuffer = FramebufferSize;
        device.Initialize(_nsWindow, framebuffer.X, framebuffer.Y, BgfxEditorGraphics.SkyClearColor, _bgfxMsaaSamples);

        // Present without colour matching, like the OpenGL window, so colours match across backends.
        if (!MetalLayerColorSpace.DisableColorMatching(_nsWindow))
        {
            Console.WriteLine("[Renderer] Warning: no CAMetalLayer found; Metal output stays colour-matched (sRGB)");
        }

        Console.WriteLine($"[Renderer] bgfx backend: {device.RendererName}");
        Console.WriteLine(device.MsaaSamples > 0 ? $"[Renderer] MSAA: {device.MsaaSamples}x" : "[Renderer] MSAA: off");
        Console.WriteLine($"[Renderer] Framebuffer: {framebuffer.X}x{framebuffer.Y} (window {ClientSize.X}x{ClientSize.Y})");
    }

    // The sample count the driver provided for the window's framebuffer. A separate method, so the bgfx path never
    // JIT-compiles (or loads) the OpenGL bindings.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogMultisampling()
    {
        GL.GetInteger(GetPName.SampleBuffers, out var sampleBuffers);
        GL.GetInteger(GetPName.Samples, out var samples);
        var provided = sampleBuffers > 0 ? $"{samples}x" : "off";
        Console.WriteLine(_requestedSamples > 0
            ? $"[Renderer] MSAA: {provided} (requested {_requestedSamples}x)"
            : $"[Renderer] MSAA: {provided}");
    }

    /// <summary>
    /// Whether the window presents with the display's vsync through the GL swap interval: OpenGL on macOS. Windows paces
    /// OpenGL with its compositor (<see cref="CompositorFramePacer"/>); bgfx sets its own vsync (BgfxDevice).
    /// </summary>
    internal static bool UsesSwapIntervalVsync(RenderBackendKind backend, bool isMacOS) => backend == RenderBackendKind.OpenGL && isMacOS;

    /// <summary>Window settings for the selected backend: a GL 3.3 Core context, or no context for bgfx.</summary>
    internal static NativeWindowSettings CreateNativeWindowSettings(EngineApplicationSettings settings, int samples)
    {
        var windowSettings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(settings.Width, settings.Height),
            Title = settings.Title,
            Icon = settings.Icon
        };

        switch (settings.RenderBackend)
        {
            case RenderBackendKind.OpenGL:
                windowSettings.API = ContextAPI.OpenGL;
                windowSettings.Flags = ContextFlags.ForwardCompatible;
                windowSettings.APIVersion = new Version(3, 3);
                windowSettings.Profile = ContextProfile.Core;
                windowSettings.NumberOfSamples = samples;
                break;
            case RenderBackendKind.Bgfx:
                // bgfx attaches its own Metal/D3D surface to the native window; no GL context may exist.
                windowSettings.API = ContextAPI.NoAPI;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.RenderBackend, "Unknown render backend.");
        }

        return windowSettings;
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        _renderer.OnResize(e.Width, e.Height);
    }

    protected override void OnFramebufferResize(FramebufferResizeEventArgs e)
    {
        base.OnFramebufferResize(e);
        _renderer.OnFramebufferResize(e.Width, e.Height);
        if (_device is not null && _device.Resize(e.Width, e.Height))
        {
            _colorSpaceResetPending = true;
            Console.WriteLine($"[Renderer] Framebuffer: {e.Width}x{e.Height} (window {ClientSize.X}x{ClientSize.Y}, bgfx reset)");
        }
    }

    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        base.OnMouseMove(e);

        if (_rightMouseLookActive)
        {
            _input.OnMouseMove(e.DeltaX, e.DeltaY, _savedCursorPosition.X, _savedCursorPosition.Y);
            return;
        }

        _input.OnMouseMove(e.DeltaX, e.DeltaY, MouseState.Position.X, MouseState.Position.Y);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        _input.OnMouseButton(e.Button, true, MouseState.Position.X, MouseState.Position.Y);

        if (e.Button == GLFWMouseButton.Right
            && _application.ShouldBeginRightMouseLook(MouseState.Position.X, MouseState.Position.Y))
        {
            BeginRightMouseLook();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _input.OnMouseButton(e.Button, false, MouseState.Position.X, MouseState.Position.Y);

        if (e.Button == GLFWMouseButton.Right)
        {
            EndRightMouseLook(restoreCursor: true);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _input.OnMouseWheel(e.OffsetX, e.OffsetY);
    }

    // Key presses with the OS key repeat, for text editing (the polled keyboard state has no repeat).
    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);
        _input.OnKeyDown(e.Key, e.ScanCode, e.Modifiers, e.IsRepeat);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        _input.OnTextInput(e.Unicode);
    }

    // Every close path (window close button, Close() from Escape, Cmd+Q via GLFW's close request)
    // arrives here; the application may veto it to ask for a project name first.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_application.RequestClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnUnload()
    {
        EndRightMouseLook(restoreCursor: false);
        CursorState = CursorState.Normal;
        // Application (Hub textures, editor meshes, programs, atlas) first, then bgfx, then the window.
        _application.Shutdown();
        if (_device is not null)
        {
            _device.Shutdown();
            Console.WriteLine("[Renderer] bgfx shut down");
        }

        base.OnUnload();
    }

    private void BeginRightMouseLook()
    {
        if (_rightMouseLookActive)
        {
            return;
        }

        _savedCursorPosition = MouseState.Position;
        _rightMouseLookActive = true;
        CursorState = CursorState.Grabbed;
    }

    /// <summary>
    /// Play: a script locked or unlocked the mouse (Input.SetMouseLocked). Locked, the cursor is hidden and held in the
    /// window while mouse movement keeps arriving (mouse look); unlocked, it is the normal cursor again.
    /// </summary>
    internal void SetGameCursorLocked(bool locked)
    {
        _gameCursorLocked = locked;
        if (!_rightMouseLookActive)
        {
            CursorState = locked ? CursorState.Grabbed : CursorState.Normal;
        }
    }

    private void EndRightMouseLook(bool restoreCursor)
    {
        if (!_rightMouseLookActive)
        {
            return;
        }

        _rightMouseLookActive = false;
        CursorState = _gameCursorLocked ? CursorState.Grabbed : CursorState.Normal;

        if (restoreCursor)
        {
            MousePosition = _savedCursorPosition;
            _input.OnMouseMove(0, 0, _savedCursorPosition.X, _savedCursorPosition.Y);
        }
    }
}
