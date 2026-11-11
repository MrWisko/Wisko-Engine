using Wisko.Engine.Math;

namespace Wisko.Engine.Gameplay;

/// <summary>One debug line in world space, with its colour (0..1 per channel).</summary>
public readonly record struct DebugSegment(Vector3 A, Vector3 B, Vector3 Color);

/// <summary>
/// Lines scripts draw to see what their systems do (Lua: DebugDraw; navigation paths, sensors, curves): each lasts a
/// number of seconds of real frame time (unscaled: a paused game keeps counting), 0 meaning the frame it was drawn in
/// (lines drawn before the first frame, in Start, show on that frame). Play-only: the renderer draws them over the
/// world while playing and they end with the session. At most <see cref="MaxSegments"/> at once; more are dropped (the
/// caller is told).
/// </summary>
public sealed class DebugDraw
{
    public const int MaxSegments = 20_000;

    /// <summary>Longest time one line can stay (an hour).</summary>
    public const double MaxSeconds = 3600d;

    // Segments in drawing order with the real time they stay until (inclusive).
    private readonly List<(DebugSegment Segment, double Until)> _items = [];
    private double _now;

    public int Count => _items.Count;

    /// <summary>Changes whenever the set of lines changes (the renderer rebuilds its buffer only then).</summary>
    public int Version { get; private set; }

    /// <summary>Lines refused because <see cref="MaxSegments"/> were already shown (diagnostics).</summary>
    public long Dropped { get; private set; }

    public bool AddLine(Vector3 a, Vector3 b, Vector3 color, double seconds)
    {
        if (_items.Count >= MaxSegments)
        {
            Dropped++;
            return false;
        }

        _items.Add((new DebugSegment(a, b, color), _now + global::System.Math.Clamp(seconds, 0d, MaxSeconds)));
        Version++;
        return true;
    }

    /// <summary>Three circles (one around each axis), 24 segments each.</summary>
    public bool AddSphere(Vector3 center, float radius, Vector3 color, double seconds)
    {
        const int steps = 24;
        var ok = true;
        for (var axis = 0; axis < 3; axis++)
        {
            var previous = CirclePoint(center, radius, axis, 0f);
            for (var i = 1; i <= steps; i++)
            {
                var point = CirclePoint(center, radius, axis, i * MathF.Tau / steps);
                ok &= AddLine(previous, point, color, seconds);
                previous = point;
            }
        }

        return ok;
    }

    /// <summary>A box's 12 edges; rotation in Euler degrees (X, Y, Z, as Transform.Rotation).</summary>
    public bool AddBox(Vector3 center, Vector3 size, Vector3 rotationDegrees, Vector3 color, double seconds)
    {
        var rotation = Physics.PhysicsMath.EulerDegreesToQuaternion(rotationDegrees);
        var half = size * 0.5f;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var local = new System.Numerics.Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
            var world = System.Numerics.Vector3.Transform(local, rotation);
            corners[i] = new Vector3(center.X + world.X, center.Y + world.Y, center.Z + world.Z);
        }

        var ok = true;
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    ok &= AddLine(corners[i], corners[i | bit], color, seconds);
                }
            }
        }

        return ok;
    }

    public void Clear()
    {
        if (_items.Count > 0)
        {
            _items.Clear();
            Version++;
        }
    }

    /// <summary>The start of a frame at this real time: lines whose time is over go.</summary>
    internal void Expire(double now)
    {
        _now = now;

        // Compacted in place (no allocation per frame).
        var kept = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Until >= now)
            {
                _items[kept++] = _items[i];
            }
        }

        if (kept < _items.Count)
        {
            _items.RemoveRange(kept, _items.Count - kept);
            Version++;
        }
    }

    /// <summary>The lines to draw now, in the order drawn.</summary>
    public void CopySegments(List<DebugSegment> into)
    {
        into.Clear();
        foreach (var (segment, _) in _items)
        {
            into.Add(segment);
        }
    }

    private static Vector3 CirclePoint(Vector3 center, float radius, int axis, float angle)
    {
        var (s, c) = MathF.SinCos(angle);
        return axis switch
        {
            0 => new Vector3(center.X, center.Y + c * radius, center.Z + s * radius),
            1 => new Vector3(center.X + c * radius, center.Y, center.Z + s * radius),
            _ => new Vector3(center.X + c * radius, center.Y + s * radius, center.Z)
        };
    }
}
