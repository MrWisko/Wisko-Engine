using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering.Backend.Bgfx.Editor;

// bgfx rendering adapters for the Move / Rotate / Resize gizmos. MoveGizmo.cs, RotateGizmo.cs and
// ResizeGizmo.cs are frozen and their renderers issue OpenGL calls directly, so they cannot run on the
// bgfx path. These adapters reproduce ONLY their rendering side: the same GizmoMeshFactory geometry, the
// same colours (copied from the frozen renderers), the same transforms (via the frozen files' public
// layout/scale helpers) and the same idle/dim/bright draw order with depth testing off. All interaction
// (picking, hover, drag, snapping) stays in the shared, frozen controllers.

/// <summary>bgfx rendering of the Move gizmo (mirrors MoveGizmoRenderer).</summary>
internal sealed class BgfxMoveGizmoRenderer : IDisposable
{
    private enum HandleState { Idle, Dim, Bright }

    private static readonly Vector3 PurpleDir = new Vector3(1f, 0f, 1f).Normalized();
    private static readonly Vector3 YellowDir = new Vector3(1f, 0f, -1f).Normalized();
    private static readonly Vector3 XBase = new(0.80f, 0.36f, 0.40f);
    private static readonly Vector3 YBase = new(0.47f, 0.73f, 0.40f);
    private static readonly Vector3 ZBase = new(0.34f, 0.53f, 0.82f);
    private static readonly Vector3 PurpleIdle = new(0.52f, 0.40f, 0.66f);
    private static readonly Vector3 PurpleBright = new(0.80f, 0.55f, 1.0f);
    private static readonly Vector3 YellowIdle = new(0.60f, 0.56f, 0.32f);
    private static readonly Vector3 YellowBright = new(1.0f, 0.90f, 0.35f);
    private const float DimFactor = 0.45f;
    private const float BrightFactor = 1.35f;

    private static readonly GizmoAxis[] Handles =
        { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z, GizmoAxis.Purple, GizmoAxis.Yellow };

    private readonly Dictionary<(GizmoAxis, HandleState), ColoredMesh> _meshes = new();

    public void Initialize()
    {
        Add(GizmoAxis.X, "MoveGizmoX", Vector3.UnitX, XBase, Dim(XBase), Bright(XBase), diagonal: false);
        Add(GizmoAxis.Y, "MoveGizmoY", Vector3.UnitY, YBase, Dim(YBase), Bright(YBase), diagonal: false);
        Add(GizmoAxis.Z, "MoveGizmoZ", Vector3.UnitZ, ZBase, Dim(ZBase), Bright(ZBase), diagonal: false);
        Add(GizmoAxis.Purple, "MoveGizmoXplusZ", PurpleDir, PurpleIdle, Dim(PurpleIdle), PurpleBright, diagonal: true);
        Add(GizmoAxis.Yellow, "MoveGizmoXminusZ", YellowDir, YellowIdle, Dim(YellowIdle), YellowBright, diagonal: true);
    }

    public void Submit(BgfxEditorGraphics graphics, ushort view, Part part, EditorCamera camera, GizmoAxis hovered, GizmoAxis active)
    {
        if (_meshes.Count == 0)
        {
            return;
        }

        var scale = MoveGizmoScale.Compute(part.Position, camera);
        var model = Matrix4.Multiply(Matrix4.CreateTranslation(part.Position), Matrix4.CreateScale(new Vector3(scale, scale, scale)));
        var dragging = active != GizmoAxis.None;
        var emphasized = dragging ? active : hovered;

        foreach (var handle in Handles)
        {
            if (handle != emphasized)
            {
                graphics.SubmitUnlit(view, _meshes[(handle, dragging ? HandleState.Dim : HandleState.Idle)], model, BgfxEditorGraphics.OverlayState);
            }
        }

        if (emphasized != GizmoAxis.None)
        {
            graphics.SubmitUnlit(view, _meshes[(emphasized, HandleState.Bright)], model, BgfxEditorGraphics.OverlayState);
        }
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
        {
            mesh.Dispose();
        }

        _meshes.Clear();
    }

    private void Add(GizmoAxis axis, string name, Vector3 direction, Vector3 idle, Vector3 dim, Vector3 bright, bool diagonal)
    {
        ColoredMesh Make(string n, Vector3 c) => diagonal
            ? GizmoMeshFactory.CreateDiagonalArrowMesh(n, direction, c)
            : GizmoMeshFactory.CreateArrowMesh(n, direction, c);
        _meshes[(axis, HandleState.Idle)] = Make(name, idle);
        _meshes[(axis, HandleState.Dim)] = Make(name + "Dim", dim);
        _meshes[(axis, HandleState.Bright)] = Make(name + "Bright", bright);
    }

    private static Vector3 Dim(Vector3 c) => new(c.X * DimFactor, c.Y * DimFactor, c.Z * DimFactor);

    private static Vector3 Bright(Vector3 c) => new(
        global::System.Math.Min(1f, c.X * BrightFactor),
        global::System.Math.Min(1f, c.Y * BrightFactor),
        global::System.Math.Min(1f, c.Z * BrightFactor));
}

/// <summary>bgfx rendering of the Rotate gizmo (mirrors RotateGizmoRenderer, including the camera-facing arcs).</summary>
internal sealed class BgfxRotateGizmoRenderer : IDisposable
{

    private static readonly Vector3 XIdle = new(0.80f, 0.36f, 0.40f);
    private static readonly Vector3 YIdle = new(0.47f, 0.73f, 0.40f);
    private static readonly Vector3 ZIdle = new(0.34f, 0.53f, 0.82f);
    private static readonly Vector3 XDim = new(0.44f, 0.20f, 0.22f);
    private static readonly Vector3 YDim = new(0.26f, 0.40f, 0.22f);
    private static readonly Vector3 ZDim = new(0.19f, 0.29f, 0.45f);
    private static readonly Vector3 XBright = new(1.0f, 0.52f, 0.56f);
    private static readonly Vector3 YBright = new(0.60f, 0.95f, 0.50f);
    private static readonly Vector3 ZBright = new(0.52f, 0.74f, 1.0f);
    private static readonly Vector3 PurpleIdle = new(0.40f, 0.32f, 0.52f);
    private static readonly Vector3 YellowIdle = new(0.50f, 0.47f, 0.28f);
    private static readonly Vector3 PurpleDim = new(0.24f, 0.19f, 0.31f);
    private static readonly Vector3 YellowDim = new(0.30f, 0.28f, 0.17f);
    private static readonly Vector3 PurpleBright = new(0.78f, 0.50f, 1.0f);
    private static readonly Vector3 YellowBright = new(1.0f, 0.90f, 0.30f);

    private readonly Dictionary<(RotateHandle, RotateHandleState), ColoredMesh> _rings = new();
    private readonly List<Vertex> _arcVertices = new();

    public void Initialize()
    {
        AddRing(RotateHandle.X, "RotateRingX", Vector3.UnitX, XIdle, XDim, XBright);
        AddRing(RotateHandle.Y, "RotateRingY", Vector3.UnitY, YIdle, YDim, YBright);
        AddRing(RotateHandle.Z, "RotateRingZ", Vector3.UnitZ, ZIdle, ZDim, ZBright);
    }

    public void Submit(BgfxEditorGraphics graphics, ushort view, Part part, EditorCamera camera, RotateHandle hovered, RotateHandle active)
    {
        if (_rings.Count == 0)
        {
            return;
        }

        var gizmoScale = MoveGizmoScale.Compute(part.Position, camera);
        var ringScale = gizmoScale * RotateGizmoLayout.RingRadiusFactor;
        var dragging = active != RotateHandle.None;
        var emphasized = dragging ? active : hovered;
        var ringModel = Matrix4.Multiply(Matrix4.CreateTranslation(part.Position), Matrix4.CreateScale(new Vector3(ringScale, ringScale, ringScale)));

        // Pass 1: non-emphasised rings; pass 2: non-emphasised arcs; pass 3: the emphasised handle, bright.
        foreach (var ring in new[] { RotateHandle.X, RotateHandle.Y, RotateHandle.Z })
        {
            if (ring != emphasized)
            {
                graphics.SubmitUnlit(view, _rings[(ring, dragging ? RotateHandleState.Dim : RotateHandleState.Idle)], ringModel, BgfxEditorGraphics.OverlayState);
            }
        }

        foreach (var arc in new[] { RotateHandle.Purple, RotateHandle.Yellow })
        {
            if (arc != emphasized)
            {
                SubmitArc(graphics, view, part.Position, camera, gizmoScale, arc, ArcColor(arc, dragging ? RotateHandleState.Dim : RotateHandleState.Idle));
            }
        }

        if (emphasized is RotateHandle.X or RotateHandle.Y or RotateHandle.Z)
        {
            graphics.SubmitUnlit(view, _rings[(emphasized, RotateHandleState.Bright)], ringModel, BgfxEditorGraphics.OverlayState);
        }
        else if (emphasized is RotateHandle.Purple or RotateHandle.Yellow)
        {
            SubmitArc(graphics, view, part.Position, camera, gizmoScale, emphasized, ArcColor(emphasized, RotateHandleState.Bright));
        }
    }

    public void Dispose()
    {
        foreach (var mesh in _rings.Values)
        {
            mesh.Dispose();
        }

        _rings.Clear();
    }

    private void AddRing(RotateHandle handle, string name, Vector3 axis, Vector3 idle, Vector3 dim, Vector3 bright)
    {
        ColoredMesh Make(string n, Vector3 c) => GizmoMeshFactory.CreateRingMesh(
            n, axis, c, RotateGizmoLayout.RingRadialHalfThickness, RotateGizmoLayout.RingAxialHalfThickness);
        _rings[(handle, RotateHandleState.Idle)] = Make(name + "Idle", idle);
        _rings[(handle, RotateHandleState.Dim)] = Make(name + "Dim", dim);
        _rings[(handle, RotateHandleState.Bright)] = Make(name + "Bright", bright);
    }

    private static Vector3 ArcColor(RotateHandle handle, RotateHandleState state) =>
        handle == RotateHandle.Purple
            ? state == RotateHandleState.Bright ? PurpleBright : state == RotateHandleState.Dim ? PurpleDim : PurpleIdle
            : state == RotateHandleState.Bright ? YellowBright : state == RotateHandleState.Dim ? YellowDim : YellowIdle;

    // Same partial-annulus geometry as RotateGizmoRenderer.DrawArc (camera plane, never edge-on).
    private void SubmitArc(BgfxEditorGraphics graphics, ushort view, Vector3 center, EditorCamera camera, float gizmoScale, RotateHandle handle, Vector3 color)
    {
        _arcVertices.Clear();
        var right = camera.Right;
        var up = camera.Up;
        var arcRadius = gizmoScale * RotateGizmoLayout.RingRadiusFactor * RotateGizmoLayout.ArcRadiusFactor;
        var halfWidth = gizmoScale * RotateGizmoLayout.ArcHalfWidthFactor;
        var centerDeg = handle == RotateHandle.Purple ? RotateGizmoLayout.ArcPurpleCenterDeg : RotateGizmoLayout.ArcYellowCenterDeg;
        var startDeg = centerDeg - RotateGizmoLayout.ArcSweepDegrees * 0.5f;

        Vector3 prevInner = default, prevOuter = default;
        for (var i = 0; i <= RotateGizmoLayout.ArcSegments; i++)
        {
            var deg = startDeg + RotateGizmoLayout.ArcSweepDegrees * (i / (float)RotateGizmoLayout.ArcSegments);
            var rad = deg * MathUtil.DegreesToRadians;
            var dir = right * MathF.Cos(rad) + up * MathF.Sin(rad);
            var inner = center + dir * (arcRadius - halfWidth);
            var outer = center + dir * (arcRadius + halfWidth);
            if (i > 0)
            {
                _arcVertices.Add(new Vertex(prevInner, color));
                _arcVertices.Add(new Vertex(prevOuter, color));
                _arcVertices.Add(new Vertex(outer, color));
                _arcVertices.Add(new Vertex(prevInner, color));
                _arcVertices.Add(new Vertex(outer, color));
                _arcVertices.Add(new Vertex(inner, color));
            }

            prevInner = inner;
            prevOuter = outer;
        }

        graphics.SubmitTransientTriangles(view, _arcVertices, BgfxEditorGraphics.OverlayState);
    }
}

/// <summary>bgfx rendering of the Resize gizmo (mirrors ResizeGizmoRenderer, including corner brackets).</summary>
internal sealed class BgfxResizeGizmoRenderer : IDisposable
{
    private enum HandleState { Idle, Dim, Bright }

    private const float VisibleHandleScaleFactor = 0.075f;
    private const float BracketScaleFactor = 0.09f;
    private const float DimFactor = 0.45f;
    private const float BrightFactor = 1.35f;

    private static readonly Vector3 XColor = new(0.80f, 0.36f, 0.40f);
    private static readonly Vector3 YColor = new(0.47f, 0.73f, 0.40f);
    private static readonly Vector3 ZColor = new(0.34f, 0.53f, 0.82f);
    private static readonly Vector3 PurpleIdle = new(0.52f, 0.40f, 0.66f);
    private static readonly Vector3 PurpleBright = new(0.80f, 0.55f, 1.0f);
    private static readonly Vector3 YellowIdle = new(0.60f, 0.56f, 0.32f);
    private static readonly Vector3 YellowBright = new(1.0f, 0.90f, 0.35f);

    private readonly Dictionary<(char Axis, HandleState State), ColoredMesh> _cubes = new();
    private readonly Dictionary<(ResizeCornerHandle Corner, HandleState State), ColoredMesh> _brackets = new();

    public void Initialize()
    {
        AddCube('x', "ResizeHandleX", XColor);
        AddCube('y', "ResizeHandleY", YColor);
        AddCube('z', "ResizeHandleZ", ZColor);
        AddBracket(ResizeCornerHandle.Purple, "ResizeCornerXplusZ", 1f, PurpleIdle, PurpleBright);
        AddBracket(ResizeCornerHandle.Yellow, "ResizeCornerXminusZ", -1f, YellowIdle, YellowBright);
    }

    public void Submit(
        BgfxEditorGraphics graphics,
        ushort view,
        Part part,
        EditorCamera camera,
        ResizeHandle hovered,
        ResizeHandle active,
        ResizeCornerHandle cornerHovered,
        ResizeCornerHandle cornerActive,
        bool showCorners)
    {
        if (_cubes.Count == 0)
        {
            return;
        }

        var gizmoScale = MoveGizmoScale.Compute(part.Position, camera);
        var visibleSize = gizmoScale * VisibleHandleScaleFactor;
        var scale = new Vector3(visibleSize, visibleSize, visibleSize);
        var dragging = active != ResizeHandle.None || cornerActive != ResizeCornerHandle.None;
        var faceEmphasis = dragging ? active : hovered;

        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            if (handle == ResizeHandle.None)
            {
                continue;
            }

            var model = Matrix4.Multiply(
                Matrix4.CreateTranslation(ResizeGizmoRenderer.GetHandleWorldPosition(part, handle)),
                Matrix4.CreateScale(scale));
            var state = handle == faceEmphasis ? HandleState.Bright : dragging ? HandleState.Dim : HandleState.Idle;
            graphics.SubmitUnlit(view, _cubes[(AxisOf(handle), state)], model, BgfxEditorGraphics.OverlayState);
        }

        if (showCorners)
        {
            var cornerEmphasis = dragging ? cornerActive : cornerHovered;
            var rotation = Matrix4.CreateRotationEuler(part.Rotation);
            var bracketWorld = gizmoScale * BracketScaleFactor;
            var bracketSize = new Vector3(bracketWorld, bracketWorld, bracketWorld);
            foreach (var corner in new[] { ResizeCornerHandle.Purple, ResizeCornerHandle.Yellow })
            {
                var model = Matrix4.Multiply(
                    Matrix4.CreateTranslation(ResizeGizmoRenderer.GetCornerWorldPosition(part, corner)),
                    Matrix4.Multiply(rotation, Matrix4.CreateScale(bracketSize)));
                var state = corner == cornerEmphasis ? HandleState.Bright : dragging ? HandleState.Dim : HandleState.Idle;
                graphics.SubmitUnlit(view, _brackets[(corner, state)], model, BgfxEditorGraphics.OverlayState);
            }
        }
    }

    public void Dispose()
    {
        foreach (var mesh in _cubes.Values.Concat(_brackets.Values))
        {
            mesh.Dispose();
        }

        _cubes.Clear();
        _brackets.Clear();
    }

    private static char AxisOf(ResizeHandle handle) => handle switch
    {
        ResizeHandle.PositiveX or ResizeHandle.NegativeX => 'x',
        ResizeHandle.PositiveY or ResizeHandle.NegativeY => 'y',
        _ => 'z'
    };

    private void AddCube(char axis, string name, Vector3 color)
    {
        _cubes[(axis, HandleState.Idle)] = UnitCube(name, color);
        _cubes[(axis, HandleState.Dim)] = UnitCube(name + "Dim", Dim(color));
        _cubes[(axis, HandleState.Bright)] = UnitCube(name + "Bright", Bright(color));
    }

    private void AddBracket(ResizeCornerHandle corner, string name, float signZ, Vector3 idle, Vector3 bright)
    {
        _brackets[(corner, HandleState.Idle)] = GizmoMeshFactory.CreateCornerBracketMesh(name, 1f, signZ, idle);
        _brackets[(corner, HandleState.Dim)] = GizmoMeshFactory.CreateCornerBracketMesh(name + "Dim", 1f, signZ, Dim(idle));
        _brackets[(corner, HandleState.Bright)] = GizmoMeshFactory.CreateCornerBracketMesh(name + "Bright", 1f, signZ, bright);
    }

    private static ColoredMesh UnitCube(string name, Vector3 color)
    {
        var (vertices, indices) = MeshFactory.CreateIndexedBox(1f, 1f, 1f, color);
        return ColoredMesh.FromIndexedVertices(name, vertices, indices);
    }

    private static Vector3 Dim(Vector3 c) => new(c.X * DimFactor, c.Y * DimFactor, c.Z * DimFactor);

    private static Vector3 Bright(Vector3 c) => new(
        global::System.Math.Min(1f, c.X * BrightFactor),
        global::System.Math.Min(1f, c.Y * BrightFactor),
        global::System.Math.Min(1f, c.Z * BrightFactor));
}
