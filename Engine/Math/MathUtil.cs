namespace Wisko.Engine.Math;

/// <summary>Small numeric helpers shared across the engine.</summary>
internal static class MathUtil
{
    public const float DegreesToRadians = MathF.PI / 180f;

    /// <summary><paramref name="value"/> clamped to [min, max]; <paramref name="fallback"/> when it is NaN or infinite.</summary>
    public static float ClampFinite(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? global::System.Math.Clamp(value, min, max) : fallback;
}
