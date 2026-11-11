using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Editor;

internal static class MoveGizmoScale
{
    public static float Compute(Vector3 gizmoPosition, EditorCamera camera)
    {
        var distance = (gizmoPosition - camera.Position).Length;
        return global::System.Math.Clamp(distance * 0.055f, 5f, 28f);
    }
}

/// <summary>
/// Mathematical names for the active Move handle. The dual-axis shortcuts are exposed as "X+Z" / "X-Z",
/// never as "Purple" / "Yellow", in any user-facing text or diagnostic.
/// </summary>
internal static class MoveAxisLabel
{
    public static string Text(GizmoAxis axis) =>
        axis switch
        {
            GizmoAxis.X => "X",
            GizmoAxis.Y => "Y",
            GizmoAxis.Z => "Z",
            GizmoAxis.Purple => "X+Z",
            GizmoAxis.Yellow => "X-Z",
            _ => "None"
        };
}

internal sealed class MoveGizmoRenderer : IDisposable
{
    private enum HandleState { Idle, Dim, Bright }

    private static readonly Vector3 PurpleDir = new Vector3(1f, 0f, 1f).Normalized();
    private static readonly Vector3 YellowDir = new Vector3(1f, 0f, -1f).Normalized();

    // RGB base colours are unchanged from the original single-colour arrows (idle appearance identical).
    private static readonly Vector3 XBase = new(0.80f, 0.36f, 0.40f);
    private static readonly Vector3 YBase = new(0.47f, 0.73f, 0.40f);
    private static readonly Vector3 ZBase = new(0.34f, 0.53f, 0.82f);

    // Secondary diagonals: subtle when idle, vivid when bright.
    private static readonly Vector3 PurpleIdle = new(0.52f, 0.40f, 0.66f);
    private static readonly Vector3 PurpleBright = new(0.80f, 0.55f, 1.0f);
    private static readonly Vector3 YellowIdle = new(0.60f, 0.56f, 0.32f);
    private static readonly Vector3 YellowBright = new(1.0f, 0.90f, 0.35f);

    private const float DimFactor = 0.45f;
    private const float BrightFactor = 1.35f;

    private static readonly GizmoAxis[] Handles =
        { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z, GizmoAxis.Purple, GizmoAxis.Yellow };

    private ColoredMesh? _xIdle, _xDim, _xBright;
    private ColoredMesh? _yIdle, _yDim, _yBright;
    private ColoredMesh? _zIdle, _zDim, _zBright;
    private ColoredMesh? _pIdle, _pDim, _pBright;
    private ColoredMesh? _qIdle, _qDim, _qBright;

    public void Initialize()
    {
        _xIdle = Arrow("MoveGizmoX", Vector3.UnitX, XBase);
        _xDim = Arrow("MoveGizmoXDim", Vector3.UnitX, Dim(XBase));
        _xBright = Arrow("MoveGizmoXBright", Vector3.UnitX, Bright(XBase));
        _yIdle = Arrow("MoveGizmoY", Vector3.UnitY, YBase);
        _yDim = Arrow("MoveGizmoYDim", Vector3.UnitY, Dim(YBase));
        _yBright = Arrow("MoveGizmoYBright", Vector3.UnitY, Bright(YBase));
        _zIdle = Arrow("MoveGizmoZ", Vector3.UnitZ, ZBase);
        _zDim = Arrow("MoveGizmoZDim", Vector3.UnitZ, Dim(ZBase));
        _zBright = Arrow("MoveGizmoZBright", Vector3.UnitZ, Bright(ZBase));

        _pIdle = Diagonal("MoveGizmoXplusZ", PurpleDir, PurpleIdle);
        _pDim = Diagonal("MoveGizmoXplusZDim", PurpleDir, Dim(PurpleIdle));
        _pBright = Diagonal("MoveGizmoXplusZBright", PurpleDir, PurpleBright);
        _qIdle = Diagonal("MoveGizmoXminusZ", YellowDir, YellowIdle);
        _qDim = Diagonal("MoveGizmoXminusZDim", YellowDir, Dim(YellowIdle));
        _qBright = Diagonal("MoveGizmoXminusZBright", YellowDir, YellowBright);
    }

    private static ColoredMesh Arrow(string name, Vector3 axis, Vector3 color) =>
        GizmoMeshFactory.CreateArrowMesh(name, axis, color);

    private static ColoredMesh Diagonal(string name, Vector3 dir, Vector3 color) =>
        GizmoMeshFactory.CreateDiagonalArrowMesh(name, dir, color);

    private static Vector3 Dim(Vector3 c) => new(c.X * DimFactor, c.Y * DimFactor, c.Z * DimFactor);

    private static Vector3 Bright(Vector3 c) => new(
        global::System.Math.Min(1f, c.X * BrightFactor),
        global::System.Math.Min(1f, c.Y * BrightFactor),
        global::System.Math.Min(1f, c.Z * BrightFactor));

    public void Render(ShaderProgram shader, Part part, EditorCamera camera, GizmoAxis hovered, GizmoAxis active)
    {
        if (_xIdle is null)
        {
            return;
        }

        var scale = MoveGizmoScale.Compute(part.Position, camera);
        var model = Matrix4.Multiply(
            Matrix4.CreateTranslation(part.Position),
            Matrix4.CreateScale(new Vector3(scale, scale, scale)));
        shader.SetMatrix4("uModel", model);

        // Emphasis = the active handle while dragging, else the hovered handle. Every other handle is
        // dimmed during a drag, idle otherwise. The emphasised handle is drawn last so it stays on top.
        var dragging = active != GizmoAxis.None;
        var emphasized = dragging ? active : hovered;

        GL.Disable(EnableCap.DepthTest);

        foreach (var handle in Handles)
        {
            if (handle == emphasized)
            {
                continue;
            }

            MeshFor(handle, dragging ? HandleState.Dim : HandleState.Idle)?.Draw();
        }

        if (emphasized != GizmoAxis.None)
        {
            MeshFor(emphasized, HandleState.Bright)?.Draw();
        }

        GL.Enable(EnableCap.DepthTest);
    }

    private ColoredMesh? MeshFor(GizmoAxis handle, HandleState state) =>
        handle switch
        {
            GizmoAxis.X => state == HandleState.Bright ? _xBright : state == HandleState.Dim ? _xDim : _xIdle,
            GizmoAxis.Y => state == HandleState.Bright ? _yBright : state == HandleState.Dim ? _yDim : _yIdle,
            GizmoAxis.Z => state == HandleState.Bright ? _zBright : state == HandleState.Dim ? _zDim : _zIdle,
            GizmoAxis.Purple => state == HandleState.Bright ? _pBright : state == HandleState.Dim ? _pDim : _pIdle,
            GizmoAxis.Yellow => state == HandleState.Bright ? _qBright : state == HandleState.Dim ? _qDim : _qIdle,
            _ => null
        };

    public void Dispose()
    {
        _xIdle?.Dispose(); _xDim?.Dispose(); _xBright?.Dispose();
        _yIdle?.Dispose(); _yDim?.Dispose(); _yBright?.Dispose();
        _zIdle?.Dispose(); _zDim?.Dispose(); _zBright?.Dispose();
        _pIdle?.Dispose(); _pDim?.Dispose(); _pBright?.Dispose();
        _qIdle?.Dispose(); _qDim?.Dispose(); _qBright?.Dispose();
        _xIdle = _xDim = _xBright = _yIdle = _yDim = _yBright = _zIdle = _zDim = _zBright = null;
        _pIdle = _pDim = _pBright = _qIdle = _qDim = _qBright = null;
    }
}

internal sealed class MoveGizmoController
{
    // Screen-space diagonal pick radius (invisible hit region, larger than the visible arrow).
    private const float DiagonalPickPixels = 18f;
    // Near-parallel exclusion: if |dot(rayDir, diagonalAxis)| >= this, the closest-point solve is too
    // ill-conditioned to begin reliably, so the handle is withheld from BOTH hover and begin (shared
    // Pick), guaranteeing a highlighted handle always activates responsively. sin^2(theta) ~ 0.03 here,
    // ~30000x above the solver's 1e-6 failure floor.
    private const float NearParallelDot = 0.985f;

    private GizmoAxis _activeAxis = GizmoAxis.None;
    private Vector3 _startPartPosition;
    private Vector3 _axisWorld;
    private float _startGrabScalar;
    private readonly Dictionary<Part, Vector3> _groupStartPositions = new();

    public bool IsDragging => _activeAxis != GizmoAxis.None;

    public GizmoAxis ActiveAxis => _activeAxis;

    public Vector3 DragStartPosition => _startPartPosition;

    public IReadOnlyDictionary<Part, Vector3> GroupDragStartPositions => _groupStartPositions;

    public GizmoAxis PickHover(
        Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight)
        => PickMoveHandle(ray, part, camera, mouseX, mouseY, viewportWidth, viewportHeight);

    public bool TryBeginDrag(
        Ray ray,
        Part leaderPart,
        IReadOnlyList<Part> groupParts,
        EditorCamera camera,
        float mouseX,
        float mouseY,
        int viewportWidth,
        int viewportHeight,
        out GizmoAxis hitAxis)
    {
        hitAxis = PickMoveHandle(ray, leaderPart, camera, mouseX, mouseY, viewportWidth, viewportHeight);
        if (hitAxis == GizmoAxis.None)
        {
            return false;
        }

        _activeAxis = hitAxis;
        _startPartPosition = leaderPart.Position;
        _axisWorld = AxisToVector(hitAxis);
        _groupStartPositions.Clear();
        foreach (var groupPart in groupParts)
        {
            _groupStartPositions[groupPart] = groupPart.Position;
        }

        // Anchor the drag to the exact point under the cursor on the chosen axis line. Storing the start
        // scalar lets UpdateDrag solve an absolute offset each frame (drift-free). The axis is normalized
        // (including the diagonals normalize(1,0,+/-1)), so the scalar is a true world distance and the
        // closest-point skew-line solve is reused verbatim for RGB and diagonal alike.
        if (!GizmoMath.TryClosestPointOnAxisLine(ray, _startPartPosition, _axisWorld, out _, out _startGrabScalar))
        {
            _activeAxis = GizmoAxis.None;
            _groupStartPositions.Clear();
            return false;
        }

        return true;
    }

    public void UpdateDrag(
        Part leaderPart,
        IReadOnlyList<Part> groupParts,
        Workspace workspace,
        Ray ray)
    {
        if (_activeAxis == GizmoAxis.None)
        {
            return;
        }

        // Solve an absolute offset from the drag-start scalar (drift-free), same skew-line solve as begin.
        if (!GizmoMath.TryClosestPointOnAxisLine(ray, _startPartPosition, _axisWorld, out _, out var grabScalar))
        {
            return;
        }

        var grabOffset = grabScalar - _startGrabScalar;
        var candidate = _startPartPosition + _axisWorld * grabOffset;

        // RGB: unchanged per-axis face/center snapping. Diagonal: coupled-scalar snap that adjusts the
        // single drag scalar so the X/Z coupling (dX = +/- dZ) stays exact; RGB path is untouched.
        var snapped = IsDiagonal(_activeAxis)
            ? ApplyCoupledScalarSnap(leaderPart, workspace, grabOffset)
            : PartSnap.SnapMovePosition(leaderPart, candidate, workspace, _activeAxis);

        var delta = new Vector3(
            snapped.X - _startPartPosition.X,
            snapped.Y - _startPartPosition.Y,
            snapped.Z - _startPartPosition.Z);

        // Move is a precision tool: no collision/baseplate restrictions, only optional face snapping.
        foreach (var groupPart in groupParts)
        {
            if (!_groupStartPositions.TryGetValue(groupPart, out var startPosition))
            {
                continue;
            }

            groupPart.Position = new Vector3(
                startPosition.X + delta.X,
                startPosition.Y + delta.Y,
                startPosition.Z + delta.Z);
        }
    }

    public void EndDrag()
    {
        if (_activeAxis == GizmoAxis.None)
        {
            return;
        }

        _activeAxis = GizmoAxis.None;
        _groupStartPositions.Clear();
    }

    /// <summary>Escape: restore every dragged part to its captured start and record nothing.</summary>
    public void Cancel()
    {
        if (_activeAxis == GizmoAxis.None)
        {
            return;
        }

        foreach (var entry in _groupStartPositions)
        {
            entry.Key.Position = entry.Value;
        }

        _activeAxis = GizmoAxis.None;
        _groupStartPositions.Clear();
    }

    // Coupled-scalar snap for the diagonal handles: query the same face/center targets the RGB path uses
    // (via the additive PartSnap.TryGetMoveAxisSnapDelta helper) for each participating axis, convert the
    // best per-axis alignment delta into a single adjustment of the drag scalar, and apply the smallest.
    // Because both components come from axisWorld * scalar, dX = +/- dZ remains exact.
    private Vector3 ApplyCoupledScalarSnap(Part leaderPart, Workspace workspace, float grabOffset)
    {
        var candidate = _startPartPosition + _axisWorld * grabOffset;
        var bestScalarAdjust = 0f;
        var bestAbs = float.MaxValue;

        // Participating axes for the X/Z diagonals: X (index 0) and Z (index 2).
        foreach (var axisIndex in new[] { 0, 2 })
        {
            var directionComponent = axisIndex == 0 ? _axisWorld.X : _axisWorld.Z;
            if (global::System.Math.Abs(directionComponent) < 1e-4f)
            {
                continue;
            }

            if (!PartSnap.TryGetMoveAxisSnapDelta(leaderPart, candidate, workspace, axisIndex, out var axisDelta))
            {
                continue;
            }

            var scalarAdjust = axisDelta / directionComponent;
            var abs = global::System.Math.Abs(scalarAdjust);
            if (abs < bestAbs)
            {
                bestAbs = abs;
                bestScalarAdjust = scalarAdjust;
            }
        }

        return _startPartPosition + _axisWorld * (grabOffset + bestScalarAdjust);
    }

    // Unified pick shared by hover and begin. RGB keeps its existing eligibility + internal selection
    // (TryPickMoveAxis is unchanged); the final overlap decision across all visible handles uses a single
    // comparable metric: screen-pixel distance to each handle's projected shaft centreline.
    private static GizmoAxis PickMoveHandle(
        Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight)
    {
        var origin = part.Position;
        var gizmoScale = MoveGizmoScale.Compute(origin, camera);

        GizmoMath.TryPickMoveAxis(
            ray, origin, gizmoScale, camera, mouseX, mouseY, viewportWidth, viewportHeight, out var rgbAxis, out _);

        var aspect = viewportWidth / (float)global::System.Math.Max(viewportHeight, 1);
        var view = camera.ViewMatrix;
        var projection = camera.GetProjectionMatrix(aspect);

        var rgbPixels = rgbAxis != GizmoAxis.None
            ? HandleScreenDistance(origin, AxisToVector(rgbAxis), gizmoScale, false, view, projection, viewportWidth, viewportHeight, mouseX, mouseY)
            : float.MaxValue;

        var rayDir = ray.Direction.Normalized();
        var bestDiagonal = GizmoAxis.None;
        var bestDiagonalPixels = float.MaxValue;
        foreach (var handle in new[] { GizmoAxis.Purple, GizmoAxis.Yellow })
        {
            var dir = AxisToVector(handle);
            if (global::System.Math.Abs(Vector3.Dot(rayDir, dir)) >= NearParallelDot)
            {
                continue; // near-parallel: withhold from hover AND begin so a highlight always activates
            }

            var pixels = HandleScreenDistance(origin, dir, gizmoScale, true, view, projection, viewportWidth, viewportHeight, mouseX, mouseY);
            if (pixels <= DiagonalPickPixels && pixels < bestDiagonalPixels)
            {
                bestDiagonalPixels = pixels;
                bestDiagonal = handle;
            }
        }

        return bestDiagonal != GizmoAxis.None && bestDiagonalPixels < rgbPixels ? bestDiagonal : rgbAxis;
    }

    private static float HandleScreenDistance(
        Vector3 origin, Vector3 dir, float gizmoScale, bool diagonal,
        Matrix4 view, Matrix4 projection, int viewportWidth, int viewportHeight, float mouseX, float mouseY)
    {
        var startT = diagonal ? GizmoMeshFactory.DiagArrowInner : 0f;
        var endT = diagonal ? GizmoMeshFactory.DiagArrowTip : GizmoMeshFactory.UnitArrowLength;
        var headT = diagonal ? (GizmoMeshFactory.DiagArrowInner + GizmoMeshFactory.DiagArrowTip) * 0.5f : 1.14f;

        var worldStart = origin + dir * (startT * gizmoScale);
        var worldEnd = origin + dir * (endT * gizmoScale);
        var worldHead = origin + dir * (headT * gizmoScale);

        var best = float.MaxValue;
        if (GizmoMath.TryWorldToScreen(worldStart, view, projection, viewportWidth, viewportHeight, out var ax, out var ay)
            && GizmoMath.TryWorldToScreen(worldEnd, view, projection, viewportWidth, viewportHeight, out var bx, out var by))
        {
            best = DistancePointToSegment2D(mouseX, mouseY, ax, ay, bx, by);
        }

        if (GizmoMath.TryWorldToScreen(worldHead, view, projection, viewportWidth, viewportHeight, out var hx, out var hy))
        {
            var dx = mouseX - hx;
            var dy = mouseY - hy;
            best = global::System.Math.Min(best, MathF.Sqrt(dx * dx + dy * dy));
        }

        return best;
    }

    private static bool IsDiagonal(GizmoAxis axis) => axis is GizmoAxis.Purple or GizmoAxis.Yellow;

    private static Vector3 AxisToVector(GizmoAxis axis) =>
        axis switch
        {
            GizmoAxis.X => Vector3.UnitX,
            GizmoAxis.Y => Vector3.UnitY,
            GizmoAxis.Z => Vector3.UnitZ,
            GizmoAxis.Purple => new Vector3(1f, 0f, 1f).Normalized(),
            GizmoAxis.Yellow => new Vector3(1f, 0f, -1f).Normalized(),
            _ => Vector3.Zero
        };

    private static float DistancePointToSegment2D(float px, float py, float ax, float ay, float bx, float by)
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

        var t = global::System.Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lenSq, 0f, 1f);
        var cx = ax + abx * t;
        var cy = ay + aby * t;
        var dx = px - cx;
        var dy = py - cy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
