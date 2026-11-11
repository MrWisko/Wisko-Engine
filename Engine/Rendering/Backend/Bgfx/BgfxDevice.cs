using System.Runtime.InteropServices;
using BgfxNative = global::Bgfx.bgfx;

namespace Wisko.Engine.Rendering.Backend.Bgfx;

/// <summary>
/// bgfx device lifecycle only: attach to a native window, clear the main view, resize, present a
/// frame, shut down. Single-threaded: renderFrame is called before init so bgfx renders on the
/// calling (main) thread, which Cocoa requires. Pinned to bgfx API 163 (see ThirdParty/bgfx).
/// </summary>
internal sealed unsafe class BgfxDevice : IDisposable
{
    private const ushort MainView = 0;

    /// <summary>
    /// bgfx's per-frame transient vertex space (bgfx's default is 6 MiB). Lua particles use six 36-byte vertices each, so
    /// the default held about 29,000 beside the editor's UI; this holds the engine's maximum (60,000 particles, 12.4 MiB),
    /// 20,000 debug lines (1.4 MiB) and the UI. A frame that still runs out draws as much as fits (TransientBudget).
    /// </summary>
    public const uint TransientVertexBufferBytes = 16u << 20;

    private const uint ResetFlags = (uint)BgfxNative.ResetFlags.Vsync;

    // The main swap chain's per-surface flags (its MSAA), passed explicitly on every reset so a resize keeps them.
    private uint _swapChainFlags;

    private bool _initialized;

    public BgfxNative.RendererType RendererType { get; private set; } = BgfxNative.RendererType.Noop;

    public string RendererName { get; private set; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The back buffer's multisample count in use (0 = off).</summary>
    public int MsaaSamples { get; private set; }

    /// <summary>
    /// The main swap chain's flags for a sample count: 2x or 4x MSAA, else none. In this bgfx (API 163) multisampling is a
    /// per-surface setting (<c>SwapChain::flags</c>); reset flags carry only device-wide settings such as vsync.
    /// </summary>
    internal static uint SwapChainFlagsFor(int msaaSamples) => msaaSamples switch
    {
        2 => (uint)BgfxNative.SwapChainFlags.MsaaX2,
        4 => (uint)BgfxNative.SwapChainFlags.MsaaX4,
        _ => 0u
    };

    /// <summary>The device-wide reset flags: vsync (presentation paced by the display).</summary>
    internal static uint DeviceResetFlags => ResetFlags;

    /// <summary>bgfx production target for now: Apple Silicon macOS with Metal.</summary>
    public static bool IsPlatformSupported(out string reason)
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"the bgfx backend currently supports macOS arm64 (Metal) only; this is {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}.";
        return false;
    }

    /// <summary>
    /// Initializes Metal on the given NSWindow with a framebuffer of the given pixel size and, when the back buffer
    /// format supports it, <paramref name="msaaSamples"/> multisampling (0 = off).
    /// </summary>
    public void Initialize(IntPtr nativeWindow, int framebufferWidth, int framebufferHeight, uint clearRgba, int msaaSamples = 0)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("bgfx is already initialized.");
        }

        // Before init: request single-threaded mode (no separate bgfx render thread).
        BgfxNative.render_frame(-1);

        var init = new BgfxNative.Init();
        BgfxNative.init_ctor(&init);
        init.type = BgfxNative.RendererType.Metal;
        init.fallback = 0; // Metal or fail; never silently pick another renderer.
        init.swapChain.nwh = (void*)nativeWindow;
        init.swapChain.width = (uint)framebufferWidth;
        init.swapChain.height = (uint)framebufferHeight;
        init.swapChain.flags = _swapChainFlags = SwapChainFlagsFor(msaaSamples); // every Apple Silicon GPU supports 2x and 4x
        init.reset = ResetFlags;
        init.limits.maxTransientVbSize = TransientVertexBufferBytes;

        if (!BgfxNative.init(&init))
        {
            throw new InvalidOperationException("bgfx.init failed for the Metal renderer.");
        }

        _initialized = true;
        RendererType = BgfxNative.get_renderer_type();
        RendererName = Marshal.PtrToStringAnsi(BgfxNative.get_renderer_name(RendererType)) ?? RendererType.ToString();
        if (RendererType != BgfxNative.RendererType.Metal)
        {
            Shutdown();
            throw new InvalidOperationException($"bgfx initialized {RendererName} instead of Metal.");
        }

        Width = framebufferWidth;
        Height = framebufferHeight;
        // Should the back buffer's format report no multisampling here, continue without it (applies at the next frame).
        var caps = BgfxNative.get_caps();
        var msaaSupported = (caps->formats[(int)BgfxNative.TextureFormat.BGRA8] & (uint)BgfxNative.CapsFormatFlags.TextureMsaa) != 0;
        MsaaSamples = _swapChainFlags != 0 && msaaSupported ? msaaSamples : 0;
        if (_swapChainFlags != 0 && !msaaSupported)
        {
            _swapChainFlags = 0;
            var swapChain = NeutralSwapChain();
            swapChain.width = (uint)framebufferWidth;
            swapChain.height = (uint)framebufferHeight;
            swapChain.flags = 0;
            BgfxNative.reset(ResetFlags, &swapChain);
        }

        BgfxNative.set_view_clear(
            MainView,
            (ushort)(BgfxNative.ClearFlags.Color | BgfxNative.ClearFlags.Depth | BgfxNative.ClearFlags.Stencil),
            clearRgba,
            1.0f,
            0);
    }

    /// <summary>Resizes the main back buffer; no-op unless the pixel size actually changed.</summary>
    public bool Resize(int framebufferWidth, int framebufferHeight)
    {
        if (!_initialized || framebufferWidth <= 0 || framebufferHeight <= 0
            || (framebufferWidth == Width && framebufferHeight == Height))
        {
            return false;
        }

        var swapChain = NeutralSwapChain();
        swapChain.width = (uint)framebufferWidth;
        swapChain.height = (uint)framebufferHeight;
        swapChain.flags = _swapChainFlags; // the same MSAA as at start, explicitly
        BgfxNative.reset(ResetFlags, &swapChain);
        Width = framebufferWidth;
        Height = framebufferHeight;
        return true;
    }

    /// <summary>
    /// Clears the main view over the whole framebuffer, submits anything drawn this frame and presents
    /// (bgfx owns presentation).
    /// </summary>
    public void Frame()
    {
        if (!_initialized)
        {
            return;
        }

        BgfxNative.set_view_rect(MainView, 0, 0, (ushort)Width, (ushort)Height, 0f, 1f);
        BgfxNative.touch(MainView);
        BgfxNative.frame(0);
    }

    public void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        BgfxNative.shutdown();
    }

    public void Dispose() => Shutdown();

    // A C# default(SwapChain) is NOT neutral for reset: colour format 0 is BC1 and depth handle 0 is a real
    // handle. "Keep current" is TextureFormat.Count for both formats and an invalid (0xFFFF) depth handle.
    private static BgfxNative.SwapChain NeutralSwapChain()
    {
        var swapChain = default(BgfxNative.SwapChain);
        swapChain.formatColor = BgfxNative.TextureFormat.Count;
        swapChain.formatDepthStencil = BgfxNative.TextureFormat.Count;
        swapChain.depth = new BgfxNative.TextureHandle { idx = ushort.MaxValue };
        return swapChain;
    }
}
