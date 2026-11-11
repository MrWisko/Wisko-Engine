using System.Text.Json;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Graphics;

/// <summary>The quality the editor's 3D View draws at: the project's own level, or a fixed one for this computer.</summary>
public enum ViewportQuality
{
    Project,
    Low,
    Medium,
    High,
    Ultra
}

/// <summary>
/// The person's editor preferences for graphics (not part of any project): kept in "editor-graphics.json" in this
/// computer's application data (<see cref="DefaultFolder"/>), so a slow laptop can preview at Low without changing what
/// the project ships with. Reading never throws (anything missing or invalid falls back to the defaults); saving is
/// atomic.
/// </summary>
public sealed record EditorGraphicsPreferences
{
    public const string FileName = "editor-graphics.json";

    /// <summary>%LOCALAPPDATA%\Wisko on Windows; ~/Library/Application Support/Wisko on macOS.</summary>
    public static string DefaultFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Wisko")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisko");

    public static EditorGraphicsPreferences Default { get; } = new();

    public ViewportQuality ViewportQuality { get; init; } = ViewportQuality.Project;

    /// <summary>The level the editor renders at for a project whose own level is <paramref name="projectLevel"/>.</summary>
    public QualityLevel EffectiveLevel(QualityLevel projectLevel) => ViewportQuality switch
    {
        ViewportQuality.Low => QualityLevel.Low,
        ViewportQuality.Medium => QualityLevel.Medium,
        ViewportQuality.High => QualityLevel.High,
        ViewportQuality.Ultra => QualityLevel.Ultra,
        _ => projectLevel
    };

    public static EditorGraphicsPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? new EditorGraphicsPreferences { ViewportQuality = GraphicsFile.EnumValue(root, "ViewportQuality", ViewportQuality.Project) }
                : Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Graphics] Could not read editor preferences {path}: {ex.Message}; using the defaults");
            return Default;
        }
    }

    public bool TrySave(string path, out string? error) =>
        AtomicFile.TryWriteAllText(path, $"{{\n  \"FormatVersion\": 1,\n  \"ViewportQuality\": \"{ViewportQuality}\"\n}}\n", out error);
}
