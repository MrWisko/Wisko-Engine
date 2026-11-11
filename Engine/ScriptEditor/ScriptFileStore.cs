using System.Security.Cryptography;
using System.Text;
using Wisko.Engine.Platform;

namespace Wisko.Engine.ScriptEditor;

/// <summary>What a script file looked like on disk when the editor last read or wrote it.</summary>
public readonly record struct ScriptFileBaseline(bool Exists, long Length, DateTime LastWriteUtc, string Hash)
{
    public static readonly ScriptFileBaseline Missing = new(false, 0, default, string.Empty);

    /// <summary>Same size and time stamp (a cheap first check before hashing).</summary>
    public bool SameStat(ScriptFileBaseline other) =>
        Exists == other.Exists && Length == other.Length && LastWriteUtc == other.LastWriteUtc;
}

/// <summary>A script file as read: its text with "\n" line breaks plus what is needed to write it back the same way.</summary>
public sealed record LoadedScript(string Text, string Newline, bool HasBom, bool HadInvalidUtf8, ScriptFileBaseline Baseline);

/// <summary>
/// Reading and safely writing Lua script files (UTF-8). Saving goes through <see cref="AtomicFile"/>, so a failure at
/// any point leaves either the old or the new script, never a truncated one. The file's line-break style and
/// byte-order mark are kept.
/// </summary>
public static class ScriptFileStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static bool TryLoad(string path, out LoadedScript? script, out string? error)
    {
        script = null;
        error = null;
        byte[] bytes;
        FileInfo info;
        try
        {
            bytes = File.ReadAllBytes(path);
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex is FileNotFoundException or DirectoryNotFoundException ? "File not found" : ex.Message;
            return false;
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = hasBom ? bytes.AsSpan(3) : bytes.AsSpan();
        string raw;
        var invalid = false;
        try
        {
            raw = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            raw = LenientUtf8.GetString(body);
            invalid = true;
        }

        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\n')
            {
                if (i > 0 && raw[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        var text = string.Join('\n', TextBuffer.SplitLines(raw));
        var baseline = new ScriptFileBaseline(true, info.Length, info.LastWriteTimeUtc, Hash(bytes));
        script = new LoadedScript(text, crlf > lf ? "\r\n" : "\n", hasBom, invalid, baseline);
        return true;
    }

    /// <summary>Writes <paramref name="text"/> ("\n" line breaks) using <paramref name="newline"/>; atomic replace.</summary>
    public static bool TrySave(string path, string text, string newline, bool withBom, out ScriptFileBaseline baseline, out string? error)
    {
        baseline = ScriptFileBaseline.Missing;
        error = null;
        try
        {
            var content = newline == "\n" ? text : text.Replace("\n", newline, StringComparison.Ordinal);
            byte[] bytes = withBom ? [.. Encoding.UTF8.GetPreamble(), .. StrictUtf8.GetBytes(content)] : StrictUtf8.GetBytes(content);
            AtomicFile.WriteAllBytes(path, bytes);
            var info = new FileInfo(path);
            baseline = new ScriptFileBaseline(true, info.Length, info.LastWriteTimeUtc, Hash(bytes));
            return true;
        }
        catch (Exception ex) when (AtomicFile.IsWriteFailure(ex))
        {
            // ArgumentException includes text that is not valid Unicode (EncoderFallbackException): nothing is written.
            error = ex is UnauthorizedAccessException ? "Permission denied" : ex.Message;
            Console.WriteLine($"[ScriptEditor] Could not save {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Size and time stamp of the file now (no hash).</summary>
    public static ScriptFileBaseline ReadStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ScriptFileBaseline(true, info.Length, info.LastWriteTimeUtc, string.Empty) : ScriptFileBaseline.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ScriptFileBaseline.Missing;
        }
    }

    /// <summary>Size, time stamp and content hash of the file now.</summary>
    public static ScriptFileBaseline ReadBaseline(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return ScriptFileBaseline.Missing;
            }

            var bytes = File.ReadAllBytes(path);
            return new ScriptFileBaseline(true, bytes.LongLength, info.LastWriteTimeUtc, Hash(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ScriptFileBaseline.Missing;
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
