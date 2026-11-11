namespace Wisko.Engine.Platform;

/// <summary>
/// The system clipboard's plain text, for editor text fields. Implemented by the window layer (GLFW on every
/// platform); never offered to Lua scripts.
/// </summary>
public interface IClipboard
{
    /// <summary>The clipboard's text, or null when it holds no text.</summary>
    string? GetText();

    void SetText(string text);
}
