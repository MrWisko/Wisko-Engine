namespace Wisko.Engine.Platform;

/// <summary>Native "open image" dialog (see <see cref="NativeFilePicker"/>), filtered to PNG and JPEG.</summary>
internal static class ImageFilePicker
{
    private static readonly FileTypeFilter[] Filters = [new("Images (PNG, JPEG)", ["png", "jpg", "jpeg"])];

    public static FilePickResult TryPick(out string? path, out string? error) =>
        NativeFilePicker.TryPickFile(Filters, out path, out error);
}
