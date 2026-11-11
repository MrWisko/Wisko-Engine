using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using Wisko.Engine.World;
using NVector3 = System.Numerics.Vector3;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Physics;

/// <summary>The collider a Part gets (what the Physics panel will show as its read-only "Collider").</summary>
public enum PhysicsColliderShape
{
    Box,
    Sphere,
    Cylinder,
    ConvexHull
}

/// <summary>
/// One registered collider shape: where the runtime keeps it, the offset from the Part's origin to the shape's own
/// centre (non-zero only for hulls, which the runtime centres on their centre of mass), the collider's volume and its
/// inertia for a mass of 1.
/// </summary>
internal readonly record struct PhysicsCollider(PhysicsColliderShape Shape, TypedIndex Index, NVector3 Center, float Volume, BodyInertia UnitInertia)
{
    public BodyInertia InertiaFor(float mass)
    {
        // Inertia scales linearly with mass, so its inverse scales with 1 / mass.
        var scale = 1f / mass;
        var tensor = UnitInertia.InverseInertiaTensor;
        return new BodyInertia
        {
            InverseMass = UnitInertia.InverseMass * scale,
            InverseInertiaTensor = new Symmetric3x3
            {
                XX = tensor.XX * scale,
                YX = tensor.YX * scale,
                YY = tensor.YY * scale,
                ZX = tensor.ZX * scale,
                ZY = tensor.ZY * scale,
                ZZ = tensor.ZZ * scale
            }
        };
    }
}

/// <summary>
/// Builds and shares the collider shapes of one physics world. A primitive's collider matches what is drawn, at the
/// Part's exact Size: Block = box; Sphere = sphere when all three sizes are equal, otherwise a convex hull of the
/// ellipsoid; Cylinder = cylinder when X and Z are equal (both along Y), otherwise a convex hull of its rings; Wedge
/// and Pyramid = convex hulls of their corners. Parts with exactly the same type and Size share one shape; sizes are
/// never rounded to share more.
/// </summary>
internal sealed class PhysicsColliders
{
    // The drawn cylinder's ring (PartMeshBuilder): the elliptical hull uses the same points, so it matches exactly.
    private const int CylinderSegments = 24;

    // The ellipsoid hull: rings every 22.5 degrees from pole to pole, 16 points around. The poles, the equator and the
    // points at 0/90/180/270 degrees are all included, so the hull reaches the full Size on every axis.
    private const int EllipsoidSegments = 16;
    private const int EllipsoidRings = 8;

    private readonly Shapes _shapes;
    private readonly BufferPool _pool;
    private readonly Dictionary<ColliderKey, PhysicsCollider> _cache = new();

    public PhysicsColliders(Shapes shapes, BufferPool pool)
    {
        _shapes = shapes;
        _pool = pool;
    }

    /// <summary>Distinct shapes registered so far.</summary>
    public int Count => _cache.Count;

    /// <summary>The collider shape a primitive of this type and Size gets (no world needed).</summary>
    public static PhysicsColliderShape ShapeOf(ShapeType type, Vector3 size) => Normalize(type) switch
    {
        ShapeType.Sphere => size.X == size.Y && size.Y == size.Z ? PhysicsColliderShape.Sphere : PhysicsColliderShape.ConvexHull,
        ShapeType.Cylinder => size.X == size.Z ? PhysicsColliderShape.Cylinder : PhysicsColliderShape.ConvexHull,
        ShapeType.Wedge or ShapeType.Pyramid => PhysicsColliderShape.ConvexHull,
        _ => PhysicsColliderShape.Box
    };

    /// <summary>The shared collider for a primitive of this type and exact Size (finite and positive: checked by the caller).</summary>
    public PhysicsCollider Get(ShapeType type, Vector3 size)
    {
        type = Normalize(type);
        var key = new ColliderKey(type, size.X, size.Y, size.Z);
        if (!_cache.TryGetValue(key, out var collider))
        {
            collider = Create(type, size);
            _cache.Add(key, collider);
        }

        return collider;
    }

    // An unknown or future ShapeType collides as the box it is drawn as (PartMeshBuilder's fallback).
    private static ShapeType Normalize(ShapeType type) => Enum.IsDefined(type) ? type : ShapeType.Block;

    private PhysicsCollider Create(ShapeType type, Vector3 size)
    {
        float x = size.X, y = size.Y, z = size.Z;
        switch (ShapeOf(type, size))
        {
            case PhysicsColliderShape.Box:
            {
                var box = new Box(x, y, z);
                return new PhysicsCollider(PhysicsColliderShape.Box, _shapes.Add(box), NVector3.Zero, Volume(type, size), box.ComputeInertia(1f));
            }

            case PhysicsColliderShape.Sphere:
            {
                var sphere = new Sphere(x * 0.5f);
                return new PhysicsCollider(PhysicsColliderShape.Sphere, _shapes.Add(sphere), NVector3.Zero, Volume(type, size), sphere.ComputeInertia(1f));
            }

            case PhysicsColliderShape.Cylinder:
            {
                var cylinder = new Cylinder(x * 0.5f, y);
                return new PhysicsCollider(PhysicsColliderShape.Cylinder, _shapes.Add(cylinder), NVector3.Zero, Volume(type, size), cylinder.ComputeInertia(1f));
            }

            default:
            {
                var points = HullPoints(type, x * 0.5f, y * 0.5f, z * 0.5f);
                ConvexHullHelper.CreateShape(points, _pool, out var center, out ConvexHull hull);
                return new PhysicsCollider(PhysicsColliderShape.ConvexHull, _shapes.Add(hull), center, Volume(type, size), hull.ComputeInertia(1f));
            }
        }
    }

    /// <summary>Volume of the collider a primitive of this type and Size gets (the drawn shape's; Auto mass = density x this).</summary>
    public static float VolumeOf(ShapeType type, Vector3 size) => Volume(Normalize(type), size);

    // Volume of the drawn shape (mass = density x volume).
    private static float Volume(ShapeType type, Vector3 size)
    {
        var box = size.X * size.Y * size.Z;
        return type switch
        {
            ShapeType.Sphere => MathF.PI / 6f * box,
            ShapeType.Cylinder => MathF.PI / 4f * box,
            ShapeType.Wedge => box * 0.5f,
            ShapeType.Pyramid => box / 3f,
            _ => box
        };
    }

    // The drawn shape's corner points (MeshFactory), scaled to Size and centred like the mesh (unit box ±size/2).
    private static NVector3[] HullPoints(ShapeType type, float hx, float hy, float hz)
    {
        switch (type)
        {
            case ShapeType.Wedge:
                // Base on −Y; the vertical face on −Z; the slope runs from the top −Z edge down to the bottom +Z edge.
                return
                [
                    new(-hx, -hy, -hz), new(hx, -hy, -hz), new(-hx, -hy, hz), new(hx, -hy, hz),
                    new(-hx, hy, -hz), new(hx, hy, -hz)
                ];

            case ShapeType.Pyramid:
                // Square base on −Y, apex centred at +Y.
                return [new(-hx, -hy, -hz), new(hx, -hy, -hz), new(hx, -hy, hz), new(-hx, -hy, hz), new(0f, hy, 0f)];

            case ShapeType.Cylinder:
            {
                var points = new NVector3[CylinderSegments * 2];
                for (var i = 0; i < CylinderSegments; i++)
                {
                    var angle = i / (float)CylinderSegments * MathF.Tau;
                    var px = hx * MathF.Cos(angle);
                    var pz = hz * MathF.Sin(angle);
                    points[i * 2] = new NVector3(px, -hy, pz);
                    points[i * 2 + 1] = new NVector3(px, hy, pz);
                }

                return points;
            }

            default:
            {
                // Ellipsoid (a Sphere with unequal sizes).
                var points = new List<NVector3> { new(0f, hy, 0f), new(0f, -hy, 0f) };
                for (var ring = 1; ring < EllipsoidRings; ring++)
                {
                    var polar = ring / (float)EllipsoidRings * MathF.PI;
                    var ringY = MathF.Cos(polar);
                    var ringRadius = MathF.Sin(polar);
                    for (var i = 0; i < EllipsoidSegments; i++)
                    {
                        var angle = i / (float)EllipsoidSegments * MathF.Tau;
                        points.Add(new NVector3(hx * ringRadius * MathF.Cos(angle), hy * ringY, hz * ringRadius * MathF.Sin(angle)));
                    }
                }

                return points.ToArray();
            }
        }
    }

    // Exact values: two Parts share a shape only when their type and Size are identical.
    private readonly record struct ColliderKey(ShapeType Type, float X, float Y, float Z);
}
