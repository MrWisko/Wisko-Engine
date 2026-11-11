using System.Text;
using KeraLua;
using LuaState = KeraLua.Lua;

namespace Wisko.Engine.ScriptEditor;

/// <summary>A syntax error at a zero-based line.</summary>
public sealed record SyntaxDiagnostic(int Line, string Message);

/// <summary>
/// Compile-only Lua syntax checking: the source is loaded (compiled) in a private, library-less Lua state and the
/// resulting function is discarded without ever being called, so no top-level code, Start or Update runs. Same Lua
/// version and text-only mode as the script runtime, so the errors match what Play reports.
/// </summary>
public sealed class LuaSyntaxValidator : IDisposable
{
    private const string ChunkName = "script";
    private const int MaxMessageLength = 300;

    private readonly LuaState _state = new(openLibs: false) { Encoding = Encoding.UTF8 };
    private bool _disposed;

    public SyntaxDiagnostic? Validate(string source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var top = _state.GetTop();
        try
        {
            if (_state.LoadBuffer(Encoding.UTF8.GetBytes(source), "=" + ChunkName, "t") == LuaStatus.OK)
            {
                return null;
            }

            return Parse(_state.ToString(-1, false) ?? "syntax error");
        }
        finally
        {
            _state.SetTop(top);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state.Dispose();
    }

    // "script:12: '=' expected near 'x'" -> line 11 (zero-based) + message.
    private static SyntaxDiagnostic Parse(string text)
    {
        var line = 0;
        var message = text;
        var prefix = ChunkName + ":";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = text[prefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon > 0 && int.TryParse(rest.AsSpan(0, colon), out var parsed))
            {
                line = System.Math.Max(0, parsed - 1);
                message = rest[(colon + 1)..].Trim();
            }
        }

        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + "...";
        }

        return new SyntaxDiagnostic(line, message);
    }
}
