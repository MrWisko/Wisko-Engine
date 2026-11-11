using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Gameplay.Navigation;

public enum NavAgentStatus
{
    /// <summary>No destination.</summary>
    Idle,

    /// <summary>On its way along a path.</summary>
    Moving,

    /// <summary>At the destination (following: close enough to the target).</summary>
    Arrived,

    /// <summary>No path (see <see cref="NavAgent.FailureReason"/>), or stuck.</summary>
    Failed
}

/// <summary>How an agent's movement reaches its object.</summary>
public enum NavAgentMove
{
    /// <summary>The body's horizontal velocity is set (gravity and collisions stay physics'); for unanchored objects.</summary>
    Velocity,

    /// <summary>The object's position is moved along the path (heights from the grid); for anchored objects.</summary>
    Transform,

    /// <summary>Nothing is moved: the script reads the desired velocity and moves the object itself.</summary>
    None
}

/// <summary>What an agent asks the world while it steps (an interface, so a step allocates nothing).</summary>
public interface INavObstacleProbe
{
    /// <summary>Something unanchored right in the agent's way along a horizontal direction: a sideways direction around it, else null.</summary>
    Vector3? BlockedAhead(NavAgent agent, Vector3 direction);
}

/// <summary>What one <see cref="NavAgent.Step"/> decided.</summary>
public readonly record struct NavAgentStep(NavAgentStatus Status, Vector3 Velocity, float Remaining, bool ArrivedNow, bool FailedNow, int WaypointReached);

/// <summary>
/// A navigation agent (Lua: Navigation.CreateAgent): an object, a destination (a point, or an object to follow at a
/// distance) and the path the grid found. Each <see cref="Step"/> moves along the path: the next corner is aimed at,
/// speed eases off near the end, other agents push it aside, unanchored obstacles in the way make it swerve, and it
/// finds a new path when the followed target moved away from the path's end, at <see cref="RepathInterval"/> while
/// following, or when it has been stuck for a second (three times in a row: it fails as stuck). The caller (the scene
/// runtime) applies the result to the object. Owned by the script that made it; Play-only.
/// </summary>
public sealed class NavAgent
{
    private List<Vector3> _path = [];
    private int _waypoint;
    private Vector3 _velocity;
    private double _sinceRepath;
    private double _stuckClock;
    private Vector3 _stuckFrom;
    private int _stuckRepaths;
    private Vector3 _pathGoal;
    private bool _haveDestination;

    internal NavAgent(int id, Part part)
    {
        Id = id;
        Part = part;
    }

    public int Id { get; }

    public Part Part { get; }

    public float Speed { get; set; } = 8f;

    public float Acceleration { get; set; } = 40f;

    public float StoppingDistance { get; set; } = 0.5f;

    public float Radius { get; set; } = 1f;

    public double RepathInterval { get; set; } = 0.5d;

    public NavAgentMove Move { get; set; } = NavAgentMove.Velocity;

    public bool FaceMovement { get; set; } = true;

    /// <summary>Degrees added to the facing yaw: a model whose front is not Wisko's −Z (glTF models face +Z: 180).</summary>
    public float FaceYawOffset { get; set; }

    public bool Avoidance { get; set; } = true;

    /// <summary>Transform movement: how far above the walked surface the object's position stays.</summary>
    public float HeightOffset { get; set; }

    public NavAgentStatus Status { get; private set; } = NavAgentStatus.Idle;

    public string? FailureReason { get; private set; }

    public Vector3? Destination => _haveDestination ? _pathGoal : null;

    /// <summary>The object it follows (null: a fixed destination or none).</summary>
    public Part? Target { get; private set; }

    public float FollowDistance { get; private set; }

    public IReadOnlyList<Vector3> Path => _path;

    public int Waypoint => _waypoint;

    public Vector3 Velocity => _velocity;

    public float Remaining { get; private set; }

    public bool Destroyed { get; internal set; }

    /// <summary>A new fixed destination; the path is found now. False and why when there is none (the agent then fails).</summary>
    public bool SetDestination(NavGrid grid, Vector3 from, Vector3 destination, out string? reason)
    {
        Target = null;
        _haveDestination = true;
        _pathGoal = destination;
        return Repath(grid, from, destination, out reason);
    }

    /// <summary>Follows an object, staying about <paramref name="distance"/> studs from it.</summary>
    public bool Follow(NavGrid grid, Vector3 from, Part target, Vector3 targetPosition, float distance, out string? reason)
    {
        Target = target;
        FollowDistance = distance;
        _haveDestination = true;
        _pathGoal = targetPosition;
        if (Horizontal(targetPosition - from) <= distance)
        {
            _path = [];
            Status = NavAgentStatus.Arrived;
            reason = null;
            return true;
        }

        return Repath(grid, from, targetPosition, out reason);
    }

    public void Stop()
    {
        Target = null;
        _haveDestination = false;
        _path = [];
        _waypoint = 0;
        _velocity = Vector3.Zero;
        Remaining = 0f;
        Status = NavAgentStatus.Idle;
        FailureReason = null;
    }

    /// <summary>
    /// One frame: <paramref name="position"/> is where the object is (its feet), <paramref name="targetPosition"/> where
    /// the followed object is (when following), <paramref name="neighbours"/> the other agents' positions and radii, and
    /// <paramref name="blockedAhead"/> says whether something unanchored is in the way at a horizontal direction.
    /// </summary>
    public NavAgentStep Step(NavGrid grid, Vector3 position, Vector3? targetPosition, double seconds, IReadOnlyList<(Vector3 Position, float Radius)> neighbours,
        INavObstacleProbe obstacles)
    {
        var dt = (float)global::System.Math.Max(0d, seconds);
        var arrivedNow = false;
        var failedNow = false;
        var waypointReached = 0;
        _sinceRepath += seconds;

        if (Status == NavAgentStatus.Failed && Target is null)
        {
            return Settle(dt, false, false, 0);
        }

        if (Target is not null)
        {
            if (targetPosition is not { } goal)
            {
                // The followed object is gone.
                Stop();
                Fail("the followed object no longer exists");
                return Settle(dt, false, true, 0);
            }

            _pathGoal = goal;
            var near = Horizontal(goal - position) <= FollowDistance;
            if (near)
            {
                if (Status != NavAgentStatus.Arrived)
                {
                    arrivedNow = true;
                }

                Status = NavAgentStatus.Arrived;
                _path = [];
                Remaining = Horizontal(goal - position);
                return Settle(dt, arrivedNow, false, 0, neighbours, position);
            }

            // A failed search is retried at the repath interval; a moving target as soon as it left the path's end.
            var due = _sinceRepath >= RepathInterval;
            var moved = _path.Count == 0 || Horizontal(goal - _path[^1]) > global::System.Math.Max(1f, FollowDistance * 0.5f);
            if (Status == NavAgentStatus.Failed ? due : Status != NavAgentStatus.Moving || moved && _sinceRepath >= global::System.Math.Min(RepathInterval, 0.1d) || due)
            {
                var wasFailed = Status == NavAgentStatus.Failed;
                if (!Repath(grid, position, goal, out _))
                {
                    return Settle(dt, false, !wasFailed, 0);
                }
            }
        }

        if (Status != NavAgentStatus.Moving)
        {
            return Settle(dt, false, false, 0, neighbours, position);
        }

        // Corners reached (horizontally) are passed.
        var reach = global::System.Math.Max(grid.CellSize * 0.6f, 0.4f);
        while (_waypoint < _path.Count - 1 && Horizontal(_path[_waypoint] - position) <= reach)
        {
            _waypoint++;
            waypointReached = _waypoint;
        }

        var end = _path[^1];
        var toEnd = Horizontal(end - position);
        var stopAt = Target is null ? StoppingDistance : global::System.Math.Max(StoppingDistance, FollowDistance);
        if (_waypoint >= _path.Count - 1 && toEnd <= stopAt)
        {
            Status = NavAgentStatus.Arrived;
            Remaining = toEnd;
            return Settle(dt, true, false, waypointReached);
        }

        Remaining = RemainingFrom(position);
        var next = _path[_waypoint];
        var direction = Flat(next - position);
        var speed = Speed;
        if (Remaining < Speed * 0.35f + stopAt)
        {
            // Ease in to the end (never below a crawl, so it does arrive).
            speed = global::System.Math.Max(Speed * 0.25f, Speed * (Remaining - stopAt) / (Speed * 0.35f));
        }

        var desired = direction * speed;
        if (Avoidance)
        {
            desired += Separation(position, neighbours) * Speed;
            if (direction.Length > 0f && obstacles.BlockedAhead(this, direction) is { } side)
            {
                desired += side * (Speed * 0.8f);
            }

            var length = desired.Length;
            if (length > Speed)
            {
                desired *= Speed / length;
            }
        }

        _velocity = Approach(_velocity, desired, Acceleration * dt);

        // Stuck: hardly any progress for a second while it should be moving.
        _stuckClock += seconds;
        if (_stuckClock >= 1d)
        {
            var progress = Horizontal(position - _stuckFrom);
            if (progress < global::System.Math.Max(0.3f, Speed * 0.1f))
            {
                _stuckRepaths++;
                if (_stuckRepaths >= 3)
                {
                    Fail("stuck: no progress along the path for 3 seconds");
                    return Settle(dt, false, true, waypointReached);
                }

                Repath(grid, position, Target is null ? _pathGoal : _pathGoal, out _);
            }
            else
            {
                _stuckRepaths = 0;
            }

            _stuckClock = 0d;
            _stuckFrom = position;
        }

        return new NavAgentStep(Status, _velocity, Remaining, arrivedNow, failedNow, waypointReached);
    }

    /// <summary>Height of the path under a horizontal position (for Transform movement): between the corners around it.</summary>
    public float PathHeightAt(Vector3 position)
    {
        if (_path.Count == 0)
        {
            return position.Y;
        }

        var a = _path[global::System.Math.Max(0, _waypoint - 1)];
        var b = _path[global::System.Math.Min(_waypoint, _path.Count - 1)];
        var ab = Horizontal(b - a);
        if (ab < 1e-4f)
        {
            return b.Y;
        }

        var t = global::System.Math.Clamp(1f - Horizontal(b - position) / ab, 0f, 1f);
        return a.Y + (b.Y - a.Y) * t;
    }

    private bool Repath(NavGrid grid, Vector3 from, Vector3 to, out string? reason)
    {
        _sinceRepath = 0d;
        var path = grid.FindPath(from, to, allowPartial: Target is not null, snapDistance: global::System.Math.Max(3f, grid.CellSize * 3f), out reason);
        if (path is null)
        {
            _path = [];
            Fail(reason ?? "no path");
            return false;
        }

        _path = [.. path.Points];
        _waypoint = _path.Count > 1 ? 1 : 0;
        Status = NavAgentStatus.Moving;
        FailureReason = null;
        _stuckClock = 0d;
        _stuckFrom = from;
        Remaining = path.Length;
        return true;
    }

    private void Fail(string reason)
    {
        Status = NavAgentStatus.Failed;
        FailureReason = reason;
        _path = [];
        _waypoint = 0;
    }

    // Slows to a stop (still pushed aside by neighbours).
    private NavAgentStep Settle(float dt, bool arrivedNow, bool failedNow, int waypointReached,
        IReadOnlyList<(Vector3 Position, float Radius)>? neighbours = null, Vector3 position = default)
    {
        var desired = Vector3.Zero;
        if (Avoidance && neighbours is not null)
        {
            desired = Separation(position, neighbours) * (Speed * 0.5f);
        }

        _velocity = Approach(_velocity, desired, Acceleration * dt);
        return new NavAgentStep(Status, _velocity, Remaining, arrivedNow, failedNow, waypointReached);
    }

    private float RemainingFrom(Vector3 position)
    {
        var total = Horizontal(_path[_waypoint] - position);
        for (var i = _waypoint + 1; i < _path.Count; i++)
        {
            total += Horizontal(_path[i] - _path[i - 1]);
        }

        return total;
    }

    // Away from agents closer than both radii (plus some room), stronger the closer they are.
    private Vector3 Separation(Vector3 position, IReadOnlyList<(Vector3 Position, float Radius)> neighbours)
    {
        var push = Vector3.Zero;
        foreach (var (other, radius) in neighbours)
        {
            var away = new Vector3(position.X - other.X, 0f, position.Z - other.Z);
            var distance = away.Length;
            var range = (Radius + radius) * 1.25f;
            if (distance >= range)
            {
                continue;
            }

            if (distance < 1e-4f)
            {
                // Exactly on top of each other: split by id order.
                away = new Vector3(1f, 0f, 0f);
                distance = 1e-4f;
            }

            push += away / distance * (1f - distance / range);
        }

        return push;
    }

    private static Vector3 Approach(Vector3 current, Vector3 target, float maxDelta)
    {
        var delta = target - current;
        var length = delta.Length;
        return length <= maxDelta || length < 1e-6f ? target : current + delta * (maxDelta / length);
    }

    private static float Horizontal(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    private static Vector3 Flat(Vector3 v)
    {
        var length = Horizontal(v);
        return length < 1e-5f ? Vector3.Zero : new Vector3(v.X / length, 0f, v.Z / length);
    }
}
