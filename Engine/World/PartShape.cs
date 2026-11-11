using Wisko.Engine.Math;

namespace Wisko.Engine.World;

/// <summary>
/// A Part's exact editor geometry, as it is drawn: Block, Wedge, Pyramid and Cylinder are the convex solids of their
/// meshes (the Cylinder's 24-sided ring), a Sphere is its ellipsoid, and an imported model is its bounds box. Every
/// shape is a unit template (inside ±1) scaled by half the Part's Size, rotated by its Rotation and centred on its
/// Position, so one implementation serves every primitive: a ray hit on the real faces with their normals, the farthest
/// point in any direction (support) and the overlap of two Parts (GJK on the supports). Editor placement, and Play's
/// region queries (Physics.OverlapSphere / OverlapBox test Parts' drawn shapes); Play Mode physics has its own colliders.
/// </summary>
internal readonly struct PartShape
{
    /// <summary>Two Parts touching, or sunk into each other by less than this, do not overlap.</summary>
    public const float ContactTolerance = 1e-4f;

    // Half sizes below this (or not finite) are treated as this: nothing divides by zero or becomes NaN.
    private const float MinHalfSize = 1e-4f;
    private const int MaxOverlapIterations = 96;
    private const int ContactSearchSteps = 22;
    private const int CylinderSegments = 24;

    private static readonly Vector3[] BoxVertices =
    [
        new(-1f, -1f, -1f), new(1f, -1f, -1f), new(-1f, 1f, -1f), new(1f, 1f, -1f),
        new(-1f, -1f, 1f), new(1f, -1f, 1f), new(-1f, 1f, 1f), new(1f, 1f, 1f)
    ];

    private static readonly Plane[] BoxPlanes =
    [
        new(new Vector3(1f, 0f, 0f), 1f), new(new Vector3(-1f, 0f, 0f), 1f),
        new(new Vector3(0f, 1f, 0f), 1f), new(new Vector3(0f, -1f, 0f), 1f),
        new(new Vector3(0f, 0f, 1f), 1f), new(new Vector3(0f, 0f, -1f), 1f)
    ];

    // MeshFactory's wedge: base on -Y, the vertical face on -Z, the slope from the top -Z edge down to the bottom +Z edge.
    private static readonly Vector3[] WedgeVertices =
    [
        new(-1f, -1f, -1f), new(1f, -1f, -1f), new(-1f, -1f, 1f), new(1f, -1f, 1f),
        new(-1f, 1f, -1f), new(1f, 1f, -1f)
    ];

    private static readonly Plane[] WedgePlanes =
    [
        new(new Vector3(0f, -1f, 0f), 1f), new(new Vector3(0f, 0f, -1f), 1f),
        new(new Vector3(0f, 1f, 1f).Normalized(), 0f),
        new(new Vector3(-1f, 0f, 0f), 1f), new(new Vector3(1f, 0f, 0f), 1f)
    ];

    // MeshFactory's pyramid: square base on -Y, apex centred at +Y.
    private static readonly Vector3[] PyramidVertices =
    [
        new(-1f, -1f, -1f), new(1f, -1f, -1f), new(1f, -1f, 1f), new(-1f, -1f, 1f), new(0f, 1f, 0f)
    ];

    private static readonly Plane[] PyramidPlanes =
    [
        new(new Vector3(0f, -1f, 0f), 1f),
        SidePlane(new Vector3(0f, 1f, 2f)), SidePlane(new Vector3(2f, 1f, 0f)),
        SidePlane(new Vector3(0f, 1f, -2f)), SidePlane(new Vector3(-2f, 1f, 0f))
    ];

    // MeshFactory's cylinder: 24 ring points at i / 24 of a turn (x = cos, z = sin), caps at ±Y.
    private static readonly Vector3[] CylinderVertices = CreateCylinderVertices();
    private static readonly Plane[] CylinderPlanes = CreateCylinderPlanes();

    private readonly Template _template;

    // Rotation (local -> world), row-major like Matrix4: world = Center + R * (template * Half).
    private readonly float _m11, _m12, _m13, _m21, _m22, _m23, _m31, _m32, _m33;

    // The same shape with its centre elsewhere.
    private PartShape(in PartShape shape, Vector3 center)
    {
        this = shape;
        Center = center;
    }

    private PartShape(Template template, Vector3 center, Vector3 half, Matrix4 rotation)
    {
        _template = template;
        Center = center;
        Half = half;
        _m11 = rotation.M11;
        _m12 = rotation.M12;
        _m13 = rotation.M13;
        _m21 = rotation.M21;
        _m22 = rotation.M22;
        _m23 = rotation.M23;
        _m31 = rotation.M31;
        _m32 = rotation.M32;
        _m33 = rotation.M33;
    }

    private enum Template
    {
        Box,
        Wedge,
        Pyramid,
        Cylinder,
        Ellipsoid
    }

    public Vector3 Center { get; }

    public Vector3 Half { get; }

    /// <summary>The Part where it is now.</summary>
    public static PartShape Of(Part part) => Of(part, part.Position);

    /// <summary>The Part as it would be at <paramref name="position"/> (same Size, Rotation and shape).</summary>
    public static PartShape Of(Part part, Vector3 position)
    {
        var template = part.Model is not null
            ? Template.Box
            : part.ShapeType switch
            {
                ShapeType.Wedge => Template.Wedge,
                ShapeType.Pyramid => Template.Pyramid,
                ShapeType.Cylinder => Template.Cylinder,
                ShapeType.Sphere => Template.Ellipsoid,
                _ => Template.Box // Block, and any unknown shape (drawn as a box)
            };
        var half = new Vector3(SafeHalf(part.Size.X), SafeHalf(part.Size.Y), SafeHalf(part.Size.Z));
        return new PartShape(template, position, half, Matrix4.CreateRotationEuler(part.Rotation));
    }

    /// <summary>A box of <paramref name="size"/> centred on <paramref name="center"/>, turned by Euler degrees as a Part is (queries, environment boxes).</summary>
    public static PartShape Box(Vector3 center, Vector3 size, Vector3 rotationDegrees) =>
        new(Template.Box, center, new Vector3(SafeHalf(size.X), SafeHalf(size.Y), SafeHalf(size.Z)), Matrix4.CreateRotationEuler(rotationDegrees));

    /// <summary>A sphere of <paramref name="radius"/> centred on <paramref name="center"/> (queries).</summary>
    public static PartShape Sphere(Vector3 center, float radius)
    {
        var half = SafeHalf(radius * 2f);
        return new PartShape(Template.Ellipsoid, center, new Vector3(half, half, half), Matrix4.Identity);
    }

    /// <summary>The world box around the Part's whole Size box (every primitive fits inside it).</summary>
    public WorldAabb Bounds
    {
        get
        {
            var extent = new Vector3(
                MathF.Abs(_m11) * Half.X + MathF.Abs(_m12) * Half.Y + MathF.Abs(_m13) * Half.Z,
                MathF.Abs(_m21) * Half.X + MathF.Abs(_m22) * Half.Y + MathF.Abs(_m23) * Half.Z,
                MathF.Abs(_m31) * Half.X + MathF.Abs(_m32) * Half.Y + MathF.Abs(_m33) * Half.Z);
            return new WorldAabb(Center - extent, Center + extent);
        }
    }

    /// <summary>The lowest world height the shape reaches.</summary>
    public float LowestY => Support(new Vector3(0f, -1f, 0f)).Y;

    /// <summary>How far the shape reaches from its centre along <paramref name="unitDirection"/>.</summary>
    public float ExtentAlong(Vector3 unitDirection) => Vector3.Dot(Support(unitDirection) - Center, unitDirection);

    /// <summary>The shape's farthest point in the world direction (any length; a zero direction gives a point of the shape).</summary>
    public Vector3 Support(Vector3 direction)
    {
        // World direction into template space: rotate back, then scale by the half size (the inverse-transpose of the scale).
        var local = InverseRotate(direction);
        var tx = local.X * Half.X;
        var ty = local.Y * Half.Y;
        var tz = local.Z * Half.Z;
        Vector3 point;
        if (_template == Template.Ellipsoid)
        {
            var length = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
            point = length > 1e-12f ? new Vector3(tx / length, ty / length, tz / length) : Vector3.Zero;
        }
        else
        {
            var vertices = VerticesOf(_template);
            point = vertices[0];
            var best = tx * point.X + ty * point.Y + tz * point.Z;
            for (var i = 1; i < vertices.Length; i++)
            {
                var vertex = vertices[i];
                var dot = tx * vertex.X + ty * vertex.Y + tz * vertex.Z;
                if (dot > best)
                {
                    best = dot;
                    point = vertex;
                }
            }
        }

        return Center + Rotate(new Vector3(point.X * Half.X, point.Y * Half.Y, point.Z * Half.Z));
    }

    /// <summary>
    /// Where the ray first meets the shape's surface and that face's outward world normal. A ray starting inside reports
    /// where it leaves.
    /// </summary>
    public bool TryRaycast(Ray ray, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;

        // Into template space; the map is affine, so the ray parameter (world distance, the direction being unit) is kept.
        var localOrigin = InverseRotate(ray.Origin - Center);
        var localDirection = InverseRotate(ray.Direction);
        var origin = new Vector3(localOrigin.X / Half.X, localOrigin.Y / Half.Y, localOrigin.Z / Half.Z);
        var direction = new Vector3(localDirection.X / Half.X, localDirection.Y / Half.Y, localDirection.Z / Half.Z);

        Vector3 templateNormal;
        if (_template == Template.Ellipsoid)
        {
            if (!TryIntersectUnitSphere(origin, direction, out distance))
            {
                return false;
            }

            templateNormal = origin + direction * distance;
        }
        else if (!TryIntersectPlanes(origin, direction, PlanesOf(_template), out distance, out templateNormal))
        {
            return false;
        }

        // A template normal becomes a local one through the inverse-transpose of the scale.
        var localNormal = new Vector3(templateNormal.X / Half.X, templateNormal.Y / Half.Y, templateNormal.Z / Half.Z);
        normal = Rotate(localNormal).Normalized();
        return true;
    }

    /// <summary>
    /// True when the two shapes overlap by more than <see cref="ContactTolerance"/> (GJK on their supports; touching is
    /// not overlapping).
    /// </summary>
    public static bool Overlap(in PartShape a, in PartShape b)
    {
        Span<Vector3> simplex = stackalloc Vector3[4];
        var direction = b.Center - a.Center;
        if (Vector3.Dot(direction, direction) < 1e-12f)
        {
            direction = Vector3.UnitX;
        }

        simplex[0] = MinkowskiSupport(a, b, direction);
        var count = 1;
        direction = Vector3.Zero - simplex[0];
        for (var iteration = 0; iteration < MaxOverlapIterations; iteration++)
        {
            var length = direction.Length;
            if (length < 1e-9f)
            {
                // The origin lies on the simplex itself: the shapes meet exactly (counted as overlapping, as before).
                return true;
            }

            var unit = direction / length;
            var point = MinkowskiSupport(a, b, unit);

            // Along this direction the shapes are apart, or sunk in by less than the tolerance: not overlapping.
            if (Vector3.Dot(point, unit) <= ContactTolerance)
            {
                return false;
            }

            simplex[count++] = point;
            if (ContainsOrigin(simplex, ref count, ref direction))
            {
                return true;
            }
        }

        // No answer within the iterations (a degenerate case): overlapping, the safe answer for placement.
        return true;
    }

    /// <summary>
    /// How far <paramref name="moving"/> can travel along <paramref name="unitDirection"/> (up to
    /// <paramref name="maxDistance"/>) before it overlaps <paramref name="target"/>: 0 when it already does, or when it
    /// would not meet the target within that distance. Convex shapes meet along a line in one interval, so halving
    /// finds the first contact.
    /// </summary>
    public static float DistanceUntilContact(in PartShape moving, in PartShape target, Vector3 unitDirection, float maxDistance)
    {
        if (!(maxDistance > 0f) || Overlap(moving, target)
            || !Overlap(new PartShape(moving, moving.Center + unitDirection * maxDistance), target))
        {
            return 0f;
        }

        var free = 0f;
        var blocked = maxDistance;
        for (var i = 0; i < ContactSearchSteps; i++)
        {
            var middle = (free + blocked) * 0.5f;
            if (Overlap(new PartShape(moving, moving.Center + unitDirection * middle), target))
            {
                blocked = middle;
            }
            else
            {
                free = middle;
            }
        }

        return free;
    }

    private static Vector3 MinkowskiSupport(in PartShape a, in PartShape b, Vector3 direction) =>
        a.Support(direction) - b.Support(Vector3.Zero - direction);

    // GJK's simplex step: the newest point is last. Reduces the simplex to the feature nearest the origin and sets the next
    // search direction (towards the origin); true once a tetrahedron encloses the origin.
    private static bool ContainsOrigin(Span<Vector3> simplex, ref int count, ref Vector3 direction)
    {
        switch (count)
        {
            case 2:
                Line(simplex, ref count, ref direction);
                return false;
            case 3:
                Triangle(simplex, ref count, ref direction);
                return false;
            default:
                return Tetrahedron(simplex, ref count, ref direction);
        }
    }

    private static void Line(Span<Vector3> simplex, ref int count, ref Vector3 direction)
    {
        var a = simplex[1];
        var b = simplex[0];
        var ab = b - a;
        var ao = Vector3.Zero - a;
        if (Vector3.Dot(ab, ao) > 0f)
        {
            direction = Vector3.Cross(Vector3.Cross(ab, ao), ab);
            count = 2;
        }
        else
        {
            simplex[0] = a;
            count = 1;
            direction = ao;
        }
    }

    // Simplex (c, b, a), a newest. The edge normals below point away from the third corner whatever the winding.
    private static void Triangle(Span<Vector3> simplex, ref int count, ref Vector3 direction)
    {
        var a = simplex[2];
        var b = simplex[1];
        var c = simplex[0];
        var ab = b - a;
        var ac = c - a;
        var ao = Vector3.Zero - a;
        var abc = Vector3.Cross(ab, ac);

        if (Vector3.Dot(Vector3.Cross(abc, ac), ao) > 0f)
        {
            if (Vector3.Dot(ac, ao) > 0f)
            {
                simplex[0] = c;
                simplex[1] = a;
                count = 2;
                direction = Vector3.Cross(Vector3.Cross(ac, ao), ac);
            }
            else
            {
                simplex[0] = b;
                simplex[1] = a;
                count = 2;
                Line(simplex, ref count, ref direction);
            }

            return;
        }

        if (Vector3.Dot(Vector3.Cross(ab, abc), ao) > 0f)
        {
            simplex[0] = b;
            simplex[1] = a;
            count = 2;
            Line(simplex, ref count, ref direction);
            return;
        }

        count = 3;
        direction = Vector3.Dot(abc, ao) > 0f ? abc : Vector3.Zero - abc;
    }

    // Simplex (d, c, b, a), a newest. Each face through a gets its normal turned away from the fourth corner; the origin
    // beyond one of them continues as that triangle, otherwise it is enclosed.
    private static bool Tetrahedron(Span<Vector3> simplex, ref int count, ref Vector3 direction)
    {
        var a = simplex[3];
        var b = simplex[2];
        var c = simplex[1];
        var d = simplex[0];
        var ao = Vector3.Zero - a;

        if (IsBeyondFace(a, b, c, d, ao))
        {
            SetTriangle(simplex, c, b, a, ref count);
        }
        else if (IsBeyondFace(a, c, d, b, ao))
        {
            SetTriangle(simplex, d, c, a, ref count);
        }
        else if (IsBeyondFace(a, d, b, c, ao))
        {
            SetTriangle(simplex, b, d, a, ref count);
        }
        else
        {
            return true;
        }

        Triangle(simplex, ref count, ref direction);
        return false;
    }

    private static bool IsBeyondFace(Vector3 a, Vector3 b, Vector3 c, Vector3 opposite, Vector3 ao)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (Vector3.Dot(normal, opposite - a) > 0f)
        {
            normal = Vector3.Zero - normal;
        }

        return Vector3.Dot(normal, ao) > 0f;
    }

    private static void SetTriangle(Span<Vector3> simplex, Vector3 c, Vector3 b, Vector3 a, ref int count)
    {
        simplex[0] = c;
        simplex[1] = b;
        simplex[2] = a;
        count = 3;
    }

    // Ray against a convex solid given by its face planes (inside: dot(n, p) <= offset); returns the entering face.
    private static bool TryIntersectPlanes(Vector3 origin, Vector3 direction, Plane[] planes, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;
        var enter = float.NegativeInfinity;
        var exit = float.PositiveInfinity;
        var enterPlane = -1;
        var exitPlane = -1;
        for (var i = 0; i < planes.Length; i++)
        {
            var plane = planes[i];
            var denominator = Vector3.Dot(plane.Normal, direction);
            var gap = plane.Offset - Vector3.Dot(plane.Normal, origin);
            if (MathF.Abs(denominator) < 1e-12f)
            {
                if (gap < 0f)
                {
                    return false;
                }

                continue;
            }

            var t = gap / denominator;
            if (denominator < 0f)
            {
                if (t > enter)
                {
                    enter = t;
                    enterPlane = i;
                }
            }
            else if (t < exit)
            {
                exit = t;
                exitPlane = i;
            }

            if (enter > exit)
            {
                return false;
            }
        }

        if (exit < 0f)
        {
            return false;
        }

        if (enter >= 0f && enterPlane >= 0)
        {
            distance = enter;
            normal = planes[enterPlane].Normal;
            return true;
        }

        if (exitPlane < 0)
        {
            return false;
        }

        distance = exit;
        normal = planes[exitPlane].Normal;
        return true;
    }

    private static bool TryIntersectUnitSphere(Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0f;
        var a = Vector3.Dot(direction, direction);
        var b = 2f * Vector3.Dot(origin, direction);
        var c = Vector3.Dot(origin, origin) - 1f;
        var discriminant = b * b - 4f * a * c;
        if (a < 1e-12f || discriminant < 0f)
        {
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        var near = (-b - root) / (2f * a);
        var far = (-b + root) / (2f * a);
        distance = near >= 0f ? near : far;
        return distance >= 0f;
    }

    private static Vector3[] VerticesOf(Template template) => template switch
    {
        Template.Wedge => WedgeVertices,
        Template.Pyramid => PyramidVertices,
        Template.Cylinder => CylinderVertices,
        _ => BoxVertices
    };

    private static Plane[] PlanesOf(Template template) => template switch
    {
        Template.Wedge => WedgePlanes,
        Template.Pyramid => PyramidPlanes,
        Template.Cylinder => CylinderPlanes,
        _ => BoxPlanes
    };

    // A pyramid side through two base corners and the apex: n . p = n . apex.
    private static Plane SidePlane(Vector3 normal)
    {
        var unit = normal.Normalized();
        return new Plane(unit, unit.Y);
    }

    private static Vector3[] CreateCylinderVertices()
    {
        var vertices = new Vector3[CylinderSegments * 2];
        for (var i = 0; i < CylinderSegments; i++)
        {
            var angle = i / (float)CylinderSegments * MathF.Tau;
            vertices[i * 2] = new Vector3(MathF.Cos(angle), -1f, MathF.Sin(angle));
            vertices[i * 2 + 1] = new Vector3(MathF.Cos(angle), 1f, MathF.Sin(angle));
        }

        return vertices;
    }

    private static Plane[] CreateCylinderPlanes()
    {
        var planes = new Plane[CylinderSegments + 2];
        var apothem = MathF.Cos(MathF.PI / CylinderSegments);
        for (var i = 0; i < CylinderSegments; i++)
        {
            var angle = (i + 0.5f) / CylinderSegments * MathF.Tau;
            planes[i] = new Plane(new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)), apothem);
        }

        planes[CylinderSegments] = new Plane(new Vector3(0f, 1f, 0f), 1f);
        planes[CylinderSegments + 1] = new Plane(new Vector3(0f, -1f, 0f), 1f);
        return planes;
    }

    private static float SafeHalf(float size)
    {
        var half = size * 0.5f;
        return float.IsFinite(half) && half > MinHalfSize ? half : MinHalfSize;
    }

    private Vector3 Rotate(Vector3 v) =>
        new(
            _m11 * v.X + _m12 * v.Y + _m13 * v.Z,
            _m21 * v.X + _m22 * v.Y + _m23 * v.Z,
            _m31 * v.X + _m32 * v.Y + _m33 * v.Z);

    private Vector3 InverseRotate(Vector3 v) =>
        new(
            _m11 * v.X + _m21 * v.Y + _m31 * v.Z,
            _m12 * v.X + _m22 * v.Y + _m32 * v.Z,
            _m13 * v.X + _m23 * v.Y + _m33 * v.Z);

    private readonly record struct Plane(Vector3 Normal, float Offset);
}
