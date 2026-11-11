using System.Text;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.World;

namespace Wisko.Tests;

/// <summary>Building blocks for the checks: project scripts, Parts and headless Play sessions.</summary>
internal static class TestSupport
{
    public const double Frame = 1.0 / 60.0;

    /// <summary>
    /// Lua helper the check scripts use: <c>chk(name, condition)</c> prints "CHECK name true|false", once per name (the
    /// first report is the one that counts, so checks inside Update do not flood the log).
    /// </summary>
    public const string ChkFunction =
        "local chkReported = {}\n" +
        "local function chk(name, cond) if chkReported[name] then return end chkReported[name] = true print(\"CHECK \" .. name .. \" \" .. tostring(cond and true or false)) end\n";

    public static string WriteScript(string projectRoot, string relative, string source)
    {
        var path = Path.Combine(projectRoot, "Assets", "Scripts", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source, new UTF8Encoding(false));
        return path;
    }

    public static Part MakePart(string name, Vector3 position, Vector3 size, bool anchored, params string[] scripts)
    {
        var part = new Part(name, position, size, new Vector3(0.5f, 0.5f, 0.9f), anchored: anchored);
        foreach (var script in scripts)
        {
            if (!ScriptReference.TryCreate(script, out var reference))
            {
                throw new InvalidOperationException($"bad script reference {script}");
            }

            part.Scripts.Add(reference);
        }

        return part;
    }

    /// <summary>Enters Play exactly as the editor does (PlayModeController), with a fixed camera.</summary>
    public static PlayModeController StartPlay(string projectRoot, Workspace workspace, GuiDocument? gui, OutputLog log, List<PlayScriptFault> faults)
    {
        var play = new PlayModeController { Output = log, GuiSource = gui, CameraHost = new StubCamera() };
        if (!play.TryStart(projectRoot, workspace, [], null, out var error))
        {
            throw new InvalidOperationException($"PlayModeController.TryStart failed: {error}");
        }

        faults.AddRange(play.TakeNewFaults());
        return play;
    }

    /// <summary>Advances Play by <paramref name="frames"/> fixed 1/60 s frames.</summary>
    public static void Step(PlayModeController play, int frames, List<PlayScriptFault> faults)
    {
        for (var i = 0; i < frames; i++)
        {
            if (!play.TryUpdate(Frame, null, out var error))
            {
                throw new InvalidOperationException($"PlayModeController.TryUpdate failed: {error}");
            }

            faults.AddRange(play.TakeNewFaults());
        }
    }

    /// <summary>What the scripts reported with <see cref="ChkFunction"/>: name -> "true"/"false" (first report wins).</summary>
    public static Dictionary<string, string> LuaReports(OutputLog log)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < log.Count; i++)
        {
            var parts = log[i].Message.Split(' ', 3);
            if (parts.Length == 3 && parts[0] == "CHECK" && !seen.ContainsKey(parts[1]))
            {
                seen[parts[1]] = parts[2];
            }
        }

        return seen;
    }

    /// <summary>One check per expected name: the script must have reported it, as true.</summary>
    public static void AssertLuaChecks(TestReport report, string prefix, OutputLog log, IEnumerable<string> expected)
    {
        var seen = LuaReports(log);
        foreach (var name in expected)
        {
            var reported = seen.TryGetValue(name, out var value);
            report.Check($"{prefix}: {name}", reported && value == "true",
                reported ? (value == "true" ? null : $"reported '{value}'") : "never reported");
        }
    }

    public static void AssertNoFaults(TestReport report, string prefix, List<PlayScriptFault> faults) =>
        report.Check($"{prefix}: no script faults", faults.Count == 0,
            string.Join(" | ", faults.Select(f => $"{f.Diagnostic.ScriptName} {f.Diagnostic.Phase} {f.Diagnostic.Kind} line {f.Diagnostic.Line}: {f.Diagnostic.Message}")));

    /// <summary>
    /// Advances Play by <paramref name="frames"/> fixed frames with these keys held (pressed on the first of them,
    /// released on the frame after the last).
    /// </summary>
    public static void StepHolding(PlayModeController play, int frames, List<PlayScriptFault> faults, params string[] keys)
    {
        var input = new ScriptInputState();
        for (var i = 0; i <= frames; i++)
        {
            foreach (var key in keys)
            {
                if (!ScriptInputState.TryGetKeyIndex(key, out var index))
                {
                    throw new InvalidOperationException($"unknown key {key}");
                }

                input.SetKey(index, down: i < frames, pressed: i == 0, released: i == frames);
            }

            if (!play.TryUpdate(Frame, input, out var error))
            {
                throw new InvalidOperationException($"PlayModeController.TryUpdate failed: {error}");
            }

            faults.AddRange(play.TakeNewFaults());
        }
    }

    /// <summary>A fixed camera so Camera is available to scripts (the editor supplies the real one).</summary>
    internal sealed class StubCamera : IScriptCameraHost
    {
        private bool _rotated;

        public Vector3 Position { get; set; } = new(0, 10, 30);
        public float Pitch { get; private set; }
        public float Yaw { get; private set; }
        public float FieldOfView { get; set; } = 70f;
        public bool MouseLocked { get; set; }

        // Looks along -Z until a script turns it; then along its yaw (0: +X, 90: +Z) and pitch (up positive).
        public Vector3 Forward => _rotated
            ? new Vector3(MathF.Cos(Pitch * MathF.PI / 180f) * MathF.Cos(Yaw * MathF.PI / 180f), MathF.Sin(Pitch * MathF.PI / 180f),
                MathF.Cos(Pitch * MathF.PI / 180f) * MathF.Sin(Yaw * MathF.PI / 180f))
            : new Vector3(0, 0, -1);
        public Vector3 Right => new(1, 0, 0);
        public Vector3 Up => new(0, 1, 0);
        public int ViewportWidth => 1280;
        public int ViewportHeight => 720;

        public void SetRotation(float pitch, float yaw)
        {
            _rotated = true;
            Pitch = pitch;
            Yaw = yaw;
        }

        public bool WorldToScreen(Vector3 point, out float x, out float y)
        {
            x = 640;
            y = 360;
            return true;
        }

        public (Vector3 Origin, Vector3 Direction) ScreenPointToRay(float x, float y) => (Position, Forward);
    }
}
