using Wisko.Engine.Math;

namespace Wisko.Engine.World;

public enum SurfaceKind
{
    Baseplate,
    Part
}

public readonly struct SurfaceHit
{
    public SurfaceHit(SurfaceKind kind, Vector3 point, Vector3 normal, float distance, Part? part)
    {
        Kind = kind;
        Point = point;
        Normal = normal;
        Distance = distance;
        Part = part;
    }

    public SurfaceKind Kind { get; }
    public Vector3 Point { get; }
    public Vector3 Normal { get; }
    public float Distance { get; }
    public Part? Part { get; }
}

/// <summary>
/// The surface under the cursor for Select-tool dragging: the closest Part face (its exact transformed geometry, see
/// <see cref="PartShape"/>: a Wedge's slope, a rotated Block's tilted face, a Sphere's curve) or the baseplate, with the
/// hit point and that surface's normal.
/// </summary>
public static class SurfaceRaycast
{
    private const float SurfaceEpsilon = 1e-3f;

    public static bool TryCast(Ray ray, Workspace workspace, Part? ignorePart, out SurfaceHit hit) =>
        TryCast(ray, workspace, part => ReferenceEquals(part, ignorePart), out hit);

    /// <summary>The closest surface, looking through every Part in <paramref name="ignoreParts"/> (the ones being dragged).</summary>
    public static bool TryCast(Ray ray, Workspace workspace, IReadOnlySet<Part> ignoreParts, out SurfaceHit hit) =>
        TryCast(ray, workspace, ignoreParts.Contains, out hit);

    /// <summary>
    /// Where the dragged Part's centre goes so that it rests on the hit surface, in its own rotation (which is kept):
    /// above the hit point along the surface normal, with its own farthest point against that normal (its exact shape)
    /// just touching the surface's plane. Where the hit Part's real shape falls away from that plane under it (a curved
    /// surface, or the Part reaching past the face's edge), it is lowered along the normal until it meets that shape.
    /// </summary>
    public static Vector3 ComputePlacementPosition(Part draggedPart, in SurfaceHit surfaceHit)
    {
        var normal = surfaceHit.Normal;
        var below = Vector3.Zero - normal;
        var supportExtent = PartShape.Of(draggedPart).ExtentAlong(below);
        var onPlane = surfaceHit.Point + normal * (supportExtent + SurfaceEpsilon);
        if (surfaceHit.Part is not { } target)
        {
            return onPlane;
        }

        // On a flat face this is the gap left above the plane (nothing changes); the same gap is kept on curves.
        var travel = PartShape.DistanceUntilContact(
            PartShape.Of(draggedPart, onPlane), PartShape.Of(target), below, supportExtent + SurfaceEpsilon);
        var lowered = travel - SurfaceEpsilon - PartShape.ContactTolerance;
        return lowered > 0f ? onPlane + below * lowered : onPlane;
    }

    public static string DescribeSurface(in SurfaceHit hit) =>
        hit.Kind switch
        {
            SurfaceKind.Baseplate => "baseplate",
            SurfaceKind.Part when hit.Part is not null => hit.Part.Name,
            _ => "unknown"
        };

    private static bool TryCast(Ray ray, Workspace workspace, Func<Part, bool> ignore, out SurfaceHit hit)
    {
        hit = default;
        var found = false;
        var bestDistance = float.MaxValue;
        SurfaceHit bestHit = default;

        foreach (var part in workspace.Parts)
        {
            if (ignore(part))
            {
                continue;
            }

            if (PartShape.Of(part).TryRaycast(ray, out var distance, out var normal) && distance < bestDistance)
            {
                bestDistance = distance;
                bestHit = new SurfaceHit(SurfaceKind.Part, ray.Origin + ray.Direction * distance, normal, distance, part);
                found = true;
            }
        }

        // A world whose baseplate was deleted has no ground to rest on (only Parts).
        if (workspace.HasBaseplate
            && TryIntersectBaseplate(ray, out var baseDistance, out var basePoint, out var baseNormal)
            && baseDistance < bestDistance)
        {
            bestHit = new SurfaceHit(SurfaceKind.Baseplate, basePoint, baseNormal, baseDistance, null);
            found = true;
        }

        if (!found)
        {
            return false;
        }

        hit = bestHit;
        return true;
    }

    private static bool TryIntersectBaseplate(
        Ray ray,
        out float distance,
        out Vector3 point,
        out Vector3 normal)
    {
        distance = 0f;
        point = Vector3.Zero;
        normal = Vector3.UnitY;

        if (!RayPlaneIntersection.TryIntersect(ray, Vector3.Zero, Vector3.UnitY, out point))
        {
            return false;
        }

        distance = Vector3.Dot(point - ray.Origin, ray.Direction);
        return distance >= 0f;
    }
}
