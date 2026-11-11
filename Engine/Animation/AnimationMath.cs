using System.Numerics;
using Matrix4 = Wisko.Engine.Math.Matrix4;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Animation;

/// <summary>A node's local transform as keyframes animate it: translation, rotation (unit quaternion) and scale.</summary>
public struct NodeTransform
{
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;

    public NodeTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Translation = translation;
        Rotation = rotation;
        Scale = scale;
    }

    public static NodeTransform Identity => new(Vector3.Zero, Quaternion.Identity, new Vector3(1f, 1f, 1f));
}

/// <summary>
/// Transform helpers for animation, in the engine <see cref="Matrix4"/> convention (column vectors, translation in
/// M14/M24/M34; the same layout Assimp's matrices are copied in). A local transform is T * R * S.
/// </summary>
public static class AnimationMath
{
    /// <summary>T * R * S (scale first, then rotation, then translation).</summary>
    public static Matrix4 Compose(in NodeTransform transform) => Compose(transform.Translation, transform.Rotation, transform.Scale);

    public static Matrix4 Compose(Vector3 t, Quaternion q, Vector3 s)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
        return new Matrix4(
            (1f - 2f * (yy + zz)) * s.X, 2f * (xy - wz) * s.Y, 2f * (xz + wy) * s.Z, t.X,
            2f * (xy + wz) * s.X, (1f - 2f * (xx + zz)) * s.Y, 2f * (yz - wx) * s.Z, t.Y,
            2f * (xz - wy) * s.X, 2f * (yz + wx) * s.Y, (1f - 2f * (xx + yy)) * s.Z, t.Z,
            0f, 0f, 0f, 1f);
    }

    /// <summary>
    /// Splits an affine matrix into translation, rotation and scale. A mirrored matrix (negative determinant) gets a
    /// negative X scale; shear cannot be represented and is lost (callers keep the exact matrix where nothing animates).
    /// </summary>
    public static NodeTransform Decompose(in Matrix4 m)
    {
        var t = new Vector3(m.M14, m.M24, m.M34);
        var sx = MathF.Sqrt(m.M11 * m.M11 + m.M21 * m.M21 + m.M31 * m.M31);
        var sy = MathF.Sqrt(m.M12 * m.M12 + m.M22 * m.M22 + m.M32 * m.M32);
        var sz = MathF.Sqrt(m.M13 * m.M13 + m.M23 * m.M23 + m.M33 * m.M33);
        if (Determinant3x3(m) < 0f)
        {
            sx = -sx;
        }

        if (MathF.Abs(sx) < 1e-12f || sy < 1e-12f || sz < 1e-12f)
        {
            return new NodeTransform(t, Quaternion.Identity, new Vector3(sx, sy, sz));
        }

        var q = FromRotationMatrix(
            m.M11 / sx, m.M12 / sy, m.M13 / sz,
            m.M21 / sx, m.M22 / sy, m.M23 / sz,
            m.M31 / sx, m.M32 / sy, m.M33 / sz);
        return new NodeTransform(t, q, new Vector3(sx, sy, sz));
    }

    /// <summary>The unit quaternion of a rotation matrix given row by row (column-vector convention, v' = R v).</summary>
    public static Quaternion FromRotationMatrix(
        float r00, float r01, float r02,
        float r10, float r11, float r12,
        float r20, float r21, float r22)
    {
        float x, y, z, w;
        var trace = r00 + r11 + r22;
        if (trace > 0f)
        {
            var s = MathF.Sqrt(trace + 1f) * 2f;
            w = 0.25f * s;
            x = (r21 - r12) / s;
            y = (r02 - r20) / s;
            z = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            var s = MathF.Sqrt(1f + r00 - r11 - r22) * 2f;
            w = (r21 - r12) / s;
            x = 0.25f * s;
            y = (r01 + r10) / s;
            z = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            var s = MathF.Sqrt(1f + r11 - r00 - r22) * 2f;
            w = (r02 - r20) / s;
            x = (r01 + r10) / s;
            y = 0.25f * s;
            z = (r12 + r21) / s;
        }
        else
        {
            var s = MathF.Sqrt(1f + r22 - r00 - r11) * 2f;
            w = (r10 - r01) / s;
            x = (r02 + r20) / s;
            y = (r12 + r21) / s;
            z = 0.25f * s;
        }

        return Quaternion.Normalize(new Quaternion(x, y, z, w));
    }

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>Spherical interpolation along the shorter arc, renormalized (keyframes and blends alike).</summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => Quaternion.Normalize(Quaternion.Slerp(a, b, t));

    /// <summary>Blends two node transforms: translation and scale linearly, rotation along the shorter arc.</summary>
    public static NodeTransform Blend(in NodeTransform a, in NodeTransform b, float weight) => new(
        Lerp(a.Translation, b.Translation, weight), Slerp(a.Rotation, b.Rotation, weight), Lerp(a.Scale, b.Scale, weight));

    public static float Determinant3x3(in Matrix4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
        - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
        + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    public static bool IsFinite(in Matrix4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    /// <summary>Writes a matrix as 16 column-major floats (OpenGL / bgfx upload order) at <paramref name="offset"/>, without allocating.</summary>
    public static void WriteColumnMajor(in Matrix4 m, float[] target, int offset)
    {
        target[offset] = m.M11;
        target[offset + 1] = m.M21;
        target[offset + 2] = m.M31;
        target[offset + 3] = m.M41;
        target[offset + 4] = m.M12;
        target[offset + 5] = m.M22;
        target[offset + 6] = m.M32;
        target[offset + 7] = m.M42;
        target[offset + 8] = m.M13;
        target[offset + 9] = m.M23;
        target[offset + 10] = m.M33;
        target[offset + 11] = m.M43;
        target[offset + 12] = m.M14;
        target[offset + 13] = m.M24;
        target[offset + 14] = m.M34;
        target[offset + 15] = m.M44;
    }

    public static Vector3 TransformPoint(in Matrix4 m, Vector3 p) => new(
        m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
        m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
        m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);
}
