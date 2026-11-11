using Wisko.Engine.Scripting;
using OpenTK.Windowing.GraphicsLibraryFramework;
using GlfwMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;

namespace Wisko.Engine.Input;

/// <summary>
/// Fills a <see cref="ScriptInputState"/> from the window's keyboard and mouse state for one frame. The platform key
/// codes stay here; scripts only ever see the engine's key and button names.
/// </summary>
internal static class ScriptInputSampler
{
    // Indexed like ScriptInputState.KeyNames / MouseButtonNames.
    private static readonly Keys[] KeyCodes = ScriptInputState.KeyNames.Select(ToKey).ToArray();
    private static readonly GlfwMouseButton[] MouseButtonCodes = ScriptInputState.MouseButtonNames.Select(ToMouseButton).ToArray();

    public static void Sample(KeyboardState keyboard, MouseState mouse, ScriptInputState state)
    {
        for (var i = 0; i < KeyCodes.Length; i++)
        {
            var key = KeyCodes[i];
            state.SetKey(i, keyboard.IsKeyDown(key), keyboard.IsKeyPressed(key), keyboard.IsKeyReleased(key));
        }

        for (var i = 0; i < MouseButtonCodes.Length; i++)
        {
            var button = MouseButtonCodes[i];
            state.SetMouseButton(i, mouse.IsButtonDown(button), mouse.IsButtonPressed(button), mouse.IsButtonReleased(button));
        }

        state.MouseX = mouse.X;
        state.MouseY = mouse.Y;
        state.MouseDeltaX = mouse.Delta.X;
        state.MouseDeltaY = mouse.Delta.Y;
        state.ScrollDelta = mouse.ScrollDelta.Y;
    }

    // "A".."Z" and named keys match OpenTK's names; digits are D0..D9 there.
    private static Keys ToKey(string name) =>
        Enum.Parse<Keys>(name.Length == 1 && char.IsAsciiDigit(name[0]) ? "D" + name : name);

    private static GlfwMouseButton ToMouseButton(string name) => Enum.Parse<GlfwMouseButton>(name);
}
