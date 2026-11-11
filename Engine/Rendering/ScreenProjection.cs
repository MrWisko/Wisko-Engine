using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering;

internal static class ScreenProjection
{
    public static bool TryProjectWorldPoint(
        Vector3 worldPoint,
        EditorCamera camera,
        int screenWidth,
        int screenHeight,
        out float screenX,
        out float screenY)
    {
        screenX = 0f;
        screenY = 0f;

        var aspect = screenWidth / (float)global::System.Math.Max(screenHeight, 1);
        var viewProjection = Matrix4.Multiply(camera.GetProjectionMatrix(aspect), camera.ViewMatrix);
        TransformPoint(viewProjection, worldPoint, out var clip, out var w);

        if (w <= 1e-4f)
        {
            return false;
        }

        var ndcX = clip.X / w;
        var ndcY = clip.Y / w;
        screenX = (ndcX + 1f) * 0.5f * screenWidth;
        screenY = (1f - ndcY) * 0.5f * screenHeight;
        return true;
    }

    public static bool TryProjectWorldBounds(
        Vector3 worldMin,
        Vector3 worldMax,
        EditorCamera camera,
        int screenWidth,
        int screenHeight,
        out ScreenRect bounds)
    {
        bounds = default;
        var hasPoint = false;
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;

        for (var xi = 0; xi < 2; xi++)
        {
            for (var yi = 0; yi < 2; yi++)
            {
                for (var zi = 0; zi < 2; zi++)
                {
                    var corner = new Vector3(
                        xi == 0 ? worldMin.X : worldMax.X,
                        yi == 0 ? worldMin.Y : worldMax.Y,
                        zi == 0 ? worldMin.Z : worldMax.Z);

                    if (!TryProjectWorldPoint(corner, camera, screenWidth, screenHeight, out var screenX, out var screenY))
                    {
                        continue;
                    }

                    hasPoint = true;
                    minX = global::System.Math.Min(minX, screenX);
                    minY = global::System.Math.Min(minY, screenY);
                    maxX = global::System.Math.Max(maxX, screenX);
                    maxY = global::System.Math.Max(maxY, screenY);
                }
            }
        }

        if (!hasPoint)
        {
            return false;
        }

        bounds = new ScreenRect(minX, minY, maxX - minX, maxY - minY);
        return true;
    }

    private static void TransformPoint(Matrix4 matrix, Vector3 point, out Vector3 transformed, out float w)
    {
        transformed = new Vector3(
            point.X * matrix.M11 + point.Y * matrix.M12 + point.Z * matrix.M13 + matrix.M14,
            point.X * matrix.M21 + point.Y * matrix.M22 + point.Z * matrix.M23 + matrix.M24,
            point.X * matrix.M31 + point.Y * matrix.M32 + point.Z * matrix.M33 + matrix.M34);
        w = point.X * matrix.M41 + point.Y * matrix.M42 + point.Z * matrix.M43 + matrix.M44;
    }

    internal readonly struct ScreenRect(float x, float y, float width, float height)
    {
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Width { get; } = width;
        public float Height { get; } = height;

        public bool Overlaps(ScreenRect other)
        {
            return X <= other.X + other.Width
                && X + Width >= other.X
                && Y <= other.Y + other.Height
                && Y + Height >= other.Y;
        }

        public bool ContainsPoint(float pointX, float pointY)
        {
            return pointX >= X
                && pointX <= X + Width
                && pointY >= Y
                && pointY <= Y + Height;
        }
    }
}
