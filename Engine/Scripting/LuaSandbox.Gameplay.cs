namespace Wisko.Engine.Scripting;

/// <summary>
/// Gameplay systems, Lua side: the gameplay prelude and the optional built-in modules, embedded in the engine as
/// plain Lua files (Engine/Scripting/Lua). The gameplay prelude is a second trusted chunk that the sandbox prelude runs
/// once per runtime with its own helpers; it adds the gameplay globals (Navigation, Perception, Random, Noise, Path,
/// GameTime, GameSave, Cameras, Particles, DebugDraw) and object methods to every script environment. The built-in
/// modules (Wisko.Dialogue, Wisko.Quests, ...) are ordinary untrusted Lua: a script's require loads one in that
/// script's own environment, exactly like a module from the project, and it can only use the public API.
/// </summary>
internal static partial class LuaSandbox
{
    private const string ModuleResourcePrefix = "Engine.Lua.Modules.";

    private static readonly Lazy<byte[]> GameplayPreludeSource = new(() => ReadResource("Engine.Lua.gameplay.lua")
        ?? throw new InvalidOperationException("the engine's gameplay prelude (Engine.Lua.gameplay.lua) is missing"));

    private static readonly Dictionary<string, byte[]> Modules = new(StringComparer.Ordinal);

    /// <summary>The gameplay prelude's source (UTF-8).</summary>
    public static byte[] GameplayPrelude => GameplayPreludeSource.Value;

    /// <summary>The names of the built-in modules ("Wisko.Dialogue", ...), sorted.</summary>
    public static IReadOnlyList<string> BuiltinModuleNames { get; } = typeof(LuaSandbox).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(ModuleResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".lua", StringComparison.Ordinal))
        .Select(name => "Wisko." + name[ModuleResourcePrefix.Length..^4])
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    /// <summary>A built-in module's source by its require name ("Wisko.Dialogue"); false for any other name.</summary>
    public static bool TryGetBuiltinModule(string name, out byte[] source)
    {
        source = [];
        if (!name.StartsWith("Wisko.", StringComparison.Ordinal) || !BuiltinModuleNames.Contains(name))
        {
            return false;
        }

        lock (Modules)
        {
            if (!Modules.TryGetValue(name, out var cached))
            {
                cached = ReadResource(ModuleResourcePrefix + name["Wisko.".Length..] + ".lua") ?? [];
                Modules[name] = cached;
            }

            source = cached;
        }

        return source.Length > 0;
    }

    private static byte[]? ReadResource(string logicalName)
    {
        using var stream = typeof(LuaSandbox).Assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
    }
}
