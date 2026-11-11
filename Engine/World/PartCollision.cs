using Wisko.Engine.Math;

namespace Wisko.Engine.World;

public static class PartCollision
{
    public const float BaseplateTopY = 0f;
    public const float MinPartSize = 0.5f;
    private const float BaseplateEpsilon = 1e-3f;

    public static bool IntersectsAny(Part part, Workspace workspace)
    {
        var shape = PartShape.Of(part);
        foreach (var other in workspace.Parts)
        {
            if (ReferenceEquals(other, part))
            {
                continue;
            }

            if (Overlaps(shape, other))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the two Parts' real shapes overlap (<see cref="PartShape"/>: a rotated Block's tilted faces, a Wedge's
    /// slope), not merely their world boxes. Touching is not overlapping.
    /// </summary>
    public static bool Overlaps(Part a, Part b) => Overlaps(PartShape.Of(a), b);

    private static bool Overlaps(in PartShape shape, Part other)
    {
        var otherShape = PartShape.Of(other);
        return shape.Bounds.Overlaps(otherShape.Bounds) && PartShape.Overlap(shape, otherShape);
    }

    public static Vector3 ClampTargetAboveBaseplate(Part part, Vector3 target)
    {
        var saved = part.Position;
        var clamped = ClampAboveBaseplate(part, target);
        part.Position = saved;
        return clamped;
    }

    /// <summary>Lifts the position so the Part's lowest real point (its own shape, rotated) is not below the baseplate.</summary>
    public static Vector3 ClampAboveBaseplate(Part part, Vector3 position)
    {
        position = new Vector3(position.X, position.Y + BaseplateLift(part, position), position.Z);
        part.Position = position;
        return position;
    }

    /// <summary>As <see cref="ClampAboveBaseplate(Part, Vector3)"/> in this world: with its baseplate deleted, nothing is lifted.</summary>
    public static Vector3 ClampAboveBaseplate(Part part, Vector3 position, Workspace workspace)
    {
        if (workspace.HasBaseplate)
        {
            return ClampAboveBaseplate(part, position);
        }

        part.Position = position;
        return position;
    }

    /// <summary>
    /// Moves every Part of <paramref name="group"/> by the same offset (lifted as one so none ends below the baseplate),
    /// unless one of them would then overlap a Part outside the group: then nothing moves. Group members are not
    /// tested against each other (they keep their places relative to one another).
    /// </summary>
    public static bool TryOffsetGroup(IReadOnlyList<Part> group, IReadOnlySet<Part> members, Vector3 offset, Workspace workspace)
    {
        var lift = 0f;
        foreach (var part in group)
        {
            lift = workspace.HasBaseplate ? global::System.Math.Max(lift, BaseplateLift(part, part.Position + offset)) : 0f;
        }

        offset = new Vector3(offset.X, offset.Y + lift, offset.Z);
        foreach (var part in group)
        {
            var shape = PartShape.Of(part, part.Position + offset);
            foreach (var other in workspace.Parts)
            {
                if (!members.Contains(other) && Overlaps(shape, other))
                {
                    return false;
                }
            }
        }

        foreach (var part in group)
        {
            part.Position += offset;
        }

        return true;
    }

    // How far the Part at this position must rise so its lowest point is on (or above) the baseplate; 0 when it is.
    private static float BaseplateLift(Part part, Vector3 position)
    {
        var lowest = PartShape.Of(part, position).LowestY;
        return lowest < BaseplateTopY - BaseplateEpsilon ? BaseplateTopY - lowest : 0f;
    }

    public static bool TryApplyPosition(Part part, Vector3 candidate, Workspace workspace, out Vector3 resolved)
    {
        var previousPosition = part.Position;
        resolved = ClampAboveBaseplate(part, candidate, workspace);

        if (IntersectsAny(part, workspace))
        {
            part.Position = previousPosition;
            return false;
        }

        return true;
    }

    public static bool TryApplyResize(
        Part part,
        Vector3 candidateSize,
        Vector3 candidatePosition,
        Workspace workspace,
        out Vector3 resolvedSize,
        out Vector3 resolvedPosition)
    {
        var previousPosition = part.Position;
        var previousSize = part.Size;

        candidateSize = new Vector3(
            global::System.Math.Max(candidateSize.X, MinPartSize),
            global::System.Math.Max(candidateSize.Y, MinPartSize),
            global::System.Math.Max(candidateSize.Z, MinPartSize));

        part.Size = candidateSize;
        part.Position = candidatePosition;
        resolvedPosition = ClampAboveBaseplate(part, candidatePosition, workspace);
        resolvedSize = candidateSize;

        if (workspace.HasBaseplate && PartShape.Of(part).LowestY < BaseplateTopY - BaseplateEpsilon)
        {
            part.Position = previousPosition;
            part.Size = previousSize;
            resolvedSize = previousSize;
            resolvedPosition = previousPosition;
            return false;
        }

        if (IntersectsAny(part, workspace))
        {
            part.Position = previousPosition;
            part.Size = previousSize;
            resolvedSize = previousSize;
            resolvedPosition = previousPosition;
            return false;
        }

        return true;
    }
}
