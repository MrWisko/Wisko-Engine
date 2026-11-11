using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering;

internal static class MouseRay
{

    public static Ray FromScreen(float mouseX, float mouseY, int viewportWidth, int viewportHeight, EditorCamera camera)
    {
        var width = global::System.Math.Max(viewportWidth, 1);
        var height = global::System.Math.Max(viewportHeight, 1);

        var ndcX = (2f * mouseX / width) - 1f;
        var ndcY = 1f - (2f * mouseY / height);

        var forward = camera.Forward;
        var right = camera.Right;
        var up = camera.Up;

        var tanHalfFov = MathF.Tan(camera.FieldOfViewDegrees * MathUtil.DegreesToRadians * 0.5f);
        var aspect = width / (float)height;

        var direction = forward
            + right * (ndcX * aspect * tanHalfFov)
            + up * (ndcY * tanHalfFov);

        return new Ray(camera.Position, direction);
    }
}
