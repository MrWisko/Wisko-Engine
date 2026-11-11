using System.Diagnostics;
using Wisko.Engine.Animation;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Gui;
using Wisko.Engine.Physics;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

/// <summary>Whether the editor is editing the scene or running its scripts.</summary>
public enum EditorMode
{
    Edit,
    Playing
}

/// <summary>What Stop restored, so the editor can refresh what depends on it.</summary>
/// <param name="MembershipChanged">The workspace's object list had to be rebuilt (renderer must re-sync).</param>
/// <param name="MeshChanged">Objects whose colour or shape differed from the snapshot (their meshes need rebuilding).</param>
/// <param name="Selection">The selection from before Play.</param>
public readonly record struct PlayStopResult(bool MembershipChanged, IReadOnlyList<Part> MeshChanged, IReadOnlyList<Part> Selection);

/// <summary>A script failure seen during Play (missing file, load, Start or Update failure), reported once.</summary>
public readonly record struct PlayScriptFault(SceneScript Script, ScriptDiagnostic Diagnostic);

/// <summary>
/// Play Mode: the single owner of the editor's <see cref="EditorMode"/>. Play takes an in-memory snapshot of the
/// edit scene (object order plus every object's full <see cref="PartSnapshot"/>, scripts included) and of the
/// selection, builds the session's <see cref="PhysicsWorld"/> from the scene (and the host's environment colliders)
/// and its <see cref="GuiRuntime"/> (a copy of the project's GUI), then runs the scene's scripts through a fresh
/// <see cref="SceneScriptRuntime"/> (they reach that GUI copy through UI): Start once, then every frame Update(dt)
/// followed by physics in fixed steps. Scripts may add Parts to the workspace (Scene.Create) and take Parts out
/// (Destroy); the host hears of each through <see cref="TakeMembershipChanges"/>. Stop
/// discards the physics world, the GUI copy and the runtime and writes the
/// snapshot back onto the same objects (created Parts are dropped, destroyed ones come back), so the edit scene is
/// exactly what it was (no drift across any number of Play/Stop cycles). Nothing here touches files or the undo
/// history; Stop cannot be refused by a script.
/// </summary>
public sealed class PlayModeController : IDisposable
{
    private const int MaxPendingWarnings = 20;

    private readonly List<(Part Part, PartSnapshot State)> _sceneSnapshot = [];
    private readonly List<Part> _selectionSnapshot = [];
    private readonly HashSet<SceneScript> _reportedFaults = new(ReferenceEqualityComparer.Instance);
    private readonly List<ScriptLogMessage> _pendingWarnings = [];
    private readonly List<(Part Part, bool Added)> _membershipChanges = [];
    private readonly ScriptInputState _startInput = new();
    private SceneScriptRuntime? _runtime;
    private PhysicsWorld? _physics;
    private GuiRuntime? _gui;
    private AnimationRuntime? _animations;
    private global::Wisko.Engine.Gameplay.GameplaySession? _gameplay;
    private bool _firstFrameReported;

    public PlayModeController()
    {
        AtmosphereHost = new ScriptAtmosphereHost(this);
        GraphicsHost = new ScriptGraphicsHost(this);
    }

    public EditorMode Mode { get; private set; } = EditorMode.Edit;

    public bool IsPlaying => Mode == EditorMode.Playing;

    /// <summary>The running scripts while playing; null in Edit mode.</summary>
    public SceneScriptRuntime? Runtime => _runtime;

    /// <summary>Play sessions started so far (diagnostics).</summary>
    public int SessionCount { get; private set; }

    /// <summary>Where script print output, warnings and failures are published (structured), when set.</summary>
    public OutputLog? Output { get; set; }

    /// <summary>The running physics simulation while playing; null in Edit mode.</summary>
    public PhysicsWorld? Physics => _physics;

    /// <summary>Fixed colliders the host draws besides the scene's Parts (e.g. its baseplate), added to every Play session.</summary>
    public PhysicsEnvironment? PhysicsEnvironment { get; set; }

    public PhysicsSettings PhysicsSettings { get; set; } = PhysicsSettings.Default;

    /// <summary>The project's authored GUI; each Play session draws its own copy of it (null: an empty GUI).</summary>
    public GuiDocument? GuiSource { get; set; }

    /// <summary>How the game GUI's text is measured (the renderer's font; UI.MeasureText); null: an estimate.</summary>
    public Func<string, int, GuiTextSize>? GuiTextMeasurer { get; set; }

    /// <summary>Most bytes each Play session's Lua heap may hold (<see cref="EditorScriptPreferences"/>).</summary>
    public long ScriptMemoryLimitBytes { get; set; } = EditorScriptPreferences.DefaultMemoryLimitBytes;

    /// <summary>Benchmarks only: false plays without the Lua memory accounting (to measure its cost).</summary>
    internal bool ScriptMemoryAccounting { get; set; } = true;

    /// <summary>The running game GUI (a copy of <see cref="GuiSource"/> made when Play started); null in Edit mode.</summary>
    public GuiRuntime? Gui => _gui;

    /// <summary>The session's model animation playback; null in Edit mode.</summary>
    public AnimationRuntime? Animations => _animations;

    /// <summary>The session's gameplay services (clock, debug lines, particles, cameras, saves); null in Edit mode.</summary>
    public global::Wisko.Engine.Gameplay.GameplaySession? Gameplay => _gameplay;

    /// <summary>Whether the host can draw camera feeds (render to texture) into GUI images; told to each session.</summary>
    public bool CameraFeedsSupported { get; set; }

    /// <summary>The view camera scripts reach through Camera while playing (the host's; null: no Camera).</summary>
    public IScriptCameraHost? CameraHost { get; set; }

    /// <summary>The project's Atmosphere when Play starts (what scripts' Atmosphere begins from).</summary>
    public AtmosphereSettings AtmosphereSource { get; set; } = AtmosphereSettings.Default;

    /// <summary>The Atmosphere scripts made this session (drawn instead of the project's); null until one changes it and after Stop.</summary>
    public AtmosphereSettings? PlayAtmosphere { get; private set; }

    /// <summary>What scripts' Atmosphere reads and writes (<see cref="PlayAtmosphere"/>, starting from <see cref="AtmosphereSource"/>).</summary>
    public IScriptAtmosphereHost AtmosphereHost { get; }

    /// <summary>The project's rendering settings when Play starts (what scripts' Graphics begins from).</summary>
    public Graphics.GraphicsSettings GraphicsSource { get; set; } = Graphics.GraphicsSettings.LegacyDefault;

    /// <summary>The rendering settings scripts made this session (drawn instead of the project's); null until one changes them and after Stop.</summary>
    public Graphics.GraphicsSettings? PlayGraphics { get; internal set; }

    /// <summary>What scripts' Graphics reads and writes (<see cref="PlayGraphics"/>, starting from <see cref="GraphicsSource"/>).</summary>
    public IScriptGraphicsHost GraphicsHost { get; }

    /// <summary>
    /// Enters Play: snapshots the scene and selection, builds the physics world (the environment, then every Part it
    /// can simulate; a Part it cannot is skipped and logged), loads every attached script from
    /// <paramref name="projectRoot"/> (null: no project folder, so attached scripts are reported missing) and runs
    /// Start. <paramref name="input"/> is what Start sees (held keys and cursor, without this frame's edges). False,
    /// still in Edit with nothing changed, when the physics world or the script runtime could not be created.
    /// </summary>
    public bool TryStart(string? projectRoot, Workspace workspace, IReadOnlyList<Part> selection, ScriptInputState? input, out string? error)
    {
        error = null;
        if (IsPlaying)
        {
            return true;
        }

        // How long Play takes to start, by phase, for the log (Start includes what scripts do first, e.g. a navigation bake).
        var startWatch = Stopwatch.StartNew();
        var physicsMilliseconds = 0d;
        var loadedMilliseconds = 0d;
        _firstFrameReported = false;
        _sceneSnapshot.Clear();
        foreach (var part in workspace.Parts)
        {
            _sceneSnapshot.Add((part, PartSnapshot.FromPart(part)));
        }

        _selectionSnapshot.Clear();
        _selectionSnapshot.AddRange(selection);
        _reportedFaults.Clear();
        _pendingWarnings.Clear();

        // Physics exists before any script runs (Start included).
        try
        {
            _physics = new PhysicsWorld(PhysicsSettings);
            // The host's environment (the baseplate) as this world has it: a deleted baseplate is not collided with.
            if (PhysicsEnvironment is not null)
            {
                _physics.AddEnvironment(workspace.RemovedEnvironment.Count == 0 ? PhysicsEnvironment : PhysicsEnvironment.Without(workspace.RemovedEnvironment));
            }

            var simulated = _physics.AddParts(workspace.Parts);
            Console.WriteLine(
                $"[Physics] {_physics.StaticCount} static, {_physics.KinematicCount} kinematic, {_physics.DynamicCount} dynamic, " +
                $"{_physics.EnvironmentCount} environment ({workspace.Parts.Count - simulated} of {workspace.Parts.Count} objects not simulated)");
            physicsMilliseconds = startWatch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Could not start physics: {ex}");
            error = $"physics could not start ({ex.Message})";
            DisposePhysics();
            _sceneSnapshot.Clear();
            _selectionSnapshot.Clear();
            return false;
        }

        // The game GUI: this session's own copy of the authored GUI (also before any script runs).
        try
        {
            _gui = new GuiRuntime(GuiSource ?? new GuiDocument()) { TextMeasurer = GuiTextMeasurer };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Could not start the GUI: {ex}");
            error = $"the GUI could not start ({ex.Message})";
            DisposePhysics();
            _sceneSnapshot.Clear();
            _selectionSnapshot.Clear();
            return false;
        }

        // Model animation: the saved Play on start clips begin before any script runs (a script can change them from its
        // first line on). Clips that finish are told to the scripts after each frame's advance.
        _animations = new AnimationRuntime { Warning = OnAnimationWarning };
        _animations.StartPlayOnStart(workspace.Parts);

        // The session's gameplay services (game clock, debug lines, particles, cameras; saves in the project's Saves folder).
        _gameplay = new global::Wisko.Engine.Gameplay.GameplaySession(projectRoot) { FeedsSupported = CameraFeedsSupported };

        try
        {
            // The runtime itself tells physics about scripts' transform changes, created and destroyed Parts.
            _membershipChanges.Clear();
            PlayAtmosphere = null;
            PlayGraphics = null;
            _runtime = new SceneScriptRuntime(projectRoot, workspace,
                new LuaScriptRuntimeOptions
                {
                    Log = OnScriptLog, Camera = CameraHost, Atmosphere = AtmosphereHost, Graphics = GraphicsHost, Gameplay = _gameplay,
                    MemoryLimitBytes = ScriptMemoryLimitBytes, MemoryAccounting = ScriptMemoryAccounting
                },
                OnScriptOutput, _gui, _physics, _animations);
            _runtime.PartCreated = part => _membershipChanges.Add((part, true));
            _runtime.PartDestroyed = part => _membershipChanges.Add((part, false));
            var runtime = _runtime;
            _animations.Finished = runtime.NotifyAnimationFinished;

            // Scripts' top-level chunks ran while loading, before anyone listened: what they destroyed or created.
            var present = workspace.Parts.ToHashSet(ReferenceEqualityComparer.Instance);
            var authored = _sceneSnapshot.Select(entry => entry.Part).ToHashSet(ReferenceEqualityComparer.Instance);
            _membershipChanges.AddRange(_sceneSnapshot.Where(entry => !present.Contains(entry.Part)).Select(entry => (entry.Part, false)));
            _membershipChanges.AddRange(workspace.Parts.Where(part => !authored.Contains(part)).Select(part => (part, true)));
            loadedMilliseconds = startWatch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Could not start the script runtime: {ex}");
            error = ex.Message;
            DisposePhysics();
            DisposeGui();
            DisposeAnimations();
            DisposeGameplay();

            // Scripts that did load may already have changed the scene: it goes back as it was.
            RestoreSnapshot(workspace);
            _sceneSnapshot.Clear();
            _selectionSnapshot.Clear();
            _membershipChanges.Clear();
            return false;
        }

        Mode = EditorMode.Playing;
        SessionCount++;
        Console.WriteLine($"[PlayMode] Play #{SessionCount}: {workspace.Parts.Count} objects, {_runtime.Scripts.Count} scripts, {_gui.Document.Count} GUI elements");

        try
        {
            if (input is not null)
            {
                _startInput.CopyFrom(input);
                _startInput.ClearEdges();
                _runtime.SetInput(_startInput);
            }

            _runtime.Start();

            // Start may have placed scripted (kinematic) Parts: their bodies start there, at rest.
            _physics.SnapKinematicsToParts();
            var total = startWatch.Elapsed.TotalMilliseconds;
            Console.WriteLine(FormattableString.Invariant(
                $"[PlayMode] Play #{SessionCount} ready in {total:F0} ms (physics {physicsMilliseconds:F0} ms, scripts loaded {loadedMilliseconds - physicsMilliseconds:F0} ms, Start {total - loadedMilliseconds:F0} ms)"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Script runtime failed during Start: {ex}");
            error = ex.Message;
            Stop(workspace);
            return false;
        }

        return true;
    }

    /// <summary>
    /// One Play frame: publishes the frame's input, then Time and every script's Update(dt) (a script's transform writes
    /// reach physics as they happen), then physics advances in fixed steps and writes the moving Parts back, then
    /// parented objects follow their parents and every script's LateUpdate(dt) runs.
    /// </summary>
    public bool TryUpdate(double deltaTime, ScriptInputState? input, out string? error)
    {
        error = null;
        if (!IsPlaying || _runtime is null)
        {
            return true;
        }

        // Time.SetTimeScale: scripts, physics and animation all run at the session's time scale (0: paused); the
        // real frame time stays what Time.unscaledDeltaTime reads. A new scale counts from the next frame.
        var realDt = double.IsFinite(deltaTime) && deltaTime > 0d ? deltaTime : 0d;
        var dt = realDt * (_gameplay?.TimeScale ?? 1d);
        var frameWatch = _firstFrameReported ? null : Stopwatch.StartNew();
        try
        {
            _runtime.Update(dt, realDt, input);
        }
        catch (Exception ex)
        {
            // Script failures never get here (they fault their script); this is the runtime itself failing.
            Console.WriteLine($"[PlayMode] Script runtime failed during Update: {ex}");
            error = ex.Message;
            return false;
        }

        try
        {
            _physics?.Advance(dt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Physics failed during Update: {ex}");
            error = $"physics failed ({ex.Message})";
            return false;
        }

        // After physics: animations advance (frame-rate independent: by this frame's time), then parented objects follow
        // and LateUpdate runs (e.g. cameras following what physics just moved).
        try
        {
            _animations?.Advance(dt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Animation failed during Update: {ex}");
            error = $"animation failed ({ex.Message})";
            return false;
        }

        try
        {
            _runtime.LateUpdate();
            if (frameWatch is not null)
            {
                // The first Play frame pays for code that runs for the first time (scripts' Update, physics steps).
                _firstFrameReported = true;
                Console.WriteLine(FormattableString.Invariant($"[PlayMode] First Play frame: {frameWatch.Elapsed.TotalMilliseconds:F1} ms"));
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Script runtime failed during LateUpdate: {ex}");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Script failures that have not been reported yet, in script order.</summary>
    public IReadOnlyList<PlayScriptFault> TakeNewFaults()
    {
        if (_runtime is null)
        {
            return [];
        }

        List<PlayScriptFault>? faults = null;
        foreach (var script in _runtime.Scripts)
        {
            if (script.Fault is { } fault && _reportedFaults.Add(script))
            {
                (faults ??= []).Add(new PlayScriptFault(script, fault));
            }
        }

        return faults ?? (IReadOnlyList<PlayScriptFault>)[];
    }

    /// <summary>
    /// Parts scripts added to the workspace (Added) or took out of it since the last call, in order (a Part created and
    /// destroyed in between appears twice). Stop needs none of these: it rebuilds the object list when it changed.
    /// </summary>
    public IReadOnlyList<(Part Part, bool Added)> TakeMembershipChanges()
    {
        if (_membershipChanges.Count == 0)
        {
            return [];
        }

        var changes = _membershipChanges.ToArray();
        _membershipChanges.Clear();
        return changes;
    }

    /// <summary>Script warnings (e.g. unknown Input key names) logged since the last call.</summary>
    public IReadOnlyList<ScriptLogMessage> TakeWarnings()
    {
        if (_pendingWarnings.Count == 0)
        {
            return [];
        }

        var warnings = _pendingWarnings.ToArray();
        _pendingWarnings.Clear();
        return warnings;
    }

    /// <summary>The state of each script attached to <paramref name="part"/> in this Play session (empty in Edit mode).</summary>
    public IEnumerable<SceneScript> ScriptsOf(Part part) =>
        _runtime?.Scripts.Where(script => ReferenceEquals(script.Object, part)) ?? [];

    /// <summary>
    /// Leaves Play: discards the script runtime, restores the snapshot (object order and every object's state, on the
    /// same objects) and returns to Edit. Always succeeds; does nothing in Edit mode.
    /// </summary>
    public PlayStopResult Stop(Workspace workspace)
    {
        if (!IsPlaying)
        {
            return new PlayStopResult(false, [], []);
        }

        DisposePhysics();
        DisposeGui();
        DisposeAnimations(); // every model shows its rest pose again
        var runtime = _runtime;
        _runtime = null;
        try
        {
            runtime?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Script runtime did not shut down cleanly: {ex.Message}");
        }

        DisposeGameplay(); // cameras, particles, debug lines and the clock go; saves stay on disk
        var (membershipChanged, meshChanged) = RestoreSnapshot(workspace);
        PlayAtmosphere = null; // the project's Atmosphere is drawn again
        PlayGraphics = null; // and the project's rendering settings
        var selection = _selectionSnapshot.ToArray();
        _sceneSnapshot.Clear();
        _selectionSnapshot.Clear();
        _reportedFaults.Clear();
        _pendingWarnings.Clear();
        _membershipChanges.Clear();
        Mode = EditorMode.Edit;
        Console.WriteLine($"[PlayMode] Stopped: scene restored ({workspace.Parts.Count} objects{(membershipChanged ? ", object list rebuilt" : string.Empty)})");
        return new PlayStopResult(membershipChanged, meshChanged, selection);
    }

    /// <summary>Discards a running session without restoring anything (application shutdown).</summary>
    public void Dispose()
    {
        DisposePhysics();
        DisposeGui();
        DisposeAnimations();
        try
        {
            _runtime?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Script runtime did not shut down cleanly: {ex.Message}");
        }

        _runtime = null;
        DisposeGameplay();
        _sceneSnapshot.Clear();
        _selectionSnapshot.Clear();
        _membershipChanges.Clear();
        Mode = EditorMode.Edit;
    }

    // The session's gameplay services end: the view gets the gameplay camera back, emitters, cameras and lines are let go.
    private void DisposeGameplay()
    {
        var gameplay = _gameplay;
        _gameplay = null;
        if (gameplay is null)
        {
            return;
        }

        try
        {
            gameplay.End(CameraHost);
            gameplay.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Gameplay services did not shut down cleanly: {ex.Message}");
        }
    }

    // The session's GUI copy is dropped (the authored GUI was never touched).
    private void DisposeGui()
    {
        _gui?.Dispose();
        _gui = null;
    }

    // Every animator is detached from its Part: playback was never part of the scene.
    private void DisposeAnimations()
    {
        _animations?.Dispose();
        _animations = null;
    }

    // A Play on start setting that cannot be followed: the Output panel shows it as a warning (no script is at fault).
    private void OnAnimationWarning(string message)
    {
        Console.WriteLine($"[Animation] {message}");
        Output?.Add(new OutputEntry(OutputKind.Warning, $"[Animation] {message}", null, null, null));
    }

    private void DisposePhysics()
    {
        var physics = _physics;
        _physics = null;
        try
        {
            physics?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlayMode] Physics did not shut down cleanly: {ex.Message}");
        }
    }

    private void OnScriptOutput(ScriptOutputEvent output)
    {
        var kind = output.Kind switch
        {
            ScriptLogKind.Error => OutputKind.Error,
            ScriptLogKind.Warning => OutputKind.Warning,
            _ => OutputKind.Info
        };
        Output?.Add(new OutputEntry(kind, output.Text, output.Script.AssetPath, output.Line, output.ObjectName));
    }

    // Every message still goes to the log; warnings are also kept for the editor to show.
    private void OnScriptLog(ScriptLogMessage message)
    {
        Console.WriteLine(message.ToString());
        if (message.Kind == ScriptLogKind.Warning && _pendingWarnings.Count < MaxPendingWarnings)
        {
            _pendingWarnings.Add(message);
        }
    }

    // The snapshot back onto the workspace: its object list (rebuilt only when it differs) and every object's state.
    private (bool MembershipChanged, List<Part> MeshChanged) RestoreSnapshot(Workspace workspace)
    {
        var membershipChanged = !HasSnapshotParts(workspace);
        if (membershipChanged)
        {
            workspace.Clear();
            foreach (var (part, _) in _sceneSnapshot)
            {
                workspace.AddPart(part);
            }
        }

        var meshChanged = new List<Part>();
        foreach (var (part, state) in _sceneSnapshot)
        {
            if (part.ShapeType != state.ShapeType || !SameValue(part.Color, state.Color))
            {
                meshChanged.Add(part);
            }

            state.ApplyAll(part);

            // Visibility is Play-only (never in a snapshot): every Part shows again.
            part.Visible = true;
        }

        return (membershipChanged, meshChanged);
    }

    private bool HasSnapshotParts(Workspace workspace)
    {
        if (workspace.Parts.Count != _sceneSnapshot.Count)
        {
            return false;
        }

        for (var i = 0; i < _sceneSnapshot.Count; i++)
        {
            if (!ReferenceEquals(workspace.Parts[i], _sceneSnapshot[i].Part))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameValue(Engine.Math.Vector3 a, Engine.Math.Vector3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    // Scripts' Atmosphere: the session's own settings, from the project's at Play (always sanitized; Stop drops them).
    private sealed class ScriptAtmosphereHost(PlayModeController owner) : IScriptAtmosphereHost
    {
        public AtmosphereSettings Settings
        {
            get => owner.PlayAtmosphere ?? owner.AtmosphereSource;
            set => owner.PlayAtmosphere = value.Sanitized();
        }
    }

    // Scripts' Graphics: the session's own rendering settings, from the project's at Play (always sanitized; Stop drops them).
    private sealed class ScriptGraphicsHost(PlayModeController owner) : IScriptGraphicsHost
    {
        public Graphics.GraphicsSettings Settings
        {
            get => owner.PlayGraphics ?? owner.GraphicsSource;
            set => owner.PlayGraphics = value.Sanitized();
        }
    }
}
