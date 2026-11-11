using Wisko.Engine.Math;

namespace Wisko.Engine.World;

/// <summary>
/// Dragging a Part's body (the Select tool, and the Rotate and Resize tools away from their handles): the pressed Part
/// follows the surface under the cursor and rests on it (its own shape, in its own rotation, against the surface's real
/// face; see <see cref="SurfaceRaycast"/>). It keeps where it was grabbed: the offset between where it was and where it
/// would rest under the cursor when the drag began is kept while the surface under the cursor faces the same way, so the
/// drag never starts with a jump (a Part grabbed off-centre stays off-centre; one in the air keeps its height above the
/// surface). With no surface under the cursor (a world without a baseplate), the grab point follows the cursor on the
/// level plane through it. A drag can carry a group (the whole selection): the pressed Part is placed and the others
/// move by the same offset. A position where something would overlap another Part is refused and the last valid one
/// kept; what is shown while dragging is what stays on release (nothing is recomputed then).
/// </summary>
public sealed class PartDragger
{
    private readonly List<Part> _group = [];
    private readonly List<Vector3> _groupStarts = [];
    private readonly HashSet<Part> _groupSet = new(ReferenceEqualityComparer.Instance);
    private Vector3 _startPosition;
    private float _lastDragMouseX;
    private float _lastDragMouseY;

    // Where the press hit the Part, relative to its position (the grab point moves with it).
    private Vector3 _grabFromPart;

    // The press's ray, the offset kept from the surface placement, and the normal of the surface it was measured on.
    private Ray _startRay;
    private bool _offsetMeasured;
    private Vector3? _surfaceOffset;
    private Vector3 _surfaceNormal;

    public bool IsDragging { get; private set; }

    /// <summary>The pressed Part: the one placed on the surface.</summary>
    public Part? DraggedPart { get; private set; }

    public Vector3 DragStartPosition => _startPosition;

    /// <summary>Every Part the drag moves (the pressed one first), and where each started.</summary>
    public IReadOnlyList<Part> Group => _group;

    public IReadOnlyList<Vector3> GroupStartPositions => _groupStarts;

    /// <param name="group">The Parts moving with <paramref name="part"/> (e.g. the selection); null or empty: it alone.</param>
    public void BeginDrag(Part part, Ray ray, Vector3 surfaceHit, float mouseX, float mouseY, IReadOnlyList<Part>? group = null)
    {
        DraggedPart = part;
        IsDragging = true;
        _startPosition = part.Position;
        _lastDragMouseX = mouseX;
        _lastDragMouseY = mouseY;
        _grabFromPart = surfaceHit - part.Position;
        _startRay = ray;
        _offsetMeasured = false;
        _surfaceOffset = null;
        _group.Clear();
        _groupStarts.Clear();
        _groupSet.Clear();
        AddToGroup(part);
        if (group is not null)
        {
            foreach (var member in group)
            {
                AddToGroup(member);
            }
        }
    }

    private void AddToGroup(Part part)
    {
        if (_groupSet.Add(part))
        {
            _group.Add(part);
            _groupStarts.Add(part.Position);
        }
    }

    public void UpdateDrag(Ray ray, Workspace workspace, float mouseX, float mouseY)
    {
        if (!IsDragging || DraggedPart is null)
        {
            return;
        }

        if (global::System.Math.Abs(mouseX - _lastDragMouseX) < 0.01f
            && global::System.Math.Abs(mouseY - _lastDragMouseY) < 0.01f)
        {
            return;
        }

        _lastDragMouseX = mouseX;
        _lastDragMouseY = mouseY;

        // Once, before anything moves: how far the Part is from where it would rest under the press (kept below).
        if (!_offsetMeasured)
        {
            _offsetMeasured = true;
            if (SurfaceRaycast.TryCast(_startRay, workspace, _groupSet, out var startHit))
            {
                _surfaceOffset = _startPosition - SurfaceRaycast.ComputePlacementPosition(DraggedPart, startHit);
                _surfaceNormal = startHit.Normal;
            }
        }

        // The dragged Parts are looked through: the surface is what lies under them.
        Vector3 target;
        if (SurfaceRaycast.TryCast(ray, workspace, _groupSet, out var surfaceHit))
        {
            target = SurfaceRaycast.ComputePlacementPosition(DraggedPart, surfaceHit);
            if (_surfaceOffset is { } offset && Vector3.Dot(surfaceHit.Normal, _surfaceNormal) > 0.999f)
            {
                target += offset;
            }
        }
        else
        {
            // Nothing under the cursor (no baseplate, no Part): the grab point follows the cursor on its own level.
            var grab = DraggedPart.Position + _grabFromPart;
            if (!RayPlaneIntersection.TryIntersect(ray, grab, Vector3.UnitY, out var onPlane)
                || Vector3.Dot(onPlane - ray.Origin, ray.Direction) <= 0f)
            {
                return;
            }

            target = DraggedPart.Position + (onPlane - grab);
        }

        // Refused (something would overlap): every Part stays where it last was valid.
        _ = PartCollision.TryOffsetGroup(_group, _groupSet, target - DraggedPart.Position, workspace);
    }

    /// <summary>Escape during a drag: every dragged Part goes back where it started and the drag ends (nothing to record).</summary>
    public bool CancelDrag()
    {
        if (!IsDragging)
        {
            return false;
        }

        for (var i = 0; i < _group.Count; i++)
        {
            _group[i].Position = _groupStarts[i];
        }

        Console.WriteLine($"[PartDrag] Cancelled: {(_group.Count == 1 ? _group[0].Name : $"{_group.Count} parts")} back where they started");
        IsDragging = false;
        DraggedPart = null;
        _group.Clear();
        _groupStarts.Clear();
        _groupSet.Clear();
        return true;
    }

    public void EndDrag()
    {
        if (IsDragging && DraggedPart is not null)
        {
            var part = DraggedPart;
            Console.WriteLine(
                $"[PartDrag] Dropped {part.Name} at ({part.Position.X:F2}, {part.Position.Y:F2}, {part.Position.Z:F2})");
        }

        IsDragging = false;
        DraggedPart = null;
        _group.Clear();
        _groupStarts.Clear();
        _groupSet.Clear();
    }
}
