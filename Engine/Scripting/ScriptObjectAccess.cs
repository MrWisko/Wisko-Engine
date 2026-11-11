using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.World;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Scripting;

/// <summary>
/// What a script may read and write on an object (self or one of Scene's results), host side. Every object has Id, Name,
/// Type, Exists and its Transform (Position, Rotation, Scale, read and written per component; Scale never below the
/// object's minimum). A scene Part (<see cref="IScriptPart"/>) also has a writable Name, Color ("#RRGGBB") and Visible,
/// its physics settings (Anchored, GravityEnabled, CanCollide, IsTrigger, Friction, Mass), IsSleeping (read-only) and
/// its body's velocities, impulses and forces; any other object answers those with neutral values and refuses writes.
/// Values that cannot be used are refused with a short message for the script (null means done).
/// </summary>
internal static class ScriptObjectAccess
{
    private static readonly HashSet<string> PartProperties = new(StringComparer.Ordinal)
    {
        "Anchored", "GravityEnabled", "CanCollide", "IsTrigger", "Friction", "Mass", "Color", "Visible", "IsSleeping", "RotationLocked",
        "GravityScale", "LinearDamping", "AngularDamping", "CollisionGroup"
    };

    // A Part's local axes: Forward is local -Z (as cameras look), Right local +X, Up local +Y.
    private static readonly NVector3 LocalForward = new(0f, 0f, -1f);

    public static object? Get(IScriptObject target, int id, string key, out string? error)
    {
        error = null;
        switch (key)
        {
            case "Id":
                return (long)id;
            case "Name":
                return target.Name;
            case "Type":
                return target.TypeName;
            case "Exists":
                return target.Exists;
        }

        if (!PartProperties.Contains(key))
        {
            error = $"{key} is not a valid member of ScriptObject";
            return null;
        }

        if (target is IScriptPart part && part.TryGet(key, out var value))
        {
            return value;
        }

        // Not a Part: what an object physics does not simulate looks like.
        return key switch
        {
            "Friction" => (double)Part.DefaultFriction,
            "Mass" or "LinearDamping" or "AngularDamping" => 0d,
            "GravityScale" => 1d,
            "CollisionGroup" => "Default",
            "IsTrigger" or "IsSleeping" or "RotationLocked" => false,
            "Color" => "#FFFFFF",
            _ => true
        };
    }

    public static string? Set(IScriptObject target, string key, object? value)
    {
        switch (key)
        {
            case "Id" or "Type" or "Exists" or "IsSleeping":
                return $"{key} is read-only";
            case "Name" when target is not IScriptPart:
                return "Name is read-only";
            case "Name":
                return ((IScriptPart)target).Set(key, value);
        }

        if (!PartProperties.Contains(key))
        {
            return $"{key} is not a valid member of ScriptObject";
        }

        return target is IScriptPart part ? part.Set(key, value) : NotSimulated(target);
    }

    public static bool GetVector(IScriptObject target, string key, out Vector3 value, out string? error)
    {
        error = null;
        switch (key)
        {
            case "Position":
                value = target.ReadTransform().Position;
                return true;
            case "Rotation":
                value = target.ReadTransform().Rotation;
                return true;
            case "Scale":
                value = target.ReadTransform().Scale;
                return true;
            case "LinearVelocity" or "AngularVelocity" or "SurfaceVelocity":
                value = target is IScriptPart part ? part.GetVelocity(key) : Vector3.Zero;
                return true;
            case "Forward" or "Right" or "Up":
                value = PhysicsMath.ToWisko(NVector3.Transform(key switch { "Forward" => LocalForward, "Right" => NVector3.UnitX, _ => NVector3.UnitY },
                    OrientationOf(target)));
                return true;
            default:
                value = default;
                error = $"{key} is not a valid member of ScriptObject";
                return false;
        }
    }

    /// <summary>The object's orientation (from its Euler degrees).</summary>
    public static NQuaternion OrientationOf(IScriptObject target) => PhysicsMath.EulerDegreesToQuaternion(target.ReadTransform().Rotation);

    /// <summary>
    /// A point or direction between the object's local space and the world: "pointworld" / "pointlocal" use its Position
    /// (its origin) and orientation, "vectorworld" / "vectorlocal" its orientation only.
    /// </summary>
    public static Vector3 ConvertSpace(IScriptObject target, string mode, Vector3 value)
    {
        var transform = target.ReadTransform();
        var orientation = PhysicsMath.EulerDegreesToQuaternion(transform.Rotation);
        var inverse = NQuaternion.Conjugate(orientation);
        var v = PhysicsMath.ToNumerics(value);
        var origin = PhysicsMath.ToNumerics(transform.Position);
        return PhysicsMath.ToWisko(mode switch
        {
            "pointworld" => origin + NVector3.Transform(v, orientation),
            "pointlocal" => NVector3.Transform(v - origin, inverse),
            "vectorworld" => NVector3.Transform(v, orientation),
            _ => NVector3.Transform(v, inverse)
        });
    }

    /// <summary>Turns the object by Euler degrees (x, y, z), in its own axes or the world's. Null when done.</summary>
    public static string? Rotate(IScriptObject target, Vector3 degrees, bool world)
    {
        var turn = PhysicsMath.EulerDegreesToQuaternion(degrees);
        var orientation = OrientationOf(target);
        return SetOrientation(target, world ? turn * orientation : orientation * turn, null);
    }

    /// <summary>Turns the object around a world point and axis by <paramref name="degrees"/>: its position swings round with it.</summary>
    public static string? RotateAround(IScriptObject target, Vector3 pivot, Vector3 axis, float degrees)
    {
        var a = PhysicsMath.ToNumerics(axis);
        if (a.LengthSquared() < 1e-12f)
        {
            return "RotateAround expects an axis that is not zero";
        }

        var turn = NQuaternion.CreateFromAxisAngle(NVector3.Normalize(a), degrees * MathF.PI / 180f);
        var transform = target.ReadTransform();
        var p = PhysicsMath.ToNumerics(pivot);
        var position = p + NVector3.Transform(PhysicsMath.ToNumerics(transform.Position) - p, turn);
        return SetOrientation(target, turn * OrientationOf(target), PhysicsMath.ToWisko(position));
    }

    /// <summary>Turns the object so its Forward (local -Z) points at <paramref name="point"/>, its Up toward <paramref name="up"/>.</summary>
    public static string? LookAt(IScriptObject target, Vector3 point, Vector3 up)
    {
        var position = PhysicsMath.ToNumerics(target.ReadTransform().Position);
        var forward = PhysicsMath.ToNumerics(point) - position;
        if (forward.LengthSquared() < 1e-12f)
        {
            return "LookAt expects a point away from the object";
        }

        forward = NVector3.Normalize(forward);
        var upVector = PhysicsMath.ToNumerics(up);
        if (upVector.LengthSquared() < 1e-12f || MathF.Abs(NVector3.Dot(NVector3.Normalize(upVector), forward)) > 0.999f)
        {
            upVector = MathF.Abs(forward.Y) < 0.999f ? NVector3.UnitY : NVector3.UnitZ;
        }

        // Rows: right (+X), up (+Y), back (+Z = -forward).
        var back = -forward;
        var right = NVector3.Normalize(NVector3.Cross(NVector3.Normalize(upVector), back));
        var trueUp = NVector3.Cross(back, right);
        var matrix = new System.Numerics.Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            trueUp.X, trueUp.Y, trueUp.Z, 0f,
            back.X, back.Y, back.Z, 0f,
            0f, 0f, 0f, 1f);
        return SetOrientation(target, NQuaternion.CreateFromRotationMatrix(matrix), null);
    }

    // Writes a new orientation (and optionally position) through the object's transform, as a script write would.
    private static string? SetOrientation(IScriptObject target, NQuaternion orientation, Vector3? position)
    {
        var rotation = PhysicsMath.QuaternionToEulerDegrees(NQuaternion.Normalize(orientation));
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z))
        {
            return "the rotation is not finite";
        }

        var transform = target.ReadTransform();
        target.WriteTransform(transform with { Rotation = rotation, Position = position ?? transform.Position });
        return null;
    }

    /// <summary>One component (x, y or z) of a Transform vector or a velocity; <paramref name="value"/> is finite (checked in Lua).</summary>
    public static string? SetComponent(IScriptObject target, string key, string axis, double value)
    {
        if (axis is not ("x" or "y" or "z") || !double.IsFinite(value) || global::System.Math.Abs(value) > float.MaxValue)
        {
            return $"{key}.{axis} must be a finite number";
        }

        var number = (float)value;
        switch (key)
        {
            case "Position" or "Rotation" or "Scale":
                var transform = target.ReadTransform();
                if (key == "Scale")
                {
                    // Never below the object's minimum (the editor's smallest size).
                    number = global::System.Math.Max(number, Component(target.MinimumScale, axis));
                }

                target.WriteTransform(key switch
                {
                    "Position" => transform with { Position = With(transform.Position, axis, number) },
                    "Rotation" => transform with { Rotation = With(transform.Rotation, axis, number) },
                    _ => transform with { Scale = With(transform.Scale, axis, number) }
                });
                return null;
            case "LinearVelocity" or "AngularVelocity" or "SurfaceVelocity":
                return target is IScriptPart part ? part.SetVelocity(key, With(part.GetVelocity(key), axis, number)) : NotSimulated(target);
            case "Forward" or "Right" or "Up":
                return $"{key} is read-only (turn the object with Rotate, LookAt or Transform.Rotation)";
            default:
                return $"{key} is not a valid member of ScriptObject";
        }
    }

    /// <summary>A whole Transform vector or velocity at once (finite, checked in Lua; Scale never below the minimum).</summary>
    public static string? SetVector(IScriptObject target, string key, Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            return $"{key} must be finite";
        }

        switch (key)
        {
            case "Position" or "Rotation" or "Scale":
                var transform = target.ReadTransform();
                var minimum = target.MinimumScale;
                target.WriteTransform(key switch
                {
                    "Position" => transform with { Position = value },
                    "Rotation" => transform with { Rotation = value },
                    _ => transform with
                    {
                        Scale = new Vector3(global::System.Math.Max(value.X, minimum.X), global::System.Math.Max(value.Y, minimum.Y), global::System.Math.Max(value.Z, minimum.Z))
                    }
                });
                return null;
            case "LinearVelocity" or "AngularVelocity" or "SurfaceVelocity":
                return target is IScriptPart part ? part.SetVelocity(key, value) : NotSimulated(target);
            case "Forward" or "Right" or "Up":
                return $"{key} is read-only (turn the object with Rotate, LookAt or Transform.Rotation)";
            default:
                return $"{key} is not a valid member of ScriptObject";
        }
    }

    public static string? ApplyImpulse(IScriptObject target, Vector3 impulse) =>
        target is IScriptPart part ? part.ApplyImpulse(impulse) : NotSimulated(target);

    public static string NotSimulated(IScriptObject target) => $"'{target.Name}' is not simulated by physics";

    /// <summary>Canonical colour text: "#RRGGBB", uppercase, each channel the nearest of 0-255.</summary>
    public static string ToHex(Vector3 color) => $"#{Channel(color.X):X2}{Channel(color.Y):X2}{Channel(color.Z):X2}";

    /// <summary>"#RRGGBB" or "RRGGBB" (six hex digits, either case) as a 0-1 colour.</summary>
    public static bool TryParseHex(string? text, out Vector3 color)
    {
        color = default;
        if (text is null)
        {
            return false;
        }

        var hex = text.StartsWith('#') ? text[1..] : text;
        if (hex.Length != 6 || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var value = Convert.ToInt32(hex, 16);
        color = new Vector3((value >> 16) / 255f, (value >> 8 & 0xFF) / 255f, (value & 0xFF) / 255f);
        return true;
    }

    private static int Channel(float value) => float.IsFinite(value) ? (int)MathF.Round(global::System.Math.Clamp(value, 0f, 1f) * 255f) : 0;

    private static float Component(Vector3 vector, string axis) => axis switch { "x" => vector.X, "y" => vector.Y, _ => vector.Z };

    private static Vector3 With(Vector3 vector, string axis, float value) => axis switch
    {
        "x" => new Vector3(value, vector.Y, vector.Z),
        "y" => new Vector3(vector.X, value, vector.Z),
        _ => new Vector3(vector.X, vector.Y, value)
    };
}
