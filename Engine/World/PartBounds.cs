using Wisko.Engine.Math;

namespace Wisko.Engine.World;

internal readonly struct WorldAabb
{
    public WorldAabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Min { get; }
    public Vector3 Max { get; }

    public bool Overlaps(WorldAabb other)
    {
        return Min.X <= other.Max.X && Max.X >= other.Min.X
            && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
            && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
    }
}

internal static class PartBounds
{
    internal static WorldAabb GetWorldAabb(Part part)
    {
        return GetWorldAabb(part, part.Position, part.Size);
    }

    internal static WorldAabb GetWorldAabb(Part part, Vector3 position, Vector3 size)
    {
        var savedPosition = part.Position;
        var savedSize = part.Size;
        part.Position = position;
        part.Size = size;

        var aabb = ComputeWorldAabb(part.Position, part.Size, part.Rotation);

        part.Position = savedPosition;
        part.Size = savedSize;
        return aabb;
    }

    private static WorldAabb ComputeWorldAabb(Vector3 position, Vector3 size, Vector3 rotationDegrees)
    {
        var half = new Vector3(size.X * 0.5f, size.Y * 0.5f, size.Z * 0.5f);
        var rotation = Matrix4.CreateRotationEuler(rotationDegrees);
        var translation = Matrix4.CreateTranslation(position);
        var model = Matrix4.Multiply(translation, rotation);

        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                for (var z = -1; z <= 1; z += 2)
                {
                    var corner = TransformPoint(model, new Vector3(half.X * x, half.Y * y, half.Z * z));
                    min = new Vector3(
                        global::System.Math.Min(min.X, corner.X),
                        global::System.Math.Min(min.Y, corner.Y),
                        global::System.Math.Min(min.Z, corner.Z));
                    max = new Vector3(
                        global::System.Math.Max(max.X, corner.X),
                        global::System.Math.Max(max.Y, corner.Y),
                        global::System.Math.Max(max.Z, corner.Z));
                }
            }
        }

        return new WorldAabb(min, max);
    }

    internal static float GetSupportExtentAlongNormal(Part part, Vector3 worldNormal)
    {
        var normal = worldNormal.Normalized();
        var half = new Vector3(part.Size.X * 0.5f, part.Size.Y * 0.5f, part.Size.Z * 0.5f);
        var rotation = Matrix4.CreateRotationEuler(part.Rotation);

        var axisX = Rotate(rotation, Vector3.UnitX);
        var axisY = Rotate(rotation, Vector3.UnitY);
        var axisZ = Rotate(rotation, Vector3.UnitZ);

        return global::System.Math.Abs(half.X * Vector3.Dot(axisX, normal))
            + global::System.Math.Abs(half.Y * Vector3.Dot(axisY, normal))
            + global::System.Math.Abs(half.Z * Vector3.Dot(axisZ, normal));
    }

    private static Vector3 Rotate(Matrix4 rotation, Vector3 vector) =>
        new(
            rotation.M11 * vector.X + rotation.M12 * vector.Y + rotation.M13 * vector.Z,
            rotation.M21 * vector.X + rotation.M22 * vector.Y + rotation.M23 * vector.Z,
            rotation.M31 * vector.X + rotation.M32 * vector.Y + rotation.M33 * vector.Z);

    private static Vector3 TransformPoint(Matrix4 matrix, Vector3 point) =>
        new(
            matrix.M11 * point.X + matrix.M12 * point.Y + matrix.M13 * point.Z + matrix.M14,
            matrix.M21 * point.X + matrix.M22 * point.Y + matrix.M23 * point.Z + matrix.M24,
            matrix.M31 * point.X + matrix.M32 * point.Y + matrix.M33 * point.Z + matrix.M34);
}
