using System.Text.Json;
using Wisko.Engine.Graphics;
using Wisko.Engine.Platform;

namespace Wisko.Engine.Scripting;

/// <summary>
/// The person's script settings for this computer, kept in "editor-scripts.json" next to the editor's graphics
/// preferences (<see cref="EditorGraphicsPreferences.DefaultFolder"/>). The Lua memory limit protects this computer
/// from a runaway script, so it belongs to the computer and not to a project: a project someone else made cannot raise
/// it. Reading never throws (anything missing or invalid falls back to the default, and values are clamped to
/// <see cref="MinimumMemoryLimitMegabytes"/>..<see cref="MaximumMemoryLimitMegabytes"/>); saving is atomic.
/// </summary>
public sealed record EditorScriptPreferences
{
    public const string FileName = "editor-scripts.json";

    /// <summary>
    /// 256 MB for every script of a Play session together (see docs/lua-limits.md for the measurements behind it): far
    /// above what the demos and showcases use, and small enough that a runaway script is stopped long before it can
    /// endanger the editor on an 8 GB computer.
    /// </summary>
    public const int DefaultMemoryLimitMegabytes = 256;

    public const int MinimumMemoryLimitMegabytes = 32;
    public const int MaximumMemoryLimitMegabytes = 4096;

    public const long DefaultMemoryLimitBytes = (long)DefaultMemoryLimitMegabytes << 20;

    public static EditorScriptPreferences Default { get; } = new();

    public int MemoryLimitMegabytes { get; init; } = DefaultMemoryLimitMegabytes;

    public long MemoryLimitBytes => (long)global::System.Math.Clamp(MemoryLimitMegabytes, MinimumMemoryLimitMegabytes, MaximumMemoryLimitMegabytes) << 20;

    public static EditorScriptPreferences Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("MemoryLimitMegabytes", out var limit)
                && limit.ValueKind == JsonValueKind.Number
                && limit.TryGetInt32(out var megabytes))
            {
                return new EditorScriptPreferences { MemoryLimitMegabytes = global::System.Math.Clamp(megabytes, MinimumMemoryLimitMegabytes, MaximumMemoryLimitMegabytes) };
            }

            return Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Scripts] Could not read script preferences {path}: {ex.Message}; using the defaults");
            return Default;
        }
    }

    public bool TrySave(string path, out string? error) =>
        AtomicFile.TryWriteAllText(path, $"{{\n  \"FormatVersion\": 1,\n  \"MemoryLimitMegabytes\": {MemoryLimitMegabytes}\n}}\n", out error);
}
