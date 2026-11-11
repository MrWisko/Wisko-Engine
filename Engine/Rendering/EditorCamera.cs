using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering;

/// <summary>
/// The editor's free-flying camera (moved with the keyboard, turned with the right mouse button).
/// </summary>
public sealed class EditorCamera
{
    private static readonly Vector3 DefaultPosition = new(120f, 80f, 120f);
    private const float DefaultYawDegrees = -135f;
    private const float DefaultPitchDegrees = -25f;

    public Vector3 Position { get; set; } = DefaultPosition;
    public float YawDegrees { get; private set; } = DefaultYawDegrees;
    public float PitchDegrees { get; private set; } = DefaultPitchDegrees;

    public float FieldOfViewDegrees { get; set; } = 55f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 2000f;

    public float MoveSpeed { get; set; } = 60f;
    public float FastMultiplier { get; set; } = 3f;
    public float LookSensitivity { get; set; } = 0.08f;
    public float MinMoveSpeed { get; set; } = 10f;
    public float MaxMoveSpeed { get; set; } = 400f;
    public float ScrollFlySpeed { get; set; } = 10f;

    public Vector3 Forward
    {
        get
        {
            var yaw = YawDegrees * MathUtil.DegreesToRadians;
            var pitch = PitchDegrees * MathUtil.DegreesToRadians;
            var cosPitch = MathF.Cos(pitch);
            return new Vector3(
                cosPitch * MathF.Cos(yaw),
                MathF.Sin(pitch),
                cosPitch * MathF.Sin(yaw)).Normalized();
        }
    }

    public Vector3 Up => Vector3.Cross(Right, Forward).Normalized();

    public Vector3 Right
    {
        get
        {
            var right = Vector3.Cross(Forward, Vector3.UnitY);
            if (right.Length > 1e-6f)
            {
                return right.Normalized();
            }

            var yaw = YawDegrees * MathUtil.DegreesToRadians;
            return new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        }
    }

    public Matrix4 ViewMatrix => Matrix4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4 GetProjectionMatrix(float aspectRatio)
    {
        return Matrix4.CreatePerspective(FieldOfViewDegrees * MathUtil.DegreesToRadians, aspectRatio, NearPlane, FarPlane);
    }

    public void ResetView()
    {
        Position = DefaultPosition;
        YawDegrees = DefaultYawDegrees;
        PitchDegrees = DefaultPitchDegrees;
    }

    /// <summary>Points the camera (yaw in degrees around the vertical; pitch kept within ±89).</summary>
    public void SetOrientation(float yawDegrees, float pitchDegrees)
    {
        if (float.IsFinite(yawDegrees))
        {
            YawDegrees = yawDegrees;
        }

        if (float.IsFinite(pitchDegrees))
        {
            PitchDegrees = global::System.Math.Clamp(pitchDegrees, -89f, 89f);
        }
    }

    public void Look(float deltaX, float deltaY)
    {
        YawDegrees += deltaX * LookSensitivity;
        PitchDegrees += deltaY * LookSensitivity;
        PitchDegrees = global::System.Math.Clamp(PitchDegrees, -89f, 89f);
    }

    public void Move(Vector3 direction, float deltaTime, bool fast)
    {
        if (direction.Length < 1e-6f || deltaTime <= 0f)
        {
            return;
        }

        var speed = MoveSpeed * (fast ? FastMultiplier : 1f);
        Position += direction.Normalized() * (speed * deltaTime);
    }

    public void ScrollFly(float scrollDelta)
    {
        if (global::System.Math.Abs(scrollDelta) < 1e-6f)
        {
            return;
        }

        Position += Forward * (scrollDelta * ScrollFlySpeed);
    }
}
