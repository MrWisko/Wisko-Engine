using System.Globalization;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

/// <summary>
/// Numeric rotation (World panel: Rotation X / Y / Z). A Part's rotation is its <see cref="Part.Rotation"/>: degrees
/// about the world axes, applied Z, then X, then Y (<see cref="Matrix4.CreateRotationEuler"/>), the very numbers the
/// Rotate gizmo changes, so the fields and the gizmo can never disagree and nothing is converted on the way. A typed
/// value may be decimal or negative; it is stored the way the gizmo stores what it turns, normalized to [0, 360)
/// (-90 becomes 270, the same orientation), never rounded or snapped (the gizmo snaps to 15 degrees with Shift).
/// </summary>
internal static class RotationInput
{
    /// <summary>The largest magnitude accepted (beyond it floats lose the fractions of a degree).</summary>
    public const float MaxMagnitude = 1_000_000f;

    public static readonly string[] AxisNames = ["X", "Y", "Z"];

    /// <summary>Typed degrees: "22.5", "-90", "+45", "1,5" (a comma is a decimal point), "90°"; false for anything else.</summary>
    public static bool TryParse(string text, out float degrees)
    {
        degrees = 0f;
        var trimmed = text.Trim().TrimEnd('°').Trim().Replace(',', '.');
        return trimmed.Length > 0
               && float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees)
               && float.IsFinite(degrees)
               && MathF.Abs(degrees) <= MaxMagnitude;
    }

    /// <summary>Degrees in [0, 360), as the Rotate gizmo keeps them (the same orientation).</summary>
    public static float Normalize(float degrees)
    {
        var n = degrees % 360f;
        if (n < 0f)
        {
            n += 360f;
        }

        return n >= 360f ? 0f : n;
    }

    /// <summary>How a value is shown: up to two decimals, no trailing zeros ("0", "22.5", "37.28").</summary>
    public static string Format(float degrees)
    {
        var rounded = MathF.Round(degrees, 2);
        if (rounded == 360f && degrees < 360f)
        {
            rounded = 0f;
        }

        if (rounded == 0f)
        {
            rounded = 0f; // never "-0"
        }

        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>One axis of the selection: its value, or "—" when the Parts differ there.</summary>
    public static string FormatAxis(IReadOnlyList<Part> parts, int axis)
    {
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var first = Component(parts[0].Rotation, axis);
        for (var i = 1; i < parts.Count; i++)
        {
            if (Format(Component(parts[i].Rotation, axis)) != Format(first))
            {
                return "—";
            }
        }

        return Format(first);
    }

    public static float Component(Vector3 rotation, int axis) => axis switch
    {
        0 => rotation.X,
        1 => rotation.Y,
        _ => rotation.Z
    };

    /// <summary>The rotation with one axis set (the other two kept exactly).</summary>
    public static Vector3 WithAxis(Vector3 rotation, int axis, float degrees) => axis switch
    {
        0 => new Vector3(degrees, rotation.Y, rotation.Z),
        1 => new Vector3(rotation.X, degrees, rotation.Z),
        _ => new Vector3(rotation.X, rotation.Y, degrees)
    };
}
