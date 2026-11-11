namespace Wisko.Engine.Atmosphere;

/// <summary>
/// The open project's current Atmosphere in the editor. Edits, presets, Reset and undo/redo replace
/// <see cref="Settings"/> (always sanitized); Play Mode never changes it.
/// </summary>
public sealed class AtmosphereState
{
    public AtmosphereSettings Settings { get; private set; } = AtmosphereSettings.Default;

    public void Set(AtmosphereSettings settings) => Settings = settings.Sanitized();
}
