using System.Text;

namespace Wisko.Editor;

/// <summary>
/// Small production log: mirrors everything written to the console into
/// ~/Library/Logs/Wisko/Wisko.log (macOS; LocalApplicationData/Wisko/Logs elsewhere),
/// so Finder launches without a terminal still leave a trace. Console output is unchanged for terminal
/// runs. Each launch starts a fresh log and keeps the previous one as Wisko.previous.log; one
/// session's log is capped so it can never grow without bound. Logging is best effort and never fatal.
/// </summary>
internal static class AppLog
{
    private const long MaxLogChars = 8L * 1024 * 1024;

    public static string? FilePath { get; private set; }

    public static void Start()
    {
        try
        {
            var directory = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Wisko")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisko", "Logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "Wisko.log");
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(directory, "Wisko.previous.log"), overwrite: true);
            }

            var file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            var capped = new CappedWriter(file, MaxLogChars);
            Console.SetOut(new TeeWriter(Console.Out, capped));
            Console.SetError(new TeeWriter(Console.Error, capped));
            FilePath = path;
            Console.WriteLine($"[Log] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Log] File logging unavailable: {ex.Message}");
        }
    }

    public static void Fatal(Exception? exception) =>
        Console.Error.WriteLine($"[Fatal] {exception?.ToString() ?? "Unknown unhandled error"}");

    /// <summary>Writes to the original console and the log file.</summary>
    private sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            file.Write(value);
        }

        public override void Write(string? value)
        {
            console.Write(value);
            file.Write(value);
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            file.WriteLine(value);
        }

        public override void Flush()
        {
            console.Flush();
            file.Flush();
        }
    }

    /// <summary>Stops writing (after one notice) once a session has written <c>limit</c> characters.</summary>
    private sealed class CappedWriter(TextWriter inner, long limit) : TextWriter
    {
        private long _written;
        private bool _stopped;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (_stopped || value is null)
            {
                return;
            }

            _written += value.Length;
            if (_written > limit)
            {
                _stopped = true;
                inner.WriteLine();
                inner.WriteLine("[Log] Size limit reached; further output goes to the console only.");
                return;
            }

            inner.Write(value);
        }

        public override void WriteLine(string? value) => Write((value ?? string.Empty) + Environment.NewLine);

        public override void Flush() => inner.Flush();
    }
}
