using System.Globalization;
using System.Text;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using KeraLua;
using LuaState = KeraLua.Lua;

namespace Wisko.Engine.Scripting;

/// <summary>A scene object bound to one or more scripts as their self: its object Id and how many scripts use it.</summary>
internal sealed class ObjectBinding(IScriptObject target, int id)
{
    public IScriptObject Target { get; } = target;

    public int Id { get; } = id;

    public int Users { get; set; }
}

public sealed class LuaScriptRuntimeOptions
{
    /// <summary>
    /// Lua VM instructions one callback (top-level chunk, Start or one Update) may execute before it is stopped.
    /// Generous: normal per-frame work is orders of magnitude below it; a runaway loop hits it in well under a second.
    /// </summary>
    public long InstructionBudget { get; init; } = 50_000_000;

    /// <summary>
    /// Most bytes the session's Lua heap may hold (every script together; see <see cref="LuaMemoryLimit"/>). A script
    /// whose allocation would go past it is stopped like one that exceeds the instruction budget. Null: no limit (tests
    /// and benchmarks only; the editor always sets one, <see cref="EditorScriptPreferences"/>).
    /// </summary>
    public long? MemoryLimitBytes { get; init; } = EditorScriptPreferences.DefaultMemoryLimitBytes;

    /// <summary>Benchmarks only: false runs without the memory accounting at all (nothing counted or limited).</summary>
    internal bool MemoryAccounting { get; init; } = true;

    /// <summary>Longest single print() message, in bytes; longer output is cut and marked as truncated.</summary>
    public int MaxPrintLength { get; init; } = 4096;

    /// <summary>Where print() output and failures go. Defaults to the console, which the application log mirrors.</summary>
    public Action<ScriptLogMessage>? Log { get; init; }

    /// <summary>The Play session's GUI copy that scripts reach through UI (null: UI finds nothing).</summary>
    public GuiRuntime? Gui { get; init; }

    /// <summary>
    /// The folder require() finds modules in (the project's Assets/Scripts; "." in package.path), and object:AttachScript
    /// scripts. Null: only package.preload modules can be required, and no script can be attached.
    /// </summary>
    public string? ModuleRoot { get; init; }

    /// <summary>The Play camera scripts reach through Camera (null: Camera is not available).</summary>
    public IScriptCameraHost? Camera { get; init; }

    /// <summary>The Play session's Atmosphere scripts reach through Atmosphere (null: Atmosphere is not available).</summary>
    public IScriptAtmosphereHost? Atmosphere { get; init; }

    /// <summary>The Play session's rendering settings scripts reach through Graphics (null: not available).</summary>
    public IScriptGraphicsHost? Graphics { get; init; }

    /// <summary>What Time.fixedDeltaTime reads: the physics step in seconds.</summary>
    public double FixedDeltaTime { get; init; } = 1d / 60d;

    /// <summary>
    /// The Play session's gameplay services (game clock, debug lines, particles, cameras, saves) scripts reach through
    /// GameTime, DebugDraw, Particles, Cameras and GameSave (null: those answer "only while the game plays").
    /// </summary>
    public global::Wisko.Engine.Gameplay.GameplaySession? Gameplay { get; init; }
}

/// <summary>
/// One Timer.After / Timer.Every: its number (handles hold only this), the script that owns it, its function (a registry
/// reference), when it is due in game time, its interval (0 for After) and the frame it was made in (it never runs in
/// that frame).
/// </summary>
internal sealed class ScriptTimer(int id, LuaScriptInstance owner, int functionReference, double due, double interval, long frame)
{
    public int Id { get; } = id;

    public LuaScriptInstance Owner { get; } = owner;

    public int FunctionReference { get; } = functionReference;

    public double Due { get; set; } = due;

    public double Interval { get; } = interval;

    /// <summary>After's delay (what Restart waits again); Every's interval.</summary>
    public double Delay { get; init; } = interval;

    /// <summary>The frame it was made (or restarted) in: it never runs in that frame.</summary>
    public long Frame { get; set; } = frame;

    public bool Active { get; set; } = true;

    /// <summary>Paused: it does not run, and keeps <see cref="PausedRemaining"/> seconds until it is resumed.</summary>
    public bool Paused { get; set; }

    public double PausedRemaining { get; set; }
}

/// <summary>
/// One GUI callback (OnClick, OnPressed, OnReleased, OnHoverEnter, OnHoverExit): its connection number, the button, the
/// event, the script that made it and its function (a registry reference).
/// </summary>
internal sealed class ClickHandler(int id, int buttonId, GuiButtonEvent kind, LuaScriptInstance owner, int functionReference)
{
    public int Id { get; } = id;

    public int ButtonId { get; } = buttonId;

    public GuiButtonEvent Kind { get; } = kind;

    public LuaScriptInstance Owner { get; } = owner;

    public int FunctionReference { get; } = functionReference;

    public bool Active { get; set; } = true;
}

/// <summary>One Events.On connection: its number, the event name, the script that made it and its function.</summary>
internal sealed class EventHandlerEntry(int id, string name, LuaScriptInstance owner, int functionReference)
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    public LuaScriptInstance Owner { get; } = owner;

    public int FunctionReference { get; } = functionReference;

    public bool Active { get; set; } = true;

    /// <summary>The engine's own connection (object:OnDestroyed, OnAttributeChanged), never matched by an Events.Emit of the same name.</summary>
    public bool Internal { get; init; }

    /// <summary>Fires once, then goes (OnDestroyed: its object is gone for good).</summary>
    public bool Once { get; init; }
}

/// <summary>
/// A scene object or GUI element passed to Events.Emit: delivered to each receiving script as its own wrapper of the same
/// object (by Id), never as the sender's table.
/// </summary>
internal readonly record struct EventObject(int Id);

internal readonly record struct EventElement(int Id, string Type, string Name);

/// <summary>
/// A table passed to Events.Emit: the sandbox's private plain-data snapshot of it (a registry reference), copied again for
/// every receiver, so no two scripts ever hold the same table. Let go once the event has been delivered.
/// </summary>
internal readonly record struct EventTable(int Reference);

/// <summary>
/// One Events.Emit waiting for delivery: its name, its arguments (nil, bool, number, string, object, element, table) and
/// their kinds as deliverEvent reads them (one letter each: 'v', 'o', 'g' or 't'). Internal: the engine's own event
/// (an object's OnDestroyed), delivered only to internal connections.
/// </summary>
internal sealed record QueuedEvent(string Name, object?[] Arguments, string Tags, bool Internal = false);

/// <summary>
/// Runs Lua scripts (standard Lua 5.4: NLua 1.7.9 / KeraLua 1.4.9 / PUC-Rio Lua 5.4.8) in a restricted environment.
/// <list type="bullet">
/// <item>One Lua state per runtime; every script gets its own environment table built from an allow-list
/// (<see cref="LuaSandbox"/>), so scripts do not share writable globals and never see the host globals, NLua's CLR
/// bridge, file/OS access or native modules. require() reads pure-Lua modules from <see cref="LuaScriptRuntimeOptions.ModuleRoot"/>
/// only (<see cref="HostModule"/>).</item>
/// <item>Script code only ever runs inside lua_resume on one reusable runner coroutine, or on coroutines the script
/// makes. Managed callbacks (print, the host functions, the instruction hook) never raise Lua errors, so a Lua error or
/// yield never unwinds a .NET frame.</item>
/// <item>Each callback has an instruction budget, enforced by a host-side count hook on every thread that runs script
/// code: it yields the runner when the budget is spent, and where the script cannot be paused (one of its coroutines,
/// or Lua code called from inside a C function such as table.sort) it makes every further instruction raise an error
/// (Lua's own debug hook, see LuaSandbox's stopThread), so a callback always ends. The debug library is never exposed to
/// scripts (only debug.traceback).</item>
/// <item>Objects (<see cref="IScriptObject"/>) have Ids in this runtime: the scene's Parts in scene order (what
/// Scene finds), and any other object a script is bound to. A bound script sees its object as self. Scripts only ever
/// hold an Id inside proxy tables: every read and write asks the host, which looks the Id up again and works on the
/// object itself, so all scripts (and self and Scene's results) see one state. After every callback the host hears
/// about it (<see cref="CallbackCompleted"/>) and passes on what the callback changed (e.g. to physics).</item>
/// <item>UI reaches the GUI given in <see cref="LuaScriptRuntimeOptions.Gui"/> (Play's copy) through one host function
/// that, like print's, never raises a Lua error: it answers with values and the sandbox's Lua wrappers raise. OnClick
/// callbacks belong to the script that registered them, run as that script (self, budget, failure rules) when
/// <see cref="DispatchClick"/> is called for their button, and are dropped when that script faults or is released.</item>
/// <item>Timers (Timer.After / Every) belong to the script that made them and run as it, in game time, when the host
/// calls <see cref="RunTimers"/> once per frame: never in the frame they were made in, an Every at most once per frame,
/// due ones in order of due time then creation. A failing timer callback faults its script like a failing Update; a
/// script's timers end with it (fault, release, its object destroyed, runtime disposed).</item>
/// <item>Button events (OnClick, OnPressed, OnReleased, OnHoverEnter, OnHoverExit) and Events.On handlers belong to the
/// script that connected them and run as it when the host dispatches them (<see cref="DispatchGuiEvent"/>,
/// <see cref="DispatchEvents"/>, once per frame each); they go with their script. Camera and Atmosphere reach the hosts
/// in <see cref="LuaScriptRuntimeOptions"/> (none: refused).</item>
/// <item>A script's event functions (OnCollision* / OnTrigger*(other, contact), OnDestroy()) run when the host calls them
/// (<see cref="CallHandler(LuaScriptInstance, ScriptEventHandler, int, ScriptContact?)"/>, <see cref="RunDestroy"/>);
/// other is the script's own wrapper of that object. Scene.Create,
/// object:Destroy() and Physics.Raycast go to the <see cref="SceneHost"/>; without one they are refused (Raycast finds
/// nothing). A destroyed object's scripts stop at once (<see cref="BeginDestroy"/>).</item>
/// <item>Not thread-safe: use from one thread (the editor's main thread).</item>
/// </list>
/// </summary>
public sealed partial class LuaScriptRuntime : IDisposable
{
    // The count hook fires every this many VM instructions.
    private const int HookInterval = 1000;

    // collectgarbage("collect") / ("step") from a script: work charged to its instruction budget (a full collection
    // costs far more than one instruction, so a loop of them still ends).
    private const long FullCollectionCost = 1_000_000;
    private const long CollectionStepCost = 100_000;

    // Largest module file require() reads.
    private const long MaxModuleBytes = 16L * 1024 * 1024;
    private const int MaxDiagnosticMessageLength = 400;

    /// <summary>Timers one script may have running at once (a runaway script cannot grow them without bound).</summary>
    public const int MaxTimersPerScript = 10_000;

    /// <summary>Events.On connections one script may have at once, and GUI callbacks.</summary>
    public const int MaxHandlersPerScript = 1_000;

    /// <summary>Events.Emit calls waiting for delivery at once (a script that emits more in one frame is refused).</summary>
    public const int MaxQueuedEvents = 10_000;

    /// <summary>Most values one Events.Emit can carry; longest event name.</summary>
    public const int MaxEventArguments = 16;

    public const int MaxEventNameLength = 64;

    /// <summary>Most results RaycastAll, OverlapSphere, OverlapBox and Scene's searches by type or tag give in one call.</summary>
    public const int MaxQueryResults = 1_000;

    /// <summary>Largest runtime GUI: UI.Create refuses to make the session's GUI bigger than this many elements.</summary>
    public const int MaxGuiElements = 10_000;

    // Game time is a sum of frame deltas: a timer this close to due runs now rather than a frame late.
    private const double TimerTolerance = 1e-9;

    private readonly LuaScriptRuntimeOptions _options;
    private readonly NLua.Lua _lua;
    private readonly LuaState _main;
    private readonly LuaState _runner;
    private readonly int _newEnvironmentReference;
    private readonly int _timeValuesReference;
    private readonly int _inputValuesReference;
    private readonly int _stringMetatableReference;
    private readonly int _stopThreadReference;

    // Native Lua holds function pointers to these; the fields keep the delegates alive.
    private readonly LuaFunction _hostWrite;
    private readonly LuaFunction _hostGui;
    private readonly LuaFunction _hostScene;
    private readonly LuaFunction _hostTimer;
    private readonly LuaFunction _hostPhysics;
    private readonly LuaFunction _hostModule;
    private readonly LuaFunction _hostGc;
    private readonly LuaFunction _hostEvents;
    private readonly LuaFunction _hostView;
    private readonly LuaFunction _hostGameplay;
    private readonly LuaHookFunction _instructionHook;
    private readonly LuaMemoryLimit _memory;

    // Module files read this session, by full path (read once; a new Play session reads them again).
    private readonly Dictionary<string, byte[]> _moduleSources = new(StringComparer.Ordinal);

    private readonly GuiScriptBridge _gui;
    private readonly List<ClickHandler> _clickHandlers = [];
    private readonly List<EventHandlerEntry> _eventHandlers = [];
    private readonly List<QueuedEvent> _eventQueue = [];
    private readonly List<LuaScriptInstance> _instances = [];
    private readonly Dictionary<IScriptObject, ObjectBinding> _bindings = new(ReferenceEqualityComparer.Instance);

    // Object Id - 1 -> object (a removed one leaves a stand-in: Ids are never reused); Scene finds the scene objects
    // only, in the order they were added (Ids ascending).
    private readonly List<IScriptObject> _objects = [];
    private readonly Dictionary<IScriptObject, int> _objectIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _sceneObjects = [];

    // Timers by number, and in creation order (finished ones are dropped once per RunTimers); a reused buffer of due ones.
    private readonly Dictionary<int, ScriptTimer> _timers = [];
    private readonly List<ScriptTimer> _timerList = [];
    private readonly List<ScriptTimer> _dueTimers = [];

    // Registry references let go of while a script runs (the main thread's stack is not touched then).
    private readonly List<int> _deferredUnrefs = [];
    private int _nextScriptId;
    private int _nextTimerId;
    private int _nextHandlerId;
    private long _frame;
    private LuaScriptInstance? _running;
    private bool _executing;
    private long _instructionsUsed;
    private bool _budgetExceeded;
    private long _refusalsAtStart;
    private bool _memoryExceeded;
    private bool _disposed;

    public LuaScriptRuntime(LuaScriptRuntimeOptions? options = null)
    {
        _options = options ?? new LuaScriptRuntimeOptions();
        _hostWrite = Guarded(HostWrite);
        _hostGui = Guarded(HostGui);
        _hostScene = Guarded(HostScene);
        _hostTimer = Guarded(HostTimer);
        _hostPhysics = Guarded(HostPhysics);
        _hostModule = Guarded(HostModule);
        _hostGc = Guarded(HostGc);
        _hostEvents = Guarded(HostEvents);
        _hostView = Guarded(HostView);
        _hostGameplay = Guarded(HostGameplay);
        _gui = new GuiScriptBridge(_options.Gui);

        _lua = new NLua.Lua(openLibs: true);
        _main = _lua.State;
        _main.Encoding = Encoding.UTF8; // KeraLua defaults to ASCII
        var memory = new LuaMemoryLimit(_main.Handle, _options.MemoryLimitBytes ?? long.MaxValue,
            _main.GarbageCollector(LuaGC.Count, 0) * 1024L + _main.GarbageCollector(LuaGC.Countb, 0), _options.MemoryAccounting);
        _memory = memory;
        _instructionHook = (state, debug) =>
        {
            memory.EnterHost();
            try
            {
                OnInstructionHook(state);
            }
            finally
            {
                memory.LeaveHost();
            }
        };

        // NLua puts its CLR bridge in the host globals. Scripts never see those, and the host does not use it.
        _main.PushNil();
        _main.SetGlobal("luanet");

        var top = _main.GetTop();

        // One runner coroutine for every script call: KeraLua pins a managed wrapper per thread it creates, so
        // threads are reused (reset after a failure) instead of created per call. The registry keeps it alive.
        _runner = _main.NewThread();
        var runnerReference = _main.Ref(LuaRegistry.Index);
        _runner.SetHook(_instructionHook, LuaHookMask.Count, HookInterval);

        if (_main.LoadBuffer(Encoding.UTF8.GetBytes(LuaSandbox.Prelude), "=wisko.sandbox", "t") != LuaStatus.OK)
        {
            throw new InvalidOperationException($"Lua sandbox setup failed to compile: {_main.ToString(-1, false)}");
        }

        _main.PushCFunction(_hostWrite);
        _main.PushInteger(_options.MaxPrintLength);
        PushStringArray(ScriptInputState.KeyNames);
        PushStringArray(ScriptInputState.MouseButtonNames);
        _main.PushCFunction(_hostGui);
        _main.PushCFunction(_hostScene);
        _main.PushCFunction(_hostTimer);
        _main.PushCFunction(_hostPhysics);
        _main.PushCFunction(_hostModule);
        _main.PushCFunction(_hostGc);
        _main.RawGetInteger(LuaRegistry.Index, runnerReference);
        _main.PushCFunction(_hostEvents);
        _main.PushCFunction(_hostView);
        _main.PushNumber(_options.FixedDeltaTime);

        // The gameplay prelude (a second trusted chunk the sandbox prelude runs with its helpers).
        _main.PushCFunction(_hostGameplay);
        if (_main.LoadBuffer(LuaSandbox.GameplayPrelude, "=wisko.gameplay", "t") != LuaStatus.OK)
        {
            throw new InvalidOperationException($"Lua gameplay setup failed to compile: {_main.ToString(-1, false)}");
        }

        if (_main.PCall(16, 5, 0) != LuaStatus.OK)
        {
            throw new InvalidOperationException($"Lua sandbox setup failed: {_main.ToString(-1, false)}");
        }

        _stopThreadReference = _main.Ref(LuaRegistry.Index);
        _stringMetatableReference = _main.Ref(LuaRegistry.Index);
        _inputValuesReference = _main.Ref(LuaRegistry.Index);
        _timeValuesReference = _main.Ref(LuaRegistry.Index);
        _newEnvironmentReference = _main.Ref(LuaRegistry.Index);
        _main.SetTop(top);
    }

    /// <summary>Seconds covered by the current frame, as seen by Time.deltaTime and Update(dt).</summary>
    public double DeltaTime { get; private set; }

    /// <summary>Sum of every frame's delta, as seen by Time.elapsedTime.</summary>
    public double ElapsedTime { get; private set; }

    /// <summary>Scripts currently loaded (not disposed).</summary>
    public int ScriptCount => _instances.Count;

    /// <summary>GUI callbacks (OnClick and the other button events) currently registered (diagnostics).</summary>
    public int ClickHandlerCount => _clickHandlers.Count;

    /// <summary>Events.On connections currently registered (diagnostics).</summary>
    public int EventHandlerCount => _eventHandlers.Count;

    /// <summary>Events.Emit calls waiting for <see cref="DispatchEvents"/> (diagnostics).</summary>
    public int QueuedEventCount => _eventQueue.Count;

    /// <summary>A script callback is running now (the host must not start another one).</summary>
    internal bool IsExecuting => _executing;

    /// <summary>Timers running (made and not yet finished or cancelled; diagnostics).</summary>
    public int TimerCount => _timers.Count;

    /// <summary>Told after every script callback (top-level chunk, Start, Update, OnClick, events, timers), also one that failed.</summary>
    internal Action? CallbackCompleted { get; set; }

    /// <summary>The running scene behind Scene.Create, object:Destroy() and Physics.Raycast (null: not available).</summary>
    internal IScriptSceneHost? SceneHost { get; set; }

    /// <summary>Lua heap in use, in bytes (diagnostics).</summary>
    public long LuaMemoryBytes
    {
        get
        {
            ThrowIfUnusable();
            return _main.GarbageCollector(LuaGC.Count, 0) * 1024L + _main.GarbageCollector(LuaGC.Countb, 0);
        }
    }

    /// <summary>The Lua heap as the memory limit counts it (every block Lua allocated and has not freed), in bytes.</summary>
    public long LuaMemoryAccountedBytes => _memory.UsedBytes;

    /// <summary>The most the Lua heap has held this session, in bytes.</summary>
    public long LuaMemoryPeakBytes => _memory.PeakBytes;

    /// <summary>The session's memory limit in bytes (<see cref="long.MaxValue"/>: none).</summary>
    public long LuaMemoryLimitBytes => _memory.LimitBytes;

    /// <summary>Runs a full Lua garbage collection (e.g. after unloading many scripts).</summary>
    public void CollectGarbage()
    {
        ThrowIfUnusable();
        _main.GarbageCollector(LuaGC.Collect, 0);
    }

    /// <summary>Starts a new frame for scripts: Time.deltaTime = deltaTime, Time.elapsedTime += deltaTime.</summary>
    public void AdvanceTime(double deltaTime) => AdvanceTime(deltaTime, deltaTime);

    /// <summary>
    /// <see cref="AdvanceTime(double)"/> when the simulation runs at a time scale: <paramref name="deltaTime"/> is the
    /// scaled frame time (Time.deltaTime, game time), <paramref name="unscaledDeltaTime"/> the real one
    /// (Time.unscaledDeltaTime).
    /// </summary>
    public void AdvanceTime(double deltaTime, double unscaledDeltaTime)
    {
        ThrowIfUnusable();
        DeltaTime = deltaTime;
        ElapsedTime += deltaTime;
        _frame++;

        var top = _main.GetTop();
        _main.RawGetInteger(LuaRegistry.Index, _timeValuesReference);
        _main.PushString("deltaTime");
        _main.PushNumber(DeltaTime);
        _main.RawSet(-3);
        _main.PushString("elapsedTime");
        _main.PushNumber(ElapsedTime);
        _main.RawSet(-3);
        _main.PushString("frameCount");
        _main.PushInteger(_frame);
        _main.RawSet(-3);
        PublishGameplayTime(unscaledDeltaTime);
        _main.SetTop(top);
    }

    /// <summary>
    /// Publishes one frame's keyboard and mouse snapshot to every script's Input (call before the frame's callbacks).
    /// Edges (pressed/released) last exactly until the next call.
    /// </summary>
    public void SetInput(ScriptInputState input)
    {
        ThrowIfUnusable();
        var top = _main.GetTop();
        _main.RawGetInteger(LuaRegistry.Index, _inputValuesReference);
        SetNumber("mouseX", input.MouseX);
        SetNumber("mouseY", input.MouseY);
        SetNumber("mouseDeltaX", input.MouseDeltaX);
        SetNumber("mouseDeltaY", input.MouseDeltaY);
        SetNumber("scrollDelta", input.ScrollDelta);

        // Fresh sets every frame: nothing from the previous frame can linger.
        SetNameSet("keysDown", input.KeysDown);
        SetNameSet("keysPressed", input.KeysPressed);
        SetNameSet("keysReleased", input.KeysReleased);
        SetNameSet("buttonsDown", input.MouseButtonsDown);
        SetNameSet("buttonsPressed", input.MouseButtonsPressed);
        SetNameSet("buttonsReleased", input.MouseButtonsReleased);
        _main.SetTop(top);
    }

    /// <summary>
    /// Loads a script from source text (UTF-8 is used for the Lua bytes). With <paramref name="self"/> the script sees
    /// that object as its self; without, it is a standalone script with no self.
    /// </summary>
    public LuaScriptInstance LoadString(string source, string name, IScriptObject? self = null) =>
        Load(Encoding.UTF8.GetBytes(source), name, self);

    /// <summary>
    /// Loads a script from a file chosen by trusted engine code (the script itself gets no file access). UTF-8, with
    /// or without a byte-order mark. A file that cannot be read gives a faulted script, not an exception.
    /// </summary>
    public LuaScriptInstance LoadFile(string path, string? name = null, IScriptObject? self = null)
    {
        ThrowIfUnusable();
        name ??= Path.GetFileName(path);
        byte[] source;
        try
        {
            source = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var instance = Register(name, null);
            Fail(instance, new ScriptDiagnostic(name, ScriptPhase.Load, ScriptDiagnosticKind.Runtime, null, $"could not read the script file ({ex.Message})"), null);
            return instance;
        }

        if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
        {
            source = source[3..];
        }

        return Load(source, name, self);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_executing)
        {
            throw new InvalidOperationException("The Lua runtime cannot be disposed from inside a running script.");
        }

        // Instances die with the runtime: their references (and their OnClick callbacks) live in this state.
        foreach (var instance in _instances)
        {
            instance.State = ScriptInstanceState.Disposed;
        }

        _clickHandlers.Clear();
        _eventHandlers.Clear();
        _eventQueue.Clear();
        _timers.Clear();
        _timerList.Clear();
        _dueTimers.Clear();
        _deferredUnrefs.Clear();
        _owned.Clear();
        _instances.Clear();
        _bindings.Clear();
        _objects.Clear();
        _objectIds.Clear();
        _sceneObjects.Clear();
        _disposed = true;
        _lua.Dispose();

        // Closing the state freed every block through the accounting allocator; only now can it go.
        _memory.Dispose();
    }

    // Host functions run while Lua code is on the stack: nothing is refused inside them (see LuaMemoryLimit).
    private LuaFunction Guarded(LuaFunction host) => state =>
    {
        _memory.EnterHost();
        try
        {
            return host(state);
        }
        finally
        {
            _memory.LeaveHost();
        }
    };

    /// <summary>
    /// Adds a scene object (call in scene order, before loading scripts): Scene finds it, and its Id is its position in
    /// that order, from 1. Returns its Id.
    /// </summary>
    internal int AddSceneObject(IScriptObject target)
    {
        ThrowIfUnusable();
        return RegisterSceneObject(target);
    }

    /// <summary>
    /// <see cref="AddSceneObject"/> for the scene host while a script runs (Scene.Create): the new object gets the next
    /// Id, after every object so far, so Scene finds it after them.
    /// </summary>
    internal int RegisterSceneObject(IScriptObject target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = IdOf(target);
        var index = _sceneObjects.BinarySearch(id);
        if (index < 0)
        {
            _sceneObjects.Insert(~index, id);
        }

        return id;
    }

    /// <summary>
    /// The scene host destroyed this object: Scene no longer finds it and its Id now names a stand-in that no longer
    /// exists (wrappers report it gone; the Id is never given again). The object itself is let go.
    /// </summary>
    internal void RemoveSceneObject(IScriptObject target)
    {
        if (_disposed || !_objectIds.Remove(target, out var id))
        {
            return;
        }

        _objects[id - 1] = new RemovedObject(target.Name);
        var index = _sceneObjects.BinarySearch(id);
        if (index >= 0)
        {
            _sceneObjects.RemoveAt(index);
        }

        // Its OnDestroyed connections hear about it at the start of the next frame (with the object, which no longer
        // exists), never inside the call that destroyed it.
        var name = DestroyedEventName(id);
        if (_eventHandlers.Exists(handler => handler.Active && handler.Internal && string.Equals(handler.Name, name, StringComparison.Ordinal)))
        {
            _eventQueue.Add(new QueuedEvent(name, [new EventObject(id)], "o", Internal: true));
        }
    }

    private static string DestroyedEventName(int objectId) => "destroyed:" + objectId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);

    private static string AttributeEventName(int objectId, string attribute) =>
        "attr:" + objectId.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ":" + attribute;

    // An attribute changed value (set or removed): its OnAttributeChanged connections hear about it at the start of the
    // next frame, with the new value (nil when removed) and the object.
    private void NoteAttributeChange(IScriptObject target, string attribute, object? before, object? after)
    {
        var objectId = IdOfObject(target);
        if (Equals(before, after) || objectId <= 0)
        {
            return;
        }

        var name = AttributeEventName(objectId, attribute);
        if (_eventHandlers.Exists(handler => handler.Active && handler.Internal && string.Equals(handler.Name, name, StringComparison.Ordinal))
            && _eventQueue.Count < MaxQueuedEvents)
        {
            _eventQueue.Add(new QueuedEvent(name, [after, new EventObject(objectId)], "vo", Internal: true));
        }
    }

    /// <summary>The object's Id in this runtime, or 0 when it has none (never registered, or removed).</summary>
    internal int IdOfObject(IScriptObject target) => _objectIds.TryGetValue(target, out var id) ? id : 0;

    /// <summary>Logs a failure found by the host about a script (e.g. a missing file) the same way script failures are logged.</summary>
    internal void Report(ScriptDiagnostic diagnostic) =>
        Emit(new ScriptLogMessage(ScriptLogKind.Error, diagnostic.ScriptName, diagnostic.ToString()) { Diagnostic = diagnostic });

    internal ScriptExecutionResult RunStart(LuaScriptInstance instance)
    {
        if (!IsRunnable(instance) || instance.State != ScriptInstanceState.Loaded)
        {
            return ScriptExecutionResult.Skipped(instance.Fault);
        }

        // Started before running: Start runs at most once even when it fails.
        instance.State = ScriptInstanceState.Started;
        return instance.HasStart
            ? Call(instance, instance.StartReference, ScriptPhase.Start, passDeltaTime: false)
            : ScriptExecutionResult.Success;
    }

    internal ScriptExecutionResult RunUpdate(LuaScriptInstance instance)
    {
        if (!IsRunnable(instance))
        {
            return ScriptExecutionResult.Skipped(instance.Fault);
        }

        if (instance.State == ScriptInstanceState.Loaded)
        {
            var start = RunStart(instance);
            if (start.Status == ScriptRunStatus.Failed)
            {
                return start;
            }

            // Start may have destroyed its own object.
            if (!IsRunnable(instance))
            {
                return ScriptExecutionResult.Skipped(instance.Fault);
            }
        }

        return instance.HasUpdate
            ? Call(instance, instance.UpdateReference, ScriptPhase.Update, passDeltaTime: true)
            : ScriptExecutionResult.Success;
    }

    // After physics: only a script that has started (a script attached this frame starts with the next Update).
    internal ScriptExecutionResult RunLateUpdate(LuaScriptInstance instance)
    {
        if (!IsRunnable(instance) || instance.State != ScriptInstanceState.Started)
        {
            return ScriptExecutionResult.Skipped(instance.Fault);
        }

        return instance.HasLateUpdate
            ? Call(instance, instance.LateUpdateReference, ScriptPhase.LateUpdate, passDeltaTime: true)
            : ScriptExecutionResult.Success;
    }

    /// <summary>
    /// A GUI Button was clicked: runs the OnClick callbacks registered for it, in registration order, each as the script
    /// that registered it (its self, its own instruction budget). A failing callback faults its script like a failing
    /// Update (reported once; that script's callbacks are dropped); the other callbacks and scripts carry on. Callbacks
    /// registered while this runs first run on the next click.
    /// </summary>
    internal void DispatchClick(int buttonId) => DispatchGuiEvent(GuiButtonEvent.Click, buttonId);

    /// <summary>
    /// A button event (<see cref="GuiButtonEvent"/>) happened: runs the callbacks registered for that button and event
    /// (OnClick, OnPressed, OnReleased, OnHoverEnter, OnHoverExit), as <see cref="DispatchClick"/> does for clicks.
    /// </summary>
    internal void DispatchGuiEvent(GuiButtonEvent kind, int buttonId)
    {
        ThrowIfUnusable();
        List<ClickHandler>? handlers = null;
        foreach (var handler in _clickHandlers)
        {
            if (handler.ButtonId == buttonId && handler.Kind == kind)
            {
                (handlers ??= []).Add(handler);
            }
        }

        if (handlers is null)
        {
            return;
        }

        var phase = kind switch
        {
            GuiButtonEvent.Pressed => ScriptPhase.OnPressed,
            GuiButtonEvent.Released => ScriptPhase.OnReleased,
            GuiButtonEvent.HoverEnter => ScriptPhase.OnHoverEnter,
            GuiButtonEvent.HoverExit => ScriptPhase.OnHoverExit,
            _ => ScriptPhase.OnClick
        };
        foreach (var handler in handlers)
        {
            if (handler.Active && IsRunnable(handler.Owner))
            {
                Call(handler.Owner, handler.FunctionReference, phase, passDeltaTime: false);
            }
        }
    }

    /// <summary>
    /// Delivers the Events.Emit calls made since the last dispatch, in emit order; each to every connection of its name,
    /// in connection order, as the script that connected (its budget and failure rules: a failing handler faults only its
    /// script). Scene objects and GUI elements arrive as the receiver's own wrappers. Events emitted meanwhile wait for
    /// the next dispatch (one per frame), so handlers can never ping-pong forever.
    /// </summary>
    internal void DispatchEvents()
    {
        ThrowIfUnusable();
        if (_eventQueue.Count == 0)
        {
            return;
        }

        var queued = _eventQueue.ToArray();
        _eventQueue.Clear();
        for (var next = 0; next < queued.Length; next++)
        {
            var queuedEvent = queued[next];
            try
            {
                List<EventHandlerEntry>? handlers = null;
                foreach (var handler in _eventHandlers)
                {
                    if (handler.Active && handler.Internal == queuedEvent.Internal && string.Equals(handler.Name, queuedEvent.Name, StringComparison.Ordinal))
                    {
                        (handlers ??= []).Add(handler);
                    }
                }

                if (handlers is null)
                {
                    continue;
                }

                foreach (var handler in handlers)
                {
                    if (handler.Active && IsRunnable(handler.Owner))
                    {
                        CallEventHandler(handler, queuedEvent, queuedEvent.Internal && TextBoxIdOf(queuedEvent.Name) is not null ? ScriptPhase.TextBoxCallback : ScriptPhase.Event);
                    }
                }

                if (queuedEvent.Internal && handlers.Exists(static handler => handler.Once))
                {
                    // OnDestroyed fires once: its object is gone for good.
                    foreach (var handler in handlers)
                    {
                        if (handler.Active && handler.Once)
                        {
                            handler.Active = false;
                            Unref(handler.FunctionReference);
                        }
                    }

                    _eventHandlers.RemoveAll(static handler => !handler.Active);
                }
            }
            finally
            {
                ReleaseEventTables(queuedEvent);
            }
        }
    }

    // An event's table snapshots are let go once every receiver has had its copy (or nobody listened).
    private void ReleaseEventTables(QueuedEvent queuedEvent)
    {
        foreach (var argument in queuedEvent.Arguments)
        {
            if (argument is EventTable table)
            {
                Unref(table.Reference);
            }
        }
    }

    /// <summary>
    /// Calls one of the script's collision / trigger functions with <paramref name="otherId"/>'s wrapper (the script's
    /// own; 0: nil), as that script (self, budget, failure rules: a failure faults it like a failing Update). Nothing
    /// runs when it does not define that function or cannot run (faulted, released, its object destroyed).
    /// </summary>
    internal ScriptExecutionResult CallHandler(LuaScriptInstance instance, ScriptEventHandler handler, int otherId) =>
        CallHandler(instance, handler, otherId, null);

    /// <summary>
    /// <see cref="CallHandler(LuaScriptInstance, ScriptEventHandler, int)"/> with the contact as the second argument
    /// (a read-only table: Point, Normal (nil for a trigger), RelativeVelocity, IsEnvironment), or nil without one.
    /// </summary>
    internal ScriptExecutionResult CallHandler(LuaScriptInstance instance, ScriptEventHandler handler, int otherId, ScriptContact? contact)
    {
        if (!IsRunnable(instance) || !instance.HasHandler(handler))
        {
            return ScriptExecutionResult.Skipped(instance.Fault);
        }

        _runner.RawGetInteger(LuaRegistry.Index, instance.DeliverReference);
        _runner.RawGetInteger(LuaRegistry.Index, instance.HandlerReferences[(int)handler]);
        if (otherId > 0)
        {
            _runner.PushInteger(otherId);
        }
        else
        {
            _runner.PushNil();
        }

        if (contact is not { } point)
        {
            return Finish(instance, PhaseOf(handler), 2);
        }

        // deliver(f, id, px, py, pz, nx, ny, nz (nil: none), vx, vy, vz, isEnvironment)
        _runner.PushNumber(point.Point.X);
        _runner.PushNumber(point.Point.Y);
        _runner.PushNumber(point.Point.Z);
        if (point.Normal is { } normal)
        {
            _runner.PushNumber(normal.X);
            _runner.PushNumber(normal.Y);
            _runner.PushNumber(normal.Z);
        }
        else
        {
            _runner.PushNil();
            _runner.PushNil();
            _runner.PushNil();
        }

        _runner.PushNumber(point.RelativeVelocity.X);
        _runner.PushNumber(point.RelativeVelocity.Y);
        _runner.PushNumber(point.RelativeVelocity.Z);
        _runner.PushBoolean(point.IsEnvironment);
        return Finish(instance, PhaseOf(handler), 12);
    }

    // One Events.On handler for one emitted event: deliverEvent(f, tags, values...) rebuilds the arguments for the
    // receiving script ('v': a plain value; 'o': a scene object Id; 'g': a GUI element's Id, type and name; 't': a table
    // snapshot, copied for the receiver).
    private ScriptExecutionResult CallEventHandler(EventHandlerEntry handler, QueuedEvent queuedEvent, ScriptPhase phase = ScriptPhase.Event)
    {
        var instance = handler.Owner;
        _runner.RawGetInteger(LuaRegistry.Index, instance.DeliverEventReference);
        _runner.RawGetInteger(LuaRegistry.Index, handler.FunctionReference);
        _runner.PushString(queuedEvent.Tags);
        var count = 3;
        foreach (var argument in queuedEvent.Arguments)
        {
            switch (argument)
            {
                case EventObject eventObject:
                    _runner.PushInteger(eventObject.Id);
                    count++;
                    break;
                case EventElement element:
                    _runner.PushInteger(element.Id);
                    _runner.PushString(element.Type);
                    _runner.PushString(element.Name);
                    count += 3;
                    break;
                case EventTable table:
                    // The snapshot itself: deliverEvent copies it for this script before f sees anything.
                    _runner.RawGetInteger(LuaRegistry.Index, table.Reference);
                    count++;
                    break;
                default:
                    PushValue(_runner, argument);
                    count++;
                    break;
            }
        }

        ThrowIfExecuting();
        return Finish(instance, phase, count - 1);
    }

    // A TextBox event's internal name: "textbox:Submit:12".
    private static string TextBoxEventName(string kind, int elementId) =>
        "textbox:" + kind + ":" + elementId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // The TextBox Id in a TextBox event's name; null for any other name.
    private static int? TextBoxIdOf(string name)
    {
        if (!name.StartsWith("textbox:", StringComparison.Ordinal))
        {
            return null;
        }

        var colon = name.LastIndexOf(':');
        return int.TryParse(name.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// A TextBox was submitted (Enter), lost focus or got focus: its OnSubmit(text) / OnFocusLost(submitted) /
    /// OnFocused() connections run at the start of the next frame, each as the script that connected it.
    /// </summary>
    internal void QueueTextBoxEvent(string kind, int elementId, object? argument)
    {
        var name = TextBoxEventName(kind, elementId);
        if (_disposed || _eventQueue.Count >= MaxQueuedEvents
            || !_eventHandlers.Exists(handler => handler.Active && handler.Internal && string.Equals(handler.Name, name, StringComparison.Ordinal)))
        {
            return;
        }

        _eventQueue.Add(argument is null
            ? new QueuedEvent(name, [], string.Empty, Internal: true)
            : new QueuedEvent(name, [argument], "v", Internal: true));
    }

    /// <summary>
    /// The script's object was destroyed (<see cref="BeginDestroy"/>): runs its OnDestroy() once, unless it already
    /// faulted or was released. A failure is reported like any other (phase OnDestroy); the caller releases it either way.
    /// </summary>
    internal void RunDestroy(LuaScriptInstance instance)
    {
        ThrowIfUnusable();
        if (instance.State is ScriptInstanceState.Loaded or ScriptInstanceState.Started && instance.HasHandler(ScriptEventHandler.OnDestroy))
        {
            Call(instance, instance.HandlerReferences[(int)ScriptEventHandler.OnDestroy], ScriptPhase.OnDestroy, passDeltaTime: false);
        }
    }

    /// <summary>
    /// <paramref name="target"/> was destroyed: every script bound to it stops at once (no more Start, Update, events,
    /// clicks or timers; its OnClick callbacks and timers are dropped). Returns them in load order, for the host to run
    /// their OnDestroy and release them once the running callback (if any) has finished. Safe while a script runs.
    /// </summary>
    internal List<LuaScriptInstance> BeginDestroy(IScriptObject target)
    {
        var stopped = new List<LuaScriptInstance>();
        foreach (var instance in _instances)
        {
            if (!instance.Destroyed && instance.Binding is { } binding && ReferenceEquals(binding.Target, target))
            {
                instance.Destroyed = true;
                RemoveClickHandlers(instance);
                RemoveEventHandlers(instance);
                RemoveTimers(instance);
                RemoveOwned(instance);
                stopped.Add(instance);
            }
        }

        return stopped;
    }

    /// <summary>
    /// One script stops at once, as when its object is destroyed (no more Start, Update, events, clicks or timers): false
    /// when it was already stopped, faulted or released. The host runs its OnDestroy and releases it afterwards.
    /// </summary>
    internal bool BeginStop(LuaScriptInstance instance)
    {
        if (instance.Destroyed || instance.State is ScriptInstanceState.Faulted or ScriptInstanceState.Disposed)
        {
            return false;
        }

        instance.Destroyed = true;
        RemoveClickHandlers(instance);
        RemoveEventHandlers(instance);
        RemoveTimers(instance);
        RemoveOwned(instance);
        return true;
    }

    /// <summary>
    /// Runs the timers that are due in game time (<see cref="ElapsedTime"/>), once per frame after
    /// <see cref="AdvanceTime"/>: those made before this frame, in order of due time then creation, each as its script.
    /// An After timer ends as it runs; an Every timer runs at most once per frame, its next due time moving on by whole
    /// intervals past now (a long frame skips the missed ones instead of running them all). Timers made while these run
    /// wait for a later frame.
    /// </summary>
    internal void RunTimers()
    {
        ThrowIfUnusable();
        if (_timerList.Count == 0)
        {
            return;
        }

        var now = ElapsedTime;
        _dueTimers.Clear();
        foreach (var timer in _timerList)
        {
            if (timer.Active && !timer.Paused && timer.Frame < _frame && now >= timer.Due - TimerTolerance)
            {
                _dueTimers.Add(timer);
            }
        }

        if (_dueTimers.Count > 1)
        {
            _dueTimers.Sort(static (a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Id.CompareTo(b.Id));
        }

        foreach (var timer in _dueTimers)
        {
            // An earlier callback may have cancelled, paused or restarted it, faulted its script or destroyed its object.
            if (!timer.Active || timer.Paused || timer.Frame >= _frame || !IsRunnable(timer.Owner))
            {
                continue;
            }

            if (timer.Interval > 0d)
            {
                var next = timer.Due + timer.Interval;
                if (next <= now)
                {
                    next = timer.Due + (global::System.Math.Floor((now - timer.Due) / timer.Interval) + 1d) * timer.Interval;
                }

                timer.Due = next > now ? next : now + timer.Interval;
                CallTimer(timer, ScriptPhase.TimerEvery);
            }
            else
            {
                // Finished before it runs (Active is false inside it); its function is let go after.
                StopTimer(timer, releaseFunction: false);
                CallTimer(timer, ScriptPhase.TimerAfter);
                Unref(timer.FunctionReference);
            }
        }

        _dueTimers.Clear();
        _timerList.RemoveAll(static timer => !timer.Active);
    }

    internal void Release(LuaScriptInstance instance)
    {
        if (instance.State == ScriptInstanceState.Disposed)
        {
            return;
        }

        ThrowIfExecuting();
        RemoveClickHandlers(instance);
        RemoveEventHandlers(instance);
        RemoveTimers(instance);
        RemoveOwned(instance);
        instance.State = ScriptInstanceState.Disposed;
        _instances.Remove(instance);
        foreach (var reference in new[] { instance.EnvironmentReference, instance.StartReference, instance.UpdateReference, instance.LateUpdateReference,
                     instance.ExportsReference, instance.DeliverReference, instance.StringReference, instance.DeliverEventReference })
        {
            if (reference != LuaScriptInstance.NoReference)
            {
                _main.Unref(LuaRegistry.Index, reference);
            }
        }

        for (var i = 0; i < instance.HandlerReferences.Length; i++)
        {
            if (instance.HandlerReferences[i] != LuaScriptInstance.NoReference)
            {
                _main.Unref(LuaRegistry.Index, instance.HandlerReferences[i]);
                instance.HandlerReferences[i] = LuaScriptInstance.NoReference;
            }
        }

        instance.EnvironmentReference = instance.StartReference = instance.UpdateReference = instance.LateUpdateReference =
            instance.ExportsReference = instance.DeliverReference = instance.StringReference = instance.DeliverEventReference = LuaScriptInstance.NoReference;
        if (instance.Binding is { } binding)
        {
            instance.Binding = null;
            if (--binding.Users == 0)
            {
                _bindings.Remove(binding.Target);
            }
        }
    }

    private LuaScriptInstance Load(byte[] source, string name, IScriptObject? self)
    {
        ThrowIfUnusable();
        var instance = Register(name, self);
        var top = _main.GetTop();
        try
        {
            // A fresh allow-listed environment for this script only (with self when bound to an object).
            _main.RawGetInteger(LuaRegistry.Index, _newEnvironmentReference);
            _main.PushString(name);
            if (instance.Binding is { } binding)
            {
                _main.PushInteger(binding.Id);
            }
            else
            {
                _main.PushNil();
            }

            if (_main.PCall(2, 4, 0) != LuaStatus.OK)
            {
                Fail(instance, Diagnose(instance, ScriptPhase.Load, ScriptDiagnosticKind.Runtime, _main.ToString(-1, false) ?? "environment setup failed"), null);
                return instance;
            }

            instance.DeliverEventReference = _main.Ref(LuaRegistry.Index);
            instance.StringReference = _main.Ref(LuaRegistry.Index);
            instance.DeliverReference = _main.Ref(LuaRegistry.Index);
            instance.EnvironmentReference = _main.Ref(LuaRegistry.Index);

            // Text chunks only: precompiled bytecode can crash the VM and is never accepted.
            if (_main.LoadBuffer(source, "=" + instance.ChunkName, "t") != LuaStatus.OK)
            {
                Fail(instance, Diagnose(instance, ScriptPhase.Load, ScriptDiagnosticKind.Syntax, ErrorText(_main)), null);
                return instance;
            }

            // A main chunk's only upvalue is _ENV: point it at the script's own environment.
            _main.RawGetInteger(LuaRegistry.Index, instance.EnvironmentReference);
            _main.SetUpValue(-2, 1);
            _main.XMove(_runner, 1);
            if (ExecuteCallback(instance, ScriptPhase.Load, 0) is { } loadFailure)
            {
                Fail(instance, loadFailure.Diagnostic, loadFailure.Traceback);
                return instance;
            }

            if (!TryReadLifecycleFunction(instance, "Start", out var startReference)
                || !TryReadLifecycleFunction(instance, "Update", out var updateReference)
                || !TryReadLifecycleFunction(instance, "LateUpdate", out var lateUpdateReference))
            {
                return instance;
            }

            instance.StartReference = startReference;
            instance.UpdateReference = updateReference;
            instance.LateUpdateReference = lateUpdateReference;
            foreach (var handler in Enum.GetValues<ScriptEventHandler>())
            {
                if (!TryReadLifecycleFunction(instance, handler.ToString(), out var handlerReference))
                {
                    return instance;
                }

                instance.HandlerReferences[(int)handler] = handlerReference;
            }

            return instance;
        }
        finally
        {
            _main.SetTop(top);
        }
    }

    private LuaScriptInstance Register(string name, IScriptObject? self)
    {
        var id = ++_nextScriptId;
        var instance = new LuaScriptInstance(this, name, IsPlainChunkName(name) ? name : $"wisko.script.{id}");
        if (self is not null)
        {
            instance.Binding = Bind(self);
        }

        _instances.Add(instance);
        return instance;
    }

    // One binding per object, shared by every script bound to it.
    private ObjectBinding Bind(IScriptObject target)
    {
        if (!_bindings.TryGetValue(target, out var binding))
        {
            binding = new ObjectBinding(target, IdOf(target));
            _bindings.Add(target, binding);
        }

        binding.Users++;
        return binding;
    }

    // The object's Id in this runtime (given on first use).
    private int IdOf(IScriptObject target)
    {
        if (!_objectIds.TryGetValue(target, out var id))
        {
            _objects.Add(target);
            id = _objects.Count;
            _objectIds.Add(target, id);
        }

        return id;
    }

    // Names Lua can show verbatim in script-visible error messages: printable ASCII (KeraLua passes chunk names with
    // the ANSI code page, which is not UTF-8 on Windows) and short enough not to be shortened by Lua (LUA_IDSIZE).
    // Other names get an internal ASCII chunk name; diagnostics always show the real name.
    private static bool IsPlainChunkName(string name) =>
        name.Length is > 0 and <= 40 && name.All(ch => ch is >= ' ' and <= '~');

    // Reads Start/Update straight from the environment (raw: a script-set metatable must not run host-side).
    private bool TryReadLifecycleFunction(LuaScriptInstance instance, string functionName, out int reference)
    {
        reference = LuaScriptInstance.NoReference;
        var top = _main.GetTop();
        _main.RawGetInteger(LuaRegistry.Index, instance.EnvironmentReference);
        _main.PushString(functionName);
        var type = _main.RawGet(-2);
        if (type == LuaType.Function)
        {
            reference = _main.Ref(LuaRegistry.Index);
            _main.SetTop(top);
            return true;
        }

        _main.SetTop(top);
        if (type == LuaType.Nil)
        {
            return true;
        }

        Fail(instance, new ScriptDiagnostic(instance.Name, ScriptPhase.Load, ScriptDiagnosticKind.Runtime, null,
            $"{functionName} must be a function, not a {_main.TypeName(type)}"), null);
        return false;
    }

    private ScriptExecutionResult Call(LuaScriptInstance instance, int functionReference, ScriptPhase phase, bool passDeltaTime)
    {
        ThrowIfExecuting();
        _runner.RawGetInteger(LuaRegistry.Index, functionReference);
        if (passDeltaTime)
        {
            _runner.PushNumber(DeltaTime);
        }

        return Finish(instance, phase, passDeltaTime ? 1 : 0);
    }

    // Runs what is on the runner's stack (function and arguments) as the instance; a failure faults it.
    private ScriptExecutionResult Finish(LuaScriptInstance instance, ScriptPhase phase, int argumentCount)
    {
        if (ExecuteCallback(instance, phase, argumentCount) is { } failure)
        {
            Fail(instance, failure.Diagnostic, failure.Traceback);
            return ScriptExecutionResult.Failure(failure.Diagnostic);
        }

        return ScriptExecutionResult.Success;
    }

    private static ScriptPhase PhaseOf(ScriptEventHandler handler) => handler switch
    {
        ScriptEventHandler.OnCollisionEnter => ScriptPhase.OnCollisionEnter,
        ScriptEventHandler.OnCollisionStay => ScriptPhase.OnCollisionStay,
        ScriptEventHandler.OnCollisionExit => ScriptPhase.OnCollisionExit,
        ScriptEventHandler.OnTriggerEnter => ScriptPhase.OnTriggerEnter,
        ScriptEventHandler.OnTriggerStay => ScriptPhase.OnTriggerStay,
        ScriptEventHandler.OnTriggerExit => ScriptPhase.OnTriggerExit,
        _ => ScriptPhase.OnDestroy
    };

    // Runs a callback, then tells the host it finished (also when it failed part-way: what it changed has happened).
    private (ScriptDiagnostic Diagnostic, string? Traceback)? ExecuteCallback(LuaScriptInstance instance, ScriptPhase phase, int argumentCount)
    {
        var failure = Execute(instance, phase, argumentCount);
        try
        {
            CallbackCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Lua] Could not apply what a script changed: {ex.Message}");
        }

        return failure;
    }

    private void PushStringArray(IReadOnlyList<string> values)
    {
        _main.CreateTable(values.Count, 0);
        for (var i = 0; i < values.Count; i++)
        {
            _main.PushString(values[i]);
            _main.RawSetInteger(-2, i + 1);
        }
    }

    // table[key] = value on the table at the top of the main stack.
    private void SetNumber(string key, float value)
    {
        _main.PushString(key);
        _main.PushNumber(float.IsFinite(value) ? value : 0d);
        _main.RawSet(-3);
    }

    // table[key] = { [name] = true, ... } on the table at the top of the main stack.
    private void SetNameSet(string key, IEnumerable<string> names)
    {
        _main.PushString(key);
        _main.NewTable();
        foreach (var name in names)
        {
            _main.PushString(name);
            _main.PushBoolean(true);
            _main.RawSet(-3);
        }

        _main.RawSet(-3);
    }

    // Runs the function (and arguments) on the runner's stack under the instruction budget, as the instance (string
    // methods come from its string library). On failure the runner is reset so it can serve the next call; the
    // failure's traceback is captured first. A callback that spent its budget fails even when it then finished (the
    // budget ran out in one of its coroutines and stopped that).
    private (ScriptDiagnostic Diagnostic, string? Traceback)? Execute(LuaScriptInstance instance, ScriptPhase phase, int argumentCount)
    {
        SetStringContext(instance);
        _instructionsUsed = 0;
        _budgetExceeded = false;
        _memoryExceeded = false;
        _refusalsAtStart = _memory.Refusals;
        _executing = true;
        _running = instance;
        LuaStatus status;
        try
        {
            _memory.Enforcing = true;
            status = _runner.Resume(null, argumentCount, out _);
        }
        finally
        {
            _memory.Enforcing = false;
            _executing = false;
            _running = null;
            FlushDeferredUnrefs();
        }

        // A refused allocation fails the callback even when the script caught the error (as the budget does).
        _memoryExceeded |= _memory.Refusals != _refusalsAtStart;
        if (status == LuaStatus.OK && !_budgetExceeded && !_memoryExceeded)
        {
            // A script's top-level chunk may return a table: what it exports to other scripts (object:GetScript).
            if (phase == ScriptPhase.Load && _runner.GetTop() >= 1 && _runner.Type(1) == LuaType.Table)
            {
                _runner.PushCopy(1);
                _runner.XMove(_main, 1);
                instance.ExportsReference = _main.Ref(LuaRegistry.Index);
            }

            _runner.SetTop(0);
            return null;
        }

        ScriptDiagnostic diagnostic;
        string message;
        if (_memoryExceeded)
        {
            message = $"stopped: the scripts' Lua memory would have gone past the limit of {LuaMemoryText(_memory.LimitBytes)} "
                + "(a table or string growing without end?)";
            diagnostic = new ScriptDiagnostic(instance.Name, phase, ScriptDiagnosticKind.MemoryLimitExceeded, CurrentScriptLine(instance), message);
        }
        else if (_budgetExceeded)
        {
            message = $"stopped after exceeding the instruction budget of {_options.InstructionBudget.ToString("N0", CultureInfo.InvariantCulture)} instructions (runaway loop?)";
            diagnostic = new ScriptDiagnostic(instance.Name, phase, ScriptDiagnosticKind.InstructionBudgetExceeded, CurrentScriptLine(instance), message);
        }
        else if (status == LuaStatus.Yield)
        {
            // Scripts' coroutine.yield refuses the runner (it plays Lua's main thread), so this should not happen.
            message = "yielded, which scripts cannot do";
            diagnostic = new ScriptDiagnostic(instance.Name, phase, ScriptDiagnosticKind.Runtime, CurrentScriptLine(instance), message);
        }
        else
        {
            message = ErrorText(_runner);
            diagnostic = Diagnose(instance, phase, ScriptDiagnosticKind.Runtime, message);
        }

        var traceback = Traceback(instance, message);
        ResetRunner();
        return (diagnostic, traceback);
    }

    // Makes the runner usable again after a failure. Closing it runs the script's pending to-be-closed variables
    // (__close), which get a budget of their own; the runner gets the count hook back (a stopped one has the budget's
    // error hook).
    private void ResetRunner()
    {
        _runner.SetHook(_instructionHook, LuaHookMask.Count, HookInterval);
        _instructionsUsed = 0;
        _budgetExceeded = false;
        _executing = true;
        try
        {
            _runner.ResetThread();
        }
        finally
        {
            _executing = false;
            _budgetExceeded = false;
            FlushDeferredUnrefs();
        }

        _runner.SetHook(_instructionHook, LuaHookMask.Count, HookInterval);
        _runner.SetTop(0);
    }

    // Strings' __index = the instance's string library (see LuaSandbox): its string methods while it runs.
    private void SetStringContext(LuaScriptInstance instance)
    {
        if (instance.StringReference == LuaScriptInstance.NoReference)
        {
            return;
        }

        var top = _main.GetTop();
        _main.RawGetInteger(LuaRegistry.Index, _stringMetatableReference);
        _main.PushString("__index");
        _main.RawGetInteger(LuaRegistry.Index, instance.StringReference);
        _main.RawSet(-3);
        _main.SetTop(top);
    }

    // Count hook on every thread that runs script code (the runner, and coroutines scripts make, which inherit it).
    // Runs inside native Lua, so it must never throw and never raise a Lua error (a longjmp across this managed frame is
    // undefined behaviour). When the budget is spent it asks the runner to yield, which Lua does after this method has
    // returned. Anywhere else (a script's coroutine, or code that cannot yield because a C function such as table.sort
    // called it) it installs Lua's own debug hook to raise an error at the next instruction (stopThread), which unwinds
    // natively: the callback always ends.
    private void OnInstructionHook(IntPtr state)
    {
        try
        {
            if (!_executing)
            {
                return;
            }

            // A script that caught "not enough memory" (pcall) is stopped too: the limit is not a recoverable error.
            if (_memory.Refusals != _refusalsAtStart)
            {
                _memoryExceeded = true;
            }
            else
            {
                _instructionsUsed += HookInterval;
                if (_instructionsUsed < _options.InstructionBudget)
                {
                    return;
                }

                _budgetExceeded = true;
            }

            if (state == _runner.Handle && _runner.IsYieldable)
            {
                _runner.Yield(0);
                return;
            }

            StopThread(state);
        }
        catch
        {
            // Never let an exception reach native code.
        }
    }

    // stopThread(thread) on that thread, in protected mode (nothing can unwind this managed frame).
    private void StopThread(IntPtr state)
    {
        var thread = state == _runner.Handle ? _runner : LuaState.FromIntPtr(state);
        var top = thread.GetTop();
        thread.RawGetInteger(LuaRegistry.Index, _stopThreadReference);
        thread.PushThread();
        thread.PCall(1, 0, 0);
        thread.SetTop(top);
    }

    // The Lua thread a host function was called on: the runner, the main thread (setup), or one of a running script's
    // coroutines.
    private LuaState? ThreadOf(IntPtr state)
    {
        if (state == _runner.Handle)
        {
            return _runner;
        }

        if (state == _main.Handle)
        {
            return _main;
        }

        return _executing && state != IntPtr.Zero ? LuaState.FromIntPtr(state) : null;
    }

    // print() sink: hostWrite(scriptName, message[, isWarning]), name and message already strings (built in Lua by the
    // sandbox). Warnings are the sandbox's own notes about a script (e.g. an unknown Input key name).
    private int HostWrite(IntPtr state)
    {
        try
        {
            var thread = ThreadOf(state);
            if (thread is not null)
            {
                var kind = thread.ToBoolean(3) ? ScriptLogKind.Warning : ScriptLogKind.Print;
                Emit(new ScriptLogMessage(kind, thread.ToString(1, false) ?? "?", thread.ToString(2, false) ?? string.Empty));
            }
        }
        catch
        {
            // Never let an exception reach native code.
        }

        return 0;
    }

    // UI's host side: hostGui(op, ...) from the sandbox's UI wrappers, which have already checked argument types. Like
    // print's sink it runs inside native Lua, so it never throws and never raises a Lua error: it answers with values,
    // and the Lua wrapper raises at the script's line. Answers: find / findid -> id, type, name (nothing when there is
    // no such element); get -> true, value; set / rect / click -> true; any of them -> false, message (false, nil: the
    // element no longer exists).
    private int HostGui(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            switch (request)
            {
                case "find":
                    return PushFound(lua, lua.Type(2) == LuaType.String ? _gui.Find(lua.ToString(2, false) ?? string.Empty) : null);
                case "findid":
                    return PushFound(lua, lua.Type(2) == LuaType.Number ? _gui.FindById(lua.ToNumber(2)) : null);
                case "findpath":
                    return PushFound(lua, lua.Type(2) == LuaType.String ? _gui.FindPath(lua.ToString(2, false) ?? string.Empty) : null);
                case "findall":
                    // id, type, name for each match, one flat list.
                    var found = lua.Type(2) == LuaType.String ? _gui.FindAll(lua.ToString(2, false) ?? string.Empty).ToList() : [];
                    lua.CreateTable(found.Count * 3, 0);
                    for (var i = 0; i < found.Count; i++)
                    {
                        lua.PushInteger(found[i].Id);
                        lua.RawSetInteger(-2, i * 3 + 1);
                        lua.PushString(found[i].Type.ToString());
                        lua.RawSetInteger(-2, i * 3 + 2);
                        lua.PushString(found[i].Name);
                        lua.RawSetInteger(-2, i * 3 + 3);
                    }

                    return 1;
                case "create":
                    // type, name or nil, parent id (0: the Canvas) -> id, type, name; or false, message.
                    string? createError;
                    GuiElement? created = null;
                    if (_gui.ResolveNode(lua.ToInteger(4)) is not { } parentNode)
                    {
                        createError = "UI.Create: the parent no longer exists";
                    }
                    else
                    {
                        createError = _gui.Create(lua.ToString(2, false) ?? string.Empty, lua.Type(3) == LuaType.String ? lua.ToString(3, false) : null,
                            parentNode, MaxGuiElements, out created);
                    }

                    if (createError is null && created is not null)
                    {
                        return PushFound(lua, created);
                    }

                    lua.PushBoolean(false);
                    lua.PushString(createError ?? "UI.Create failed");
                    return 2;
                case "measure":
                    // UI.MeasureText(text, fontSize) -> width, line height (nothing without a GUI).
                    if (_gui.MeasureText(lua.ToString(2, false) ?? string.Empty, (int)global::System.Math.Clamp(global::System.Math.Round(lua.ToNumber(3)), GuiText.MinFontSize, GuiText.MaxFontSize)) is not { } size)
                    {
                        return 0;
                    }

                    lua.PushNumber(size.Width);
                    lua.PushNumber(size.LineHeight);
                    return 2;
                case "typing":
                    lua.PushBoolean(_gui.Typing);
                    return 1;
                case "canvas":
                    // x, y, width, height of the game view the GUI is laid out in (window pixels).
                    var canvasRect = _gui.CanvasRect;
                    lua.PushNumber(canvasRect.X);
                    lua.PushNumber(canvasRect.Y);
                    lua.PushNumber(canvasRect.Width);
                    lua.PushNumber(canvasRect.Height);
                    return 4;
                case "children" or "findchild":
                    // The Canvas (0) or an element: its children in order (id, type, name each, flat), or the first
                    // child with this name. Nothing when the node is gone.
                    if (_gui.ResolveNode(lua.ToInteger(2)) is not { } node)
                    {
                        return 0;
                    }

                    if (request == "findchild")
                    {
                        var childName = lua.ToString(3, false) ?? string.Empty;
                        return PushFound(lua, node.Children.FirstOrDefault(child => string.Equals(child.Name, childName, StringComparison.Ordinal)));
                    }

                    lua.CreateTable(node.Children.Count * 3, 0);
                    for (var i = 0; i < node.Children.Count; i++)
                    {
                        lua.PushInteger(node.Children[i].Id);
                        lua.RawSetInteger(-2, i * 3 + 1);
                        lua.PushString(node.Children[i].Type.ToString());
                        lua.RawSetInteger(-2, i * 3 + 2);
                        lua.PushString(node.Children[i].Name);
                        lua.RawSetInteger(-2, i * 3 + 3);
                    }

                    return 1;
                case "disconnect" or "connected":
                    // A GUI callback's connection (by number).
                    var connection = _clickHandlers.Find(handler => handler.Id == lua.ToInteger(2) && handler.Active);
                    if (request == "connected")
                    {
                        lua.PushBoolean(connection is not null);
                        return 1;
                    }

                    if (connection is not null)
                    {
                        connection.Active = false;
                        Unref(connection.FunctionReference);
                        _clickHandlers.Remove(connection);
                    }

                    return 0;
            }

            if (_gui.Resolve(lua.ToInteger(2)) is not { } element)
            {
                lua.PushBoolean(false);
                lua.PushNil();
                return 2;
            }

            string? error;
            switch (request)
            {
                case "get":
                    var value = _gui.Get(element, lua.ToString(3, false) ?? string.Empty, out error);
                    if (error is null)
                    {
                        lua.PushBoolean(true);
                        PushValue(lua, value);
                        return 2;
                    }

                    break;
                case "set":
                    error = _gui.Set(element, lua.ToString(3, false) ?? string.Empty, ValueAt(lua, 4));
                    break;
                case "rect":
                    error = _gui.SetRect(element, lua.ToString(3, false) ?? string.Empty, lua.ToNumber(4), lua.ToNumber(5), lua.ToNumber(6), lua.ToNumber(7));
                    break;
                case "rectget":
                    var rect = GuiScriptBridge.GetRect(element, lua.ToString(3, false) ?? string.Empty);
                    lua.PushBoolean(true);
                    lua.PushNumber(rect.X.Scale);
                    lua.PushNumber(rect.X.Offset);
                    lua.PushNumber(rect.Y.Scale);
                    lua.PushNumber(rect.Y.Offset);
                    return 5;
                case "anchorget":
                    lua.PushBoolean(true);
                    lua.PushNumber(element.AnchorPoint.X);
                    lua.PushNumber(element.AnchorPoint.Y);
                    return 3;
                case "anchor":
                    error = _gui.SetAnchor(element, lua.ToNumber(3), lua.ToNumber(4));
                    break;
                case "click" or "event":
                    // click (f) / event (kind, f): a button callback; answers its connection number.
                    var kind = request == "click" ? GuiButtonEvent.Click : Enum.TryParse<GuiButtonEvent>(lua.ToString(3, false), out var parsed) ? parsed : (GuiButtonEvent?)null;
                    var handlerId = 0;
                    error = kind is { } eventKind ? RegisterClick(lua, element, eventKind, request == "click" ? 3 : 4, out handlerId) : "unknown button event";
                    if (error is null)
                    {
                        lua.PushInteger(handlerId);
                        return 1;
                    }

                    break;
                case "focus":
                    error = _gui.Focus(element);
                    break;
                case "release":
                    error = _gui.Release(element);
                    break;
                case "textevent":
                    // kind ("Submit", "FocusLost", "Focused"), f: a TextBox callback (delivered at the start of the next
                    // frame); answers its connection number (an internal Events connection).
                    if (element is not GuiTextBox)
                    {
                        error = GuiScriptBridge.NoMember(element, "On" + (lua.ToString(3, false) ?? string.Empty));
                        break;
                    }

                    return ConnectInternal(lua, 4, TextBoxEventName(lua.ToString(3, false) ?? string.Empty, element.Id), "a TextBox callback");
                case "parent":
                    // true, id, type, name of the parent; true, 0 for the Canvas.
                    lua.PushBoolean(true);
                    if (element.Parent is GuiElement parentElement)
                    {
                        return 1 + PushFound(lua, parentElement);
                    }

                    lua.PushInteger(GuiCanvas.CanvasId);
                    return 2;
                case "setparent":
                    error = _gui.ResolveNode(lua.ToInteger(3)) is { } newParent ? _gui.SetParent(element, newParent) : "SetParent: the new parent no longer exists";
                    break;
                case "destroy":
                    // The element and its subtree go; their callbacks with them.
                    var removed = _gui.Destroy(element).ToHashSet();
                    foreach (var handler in _clickHandlers)
                    {
                        if (handler.Active && removed.Contains(handler.ButtonId))
                        {
                            handler.Active = false;
                            Unref(handler.FunctionReference);
                        }
                    }

                    _clickHandlers.RemoveAll(static handler => !handler.Active);
                    foreach (var handler in _eventHandlers)
                    {
                        if (handler.Active && handler.Internal && TextBoxIdOf(handler.Name) is { } boxId && removed.Contains(boxId))
                        {
                            handler.Active = false;
                            Unref(handler.FunctionReference);
                        }
                    }

                    _eventHandlers.RemoveAll(static handler => !handler.Active);
                    error = null;
                    break;
                default:
                    error = "unknown UI request";
                    break;
            }

            if (error is null)
            {
                lua.PushBoolean(true);
                return 1;
            }

            lua.PushBoolean(false);
            lua.PushString(error);
            return 2;
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] UI request failed: {ex.Message}");
            lua.PushBoolean(false);
            lua.PushString("the UI request failed");
            return 2;
        }
    }

    // Scene's and self's host side: hostScene(op, ...) from the sandbox's object wrappers (argument types already checked
    // there). Never throws and never raises a Lua error (see HostGui). Answers: find / findid -> id (nothing when there
    // is no such scene object); findall -> a table of ids; name -> the name (nothing when gone); get -> true, value;
    // vget -> true, x, y, z; set / vset / impulse -> true; any of them -> false, message (false, nil: the object no
    // longer exists).
    private int HostScene(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            switch (request)
            {
                case "find":
                    return PushObjectId(lua, lua.Type(2) == LuaType.String ? FindSceneObjects(lua.ToString(2, false) ?? string.Empty).FirstOrDefault() : 0);
                case "findall":
                    var found = lua.Type(2) == LuaType.String ? FindSceneObjects(lua.ToString(2, false) ?? string.Empty).ToList() : [];
                    lua.CreateTable(found.Count, 0);
                    for (var i = 0; i < found.Count; i++)
                    {
                        lua.PushInteger(found[i]);
                        lua.RawSetInteger(-2, i + 1);
                    }

                    return 1;
                case "findid":
                    return PushObjectId(lua, lua.Type(2) == LuaType.Number ? FindSceneObjectById(lua.ToNumber(2)) : 0);
                case "all" or "bytype" or "bytag":
                    // all -> every object; bytype / bytag (name, firstOnly) -> those of that type or with that tag.
                    var searchKey = lua.ToString(2, false) ?? string.Empty;
                    var matches = request switch
                    {
                        "bytype" => SceneObjectsWhere(candidate => string.Equals(candidate.TypeName, searchKey, StringComparison.Ordinal)),
                        "bytag" => SceneObjectsWhere(candidate => candidate is IScriptPart tagged && tagged.Data.Tags.Contains(searchKey)),
                        _ => SceneObjectsWhere(static _ => true)
                    };
                    if (lua.ToBoolean(3))
                    {
                        return PushObjectId(lua, matches.FirstOrDefault());
                    }

                    PushIdTable(lua, matches.ToList());
                    return 1;
                case "count" or "countbytype" or "countbytag":
                    var countKey = lua.ToString(2, false) ?? string.Empty;
                    lua.PushInteger(request switch
                    {
                        "countbytype" => SceneObjectsWhere(candidate => string.Equals(candidate.TypeName, countKey, StringComparison.Ordinal)).Count(),
                        "countbytag" => SceneObjectsWhere(candidate => candidate is IScriptPart tagged && tagged.Data.Tags.Contains(countKey)).Count(),
                        _ => SceneObjectsWhere(static _ => true).Count()
                    });
                    return 1;
                case "scriptinfo":
                    // A runtime-attached script's handle: active, asset path (nothing when unknown).
                    if (SceneHost is not { } infoHost || !infoHost.TryGetAttachedScript((int)global::System.Math.Clamp(lua.ToInteger(2), 0, int.MaxValue), out var scriptActive, out var scriptPath))
                    {
                        return 0;
                    }

                    lua.PushBoolean(scriptActive);
                    lua.PushString(scriptPath);
                    return 2;
                case "detach":
                    SceneHost?.DetachScript((int)global::System.Math.Clamp(lua.ToInteger(2), 0, int.MaxValue));
                    return 0;
                case "create":
                    // type, name or nil, anchored, isTrigger (checked in Lua) -> the new object's id, or false, message.
                    string? createError;
                    IScriptObject? created = null;
                    if (SceneHost is not { } host)
                    {
                        createError = "Scene.Create is not available here (no running scene)";
                    }
                    else
                    {
                        createError = host.Create(lua.ToString(2, false) ?? string.Empty, lua.Type(3) == LuaType.String ? lua.ToString(3, false) : null,
                            lua.ToBoolean(4), lua.ToBoolean(5), out created);
                    }

                    if (createError is null && created is not null && IdOfObject(created) is > 0 and var createdId)
                    {
                        lua.PushInteger(createdId);
                        return 1;
                    }

                    lua.PushBoolean(false);
                    lua.PushString(createError ?? "Scene.Create failed");
                    return 2;
            }

            var id = lua.ToInteger(2);
            var target = id > 0 && id <= _objects.Count ? _objects[(int)id - 1] : null;
            var exists = target is { Exists: true };
            var key = lua.ToString(3, false) ?? string.Empty;
            if (request == "name")
            {
                // Also for an object that is gone (its last name, for messages).
                if (target is null)
                {
                    return 0;
                }

                lua.PushString(target.Name);
                return 1;
            }

            if (request == "get" && key == "Exists")
            {
                lua.PushBoolean(true);
                lua.PushBoolean(exists);
                return 2;
            }

            if (!exists)
            {
                lua.PushBoolean(false);
                lua.PushNil();
                return 2;
            }

            string? error;
            switch (request)
            {
                case "get":
                    var value = ScriptObjectAccess.Get(target!, (int)id, key, out error);
                    if (error is null)
                    {
                        lua.PushBoolean(true);
                        PushValue(lua, value);
                        return 2;
                    }

                    break;
                case "set":
                    error = ScriptObjectAccess.Set(target!, key, ValueAt(lua, 4));
                    break;
                case "matget" or "lightget":
                    // A Material or Light member (key); a light-less object's Light members are refused.
                    if (target is not IScriptGraphicsTarget graphicsGet)
                    {
                        error = "only scene parts and models have a material and a light";
                        break;
                    }

                    var graphicsValue = request == "matget"
                        ? ScriptGraphics.GetMaterial(graphicsGet.Part, key, out error)
                        : ScriptGraphics.GetLight(graphicsGet.Part, key, out error);
                    if (error is null)
                    {
                        lua.PushBoolean(true);
                        PushValue(lua, graphicsValue);
                        return 2;
                    }

                    break;
                case "matset" or "lightset" or "matpreset" or "matreset" or "lighthas" or "lightadd" or "lightremove":
                    if (target is not IScriptGraphicsTarget graphics)
                    {
                        error = "only scene parts and models have a material and a light";
                        break;
                    }

                    var part = graphics.Part;
                    switch (request)
                    {
                        case "matset":
                            error = ScriptGraphics.SetMaterial(part, key, ValueAt(lua, 4));
                            break;
                        case "lightset":
                            error = ScriptGraphics.SetLight(part, key, ValueAt(lua, 4));
                            break;
                        case "matpreset":
                            error = ScriptGraphics.ApplyPreset(part, key);
                            break;
                        case "matreset":
                            ScriptGraphics.ResetMaterial(part);
                            error = null;
                            break;
                        case "lighthas":
                            lua.PushBoolean(true);
                            lua.PushBoolean(part.Light is not null);
                            return 2;
                        case "lightadd":
                            error = ScriptGraphics.AddLight(part, lua.Type(3) == LuaType.String ? lua.ToString(3, false) : null);
                            break;
                        default:
                            part.Light = null;
                            error = null;
                            break;
                    }

                    break;
                case "vget":
                    if (ScriptObjectAccess.GetVector(target!, key, out var vector, out error))
                    {
                        lua.PushBoolean(true);
                        lua.PushNumber(vector.X);
                        lua.PushNumber(vector.Y);
                        lua.PushNumber(vector.Z);
                        return 4;
                    }

                    break;
                case "vsetall":
                    error = ScriptObjectAccess.SetVector(target!, key, new Vector3((float)lua.ToNumber(4), (float)lua.ToNumber(5), (float)lua.ToNumber(6)));
                    break;
                case "vset":
                    error = ScriptObjectAccess.SetComponent(target!, key, lua.ToString(4, false) ?? string.Empty, lua.ToNumber(5));
                    break;
                case "impulse":
                    error = ScriptObjectAccess.ApplyImpulse(target!, new Vector3((float)lua.ToNumber(3), (float)lua.ToNumber(4), (float)lua.ToNumber(5)));
                    break;
                case "push":
                    // kind (force, torque, angular, impulseat, forceat), x, y, z[, px, py, pz][, local]: finite numbers
                    // (checked in Lua). local: the vector is in the object's own axes (turned to the world's here).
                    if (target is not IScriptPart pushed)
                    {
                        error = ScriptObjectAccess.NotSimulated(target!);
                        break;
                    }

                    var push = new Vector3((float)lua.ToNumber(4), (float)lua.ToNumber(5), (float)lua.ToNumber(6));
                    if (lua.ToBoolean(10))
                    {
                        push = ScriptObjectAccess.ConvertSpace(target!, "vectorworld", push);
                    }

                    error = pushed.Push(key, push, new Vector3((float)lua.ToNumber(7), (float)lua.ToNumber(8), (float)lua.ToNumber(9)));
                    break;
                case "pointvel":
                    // x, y, z (a world point) -> true, vx, vy, vz.
                    var pointVelocity = target is IScriptPart moving
                        ? moving.VelocityAtPoint(new Vector3((float)lua.ToNumber(3), (float)lua.ToNumber(4), (float)lua.ToNumber(5)))
                        : Vector3.Zero;
                    lua.PushBoolean(true);
                    PushVector(lua, pointVelocity);
                    return 4;
                case "mass":
                    lua.PushBoolean(true);
                    if (target is IScriptPart { EffectiveMass: { } mass })
                    {
                        lua.PushNumber(mass);
                    }
                    else
                    {
                        lua.PushNil();
                    }

                    return 2;
                case "space":
                    // mode (pointworld, pointlocal, vectorworld, vectorlocal), x, y, z -> true, x, y, z.
                    lua.PushBoolean(true);
                    PushVector(lua, ScriptObjectAccess.ConvertSpace(target!, key, new Vector3((float)lua.ToNumber(4), (float)lua.ToNumber(5), (float)lua.ToNumber(6))));
                    return 4;
                case "rotate":
                    // world ("world" or "local"), x, y, z degrees.
                    error = ScriptObjectAccess.Rotate(target!, new Vector3((float)lua.ToNumber(4), (float)lua.ToNumber(5), (float)lua.ToNumber(6)), key == "world");
                    break;
                case "rotatearound":
                    // pivot x, y, z, axis x, y, z, degrees (index 3 is unused).
                    error = ScriptObjectAccess.RotateAround(target!, new Vector3((float)lua.ToNumber(4), (float)lua.ToNumber(5), (float)lua.ToNumber(6)),
                        new Vector3((float)lua.ToNumber(7), (float)lua.ToNumber(8), (float)lua.ToNumber(9)), (float)lua.ToNumber(10));
                    break;
                case "lookat":
                    // target x, y, z, up x, y, z (index 3 is unused).
                    error = ScriptObjectAccess.LookAt(target!, new Vector3((float)lua.ToNumber(4), (float)lua.ToNumber(5), (float)lua.ToNumber(6)),
                        new Vector3((float)lua.ToNumber(7), (float)lua.ToNumber(8), (float)lua.ToNumber(9)));
                    break;
                case "ignorecol":
                    // other id (read as a number: the key read above made it text), ignore.
                    var otherId = (long)lua.ToNumber(3);
                    var other = otherId > 0 && otherId <= _objects.Count ? _objects[(int)otherId - 1] : null;
                    error = other is not { Exists: true } ? "IgnoreCollisionWith: that object no longer exists"
                        : target is IScriptPart ignoring ? ignoring.IgnoreCollisionWith(other, lua.ToBoolean(4))
                        : ScriptObjectAccess.NotSimulated(target!);
                    break;
                case "onattr":
                    // name, f -> the connection's number (an Events connection), or false, message.
                    if (_running is not { } attributeWatcher || lua.Type(4) != LuaType.Function || ScriptPartData.CheckName("an attribute name", key) is not null)
                    {
                        error = "OnAttributeChanged is not available here";
                        break;
                    }

                    if (_eventHandlers.Count(handler => ReferenceEquals(handler.Owner, attributeWatcher)) >= MaxHandlersPerScript)
                    {
                        error = $"this script already has {MaxHandlersPerScript:N0} event connections: disconnect the ones it no longer needs";
                        break;
                    }

                    lua.PushCopy(4);
                    var attributeHandlerId = ++_nextHandlerId;
                    _eventHandlers.Add(new EventHandlerEntry(attributeHandlerId, AttributeEventName((int)id, key), attributeWatcher, lua.Ref(LuaRegistry.Index))
                    {
                        Internal = true
                    });
                    lua.PushInteger(attributeHandlerId);
                    return 1;
                case "anim":
                    // op, arguments (see LuaScriptRuntime.Animation).
                    return AnimationRequest(lua, target!, key);
                case "onanim":
                    // f -> the connection's number, or false, message.
                    return AnimationFinishedConnection(lua, (int)id);
                case "ontrigger":
                    // entered (3), f (4) -> the connection's number, or false, message.
                    return TriggerConnection(lua, target!, lua.ToBoolean(3));
                case "teleport":
                    // x, y, z (3..5), hasRotation, rx, ry, rz (6..9), keepVelocity, rotateVelocity (10, 11); finite (checked in Lua).
                    error = GameplayScene is { } teleporter
                        ? teleporter.Teleport(target!, new Vector3((float)lua.ToNumber(3), (float)lua.ToNumber(4), (float)lua.ToNumber(5)),
                            lua.ToBoolean(6) ? new Vector3((float)lua.ToNumber(7), (float)lua.ToNumber(8), (float)lua.ToNumber(9)) : null,
                            lua.ToBoolean(10), lua.ToBoolean(11))
                        : "Teleport is only available while the game plays";
                    break;
                case "bones" or "hasbone":
                    if (GameplayScene?.BonesOf(target!, out var boneProblem) is not { } bones)
                    {
                        if (request == "hasbone")
                        {
                            lua.PushBoolean(true);
                            lua.PushBoolean(false);
                            return 2;
                        }

                        lua.PushBoolean(true);
                        lua.CreateTable(0, 0);
                        return 2;
                    }

                    lua.PushBoolean(true);
                    if (request == "hasbone")
                    {
                        lua.PushBoolean(bones.Contains(key, StringComparer.Ordinal));
                        return 2;
                    }

                    PushNames(lua, bones);
                    return 2;
                case "bone":
                    // name (3) -> true, position x, y, z, rotation x, y, z; or false, message.
                    if (GameplayScene is not { } posed)
                    {
                        error = "bones are only available while the game plays";
                        break;
                    }

                    if (posed.BoneTransform(target!, key, out var bonePosition, out var boneRotation) is { } boneError)
                    {
                        error = boneError;
                        break;
                    }

                    lua.PushBoolean(true);
                    PushVector(lua, bonePosition);
                    PushVector(lua, boneRotation);
                    return 7;
                case "wake":
                    error = target is IScriptPart woken ? woken.Wake() : ScriptObjectAccess.NotSimulated(target!);
                    break;
                case "bounds":
                    if (target is not IScriptPart bounded)
                    {
                        error = $"'{target!.Name}' has no bounds";
                        break;
                    }

                    var (min, max) = bounded.Bounds;
                    lua.PushBoolean(true);
                    lua.PushNumber(min.X);
                    lua.PushNumber(min.Y);
                    lua.PushNumber(min.Z);
                    lua.PushNumber(max.X);
                    lua.PushNumber(max.Y);
                    lua.PushNumber(max.Z);
                    return 7;
                case "tag":
                    return PartDataRequest(lua, target!, key, isTag: true);
                case "attr":
                    return PartDataRequest(lua, target!, key, isTag: false);
                case "clone":
                    IScriptObject? clone = null;
                    error = SceneHost is { } cloneHost
                        ? cloneHost.Clone(target!, lua.Type(3) == LuaType.String ? lua.ToString(3, false) : null, out clone)
                        : "Clone is not available here (no running scene)";
                    if (error is null && clone is not null && IdOfObject(clone) is > 0 and var cloneId)
                    {
                        lua.PushInteger(cloneId);
                        return 1;
                    }

                    error ??= "Clone failed";
                    break;
                case "attach":
                    // A path inside Assets/Scripts ("Spin.lua", "Combat/Damage.lua" or "Assets/Scripts/Spin.lua").
                    error = AttachScript(lua, target!, out var handle);
                    if (error is null)
                    {
                        lua.PushInteger(handle);
                        return 1;
                    }

                    break;
                case "destroy":
                    error = SceneHost is { } sceneHost ? sceneHost.Destroy(target!) : "Destroy is not available here (no running scene)";
                    break;
                case "setparent":
                    // parent id (0: let go), hasPosition, x, y, z, hasRotation, x, y, z (finite, checked in Lua).
                    if (target is not IScriptPart child)
                    {
                        error = $"'{target!.Name}' cannot follow a parent";
                        break;
                    }

                    // Read as a number: the shared key read above (ToString) has turned that slot into a string.
                    var parentId = (long)lua.ToNumber(3);
                    var parentTarget = parentId > 0 && parentId <= _objects.Count ? _objects[(int)parentId - 1] : null;
                    if (parentId != 0 && parentTarget is not { Exists: true })
                    {
                        error = "SetParent: that parent no longer exists";
                        break;
                    }

                    Vector3? offsetPosition = lua.ToBoolean(4) ? new Vector3((float)lua.ToNumber(5), (float)lua.ToNumber(6), (float)lua.ToNumber(7)) : null;
                    Vector3? offsetRotation = lua.ToBoolean(8) ? new Vector3((float)lua.ToNumber(9), (float)lua.ToNumber(10), (float)lua.ToNumber(11)) : null;
                    var bone = lua.Type(12) == LuaType.String ? lua.ToString(12, false) : null;
                    error = child.SetParent(parentTarget, offsetPosition, offsetRotation, bone);
                    break;
                case "parentof":
                    lua.PushBoolean(true);
                    if ((target as IScriptPart)?.Parent is { } parentObject && IdOfObject(parentObject) is > 0 and var parentObjectId)
                    {
                        lua.PushInteger(parentObjectId);
                    }
                    else
                    {
                        lua.PushNil();
                    }

                    return 2;
                case "children":
                    lua.PushBoolean(true);
                    PushIdTable(lua, ((target as IScriptPart)?.Children ?? []).Select(IdOfObject).Where(childId => childId > 0).ToList());
                    return 2;
                case "ground":
                    // maxDistance (>= 0, checked in Lua) -> true, false | true, true, object id (0: environment), gap, point, normal.
                    lua.PushBoolean(true);
                    if (target is not IScriptPart standing || !standing.TryGetGround((float)lua.ToNumber(3), out var ground))
                    {
                        lua.PushBoolean(false);
                        return 2;
                    }

                    lua.PushBoolean(true);
                    lua.PushInteger(ground.Object is null ? 0 : IdOfObject(ground.Object));
                    foreach (var number in new[] { ground.Distance, ground.Point.X, ground.Point.Y, ground.Point.Z, ground.Normal.X, ground.Normal.Y, ground.Normal.Z })
                    {
                        lua.PushNumber(number);
                    }

                    return 10;
                case "getscript":
                    // name -> true, the script's exports table | true, nil, why not.
                    return PushScriptExports(lua, target!, lua.ToString(3, false) ?? string.Empty);
                case "ondestroyed":
                    // f -> the connection's number (an Events connection: Connected / Disconnect), or false, message.
                    if (_running is not { } watcher || lua.Type(3) != LuaType.Function)
                    {
                        error = "OnDestroyed is not available here";
                        break;
                    }

                    if (_eventHandlers.Count(handler => ReferenceEquals(handler.Owner, watcher)) >= MaxHandlersPerScript)
                    {
                        error = $"this script already has {MaxHandlersPerScript:N0} Events.On / OnDestroyed connections: disconnect the ones it no longer needs";
                        break;
                    }

                    lua.PushCopy(3);
                    var watchId = ++_nextHandlerId;
                    _eventHandlers.Add(new EventHandlerEntry(watchId, DestroyedEventName((int)id), watcher, lua.Ref(LuaRegistry.Index)) { Internal = true, Once = true });
                    lua.PushInteger(watchId);
                    return 1;
                default:
                    error = "unknown Scene request";
                    break;
            }

            if (error is null)
            {
                lua.PushBoolean(true);
                return 1;
            }

            lua.PushBoolean(false);
            lua.PushString(error);
            return 2;
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] Scene request failed: {ex.Message}");
            lua.PushBoolean(false);
            lua.PushString("the Scene request failed");
            return 2;
        }
    }

    // Timer's host side: hostTimer(op, ...) from the sandbox's Timer wrappers (arguments checked there). Never throws and
    // never raises a Lua error (see HostGui). Answers: after / every (seconds, function) -> the timer's number, or false,
    // message; active (number) -> whether it is still running; cancel (number) -> nothing (idempotent).
    private int HostTimer(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            switch (request)
            {
                case "after" or "every" when _running is null || lua.Type(3) != LuaType.Function:
                    lua.PushBoolean(false);
                    lua.PushString("Timer is not available here");
                    return 2;
                case "after" or "every" when _running!.TimerCount >= MaxTimersPerScript:
                    lua.PushBoolean(false);
                    lua.PushString($"this script already has {MaxTimersPerScript:N0} timers running: cancel the ones it no longer needs");
                    return 2;
                case "after" or "every":
                    var owner = _running!;
                    var seconds = lua.ToNumber(2);
                    lua.PushCopy(3);
                    var timer = new ScriptTimer(++_nextTimerId, owner, lua.Ref(LuaRegistry.Index), ElapsedTime + seconds,
                        request == "every" ? seconds : 0d, _frame) { Delay = seconds };
                    _timers.Add(timer.Id, timer);
                    _timerList.Add(timer);
                    owner.TimerCount++;
                    lua.PushInteger(timer.Id);
                    return 1;
                case "active":
                    lua.PushBoolean(lua.Type(2) == LuaType.Number && lua.ToInteger(2) is var activeId && activeId is > 0 and <= int.MaxValue
                        && _timers.TryGetValue((int)activeId, out var active) && active.Active);
                    return 1;
                case "cancel":
                    if (lua.Type(2) == LuaType.Number && lua.ToInteger(2) is var cancelId && cancelId is > 0 and <= int.MaxValue
                        && _timers.TryGetValue((int)cancelId, out var cancelled))
                    {
                        StopTimer(cancelled, releaseFunction: true);
                    }

                    return 0;
                case "remaining" or "paused" or "pause" or "resume" or "restart":
                    // On a timer that ended (ran out, cancelled, its script gone): remaining 0, not paused, and
                    // pause / resume / restart do nothing (restart answers false).
                    var running = lua.Type(2) == LuaType.Number && lua.ToInteger(2) is var runningId && runningId is > 0 and <= int.MaxValue
                        && _timers.TryGetValue((int)runningId, out var found) && found.Active ? found : null;
                    switch (request)
                    {
                        case "remaining":
                            lua.PushNumber(running is null ? 0d : running.Paused ? running.PausedRemaining : global::System.Math.Max(0d, running.Due - ElapsedTime));
                            return 1;
                        case "paused":
                            lua.PushBoolean(running is { Paused: true });
                            return 1;
                        case "pause" when running is { Paused: false }:
                            running.PausedRemaining = global::System.Math.Max(0d, running.Due - ElapsedTime);
                            running.Paused = true;
                            return 0;
                        case "resume" when running is { Paused: true }:
                            running.Due = ElapsedTime + running.PausedRemaining;
                            running.Paused = false;
                            return 0;
                        case "restart":
                            if (running is not null)
                            {
                                // A full delay (After) or interval (Every) from now, running; never in this frame.
                                running.Due = ElapsedTime + running.Delay;
                                running.Paused = false;
                                running.Frame = _frame;
                                if (!_timerList.Contains(running))
                                {
                                    _timerList.Add(running);
                                }
                            }

                            lua.PushBoolean(running is not null);
                            return 1;
                        default:
                            return 0;
                    }
                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] Timer request failed: {ex.Message}");
            lua.PushBoolean(false);
            lua.PushString("the Timer request failed");
            return 2;
        }
    }

    // Physics' host side, from the sandbox's Physics wrapper (finite numbers, non-zero directions and positive sizes are
    // checked there). Never throws and never raises a Lua error. Answers:
    // raycast (ox, oy, oz, dx, dy, dz, maxDistance, ignoreId) / spherecast (ox, oy, oz, dx, dy, dz, radius, maxDistance,
    // ignoreId): false (nothing hit); true, object id (0: the environment), distance, point x, y, z, normal x, y, z.
    // raycastall (as raycast): a flat table of those 8 values per hit, nearest first.
    // overlapsphere (x, y, z, radius, ignoreId) / overlapbox (x, y, z, sx, sy, sz, rx, ry, rz, ignoreId): a table of
    // object ids (scene order), whether the environment is reached.
    // gravity: x, y, z. Anything unusable: nil, message.
    private int HostPhysics(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            switch (request)
            {
                case "gravity":
                    var gravity = SceneHost?.Gravity ?? Vector3.Zero;
                    lua.PushNumber(gravity.X);
                    lua.PushNumber(gravity.Y);
                    lua.PushNumber(gravity.Z);
                    return 3;
                case "setgravity":
                    // x, y, z (finite, checked in Lua) -> true, or nil, message.
                    var gravityError = SceneHost is { } gravityHost
                        ? gravityHost.SetGravity(new Vector3((float)lua.ToNumber(2), (float)lua.ToNumber(3), (float)lua.ToNumber(4)))
                        : "Physics.Gravity can only be set while physics runs (Play)";
                    if (gravityError is null)
                    {
                        lua.PushBoolean(true);
                        return 1;
                    }

                    lua.PushNil();
                    lua.PushString(gravityError);
                    return 2;
                case "groupset":
                    // a, b (group names), collide -> true, or nil, message.
                    var groupError = SceneHost?.SetGroupsCollide(lua.ToString(2, false) ?? "Default", lua.ToString(3, false) ?? "Default", lua.ToBoolean(4))
                        ?? (SceneHost is null ? "collision groups only exist while physics runs (Play)" : null);
                    if (groupError is null)
                    {
                        lua.PushBoolean(true);
                        return 1;
                    }

                    lua.PushNil();
                    lua.PushString(groupError);
                    return 2;
                case "groupget":
                    lua.PushBoolean(SceneHost?.GroupsCollide(lua.ToString(2, false) ?? "Default", lua.ToString(3, false) ?? "Default") ?? true);
                    return 1;
                case "groups":
                    var groupNames = SceneHost?.CollisionGroups ?? ["Default"];
                    lua.CreateTable(groupNames.Count, 0);
                    for (var i = 0; i < groupNames.Count; i++)
                    {
                        lua.PushString(groupNames[i]);
                        lua.RawSetInteger(-2, i + 1);
                    }

                    return 1;
                case "joint":
                    // kind, a id, b id (0: the world), point x, y, z, axis x, y, z -> the joint's number, or nil, message.
                    var jointError = "joints only exist while physics runs (Play)";
                    var jointId = 0;
                    if (SceneHost is { } jointHost && ObjectAt(lua, 3) is { } jointA)
                    {
                        var jointB = ObjectAt(lua, 4);
                        jointError = lua.ToInteger(4) != 0 && jointB is null
                            ? "that object no longer exists"
                            : jointHost.CreateJoint(lua.ToString(2, false) ?? string.Empty, jointA, jointB,
                                new Vector3((float)lua.ToNumber(5), (float)lua.ToNumber(6), (float)lua.ToNumber(7)),
                                new Vector3((float)lua.ToNumber(8), (float)lua.ToNumber(9), (float)lua.ToNumber(10)), out jointId);
                    }
                    else if (SceneHost is not null)
                    {
                        jointError = "that object no longer exists";
                    }

                    if (jointError is null)
                    {
                        lua.PushInteger(jointId);
                        return 1;
                    }

                    lua.PushNil();
                    lua.PushString(jointError);
                    return 2;
                case "jget":
                    // id, key -> true, value, or nil, message.
                    if (SceneHost is not { } readHost)
                    {
                        lua.PushNil();
                        lua.PushString("that joint no longer exists");
                        return 2;
                    }

                    var jointValue = readHost.JointGet((int)lua.ToInteger(2), lua.ToString(3, false) ?? string.Empty, out var readError);
                    if (readError is not null)
                    {
                        lua.PushNil();
                        lua.PushString(readError);
                        return 2;
                    }

                    lua.PushBoolean(true);
                    if (jointValue is IScriptObject jointObject && IdOfObject(jointObject) is > 0 and var jointObjectId)
                    {
                        lua.PushInteger(jointObjectId);
                    }
                    else
                    {
                        PushValue(lua, jointValue is IScriptObject ? null : jointValue);
                    }

                    return 2;
                case "jset":
                    var writeError = SceneHost?.JointSet((int)lua.ToInteger(2), lua.ToString(3, false) ?? string.Empty, ValueAt(lua, 4))
                        ?? (SceneHost is null ? "that joint no longer exists" : null);
                    if (writeError is null)
                    {
                        lua.PushBoolean(true);
                        return 1;
                    }

                    lua.PushNil();
                    lua.PushString(writeError);
                    return 2;
                case "jdestroy":
                    SceneHost?.DestroyJoint((int)lua.ToInteger(2));
                    return 0;
                case "overlapsphere" or "overlapbox":
                    var box = request == "overlapbox";
                    var center = new Vector3((float)lua.ToNumber(2), (float)lua.ToNumber(3), (float)lua.ToNumber(4));
                    var size = box ? new Vector3((float)lua.ToNumber(5), (float)lua.ToNumber(6), (float)lua.ToNumber(7)) : new Vector3((float)lua.ToNumber(5), 0f, 0f);
                    var rotation = box ? new Vector3((float)lua.ToNumber(8), (float)lua.ToNumber(9), (float)lua.ToNumber(10)) : Vector3.Zero;
                    var overlapFilter = FilterAt(lua, box ? 11 : 6);
                    var (objects, environment) = SceneHost?.Overlap(box, center, size, rotation, overlapFilter, MaxQueryResults) ?? ([], false);
                    PushIdTable(lua, objects.Select(IdOfObject).Where(id => id > 0).ToList());
                    lua.PushBoolean(environment);
                    return 2;
                case "raycast" or "raycastall" or "spherecast":
                    break;
                default:
                    lua.PushNil();
                    lua.PushString("unknown Physics request");
                    return 2;
            }

            var functionName = request switch { "raycastall" => "Physics.RaycastAll", "spherecast" => "Physics.SphereCast", _ => "Physics.Raycast" };
            if (!TryDirection(lua.ToNumber(5), lua.ToNumber(6), lua.ToNumber(7), out var direction))
            {
                lua.PushNil();
                lua.PushString($"{functionName} expects a direction that is not zero");
                return 2;
            }

            var origin = new Vector3((float)lua.ToNumber(2), (float)lua.ToNumber(3), (float)lua.ToNumber(4));
            if (request == "raycastall")
            {
                var hits = SceneHost?.RaycastAll(origin, direction, (float)lua.ToNumber(8), FilterAt(lua, 9), MaxQueryResults) ?? [];
                lua.CreateTable(hits.Count * 8, 0);
                var index = 0;
                foreach (var found in hits)
                {
                    lua.PushInteger(found.Object is null ? 0 : IdOfObject(found.Object));
                    lua.RawSetInteger(-2, ++index);
                    foreach (var number in new[] { found.Distance, found.Point.X, found.Point.Y, found.Point.Z, found.Normal.X, found.Normal.Y, found.Normal.Z })
                    {
                        lua.PushNumber(number);
                        lua.RawSetInteger(-2, ++index);
                    }
                }

                return 1;
            }

            var hit = default(ScriptRaycastHit);
            var hitSomething = request == "spherecast"
                ? SceneHost is { } sweepHost && sweepHost.SphereCast(origin, direction, (float)lua.ToNumber(8), (float)lua.ToNumber(9), FilterAt(lua, 10), out hit)
                : SceneHost is { } rayHost && rayHost.Raycast(origin, direction, (float)lua.ToNumber(8), FilterAt(lua, 9), out hit);
            if (!hitSomething)
            {
                lua.PushBoolean(false);
                return 1;
            }

            lua.PushBoolean(true);
            lua.PushInteger(hit.Object is null ? 0 : IdOfObject(hit.Object));
            lua.PushNumber(hit.Distance);
            lua.PushNumber(hit.Point.X);
            lua.PushNumber(hit.Point.Y);
            lua.PushNumber(hit.Point.Z);
            lua.PushNumber(hit.Normal.X);
            lua.PushNumber(hit.Normal.Y);
            lua.PushNumber(hit.Normal.Z);
            return 9;
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] Physics request failed: {ex.Message}");
            lua.PushNil();
            lua.PushString("the Physics request failed");
            return 2;
        }
    }

    // A direction of length 1 from three finite numbers (very small or large ones are scaled first); false for zero.
    private static bool TryDirection(double dx, double dy, double dz, out Vector3 direction)
    {
        direction = default;
        var length = global::System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 0d) || !double.IsFinite(length))
        {
            // Too small (or large) to give a direction once squared: scale it first.
            var largest = global::System.Math.Max(global::System.Math.Abs(dx), global::System.Math.Max(global::System.Math.Abs(dy), global::System.Math.Abs(dz)));
            if (largest > 0d && double.IsFinite(largest))
            {
                (dx, dy, dz) = (dx / largest, dy / largest, dz / largest);
                length = global::System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        if (!(length > 0d) || !double.IsFinite(length))
        {
            return false;
        }

        direction = new Vector3((float)(dx / length), (float)(dy / length), (float)(dz / length));
        return true;
    }

    // object:GetScript(name): the table that script's top-level chunk returned (its exports), found among the scripts
    // running on the object (the host matches the name). Pushes true, table; or true, nil, why not.
    private int PushScriptExports(LuaState lua, IScriptObject target, string name)
    {
        lua.PushBoolean(true);
        var (instance, problem) = SceneHost?.FindScript(target, name) ?? (null, "GetScript is not available here (no running scene)");
        if (instance is { ExportsReference: not LuaScriptInstance.NoReference } && IsLive(instance))
        {
            lua.RawGetInteger(LuaRegistry.Index, instance.ExportsReference);
            return 2;
        }

        lua.PushNil();
        lua.PushString(instance is null
            ? problem ?? $"'{target.Name}' has no running script '{name}'"
            : !IsLive(instance)
                ? $"the script '{name}' on '{target.Name}' is not running (it failed or was stopped)"
                : $"the script '{name}' on '{target.Name}' exports nothing: end it with return and a table (return M)");
        return 3;
    }

    // A query's filter at that stack index, as the sandbox builds it: nil (none), an object Id (that object ignored), or
    // a plain table { ids = {...}, ignoreTags = {...}, onlyTags = {...}, ignoreTriggers = bool, ignoreEnvironment = bool }
    // (names and types checked in Lua; the sandbox's own table, so no metamethods run).
    private ScriptQueryFilter? FilterAt(LuaState lua, int index)
    {
        switch (lua.Type(index))
        {
            case LuaType.Number:
                var single = ObjectAt(lua, index);
                if (single is null)
                {
                    return null;
                }

                var only = new ScriptQueryFilter();
                only.Ignore.Add(single);
                return only;
            case LuaType.Table:
                break;
            default:
                return null;
        }

        var filter = new ScriptQueryFilter();
        var table = lua.AbsIndex(index);
        if (lua.GetField(table, "ids") == LuaType.Table)
        {
            var count = (int)global::System.Math.Min(lua.RawLen(-1), MaxQueryResults);
            for (var i = 1; i <= count; i++)
            {
                lua.RawGetInteger(-1, i);
                var id = lua.Type(-1) == LuaType.Number ? lua.ToInteger(-1) : 0;
                if (id > 0 && id <= _objects.Count)
                {
                    filter.Ignore.Add(_objects[(int)id - 1]);
                }

                lua.Pop(1);
            }
        }

        lua.Pop(1);
        ReadStrings(lua, table, "ignoreTags", filter.IgnoreTags);
        ReadStrings(lua, table, "onlyTags", filter.OnlyTags);
        lua.GetField(table, "ignoreTriggers");
        filter.IgnoreTriggers = lua.ToBoolean(-1);
        lua.Pop(1);
        lua.GetField(table, "ignoreEnvironment");
        filter.IgnoreEnvironment = lua.ToBoolean(-1);
        lua.Pop(1);
        if (lua.GetField(table, "collisionGroup") == LuaType.String)
        {
            filter.CollisionGroup = lua.ToString(-1, false);
        }

        lua.Pop(1);
        return filter;

        static void ReadStrings(LuaState lua, int table, string key, List<string> into)
        {
            if (lua.GetField(table, key) == LuaType.Table)
            {
                var count = (int)global::System.Math.Min(lua.RawLen(-1), ScriptPartData.MaxTags * 4);
                for (var i = 1; i <= count; i++)
                {
                    if (lua.RawGetInteger(-1, i) == LuaType.String && lua.ToString(-1, false) is { } text)
                    {
                        into.Add(text);
                    }

                    lua.Pop(1);
                }
            }

            lua.Pop(1);
        }
    }

    // The scene object whose Id is at that stack index (nil, 0 or an unknown Id: none).
    private IScriptObject? ObjectAt(LuaState lua, int index)
    {
        var id = lua.Type(index) == LuaType.Number ? lua.ToInteger(index) : 0;
        return id > 0 && id <= _objects.Count ? _objects[(int)id - 1] : null;
    }

    // Events' host side, from the sandbox's Events wrapper (names and argument types checked there). Never throws and
    // never raises a Lua error. on (name, f) -> connection number, or false, message; off (number) -> nothing;
    // connected (number) -> boolean; emit (name, tags, values...) -> true, or false, message. tags has one letter per
    // argument: 'v' a plain value (nil, boolean, number, string), 'o' a scene object Id, 'g' a GUI element (Id, type,
    // name: three values), 't' the sandbox's snapshot of a table.
    private int HostEvents(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            switch (request)
            {
                case "on":
                    if (_running is not { } owner || lua.Type(3) != LuaType.Function)
                    {
                        lua.PushBoolean(false);
                        lua.PushString("Events.On is not available here");
                        return 2;
                    }

                    if (_eventHandlers.Count(handler => ReferenceEquals(handler.Owner, owner)) >= MaxHandlersPerScript)
                    {
                        lua.PushBoolean(false);
                        lua.PushString($"this script already has {MaxHandlersPerScript:N0} Events.On connections: disconnect the ones it no longer needs");
                        return 2;
                    }

                    lua.PushCopy(3);
                    var handlerId = ++_nextHandlerId;
                    _eventHandlers.Add(new EventHandlerEntry(handlerId, lua.ToString(2, false) ?? string.Empty, owner, lua.Ref(LuaRegistry.Index)));
                    lua.PushInteger(handlerId);
                    return 1;
                case "off" or "connected":
                    var connection = _eventHandlers.Find(handler => handler.Id == lua.ToInteger(2) && handler.Active);
                    if (request == "connected")
                    {
                        lua.PushBoolean(connection is not null);
                        return 1;
                    }

                    if (connection is not null)
                    {
                        connection.Active = false;
                        Unref(connection.FunctionReference);
                        _eventHandlers.Remove(connection);
                    }

                    return 0;
                case "emit":
                    if (_eventQueue.Count >= MaxQueuedEvents)
                    {
                        lua.PushBoolean(false);
                        lua.PushString($"Events.Emit: {MaxQueuedEvents:N0} events are already waiting for delivery this frame");
                        return 2;
                    }

                    var tags = lua.ToString(3, false) ?? string.Empty;
                    var arguments = new object?[tags.Length];
                    var index = 4;
                    for (var i = 0; i < tags.Length; i++)
                    {
                        switch (tags[i])
                        {
                            case 'o':
                                arguments[i] = new EventObject((int)global::System.Math.Clamp(lua.ToInteger(index++), 0, int.MaxValue));
                                break;
                            case 'g':
                                arguments[i] = new EventElement((int)global::System.Math.Clamp(lua.ToInteger(index), 0, int.MaxValue),
                                    lua.ToString(index + 1, false) ?? "Frame", lua.ToString(index + 2, false) ?? string.Empty);
                                index += 3;
                                break;
                            case 't' when lua.Type(index) == LuaType.Table:
                                // The sandbox's own snapshot (no script holds it): kept until delivered.
                                lua.PushCopy(index++);
                                arguments[i] = new EventTable(lua.Ref(LuaRegistry.Index));
                                break;
                            default:
                                arguments[i] = lua.Type(index) switch
                                {
                                    LuaType.Boolean => lua.ToBoolean(index),
                                    LuaType.Number => lua.IsInteger(index) ? (object)lua.ToInteger(index) : lua.ToNumber(index),
                                    LuaType.String => lua.ToString(index, false),
                                    _ => null
                                };
                                index++;
                                break;
                        }
                    }

                    _eventQueue.Add(new QueuedEvent(lua.ToString(2, false) ?? string.Empty, arguments, tags));
                    lua.PushBoolean(true);
                    return 1;
                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] Events request failed: {ex.Message}");
            lua.PushBoolean(false);
            lua.PushString("the Events request failed");
            return 2;
        }
    }

    // Camera's and Atmosphere's host side, from the sandbox's wrappers (types and finite numbers checked there). Never
    // throws and never raises a Lua error. Answers false, message when unusable (no Play view). Camera: cam (key) ->
    // true, values (Position: x, y, z; Rotation: pitch, yaw, 0; FieldOfView; Forward / Right / Up: x, y, z;
    // ViewportWidth / ViewportHeight); camset (key, values) / lookat (x, y, z) -> true; screen (x, y, z) -> true, sx, sy,
    // onScreen; ray (sx, sy) -> true, origin x, y, z, direction x, y, z. Atmosphere: atmo (key) -> true, value;
    // atmoset (key, value) -> true. Graphics: gfx (key) -> true, value; gfxset (key, value) -> true.
    private int HostView(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            var key = lua.ToString(2, false) ?? string.Empty;
            string? error;
            if (request is "gfx" or "gfxset")
            {
                if (_options.Graphics is not { } graphics)
                {
                    error = "Graphics is not available here (no Play session)";
                }
                else if (request == "gfx")
                {
                    var setting = ScriptGraphics.GetSetting(graphics.Settings, key, out error);
                    if (error is null)
                    {
                        lua.PushBoolean(true);
                        PushValue(lua, setting);
                        return 2;
                    }
                }
                else
                {
                    error = ScriptGraphics.SetSetting(graphics, key, ValueAt(lua, 3));
                }
            }
            else if (request is "atmo" or "atmoset")
            {
                if (_options.Atmosphere is not { } atmosphere)
                {
                    error = "Atmosphere is not available here (no Play session)";
                }
                else if (request == "atmo")
                {
                    lua.PushBoolean(true);
                    PushValue(lua, ScriptAtmosphere.Get(atmosphere.Settings, key));
                    return 2;
                }
                else
                {
                    error = ScriptAtmosphere.Set(atmosphere, key, ValueAt(lua, 3));
                }
            }
            else if (_options.Camera is not { } camera)
            {
                error = "Camera is not available here (no Play view)";
            }
            else
            {
                switch (request)
                {
                    case "cam":
                        lua.PushBoolean(true);
                        switch (key)
                        {
                            case "Position":
                                return 1 + PushVector(lua, camera.Position);
                            case "Rotation":
                                return 1 + PushVector(lua, new Vector3(camera.Pitch, camera.Yaw, 0f));
                            case "Forward":
                                return 1 + PushVector(lua, camera.Forward);
                            case "Right":
                                return 1 + PushVector(lua, camera.Right);
                            case "Up":
                                return 1 + PushVector(lua, camera.Up);
                            case "FieldOfView":
                                lua.PushNumber(camera.FieldOfView);
                                return 2;
                            case "ViewportWidth":
                                lua.PushInteger(camera.ViewportWidth);
                                return 2;
                            default:
                                lua.PushInteger(camera.ViewportHeight);
                                return 2;
                        }

                    case "camset":
                        error = ScriptCamera.Set(camera, key, lua.ToNumber(3), lua.ToNumber(4), lua.ToNumber(5));
                        break;
                    case "lookat":
                        error = ScriptCamera.LookAt(camera, new Vector3((float)lua.ToNumber(2), (float)lua.ToNumber(3), (float)lua.ToNumber(4)));
                        break;
                    case "screen":
                        var onScreen = camera.WorldToScreen(new Vector3((float)lua.ToNumber(2), (float)lua.ToNumber(3), (float)lua.ToNumber(4)), out var sx, out var sy);
                        lua.PushBoolean(true);
                        lua.PushNumber(onScreen ? sx : 0d);
                        lua.PushNumber(onScreen ? sy : 0d);
                        lua.PushBoolean(onScreen && sx >= 0f && sy >= 0f && sx <= camera.ViewportWidth && sy <= camera.ViewportHeight);
                        lua.PushBoolean(onScreen);
                        return 5;
                    case "ray":
                        var (origin, direction) = camera.ScreenPointToRay((float)lua.ToNumber(2), (float)lua.ToNumber(3));
                        lua.PushBoolean(true);
                        PushVector(lua, origin);
                        PushVector(lua, direction);
                        return 7;
                    case "mouselock":
                        // Input.SetMouseLocked(locked).
                        camera.MouseLocked = lua.ToBoolean(3);
                        error = null;
                        break;
                    case "mouselocked":
                        lua.PushBoolean(true);
                        lua.PushBoolean(camera.MouseLocked);
                        return 2;
                    default:
                        error = "unknown Camera request";
                        break;
                }
            }

            if (error is null)
            {
                lua.PushBoolean(true);
                return 1;
            }

            lua.PushBoolean(false);
            lua.PushString(error);
            return 2;
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] View request failed: {ex.Message}");
            lua.PushBoolean(false);
            lua.PushString("the Camera / Atmosphere request failed");
            return 2;
        }
    }

    private static int PushVector(LuaState lua, Vector3 value)
    {
        lua.PushNumber(value.X);
        lua.PushNumber(value.Y);
        lua.PushNumber(value.Z);
        return 3;
    }

    // require()'s file access, from the sandbox's package searcher: hostModule("exists", file) -> whether that module
    // file can be read; hostModule("read", file) -> its source and its asset path (for messages), or nil, message.
    // file is a name from package.path ("./Combat/Damage.lua"), relative to ModuleRoot. Only .lua files inside
    // ModuleRoot are ever looked at: no rooted paths, drive letters, "..", backslashes or symbolic links, whatever
    // package.path says. Each file is read once per runtime. Never throws and never raises a Lua error.
    private int HostModule(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            var request = lua.ToString(1, false);
            var file = lua.Type(2) == LuaType.String ? lua.ToString(2, false) : null;
            if (request == "builtin")
            {
                // The optional utilities: require("Wisko.Name") -> the engine's own module source and its chunk name.
                if (file is not null && LuaSandbox.TryGetBuiltinModule(file, out var builtin))
                {
                    lua.PushBuffer(builtin);
                    lua.PushString(file);
                    return 2;
                }

                return 0;
            }

            var resolved = TryResolveModule(file, out var fullPath, out var assetPath, out var refusal);
            if (request == "exists")
            {
                lua.PushBoolean(resolved && File.Exists(fullPath));
                return 1;
            }

            if (request != "read")
            {
                return 0;
            }

            if (!resolved)
            {
                lua.PushNil();
                lua.PushString(refusal);
                return 2;
            }

            if (!_moduleSources.TryGetValue(fullPath, out var source))
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    lua.PushNil();
                    lua.PushString("the file no longer exists");
                    return 2;
                }

                if (info.Length > MaxModuleBytes)
                {
                    lua.PushNil();
                    lua.PushString($"the file is larger than {MaxModuleBytes / (1024 * 1024)} MB");
                    return 2;
                }

                source = File.ReadAllBytes(fullPath);
                if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
                {
                    source = source[3..];
                }

                _moduleSources[fullPath] = source;
            }

            lua.PushBuffer(source);
            lua.PushString(assetPath);
            return 2;
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            lua.PushNil();
            lua.PushString($"could not read the file ({ex.Message})");
            return 2;
        }
    }

    // A module file name from package.path, as a full path inside ModuleRoot and an asset path ("Assets/Scripts/...").
    private bool TryResolveModule(string? file, out string fullPath, out string assetPath, out string refusal)
    {
        fullPath = assetPath = string.Empty;
        refusal = "modules are .lua files inside Assets/Scripts";
        if (_options.ModuleRoot is not { } moduleRoot || string.IsNullOrEmpty(file))
        {
            refusal = "this world has no project folder to load modules from";
            return false;
        }

        var path = file;
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length == 0 || path.Length > 1024 || path.IndexOfAny(['\\', ':', '\0']) >= 0 || path.StartsWith('/')
            || Path.IsPathRooted(path) || !path.EndsWith(Assets.ProjectAssetPaths.ScriptExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
        {
            return false;
        }

        var root = Path.GetFullPath(moduleRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!Assets.ProjectAssetPaths.IsUnder(candidate, root))
        {
            return false;
        }

        // A symbolic link anywhere below the folder could lead out of it.
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.Exists && entry.LinkTarget is not null)
            {
                return false;
            }
        }

        fullPath = candidate;
        assetPath = Assets.ProjectAssetPaths.ScriptsFolder + "/" + string.Join('/', segments);
        return true;
    }

    // collectgarbage("collect") / ("step", n) from a script (options checked by the sandbox): the collection runs, and
    // its cost is charged to the running callback's instruction budget. Answers: collect -> 0, step -> whether a cycle
    // finished. Never throws and never raises a Lua error.
    private int HostGc(IntPtr state)
    {
        var lua = ThreadOf(state);
        if (lua is null)
        {
            return 0;
        }

        try
        {
            if (lua.ToString(1, false) == "step")
            {
                _instructionsUsed += CollectionStepCost;
                var size = lua.Type(2) == LuaType.Number ? (int)global::System.Math.Clamp(lua.ToNumber(2), 0d, 1_000_000d) : 0;
                lua.PushBoolean(lua.GarbageCollector(LuaGC.Step, size) != 0);
                return 1;
            }

            _instructionsUsed += FullCollectionCost;
            lua.GarbageCollector(LuaGC.Collect, 0);
            lua.PushInteger(0);
            return 1;
        }
        catch (Exception ex)
        {
            // Never let an exception reach native code.
            Console.WriteLine($"[Lua] Collection request failed: {ex.Message}");
            lua.PushInteger(0);
            return 1;
        }
    }

    // Ends a timer (its handle reads Active = false from now on). Its function is let go unless the caller still runs it.
    private void StopTimer(ScriptTimer timer, bool releaseFunction)
    {
        if (!timer.Active)
        {
            return;
        }

        timer.Active = false;
        _timers.Remove(timer.Id);
        timer.Owner.TimerCount--;
        if (releaseFunction)
        {
            Unref(timer.FunctionReference);
        }
    }

    // A script's timers go with it (faulted, released, its object destroyed).
    private void RemoveTimers(LuaScriptInstance instance)
    {
        if (instance.TimerCount == 0)
        {
            return;
        }

        foreach (var timer in _timerList)
        {
            if (timer.Active && ReferenceEquals(timer.Owner, instance))
            {
                StopTimer(timer, releaseFunction: true);
            }
        }
    }

    // Lets go of a registry reference now, or once the running script callback returns.
    // A script stopped by the memory limit never runs again: what only it holds (its globals, its functions) is let go
    // and collected at once, so the other scripts get the memory back. Its exports stay for scripts that already have
    // them (GetScript).
    private void ReleaseScriptMemory(LuaScriptInstance instance)
    {
        foreach (var reference in new[] { instance.EnvironmentReference, instance.StartReference, instance.UpdateReference,
                     instance.LateUpdateReference, instance.DeliverReference, instance.DeliverEventReference })
        {
            if (reference != LuaScriptInstance.NoReference)
            {
                Unref(reference);
            }
        }

        instance.EnvironmentReference = instance.StartReference = instance.UpdateReference = instance.LateUpdateReference =
            instance.DeliverReference = instance.DeliverEventReference = LuaScriptInstance.NoReference;
        for (var i = 0; i < instance.HandlerReferences.Length; i++)
        {
            if (instance.HandlerReferences[i] != LuaScriptInstance.NoReference)
            {
                Unref(instance.HandlerReferences[i]);
                instance.HandlerReferences[i] = LuaScriptInstance.NoReference;
            }
        }

        if (!_executing)
        {
            _main.GarbageCollector(LuaGC.Collect, 0);
        }
    }

    // "512 MB", "1.5 GB".
    private static string LuaMemoryText(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : (bytes >> 20).ToString(CultureInfo.InvariantCulture) + " MB";

    private void Unref(int reference)
    {
        if (_executing)
        {
            _deferredUnrefs.Add(reference);
        }
        else
        {
            _main.Unref(LuaRegistry.Index, reference);
        }
    }

    private void FlushDeferredUnrefs()
    {
        if (_deferredUnrefs.Count == 0 || _disposed)
        {
            return;
        }

        foreach (var reference in _deferredUnrefs)
        {
            _main.Unref(LuaRegistry.Index, reference);
        }

        _deferredUnrefs.Clear();
    }

    // Scene objects with exactly this name, in scene order (removed ones left out).
    private IEnumerable<int> FindSceneObjects(string name)
    {
        foreach (var id in _sceneObjects)
        {
            var target = _objects[id - 1];
            if (target.Exists && string.Equals(target.Name, name, StringComparison.Ordinal))
            {
                yield return id;
            }
        }
    }

    // The scene object with this Id (a whole number naming one that still exists), else 0.
    private int FindSceneObjectById(double id) =>
        double.IsFinite(id) && id == global::System.Math.Floor(id) && id >= 1 && id <= _objects.Count && _sceneObjects.BinarySearch((int)id) >= 0 && _objects[(int)id - 1].Exists
            ? (int)id
            : 0;

    // Scene objects (Ids, scene order) that still exist and match.
    private IEnumerable<int> SceneObjectsWhere(Func<IScriptObject, bool> match)
    {
        foreach (var id in _sceneObjects)
        {
            var target = _objects[id - 1];
            if (target.Exists && match(target))
            {
                yield return id;
            }
        }
    }

    private static void PushIdTable(LuaState lua, IReadOnlyList<int> ids)
    {
        lua.CreateTable(ids.Count, 0);
        for (var i = 0; i < ids.Count; i++)
        {
            lua.PushInteger(ids[i]);
            lua.RawSetInteger(-2, i + 1);
        }
    }

    // hostScene("tag", id, op, name) / hostScene("attr", id, op, name, value): a Part's tags and attributes (shared by
    // every script, gone at Stop). Answers true[, value] or false, message.
    private int PartDataRequest(LuaState lua, IScriptObject target, string op, bool isTag)
    {
        string? error;
        if (target is not IScriptPart part)
        {
            error = $"'{target.Name}' cannot have {(isTag ? "tags" : "attributes")}";
        }
        else
        {
            var data = part.Data;
            var name = lua.Type(4) == LuaType.String ? lua.ToString(4, false) : null;
            error = op is "list" or "all" ? null : ScriptPartData.CheckName(isTag ? "a tag" : "an attribute name", name);
            if (error is null)
            {
                switch (op)
                {
                    case "list":
                        lua.PushBoolean(true);
                        lua.CreateTable(data.Tags.Count, 0);
                        for (var i = 0; i < data.Tags.Count; i++)
                        {
                            lua.PushString(data.Tags[i]);
                            lua.RawSetInteger(-2, i + 1);
                        }

                        return 2;
                    case "all":
                        lua.PushBoolean(true);
                        lua.CreateTable(0, data.Attributes.Count);
                        foreach (var (attributeName, attributeValue) in data.Attributes)
                        {
                            lua.PushString(attributeName);
                            PushValue(lua, attributeValue);
                            lua.RawSet(-3);
                        }

                        return 2;
                    case "has":
                        lua.PushBoolean(true);
                        lua.PushBoolean(isTag ? data.Tags.Contains(name!) : data.Attributes.ContainsKey(name!));
                        return 2;
                    case "get":
                        lua.PushBoolean(true);
                        PushValue(lua, data.Attributes.GetValueOrDefault(name!));
                        return 2;
                    case "add":
                        if (!data.Tags.Contains(name!))
                        {
                            if (data.Tags.Count >= ScriptPartData.MaxTags)
                            {
                                error = $"'{target.Name}' already has {ScriptPartData.MaxTags} tags";
                                break;
                            }

                            data.Tags.Add(name!);
                        }

                        break;
                    case "remove" when isTag:
                        data.Tags.Remove(name!);
                        break;
                    case "remove":
                        if (data.Attributes.Remove(name!, out var removedValue))
                        {
                            NoteAttributeChange(target, name!, removedValue, null);
                        }

                        break;
                    case "set":
                        var previous = data.Attributes.GetValueOrDefault(name!);
                        error = SetAttribute(target, data, name!, lua, 5);
                        if (error is null)
                        {
                            NoteAttributeChange(target, name!, previous, data.Attributes.GetValueOrDefault(name!));
                        }

                        break;
                    default:
                        error = "unknown request";
                        break;
                }
            }
        }

        if (error is null)
        {
            lua.PushBoolean(true);
            return 1;
        }

        lua.PushBoolean(false);
        lua.PushString(error);
        return 2;
    }

    // An attribute holds a boolean, a number (integer or float, as given) or a string; nil removes it.
    private static string? SetAttribute(IScriptObject target, ScriptPartData data, string name, LuaState lua, int index)
    {
        object value;
        switch (lua.Type(index))
        {
            case LuaType.Nil or LuaType.None:
                data.Attributes.Remove(name);
                return null;
            case LuaType.Boolean:
                value = lua.ToBoolean(index);
                break;
            case LuaType.Number:
                // Boxed apart: a conditional of long and double would make every integer a float.
                value = lua.IsInteger(index) ? (object)lua.ToInteger(index) : lua.ToNumber(index);
                break;
            case LuaType.String:
                var text = lua.ToString(index, false) ?? string.Empty;
                if (text.Length > ScriptPartData.MaxStringLength)
                {
                    return $"attribute strings can be at most {ScriptPartData.MaxStringLength} characters";
                }

                value = text;
                break;
            default:
                return $"attributes hold numbers, strings or booleans (not a {lua.TypeName(lua.Type(index))} value)";
        }

        if (!data.Attributes.ContainsKey(name) && data.Attributes.Count >= ScriptPartData.MaxAttributes)
        {
            return $"'{target.Name}' already has {ScriptPartData.MaxAttributes} attributes";
        }

        data.Attributes[name] = value;
        return null;
    }

    // object:AttachScript(path): the file must be a .lua file inside Assets/Scripts (as require() finds modules), at most
    // MaxModuleBytes; the scene host attaches it.
    private string? AttachScript(LuaState lua, IScriptObject target, out int handle)
    {
        handle = 0;
        var path = lua.ToString(3, false) ?? string.Empty;
        if (SceneHost is not { } host)
        {
            return "AttachScript is not available here (no running scene)";
        }

        var relative = path.StartsWith(Assets.ProjectAssetPaths.ScriptsFolder + "/", StringComparison.Ordinal)
            ? path[(Assets.ProjectAssetPaths.ScriptsFolder.Length + 1)..]
            : path;
        if (!TryResolveModule(relative, out var fullPath, out var assetPath, out var refusal))
        {
            return $"AttachScript: \"{Shorten(path)}\" is not usable: {refusal} (e.g. \"Spin.lua\" or \"Assets/Scripts/Spin.lua\")";
        }

        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                return $"AttachScript: {assetPath} does not exist";
            }

            if (file.Length > MaxModuleBytes)
            {
                return $"AttachScript: {assetPath} is larger than {MaxModuleBytes / (1024 * 1024)} MB";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"AttachScript: {assetPath} cannot be read";
        }

        return host.AttachScript(target, fullPath, assetPath, out handle);
    }

    private static string Shorten(string text) => text.Length > 60 ? text[..60] + "..." : text;

    private static int PushObjectId(LuaState lua, int id)
    {
        if (id == 0)
        {
            return 0;
        }

        lua.PushInteger(id);
        return 1;
    }

    // A script value as the host reads it: bool, double, string, or null (nil and anything else).
    private static object? ValueAt(LuaState lua, int index) => lua.Type(index) switch
    {
        LuaType.Boolean => lua.ToBoolean(index),
        LuaType.Number => lua.ToNumber(index),
        LuaType.String => lua.ToString(index, false),
        _ => null
    };

    // button:OnClick(f) / OnPressed / OnReleased / OnHoverEnter / OnHoverExit: f (at functionIndex) is kept in the
    // registry for the running script.
    private string? RegisterClick(LuaState lua, GuiElement element, GuiButtonEvent kind, int functionIndex, out int handlerId)
    {
        handlerId = 0;
        var method = kind switch
        {
            GuiButtonEvent.Pressed => "OnPressed",
            GuiButtonEvent.Released => "OnReleased",
            GuiButtonEvent.HoverEnter => "OnHoverEnter",
            GuiButtonEvent.HoverExit => "OnHoverExit",
            _ => "OnClick"
        };
        if (element is not GuiButton)
        {
            return GuiScriptBridge.NoMember(element, method);
        }

        if (_running is not { } owner || lua.Type(functionIndex) != LuaType.Function)
        {
            return $"{method} expects a function";
        }

        if (_clickHandlers.Count(handler => ReferenceEquals(handler.Owner, owner)) >= MaxHandlersPerScript)
        {
            return $"this script already has {MaxHandlersPerScript:N0} GUI callbacks: disconnect the ones it no longer needs";
        }

        lua.PushCopy(functionIndex);
        handlerId = ++_nextHandlerId;
        _clickHandlers.Add(new ClickHandler(handlerId, element.Id, kind, owner, lua.Ref(LuaRegistry.Index)));
        return null;
    }

    // A script's Events.On connections go with it (released, faulted or stopped): their functions are let go.
    private void RemoveEventHandlers(LuaScriptInstance instance)
    {
        var removed = false;
        foreach (var handler in _eventHandlers)
        {
            if (handler.Active && ReferenceEquals(handler.Owner, instance))
            {
                handler.Active = false;
                Unref(handler.FunctionReference);
                removed = true;
            }
        }

        if (removed)
        {
            _eventHandlers.RemoveAll(static handler => !handler.Active);
        }
    }

    private void CallTimer(ScriptTimer timer, ScriptPhase phase) => Call(timer.Owner, timer.FunctionReference, phase, passDeltaTime: false);

    private static int PushFound(LuaState lua, GuiElement? element)
    {
        if (element is null)
        {
            return 0;
        }

        lua.PushInteger(element.Id);
        lua.PushString(element.Type.ToString());
        lua.PushString(element.Name);
        return 3;
    }

    private static void PushValue(LuaState lua, object? value)
    {
        switch (value)
        {
            case long number:
                lua.PushInteger(number);
                break;
            case double number:
                lua.PushNumber(number);
                break;
            case bool flag:
                lua.PushBoolean(flag);
                break;
            case string text:
                lua.PushString(text);
                break;
            default:
                lua.PushNil();
                break;
        }
    }

    // A script's callbacks go with it (released or faulted): their functions are let go.
    private void RemoveClickHandlers(LuaScriptInstance instance)
    {
        var removed = false;
        foreach (var handler in _clickHandlers)
        {
            if (handler.Active && ReferenceEquals(handler.Owner, instance))
            {
                handler.Active = false;
                Unref(handler.FunctionReference);
                removed = true;
            }
        }

        if (removed)
        {
            _clickHandlers.RemoveAll(handler => !handler.Active);
        }
    }

    private void Fail(LuaScriptInstance instance, ScriptDiagnostic diagnostic, string? traceback)
    {
        instance.State = ScriptInstanceState.Faulted;
        instance.Fault = diagnostic;
        RemoveClickHandlers(instance);
        RemoveEventHandlers(instance);
        RemoveTimers(instance);
        RemoveOwned(instance);
        if (diagnostic.Kind == ScriptDiagnosticKind.MemoryLimitExceeded)
        {
            ReleaseScriptMemory(instance);
        }

        Emit(new ScriptLogMessage(ScriptLogKind.Error, instance.Name, diagnostic.ToString()) { Diagnostic = diagnostic });
        if (!string.IsNullOrWhiteSpace(traceback))
        {
            foreach (var line in traceback.Split('\n'))
            {
                Emit(new ScriptLogMessage(ScriptLogKind.Detail, instance.Name, line.TrimEnd()));
            }
        }
    }

    private void Emit(ScriptLogMessage message)
    {
        try
        {
            if (_options.Log is { } log)
            {
                log(message);
            }
            else
            {
                Console.WriteLine(message.ToString());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Lua] Log sink failed: {ex.Message}");
        }
    }

    // "wisko.script.N:12: message" -> line 12 + message, with the internal chunk name replaced by the display name.
    private static ScriptDiagnostic Diagnose(LuaScriptInstance instance, ScriptPhase phase, ScriptDiagnosticKind kind, string text)
    {
        int? line = null;
        var message = text;
        var prefix = instance.ChunkName + ":";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = text[prefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon > 0 && int.TryParse(rest.AsSpan(0, colon), out var parsedLine))
            {
                line = parsedLine;
                message = rest[(colon + 1)..].TrimStart();
            }
        }

        message = message.Replace(instance.ChunkName, instance.Name, StringComparison.Ordinal);
        if (message.Length > MaxDiagnosticMessageLength)
        {
            message = message[..MaxDiagnosticMessageLength] + "...";
        }

        return new ScriptDiagnostic(instance.Name, phase, kind, line, message);
    }

    // The error value on top of the stack, read without running any metamethod.
    private static string ErrorText(LuaState state)
    {
        var type = state.Type(-1);
        return type is LuaType.String or LuaType.Number
            ? state.ToString(-1, false) ?? string.Empty
            : $"(error object is a {state.TypeName(type)} value)";
    }

    private string? Traceback(LuaScriptInstance instance, string message)
    {
        var top = _main.GetTop();
        try
        {
            _main.Traceback(_runner, message, 0);
            return _main.ToString(-1, false)?.Replace(instance.ChunkName, instance.Name, StringComparison.Ordinal);
        }
        catch
        {
            return null;
        }
        finally
        {
            _main.SetTop(top);
        }
    }

    // Line of the innermost script frame of the stopped runner (for budget stops, where there is no error message).
    private int? CurrentScriptLine(LuaScriptInstance instance)
    {
        try
        {
            for (var level = 0; level < 32; level++)
            {
                var debug = new LuaDebug();
                if (_runner.GetStack(level, ref debug) == 0)
                {
                    break;
                }

                if (_runner.GetInfo("Sl", ref debug) && debug.CurrentLine > 0
                    && debug.ShortSource.StartsWith(instance.ChunkName, StringComparison.Ordinal))
                {
                    return debug.CurrentLine;
                }
            }
        }
        catch
        {
            // Line is optional.
        }

        return null;
    }

    private bool IsRunnable(LuaScriptInstance instance)
    {
        ThrowIfExecuting();
        return IsLive(instance);
    }

    // Whether the script still runs (not faulted, stopped or released), asked from inside a running callback.
    private bool IsLive(LuaScriptInstance instance) =>
        !_disposed && !instance.Destroyed && instance.State is ScriptInstanceState.Loaded or ScriptInstanceState.Started;

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfExecuting();
    }

    private void ThrowIfExecuting()
    {
        if (_executing)
        {
            throw new InvalidOperationException("The Lua runtime is already running a script callback (re-entrant call).");
        }
    }

    // What a destroyed object's Id names from then on: gone, with its last name for messages.
    private sealed class RemovedObject(string name) : IScriptObject
    {
        public string Name { get; } = name;

        public Vector3 MinimumScale => Vector3.Zero;

        public bool Exists => false;

        public ScriptTransform ReadTransform() => default;

        public void WriteTransform(ScriptTransform transform)
        {
        }
    }
}
