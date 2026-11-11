using Wisko.Engine.Assets;

namespace Wisko.Engine.Platform;

/// <summary>Native "open model" dialog (see <see cref="NativeFilePicker"/>), limited to the formats Import offers.</summary>
internal static class ModelFilePicker
{
    private static readonly FileTypeFilter[] Filters =
    [
        new("3D models (GLB, glTF, OBJ, FBX)", ProjectModelImporter.SupportedExtensions.Select(extension => extension.TrimStart('.')).ToArray())
    ];

    public static FilePickResult TryPick(out string? path, out string? error) =>
        NativeFilePicker.TryPickFile(Filters, out path, out error);
}
