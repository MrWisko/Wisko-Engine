namespace Wisko.Engine.Math;

public readonly struct Matrix4
{
    public float M11 { get; }
    public float M12 { get; }
    public float M13 { get; }
    public float M14 { get; }

    public float M21 { get; }
    public float M22 { get; }
    public float M23 { get; }
    public float M24 { get; }

    public float M31 { get; }
    public float M32 { get; }
    public float M33 { get; }
    public float M34 { get; }

    public float M41 { get; }
    public float M42 { get; }
    public float M43 { get; }
    public float M44 { get; }

    public static Matrix4 Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public Matrix4(
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44)
    {
        M11 = m11; M12 = m12; M13 = m13; M14 = m14;
        M21 = m21; M22 = m22; M23 = m23; M24 = m24;
        M31 = m31; M32 = m32; M33 = m33; M34 = m34;
        M41 = m41; M42 = m42; M43 = m43; M44 = m44;
    }

    public float[] ToOpenGlColumnMajor() =>
    [
        M11, M21, M31, M41,
        M12, M22, M32, M42,
        M13, M23, M33, M43,
        M14, M24, M34, M44
    ];

    public static Matrix4 CreateTranslation(Vector3 translation) => new(
        1, 0, 0, translation.X,
        0, 1, 0, translation.Y,
        0, 0, 1, translation.Z,
        0, 0, 0, 1);

    public static Matrix4 CreateScale(Vector3 scale) => new(
        scale.X, 0, 0, 0,
        0, scale.Y, 0, 0,
        0, 0, scale.Z, 0,
        0, 0, 0, 1);

    public static Matrix4 CreateRotationAxis(Vector3 axis, float radians)
    {
        var normalized = axis.Normalized();
        var x = normalized.X;
        var y = normalized.Y;
        var z = normalized.Z;
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        var t = 1f - c;

        return new(
            t * x * x + c, t * x * y + s * z, t * x * z - s * y, 0,
            t * x * y - s * z, t * y * y + c, t * y * z + s * x, 0,
            t * x * z + s * y, t * y * z - s * x, t * z * z + c, 0,
            0, 0, 0, 1);
    }

    public static Matrix4 CreateRotationX(float radians)
    {
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        return new(
            1, 0, 0, 0,
            0, c, s, 0,
            0, -s, c, 0,
            0, 0, 0, 1);
    }

    public static Matrix4 CreateRotationY(float radians)
    {
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        return new(
            c, 0, s, 0,
            0, 1, 0, 0,
            -s, 0, c, 0,
            0, 0, 0, 1);
    }

    public static Matrix4 CreateRotationZ(float radians)
    {
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        return new(
            c, s, 0, 0,
            -s, c, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);
    }

    public static Matrix4 CreateRotationEuler(Vector3 rotationDegrees)
    {
        const float degToRad = MathF.PI / 180f;
        var rotX = CreateRotationX(rotationDegrees.X * degToRad);
        var rotY = CreateRotationY(rotationDegrees.Y * degToRad);
        var rotZ = CreateRotationZ(rotationDegrees.Z * degToRad);
        return Multiply(Multiply(rotY, rotX), rotZ);
    }

    public static Matrix4 ComposeTrs(Vector3 translation, Vector3 rotationDegrees, Vector3 scale)
    {
        var scaleMatrix = CreateScale(scale);
        var rotationMatrix = CreateRotationEuler(rotationDegrees);
        var translationMatrix = CreateTranslation(translation);
        return Multiply(translationMatrix, Multiply(rotationMatrix, scaleMatrix));
    }

    public static Matrix4 Multiply(Matrix4 a, Matrix4 b)
    {
        return new(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
            a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
            a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
            a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,

            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
            a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
            a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
            a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,

            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
            a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
            a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
            a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,

            a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
            a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
            a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
            a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);
    }

    /// <summary>
    /// Inverse of an affine transform (bottom row 0 0 0 1; translation in M14/M24/M34). False when the 3x3 part is
    /// singular (a zero scale), in which case <paramref name="inverse"/> is identity.
    /// </summary>
    public static bool TryInvertAffine(Matrix4 m, out Matrix4 inverse)
    {
        float a = m.M11, b = m.M12, c = m.M13;
        float d = m.M21, e = m.M22, f = m.M23;
        float g = m.M31, h = m.M32, i = m.M33;
        var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (!(MathF.Abs(determinant) > 1e-20f))
        {
            inverse = Identity;
            return false;
        }

        var s = 1f / determinant;
        float r11 = (e * i - f * h) * s, r12 = (c * h - b * i) * s, r13 = (b * f - c * e) * s;
        float r21 = (f * g - d * i) * s, r22 = (a * i - c * g) * s, r23 = (c * d - a * f) * s;
        float r31 = (d * h - e * g) * s, r32 = (b * g - a * h) * s, r33 = (a * e - b * d) * s;
        float tx = m.M14, ty = m.M24, tz = m.M34;
        inverse = new Matrix4(
            r11, r12, r13, -(r11 * tx + r12 * ty + r13 * tz),
            r21, r22, r23, -(r21 * tx + r22 * ty + r23 * tz),
            r31, r32, r33, -(r31 * tx + r32 * ty + r33 * tz),
            0, 0, 0, 1);
        return true;
    }

    // v0.1 checkpoint: M34 must be -1 and M43 must hold depth scale (not swapped).
    public static Matrix4 CreatePerspective(float fovYRadians, float aspect, float near, float far)
    {
        var tanHalfFov = MathF.Tan(fovYRadians * 0.5f);
        var range = near - far;

        return new(
            1f / (aspect * tanHalfFov), 0, 0, 0,
            0, 1f / tanHalfFov, 0, 0,
            0, 0, (far + near) / range, (2f * far * near) / range,
            0, 0, -1, 0);
    }

    public static Matrix4 CreateLookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        var forward = (target - eye).Normalized();
        var right = Vector3.Cross(forward, up).Normalized();
        var correctedUp = Vector3.Cross(right, forward);

        return new(
            right.X, right.Y, right.Z, -Vector3.Dot(right, eye),
            correctedUp.X, correctedUp.Y, correctedUp.Z, -Vector3.Dot(correctedUp, eye),
            -forward.X, -forward.Y, -forward.Z, Vector3.Dot(forward, eye),
            0, 0, 0, 1);
    }

    public static Matrix4 CreateOrthographicOffCenter(float left, float right, float bottom, float top, float near, float far)
    {
        var rl = right - left;
        var tb = top - bottom;
        var fn = far - near;

        return new(
            2f / rl, 0, 0, -(right + left) / rl,
            0, 2f / tb, 0, -(top + bottom) / tb,
            0, 0, -2f / fn, -(far + near) / fn,
            0, 0, 0, 1);
    }
}
