using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

public static class PartRotator
{
    public static void RotateAroundY(Part part, float deltaDegrees)
    {
        var rotation = part.Rotation;
        part.Rotation = new Vector3(
            rotation.X,
            NormalizeDegrees(rotation.Y + deltaDegrees),
            rotation.Z);
    }

    private static float NormalizeDegrees(float degrees)
    {
        var normalized = degrees % 360f;
        if (normalized < 0f)
        {
            normalized += 360f;
        }

        return normalized;
    }
}
