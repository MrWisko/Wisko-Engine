using Wisko.Engine.Math;

namespace Wisko.Engine.Rendering;

public static class MeshFactory
{
    public static (Vertex[] Vertices, uint[] Indices) CreateIndexedBox(float sizeX, float sizeY, float sizeZ, Vector3 color)
    {
        var hx = sizeX * 0.5f;
        var hy = sizeY * 0.5f;
        var hz = sizeZ * 0.5f;

        var vertices = new Vertex[]
        {
            new(new Vector3(-hx, -hy, hz), color),
            new(new Vector3(hx, -hy, hz), color),
            new(new Vector3(hx, hy, hz), color),
            new(new Vector3(-hx, hy, hz), color),
            new(new Vector3(-hx, -hy, -hz), color),
            new(new Vector3(hx, -hy, -hz), color),
            new(new Vector3(hx, hy, -hz), color),
            new(new Vector3(-hx, hy, -hz), color)
        };

        uint[] indices =
        [
            0, 1, 2, 2, 3, 0,
            5, 4, 7, 7, 6, 5,
            4, 0, 3, 3, 7, 4,
            1, 5, 6, 6, 2, 1,
            3, 2, 6, 6, 7, 3,
            4, 5, 1, 1, 0, 4
        ];

        return (vertices, indices);
    }

    /// <summary>
    /// Solid baseplate box: grey top, slightly darker sides/bottom.
    /// Local origin centered; top face at +hy.
    /// </summary>
    public static Vertex[] CreateSolidBaseplateBox(float sizeX, float sizeY, float sizeZ, Vector3 topColor, Vector3 sideColor)
    {
        var hx = sizeX * 0.5f;
        var hy = sizeY * 0.5f;
        var hz = sizeZ * 0.5f;
        var vertices = new List<Vertex>();

        AddQuad(vertices,
            new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz),
            topColor);
        AddQuad(vertices,
            new Vector3(hx, -hy, -hz), new Vector3(-hx, -hy, -hz), new Vector3(-hx, hy, -hz), new Vector3(hx, hy, -hz),
            sideColor);
        AddQuad(vertices,
            new Vector3(-hx, -hy, -hz), new Vector3(-hx, -hy, hz), new Vector3(-hx, hy, hz), new Vector3(-hx, hy, -hz),
            sideColor);
        AddQuad(vertices,
            new Vector3(hx, -hy, hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(hx, hy, hz),
            sideColor);
        AddQuad(vertices,
            new Vector3(-hx, hy, hz), new Vector3(hx, hy, hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
            topColor);
        AddQuad(vertices,
            new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, -hy, hz), new Vector3(-hx, -hy, hz),
            sideColor);

        return vertices.ToArray();
    }

    public static (Vertex[] Vertices, uint[] Indices) CreateUvSphere(float radius, int segments, int rings, Vector3 color)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        for (var ring = 0; ring <= rings; ring++)
        {
            var v = ring / (float)rings;
            var phi = v * MathF.PI;

            for (var seg = 0; seg <= segments; seg++)
            {
                var u = seg / (float)segments;
                var theta = u * MathF.Tau;

                var x = radius * MathF.Sin(phi) * MathF.Cos(theta);
                var y = radius * MathF.Cos(phi);
                var z = radius * MathF.Sin(phi) * MathF.Sin(theta);
                // Smooth shading: outward unit normal = normalize(localPosition) for a sphere centred at
                // the origin. Shared vertices interpolate these across the surface. Poles are at distance
                // = radius from the centre, so the normalize is always well-defined.
                var position = new Vector3(x, y, z);
                vertices.Add(new Vertex(position, color, position.Normalized()));
            }
        }

        var rowLength = segments + 1;
        for (var ring = 0; ring < rings; ring++)
        {
            for (var seg = 0; seg < segments; seg++)
            {
                var current = (uint)(ring * rowLength + seg);
                var next = current + (uint)rowLength;

                // Outward-facing winding (normals point away from the sphere centre).
                indices.Add(current);
                indices.Add(current + 1);
                indices.Add(next);

                indices.Add(current + 1);
                indices.Add(next + 1);
                indices.Add(next);
            }
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    public static (Vertex[] Vertices, uint[] Indices) CreateIndexedCylinder(
        float sizeX,
        float sizeY,
        float sizeZ,
        int segments,
        Vector3 color)
    {
        var radiusX = sizeX * 0.5f;
        var radiusZ = sizeZ * 0.5f;
        var halfHeight = sizeY * 0.5f;

        // Side and cap rings use SEPARATE vertices so the side-to-cap edge stays a hard crease: the side
        // ring carries smooth radial normals (shared around the loop, including the wrap seam), while the
        // cap rings carry flat vertical normals. Layout blocks: side-bottom, side-top, cap-bottom, cap-top,
        // then the two cap centres.
        const int sideBottom = 0;
        var sideTop = segments;
        var capBottom = segments * 2;
        var capTop = segments * 3;
        var bottomCenterIndex = (uint)(segments * 4);
        var topCenterIndex = bottomCenterIndex + 1;

        var vertices = new Vertex[segments * 4 + 2];
        var indices = new List<uint>();

        var upNormal = new Vector3(0f, 1f, 0f);
        var downNormal = new Vector3(0f, -1f, 0f);

        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.Tau;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var x = radiusX * cos;
            var z = radiusZ * sin;

            // Local-space radial normal for the smooth side; the shader's inverse-transpose normal matrix
            // corrects it under non-uniform scale (elliptical cross-sections).
            var radial = new Vector3(x, 0f, z).Normalized();
            vertices[sideBottom + i] = new Vertex(new Vector3(x, -halfHeight, z), color, radial);
            vertices[sideTop + i] = new Vertex(new Vector3(x, halfHeight, z), color, radial);
            vertices[capBottom + i] = new Vertex(new Vector3(x, -halfHeight, z), color, downNormal);
            vertices[capTop + i] = new Vertex(new Vector3(x, halfHeight, z), color, upNormal);
        }

        vertices[bottomCenterIndex] = new Vertex(new Vector3(0f, -halfHeight, 0f), color, downNormal);
        vertices[topCenterIndex] = new Vertex(new Vector3(0f, halfHeight, 0f), color, upNormal);

        // Sides (smooth radial normals). Wound so the OUTER surface is front-facing (CCW), matching the
        // outward radial vertex normals so gl_FrontFacing keeps them pointing outward. The pre-lighting mesh
        // wound the outer surface back-facing — harmless for the old derivative shader (its normal sign
        // follows gl_FrontFacing) but it inverts fixed vertex normals, which rendered spawned cylinders dark.
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            var bottom = (uint)(sideBottom + i);
            var top = (uint)(sideTop + i);
            var bottomNext = (uint)(sideBottom + next);
            var topNext = (uint)(sideTop + next);

            indices.Add(bottom);
            indices.Add(top);
            indices.Add(bottomNext);

            indices.Add(top);
            indices.Add(topNext);
            indices.Add(bottomNext);
        }

        // Bottom cap (flat downward normals). Outer (-Y) surface front-facing.
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            indices.Add(bottomCenterIndex);
            indices.Add((uint)(capBottom + i));
            indices.Add((uint)(capBottom + next));
        }

        // Top cap (flat upward normals). Outer (+Y) surface front-facing.
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            indices.Add(topCenterIndex);
            indices.Add((uint)(capTop + next));
            indices.Add((uint)(capTop + i));
        }

        return (vertices, indices.ToArray());
    }

    /// <summary>
    /// Centered triangular-prism wedge in a unit (±size/2) box. Base on -Y, vertical face on -Z, slope
    /// from the top -Z edge down to the bottom +Z edge, extruded along X. Per-face duplicated vertices
    /// (flat-shading ready) with consistent outward winding. 18 vertices, 8 triangles.
    /// </summary>
    public static (Vertex[] Vertices, uint[] Indices) CreateIndexedWedge(float sizeX, float sizeY, float sizeZ, Vector3 color)
    {
        var hx = sizeX * 0.5f;
        var hy = sizeY * 0.5f;
        var hz = sizeZ * 0.5f;

        var bfl = new Vector3(-hx, -hy, hz);
        var bfr = new Vector3(hx, -hy, hz);
        var bbl = new Vector3(-hx, -hy, -hz);
        var bbr = new Vector3(hx, -hy, -hz);
        var tbl = new Vector3(-hx, hy, -hz);
        var tbr = new Vector3(hx, hy, -hz);

        var vertices = new List<Vertex>(18);
        var indices = new List<uint>(24);

        AddIndexedQuad(vertices, indices, bfl, bbl, bbr, bfr, color); // bottom (-Y)
        AddIndexedQuad(vertices, indices, bbr, bbl, tbl, tbr, color); // back (-Z)
        AddIndexedQuad(vertices, indices, tbl, bfl, bfr, tbr, color); // slope (+Y/+Z)
        AddIndexedTriangle(vertices, indices, bfl, tbl, bbl, color);  // left cap (-X)
        AddIndexedTriangle(vertices, indices, bfr, bbr, tbr, color);  // right cap (+X)

        return (vertices.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// Centered square pyramid in a unit (±size/2) box. Base on -Y, apex centered at +Y. Per-face
    /// duplicated vertices (flat-shading ready) with consistent outward winding. 16 vertices, 6 triangles.
    /// </summary>
    public static (Vertex[] Vertices, uint[] Indices) CreateIndexedPyramid(float sizeX, float sizeY, float sizeZ, Vector3 color)
    {
        var hx = sizeX * 0.5f;
        var hy = sizeY * 0.5f;
        var hz = sizeZ * 0.5f;

        var a = new Vector3(-hx, -hy, -hz);
        var b = new Vector3(hx, -hy, -hz);
        var c = new Vector3(hx, -hy, hz);
        var d = new Vector3(-hx, -hy, hz);
        var apex = new Vector3(0f, hy, 0f);

        var vertices = new List<Vertex>(16);
        var indices = new List<uint>(18);

        AddIndexedQuad(vertices, indices, a, b, c, d, color);     // base (-Y)
        AddIndexedTriangle(vertices, indices, d, c, apex, color); // +Z side
        AddIndexedTriangle(vertices, indices, c, b, apex, color); // +X side
        AddIndexedTriangle(vertices, indices, b, a, apex, color); // -Z side
        AddIndexedTriangle(vertices, indices, a, d, apex, color); // -X side

        return (vertices.ToArray(), indices.ToArray());
    }

    // Appends a quad as 4 duplicated vertices + 2 triangles: (v0,v1,v2) and (v0,v2,v3).
    private static void AddIndexedQuad(
        List<Vertex> vertices, List<uint> indices,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 color)
    {
        var b = (uint)vertices.Count;
        vertices.Add(new Vertex(v0, color));
        vertices.Add(new Vertex(v1, color));
        vertices.Add(new Vertex(v2, color));
        vertices.Add(new Vertex(v3, color));
        indices.Add(b);
        indices.Add(b + 1);
        indices.Add(b + 2);
        indices.Add(b);
        indices.Add(b + 2);
        indices.Add(b + 3);
    }

    // Appends a triangle as 3 duplicated vertices + 1 triangle (v0,v1,v2).
    private static void AddIndexedTriangle(
        List<Vertex> vertices, List<uint> indices,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 color)
    {
        var b = (uint)vertices.Count;
        vertices.Add(new Vertex(v0, color));
        vertices.Add(new Vertex(v1, color));
        vertices.Add(new Vertex(v2, color));
        indices.Add(b);
        indices.Add(b + 1);
        indices.Add(b + 2);
    }

    private static void AddQuad(List<Vertex> vertices, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 color)
    {
        vertices.Add(new Vertex(v0, color));
        vertices.Add(new Vertex(v1, color));
        vertices.Add(new Vertex(v2, color));

        vertices.Add(new Vertex(v0, color));
        vertices.Add(new Vertex(v2, color));
        vertices.Add(new Vertex(v3, color));
    }
}
