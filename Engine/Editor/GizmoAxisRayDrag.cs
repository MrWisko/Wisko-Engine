using Wisko.Engine.Math;
using Wisko.Engine.Rendering;

namespace Wisko.Engine.Editor;

internal static class GizmoAxisRayDrag
{
    public static bool TryProjectRayOnAxis(
        Ray ray,
        Vector3 axisOrigin,
        Vector3 axisWorld,
        EditorCamera camera,
        out float axisScalar)
    {
        axisScalar = 0f;

        var axis = axisWorld.Normalized();
        if (!TryBuildDragPlaneNormal(axis, camera, out var planeNormal))
        {
            return false;
        }

        if (!RayPlaneIntersection.TryIntersect(ray, axisOrigin, planeNormal, out var hit))
        {
            return false;
        }

        axisScalar = Vector3.Dot(hit - axisOrigin, axis);
        return true;
    }

    private static bool TryBuildDragPlaneNormal(Vector3 axis, EditorCamera camera, out Vector3 planeNormal)
    {
        planeNormal = Vector3.Zero;

        var tangent = camera.Forward - axis * Vector3.Dot(camera.Forward, axis);
        if (Vector3.Dot(tangent, tangent) < 1e-8f)
        {
            tangent = camera.Up - axis * Vector3.Dot(camera.Up, axis);
        }

        if (Vector3.Dot(tangent, tangent) < 1e-8f)
        {
            tangent = camera.Right - axis * Vector3.Dot(camera.Right, axis);
        }

        if (Vector3.Dot(tangent, tangent) < 1e-8f)
        {
            return false;
        }

        planeNormal = Vector3.Cross(axis, tangent).Normalized();
        return Vector3.Dot(planeNormal, planeNormal) > 1e-8f;
    }
}
