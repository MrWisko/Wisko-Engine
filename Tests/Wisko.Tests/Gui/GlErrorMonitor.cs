using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Tests.Gui;

/// <summary>
/// Synchronous KHR_debug output on the editor's GL context: every GL error is caught at the call that caused it, with
/// the Wisko frames of the managed stack. Install on the window thread with the context current.
/// </summary>
internal sealed class GlErrorMonitor
{
    private readonly List<string> _errors = [];
    private DebugProc? _callback; // kept alive for the driver

    public IReadOnlyList<string> Errors => _errors;

    public string Note { get; private set; } = string.Empty;

    public static GlErrorMonitor Install()
    {
        var monitor = new GlErrorMonitor();
        TestReport.Info($"GL: {GL.GetString(StringName.Vendor)} | {GL.GetString(StringName.Renderer)} | {GL.GetString(StringName.Version)}");
        try
        {
            monitor._callback = monitor.OnMessage;
            GL.Enable(EnableCap.DebugOutput);
            GL.Enable(EnableCap.DebugOutputSynchronous);
            GL.DebugMessageCallback(monitor._callback, IntPtr.Zero);
            monitor.Note = "KHR_debug callback installed";
        }
        catch (Exception ex)
        {
            monitor.Note = $"KHR_debug unavailable ({ex.Message}); only the editor's own glGetError checks apply";
        }

        GL.GetError(); // a debug-setup error must not be reported later as the editor's
        return monitor;
    }

    private void OnMessage(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
    {
        if (type != DebugType.DebugTypeError || _errors.Count >= 20)
        {
            return;
        }

        var text = Marshal.PtrToStringAnsi(message, length);
        var stack = string.Join(" < ", Environment.StackTrace.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("Wisko.Engine", StringComparison.Ordinal))
            .Take(4));
        _errors.Add($"{text} at {stack}");
        Console.WriteLine($"[GL-ERROR] {text}\n      {stack}");
    }
}
