namespace Wisko.Engine.Scripting;

/// <summary>Where in a script's life a failure happened.</summary>
public enum ScriptPhase
{
    /// <summary>Compiling the source and running its top-level chunk.</summary>
    Load,
    Start,
    Update,

    /// <summary>A GUI Button's OnClick callback (Play).</summary>
    OnClick,

    /// <summary>The script's OnCollisionEnter / Stay / Exit(other): its object's contacts (Play).</summary>
    OnCollisionEnter,
    OnCollisionStay,
    OnCollisionExit,

    /// <summary>The script's OnTriggerEnter / Stay / Exit(other): its object's trigger overlaps (Play).</summary>
    OnTriggerEnter,
    OnTriggerStay,
    OnTriggerExit,

    /// <summary>The script's OnDestroy(): its object was destroyed while playing.</summary>
    OnDestroy,

    /// <summary>A Timer.After callback (shown as "Timer.After").</summary>
    TimerAfter,

    /// <summary>A Timer.Every callback (shown as "Timer.Every").</summary>
    TimerEvery,

    /// <summary>A GUI Button's OnPressed / OnReleased / OnHoverEnter / OnHoverExit callback (Play).</summary>
    OnPressed,
    OnReleased,
    OnHoverEnter,
    OnHoverExit,

    /// <summary>An Events.On handler (shown as "Events.On").</summary>
    Event,

    /// <summary>The script's LateUpdate(dt): every frame after physics (Play).</summary>
    LateUpdate,

    /// <summary>A Time.OnUpdate callback (shown as "Time.OnUpdate").</summary>
    FrameCallback,

    /// <summary>An object:OnTriggerEntered / OnTriggerExited callback (shown as "OnTriggerEntered/Exited").</summary>
    TriggerCallback,

    /// <summary>A TextBox's OnSubmit / OnFocusLost / OnFocused callback (shown as "TextBox callback").</summary>
    TextBoxCallback,

    /// <summary>A Chat.OnMessage listener (shown as "Chat.OnMessage").</summary>
    ChatMessage,

    /// <summary>A Chat.SetFilter filter (shown as "Chat.SetFilter").</summary>
    ChatFilter
}

public enum ScriptDiagnosticKind
{
    /// <summary>The source does not compile.</summary>
    Syntax,

    /// <summary>A Lua error while running (including errors raised with error()).</summary>
    Runtime,

    /// <summary>A callback ran longer than the runtime's instruction budget and was stopped.</summary>
    InstructionBudgetExceeded,

    /// <summary>A scene object references a script file that does not exist in the project.</summary>
    MissingScript,

    /// <summary>A callback would have taken the scripts' Lua memory past the session's limit and was stopped.</summary>
    MemoryLimitExceeded
}

/// <summary>
/// One script failure in engine terms: which script, which phase, what kind, the line when known and a concise
/// message. Full Lua tracebacks are written to the log only.
/// </summary>
public sealed record ScriptDiagnostic(string ScriptName, ScriptPhase Phase, ScriptDiagnosticKind Kind, int? Line, string Message)
{
    /// <summary>How a phase is shown: its name, or the API that ran it ("Timer.After", "Timer.Every").</summary>
    public static string PhaseName(ScriptPhase phase) => phase switch
    {
        ScriptPhase.TimerAfter => "Timer.After",
        ScriptPhase.TimerEvery => "Timer.Every",
        ScriptPhase.Event => "Events.On",
        ScriptPhase.FrameCallback => "Time.OnUpdate",
        ScriptPhase.TriggerCallback => "OnTriggerEntered/Exited",
        ScriptPhase.TextBoxCallback => "TextBox callback",
        ScriptPhase.ChatMessage => "Chat.OnMessage",
        ScriptPhase.ChatFilter => "Chat.SetFilter",
        _ => phase.ToString()
    };

    public override string ToString() =>
        Line is int line
            ? $"{ScriptName}:{line}: {Message} [{PhaseName(Phase)}]"
            : $"{ScriptName}: {Message} [{PhaseName(Phase)}]";
}

public enum ScriptRunStatus
{
    Succeeded,
    Failed,

    /// <summary>Nothing ran: Start already ran, or the script is faulted or disposed.</summary>
    NotRun
}

/// <summary>Outcome of one lifecycle call. A NotRun result carries the earlier fault, if any, without re-reporting it.</summary>
public readonly record struct ScriptExecutionResult(ScriptRunStatus Status, ScriptDiagnostic? Diagnostic)
{
    public bool Succeeded => Status == ScriptRunStatus.Succeeded;

    internal static ScriptExecutionResult Success => new(ScriptRunStatus.Succeeded, null);

    internal static ScriptExecutionResult Failure(ScriptDiagnostic diagnostic) => new(ScriptRunStatus.Failed, diagnostic);

    internal static ScriptExecutionResult Skipped(ScriptDiagnostic? earlierFault) => new(ScriptRunStatus.NotRun, earlierFault);
}

public enum ScriptLogKind
{
    /// <summary>Output of the script's print().</summary>
    Print,

    /// <summary>A failure, one line (the diagnostic).</summary>
    Error,

    /// <summary>A problem the script survives (e.g. an unknown Input key name); reported once.</summary>
    Warning,

    /// <summary>Supporting detail for the log only (Lua traceback).</summary>
    Detail
}

public readonly record struct ScriptLogMessage(ScriptLogKind Kind, string ScriptName, string Text)
{
    /// <summary>For <see cref="ScriptLogKind.Error"/>: the structured failure (line, concise message).</summary>
    public ScriptDiagnostic? Diagnostic { get; init; }

    public override string ToString() => Kind switch
    {
        ScriptLogKind.Print => $"[Lua] {ScriptName}: {Text}",
        ScriptLogKind.Error => $"[Lua] Error: {Text}",
        ScriptLogKind.Warning => $"[Lua] Warning: {ScriptName}: {Text}",
        _ => $"[Lua]   {Text}"
    };
}
