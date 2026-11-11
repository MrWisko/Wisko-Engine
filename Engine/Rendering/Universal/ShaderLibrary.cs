namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// The Universal renderer's GLSL programs: sources embedded in the engine ("Engine.Universal.*"), composed as
/// <c>#version 330 core</c> + the variant's #defines + (fragment stage) common.glsl and the shared Atmosphere
/// functions + the file. Each (vertex, fragment, defines) variant is compiled once and cached for the renderer's
/// lifetime; nothing is compiled per frame. A failure never throws: the program is null, the error is kept in
/// <see cref="Errors"/> and logged once, and the renderer falls back to the Classic renderer.
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    private readonly Dictionary<string, GlProgram?> _programs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    /// <summary>
    /// Test hook: a shader file name (e.g. "surface.frag") whose source is deliberately broken, to verify that a
    /// compile failure falls back safely. Null in normal use.
    /// </summary>
    internal static string? BreakForTests { get; set; }

    public IReadOnlyList<string> Errors => _errors;

    public int CompiledCount => _programs.Values.Count(p => p is not null);

    /// <summary>Milliseconds spent compiling and linking so far (start-up cost diagnostics).</summary>
    public double CompileMilliseconds { get; private set; }

    public GlProgram? Get(string vertex, string fragment, params string[] defines)
    {
        var key = vertex + "|" + fragment + "|" + string.Join(",", defines);
        if (_programs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        GlProgram? program = null;
        string? error;
        try
        {
            var header = "#version 330 core\n" + string.Concat(defines.Select(d => $"#define {d}\n"));
            var vertexSource = header + "#line 1\n" + Source(vertex);
            var fragmentSource = header + Source("common.glsl") + "\n" + AtmosphereSkyRenderer.AtmosphereFunctions + "\n#line 1\n" + Source(fragment);
            program = GlProgram.TryCreate($"{vertex}+{fragment}{(defines.Length > 0 ? " [" + string.Join(", ", defines) + "]" : string.Empty)}",
                vertexSource, fragmentSource, out error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            error = ex.Message;
        }

        CompileMilliseconds += clock.Elapsed.TotalMilliseconds;
        if (program is null)
        {
            var message = error ?? "unknown error";
            _errors.Add(message);
            Console.WriteLine($"[Graphics] Shader failed: {message.Trim()}");
        }

        _programs[key] = program;
        return program;
    }

    private string Source(string name)
    {
        if (!_sources.TryGetValue(name, out var text))
        {
            var resource = "Engine.Universal." + name;
            using var stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded shader source not found: {resource}");
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
            _sources[name] = text;
        }

        return string.Equals(BreakForTests, name, StringComparison.Ordinal) ? text + "\n this is not GLSL;\n" : text;
    }

    public void Dispose()
    {
        foreach (var program in _programs.Values)
        {
            program?.Dispose();
        }

        _programs.Clear();
    }
}
