using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Editor;

internal enum ResizeHandle
{
    None,
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ
}

/// <summary>
/// Dual-axis Resize corner shortcuts (kept separate from the RGB <see cref="ResizeHandle"/> so the RGB
/// enum and its render/pick loops are untouched). Purple = local +X+Z corner, Yellow = local +X-Z corner.
/// </summary>
internal enum ResizeCornerHandle
{
    None,
    Purple,
    Yellow
}

internal sealed class ResizeGizmoRenderer : IDisposable
{
    private enum HandleState { Idle, Dim, Bright }

    internal const float FacePadding = 0.55f;

    // Visible handle: a unit cube scaled per-frame by gizmoScale so its apparent screen size stays
    // stable (same family as Move). Picking is a SEPARATE concern (see GetPickRadius) so the invisible
    // hit region never shrinks with the smaller visuals.
    private const float VisibleHandleScaleFactor = 0.075f;
    private const float PickRadiusScaleFactor = 0.075f;
    private const float MinimumPickRadius = 0.72f; // current effective radius; pick never regresses below this
    private const float BracketScaleFactor = 0.09f; // corner-bracket apparent size (unit bars 1.0 long)

    private const float DimFactor = 0.45f;
    private const float BrightFactor = 1.35f;

    private static readonly Vector3 XColor = new(0.80f, 0.36f, 0.40f);
    private static readonly Vector3 YColor = new(0.47f, 0.73f, 0.40f);
    private static readonly Vector3 ZColor = new(0.34f, 0.53f, 0.82f);
    private static readonly Vector3 PurpleIdle = new(0.52f, 0.40f, 0.66f);
    private static readonly Vector3 PurpleBright = new(0.80f, 0.55f, 1.0f);
    private static readonly Vector3 YellowIdle = new(0.60f, 0.56f, 0.32f);
    private static readonly Vector3 YellowBright = new(1.0f, 0.90f, 0.35f);

    private ColoredMesh? _xIdle, _xDim, _xBright;
    private ColoredMesh? _yIdle, _yDim, _yBright;
    private ColoredMesh? _zIdle, _zDim, _zBright;
    private ColoredMesh? _purpleIdle, _purpleDim, _purpleBright;
    private ColoredMesh? _yellowIdle, _yellowDim, _yellowBright;

    public void Initialize()
    {
        _xIdle = CreateUnitCube("ResizeHandleX", XColor);
        _xDim = CreateUnitCube("ResizeHandleXDim", Dim(XColor));
        _xBright = CreateUnitCube("ResizeHandleXBright", Bright(XColor));
        _yIdle = CreateUnitCube("ResizeHandleY", YColor);
        _yDim = CreateUnitCube("ResizeHandleYDim", Dim(YColor));
        _yBright = CreateUnitCube("ResizeHandleYBright", Bright(YColor));
        _zIdle = CreateUnitCube("ResizeHandleZ", ZColor);
        _zDim = CreateUnitCube("ResizeHandleZDim", Dim(ZColor));
        _zBright = CreateUnitCube("ResizeHandleZBright", Bright(ZColor));

        _purpleIdle = GizmoMeshFactory.CreateCornerBracketMesh("ResizeCornerXplusZ", 1f, 1f, PurpleIdle);
        _purpleDim = GizmoMeshFactory.CreateCornerBracketMesh("ResizeCornerXplusZDim", 1f, 1f, Dim(PurpleIdle));
        _purpleBright = GizmoMeshFactory.CreateCornerBracketMesh("ResizeCornerXplusZBright", 1f, 1f, PurpleBright);
        _yellowIdle = GizmoMeshFactory.CreateCornerBracketMesh("ResizeCornerXminusZ", 1f, -1f, YellowIdle);
        _yellowDim = GizmoMeshFactory.CreateCornerBracketMesh("ResizeCornerXminusZDim", 1f, -1f, Dim(YellowIdle));
        _yellowBright = GizmoMeshFactory.CreateCornerBracketMesh("ResizeCornerXminusZBright", 1f, -1f, YellowBright);
    }

    private static ColoredMesh CreateUnitCube(string name, Vector3 color)
    {
        var (vertices, indices) = MeshFactory.CreateIndexedBox(1f, 1f, 1f, color);
        return ColoredMesh.FromIndexedVertices(name, vertices, indices);
    }

    private static Vector3 Dim(Vector3 c) => new(c.X * DimFactor, c.Y * DimFactor, c.Z * DimFactor);

    private static Vector3 Bright(Vector3 c) => new(
        global::System.Math.Min(1f, c.X * BrightFactor),
        global::System.Math.Min(1f, c.Y * BrightFactor),
        global::System.Math.Min(1f, c.Z * BrightFactor));

    public void Render(
        ShaderProgram shader,
        Part part,
        EditorCamera camera,
        ResizeHandle hovered,
        ResizeHandle active,
        ResizeCornerHandle cornerHovered,
        ResizeCornerHandle cornerActive,
        bool showCorners)
    {
        if (_xIdle is null || _yIdle is null || _zIdle is null)
        {
            return;
        }

        // Positions (GetLocalOffset/FacePadding) are unchanged; only the visible cube size scales.
        var gizmoScale = MoveGizmoScale.Compute(part.Position, camera);
        var visibleSize = gizmoScale * VisibleHandleScaleFactor;
        var scale = new Vector3(visibleSize, visibleSize, visibleSize);

        var dragging = active != ResizeHandle.None || cornerActive != ResizeCornerHandle.None;
        var faceEmphasis = dragging ? active : hovered;

        GL.Disable(EnableCap.DepthTest);

        // RGB face cubes: iteration is unchanged (the diagonals are a separate enum, so they never appear
        // here). Idle appearance is identical to before; during a drag inactive faces dim, active brightens.
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            if (handle == ResizeHandle.None)
            {
                continue;
            }

            var localOffset = GetLocalOffset(handle, part.Size);
            var worldOffset = RotateOffset(part.Rotation, localOffset);
            var model = Matrix4.Multiply(
                Matrix4.CreateTranslation(part.Position + worldOffset),
                Matrix4.CreateScale(scale));
            shader.SetMatrix4("uModel", model);
            var state = handle == faceEmphasis ? HandleState.Bright : dragging ? HandleState.Dim : HandleState.Idle;
            CubeFor(handle, state).Draw();
        }

        // Secondary corner brackets: single-selection only. Rotation-aware (built in the local frame).
        if (showCorners)
        {
            var cornerEmphasis = dragging ? cornerActive : cornerHovered;
            var rotation = Matrix4.CreateRotationEuler(part.Rotation);
            var bracketWorld = gizmoScale * BracketScaleFactor;
            var bracketSize = new Vector3(bracketWorld, bracketWorld, bracketWorld);
            foreach (var corner in new[] { ResizeCornerHandle.Purple, ResizeCornerHandle.Yellow })
            {
                var cornerWorld = GetCornerWorldPosition(part, corner);
                var model = Matrix4.Multiply(
                    Matrix4.CreateTranslation(cornerWorld),
                    Matrix4.Multiply(rotation, Matrix4.CreateScale(bracketSize)));
                shader.SetMatrix4("uModel", model);
                var state = corner == cornerEmphasis ? HandleState.Bright : dragging ? HandleState.Dim : HandleState.Idle;
                BracketFor(corner, state).Draw();
            }
        }

        GL.Enable(EnableCap.DepthTest);
    }

    private ColoredMesh CubeFor(ResizeHandle handle, HandleState state)
    {
        var (idle, dim, bright) = handle switch
        {
            ResizeHandle.PositiveX or ResizeHandle.NegativeX => (_xIdle!, _xDim!, _xBright!),
            ResizeHandle.PositiveY or ResizeHandle.NegativeY => (_yIdle!, _yDim!, _yBright!),
            _ => (_zIdle!, _zDim!, _zBright!)
        };
        return state == HandleState.Bright ? bright : state == HandleState.Dim ? dim : idle;
    }

    private ColoredMesh BracketFor(ResizeCornerHandle corner, HandleState state)
    {
        var (idle, dim, bright) = corner == ResizeCornerHandle.Purple
            ? (_purpleIdle!, _purpleDim!, _purpleBright!)
            : (_yellowIdle!, _yellowDim!, _yellowBright!);
        return state == HandleState.Bright ? bright : state == HandleState.Dim ? dim : idle;
    }

    public static Vector3 GetHandleWorldPosition(Part part, ResizeHandle handle)
    {
        var localOffset = GetLocalOffset(handle, part.Size);
        return part.Position + RotateOffset(part.Rotation, localOffset);
    }

    public static (float SignX, float SignZ) CornerSigns(ResizeCornerHandle corner) =>
        corner == ResizeCornerHandle.Purple ? (1f, 1f) : (1f, -1f);

    // Bracket sits at the moving corner, offset outward by FacePadding on both axes (same family as the
    // face cubes). The drag math uses the true corner; the constant pad cancels in the grab offset.
    public static Vector3 GetCornerWorldPosition(Part part, ResizeCornerHandle corner)
    {
        var (sx, sz) = CornerSigns(corner);
        var hx = part.Size.X * 0.5f + FacePadding;
        var hz = part.Size.Z * 0.5f + FacePadding;
        return part.Position + RotateOffset(part.Rotation, new Vector3(sx * hx, 0f, sz * hz));
    }

    // Picking radius is decoupled from the visible cube size: distance-scaled for comfort far away,
    // floored at the previous effective radius so click comfort can never regress.
    public static float GetPickRadius(float gizmoScale) =>
        global::System.Math.Max(MinimumPickRadius, gizmoScale * PickRadiusScaleFactor);

    public static Vector3 GetLocalOffset(ResizeHandle handle, Vector3 size)
    {
        var hx = size.X * 0.5f + FacePadding;
        var hy = size.Y * 0.5f + FacePadding;
        var hz = size.Z * 0.5f + FacePadding;

        return handle switch
        {
            ResizeHandle.PositiveX => new Vector3(hx, 0f, 0f),
            ResizeHandle.NegativeX => new Vector3(-hx, 0f, 0f),
            ResizeHandle.PositiveY => new Vector3(0f, hy, 0f),
            ResizeHandle.NegativeY => new Vector3(0f, -hy, 0f),
            ResizeHandle.PositiveZ => new Vector3(0f, 0f, hz),
            ResizeHandle.NegativeZ => new Vector3(0f, 0f, -hz),
            _ => Vector3.Zero
        };
    }

    private static Vector3 RotateOffset(Vector3 rotationDegrees, Vector3 localOffset)
    {
        var rotation = Matrix4.CreateRotationEuler(rotationDegrees);
        return new Vector3(
            rotation.M11 * localOffset.X + rotation.M12 * localOffset.Y + rotation.M13 * localOffset.Z,
            rotation.M21 * localOffset.X + rotation.M22 * localOffset.Y + rotation.M23 * localOffset.Z,
            rotation.M31 * localOffset.X + rotation.M32 * localOffset.Y + rotation.M33 * localOffset.Z);
    }

    public void Dispose()
    {
        _xIdle?.Dispose(); _xDim?.Dispose(); _xBright?.Dispose();
        _yIdle?.Dispose(); _yDim?.Dispose(); _yBright?.Dispose();
        _zIdle?.Dispose(); _zDim?.Dispose(); _zBright?.Dispose();
        _purpleIdle?.Dispose(); _purpleDim?.Dispose(); _purpleBright?.Dispose();
        _yellowIdle?.Dispose(); _yellowDim?.Dispose(); _yellowBright?.Dispose();
        _xIdle = _xDim = _xBright = _yIdle = _yDim = _yBright = _zIdle = _zDim = _zBright = null;
        _purpleIdle = _purpleDim = _purpleBright = _yellowIdle = _yellowDim = _yellowBright = null;
    }
}

internal readonly struct ResizeDragStartState
{
    public Vector3 Size { get; init; }
    public Vector3 Position { get; init; }
}

internal sealed class ResizeGizmoController
{
    private const float CornerPickPixels = 18f;
    private const float NearParallelDot = 0.985f;

    private ResizeHandle _activeHandle = ResizeHandle.None;
    private ResizeCornerHandle _activeCorner = ResizeCornerHandle.None;
    private Vector3 _startSize;
    private Vector3 _startPosition;
    private Vector3 _anchorWorld;
    private Vector3 _axisWorld;
    private Vector3 _startRotation;
    private WorldAabb _startWorldAabb;
    private Vector3 _groupCenter;
    private readonly Dictionary<Part, ResizeDragStartState> _groupStartStates = new();

    // Corner-drag captured state (drift-free: everything derived from these each frame).
    private Vector3 _cornerAnchor;      // fixed opposite vertical-edge midpoint (world)
    private Vector3 _cornerLineOrigin;  // C0: start moving-corner (world), origin for the scalar
    private Vector3 _cornerAxis;        // dW: normalized world diagonal
    private float _cornerStartScalar;   // t0
    private float _cornerSignX;
    private float _cornerSignZ;

    public bool IsDragging => _activeHandle != ResizeHandle.None || _activeCorner != ResizeCornerHandle.None;

    public ResizeHandle ActiveHandle => _activeHandle;

    public ResizeCornerHandle ActiveCorner => _activeCorner;

    public Vector3 DragStartPosition => _startPosition;

    public Vector3 DragStartSize => _startSize;

    public IReadOnlyDictionary<Part, ResizeDragStartState> GroupDragStartStates => _groupStartStates;

    public void PickHover(
        Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight,
        bool allowCorners, out ResizeHandle rgbHover, out ResizeCornerHandle cornerHover)
    {
        var (rgb, corner) = PickUnified(ray, part, camera, mouseX, mouseY, viewportWidth, viewportHeight, allowCorners);
        cornerHover = corner;
        rgbHover = corner == ResizeCornerHandle.None ? rgb : ResizeHandle.None;
    }

    public bool TryBeginDrag(
        Ray ray,
        Part leaderPart,
        IReadOnlyList<Part> groupParts,
        EditorCamera camera,
        float mouseX,
        float mouseY,
        int viewportWidth,
        int viewportHeight,
        out ResizeHandle hitHandle)
    {
        hitHandle = ResizeHandle.None;
        _activeHandle = ResizeHandle.None;
        _activeCorner = ResizeCornerHandle.None;

        var allowCorners = groupParts.Count <= 1;
        var (rgb, corner) = PickUnified(ray, leaderPart, camera, mouseX, mouseY, viewportWidth, viewportHeight, allowCorners);

        if (corner != ResizeCornerHandle.None)
        {
            return TryBeginCornerDrag(corner, ray, leaderPart, camera, groupParts);
        }

        if (rgb == ResizeHandle.None)
        {
            return false;
        }

        hitHandle = rgb;
        _activeHandle = rgb;
        _startSize = leaderPart.Size;
        _startPosition = leaderPart.Position;
        _startRotation = leaderPart.Rotation;
        _anchorWorld = GetOppositeFaceAnchor(leaderPart, rgb);
        _axisWorld = GetOutwardWorldNormal(leaderPart, rgb).Normalized();
        _startWorldAabb = PartBounds.GetWorldAabb(leaderPart);
        _groupStartStates.Clear();
        foreach (var groupPart in groupParts)
        {
            _groupStartStates[groupPart] = new ResizeDragStartState
            {
                Size = groupPart.Size,
                Position = groupPart.Position
            };
        }

        if (groupParts.Count > 1)
        {
            _groupCenter = ComputeGroupCenter(groupParts);
        }

        if (!TrySolveTargetHandleOnAxis(ray, _anchorWorld, _axisWorld, camera, out _, out _))
        {
            _activeHandle = ResizeHandle.None;
            _groupStartStates.Clear();
            return false;
        }

        Console.WriteLine($"[ResizeGizmo] Cursor drag started handle={hitHandle} axis={GetAxisLabel(hitHandle)}");

        return true;
    }

    private bool TryBeginCornerDrag(
        ResizeCornerHandle corner, Ray ray, Part leaderPart, EditorCamera camera, IReadOnlyList<Part> groupParts)
    {
        var (sx, sz) = ResizeGizmoRenderer.CornerSigns(corner);
        _startSize = leaderPart.Size;
        _startPosition = leaderPart.Position;
        _startRotation = leaderPart.Rotation;
        _cornerSignX = sx;
        _cornerSignZ = sz;

        var hx = _startSize.X * 0.5f;
        var hz = _startSize.Z * 0.5f;
        _cornerAnchor = _startPosition + RotateOffset(_startRotation, new Vector3(-sx * hx, 0f, -sz * hz));
        _cornerLineOrigin = _startPosition + RotateOffset(_startRotation, new Vector3(sx * hx, 0f, sz * hz));
        _cornerAxis = RotateOffset(_startRotation, new Vector3(sx, 0f, sz)).Normalized();

        _groupStartStates.Clear();
        foreach (var groupPart in groupParts)
        {
            _groupStartStates[groupPart] = new ResizeDragStartState
            {
                Size = groupPart.Size,
                Position = groupPart.Position
            };
        }

        if (!TrySolveTargetHandleOnAxis(ray, _cornerLineOrigin, _cornerAxis, camera, out _, out _cornerStartScalar))
        {
            _groupStartStates.Clear();
            return false;
        }

        _activeCorner = corner;
        Console.WriteLine($"[ResizeGizmo] Corner drag started corner={(corner == ResizeCornerHandle.Purple ? "X+Z" : "X-Z")}");
        return true;
    }

    public void UpdateDrag(
        Part leaderPart,
        IReadOnlyList<Part> groupParts,
        Workspace workspace,
        EditorCamera camera,
        Ray ray)
    {
        if (_activeCorner != ResizeCornerHandle.None)
        {
            UpdateCornerDrag(leaderPart, workspace, camera, ray);
            return;
        }

        if (_activeHandle == ResizeHandle.None)
        {
            return;
        }

        if (!TrySolveTargetHandleOnAxis(ray, _anchorWorld, _axisWorld, camera, out _, out var handleScalar))
        {
            return;
        }

        var minHandleScalar = PartCollision.MinPartSize + ResizeGizmoRenderer.FacePadding;
        handleScalar = global::System.Math.Max(minHandleScalar, handleScalar);

        var axisIndex = GetAxisIndex(_activeHandle);
        var startAxisSize = GetAxisComponent(_startSize, axisIndex);
        var newAxisSize = handleScalar - ResizeGizmoRenderer.FacePadding;
        var worldDelta = newAxisSize - startAxisSize;

        if (groupParts.Count > 1)
        {
            ApplyUniformGroupScale(groupParts, ComputeUniformGroupScaleFactor(axisIndex, worldDelta));
        }
        else
        {
            var snappedDelta = PartSnap.AdjustResizeWorldDelta(
                leaderPart,
                _startSize,
                _startWorldAabb,
                _anchorWorld,
                _startRotation,
                _activeHandle,
                worldDelta,
                workspace);
            ResizeGizmoMath.ComputeResizedState(
                _startSize,
                _anchorWorld,
                _startRotation,
                _activeHandle,
                snappedDelta,
                out var computedSize,
                out var computedPosition);

            leaderPart.Size = computedSize;
            leaderPart.Position = computedPosition;
        }
    }

    // Dual-axis corner drag: X and Z grow/shrink together by deltaSize = (t - t0) / sqrt(2); the opposite
    // vertical edge is anchored. Drift-free (recomputed from captured start each frame); a solver failure
    // holds the last valid state (no jump, no accumulation).
    private void UpdateCornerDrag(Part leaderPart, Workspace workspace, EditorCamera camera, Ray ray)
    {
        if (!TrySolveTargetHandleOnAxis(ray, _cornerLineOrigin, _cornerAxis, camera, out _, out var scalar))
        {
            return; // hold last valid state
        }

        var s = scalar - _cornerStartScalar;
        var deltaSize = s / MathF.Sqrt(2f);

        deltaSize += ComputeCornerSnapAdjust(leaderPart, workspace, deltaSize);

        // Min-size clamp from the captured start: the smaller starting dimension binds first (equal change).
        var minStart = global::System.Math.Min(_startSize.X, _startSize.Z);
        var minDelta = PartCollision.MinPartSize - minStart;
        if (deltaSize < minDelta)
        {
            deltaSize = minDelta;
        }

        ResizeGizmoMath.ComputeDualAxisResizedState(
            _startSize, _cornerAnchor, _startRotation, _cornerSignX, _cornerSignZ, deltaSize, out var newSize, out var newPosition);

        leaderPart.Size = newSize;
        leaderPart.Position = newPosition;
    }

    // Coupled-scalar snap: query each moving face (local +X, local +Z) via the additive PartSnap helper and
    // apply the smallest deltaSize adjustment. Both faces then move by the same corrected deltaSize, so the
    // equal X==Z change stays exact. No sqrt(2) scaling of the face adjustment.
    private float ComputeCornerSnapAdjust(Part leaderPart, Workspace workspace, float deltaSize)
    {
        ResizeGizmoMath.ComputeDualAxisResizedState(
            _startSize, _cornerAnchor, _startRotation, _cornerSignX, _cornerSignZ, deltaSize, out var proposedSize, out var proposedPosition);

        var normalX = RotateOffset(_startRotation, new Vector3(_cornerSignX, 0f, 0f)).Normalized();
        var normalZ = RotateOffset(_startRotation, new Vector3(0f, 0f, _cornerSignZ)).Normalized();

        var best = 0f;
        var bestAbs = float.MaxValue;

        if (PartSnap.TryGetResizeFaceSnapDelta(leaderPart, proposedSize, proposedPosition, normalX, workspace, out var adjustX)
            && global::System.Math.Abs(adjustX) < bestAbs)
        {
            bestAbs = global::System.Math.Abs(adjustX);
            best = adjustX;
        }

        if (PartSnap.TryGetResizeFaceSnapDelta(leaderPart, proposedSize, proposedPosition, normalZ, workspace, out var adjustZ)
            && global::System.Math.Abs(adjustZ) < bestAbs)
        {
            best = adjustZ;
        }

        return best;
    }

    private (ResizeHandle Rgb, ResizeCornerHandle Corner) PickUnified(
        Ray ray, Part part, EditorCamera camera, float mouseX, float mouseY, int viewportWidth, int viewportHeight, bool allowCorners)
    {
        var gizmoScale = MoveGizmoScale.Compute(part.Position, camera);
        var pickRadius = ResizeGizmoRenderer.GetPickRadius(gizmoScale);

        // RGB face handles: existing ray-sphere selection, unchanged.
        var rgb = ResizeHandle.None;
        var bestDistance = float.MaxValue;
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            if (handle == ResizeHandle.None)
            {
                continue;
            }

            var center = ResizeGizmoRenderer.GetHandleWorldPosition(part, handle);
            if (GizmoMath.TryPickSphere(ray, center, pickRadius, out var distance) && distance < bestDistance)
            {
                rgb = handle;
                bestDistance = distance;
            }
        }

        if (!allowCorners)
        {
            return (rgb, ResizeCornerHandle.None);
        }

        // Final overlap decision across RGB + corners by comparable screen-pixel distance to each handle.
        var aspect = viewportWidth / (float)global::System.Math.Max(viewportHeight, 1);
        var view = camera.ViewMatrix;
        var projection = camera.GetProjectionMatrix(aspect);

        var rgbPixels = rgb != ResizeHandle.None
            ? ScreenDistance(ResizeGizmoRenderer.GetHandleWorldPosition(part, rgb), view, projection, viewportWidth, viewportHeight, mouseX, mouseY)
            : float.MaxValue;

        var rayDir = ray.Direction.Normalized();
        var bestCorner = ResizeCornerHandle.None;
        var bestCornerPixels = float.MaxValue;
        foreach (var corner in new[] { ResizeCornerHandle.Purple, ResizeCornerHandle.Yellow })
        {
            var (sx, sz) = ResizeGizmoRenderer.CornerSigns(corner);
            var dW = RotateOffset(part.Rotation, new Vector3(sx, 0f, sz)).Normalized();
            if (global::System.Math.Abs(Vector3.Dot(rayDir, dW)) >= NearParallelDot)
            {
                continue; // near-parallel: withhold from hover AND begin
            }

            var pixels = ScreenDistance(ResizeGizmoRenderer.GetCornerWorldPosition(part, corner), view, projection, viewportWidth, viewportHeight, mouseX, mouseY);
            if (pixels <= CornerPickPixels && pixels < bestCornerPixels)
            {
                bestCornerPixels = pixels;
                bestCorner = corner;
            }
        }

        return bestCorner != ResizeCornerHandle.None && bestCornerPixels < rgbPixels
            ? (ResizeHandle.None, bestCorner)
            : (rgb, ResizeCornerHandle.None);
    }

    private static float ScreenDistance(
        Vector3 world, Matrix4 view, Matrix4 projection, int viewportWidth, int viewportHeight, float mouseX, float mouseY)
    {
        if (!GizmoMath.TryWorldToScreen(world, view, projection, viewportWidth, viewportHeight, out var sx, out var sy))
        {
            return float.MaxValue;
        }

        var dx = mouseX - sx;
        var dy = mouseY - sy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool TrySolveTargetHandleOnAxis(
        Ray ray,
        Vector3 anchorWorld,
        Vector3 axisWorld,
        EditorCamera camera,
        out Vector3 targetHandleWorld,
        out float handleScalar)
    {
        targetHandleWorld = Vector3.Zero;
        handleScalar = 0f;

        var axis = axisWorld.Normalized();
        var origin = ray.Origin;
        var direction = ray.Direction;
        var w = origin - anchorWorld;
        var a = Vector3.Dot(direction, direction);
        var b = Vector3.Dot(direction, axis);
        var denom = a - b * b;

        if (global::System.Math.Abs(denom) > 1e-6f)
        {
            var d = Vector3.Dot(direction, w);
            var e = Vector3.Dot(axis, w);
            handleScalar = (a * e - b * d) / denom;
            targetHandleWorld = anchorWorld + axis * handleScalar;
            return true;
        }

        if (GizmoAxisRayDrag.TryProjectRayOnAxis(ray, anchorWorld, axisWorld, camera, out handleScalar))
        {
            targetHandleWorld = anchorWorld + axis * handleScalar;
            return true;
        }

        return false;
    }

    public void EndDrag()
    {
        if (_activeHandle == ResizeHandle.None && _activeCorner == ResizeCornerHandle.None)
        {
            return;
        }

        Console.WriteLine($"[ResizeGizmo] Resize drag ended handle={_activeHandle} corner={_activeCorner}");
        _activeHandle = ResizeHandle.None;
        _activeCorner = ResizeCornerHandle.None;
        _groupStartStates.Clear();
    }

    /// <summary>Escape: restore every dragged part to its captured start (size + position), record nothing.</summary>
    public void Cancel()
    {
        if (_activeHandle == ResizeHandle.None && _activeCorner == ResizeCornerHandle.None)
        {
            return;
        }

        foreach (var entry in _groupStartStates)
        {
            entry.Key.Size = entry.Value.Size;
            entry.Key.Position = entry.Value.Position;
        }

        _activeHandle = ResizeHandle.None;
        _activeCorner = ResizeCornerHandle.None;
        _groupStartStates.Clear();
    }

    private float ComputeUniformGroupScaleFactor(int axisIndex, float axisDelta)
    {
        var startAxisSize = GetAxisComponent(_startSize, axisIndex);
        if (startAxisSize < 1e-4f)
        {
            return 1f;
        }

        var scale = (startAxisSize + axisDelta) / startAxisSize;
        var minScale = 0f;
        foreach (var start in _groupStartStates.Values)
        {
            minScale = global::System.Math.Max(minScale, GetMinimumUniformScale(start.Size));
        }

        return global::System.Math.Max(minScale, scale);
    }

    private static float GetMinimumUniformScale(Vector3 size)
    {
        var minScale = 0f;
        if (size.X > 1e-4f)
        {
            minScale = global::System.Math.Max(minScale, PartCollision.MinPartSize / size.X);
        }

        if (size.Y > 1e-4f)
        {
            minScale = global::System.Math.Max(minScale, PartCollision.MinPartSize / size.Y);
        }

        if (size.Z > 1e-4f)
        {
            minScale = global::System.Math.Max(minScale, PartCollision.MinPartSize / size.Z);
        }

        return minScale;
    }

    private void ApplyUniformGroupScale(IReadOnlyList<Part> groupParts, float scaleFactor)
    {
        foreach (var groupPart in groupParts)
        {
            if (!_groupStartStates.TryGetValue(groupPart, out var start))
            {
                continue;
            }

            var offset = start.Position - _groupCenter;
            groupPart.Position = _groupCenter + offset * scaleFactor;
            groupPart.Size = new Vector3(
                global::System.Math.Max(PartCollision.MinPartSize, start.Size.X * scaleFactor),
                global::System.Math.Max(PartCollision.MinPartSize, start.Size.Y * scaleFactor),
                global::System.Math.Max(PartCollision.MinPartSize, start.Size.Z * scaleFactor));
        }
    }

    private static Vector3 ComputeGroupCenter(IReadOnlyList<Part> groupParts)
    {
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        foreach (var part in groupParts)
        {
            var aabb = PartBounds.GetWorldAabb(part);
            min = new Vector3(
                global::System.Math.Min(min.X, aabb.Min.X),
                global::System.Math.Min(min.Y, aabb.Min.Y),
                global::System.Math.Min(min.Z, aabb.Min.Z));
            max = new Vector3(
                global::System.Math.Max(max.X, aabb.Max.X),
                global::System.Math.Max(max.Y, aabb.Max.Y),
                global::System.Math.Max(max.Z, aabb.Max.Z));
        }

        return new Vector3(
            (min.X + max.X) * 0.5f,
            (min.Y + max.Y) * 0.5f,
            (min.Z + max.Z) * 0.5f);
    }

    private static Vector3 GetOppositeFaceAnchor(Part part, ResizeHandle handle)
    {
        var half = new Vector3(part.Size.X * 0.5f, part.Size.Y * 0.5f, part.Size.Z * 0.5f);
        var localAnchor = handle switch
        {
            ResizeHandle.PositiveX => new Vector3(-half.X, 0f, 0f),
            ResizeHandle.NegativeX => new Vector3(half.X, 0f, 0f),
            ResizeHandle.PositiveY => new Vector3(0f, -half.Y, 0f),
            ResizeHandle.NegativeY => new Vector3(0f, half.Y, 0f),
            ResizeHandle.PositiveZ => new Vector3(0f, 0f, -half.Z),
            ResizeHandle.NegativeZ => new Vector3(0f, 0f, half.Z),
            _ => Vector3.Zero
        };

        return part.Position + RotateOffset(part.Rotation, localAnchor);
    }

    private static Vector3 GetOutwardWorldNormal(Part part, ResizeHandle handle) =>
        ResizeGizmoMath.GetOutwardWorldNormal(part.Rotation, handle);

    private static Vector3 RotateOffset(Vector3 rotationDegrees, Vector3 localOffset)
    {
        var rotation = Matrix4.CreateRotationEuler(rotationDegrees);
        return new Vector3(
            rotation.M11 * localOffset.X + rotation.M12 * localOffset.Y + rotation.M13 * localOffset.Z,
            rotation.M21 * localOffset.X + rotation.M22 * localOffset.Y + rotation.M23 * localOffset.Z,
            rotation.M31 * localOffset.X + rotation.M32 * localOffset.Y + rotation.M33 * localOffset.Z);
    }

    private static int GetAxisIndex(ResizeHandle handle) =>
        handle switch
        {
            ResizeHandle.PositiveX or ResizeHandle.NegativeX => 0,
            ResizeHandle.PositiveY or ResizeHandle.NegativeY => 1,
            _ => 2
        };

    private static float GetAxisComponent(Vector3 value, int axisIndex) =>
        axisIndex switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };

    private static string GetAxisLabel(ResizeHandle handle) =>
        GetAxisIndex(handle) switch
        {
            0 => "X",
            1 => "Y",
            _ => "Z"
        };
}
