namespace Wisko.Engine.Rendering.Backend;

/// <summary>
/// Parses the startup renderer choice: <c>--renderer=opengl</c> or <c>--renderer=bgfx</c>
/// (case-insensitive). No renderer argument means OpenGL. Other arguments are ignored.
/// </summary>
public static class RenderBackendArguments
{
    private const string Prefix = "--renderer";

    public const string ValidValues = "opengl, bgfx";

    public static bool TryParse(IReadOnlyList<string> args, out RenderBackendKind backend, out string? error)
    {
        backend = RenderBackendKind.OpenGL;
        error = null;

        foreach (var arg in args)
        {
            if (!arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.Length == Prefix.Length || arg[Prefix.Length] != '=')
            {
                if (arg.Length != Prefix.Length)
                {
                    continue; // some other option that merely starts with "--renderer"
                }

                error = $"Missing renderer value. Use --renderer=<value> with one of: {ValidValues}.";
                return false;
            }

            var value = arg[(Prefix.Length + 1)..].Trim();
            if (value.Equals("opengl", StringComparison.OrdinalIgnoreCase))
            {
                backend = RenderBackendKind.OpenGL;
            }
            else if (value.Equals("bgfx", StringComparison.OrdinalIgnoreCase))
            {
                backend = RenderBackendKind.Bgfx;
            }
            else
            {
                error = $"Unknown renderer '{value}'. Valid values: {ValidValues}.";
                return false;
            }
        }

        return true;
    }
}
