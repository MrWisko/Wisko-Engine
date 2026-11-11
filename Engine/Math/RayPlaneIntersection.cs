namespace Wisko.Engine.Math;

public static class RayPlaneIntersection
{
    public static bool TryIntersect(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        hit = Vector3.Zero;
        var denominator = Vector3.Dot(planeNormal, ray.Direction);

        if (global::System.Math.Abs(denominator) < 1e-6f)
        {
            return false;
        }

        var distance = Vector3.Dot(planePoint - ray.Origin, planeNormal) / denominator;
        if (distance < 0f)
        {
            return false;
        }

        hit = ray.Origin + ray.Direction * distance;
        return true;
    }

    public static bool TryIntersectHorizontal(Ray ray, float planeY, out Vector3 hit)
    {
        hit = Vector3.Zero;

        if (global::System.Math.Abs(ray.Direction.Y) < 1e-6f)
        {
            return false;
        }

        var distance = (planeY - ray.Origin.Y) / ray.Direction.Y;
        if (distance < 0f)
        {
            return false;
        }

        hit = ray.Origin + ray.Direction * distance;
        return true;
    }
}
