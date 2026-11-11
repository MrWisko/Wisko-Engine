using System.Runtime.InteropServices;

namespace Wisko.Engine.Rendering.Backend.Bgfx;

/// <summary>
/// macOS-only: makes the bgfx-owned CAMetalLayer present like the OpenGL window does. CAMetalLayer
/// defaults to kCGColorSpaceSRGB, so macOS colour-matches Metal output from sRGB to the display profile,
/// while the OpenGL surface is shown without matching; saturated UI colours then differ between
/// backends. Setting the layer's colorspace to nil disables colour matching, so the same RGB values
/// reach the display on both backends (on any screen). Output-layer setting only: no colour changes.
/// bgfx's Metal swap-chain resize sets the layer's pixel format, which restores the sRGB default, so this
/// is re-applied after every bgfx reset that resizes the swap chain.
/// </summary>
internal static class MetalLayerColorSpace
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr Class(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr argument);

    /// <summary>
    /// Disables colour matching on the CAMetalLayer bgfx attached to the window's content view.
    /// Call on the main thread after bgfx.init. Returns false if no CAMetalLayer is attached.
    /// </summary>
    public static bool DisableColorMatching(IntPtr nsWindow)
    {
        if (!OperatingSystem.IsMacOS() || nsWindow == IntPtr.Zero)
        {
            return false;
        }

        var contentView = Send(nsWindow, Selector("contentView"));
        var layer = contentView == IntPtr.Zero ? IntPtr.Zero : Send(contentView, Selector("layer"));
        if (layer == IntPtr.Zero || !SendBool(layer, Selector("isKindOfClass:"), Class("CAMetalLayer")))
        {
            return false;
        }

        Send(layer, Selector("setColorspace:"), IntPtr.Zero);
        return true;
    }
}
