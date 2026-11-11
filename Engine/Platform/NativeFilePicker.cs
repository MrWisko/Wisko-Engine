using NativeFileDialogs.Net;

namespace Wisko.Engine.Platform;

internal enum FilePickResult
{
    Picked,
    Cancelled,
    Failed
}

/// <summary>One file-type choice in an open dialog: a label and its extensions without dots (e.g. "glb").</summary>
internal readonly record struct FileTypeFilter(string Label, IReadOnlyList<string> Extensions);

/// <summary>
/// Native "open file" dialog. Today every platform goes through NativeFileDialogs.Net (nfd-extended: NSOpenPanel on
/// macOS, the common item dialog on Windows, GTK on Linux); callers only see this API, so a platform can get its own
/// implementation here later without changing them. Blocks until the user picks or cancels; call it on the
/// main/window thread. Paths come back as full native paths (spaces and Unicode intact).
/// </summary>
internal static class NativeFilePicker
{
    public static FilePickResult TryPickFile(IReadOnlyList<FileTypeFilter> filters, out string? path, out string? error) =>
        TryPickFile(filters, null, out path, out error);

    /// <summary>As above, opening in <paramref name="defaultFolder"/> when it is given and exists.</summary>
    public static FilePickResult TryPickFile(IReadOnlyList<FileTypeFilter> filters, string? defaultFolder, out string? path, out string? error)
    {
        path = null;
        error = null;
        if (VisualCppRuntime.FileDialogsBlocked)
        {
            // nfd.dll cannot load without Microsoft's Visual C++ runtime (Windows): say why instead of trying.
            error = VisualCppRuntime.ShortProblem;
            return FilePickResult.Failed;
        }

        var nfdFilters = filters.ToDictionary(filter => filter.Label, filter => string.Join(',', filter.Extensions));
        try
        {
            var startIn = defaultFolder is not null && Directory.Exists(defaultFolder) ? defaultFolder : null;
            var status = Nfd.OpenDialog(out var picked, nfdFilters, startIn!);
            if (status == NfdStatus.Ok && !string.IsNullOrEmpty(picked))
            {
                path = picked;
                return FilePickResult.Picked;
            }

            return FilePickResult.Cancelled;
        }
        catch (Exception ex) when (ex is NfdException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = ex is NfdException ? ex.Message : "File picker is not available on this platform";
            Console.WriteLine($"[FilePicker] {ex.GetType().Name}: {ex.Message}");
            return FilePickResult.Failed;
        }
    }
}
