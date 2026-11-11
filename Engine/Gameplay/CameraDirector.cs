using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;

namespace Wisko.Engine.Gameplay;

/// <summary>Where a camera is and where it looks: position, pitch (−89..89, up positive), yaw (0 looks along +X, 90 along +Z) and vertical field of view in degrees.</summary>
public readonly record struct CameraPose(Vector3 Position, float Pitch, float Yaw, float FieldOfView)
{
    public static CameraPose Of(IScriptCameraHost view) => new(view.Position, view.Pitch, view.Yaw, view.FieldOfView);

    /// <summary>The pose looking from <paramref name="position"/> at <paramref name="target"/> (pitch kept within ±89°).</summary>
    public static (float Pitch, float Yaw) LookRotation(Vector3 position, Vector3 target, float pitch, float yaw)
    {
        var d = target - position;
        var flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        if (flat < 1e-6f && MathF.Abs(d.Y) < 1e-6f)
        {
            return (pitch, yaw);
        }

        var newPitch = global::System.Math.Clamp(MathF.Atan2(d.Y, flat) * 180f / MathF.PI, -89f, 89f);
        var newYaw = flat < 1e-6f ? yaw : MathF.Atan2(d.Z, d.X) * 180f / MathF.PI;
        return (newPitch, newYaw);
    }

    public Vector3 Forward
    {
        get
        {
            var p = Pitch * MathF.PI / 180f;
            var y = Yaw * MathF.PI / 180f;
            return new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Sin(p), MathF.Cos(p) * MathF.Sin(y));
        }
    }
}

/// <summary>
/// A camera scripts made (Lua: Cameras.Create): a named pose that the view shows while it is the active camera, and
/// that can follow an object or feed a GUI Image (render to texture). Play-only.
/// </summary>
public sealed class ScriptCameraRig
{
    internal ScriptCameraRig(int id, string name, CameraPose pose)
    {
        Id = id;
        Name = name;
        Pose = pose;
    }

    public int Id { get; }

    public string Name { get; }

    public CameraPose Pose { get; set; }

    /// <summary>The object it follows each frame (null: it stays where scripts put it).</summary>
    public Part? FollowTarget { get; set; }

    /// <summary>Where it sits relative to the followed object: an offset in the object's own axes.</summary>
    public Vector3 FollowOffset { get; set; }

    /// <summary>It turns to look at the followed object (plus <see cref="LookOffset"/>, world axes).</summary>
    public bool FollowLookAt { get; set; } = true;

    public Vector3 LookOffset { get; set; }

    /// <summary>How quickly it catches up (per second, exponential); 0 follows exactly.</summary>
    public float FollowSmoothing { get; set; }

    /// <summary>Rendered into a texture every frame for GUI Images ("camera:Name"), at this size.</summary>
    public bool HasFeed { get; set; }

    public int FeedWidth { get; set; }

    public int FeedHeight { get; set; }

    public bool Destroyed { get; internal set; }
}

/// <summary>
/// Which camera the Play view shows (Lua: Cameras). The gameplay camera is the one scripts move through Camera (the
/// editor's view camera while playing); scripts may make named cameras and make one of them active, blending there over
/// some seconds, and shake whichever is shown. Camera always stays the gameplay camera for scripts: at the start of every
/// frame the view goes back to the gameplay pose (<see cref="BeginFrame"/>), scripts move it as before, and after
/// LateUpdate (<see cref="EndFrame"/>) the shown pose (active camera, blend, shake) is put on the view for drawing. Blends
/// and shakes run in real (unscaled) time, so a paused game can still move its cameras. Without a named camera, a blend
/// or a shake the view is never touched.
/// </summary>
public sealed class CameraDirector
{
    public const int MaxCameras = 64;
    public const int MaxFeeds = 4;
    public const int MaxFeedSize = 1024;

    private readonly Dictionary<int, ScriptCameraRig> _cameras = [];
    private readonly Dictionary<string, ScriptCameraRig> _byName = new(StringComparer.Ordinal);
    private int _nextId;

    private CameraPose _blendFrom;
    private double _blendDuration;
    private double _blendElapsed;
    private bool _blendLinear;
    private bool _blending;

    private double _shakeIntensity;
    private double _shakeDuration;
    private double _shakeElapsed;
    private double _shakeFrequency;
    private double _shakeTime;

    private CameraPose? _savedMain;
    private CameraPose? _lastShown;

    /// <summary>The active named camera; null: the gameplay camera.</summary>
    public ScriptCameraRig? Active { get; private set; }

    public IReadOnlyCollection<ScriptCameraRig> Cameras => _cameras.Values;

    /// <summary>Whether the view showed something other than the gameplay camera last frame (diagnostics).</summary>
    public bool Overriding => _savedMain is not null;

    public bool IsBlending => _blending;

    public bool IsShaking => _shakeElapsed < _shakeDuration;

    public ScriptCameraRig? Find(int id) => _cameras.TryGetValue(id, out var rig) ? rig : null;

    public ScriptCameraRig? Find(string name) => _byName.TryGetValue(name, out var rig) ? rig : null;

    public string? Create(string name, CameraPose pose, out ScriptCameraRig? rig)
    {
        rig = null;
        if (name.Length is 0 or > 64)
        {
            return "Cameras.Create expects a name of 1 to 64 characters";
        }

        if (_byName.ContainsKey(name))
        {
            return $"there is already a camera called \"{name}\" (Cameras.Find(\"{name}\") gives it)";
        }

        if (_cameras.Count >= MaxCameras)
        {
            return $"there can be at most {MaxCameras} cameras at once: destroy the ones no longer needed";
        }

        rig = new ScriptCameraRig(++_nextId, name, Sanitize(pose));
        _cameras[rig.Id] = rig;
        _byName[name] = rig;
        return null;
    }

    public void Destroy(ScriptCameraRig rig)
    {
        if (rig.Destroyed)
        {
            return;
        }

        if (ReferenceEquals(Active, rig))
        {
            SetActive(null, 0d, linear: false);
        }

        rig.Destroyed = true;
        rig.HasFeed = false;
        _cameras.Remove(rig.Id);
        _byName.Remove(rig.Name);
    }

    /// <summary>Shows this camera (null: the gameplay camera), blending from what is shown now over <paramref name="blendSeconds"/>.</summary>
    public void SetActive(ScriptCameraRig? rig, double blendSeconds, bool linear)
    {
        if (ReferenceEquals(rig, Active))
        {
            return;
        }

        if (blendSeconds > 0d && _lastShown is { } shown)
        {
            _blendFrom = shown;
            _blendDuration = blendSeconds;
            _blendElapsed = 0d;
            _blendLinear = linear;
            _blending = true;
        }
        else
        {
            _blending = false;
        }

        Active = rig;
    }

    public void Shake(double intensity, double duration, double frequency)
    {
        _shakeIntensity = intensity;
        _shakeDuration = duration;
        _shakeElapsed = 0d;
        _shakeFrequency = frequency;
    }

    public string? RequestFeed(ScriptCameraRig rig, int width, int height)
    {
        if (width < 16 || height < 16 || width > MaxFeedSize || height > MaxFeedSize)
        {
            return $"a camera feed is 16 to {MaxFeedSize} pixels wide and high";
        }

        if (!rig.HasFeed && _cameras.Values.Count(camera => camera.HasFeed) >= MaxFeeds)
        {
            return $"at most {MaxFeeds} cameras can feed images at once";
        }

        rig.HasFeed = true;
        rig.FeedWidth = width;
        rig.FeedHeight = height;
        return null;
    }

    /// <summary>The start of a frame: the view goes back to the gameplay camera, as scripts left it.</summary>
    internal void BeginFrame(IScriptCameraHost? view)
    {
        if (_savedMain is { } main && view is not null)
        {
            Apply(view, main);
        }

        _savedMain = null;
    }

    /// <summary>
    /// The end of a frame (after LateUpdate): following cameras move, then the shown pose goes on the view.
    /// <paramref name="poseOf"/> gives an object's position and rotation (null: it no longer exists).
    /// </summary>
    internal void EndFrame(double realSeconds, IScriptCameraHost? view, Func<Part, (Vector3 Position, System.Numerics.Quaternion Rotation)?> poseOf)
    {
        foreach (var rig in _cameras.Values)
        {
            if (rig.FollowTarget is { } target)
            {
                Follow(rig, target, realSeconds, poseOf);
            }
        }

        if (view is null)
        {
            return;
        }

        var main = CameraPose.Of(view);
        var shown = Active?.Pose ?? main;
        if (_blending)
        {
            _blendElapsed += realSeconds;
            var t = _blendDuration <= 0d ? 1d : global::System.Math.Clamp(_blendElapsed / _blendDuration, 0d, 1d);
            var eased = (float)(_blendLinear ? t : t * t * (3d - 2d * t));
            shown = Lerp(_blendFrom, shown, eased);
            if (t >= 1d)
            {
                _blending = false;
            }
        }

        if (_shakeElapsed < _shakeDuration)
        {
            _shakeElapsed += realSeconds;
            _shakeTime += realSeconds;
            var fade = 1d - global::System.Math.Clamp(_shakeElapsed / _shakeDuration, 0d, 1d);
            var amount = (float)(_shakeIntensity * fade);
            var phase = _shakeTime * _shakeFrequency * global::System.Math.Tau;
            var offset = new Vector3(Wave(phase, 0.0), Wave(phase, 1.7), Wave(phase, 3.1)) * amount;
            shown = shown with
            {
                Position = shown.Position + offset,
                Pitch = global::System.Math.Clamp(shown.Pitch + Wave(phase, 4.3) * amount * 2f, -89f, 89f),
                Yaw = shown.Yaw + Wave(phase, 5.9) * amount * 2f
            };
        }

        _lastShown = shown;
        if (shown != main)
        {
            _savedMain = main;
            Apply(view, shown);
        }
    }

    /// <summary>The pose the view showed last frame (the gameplay camera's when nothing overrides it).</summary>
    public CameraPose? LastShown => _lastShown;

    internal void Clear(IScriptCameraHost? view)
    {
        BeginFrame(view);
        foreach (var rig in _cameras.Values)
        {
            rig.Destroyed = true;
            rig.HasFeed = false;
        }

        _cameras.Clear();
        _byName.Clear();
        Active = null;
        _blending = false;
        _shakeDuration = 0d;
    }

    private static void Follow(ScriptCameraRig rig, Part target, double seconds, Func<Part, (Vector3 Position, System.Numerics.Quaternion Rotation)?> poseOf)
    {
        if (poseOf(target) is not { } pose)
        {
            rig.FollowTarget = null;
            return;
        }

        var offset = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(rig.FollowOffset.X, rig.FollowOffset.Y, rig.FollowOffset.Z), pose.Rotation);
        var desired = pose.Position + new Vector3(offset.X, offset.Y, offset.Z);
        var position = rig.FollowSmoothing > 0f && seconds > 0d
            ? Lerp(rig.Pose.Position, desired, 1f - MathF.Exp(-rig.FollowSmoothing * (float)seconds))
            : desired;
        var (pitch, yaw) = rig.FollowLookAt
            ? CameraPose.LookRotation(position, pose.Position + rig.LookOffset, rig.Pose.Pitch, rig.Pose.Yaw)
            : (rig.Pose.Pitch, rig.Pose.Yaw);
        rig.Pose = rig.Pose with { Position = position, Pitch = pitch, Yaw = yaw };
    }

    private static void Apply(IScriptCameraHost view, CameraPose pose)
    {
        view.Position = pose.Position;
        view.SetRotation(pose.Pitch, pose.Yaw);
        view.FieldOfView = pose.FieldOfView;
    }

    internal static CameraPose Sanitize(CameraPose pose) =>
        new(pose.Position, global::System.Math.Clamp(pose.Pitch, -89f, 89f), pose.Yaw, global::System.Math.Clamp(pose.FieldOfView, 10f, 120f));

    private static CameraPose Lerp(CameraPose a, CameraPose b, float t)
    {
        // Yaw the short way round.
        var dy = ((b.Yaw - a.Yaw) % 360f + 540f) % 360f - 180f;
        return new CameraPose(Lerp(a.Position, b.Position, t), a.Pitch + (b.Pitch - a.Pitch) * t, a.Yaw + dy * t, a.FieldOfView + (b.FieldOfView - a.FieldOfView) * t);
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    // A smooth wobble in -1..1: two sines of unrelated frequencies.
    private static float Wave(double phase, double offset) =>
        (float)(0.6 * global::System.Math.Sin(phase + offset) + 0.4 * global::System.Math.Sin(phase * 2.37 + offset * 1.9));
}
