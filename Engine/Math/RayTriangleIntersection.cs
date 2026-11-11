namespace Wisko.Engine.Math;

public static class RayTriangleIntersection
{
    /// <summary>
    /// Möller–Trumbore, two-sided (geometry is drawn without back-face culling). <paramref name="direction"/> need not be
    /// normalized: <paramref name="t"/> is in units of it, so a ray mapped into another space by an affine transform
    /// keeps the same t as in the original space. Only hits with t &gt;= 0 count.
    /// </summary>
    public static bool TryIntersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, p);

        // Parallel / degenerate, relative to the triangle's and ray's own scale.
        var scale = edge1.Length * edge2.Length * direction.Length;
        if (!(global::System.Math.Abs(determinant) > 1e-7f * scale))
        {
            return false;
        }

        var inverse = 1f / determinant;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inverse;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        var q = Vector3.Cross(s, edge1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        t = Vector3.Dot(edge2, q) * inverse;
        return t >= 0f;
    }
}
