using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Editor;

internal enum RotateHandle
{
    None,
    X,
    Y,
    Z,
    Purple, // '/' : coupled X(+a), Y(+a)
    Yellow  // '\' : coupled X(+a), Y(-a)
}

/// <summary>Three visual states so the hierarchy (idle / dimmed-during-drag / bright) reads without alpha.</summary>
internal enum RotateHandleState
{
    Idle,
    Dim,
    Bright
}

/// <summary>Shared geometry/pick constants so the renderer's visuals and the controller's hit-testing agree.</summary>
internal static class RotateGizmoLayout
{
    // Rings ---------------------------------------------------------------------------------------
    public const float RingRadiusFactor = 0.90f;         // world ring mean radius = gizmoScale * this
    public const float RingRadialHalfThickness = 0.018f; // unit-ring space: face-on thinness (thin, professional)
    public const float RingAxialHalfThickness = 0.032f;  // unit-ring space: standing-ribbon height for edge-on visibility
    public const int RingPickSamples = 48;               // samples used to project the ring centreline for screen-space picking

    // Secondary camera-facing arcs ----------------------------------------------------------------
    public const float ArcRadiusFactor = 0.62f;          // arc mean radius = ringRadius * this (well inside the ring)
    public const float ArcHalfWidthFactor = 0.018f;      // world arc half-width = gizmoScale * this (subtle)
    public const float ArcSweepDegrees = 60f;            // angular length of each arc
    public const float ArcPurpleCenterDeg = 135f;        // upper-left sector (screen basis: 0 = +Right, 90 = +Up)
    public const float ArcYellowCenterDeg = 45f;         // upper-right sector
    public const int ArcSegments = 16;                   // tessellation of each arc

    // Picking / drag ------------------------------------------------------------------------------
    public const float RingPickPixels = 12f;             // screen-space hit distance to a ring centreline
    public const float ArcPickPixels = 18f;              // screen-space hit distance to an arc centreline (easier to grab)
    public const float MinScreenStartRadiusPx = 20f;     // screen-sweep handles ungrabbable within this of the projected centre
    public const float ScreenSweepMinRadiusPx = 12f;     // per-frame: hold the angle when the cursor is nearer the centre than this
    public const float EdgeOnThreshold = 0.12f;          // ring drag solver: |dot(rayDir,axis)| >= this -> ray-plane, else screen-sweep
    public const float NearCenterFactor = 0.25f;         // ray-plane ring drag: ignore hits closer than gizmoScale*this to the centre
}

/// <summary>
/// Draws the five-handle Rotate gizmo: three world-aligned RGB rings (thin rectangular-tube ribbons so they
/// stay readable front, back and edge-on) and two camera-facing curved arcs (secondary '/' purple and '\'
/// yellow). No alpha/shader changes: hierarchy is idle / dim (all inactive handles during a drag) / bright
/// (the hovered-or-active handle, always drawn last so it stays on top).
/// </summary>
internal sealed class RotateGizmoRenderer : IDisposable
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

    private ColoredMesh? _xIdle, _xDim, _xBright;
    private ColoredMesh? _yIdle, _yDim, _yBright;
    private ColoredMesh? _zIdle, _zDim, _zBright;

    private readonly List<Vertex> _arcVertices = new();
    private float[] _arcUpload = System.Array.Empty<float>();
    private int _arcVao;
    private int _arcVbo;

    public void Initialize()
    {
        _xIdle = Ring("RotateRingXIdle", Vector3.UnitX, XIdle);
        _xDim = Ring("RotateRingXDim", Vector3.UnitX, XDim);
        _xBright = Ring("RotateRingXBright", Vector3.UnitX, XBright);
        _yIdle = Ring("RotateRingYIdle", Vector3.UnitY, YIdle);
        _yDim = Ring("RotateRingYDim", Vector3.UnitY, YDim);
        _yBright = Ring("RotateRingYBright", Vector3.UnitY, YBright);
        _zIdle = Ring("RotateRingZIdle", Vector3.UnitZ, ZIdle);
        _zDim = Ring("RotateRingZDim", Vector3.UnitZ, ZDim);
        _zBright = Ring("RotateRingZBright", Vector3.UnitZ, ZBright);

        _arcVao = GL.GenVertexArray();
        _arcVbo = GL.GenBuffer();
        GL.BindVertexArray(_arcVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _arcVbo);
        const int stride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    private static ColoredMesh Ring(string name, Vector3 axis, Vector3 color) =>
        GizmoMeshFactory.CreateRingMesh(
            name, axis, color,
            RotateGizmoLayout.RingRadialHalfThickness,
            RotateGizmoLayout.RingAxialHalfThickness);

    public void Render(ShaderProgram shader, Part part, EditorCamera camera, RotateHandle hovered, RotateHandle active)
    {
        if (_xIdle is null)
        {
            return;
        }

        var gizmoScale = MoveGizmoScale.Compute(part.Position, camera);
        var ringScale = gizmoScale * RotateGizmoLayout.RingRadiusFactor;
        var dragging = active != RotateHandle.None;

        // The emphasised handle (the one shown bright) is the active handle while dragging, else the
        // hovered handle. Every other handle is dimmed during a drag, or shown idle when not dragging.
        var emphasized = dragging ? active : hovered;

        GL.Disable(EnableCap.DepthTest);

        var ringModel = Matrix4.Multiply(
            Matrix4.CreateTranslation(part.Position),
            Matrix4.CreateScale(new Vector3(ringScale, ringScale, ringScale)));
        shader.SetMatrix4("uModel", ringModel);

        // Pass 1: non-emphasised rings first (dim while dragging, otherwise idle).
        foreach (var ring in new[] { RotateHandle.X, RotateHandle.Y, RotateHandle.Z })
        {
            if (ring != emphasized)
            {
                RingMesh(ring, dragging ? RotateHandleState.Dim : RotateHandleState.Idle)!.Draw();
            }
        }

        // Pass 2: non-emphasised arcs (camera-facing; identity model set inside DrawArc).
        foreach (var arc in new[] { RotateHandle.Purple, RotateHandle.Yellow })
        {
            if (arc != emphasized)
            {
                DrawArc(shader, part.Position, camera, gizmoScale, arc, ArcColor(arc, dragging ? RotateHandleState.Dim : RotateHandleState.Idle));
            }
        }

        // Pass 3: the emphasised handle last, bright, so it is never overpainted.
        if (emphasized is RotateHandle.X or RotateHandle.Y or RotateHandle.Z)
        {
            shader.SetMatrix4("uModel", ringModel); // restore: DrawArc set identity
            RingMesh(emphasized, RotateHandleState.Bright)!.Draw();
        }
        else if (emphasized is RotateHandle.Purple or RotateHandle.Yellow)
        {
            DrawArc(shader, part.Position, camera, gizmoScale, emphasized, ArcColor(emphasized, RotateHandleState.Bright));
        }

        GL.Enable(EnableCap.DepthTest);
    }

    private ColoredMesh? RingMesh(RotateHandle handle, RotateHandleState state) =>
        handle switch
        {
            RotateHandle.X => state == RotateHandleState.Bright ? _xBright : state == RotateHandleState.Dim ? _xDim : _xIdle,
            RotateHandle.Y => state == RotateHandleState.Bright ? _yBright : state == RotateHandleState.Dim ? _yDim : _yIdle,
            RotateHandle.Z => state == RotateHandleState.Bright ? _zBright : state == RotateHandleState.Dim ? _zDim : _zIdle,
            _ => null
        };

    private static Vector3 ArcColor(RotateHandle handle, RotateHandleState state) =>
        handle == RotateHandle.Purple
            ? state == RotateHandleState.Bright ? PurpleBright : state == RotateHandleState.Dim ? PurpleDim : PurpleIdle
            : state == RotateHandleState.Bright ? YellowBright : state == RotateHandleState.Dim ? YellowDim : YellowIdle;

    private void DrawArc(ShaderProgram shader, Vector3 center, EditorCamera camera, float gizmoScale, RotateHandle handle, Vector3 color)
    {
        _arcVertices.Clear();

        var right = camera.Right;
        var up = camera.Up;
        var arcRadius = gizmoScale * RotateGizmoLayout.RingRadiusFactor * RotateGizmoLayout.ArcRadiusFactor;
        var halfWidth = gizmoScale * RotateGizmoLayout.ArcHalfWidthFactor;
        var centerDeg = handle == RotateHandle.Purple ? RotateGizmoLayout.ArcPurpleCenterDeg : RotateGizmoLayout.ArcYellowCenterDeg;
        var startDeg = centerDeg - RotateGizmoLayout.ArcSweepDegrees * 0.5f;

        // Curved concentric ribbon (partial annulus) in the camera plane: any point center + a*right +
        // b*up satisfies (p-center)·forward == 0, so the arc always faces the camera and never goes edge-on.
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

        var count = _arcVertices.Count * 6;
        if (_arcUpload.Length < count)
        {
            _arcUpload = new float[System.Math.Max(count, _arcUpload.Length * 2)];
        }

        for (var i = 0; i < _arcVertices.Count; i++)
        {
            var v = _arcVertices[i];
            var o = i * 6;
            _arcUpload[o + 0] = v.Position.X;
            _arcUpload[o + 1] = v.Position.Y;
            _arcUpload[o + 2] = v.Position.Z;
            _arcUpload[o + 3] = v.Color.X;
            _arcUpload[o + 4] = v.Color.Y;
            _arcUpload[o + 5] = v.Color.Z;
        }

        shader.SetMatrix4("uModel", Matrix4.Identity);
        GL.BindVertexArray(_arcVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _arcVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, count * sizeof(float), _arcUpload, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _arcVertices.Count);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        _xIdle?.Dispose(); _xDim?.Dispose(); _xBright?.Dispose();
        _yIdle?.Dispose(); _yDim?.Dispose(); _yBright?.Dispose();
        _zIdle?.Dispose(); _zDim?.Dispose(); _zBright?.Dispose();
        _xIdle = _xDim = _xBright = _yIdle = _yDim = _yBright = _zIdle = _zDim = _zBright = null;
        if (_arcVao != 0) { GL.DeleteVertexArray(_arcVao); _arcVao = 0; }
        if (_arcVbo != 0) { GL.DeleteBuffer(_arcVbo); _arcVbo = 0; }
    }
}

/// <summary>
/// Drag state machine for the Rotate gizmo. Every drag is solved from the captured start Euler plus a total
/// drag angle (no per-frame accumulation of the part rotation), so results are frame-rate independent and
/// reproducible. The solver is chosen at drag start and locked until release/Escape:
///   - readable ring (not edge-on): world ray-plane angular solver;
///   - near/at edge-on ring, and both secondary arcs: screen-space angle swept around the projected centre.
/// Picking and drag-start share one <see cref="Pick"/>, so a handle can never brighten as selectable and
/// then refuse to begin dragging.
/// </summary>
internal sealed class RotateGizmoController
{
    private const float SnapDegrees = 15f;

    private RotateHandle _activeHandle = RotateHandle.None;
    private bool _ringScreenAngleFallback; // ring handle grabbed near/at edge-on -> use the screen-sweep solver
    private Vector3 _startEuler;
    private Vector3 _center;
    private Vector3 _axis;   // ring rotation axis (ray-plane solver only)
    private Vector3 _u;      // ring plane basis
    private Vector3 _v;
    private float _lastAngleDeg;
    private float _totalAngleDeg;

    public bool IsDragging => _activeHandle != RotateHandle.None;
    public RotateHandle ActiveHandle => _activeHandle;
    public Vector3 DragStartRotation => _startEuler;

    public RotateHandle PickHover(Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight)
        => Pick(ray, part, camera, mouseX, mouseY, viewportWidth, viewportHeight);

    public bool TryBeginDrag(Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight, out RotateHandle hitHandle)
    {
        hitHandle = Pick(ray, part, camera, mouseX, mouseY, viewportWidth, viewportHeight);
        if (hitHandle == RotateHandle.None)
        {
            return false;
        }

        _activeHandle = hitHandle;
        _startEuler = part.Rotation;
        _center = part.Position;
        _totalAngleDeg = 0f;

        if (IsRing(hitHandle))
        {
            _axis = AxisFor(hitHandle);
            // Same edge-on test the picker used to gate this handle, so the locked solver matches the pick.
            _ringScreenAngleFallback = System.Math.Abs(Vector3.Dot(ray.Direction, _axis)) < RotateGizmoLayout.EdgeOnThreshold;
            if (_ringScreenAngleFallback)
            {
                _lastAngleDeg = ScreenAngle(_center, camera, mouseX, mouseY, viewportWidth, viewportHeight);
            }
            else
            {
                BuildPlaneBasis(_axis, out _u, out _v);
                _lastAngleDeg = RayPlaneIntersection.TryIntersect(ray, _center, _axis, out var hit)
                    ? AngleInPlane(hit - _center, _u, _v)
                    : 0f;
            }
        }
        else
        {
            _ringScreenAngleFallback = false;
            _lastAngleDeg = ScreenAngle(_center, camera, mouseX, mouseY, viewportWidth, viewportHeight);
        }

        return true;
    }

    public void UpdateDrag(Part part, EditorCamera camera, Ray ray, float mouseX, float mouseY, int viewportWidth, int viewportHeight, bool snap)
    {
        if (_activeHandle == RotateHandle.None)
        {
            return;
        }

        var gizmoScale = MoveGizmoScale.Compute(part.Position, camera);

        if (IsRing(_activeHandle) && !_ringScreenAngleFallback)
        {
            // Ray-plane angular solver (readable ring).
            if (System.Math.Abs(Vector3.Dot(ray.Direction, _axis)) < RotateGizmoLayout.EdgeOnThreshold * 0.6f)
            {
                return; // ring grazed too far this frame: hold the current angle
            }

            if (!RayPlaneIntersection.TryIntersect(ray, _center, _axis, out var hit))
            {
                return;
            }

            if ((hit - _center).Length < gizmoScale * RotateGizmoLayout.NearCenterFactor)
            {
                return; // near the centre the angle is unstable: ignore this frame
            }

            var current = AngleInPlane(hit - _center, _u, _v);
            _totalAngleDeg += ShortestArc(current - _lastAngleDeg);
            _lastAngleDeg = current;
        }
        else
        {
            // Screen-sweep solver (secondary arcs, and rings grabbed near/at edge-on).
            if (!TryScreenAngleAndRadius(_center, camera, mouseX, mouseY, viewportWidth, viewportHeight, out var current, out var radiusPx))
            {
                return;
            }

            if (radiusPx < RotateGizmoLayout.ScreenSweepMinRadiusPx)
            {
                // Too close to the centre for a stable angle: re-anchor without accumulating, so crossing
                // the centre injects no spurious sweep.
                _lastAngleDeg = current;
                part.Rotation = ApplyFromStart(_activeHandle, _startEuler, SnapIfNeeded(_totalAngleDeg, snap));
                return;
            }

            _totalAngleDeg += ShortestArc(current - _lastAngleDeg);
            _lastAngleDeg = current;
        }

        part.Rotation = ApplyFromStart(_activeHandle, _startEuler, SnapIfNeeded(_totalAngleDeg, snap));
    }

    public void EndDrag()
    {
        _activeHandle = RotateHandle.None;
        _ringScreenAngleFallback = false;
    }

    public void Cancel(Part part)
    {
        if (_activeHandle == RotateHandle.None)
        {
            return;
        }

        part.Rotation = _startEuler;
        _activeHandle = RotateHandle.None;
        _ringScreenAngleFallback = false;
    }

    private static float SnapIfNeeded(float totalAngleDeg, bool snap) =>
        snap ? MathF.Round(totalAngleDeg / SnapDegrees) * SnapDegrees : totalAngleDeg;

    private static Vector3 ApplyFromStart(RotateHandle handle, Vector3 start, float a) =>
        handle switch
        {
            RotateHandle.X => new Vector3(Normalize(start.X + a), start.Y, start.Z),
            RotateHandle.Y => new Vector3(start.X, Normalize(start.Y + a), start.Z),
            RotateHandle.Z => new Vector3(start.X, start.Y, Normalize(start.Z + a)),
            RotateHandle.Purple => new Vector3(Normalize(start.X + a), Normalize(start.Y + a), start.Z),
            RotateHandle.Yellow => new Vector3(Normalize(start.X + a), Normalize(start.Y - a), start.Z),
            _ => start
        };

    /// <summary>
    /// Unified screen-space "closest visible centreline" pick over all five handles (no diagonals-first
    /// priority). A ring qualifies within <see cref="RotateGizmoLayout.RingPickPixels"/> of its projected
    /// centreline; an arc within <see cref="RotateGizmoLayout.ArcPickPixels"/>. Any handle that would use the
    /// screen-sweep solver (an arc, or a ring grabbed near/at edge-on) is excluded within
    /// <see cref="RotateGizmoLayout.MinScreenStartRadiusPx"/> of the projected centre, so hover and drag-start
    /// agree: a handle only brightens if it can actually begin a drag. Ties resolve Purple, Yellow, X, Y, Z.
    /// </summary>
    private static RotateHandle Pick(Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight)
    {
        var center = part.Position;
        var gizmoScale = MoveGizmoScale.Compute(center, camera);
        var ringRadius = gizmoScale * RotateGizmoLayout.RingRadiusFactor;
        var arcRadius = ringRadius * RotateGizmoLayout.ArcRadiusFactor;

        var view = camera.ViewMatrix;
        var projection = camera.GetProjectionMatrix(viewportWidth / (float)System.Math.Max(viewportHeight, 1));

        if (!GizmoMath.TryWorldToScreen(center, view, projection, viewportWidth, viewportHeight, out var cx, out var cy))
        {
            return RotateHandle.None;
        }

        var cursorRadiusPx = MathF.Sqrt((mouseX - cx) * (mouseX - cx) + (mouseY - cy) * (mouseY - cy));
        var nearCentre = cursorRadiusPx < RotateGizmoLayout.MinScreenStartRadiusPx;

        var best = RotateHandle.None;
        var bestScore = float.MaxValue;

        // Rings: distance to the projected ring centreline. A ring grabbed near/at edge-on will use the
        // screen-sweep solver, so it is subject to the near-centre exclusion (matching drag-start).
        foreach (var handle in new[] { RotateHandle.X, RotateHandle.Y, RotateHandle.Z })
        {
            var axis = AxisFor(handle);
            var usesScreenSweep = System.Math.Abs(Vector3.Dot(ray.Direction, axis)) < RotateGizmoLayout.EdgeOnThreshold;
            if (usesScreenSweep && nearCentre)
            {
                continue;
            }

            var dist = MinDistanceToProjectedCircle(center, axis, ringRadius, view, projection, viewportWidth, viewportHeight, mouseX, mouseY);
            if (dist <= RotateGizmoLayout.RingPickPixels && dist < bestScore)
            {
                bestScore = dist;
                best = handle;
            }
        }

        // Secondary arcs (always screen-sweep): distance to the projected arc centreline.
        if (!nearCentre)
        {
            foreach (var handle in new[] { RotateHandle.Purple, RotateHandle.Yellow })
            {
                var centerDeg = handle == RotateHandle.Purple ? RotateGizmoLayout.ArcPurpleCenterDeg : RotateGizmoLayout.ArcYellowCenterDeg;
                var dist = MinDistanceToProjectedArc(center, camera, arcRadius, centerDeg, view, projection, viewportWidth, viewportHeight, mouseX, mouseY);
                if (dist <= RotateGizmoLayout.ArcPickPixels && dist < bestScore)
                {
                    bestScore = dist;
                    best = handle;
                }
            }
        }

        return best;
    }

    private static float MinDistanceToProjectedCircle(
        Vector3 center, Vector3 axis, float radius,
        Matrix4 view, Matrix4 projection, int viewportWidth, int viewportHeight, float mouseX, float mouseY)
    {
        BuildPlaneBasis(axis, out var u, out var v);
        var best = float.MaxValue;
        var hasPrev = false;
        float prevX = 0f, prevY = 0f;
        float firstX = 0f, firstY = 0f;
        var hasFirst = false;

        for (var i = 0; i < RotateGizmoLayout.RingPickSamples; i++)
        {
            var t = i / (float)RotateGizmoLayout.RingPickSamples * MathF.Tau;
            var dir = u * MathF.Cos(t) + v * MathF.Sin(t);
            var world = center + dir * radius;
            if (!GizmoMath.TryWorldToScreen(world, view, projection, viewportWidth, viewportHeight, out var sx, out var sy))
            {
                hasPrev = false;
                continue;
            }

            if (!hasFirst)
            {
                firstX = sx;
                firstY = sy;
                hasFirst = true;
            }

            if (hasPrev)
            {
                best = System.Math.Min(best, DistancePointToSegment(mouseX, mouseY, prevX, prevY, sx, sy));
            }

            prevX = sx;
            prevY = sy;
            hasPrev = true;
        }

        if (hasFirst && hasPrev)
        {
            best = System.Math.Min(best, DistancePointToSegment(mouseX, mouseY, prevX, prevY, firstX, firstY)); // close the loop
        }

        return best;
    }

    private static float MinDistanceToProjectedArc(
        Vector3 center, EditorCamera camera, float radius, float centerDeg,
        Matrix4 view, Matrix4 projection, int viewportWidth, int viewportHeight, float mouseX, float mouseY)
    {
        var right = camera.Right;
        var up = camera.Up;
        var startDeg = centerDeg - RotateGizmoLayout.ArcSweepDegrees * 0.5f;
        var best = float.MaxValue;
        var hasPrev = false;
        float prevX = 0f, prevY = 0f;

        for (var i = 0; i <= RotateGizmoLayout.ArcSegments; i++)
        {
            var deg = startDeg + RotateGizmoLayout.ArcSweepDegrees * (i / (float)RotateGizmoLayout.ArcSegments);
            var rad = deg * (MathF.PI / 180f);
            var dir = right * MathF.Cos(rad) + up * MathF.Sin(rad);
            var world = center + dir * radius;
            if (!GizmoMath.TryWorldToScreen(world, view, projection, viewportWidth, viewportHeight, out var sx, out var sy))
            {
                hasPrev = false;
                continue;
            }

            if (hasPrev)
            {
                best = System.Math.Min(best, DistancePointToSegment(mouseX, mouseY, prevX, prevY, sx, sy));
            }

            prevX = sx;
            prevY = sy;
            hasPrev = true;
        }

        return best;
    }

    private static bool TryScreenAngleAndRadius(
        Vector3 center, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight,
        out float angleDeg, out float radiusPx)
    {
        angleDeg = 0f;
        radiusPx = 0f;

        var view = camera.ViewMatrix;
        var projection = camera.GetProjectionMatrix(viewportWidth / (float)System.Math.Max(viewportHeight, 1));
        if (!GizmoMath.TryWorldToScreen(center, view, projection, viewportWidth, viewportHeight, out var cx, out var cy))
        {
            return false;
        }

        var dx = mouseX - cx;
        var dy = mouseY - cy;
        angleDeg = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        radiusPx = MathF.Sqrt(dx * dx + dy * dy);
        return true;
    }

    private static float ScreenAngle(Vector3 center, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight)
    {
        TryScreenAngleAndRadius(center, camera, mouseX, mouseY, viewportWidth, viewportHeight, out var angle, out _);
        return angle;
    }

    private static bool IsRing(RotateHandle h) => h is RotateHandle.X or RotateHandle.Y or RotateHandle.Z;

    private static Vector3 AxisFor(RotateHandle h) =>
        h switch
        {
            RotateHandle.X => Vector3.UnitX,
            RotateHandle.Y => Vector3.UnitY,
            _ => Vector3.UnitZ
        };

    private static void BuildPlaneBasis(Vector3 axis, out Vector3 u, out Vector3 v)
    {
        var a = axis.Normalized();
        var reference = System.Math.Abs(a.Y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f);
        u = Vector3.Cross(a, reference).Normalized();
        v = Vector3.Cross(a, u);
    }

    private static float AngleInPlane(Vector3 vec, Vector3 u, Vector3 v) =>
        MathF.Atan2(Vector3.Dot(vec, v), Vector3.Dot(vec, u)) * (180f / MathF.PI);

    private static float ShortestArc(float deltaDeg)
    {
        var d = deltaDeg % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return d;
    }

    private static float Normalize(float degrees)
    {
        var n = degrees % 360f;
        if (n < 0f) n += 360f;
        return n;
    }

    private static float DistancePointToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        var abx = bx - ax;
        var aby = by - ay;
        var lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-6f)
        {
            var dx0 = px - ax;
            var dy0 = py - ay;
            return MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
        }

        var t = System.Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lenSq, 0f, 1f);
        var cx = ax + abx * t;
        var cy = ay + aby * t;
        var dx = px - cx;
        var dy = py - cy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
