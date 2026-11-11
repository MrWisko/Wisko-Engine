namespace Wisko.Engine.Graphics;

/// <summary>
/// The open project's current rendering settings (Graphics.json) in the editor. Its edits (Environment & Graphics panel) and undo/redo
/// replace <see cref="Settings"/> (always sanitized); Play Mode never changes it (scripts change a Play copy).
/// </summary>
public sealed class GraphicsState
{
    public GraphicsSettings Settings { get; private set; } = GraphicsSettings.LegacyDefault;

    public void Set(GraphicsSettings settings) => Settings = settings.Sanitized();
}
