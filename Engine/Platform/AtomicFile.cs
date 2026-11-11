using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisko.Engine.Platform;

/// <summary>The points inside an <see cref="AtomicFile"/> write where a test can make it fail.</summary>
internal enum AtomicWriteStage
{
    /// <summary>The temporary file holds every byte, not yet flushed to the disk.</summary>
    Written,

    /// <summary>The temporary file is on the disk; the destination still holds the old contents.</summary>
    Flushed,

    /// <summary>The temporary file has replaced the destination.</summary>
    Replaced
}

/// <summary>
/// Replaces a file's contents all at once. The new bytes go to a temporary file in the same folder, are flushed to the
/// disk (FlushFileBuffers on Windows; F_FULLFSYNC on macOS, through .NET), and the temporary file is then renamed over
/// the destination, which Windows (MoveFileEx) and macOS (rename) do as one step on the same volume. A failure or a
/// crash at any point leaves either the old file or the new one, never a mixture or a truncated file. On macOS the
/// folder is flushed after the rename as well, so the rename itself survives a power loss; Windows offers no folder
/// flush without administrator rights, and NTFS records the rename in its journal. Concurrent writers each use their
/// own temporary file, so the destination ends up as one writer's complete contents.
/// </summary>
public static partial class AtomicFile
{
    // A just-written temporary file can be held open for a moment by a virus scanner or a sync client, and a reader
    // may have the destination open; the rename is retried briefly before the write is reported as failed.
    private const int ReplaceAttempts = 6;
    private const int ReplaceRetryMilliseconds = 50;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private enum Mode
    {
        Overwrite,
        CreateNew
    }

    /// <summary>Tests only: called at each stage of every write; throwing from it simulates a failure there.</summary>
    internal static Action<string, AtomicWriteStage>? FaultInjection { get; set; }

    /// <summary>UTF-8 without a byte order mark, as every Wisko JSON file is written.</summary>
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Utf8NoBom.GetBytes(text));

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));

    /// <summary>
    /// <paramref name="write"/> fills the new contents. An exception from it, or from any later step, leaves the
    /// destination exactly as it was and is rethrown; the temporary file is removed.
    /// </summary>
    public static void Write(string path, Action<Stream> write) => WriteCore(path, write, Mode.Overwrite, null);

    /// <summary>As <see cref="Write"/>, keeping the previous contents as <paramref name="backupPath"/> (replaced each time).</summary>
    public static void WriteWithBackup(string path, string backupPath, Action<Stream> write) =>
        WriteCore(path, write, Mode.Overwrite, Path.GetFullPath(backupPath));

    /// <summary>Creates <paramref name="path"/> complete or not at all; an existing file is never replaced (IOException).</summary>
    public static void WriteNew(string path, Action<Stream> write) => WriteCore(path, write, Mode.CreateNew, null);

    /// <summary>As <see cref="WriteAllText"/>; false and the reason instead of an I/O or permission exception.</summary>
    public static bool TryWriteAllText(string path, string text, out string? error) =>
        TryWrite(path, stream => stream.Write(Utf8NoBom.GetBytes(text)), out error);

    public static bool TryWriteAllBytes(string path, byte[] bytes, out string? error) =>
        TryWrite(path, stream => stream.Write(bytes), out error);

    public static bool TryWrite(string path, Action<Stream> write, out string? error)
    {
        try
        {
            Write(path, write);
            error = null;
            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            error = Describe(ex);
            return false;
        }
    }

    /// <summary>
    /// Renames the complete file <paramref name="source"/> over <paramref name="destination"/> in one step (same volume),
    /// with the same retries as a write, and makes the rename durable (macOS folder flush).
    /// </summary>
    public static void MoveReplace(string source, string destination)
    {
        var fullDestination = Path.GetFullPath(destination);
        Replace(Path.GetFullPath(source), fullDestination, Mode.Overwrite, null);
        if (!OperatingSystem.IsWindows())
        {
            FlushFolder(Path.GetDirectoryName(fullDestination)!);
        }
    }

    /// <summary>Whether <paramref name="path"/> is a link (symbolic link, junction or other reparse point).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The exceptions a write can fail with that callers report rather than crash on.</summary>
    public static bool IsWriteFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    public static string Describe(Exception ex) => ex is UnauthorizedAccessException ? $"Permission denied ({ex.Message})" : ex.Message;

    /// <summary>Whether <paramref name="fileName"/> is one of this class's temporary files (left by a crash).</summary>
    public static bool IsTemporaryFile(string fileName) => TemporaryName().IsMatch(fileName);

    /// <summary>
    /// Removes temporary files that interrupted writes left in <paramref name="folder"/>, and nothing else. Only files
    /// older than <paramref name="minimumAge"/> are touched, so a write in progress keeps its file. Returns the count.
    /// </summary>
    public static int RemoveStaleTemporaryFiles(string folder, TimeSpan minimumAge)
    {
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, ".*.tmp"))
            {
                if (IsTemporaryFile(Path.GetFileName(file))
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= minimumAge
                    && TryDelete(file))
                {
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has nothing to clean.
        }

        return removed;
    }

    private static void WriteCore(string path, Action<Stream> write, Mode mode, string? backupPath)
    {
        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new IOException($"{path} has no folder");
        var temp = Path.Combine(folder, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(folder);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                FaultInjection?.Invoke(fullPath, AtomicWriteStage.Written);
                stream.Flush(flushToDisk: true);
            }

            FaultInjection?.Invoke(fullPath, AtomicWriteStage.Flushed);

            // The temporary file got the default permissions; keep the old file's (POSIX).
            if (!OperatingSystem.IsWindows() && File.Exists(fullPath))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(fullPath));
            }

            Replace(temp, fullPath, mode, backupPath);
            FaultInjection?.Invoke(fullPath, AtomicWriteStage.Replaced);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        if (!OperatingSystem.IsWindows())
        {
            FlushFolder(folder);
        }
    }

    private static void Replace(string temp, string destination, Mode mode, string? backupPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (mode == Mode.CreateNew)
                {
                    File.Move(temp, destination, overwrite: false);
                }
                else if (backupPath is not null && File.Exists(destination))
                {
                    ReplaceKeepingBackup(temp, destination, backupPath);
                }
                else
                {
                    File.Move(temp, destination, overwrite: true);
                }

                return;
            }
            catch (IOException ex) when (attempt < ReplaceAttempts && IsSharingViolation(ex))
            {
                Thread.Sleep(ReplaceRetryMilliseconds * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < ReplaceAttempts && OperatingSystem.IsWindows())
            {
                // Windows reports a file that is being deleted, or is mapped by another process, as access denied.
                Thread.Sleep(ReplaceRetryMilliseconds * attempt);
            }
        }
    }

    // File.Replace keeps the old contents as the backup. On Windows, ReplaceFile can fail after moving the old file to
    // the backup name without moving the new one in (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): the destination is then
    // put back, from the new file when possible and otherwise from the backup, before the error is reported.
    private static void ReplaceKeepingBackup(string temp, string destination, string backupPath)
    {
        try
        {
            File.Replace(temp, destination, backupPath, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(destination))
            {
                try
                {
                    File.Move(temp, destination);
                    return;
                }
                catch (Exception) when (File.Exists(backupPath) && !File.Exists(destination))
                {
                    File.Copy(backupPath, destination);
                }
            }

            throw;
        }
    }

    // ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // POSIX: a rename is durable once the folder that holds it is flushed.
    private static void FlushFolder(string folder)
    {
        var descriptor = Posix.Open(folder, 0 /* O_RDONLY */);
        if (descriptor < 0)
        {
            return;
        }

        try
        {
            _ = Posix.FSync(descriptor);
        }
        finally
        {
            _ = Posix.Close(descriptor);
        }
    }

    [GeneratedRegex(@"^\..+\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryName();

    private static class Posix
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static extern int FSync(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);
    }
}
