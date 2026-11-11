using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// The five primitive shapes for the Universal renderer, in a unit (±0.5) box, scaled by the Part's size like the
/// Classic meshes and with exactly their positions (same segment counts), so drawing, picking and physics agree. Each
/// shape adds what textures and lighting need: face normals for flat faces, smooth normals for round ones, UVs
/// (0-1 per face; round shapes wrap) and tangents; every triangle is wound counter-clockwise seen from outside.
/// </summary>
internal static class UniversalPrimitives
{
    private const int CylinderSegments = 24;
    private const int SphereSegments = 24;
    private const int SphereRings = 16;

    public static (float[] Vertices, uint[] Indices) Create(ShapeType shape) => shape switch
    {
        ShapeType.Sphere => Sphere(),
        ShapeType.Cylinder => Cylinder(),
        ShapeType.Wedge => FlatFaces(Wedge()),
        ShapeType.Pyramid => FlatFaces(Pyramid()),
        _ => FlatFaces(Box())
    };

    // ---- flat-faced shapes: one quad or triangle per face, face normal, UV projected on the face ----

    private static List<Vector3[]> Box()
    {
        Vector3 P(float x, float y, float z) => new(x * 0.5f, y * 0.5f, z * 0.5f);
        return
        [
            [P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1)],      // +Z
            [P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1)],  // -Z
            [P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1)],      // +X
            [P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1)],  // -X
            [P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1)],      // +Y
            [P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1)]   // -Y
        ];
    }

    // Base on -Y, vertical face on -Z, slope from the top -Z edge to the bottom +Z edge (Classic CreateIndexedWedge).
    private static List<Vector3[]> Wedge()
    {
        var bfl = new Vector3(-0.5f, -0.5f, 0.5f);
        var bfr = new Vector3(0.5f, -0.5f, 0.5f);
        var bbl = new Vector3(-0.5f, -0.5f, -0.5f);
        var bbr = new Vector3(0.5f, -0.5f, -0.5f);
        var tbl = new Vector3(-0.5f, 0.5f, -0.5f);
        var tbr = new Vector3(0.5f, 0.5f, -0.5f);
        return [[bfl, bbl, bbr, bfr], [bbr, bbl, tbl, tbr], [tbl, bfl, bfr, tbr], [bfl, tbl, bbl], [bfr, bbr, tbr]];
    }

    // Base on -Y, apex at +Y (Classic CreateIndexedPyramid).
    private static List<Vector3[]> Pyramid()
    {
        var a = new Vector3(-0.5f, -0.5f, -0.5f);
        var b = new Vector3(0.5f, -0.5f, -0.5f);
        var c = new Vector3(0.5f, -0.5f, 0.5f);
        var d = new Vector3(-0.5f, -0.5f, 0.5f);
        var apex = new Vector3(0f, 0.5f, 0f);
        return [[a, b, c, d], [d, c, apex], [c, b, apex], [b, a, apex], [a, d, apex]];
    }

    private static (float[] Vertices, uint[] Indices) FlatFaces(List<Vector3[]> faces)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<(float U, float V)>();
        var indices = new List<uint>();
        foreach (var face in faces)
        {
            // Outward normal (all shapes are convex around the origin): flip the polygon if it points inwards.
            var polygon = face;
            var normal = Vector3.Cross(polygon[1] - polygon[0], polygon[2] - polygon[0]).Normalized();
            var centre = polygon.Aggregate(Vector3.Zero, (s, p) => s + p) / polygon.Length;
            if (Vector3.Dot(normal, centre) < 0f)
            {
                polygon = polygon.Reverse().ToArray();
                normal = normal * -1f;
            }

            var first = (uint)positions.Count;
            foreach (var p in polygon)
            {
                positions.Add(p);
                normals.Add(normal);
                uvs.Add(Project(p, normal));
            }

            for (var i = 1; i + 1 < polygon.Length; i++)
            {
                indices.Add(first);
                indices.Add(first + (uint)i);
                indices.Add(first + (uint)i + 1);
            }
        }

        var p3 = positions.ToArray();
        var n3 = normals.ToArray();
        var uv = uvs.ToArray();
        var idx = indices.ToArray();
        return (MeshBuilder.Interleave(p3, n3, uv, null, null, idx), idx);
    }

    // The same box projection as surface.vert's Studs mapping, scaled to the unit cube: 0-1 across each face, image
    // upright when seen from outside (v grows downwards).
    internal static (float U, float V) Project(Vector3 p, Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ay >= ax && ay >= az)
        {
            var s = n.Y >= 0f ? 1f : -1f;
            return (p.X + 0.5f, s * p.Z + 0.5f);
        }

        if (ax >= az)
        {
            var s = n.X >= 0f ? 1f : -1f;
            return (-s * p.Z + 0.5f, -p.Y + 0.5f);
        }

        var t = n.Z >= 0f ? 1f : -1f;
        return (t * p.X + 0.5f, -p.Y + 0.5f);
    }

    // ---- round shapes ----

    // Classic CreateUvSphere (radius 0.5, 24 x 16), with a duplicated seam so the texture wraps without a stretch.
    private static (float[] Vertices, uint[] Indices) Sphere()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<(float U, float V)>();
        for (var ring = 0; ring <= SphereRings; ring++)
        {
            var v = ring / (float)SphereRings;
            var phi = v * MathF.PI;
            for (var seg = 0; seg <= SphereSegments; seg++)
            {
                var u = seg / (float)SphereSegments;
                var theta = u * MathF.Tau;
                var position = new Vector3(0.5f * MathF.Sin(phi) * MathF.Cos(theta), 0.5f * MathF.Cos(phi), 0.5f * MathF.Sin(phi) * MathF.Sin(theta));
                positions.Add(position);
                normals.Add(position.Length > 1e-6f ? position.Normalized() : new Vector3(0f, ring == 0 ? 1f : -1f, 0f));
                uvs.Add((1f - u, v));
            }
        }

        var indices = new List<uint>();
        var row = SphereSegments + 1;
        for (var ring = 0; ring < SphereRings; ring++)
        {
            for (var seg = 0; seg < SphereSegments; seg++)
            {
                var current = (uint)(ring * row + seg);
                var next = current + (uint)row;
                AddOutward(indices, positions, normals, current, current + 1, next);
                AddOutward(indices, positions, normals, current + 1, next + 1, next);
            }
        }

        var idx = indices.ToArray();
        return (MeshBuilder.Interleave(positions.ToArray(), normals.ToArray(), uvs.ToArray(), null, null, idx), idx);
    }

    // Classic CreateIndexedCylinder (axis Y, radius 0.5, 24 segments): smooth sides with a seam, flat caps.
    private static (float[] Vertices, uint[] Indices) Cylinder()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<(float U, float V)>();
        var indices = new List<uint>();

        // Sides: segments + 1 columns (the last one repeats the first with u = 1).
        for (var i = 0; i <= CylinderSegments; i++)
        {
            var angle = i / (float)CylinderSegments * MathF.Tau;
            var x = 0.5f * MathF.Cos(angle);
            var z = 0.5f * MathF.Sin(angle);
            var radial = new Vector3(x, 0f, z).Normalized();
            var u = 1f - i / (float)CylinderSegments;
            positions.Add(new Vector3(x, -0.5f, z));
            normals.Add(radial);
            uvs.Add((u, 1f));
            positions.Add(new Vector3(x, 0.5f, z));
            normals.Add(radial);
            uvs.Add((u, 0f));
        }

        for (var i = 0; i < CylinderSegments; i++)
        {
            var bottom = (uint)(i * 2);
            var top = bottom + 1;
            var bottomNext = bottom + 2;
            var topNext = bottom + 3;
            AddOutward(indices, positions, normals, bottom, top, bottomNext);
            AddOutward(indices, positions, normals, top, topNext, bottomNext);
        }

        // Caps: a centre and a ring each, flat normals, UV projected from above / below.
        foreach (var y in new[] { -0.5f, 0.5f })
        {
            var normal = new Vector3(0f, y > 0f ? 1f : -1f, 0f);
            var centre = (uint)positions.Count;
            positions.Add(new Vector3(0f, y, 0f));
            normals.Add(normal);
            uvs.Add(Project(new Vector3(0f, y, 0f), normal));
            for (var i = 0; i < CylinderSegments; i++)
            {
                var angle = i / (float)CylinderSegments * MathF.Tau;
                var p = new Vector3(0.5f * MathF.Cos(angle), y, 0.5f * MathF.Sin(angle));
                positions.Add(p);
                normals.Add(normal);
                uvs.Add(Project(p, normal));
            }

            for (var i = 0; i < CylinderSegments; i++)
            {
                var a = centre + 1 + (uint)i;
                var b = centre + 1 + (uint)((i + 1) % CylinderSegments);
                AddOutward(indices, positions, normals, centre, a, b);
            }
        }

        var idx = indices.ToArray();
        return (MeshBuilder.Interleave(positions.ToArray(), normals.ToArray(), uvs.ToArray(), null, null, idx), idx);
    }

    // Adds a triangle wound counter-clockwise seen from where its vertex normals point.
    private static void AddOutward(List<uint> indices, List<Vector3> positions, List<Vector3> normals, uint a, uint b, uint c)
    {
        var face = Vector3.Cross(positions[(int)b] - positions[(int)a], positions[(int)c] - positions[(int)a]);
        var normal = normals[(int)a] + normals[(int)b] + normals[(int)c];
        if (Vector3.Dot(face, normal) < 0f)
        {
            (b, c) = (c, b);
        }

        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }
}
