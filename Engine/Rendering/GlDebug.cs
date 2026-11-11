using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

internal static class GlDebug
{
    public static void CheckError(string operation)
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Console.WriteLine($"[OpenGL] Error after {operation}: {error}");
        }
    }
}
