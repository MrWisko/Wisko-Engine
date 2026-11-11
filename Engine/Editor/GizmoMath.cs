using Wisko.Engine.Math;
using Wisko.Engine.Rendering;

namespace Wisko.Engine.Editor;

internal static class GizmoMath
{
    private const float ScreenPickThresholdPx = 28f;

    public static bool TryPickMoveAxis(
        Ray ray,
        Vector3 origin,
        float gizmoScale,
        EditorCamera camera,
        float mouseX,
        float mouseY,
        int viewportWidth,
        int viewportHeight,
        out GizmoAxis axis,
        out float pickScore)
    {
        axis = GizmoAxis.None;
        pickScore = float.MaxValue;

        var arrowLength = gizmoScale * GizmoMeshFactory.UnitArrowLength;
        var worldPickRadius = gizmoScale * 0.55f;
        var headCenterDist = gizmoScale * 1.14f;
        var headRadius = gizmoScale * 0.42f;

        var aspect = viewportWidth / (float)global::System.Math.Max(viewportHeight, 1);
        var view = camera.ViewMatrix;
        var projection = camera.GetProjectionMatrix(aspect);
        var mouse = new Vector2(mouseX, mouseY);

        TryPickAxisCandidate(
            ray, origin, Vector3.UnitX, GizmoAxis.X, arrowLength, worldPickRadius,
            headCenterDist, headRadius, view, projection, viewportWidth, viewportHeight, mouse,
            ref axis, ref pickScore);
        TryPickAxisCandidate(
            ray, origin, Vector3.UnitY, GizmoAxis.Y, arrowLength, worldPickRadius,
            headCenterDist, headRadius, view, projection, viewportWidth, viewportHeight, mouse,
            ref axis, ref pickScore);
        TryPickAxisCandidate(
            ray, origin, Vector3.UnitZ, GizmoAxis.Z, arrowLength, worldPickRadius,
            headCenterDist, headRadius, view, projection, viewportWidth, viewportHeight, mouse,
            ref axis, ref pickScore);

        return axis != GizmoAxis.None;
    }

    public static float GetAxisScalarFromRay(Ray ray, Vector3 axisOrigin, Vector3 axisDir)
    {
        if (TryClosestPointOnAxisLine(ray, axisOrigin, axisDir, out _, out var scalar))
        {
            return scalar;
        }

        return Vector3.Dot(ray.Origin - axisOrigin, axisDir.Normalized());
    }

    public static bool TryClosestPointOnAxisLine(
        Ray ray,
        Vector3 axisOrigin,
        Vector3 axisDir,
        out Vector3 pointOnAxis,
        out float axisScalar)
    {
        pointOnAxis = axisOrigin;
        axisScalar = 0f;

        var direction = axisDir.Normalized();
        var w0 = ray.Origin - axisOrigin;
        var a = Vector3.Dot(ray.Direction, ray.Direction);
        var b = Vector3.Dot(ray.Direction, direction);
        var c = Vector3.Dot(direction, direction);
        var d = Vector3.Dot(ray.Direction, w0);
        var e = Vector3.Dot(direction, w0);
        var denom = a * c - b * b;

        if (global::System.Math.Abs(denom) < 1e-6f)
        {
            return false;
        }

        axisScalar = (a * e - b * d) / denom;
        pointOnAxis = axisOrigin + direction * axisScalar;
        return true;
    }

    internal static bool TryWorldToScreen(
        Vector3 world,
        Matrix4 view,
        Matrix4 projection,
        int viewportWidth,
        int viewportHeight,
        out float screenX,
        out float screenY)
    {
        screenX = 0f;
        screenY = 0f;

        var viewPoint = TransformPoint(view, world);
        var clip = TransformPoint(projection, viewPoint);
        if (clip.W <= 1e-4f)
        {
            return false;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        var ndcZ = clip.Z / clip.W;
        if (ndcZ < -1f || ndcZ > 1f)
        {
            return false;
        }

        screenX = (ndcX + 1f) * 0.5f * viewportWidth;
        screenY = (1f - ndcY) * 0.5f * viewportHeight;
        return true;
    }

    private static void TryPickAxisCandidate(
        Ray ray,
        Vector3 origin,
        Vector3 axis,
        GizmoAxis axisId,
        float arrowLength,
        float worldPickRadius,
        float headCenterDist,
        float headRadius,
        Matrix4 view,
        Matrix4 projection,
        int viewportWidth,
        int viewportHeight,
        Vector2 mouse,
        ref GizmoAxis bestAxis,
        ref float bestScore)
    {
        var axisDir = axis.Normalized();
        var end = origin + axisDir * arrowLength;
        var headCenter = origin + axisDir * headCenterDist;

        var score = float.MaxValue;

        if (TryWorldToScreen(origin, view, projection, viewportWidth, viewportHeight, out var screenAx, out var screenAy)
            && TryWorldToScreen(end, view, projection, viewportWidth, viewportHeight, out var screenBx, out var screenBy))
        {
            var screenDist = DistancePointToSegment2D(mouse.X, mouse.Y, screenAx, screenAy, screenBx, screenBy);
            if (screenDist <= ScreenPickThresholdPx)
            {
                score = global::System.Math.Min(score, screenDist);
            }
        }

        if (TryWorldToScreen(headCenter, view, projection, viewportWidth, viewportHeight, out var screenHeadX, out var screenHeadY))
        {
            var headScreenRadius = global::System.Math.Max(headRadius * 8f, ScreenPickThresholdPx * 0.85f);
            var dx = mouse.X - screenHeadX;
            var dy = mouse.Y - screenHeadY;
            var headDist = MathF.Sqrt(dx * dx + dy * dy);
            if (headDist <= headScreenRadius)
            {
                score = global::System.Math.Min(score, headDist);
            }
        }

        if (TryRayToAxisSegment(ray, origin, end, worldPickRadius, out var rayDistance))
        {
            score = global::System.Math.Min(score, rayDistance * 0.01f);
        }

        if (TryPickSphere(ray, headCenter, headRadius, out var sphereDistance))
        {
            score = global::System.Math.Min(score, sphereDistance * 0.01f);
        }

        if (score < bestScore)
        {
            bestAxis = axisId;
            bestScore = score;
        }
    }

    internal static bool TryPickSphere(Ray ray, Vector3 center, float radius, out float distance)
    {
        distance = float.MaxValue;
        var offset = center - ray.Origin;
        var t = Vector3.Dot(offset, ray.Direction);
        if (t < 0f)
        {
            return false;
        }

        var closest = ray.Origin + ray.Direction * t;
        var diff = closest - center;
        var distSq = diff.X * diff.X + diff.Y * diff.Y + diff.Z * diff.Z;
        if (distSq > radius * radius)
        {
            return false;
        }

        var halfChord = MathF.Sqrt(radius * radius - distSq);
        distance = t - halfChord;
        return distance >= 0f;
    }

    private static bool TryRayToAxisSegment(Ray ray, Vector3 a, Vector3 b, float radius, out float rayDistance)
    {
        rayDistance = float.MaxValue;
        var ab = b - a;
        var abLenSq = ab.X * ab.X + ab.Y * ab.Y + ab.Z * ab.Z;
        if (abLenSq < 1e-6f)
        {
            return false;
        }

        var abLen = MathF.Sqrt(abLenSq);
        var abDir = ab / abLen;
        var ao = ray.Origin - a;
        var cross = Vector3.Cross(ray.Direction, abDir);
        var crossLenSq = cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z;

        if (crossLenSq < 1e-6f)
        {
            var perp = Vector3.Cross(ao, abDir);
            var perpLen = perp.Length;
            if (perpLen > radius)
            {
                return false;
            }

            var along = Vector3.Dot(ao, abDir);
            if (along < -radius || along > abLen + radius)
            {
                return false;
            }

            rayDistance = 0f;
            return true;
        }

        var crossLen = MathF.Sqrt(crossLenSq);
        var dist = global::System.Math.Abs(Vector3.Dot(ao, cross)) / crossLen;
        if (dist > radius)
        {
            return false;
        }

        var t = Vector3.Dot(Vector3.Cross(ao, abDir), cross) / crossLenSq;
        var s = Vector3.Dot(Vector3.Cross(ao, ray.Direction), cross) / crossLenSq;

        if (s < -radius * 0.5f || s > abLen + radius)
        {
            return false;
        }

        if (t < 0f)
        {
            return false;
        }

        rayDistance = t;
        return true;
    }

    private static float DistancePointToSegment2D(
        float pointX, float pointY,
        float ax, float ay,
        float bx, float by)
    {
        var abX = bx - ax;
        var abY = by - ay;
        var lenSq = abX * abX + abY * abY;
        if (lenSq < 1e-6f)
        {
            var dx = pointX - ax;
            var dy = pointY - ay;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        var t = ((pointX - ax) * abX + (pointY - ay) * abY) / lenSq;
        t = global::System.Math.Clamp(t, 0f, 1f);
        var cx = ax + abX * t;
        var cy = ay + abY * t;
        var dx2 = pointX - cx;
        var dy2 = pointY - cy;
        return MathF.Sqrt(dx2 * dx2 + dy2 * dy2);
    }

    private static Vector4 TransformPoint(Matrix4 matrix, Vector3 point) =>
        new(
            matrix.M11 * point.X + matrix.M12 * point.Y + matrix.M13 * point.Z + matrix.M14,
            matrix.M21 * point.X + matrix.M22 * point.Y + matrix.M23 * point.Z + matrix.M24,
            matrix.M31 * point.X + matrix.M32 * point.Y + matrix.M33 * point.Z + matrix.M34,
            matrix.M41 * point.X + matrix.M42 * point.Y + matrix.M43 * point.Z + matrix.M44);

    private static Vector4 TransformPoint(Matrix4 matrix, Vector4 point) =>
        new(
            matrix.M11 * point.X + matrix.M12 * point.Y + matrix.M13 * point.Z + matrix.M14 * point.W,
            matrix.M21 * point.X + matrix.M22 * point.Y + matrix.M23 * point.Z + matrix.M24 * point.W,
            matrix.M31 * point.X + matrix.M32 * point.Y + matrix.M33 * point.Z + matrix.M34 * point.W,
            matrix.M41 * point.X + matrix.M42 * point.Y + matrix.M43 * point.Z + matrix.M44 * point.W);

    private readonly struct Vector2(float x, float y)
    {
        public float X { get; } = x;
        public float Y { get; } = y;

        public float Length()
        {
            return MathF.Sqrt(X * X + Y * Y);
        }

        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    }

    private readonly struct Vector4(float x, float y, float z, float w)
    {
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Z { get; } = z;
        public float W { get; } = w;
    }
}

public enum GizmoAxis
{
    None,
    X,
    Y,
    Z,
    Purple, // dual-axis shortcut: world +X+Z (labelled "X+Z")
    Yellow  // dual-axis shortcut: world +X-Z (labelled "X-Z")
}
