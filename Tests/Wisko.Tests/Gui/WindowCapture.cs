using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Wisko.Engine.Rendering;

namespace Wisko.Tests.Gui;

/// <summary>Saves a window's client area as PNG (Windows PrintWindow, so other windows on top do not matter).</summary>
internal static unsafe class WindowCapture
{
    public static IntPtr Handle(NativeWindow window) => GLFW.GetWin32Window(window.WindowPtr);

    public static void SavePng(IntPtr hwnd, string path)
    {
        GetClientRect(hwnd, out var rect);
        int width = rect.Right, height = rect.Bottom;
        if (width <= 0 || height <= 0)
        {
            return; // minimized
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32
        };
        var bitmap = CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            PrintWindow(hwnd, memoryDc, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); // BGRA -> RGBA
                pixels[i + 3] = 255;
            }

            File.WriteAllBytes(path, PngEncoder.EncodeRgba(pixels, width, height));
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// The last presented frame read back from the window's own front buffer (OpenGL, on the render thread). Works when
    /// PrintWindow cannot: e.g. while the display sleeps, the compositor does not refresh what PrintWindow copies.
    /// </summary>
    public static void SaveGlFrontBuffer(string path, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        File.WriteAllBytes(path, PngEncoder.EncodeRgba(ReadGlFrontBuffer(width, height), width, height));
    }

    /// <summary>The last presented frame as top-down RGBA rows (alpha forced opaque), as <see cref="SaveGlFrontBuffer"/> reads it.</summary>
    public static byte[] ReadGlFrontBuffer(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Front);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.ReadBuffer(ReadBufferMode.Back);

        // OpenGL rows run bottom-up.
        var flipped = new byte[pixels.Length];
        var stride = width * 4;
        for (var row = 0; row < height; row++)
        {
            System.Buffer.BlockCopy(pixels, row * stride, flipped, (height - 1 - row) * stride, stride);
        }

        for (var i = 3; i < flipped.Length; i += 4)
        {
            flipped[i] = 255;
        }

        return flipped;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
