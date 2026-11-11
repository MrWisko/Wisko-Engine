namespace Wisko.Engine.Scripting;

/// <summary>The functions a script may define for its object's events (called with the arguments shown).</summary>
internal enum ScriptEventHandler
{
    OnCollisionEnter,
    OnCollisionStay,
    OnCollisionExit,
    OnTriggerEnter,
    OnTriggerStay,
    OnTriggerExit,
    OnDestroy
}

public enum ScriptInstanceState
{
    /// <summary>Compiled and its top-level chunk has run; Start has not run yet.</summary>
    Loaded,

    /// <summary>Start has run (or was absent); Update may run.</summary>
    Started,

    /// <summary>A Load, Start or Update failure stopped the script; it never runs again.</summary>
    Faulted,

    Disposed
}

/// <summary>
/// One loaded script: its own environment (globals) inside the runtime's Lua state, plus its optional
/// Start() and Update(dt) functions and event functions (OnCollisionEnter / Stay(other, contact), OnCollisionExit(other),
/// OnTriggerEnter / Stay(other, contact), OnTriggerExit(other), OnDestroy()), read once its top-level chunk has run.
/// <para>Lifecycle rules: the top-level chunk runs once when the script is loaded. Start runs at most once.
/// Update runs Start first if it has not run yet. A missing Start or Update is not an error. After any failure the
/// script is Faulted: later calls do nothing and return the original diagnostic (reported once, not every frame).
/// When its object is destroyed the script stops at once (no more Update, events, timers or clicks); its OnDestroy runs
/// once, then it is released.</para>
/// </summary>
public sealed class LuaScriptInstance : IDisposable
{
    internal const int NoReference = -2; // LUA_NOREF

    private readonly LuaScriptRuntime _runtime;

    internal LuaScriptInstance(LuaScriptRuntime runtime, string name, string chunkName)
    {
        _runtime = runtime;
        Name = name;
        ChunkName = chunkName;
    }

    /// <summary>Display name used in diagnostics and print output (the file name for scripts loaded from files).</summary>
    public string Name { get; }

    public ScriptInstanceState State { get; internal set; } = ScriptInstanceState.Loaded;

    /// <summary>The failure that faulted this script, if any.</summary>
    public ScriptDiagnostic? Fault { get; internal set; }

    public bool HasStart => StartReference != NoReference;

    public bool HasUpdate => UpdateReference != NoReference;

    public bool HasLateUpdate => LateUpdateReference != NoReference;

    /// <summary>True when the script is bound to a scene object (it has self).</summary>
    public bool HasSelf => Binding is not null;

    // ASCII name handed to native Lua (KeraLua marshals chunk names with the ANSI code page on Windows).
    internal string ChunkName { get; }

    internal int EnvironmentReference { get; set; } = NoReference;

    internal int StartReference { get; set; } = NoReference;

    internal int UpdateReference { get; set; } = NoReference;

    internal int LateUpdateReference { get; set; } = NoReference;

    // What the script's top-level chunk returned when that was a table: the functions and values it exports to other
    // scripts (object:GetScript).
    internal int ExportsReference { get; set; } = NoReference;

    // The script's own string library (string methods come from it while the script runs).
    internal int StringReference { get; set; } = NoReference;

    // The environment's deliver(f, id) (see LuaSandbox) and the event functions, by ScriptEventHandler.
    internal int DeliverReference { get; set; } = NoReference;

    // The environment's deliverEvent(f, tags, ...) (see LuaSandbox): Events.On handlers get their arguments through it.
    internal int DeliverEventReference { get; set; } = NoReference;

    internal int[] HandlerReferences { get; } = [NoReference, NoReference, NoReference, NoReference, NoReference, NoReference, NoReference];

    // Its object was destroyed: nothing runs any more but OnDestroy (then it is released).
    internal bool Destroyed { get; set; }

    // Timers it has that have not finished or been cancelled.
    internal int TimerCount { get; set; }

    internal bool HasHandler(ScriptEventHandler handler) => HandlerReferences[(int)handler] != NoReference;

    internal ObjectBinding? Binding { get; set; }

    public ScriptExecutionResult Start() => _runtime.RunStart(this);

    /// <summary>Calls Update(dt) with the runtime's current Time.deltaTime (see <see cref="LuaScriptRuntime.AdvanceTime"/>).</summary>
    public ScriptExecutionResult Update() => _runtime.RunUpdate(this);

    /// <summary>Calls LateUpdate(dt) (after physics) once the script has started; Time.deltaTime as for Update.</summary>
    public ScriptExecutionResult LateUpdate() => _runtime.RunLateUpdate(this);

    public void Dispose() => _runtime.Release(this);
}
