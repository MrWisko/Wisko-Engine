using Wisko.Engine.Math;
using Wisko.Engine.Rendering;

namespace Wisko.Engine.Editor;

internal static class GizmoMeshFactory
{
    public const float UnitArrowLength = 1.45f;

    // Slim shaft + cone head, in unit space (scaled per-frame by MoveGizmoScale). Kept within
    // UnitArrowLength so the analytic Move picking (shaft segment + head sphere at 1.14) still
    // aligns: the cone spans [1.0, 1.45], inside the head-pick sphere (centre 1.14, radius 0.42).
    private const float ShaftThickness = 0.045f;   // cross-section (was a 0.14 blocky bar)
    private const float ShaftLength = 1.0f;         // along the axis: 0 .. 1.0
    private const float ConeBaseDistance = 1.0f;    // cone base where the shaft ends
    private const float ConeHeight = 0.45f;         // tip at 1.0 + 0.45 = 1.45 = UnitArrowLength
    private const float ConeBaseRadius = 0.12f;
    private const int ConeSegments = 16;            // modest: smooth enough, little geometry

    public static ColoredMesh CreateArrowMesh(string name, Vector3 axis, Vector3 color)
    {
        var (vertices, indices) = CreateSimpleArrow(axis, color);
        return ColoredMesh.FromIndexedVertices(name, vertices, indices);
    }

    // Compact secondary "dual-axis" arrow pointing along an ARBITRARY unit direction (used by the
    // Move gizmo's purple/yellow diagonals). It is offset from the origin (DiagArrowInner..DiagArrowTip)
    // so it sits in the 45-degree gap between the RGB axis arrows and never crosses the object centre.
    // Same visual family as the RGB arrows (thin shaft + cone), just shorter, thinner, and off-axis.
    private const float DiagShaftStart = 0.35f;
    private const float DiagShaftEnd = 0.72f;
    private const float DiagShaftHalf = 0.03f;      // thinner than the RGB shaft (reads as secondary)
    private const float DiagConeBaseRadius = 0.10f;
    private const float DiagConeHeight = 0.23f;     // tip at 0.72 + 0.23 = 0.95
    private const int DiagConeSegments = 16;

    public const float DiagArrowInner = DiagShaftStart; // pick centreline start (unit space)
    public const float DiagArrowTip = DiagShaftEnd + DiagConeHeight; // pick centreline end (unit space)

    public static ColoredMesh CreateDiagonalArrowMesh(string name, Vector3 direction, Vector3 color)
    {
        var a = direction.Normalized();
        var reference = MathF.Abs(a.Y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f);
        var u = Vector3.Cross(a, reference).Normalized();
        var v = Vector3.Cross(a, u);

        var shaft = OrientedShaft(a, u, v, DiagShaftStart, DiagShaftEnd, DiagShaftHalf, color);
        var head = Cone(a, DiagShaftEnd, DiagConeBaseRadius, DiagConeHeight, DiagConeSegments, color);
        var (vertices, indices) = Merge(shaft, head);
        return ColoredMesh.FromIndexedVertices(name, vertices, indices);
    }

    // Corner-bracket handle for the dual-axis Resize shortcuts: two short thin bars in the local XZ
    // plane meeting at the moving corner and opening inward (mirrored per corner by signX/signZ). Built
    // in a unit local frame with the corner at the origin; the renderer places/rotates/scales it at the
    // part's corner. Reuses OffsetBox/Merge; winding is cosmetic (culling disabled).
    private const float BracketBarLength = 1.0f;
    private const float BracketBarThickness = 0.16f;

    public static ColoredMesh CreateCornerBracketMesh(string name, float signX, float signZ, Vector3 color)
    {
        var half = BracketBarLength * 0.5f;
        var t = BracketBarThickness;
        var barX = OffsetBox(-signX * half, 0f, 0f, BracketBarLength, t, t, color);
        var barZ = OffsetBox(0f, 0f, -signZ * half, t, t, BracketBarLength, color);
        var (vertices, indices) = Merge(barX, barZ);
        return ColoredMesh.FromIndexedVertices(name, vertices, indices);
    }

    // A thin box swept along 'a' from start..end with a square (u,v) cross-section of half-extent 'half'.
    // Winding is cosmetic (culling is disabled engine-wide), so both faces render.
    private static (Vertex[] Vertices, uint[] Indices) OrientedShaft(
        Vector3 a, Vector3 u, Vector3 v, float start, float end, float half, Vector3 color)
    {
        Vector3 P(float t, float su, float sv) => a * t + u * (su * half) + v * (sv * half);

        var vertices = new[]
        {
            new Vertex(P(start, -1f, -1f), color),
            new Vertex(P(start, -1f, 1f), color),
            new Vertex(P(start, 1f, -1f), color),
            new Vertex(P(start, 1f, 1f), color),
            new Vertex(P(end, -1f, -1f), color),
            new Vertex(P(end, -1f, 1f), color),
            new Vertex(P(end, 1f, -1f), color),
            new Vertex(P(end, 1f, 1f), color),
        };

        var indices = new List<uint>(36);
        void Quad(uint qa, uint qb, uint qc, uint qd)
        {
            indices.Add(qa); indices.Add(qb); indices.Add(qc);
            indices.Add(qa); indices.Add(qc); indices.Add(qd);
        }

        Quad(0, 1, 3, 2); // start cap
        Quad(4, 6, 7, 5); // end cap
        Quad(0, 4, 5, 1); // -u wall
        Quad(2, 3, 7, 6); // +u wall
        Quad(0, 2, 6, 4); // -v wall
        Quad(1, 5, 7, 3); // +v wall

        return (vertices, indices.ToArray());
    }

    // Rotation ring of unit mean radius in the plane perpendicular to 'axis'. Built as a thin
    // rectangular-tube (box cross-section) sweep rather than a flat washer: a zero-thickness annulus
    // collapses to an invisible line when viewed edge-on (face culling is disabled engine-wide, so
    // culling is not the cause). A small axial extent keeps the ring readable from the front, the back
    // and edge-on while staying visually thin. Triangles only (macOS core profiles clamp wide GL lines
    // to 1px). The renderer scales it by the gizmo scale, so radius and thickness stay apparent-size
    // stable together. radialHalfThickness = face-on thinness; axialHalfThickness = edge-on visibility.
    public const float RingUnitRadius = 1.0f;
    public const float RingUnitHalfThickness = 0.03f; // default half-extent (radial and axial) in unit-ring space

    public static ColoredMesh CreateRingMesh(
        string name,
        Vector3 axis,
        Vector3 color,
        float radialHalfThickness = RingUnitHalfThickness,
        float axialHalfThickness = RingUnitHalfThickness)
    {
        const int segments = 48;
        var a = axis.Normalized();
        var reference = MathF.Abs(a.Y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f);
        var u = Vector3.Cross(a, reference).Normalized();
        var v = Vector3.Cross(a, u);

        // Four cross-section vertices per segment: inner/outer radial edges, each at -/+ the axial
        // offset along the ring axis. Per-segment order: 0 inner-lo, 1 outer-lo, 2 outer-hi, 3 inner-hi.
        var vertices = new Vertex[segments * 4];
        for (var i = 0; i < segments; i++)
        {
            var t = i / (float)segments * MathF.Tau;
            var dir = u * MathF.Cos(t) + v * MathF.Sin(t);
            var inner = dir * (RingUnitRadius - radialHalfThickness);
            var outer = dir * (RingUnitRadius + radialHalfThickness);
            var lo = a * -axialHalfThickness;
            var hi = a * axialHalfThickness;
            var b = i * 4;
            vertices[b + 0] = new Vertex(inner + lo, color);
            vertices[b + 1] = new Vertex(outer + lo, color);
            vertices[b + 2] = new Vertex(outer + hi, color);
            vertices[b + 3] = new Vertex(inner + hi, color);
        }

        // Four walls (outer, inner, top rim, bottom rim) between consecutive segments. Winding is
        // cosmetic (culling disabled), so both faces show — visible front and back.
        var indices = new uint[segments * 24];
        var w = 0;
        for (var i = 0; i < segments; i++)
        {
            var c0 = (uint)(i * 4);
            var next = (i + 1) % segments;
            var c1 = (uint)(next * 4);

            AddQuad(indices, ref w, c0 + 1, c0 + 2, c1 + 2, c1 + 1); // outer wall
            AddQuad(indices, ref w, c1 + 0, c1 + 3, c0 + 3, c0 + 0); // inner wall
            AddQuad(indices, ref w, c0 + 3, c0 + 2, c1 + 2, c1 + 3); // top rim
            AddQuad(indices, ref w, c0 + 0, c1 + 0, c1 + 1, c0 + 1); // bottom rim
        }

        return ColoredMesh.FromIndexedVertices(name, vertices, indices);
    }

    private static void AddQuad(uint[] indices, ref int w, uint a, uint b, uint c, uint d)
    {
        indices[w++] = a;
        indices[w++] = b;
        indices[w++] = c;
        indices[w++] = a;
        indices[w++] = c;
        indices[w++] = d;
    }

    private static (Vertex[] Vertices, uint[] Indices) CreateSimpleArrow(Vector3 axis, Vector3 color)
    {
        var half = ShaftLength * 0.5f;

        (Vertex[] Vertices, uint[] Indices) shaft;
        if (axis.Y > 0.9f)
        {
            shaft = OffsetBox(0f, half, 0f, ShaftThickness, ShaftLength, ShaftThickness, color);
        }
        else if (axis.Z > 0.9f)
        {
            shaft = OffsetBox(0f, 0f, half, ShaftThickness, ShaftThickness, ShaftLength, color);
        }
        else
        {
            shaft = OffsetBox(half, 0f, 0f, ShaftLength, ShaftThickness, ShaftThickness, color);
        }

        var head = Cone(axis, ConeBaseDistance, ConeBaseRadius, ConeHeight, ConeSegments, color);
        return Merge(shaft, head);
    }

    /// <summary>
    /// A cone oriented along <paramref name="axis"/>, base at <paramref name="baseDistance"/> from the
    /// origin and tip at baseDistance + height. Back-face culling is disabled and the mesh is a single
    /// flat colour, so winding is purely cosmetic; it is kept consistent (outward CCW) and constructed
    /// identically for X/Y/Z from an orthonormal perpendicular basis, so all three axes match.
    /// </summary>
    private static (Vertex[] Vertices, uint[] Indices) Cone(
        Vector3 axis, float baseDistance, float baseRadius, float height, int segments, Vector3 color)
    {
        var a = axis.Normalized();
        var reference = MathF.Abs(a.Y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f);
        var u = Vector3.Cross(a, reference).Normalized();
        var v = Vector3.Cross(a, u); // unit; (u, v, a) is right-handed

        var baseCenter = a * baseDistance;
        var tip = a * (baseDistance + height);

        var vertices = new Vertex[segments + 2];
        for (var i = 0; i < segments; i++)
        {
            var t = i / (float)segments * MathF.Tau;
            var dir = u * MathF.Cos(t) + v * MathF.Sin(t);
            vertices[i] = new Vertex(baseCenter + dir * baseRadius, color);
        }

        var tipIndex = (uint)segments;
        var baseCenterIndex = (uint)(segments + 1);
        vertices[segments] = new Vertex(tip, color);
        vertices[segments + 1] = new Vertex(baseCenter, color);

        var indices = new uint[segments * 6];
        var w = 0;
        for (var i = 0; i < segments; i++)
        {
            var cur = (uint)i;
            var next = (uint)((i + 1) % segments);

            // Side face toward the tip.
            indices[w++] = cur;
            indices[w++] = next;
            indices[w++] = tipIndex;

            // Base cap (faces away from the tip).
            indices[w++] = baseCenterIndex;
            indices[w++] = next;
            indices[w++] = cur;
        }

        return (vertices, indices);
    }

    private static (Vertex[], uint[]) OffsetBox(
        float cx, float cy, float cz,
        float sx, float sy, float sz,
        Vector3 color)
    {
        var (vertices, indices) = MeshFactory.CreateIndexedBox(sx, sy, sz, color);
        var offset = new Vertex[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var p = vertices[i].Position;
            offset[i] = new Vertex(new Vector3(p.X + cx, p.Y + cy, p.Z + cz), color);
        }

        return (offset, indices);
    }

    private static (Vertex[] Vertices, uint[] Indices) Merge(
        (Vertex[] Vertices, uint[] Indices) a,
        (Vertex[] Vertices, uint[] Indices) b)
    {
        var vertices = new Vertex[a.Vertices.Length + b.Vertices.Length];
        a.Vertices.CopyTo(vertices, 0);
        b.Vertices.CopyTo(vertices, a.Vertices.Length);

        var indices = new uint[a.Indices.Length + b.Indices.Length];
        for (var i = 0; i < a.Indices.Length; i++)
        {
            indices[i] = a.Indices[i];
        }

        for (var i = 0; i < b.Indices.Length; i++)
        {
            indices[a.Indices.Length + i] = b.Indices[i] + (uint)a.Vertices.Length;
        }

        return (vertices, indices);
    }
}
