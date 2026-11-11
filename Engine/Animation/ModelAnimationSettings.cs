namespace Wisko.Engine.Animation;

/// <summary>
/// A model instance's saved animation settings (Scene.json "Animation"): the clip it plays by itself when Play starts
/// (<see cref="PlayOnStart"/>), whether that loops and how fast. The editor's Animation panel edits them (one undo step
/// each) and its preview plays with them; scripts start from them but are not limited by them. Immutable: a change is a
/// new value.
/// </summary>
public sealed record ModelAnimationSettings
{
    public const float MinSpeed = -10f;
    public const float MaxSpeed = 10f;

    private readonly float _speed = 1f;

    /// <summary>Nothing plays by itself; loop on; speed 1 (what an instance without saved settings has).</summary>
    public static ModelAnimationSettings Default { get; } = new();

    /// <summary>The clip's name (null: none chosen).</summary>
    public string? Clip { get; init; }

    /// <summary>Play <see cref="Clip"/> when Play starts, before any script asks for anything.</summary>
    public bool PlayOnStart { get; init; }

    public bool Loop { get; init; } = true;

    /// <summary>Playback speed (1 = as authored; negative plays backwards); kept finite within -10..10.</summary>
    public float Speed
    {
        get => _speed;
        init => _speed = float.IsFinite(value) ? global::System.Math.Clamp(value, MinSpeed, MaxSpeed) : 1f;
    }

    public bool IsDefault => Equals(Default);
}
