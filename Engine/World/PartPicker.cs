using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;

namespace Wisko.Engine.World;

public static class PartPicker
{
    public static Part? Pick(Ray ray, Workspace workspace)
    {
        return Pick(ray, workspace, out _);
    }

    public static Part? Pick(Ray ray, Workspace workspace, out Vector3 hitPoint) => Pick(ray, workspace, out hitPoint, skipLocked: false);

    /// <summary>The closest Part the ray hits; with <paramref name="skipLocked"/>, locked Parts are looked through.</summary>
    public static Part? Pick(Ray ray, Workspace workspace, out Vector3 hitPoint, bool skipLocked)
    {
        Part? closest = null;
        var closestDistance = float.MaxValue;
        hitPoint = Vector3.Zero;

        foreach (var part in workspace.Parts)
        {
            // A Part a script hid (Play) cannot be clicked either.
            if ((skipLocked && part.Locked) || !part.Visible)
            {
                continue;
            }

            if (TryIntersect(ray, part, out var distance) && distance < closestDistance)
            {
                closest = part;
                closestDistance = distance;
            }
        }

        if (closest is not null)
        {
            hitPoint = ray.Origin + ray.Direction * closestDistance;
        }

        return closest;
    }

    private static bool TryIntersect(Ray ray, Part part, out float distance)
    {
        var rotation = Matrix4.CreateRotationEuler(part.Rotation);
        var localOrigin = InverseRotate(rotation, ray.Origin - part.Position);
        var localDirection = InverseRotate(rotation, ray.Direction);

        var half = new Vector3(part.Size.X * 0.5f, part.Size.Y * 0.5f, part.Size.Z * 0.5f);
        var min = new Vector3(-half.X, -half.Y, -half.Z);
        var max = half;

        if (!TryIntersectAabb(localOrigin, localDirection, min, max, out var localDistance))
        {
            distance = 0f;
            return false;
        }

        // A model's box is only the broad phase: the hit must land on one of its triangles.
        if (part.Model?.Asset is { } asset)
        {
            return TryIntersectModel(ray, part, asset, out distance);
        }

        distance = localDistance;
        return true;
    }

    // Exact model pick: the world ray is mapped into each mesh's own space (instance * node transform, inverted), then
    // tested against that mesh's bounds and triangles. The mapping is affine and the world direction is unit length,
    // so the t found in mesh space is the world distance. An animated instance is picked in the pose it is drawn in: a
    // rigid mesh where its node is now, a skinned mesh skinned on the CPU for this pick (model space; its draw transform is
    // identity). The broad phase stays the Part's box (the rest pose's bounds).
    private static bool TryIntersectModel(Ray ray, Part part, ModelAsset asset, out float distance)
    {
        var instance = ModelTransform.GetInstanceMatrix(part);
        var pose = part.Animator?.Pose;
        var closest = float.MaxValue;
        foreach (var draw in asset.Draws)
        {
            var drawTransform = pose is not null && !draw.Skinned && draw.Node >= 0 ? pose.GlobalOf(draw.Node) : draw.Transform;
            if (!Matrix4.TryInvertAffine(Matrix4.Multiply(instance, drawTransform), out var worldToMesh))
            {
                continue;
            }

            var mesh = asset.Model.Meshes[draw.MeshIndex];
            var positions = asset.RestPositionsOf(draw);
            var meshBounds = asset.RestBoundsOf(draw);
            if (pose is not null && draw.Skinned)
            {
                positions = new Vector3[mesh.VertexCount];
                SkinningCpu.Skin(mesh, pose.PaletteOf(draw.MeshIndex), positions, []);
                meshBounds = ImportedBounds.Empty;
                foreach (var p in positions)
                {
                    meshBounds = meshBounds.Include(p);
                }
            }

            var origin = TransformPoint(worldToMesh, ray.Origin);
            var direction = TransformVector(worldToMesh, ray.Direction);
            if (meshBounds.IsEmpty
                || !TryIntersectAabb(origin, direction, meshBounds.Min, meshBounds.Max, out _))
            {
                continue;
            }

            var indices = mesh.Indices;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                if (RayTriangleIntersection.TryIntersect(
                        origin, direction, positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]], out var t)
                    && t < closest)
                {
                    closest = t;
                }
            }
        }

        distance = closest;
        return closest < float.MaxValue;
    }

    private static Vector3 TransformPoint(Matrix4 m, Vector3 p) =>
        new(
            m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
            m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
            m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);

    private static Vector3 TransformVector(Matrix4 m, Vector3 v) =>
        new(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

    private static Vector3 InverseRotate(Matrix4 rotation, Vector3 vector) =>
        new(
            rotation.M11 * vector.X + rotation.M21 * vector.Y + rotation.M31 * vector.Z,
            rotation.M12 * vector.X + rotation.M22 * vector.Y + rotation.M32 * vector.Z,
            rotation.M13 * vector.X + rotation.M23 * vector.Y + rotation.M33 * vector.Z);

    private static bool TryIntersectAabb(
        Vector3 origin,
        Vector3 direction,
        Vector3 min,
        Vector3 max,
        out float distance)
    {
        distance = 0f;
        var tMin = float.NegativeInfinity;
        var tMax = float.PositiveInfinity;

        if (!Slab(origin.X, direction.X, min.X, max.X, ref tMin, ref tMax) ||
            !Slab(origin.Y, direction.Y, min.Y, max.Y, ref tMin, ref tMax) ||
            !Slab(origin.Z, direction.Z, min.Z, max.Z, ref tMin, ref tMax))
        {
            return false;
        }

        if (tMax < 0f)
        {
            return false;
        }

        distance = tMin >= 0f ? tMin : tMax;
        return distance >= 0f;
    }

    private static bool Slab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
    {
        if (global::System.Math.Abs(direction) < 1e-6f)
        {
            return origin >= min && origin <= max;
        }

        var inverse = 1f / direction;
        var t0 = (min - origin) * inverse;
        var t1 = (max - origin) * inverse;

        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        tMin = global::System.Math.Max(tMin, t0);
        tMax = global::System.Math.Min(tMax, t1);
        return tMin <= tMax;
    }
}
