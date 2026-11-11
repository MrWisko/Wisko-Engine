using Wisko.Engine.Platform;
using OpenTK.Windowing.Desktop;

namespace Wisko.Engine.Core;

/// <summary>The system clipboard through the window (GLFW's clipboard on macOS, Windows and Linux).</summary>
internal sealed class WindowClipboard(NativeWindow window) : IClipboard
{
    public string? GetText()
    {
        try
        {
            return window.ClipboardString;
        }
        catch (Exception ex)
        {
            // GLFW reports a format error when the clipboard holds no text.
            Console.WriteLine($"[Clipboard] No text available: {ex.Message}");
            return null;
        }
    }

    public void SetText(string text)
    {
        try
        {
            window.ClipboardString = text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Clipboard] Could not set text: {ex.Message}");
        }
    }
}
