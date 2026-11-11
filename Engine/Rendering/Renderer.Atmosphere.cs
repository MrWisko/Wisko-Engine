using Wisko.Engine.Atmosphere;
using Wisko.Engine.Editor;

namespace Wisko.Engine.Rendering;

/// <summary>
/// Atmosphere in the editor: the open project's settings (saved with the project), the per-frame shader parameters
/// both backends upload, and its editing (in the Environment &amp; Graphics panel: the Atmosphere tab, and the Atmosphere's
/// rows in the Lighting and Post-Processing tabs). A slider drag, colour drag, switch, preset or Reset is one undo step.
/// Editing is Edit-mode only: Play renders the current settings and never changes them.
/// </summary>
public sealed partial class Renderer
{
    private readonly AtmosphereState _atmosphere = new();
    private readonly float[] _atmosphereUniforms = AtmosphereUniforms.Create();

    /// <summary>The open project's Atmosphere (saved next to its scene).</summary>
    internal AtmosphereSettings Atmosphere => _atmosphere.Settings;

    /// <summary>Entering a project: its saved Atmosphere (or the defaults); not an undo step.</summary>
    internal void LoadAtmosphere(AtmosphereSettings settings)
    {
        DropAtmosphereDrag();
        _atmosphere.Set(settings);
        if (_sheetColor is { } key && EnvironmentSheet.IsAtmosphere(key))
        {
            _sheetColor = null;
        }

        _sheetHsvSource = null;
    }

    // For the view camera, or another (a script's camera feed).
    private float[] BuildAtmosphereUniforms(float aspect, EditorCamera? camera = null)
    {
        camera ??= _camera;
        var view = new AtmosphereView(
            camera.Position,
            camera.Forward,
            camera.Right,
            camera.Up,
            camera.FieldOfViewDegrees * MathF.PI / 180f,
            aspect);
        AtmosphereUniforms.Write(EffectiveAtmosphere, view, _atmosphereUniforms);
        return _atmosphereUniforms;
    }

    private string? MatchingAtmospherePreset() =>
        AtmospherePresets.All.FirstOrDefault(preset => preset.Settings == _atmosphere.Settings)?.Name;

    // What is drawn: while playing, what scripts made of the project's Atmosphere (the panel shows it, read-only).
    private AtmosphereSettings EffectiveAtmosphere => _playMode.PlayAtmosphere ?? _atmosphere.Settings;

    private void ChangeAtmosphere(AtmosphereSettings after, string label)
    {
        var before = _atmosphere.Settings;
        _atmosphere.Set(after);
        RecordAtmosphereChange(before, _atmosphere.Settings, label);
    }

    private void RecordAtmosphereChange(AtmosphereSettings before, AtmosphereSettings after, string label)
    {
        if (before == after)
        {
            return;
        }

        _editorHistory.Push(EditorCommand.AtmosphereChange(_atmosphere, before, after, label));
        Console.WriteLine($"[Atmosphere] {label}");
    }

    private void ApplyAtmospherePreset(string name)
    {
        if (AtmospherePresets.Find(name) is not { } preset)
        {
            return;
        }

        ChangeAtmosphere(preset.Settings, $"Preset {preset.Name}");
        _sheetHsvSource = null;
        _toast.Show($"Atmosphere: {preset.Name} (every value stays editable)");
    }

    private void ResetAtmosphere()
    {
        ChangeAtmosphere(AtmosphereSettings.Default, "Reset to Default");
        _sheetHsvSource = null;
        _toast.Show("Atmosphere reset to the default");
    }

    private static string SlotLabel(AtmosphereColorSlot slot) => slot switch
    {
        AtmosphereColorSlot.Sky => "Sky Color",
        AtmosphereColorSlot.Horizon => "Horizon",
        AtmosphereColorSlot.Sun => "Sun Color",
        AtmosphereColorSlot.Ambient => "Ambient Color",
        AtmosphereColorSlot.Ground => "Ground Color",
        _ => "Fog Color"
    };
}
