using Wisko.Engine.Chat;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;

namespace Wisko.Engine.Gameplay;

/// <summary>
/// The gameplay services of one Play session: the simulation's time scale, the game clock, debug lines,
/// particles, named cameras, game saves and the text chat (navigation lives with the scene's scripts, which own the physics it reads).
/// Made when Play starts and thrown away at Stop, so none of it outlives the session; only <see cref="Saves"/> touches
/// files, inside the project's Saves folder.
/// <para>Frame order: <see cref="BeginFrame"/> before any script runs (clock, expired debug lines, the view back to the
/// gameplay camera), <see cref="EndFrame"/> after LateUpdate (particles, then the shown camera).</para>
/// </summary>
public sealed class GameplaySession : IDisposable
{
    public const double MaxTimeScale = 100d;

    private readonly System.Diagnostics.Stopwatch _realClock = System.Diagnostics.Stopwatch.StartNew();
    private bool _disposed;

    public GameplaySession(string? projectRoot = null)
    {
        Saves = new SaveStore(projectRoot);
        Chat = new ChatService(clock: () => RealTime);
    }

    /// <summary>How fast the simulation runs: 1 normal, 0.5 half speed, 0 paused (scripts still run, with dt = 0).</summary>
    public double TimeScale { get; private set; } = 1d;

    /// <summary>Seconds of real time since the session began (high resolution, never scaled).</summary>
    public double RealTime => _realClock.Elapsed.TotalSeconds;

    public GameClock Clock { get; } = new();

    public DebugDraw Debug { get; } = new();

    public ParticleSystem Particles { get; } = new();

    public CameraDirector Cameras { get; } = new();

    public SaveStore Saves { get; }

    /// <summary>The session's text chat (local only: Wisko has no networking); everything in it is forgotten at Stop.</summary>
    public ChatService Chat { get; }

    /// <summary>The renderer can draw camera feeds (render to texture). Off on backends without it, and headless.</summary>
    public bool FeedsSupported { get; set; }

    public string? SetTimeScale(double scale)
    {
        if (!double.IsFinite(scale) || scale < 0d || scale > MaxTimeScale)
        {
            return $"Time.SetTimeScale expects a number from 0 to {MaxTimeScale:0} (1: normal speed, 0: paused)";
        }

        TimeScale = scale;
        return null;
    }

    /// <summary>Real (unscaled) frame time summed over the session's frames: what debug lines' lifetimes count in.</summary>
    public double FrameTime { get; private set; }

    /// <summary>
    /// The start of a frame (before any script): <paramref name="seconds"/> of simulation time and
    /// <paramref name="realSeconds"/> of real time have passed.
    /// </summary>
    public void BeginFrame(double seconds, double realSeconds, IScriptCameraHost? view)
    {
        if (_disposed)
        {
            return;
        }

        FrameTime += double.IsFinite(realSeconds) && realSeconds > 0d ? realSeconds : 0d;
        Clock.Advance(seconds);
        Debug.Expire(FrameTime);
        Cameras.BeginFrame(view);
    }

    /// <summary>
    /// The end of a frame (after LateUpdate): particles move by <paramref name="seconds"/> of simulation time, cameras
    /// follow and blend by <paramref name="realSeconds"/>, and the shown camera goes on the view.
    /// </summary>
    public void EndFrame(double seconds, double realSeconds, IScriptCameraHost? view, Vector3 gravity,
        Func<Part, (Vector3 Position, System.Numerics.Quaternion Rotation)?> poseOf)
    {
        if (_disposed)
        {
            return;
        }

        Particles.Step((float)seconds, gravity, view?.Position ?? Vector3.Zero);
        Cameras.EndFrame(realSeconds, view, poseOf);
    }

    /// <summary>The session ends: the view gets the gameplay camera back, everything made is let go.</summary>
    public void End(IScriptCameraHost? view)
    {
        if (_disposed)
        {
            return;
        }

        Cameras.Clear(view);
        Particles.Clear();
        Debug.Clear();
        Chat.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Particles.Clear();
        Debug.Clear();
        Chat.Dispose();
        _disposed = true;
    }
}
