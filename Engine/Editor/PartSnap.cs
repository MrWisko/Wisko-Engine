using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

/// <summary>
/// Editor face-alignment snapping for Move and Resize. This is NOT collision: it only
/// nudges a Part onto a nearby clean alignment when it is within <see cref="Threshold"/>.
/// Drag past the snap zone and the Part moves freely.
/// </summary>
internal static class PartSnap
{
    public const float Threshold = 0.4f;
    private const float NeighborMargin = 0.5f;
    private const int MaxGridSlots = 64;
    private const float Epsilon = 1e-3f;

    /// <summary>
    /// Snaps a Move candidate position on all three axes. Each axis independently looks for the
    /// closest clean alignment (face-to-face contact, flush faces, center align, or sub-size grid
    /// slots when the Part is smaller than a neighbour). Only neighbours that are adjacent on the
    /// perpendicular axes are considered, so far-away Parts never pull the drag.
    /// </summary>
    public static Vector3 SnapMovePosition(Part part, Vector3 candidate, Workspace workspace, GizmoAxis activeAxis)
    {
        _ = activeAxis;
        var movingAabb = PartBounds.GetWorldAabb(part, candidate, part.Size);
        var result = candidate;

        for (var axis = 0; axis < 3; axis++)
        {
            if (TryFindBestCenterDelta(movingAabb, workspace, part, axis, out var delta))
            {
                result = ApplyAxisDelta(result, axis, delta);
            }
        }

        return result;
    }

    /// <summary>
    /// Read-only query of the same per-axis alignment target the RGB Move path uses (face contact, flush
    /// faces, center align, sub-size grid slots), for a candidate position on a single axis, WITHOUT
    /// applying it. The dual-axis (diagonal) Move path consumes this to snap its single drag scalar while
    /// keeping the X/Z coupling exact; the RGB <see cref="SnapMovePosition"/> path is unaffected.
    /// </summary>
    public static bool TryGetMoveAxisSnapDelta(Part part, Vector3 candidate, Workspace workspace, int axis, out float delta)
    {
        var movingAabb = PartBounds.GetWorldAabb(part, candidate, part.Size);
        return TryFindBestCenterDelta(movingAabb, workspace, part, axis, out delta);
    }

    public static float AdjustResizeWorldDelta(
        Part part,
        Vector3 startSize,
        WorldAabb startAabb,
        Vector3 anchorWorld,
        Vector3 rotation,
        ResizeHandle handle,
        float worldDelta,
        Workspace workspace)
    {
        ResizeGizmoMath.ComputeResizedState(
            startSize, anchorWorld, rotation, handle, worldDelta, out var size, out var position);
        var proposedAabb = PartBounds.GetWorldAabb(part, position, size);
        var outward = ResizeGizmoMath.GetOutwardWorldNormal(rotation, handle);

        var bestAdjust = 0f;
        var bestAbs = Threshold + 1f;

        for (var axis = 0; axis < 3; axis++)
        {
            if (!TryGetMovingFace(startAabb, proposedAabb, axis, out var movingIsMax, out var movingCoord))
            {
                continue;
            }

            if (!TryFindBestFaceCoordDelta(proposedAabb, workspace, part, axis, movingCoord, out var snapDelta))
            {
                continue;
            }

            var projection = Vector3.Dot(outward, GetUnitAxis(axis));
            if (global::System.Math.Abs(projection) < 1e-4f)
            {
                continue;
            }

            var faceSign = movingIsMax ? 1f : -1f;
            var worldDeltaAdjust = snapDelta * faceSign / projection;
            var absSnap = global::System.Math.Abs(snapDelta);
            if (absSnap < bestAbs)
            {
                bestAbs = absSnap;
                bestAdjust = worldDeltaAdjust;
            }
        }

        return worldDelta + bestAdjust;
    }

    /// <summary>
    /// Read-only, additive query for the dual-axis corner Resize path: for one moving face (identified by
    /// its outward world normal) of a proposed box, returns the adjustment to the single deltaSize scalar
    /// that would snap that face to a neighbour, measured directly along the outward normal (no sqrt(2)).
    /// Reuses the same neighbour face/center search as the RGB path; the RGB
    /// <see cref="AdjustResizeWorldDelta"/> flow is not touched.
    /// </summary>
    public static bool TryGetResizeFaceSnapDelta(
        Part part,
        Vector3 proposedSize,
        Vector3 proposedPosition,
        Vector3 outwardWorldNormal,
        Workspace workspace,
        out float deltaSizeAdjust)
    {
        deltaSizeAdjust = 0f;
        var proposedAabb = PartBounds.GetWorldAabb(part, proposedPosition, proposedSize);
        var axis = DominantAxis(outwardWorldNormal);
        var projection = GetAxisComponent(outwardWorldNormal, axis);
        if (global::System.Math.Abs(projection) < 1e-4f)
        {
            return false;
        }

        var movingIsMax = projection > 0f;
        var movingCoord = movingIsMax
            ? GetAxisComponent(proposedAabb.Max, axis)
            : GetAxisComponent(proposedAabb.Min, axis);
        if (!TryFindBestFaceCoordDelta(proposedAabb, workspace, part, axis, movingCoord, out var snapDelta))
        {
            return false;
        }

        var faceSign = movingIsMax ? 1f : -1f;
        deltaSizeAdjust = snapDelta * faceSign / projection;
        return true;
    }

    private static int DominantAxis(Vector3 v)
    {
        var ax = global::System.Math.Abs(v.X);
        var ay = global::System.Math.Abs(v.Y);
        var az = global::System.Math.Abs(v.Z);
        if (ax >= ay && ax >= az)
        {
            return 0;
        }

        return ay >= az ? 1 : 2;
    }

    /// <summary>
    /// Finds the closest clean center alignment for the moving box on a single axis, considering
    /// face contact, flush faces, center alignment, and sub-size grid slots.
    /// </summary>
    private static bool TryFindBestCenterDelta(
        WorldAabb movingAabb,
        Workspace workspace,
        Part ignorePart,
        int axis,
        out float bestDelta)
    {
        bestDelta = 0f;
        var bestAbs = Threshold + Epsilon;
        var found = false;

        var movingCenter = GetCenter(movingAabb, axis);
        var movingSize = GetSize(movingAabb, axis);
        var movingHalf = movingSize * 0.5f;

        foreach (var other in workspace.Parts)
        {
            if (ReferenceEquals(other, ignorePart))
            {
                continue;
            }

            var otherAabb = PartBounds.GetWorldAabb(other);
            if (!NeighborsOnOtherAxes(movingAabb, otherAabb, axis, NeighborMargin))
            {
                continue;
            }

            var oMin = GetAxisComponent(otherAabb.Min, axis);
            var oMax = GetAxisComponent(otherAabb.Max, axis);
            var oCenter = (oMin + oMax) * 0.5f;
            var oSize = oMax - oMin;

            ConsiderCenterTarget(oCenter, movingCenter, ref bestDelta, ref bestAbs, ref found);
            ConsiderCenterTarget(oMin + movingHalf, movingCenter, ref bestDelta, ref bestAbs, ref found);
            ConsiderCenterTarget(oMax - movingHalf, movingCenter, ref bestDelta, ref bestAbs, ref found);
            ConsiderCenterTarget(oMin - movingHalf, movingCenter, ref bestDelta, ref bestAbs, ref found);
            ConsiderCenterTarget(oMax + movingHalf, movingCenter, ref bestDelta, ref bestAbs, ref found);

            if (movingSize < oSize - Epsilon)
            {
                for (var k = 1; k <= MaxGridSlots; k++)
                {
                    var minSide = oMin + k * movingSize;
                    if (minSide + movingSize > oMax + Epsilon)
                    {
                        break;
                    }

                    ConsiderCenterTarget(minSide + movingHalf, movingCenter, ref bestDelta, ref bestAbs, ref found);
                }

                for (var k = 1; k <= MaxGridSlots; k++)
                {
                    var maxSide = oMax - k * movingSize;
                    if (maxSide - movingSize < oMin - Epsilon)
                    {
                        break;
                    }

                    ConsiderCenterTarget(maxSide - movingHalf, movingCenter, ref bestDelta, ref bestAbs, ref found);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Finds the closest neighbour face/center coordinate to align a single moving resize face to.
    /// </summary>
    private static bool TryFindBestFaceCoordDelta(
        WorldAabb proposedAabb,
        Workspace workspace,
        Part ignorePart,
        int axis,
        float movingCoord,
        out float bestDelta)
    {
        bestDelta = 0f;
        var bestAbs = Threshold + Epsilon;
        var found = false;

        foreach (var other in workspace.Parts)
        {
            if (ReferenceEquals(other, ignorePart))
            {
                continue;
            }

            var otherAabb = PartBounds.GetWorldAabb(other);
            if (!NeighborsOnOtherAxes(proposedAabb, otherAabb, axis, NeighborMargin))
            {
                continue;
            }

            var oMin = GetAxisComponent(otherAabb.Min, axis);
            var oMax = GetAxisComponent(otherAabb.Max, axis);
            var oCenter = (oMin + oMax) * 0.5f;

            ConsiderCenterTarget(oMin, movingCoord, ref bestDelta, ref bestAbs, ref found);
            ConsiderCenterTarget(oMax, movingCoord, ref bestDelta, ref bestAbs, ref found);
            ConsiderCenterTarget(oCenter, movingCoord, ref bestDelta, ref bestAbs, ref found);
        }

        return found;
    }

    private static void ConsiderCenterTarget(
        float target,
        float movingCoord,
        ref float bestDelta,
        ref float bestAbs,
        ref bool found)
    {
        var delta = target - movingCoord;
        var abs = global::System.Math.Abs(delta);
        if (abs <= Threshold && abs < bestAbs)
        {
            bestAbs = abs;
            bestDelta = delta;
            found = true;
        }
    }

    /// <summary>
    /// True when the two boxes overlap (within <paramref name="margin"/>) on both axes other than
    /// <paramref name="axis"/>, i.e. they are genuinely adjacent so a snap on this axis is relevant.
    /// </summary>
    private static bool NeighborsOnOtherAxes(WorldAabb a, WorldAabb b, int axis, float margin)
    {
        for (var other = 0; other < 3; other++)
        {
            if (other == axis)
            {
                continue;
            }

            var aMin = GetAxisComponent(a.Min, other);
            var aMax = GetAxisComponent(a.Max, other);
            var bMin = GetAxisComponent(b.Min, other);
            var bMax = GetAxisComponent(b.Max, other);

            if (aMin - margin > bMax || aMax + margin < bMin)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetMovingFace(
        WorldAabb startAabb,
        WorldAabb proposedAabb,
        int axis,
        out bool movingIsMax,
        out float movingCoord)
    {
        movingIsMax = false;
        movingCoord = 0f;

        var deltaMax = global::System.Math.Abs(GetAxisComponent(proposedAabb.Max, axis) - GetAxisComponent(startAabb.Max, axis));
        var deltaMin = global::System.Math.Abs(GetAxisComponent(proposedAabb.Min, axis) - GetAxisComponent(startAabb.Min, axis));

        if (deltaMax < 1e-4f && deltaMin < 1e-4f)
        {
            return false;
        }

        movingIsMax = deltaMax >= deltaMin;
        movingCoord = movingIsMax
            ? GetAxisComponent(proposedAabb.Max, axis)
            : GetAxisComponent(proposedAabb.Min, axis);
        return true;
    }

    private static Vector3 ApplyAxisDelta(Vector3 position, int axis, float delta) =>
        axis switch
        {
            0 => new Vector3(position.X + delta, position.Y, position.Z),
            1 => new Vector3(position.X, position.Y + delta, position.Z),
            _ => new Vector3(position.X, position.Y, position.Z + delta)
        };

    private static Vector3 GetUnitAxis(int axis) =>
        axis switch
        {
            0 => Vector3.UnitX,
            1 => Vector3.UnitY,
            _ => Vector3.UnitZ
        };

    private static float GetCenter(WorldAabb aabb, int axis) =>
        (GetAxisComponent(aabb.Min, axis) + GetAxisComponent(aabb.Max, axis)) * 0.5f;

    private static float GetSize(WorldAabb aabb, int axis) =>
        GetAxisComponent(aabb.Max, axis) - GetAxisComponent(aabb.Min, axis);

    private static float GetAxisComponent(Vector3 value, int axis) =>
        axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };
}

internal static class ResizeGizmoMath
{
    public static void ComputeResizedState(
        Vector3 startSize,
        Vector3 anchorWorld,
        Vector3 rotationDegrees,
        ResizeHandle handle,
        float worldDelta,
        out Vector3 newSize,
        out Vector3 newPosition)
    {
        newSize = startSize;
        var axisIndex = GetAxisIndex(handle);
        var axisSize = GetAxisComponent(startSize, axisIndex);
        newSize = SetAxisComponent(newSize, axisIndex, global::System.Math.Max(PartCollision.MinPartSize, axisSize + worldDelta));

        var newHalf = GetAxisComponent(newSize, axisIndex) * 0.5f;
        var centerOffsetLocal = GetCenterOffsetLocal(handle, newHalf);
        newPosition = anchorWorld + RotateOffset(rotationDegrees, centerOffsetLocal);
    }

    /// <summary>
    /// Dual-axis (corner) Resize: X and Z grow by the same <paramref name="deltaSize"/> (Y unchanged),
    /// with the diagonally opposite vertical edge (<paramref name="anchorWorld"/>) fixed in world space.
    /// deltaSize is expected pre-clamped by the caller (min-size derived from the captured start), so the
    /// equal X==Z change stays exact. Rotation-aware; drift-free (pure function of the start state + delta).
    /// </summary>
    public static void ComputeDualAxisResizedState(
        Vector3 startSize,
        Vector3 anchorWorld,
        Vector3 rotationDegrees,
        float signX,
        float signZ,
        float deltaSize,
        out Vector3 newSize,
        out Vector3 newPosition)
    {
        newSize = new Vector3(startSize.X + deltaSize, startSize.Y, startSize.Z + deltaSize);
        var hx = newSize.X * 0.5f;
        var hz = newSize.Z * 0.5f;
        newPosition = anchorWorld + RotateOffset(rotationDegrees, new Vector3(signX * hx, 0f, signZ * hz));
    }

    public static Vector3 GetOutwardWorldNormal(Vector3 rotationDegrees, ResizeHandle handle)
    {
        var localNormal = handle switch
        {
            ResizeHandle.PositiveX => Vector3.UnitX,
            ResizeHandle.NegativeX => new Vector3(-1f, 0f, 0f),
            ResizeHandle.PositiveY => Vector3.UnitY,
            ResizeHandle.NegativeY => new Vector3(0f, -1f, 0f),
            ResizeHandle.PositiveZ => Vector3.UnitZ,
            ResizeHandle.NegativeZ => new Vector3(0f, 0f, -1f),
            _ => Vector3.Zero
        };

        return RotateOffset(rotationDegrees, localNormal).Normalized();
    }

    private static Vector3 GetCenterOffsetLocal(ResizeHandle handle, float half) =>
        handle switch
        {
            ResizeHandle.PositiveX => new Vector3(half, 0f, 0f),
            ResizeHandle.NegativeX => new Vector3(-half, 0f, 0f),
            ResizeHandle.PositiveY => new Vector3(0f, half, 0f),
            ResizeHandle.NegativeY => new Vector3(0f, -half, 0f),
            ResizeHandle.PositiveZ => new Vector3(0f, 0f, half),
            ResizeHandle.NegativeZ => new Vector3(0f, 0f, -half),
            _ => Vector3.Zero
        };

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

    private static Vector3 SetAxisComponent(Vector3 value, int axisIndex, float component) =>
        axisIndex switch
        {
            0 => new Vector3(component, value.Y, value.Z),
            1 => new Vector3(value.X, component, value.Z),
            _ => new Vector3(value.X, value.Y, component)
        };
}
