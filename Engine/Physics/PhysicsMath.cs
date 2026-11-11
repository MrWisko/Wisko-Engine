using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Physics;

/// <summary>
/// The boundary between Wisko's transform terms and the physics runtime's: Wisko <see cref="Vector3"/> versus
/// System.Numerics, and a Part's Rotation (Euler degrees) versus a unit quaternion.
/// <para>
/// A Part's Rotation means exactly what the renderer draws: <c>Matrix4.CreateRotationEuler</c> composes
/// RY(Y)·RX(X)·RZ(Z) acting on column vectors (Z first, then X, then Y). Wisko's RY turns by +Y about the axis
/// (right-handed), while its RX and RZ are the transposed matrices, i.e. turn by −X and −Z. The same orientation as a
/// quaternion is therefore qY(Y)·qX(−X)·qZ(−Z).
/// </para>
/// </summary>
public static class PhysicsMath
{
    private const float RadiansToDegrees = 180f / MathF.PI;

    // Beyond this |sin(tilt)| the X tilt is treated as ±90 degrees (gimbal lock): Y and Z then describe the same turn.
    private const float GimbalThreshold = 0.99999f;

    public static NVector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);

    public static Vector3 ToWisko(NVector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>The orientation a Part with this Rotation (Euler degrees) is drawn with.</summary>
    public static NQuaternion EulerDegreesToQuaternion(Vector3 rotationDegrees)
    {
        var y = NQuaternion.CreateFromAxisAngle(NVector3.UnitY, rotationDegrees.Y * global::Wisko.Engine.Math.MathUtil.DegreesToRadians);
        var x = NQuaternion.CreateFromAxisAngle(NVector3.UnitX, -rotationDegrees.X * global::Wisko.Engine.Math.MathUtil.DegreesToRadians);
        var z = NQuaternion.CreateFromAxisAngle(NVector3.UnitZ, -rotationDegrees.Z * global::Wisko.Engine.Math.MathUtil.DegreesToRadians);
        return NQuaternion.Normalize(y * x * z);
    }

    /// <summary>
    /// A Rotation (Euler degrees, each in (−180, 180]) the renderer draws with exactly this orientation. X stays within
    /// [−90, 90]; at ±90 (gimbal lock) the whole turn about the vertical is put in Y and Z is 0.
    /// </summary>
    public static Vector3 QuaternionToEulerDegrees(NQuaternion orientation)
    {
        var q = NQuaternion.Normalize(orientation);
        float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        float xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        float wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;

        // Rotation matrix entries (row, column) for column vectors.
        var m00 = 1f - 2f * (yy + zz);
        var m02 = 2f * (xz + wy);
        var m10 = 2f * (xy + wz);
        var m11 = 1f - 2f * (xx + zz);
        var m12 = 2f * (yz - wx);
        var m20 = 2f * (xz - wy);
        var m22 = 1f - 2f * (xx + yy);

        // In standard terms the matrix is Ry(y)·Rx(a)·Rz(c) with a = −X and c = −Z: m12 = −sin(a).
        var sinA = global::System.Math.Clamp(-m12, -1f, 1f);
        float a, yAngle, c;
        if (MathF.Abs(sinA) < GimbalThreshold)
        {
            a = MathF.Asin(sinA);
            yAngle = MathF.Atan2(m02, m22);
            c = MathF.Atan2(m10, m11);
        }
        else
        {
            a = sinA > 0f ? MathF.PI * 0.5f : -MathF.PI * 0.5f;
            yAngle = MathF.Atan2(-m20, m00);
            c = 0f;
        }

        return new Vector3(
            NormalizeDegrees(-a * RadiansToDegrees),
            NormalizeDegrees(yAngle * RadiansToDegrees),
            NormalizeDegrees(-c * RadiansToDegrees));
    }

    /// <summary>The same angle in (−180, 180].</summary>
    public static float NormalizeDegrees(float degrees)
    {
        var wrapped = degrees % 360f;
        if (wrapped <= -180f)
        {
            wrapped += 360f;
        }
        else if (wrapped > 180f)
        {
            wrapped -= 360f;
        }

        return wrapped == 0f ? 0f : wrapped; // no −0
    }

    public static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static bool IsFinite(NVector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static bool IsFinite(NQuaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
