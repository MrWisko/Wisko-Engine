namespace Wisko.Engine.Platform;

/// <summary>Native "open script" dialog (see <see cref="NativeFilePicker"/>), limited to Lua scripts.</summary>
internal static class ScriptFilePicker
{
    private static readonly FileTypeFilter[] Filters = [new("Lua scripts", ["lua"])];

    public static FilePickResult TryPick(string? defaultFolder, out string? path, out string? error) =>
        NativeFilePicker.TryPickFile(Filters, defaultFolder, out path, out error);
}
