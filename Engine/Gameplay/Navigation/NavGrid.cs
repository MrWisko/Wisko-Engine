using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.World;

namespace Wisko.Engine.Gameplay.Navigation;

/// <summary>How a <see cref="NavGrid"/> is baked: its area, cell size, the walking agent's size and abilities, and what does not count as an obstacle.</summary>
public sealed record NavGridSettings
{
    public Vector3 Min { get; init; }

    public Vector3 Max { get; init; }

    /// <summary>Cell width in studs (0.25 to 8).</summary>
    public float CellSize { get; init; } = 1f;

    /// <summary>How far walls are kept from the agent's middle.</summary>
    public float AgentRadius { get; init; } = 0.8f;

    /// <summary>Headroom a walkable cell needs above its surface.</summary>
    public float AgentHeight { get; init; } = 4f;

    /// <summary>Largest step up or down between neighbouring cells.</summary>
    public float MaxClimb { get; init; } = 1.1f;

    /// <summary>Steepest walkable surface, in degrees.</summary>
    public float MaxSlope { get; init; } = 46f;

    /// <summary>Which surface of a column is walked on when several have headroom: the lowest (default) or the highest.</summary>
    public bool UseHighestSurface { get; init; }

    /// <summary>Parts this says yes to are neither obstacles nor ground (e.g. scripts' IgnoreTags; null: none).</summary>
    public Func<Part, bool>? Ignore { get; init; }

    /// <summary>Unanchored Parts count too (by default only anchored Parts and the environment shape the grid).</summary>
    public bool IncludeUnanchored { get; init; }

    public const int MaxCells = 250_000;
}

/// <summary>A path the grid found: its points (start, corners, end) and whether it reaches the destination.</summary>
public sealed record NavPath(IReadOnlyList<Vector3> Points, float Length, bool Complete);

/// <summary>
/// A 2.5D walkability grid over part of the world, baked from the physics world's own queries: for each cell, rays
/// down find the surfaces in its column, and a box of the agent's height above a surface must be empty for the cell to
/// be walkable there (one surface per cell: the lowest with headroom, or the highest). Steep surfaces are not walkable;
/// neighbouring cells connect when their heights differ by at most <see cref="NavGridSettings.MaxClimb"/>. Walls are
/// kept <see cref="NavGridSettings.AgentRadius"/> away by eroding the walkable area. Areas scripts block are not
/// walkable until unblocked. Connected walkable areas are numbered, so "can A reach B" is one comparison. Paths are A*
/// over the 8 neighbours (no corner cutting) followed by string pulling along straight walkable lines.
/// Not thread-safe; the Play session's scripts use it from the main thread.
/// </summary>
public sealed class NavGrid
{
    // One stored surface per cell; NaN: no walkable surface.
    private readonly float[] _height;
    private readonly bool[] _baked;      // a walkable surface with headroom was found
    private readonly bool[] _blocked;    // scripts blocked it
    private readonly bool[] _walkable;   // baked, not blocked, not too close to an obstacle
    private readonly int[] _region;      // connected walkable area (0: none)
    private readonly float[] _distance;  // to the nearest non-walkable cell, in cells (erosion)

    // A* scratch, reused: stamps instead of clearing.
    private readonly float[] _g;
    private readonly int[] _parent;
    private readonly int[] _openStamp;
    private readonly int[] _closedStamp;
    private readonly List<(float F, int Node)> _heap = [];
    private int _stamp;

    public NavGrid(NavGridSettings settings)
    {
        Settings = settings;
        CellSize = settings.CellSize;
        Origin = new Vector3(settings.Min.X, settings.Min.Y, settings.Min.Z);
        Width = global::System.Math.Max(1, (int)MathF.Ceiling((settings.Max.X - settings.Min.X) / CellSize));
        Depth = global::System.Math.Max(1, (int)MathF.Ceiling((settings.Max.Z - settings.Min.Z) / CellSize));
        var cells = Width * Depth;
        _height = new float[cells];
        _baked = new bool[cells];
        _blocked = new bool[cells];
        _walkable = new bool[cells];
        _region = new int[cells];
        _distance = new float[cells];
        _g = new float[cells];
        _parent = new int[cells];
        _openStamp = new int[cells];
        _closedStamp = new int[cells];
        Array.Fill(_height, float.NaN);
    }

    public NavGridSettings Settings { get; }

    public float CellSize { get; }

    public Vector3 Origin { get; }

    public int Width { get; }

    public int Depth { get; }

    public int CellCount => Width * Depth;

    public int WalkableCount { get; private set; }

    public int RegionCount { get; private set; }

    /// <summary>Most cells one search may visit (bigger searches give up).</summary>
    public int MaxSearchNodes { get; set; } = 200_000;

    /// <summary>Bakes every cell from the physics world.</summary>
    public void Bake(PhysicsWorld physics) => Bake(physics, 0, 0, Width - 1, Depth - 1);

    /// <summary>Re-bakes the cells overlapping a box (a door opened, something was built) and refreshes the rest.</summary>
    public void Rebake(PhysicsWorld physics, Vector3 center, Vector3 size)
    {
        var (x0, z0) = CellOf(center - size * 0.5f);
        var (x1, z1) = CellOf(center + size * 0.5f);
        Bake(physics, global::System.Math.Max(0, x0), global::System.Math.Max(0, z0), global::System.Math.Min(Width - 1, x1), global::System.Math.Min(Depth - 1, z1));
    }

    /// <summary>Marks the cells overlapping a box as blocked (or no longer blocked) by a script.</summary>
    public void SetBlocked(Vector3 center, Vector3 size, bool blocked)
    {
        var (x0, z0) = CellOf(center - size * 0.5f);
        var (x1, z1) = CellOf(center + size * 0.5f);
        for (var z = global::System.Math.Max(0, z0); z <= global::System.Math.Min(Depth - 1, z1); z++)
        {
            for (var x = global::System.Math.Max(0, x0); x <= global::System.Math.Min(Width - 1, x1); x++)
            {
                _blocked[z * Width + x] = blocked;
            }
        }

        Refresh();
    }

    private void Bake(PhysicsWorld physics, int x0, int z0, int x1, int z1)
    {
        var settings = Settings;
        var ignored = settings.Ignore;
        bool Obstacle(Part part) => (settings.IncludeUnanchored || part.Anchored) && (ignored is null || !ignored(part));
        var filter = new PhysicsQueryFilter { IgnoreTriggers = true, AcceptPart = Obstacle };
        var minWalkableNormal = MathF.Cos(global::System.Math.Clamp(settings.MaxSlope, 0f, 89f) * MathF.PI / 180f);
        var top = settings.Max.Y;
        var span = settings.Max.Y - settings.Min.Y;
        var clearance = global::System.Math.Max(0.2f, settings.AgentHeight - 0.05f);
        var footprint = CellSize * 0.9f;
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var index = z * Width + x;
                _baked[index] = false;
                _height[index] = float.NaN;
                var center = CellCenter(x, z, 0f);
                var hits = physics.RaycastAll(new Vector3(center.X, top, center.Z), new Vector3(0f, -1f, 0f), span, filter, 16);
                if (hits.Count == 0)
                {
                    continue;
                }

                // Candidate surfaces: the ray's entry points facing up enough to walk on.
                var start = settings.UseHighestSurface ? 0 : hits.Count - 1;
                var step = settings.UseHighestSurface ? 1 : -1;
                for (var i = start; i >= 0 && i < hits.Count; i += step)
                {
                    var hit = hits[i];
                    if (hit.Normal.Y < minWalkableNormal)
                    {
                        continue;
                    }

                    var surface = hit.Point.Y;
                    // On a slope the surface rises toward one side of the cell: the headroom box starts above its
                    // highest point within the footprint, or a ramp would block its own cells.
                    var rise = 0.5f * footprint * (MathF.Abs(hit.Normal.X) + MathF.Abs(hit.Normal.Z)) / hit.Normal.Y;
                    var box = new Vector3(center.X, surface + 0.05f + rise + clearance * 0.5f, center.Z);
                    if (physics.OverlapBox(box, new Vector3(footprint, clearance, footprint), Vector3.Zero, filter, 1) is { Parts.Count: 0, Environment: false })
                    {
                        _baked[index] = true;
                        _height[index] = surface;
                        break;
                    }
                }
            }
        }

        Refresh();
    }

    // Walkable = baked, not blocked and far enough from every non-walkable cell; then the connected areas.
    private void Refresh()
    {
        var cells = CellCount;
        for (var i = 0; i < cells; i++)
        {
            _distance[i] = _baked[i] && !_blocked[i] ? float.MaxValue : 0f;
        }

        // A wall whose top is walkable (most solid blocks) or the brink of a drop has a surface in every cell, so it is
        // an edge too: the higher cell of two neighbours too far apart in height to step. Agents then keep AgentRadius
        // from the block's sides (and from the edge of its top).
        for (var z = 0; z < Depth; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = z * Width + x;
                if (_distance[i] != 0f && ((x > 0 && IsLedge(i, i - 1)) || (x < Width - 1 && IsLedge(i, i + 1))
                                           || (z > 0 && IsLedge(i, i - Width)) || (z < Depth - 1 && IsLedge(i, i + Width))))
                {
                    _distance[i] = 0f;
                }
            }
        }

        // Two-pass chamfer distance (1 straight, 1.4142 diagonal) to the nearest non-walkable cell.
        const float diagonal = 1.41421356f;
        for (var z = 0; z < Depth; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = z * Width + x;
                if (_distance[i] == 0f)
                {
                    continue;
                }

                var d = _distance[i];
                if (x > 0) d = MathF.Min(d, _distance[i - 1] + 1f);
                if (z > 0) d = MathF.Min(d, _distance[i - Width] + 1f);
                if (x > 0 && z > 0) d = MathF.Min(d, _distance[i - Width - 1] + diagonal);
                if (x < Width - 1 && z > 0) d = MathF.Min(d, _distance[i - Width + 1] + diagonal);
                _distance[i] = d;
            }
        }

        for (var z = Depth - 1; z >= 0; z--)
        {
            for (var x = Width - 1; x >= 0; x--)
            {
                var i = z * Width + x;
                if (_distance[i] == 0f)
                {
                    continue;
                }

                var d = _distance[i];
                if (x < Width - 1) d = MathF.Min(d, _distance[i + 1] + 1f);
                if (z < Depth - 1) d = MathF.Min(d, _distance[i + Width] + 1f);
                if (x < Width - 1 && z < Depth - 1) d = MathF.Min(d, _distance[i + Width + 1] + diagonal);
                if (x > 0 && z < Depth - 1) d = MathF.Min(d, _distance[i + Width - 1] + diagonal);
                _distance[i] = d;
            }
        }

        // An obstacle somewhere in a neighbouring cell may be half a cell from this cell's middle.
        var keepAway = Settings.AgentRadius / CellSize + 0.5f;
        var walkable = 0;
        for (var i = 0; i < cells; i++)
        {
            _walkable[i] = _distance[i] >= keepAway || (_distance[i] == float.MaxValue);
            if (_walkable[i])
            {
                walkable++;
            }
        }

        WalkableCount = walkable;
        LabelRegions();
    }

    private void LabelRegions()
    {
        Array.Clear(_region);
        var region = 0;
        var stack = new Stack<int>();
        for (var i = 0; i < CellCount; i++)
        {
            if (!_walkable[i] || _region[i] != 0)
            {
                continue;
            }

            region++;
            _region[i] = region;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                var cx = cell % Width;
                var cz = cell / Width;
                for (var n = 0; n < 4; n++)
                {
                    var nx = cx + (n == 0 ? 1 : n == 1 ? -1 : 0);
                    var nz = cz + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= Width || nz >= Depth)
                    {
                        continue;
                    }

                    var next = nz * Width + nx;
                    if (_walkable[next] && _region[next] == 0 && CanStep(cell, next))
                    {
                        _region[next] = region;
                        stack.Push(next);
                    }
                }
            }
        }

        RegionCount = region;
    }

    public (int X, int Z) CellOf(Vector3 point) =>
        ((int)MathF.Floor((point.X - Origin.X) / CellSize), (int)MathF.Floor((point.Z - Origin.Z) / CellSize));

    public bool Contains(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;

    public Vector3 CellCenter(int x, int z, float y) => new(Origin.X + (x + 0.5f) * CellSize, y, Origin.Z + (z + 0.5f) * CellSize);

    /// <summary>Whether the point's cell is walkable, and its surface height.</summary>
    public bool IsWalkable(Vector3 point, out float height)
    {
        var (x, z) = CellOf(point);
        height = 0f;
        if (!Contains(x, z) || !_walkable[z * Width + x])
        {
            return false;
        }

        height = _height[z * Width + x];
        return true;
    }

    /// <summary>The walkable cell nearest to <paramref name="point"/> within <paramref name="maxDistance"/> studs (horizontally), or -1.</summary>
    public int NearestWalkable(Vector3 point, float maxDistance)
    {
        var (cx, cz) = CellOf(point);
        var reach = (int)MathF.Ceiling(maxDistance / CellSize);
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var ring = 0; ring <= reach; ring++)
        {
            for (var z = cz - ring; z <= cz + ring; z++)
            {
                for (var x = cx - ring; x <= cx + ring; x++)
                {
                    if (global::System.Math.Max(global::System.Math.Abs(x - cx), global::System.Math.Abs(z - cz)) != ring || !Contains(x, z))
                    {
                        continue;
                    }

                    var index = z * Width + x;
                    if (!_walkable[index])
                    {
                        continue;
                    }

                    var center = CellCenter(x, z, _height[index]);
                    var dx = center.X - point.X;
                    var dz = center.Z - point.Z;
                    // Height counts too, a little: of two layers' worth of cells, the one at the point's own height wins.
                    var distance = dx * dx + dz * dz + 0.25f * (center.Y - point.Y) * (center.Y - point.Y);
                    if (distance < bestDistance && MathF.Sqrt(dx * dx + dz * dz) <= maxDistance + CellSize)
                    {
                        bestDistance = distance;
                        best = index;
                    }
                }
            }

            // Anything found in this ring is closer than every cell of the rings beyond (but one more ring may beat a
            // corner of this one).
            if (best >= 0 && ring > 0 && bestDistance < (ring * CellSize) * (ring * CellSize))
            {
                break;
            }
        }

        return best;
    }

    public Vector3 PointOf(int cell) => CellCenter(cell % Width, cell / Width, _height[cell]);

    public int RegionOf(int cell) => cell >= 0 && cell < CellCount ? _region[cell] : 0;

    public bool IsWalkableCell(int cell) => cell >= 0 && cell < CellCount && _walkable[cell];

    public float HeightOf(int cell) => _height[cell];

    /// <summary>
    /// A path from <paramref name="from"/> to <paramref name="to"/>. The ends snap to the nearest walkable cell within
    /// <paramref name="snapDistance"/>. Unreachable destinations give the path to the closest reachable cell when
    /// <paramref name="allowPartial"/>, else null and why.
    /// </summary>
    public NavPath? FindPath(Vector3 from, Vector3 to, bool allowPartial, float snapDistance, out string? reason)
    {
        reason = null;
        var start = NearestWalkable(from, snapDistance);
        if (start < 0)
        {
            reason = "the start is not on walkable ground (nothing walkable within " + snapDistance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " studs)";
            return null;
        }

        var goal = NearestWalkable(to, snapDistance);
        if (goal < 0)
        {
            if (!allowPartial)
            {
                reason = "the destination is not on walkable ground (nothing walkable within " + snapDistance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " studs)";
                return null;
            }

            goal = NearestWalkable(to, MathF.Max(Width, Depth) * CellSize);
            if (goal < 0)
            {
                reason = "there is no walkable ground at all";
                return null;
            }
        }

        var reachable = _region[start] == _region[goal];
        if (!reachable && !allowPartial)
        {
            reason = "the destination cannot be reached from the start (no walkable connection)";
            return null;
        }

        var cells = Search(start, goal, out var complete);
        if (cells is null)
        {
            reason = $"the search gave up after {MaxSearchNodes} cells (use a larger CellSize or a smaller area)";
            return null;
        }

        if (!complete && !allowPartial)
        {
            reason = "the destination cannot be reached from the start";
            return null;
        }

        // Exact ends: the real start and, when it is walkable here, the real destination (at the surface height). The
        // corners were pulled from cell centres; each end joins the first (last) corner it can reach in a straight
        // walkable line, else the centre of its own cell, so every segment of the path stays walkable.
        var corners = Smooth(cells);
        var exactStart = new Vector3(from.X, _height[start], from.Z);
        var points = new List<Vector3> { exactStart };
        var first = 1;
        if (corners.Count > 2 && LineWalkable(exactStart, PointOf(corners[2])))
        {
            first = 2;
        }
        else if (corners.Count < 2 || !LineWalkable(exactStart, PointOf(corners[1])))
        {
            first = 0;
        }

        for (var i = first; i < corners.Count; i++)
        {
            points.Add(PointOf(corners[i]));
        }

        if (complete && IsWalkable(to, out var goalHeight))
        {
            var exactGoal = new Vector3(to.X, goalHeight, to.Z);
            if (points.Count >= 2 && LineWalkable(points[^2], exactGoal))
            {
                points[^1] = exactGoal;
            }
            else if (LineWalkable(points[^1], exactGoal))
            {
                points.Add(exactGoal);
            }
        }

        // Repeated points (an end on its cell's centre) go.
        for (var i = points.Count - 1; i > 0; i--)
        {
            if ((points[i] - points[i - 1]).Length < 1e-4f)
            {
                points.RemoveAt(i);
            }
        }

        if (points.Count == 1)
        {
            points.Add(points[0]);
        }

        var length = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            length += (points[i] - points[i - 1]).Length;
        }

        return new NavPath(points, length, complete);
    }

    /// <summary>Whether a straight walk between two points stays on walkable cells with climbable steps.</summary>
    public bool LineWalkable(Vector3 a, Vector3 b)
    {
        var (ax, az) = CellOf(a);
        var (bx, bz) = CellOf(b);
        if (!Contains(ax, az) || !Contains(bx, bz))
        {
            return false;
        }

        // Every cell the segment touches (a supercover walk), in order.
        var x = ax;
        var z = az;
        var previous = az * Width + ax;
        if (!_walkable[previous])
        {
            return false;
        }

        var dx = (b.X - a.X) / CellSize;
        var dz = (b.Z - a.Z) / CellSize;
        var stepX = global::System.Math.Sign(dx);
        var stepZ = global::System.Math.Sign(dz);
        var fx = (a.X - Origin.X) / CellSize;
        var fz = (a.Z - Origin.Z) / CellSize;
        var tMaxX = stepX == 0 ? float.MaxValue : ((stepX > 0 ? MathF.Floor(fx) + 1f : MathF.Floor(fx)) - fx) / dx;
        var tMaxZ = stepZ == 0 ? float.MaxValue : ((stepZ > 0 ? MathF.Floor(fz) + 1f : MathF.Floor(fz)) - fz) / dz;
        var tDeltaX = stepX == 0 ? float.MaxValue : MathF.Abs(1f / dx);
        var tDeltaZ = stepZ == 0 ? float.MaxValue : MathF.Abs(1f / dz);
        var guard = global::System.Math.Abs(bx - ax) + global::System.Math.Abs(bz - az) + 2;
        while ((x != bx || z != bz) && guard-- > 0)
        {
            if (MathF.Abs(tMaxX - tMaxZ) < 1e-6f)
            {
                // Through a corner: both side cells must be walkable too (no squeezing past a corner).
                if (!Walkable(x + stepX, z, previous) || !Walkable(x, z + stepZ, previous))
                {
                    return false;
                }

                x += stepX;
                z += stepZ;
                tMaxX += tDeltaX;
                tMaxZ += tDeltaZ;
            }
            else if (tMaxX < tMaxZ)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                z += stepZ;
                tMaxZ += tDeltaZ;
            }

            if (!Contains(x, z))
            {
                return false;
            }

            var cell = z * Width + x;
            if (!_walkable[cell] || !CanStep(previous, cell))
            {
                return false;
            }

            previous = cell;
        }

        return x == bx && z == bz;
    }

    private bool Walkable(int x, int z, int from) => Contains(x, z) && _walkable[z * Width + x] && CanStep(from, z * Width + x);

    private bool CanStep(int a, int b) => MathF.Abs(_height[a] - _height[b]) <= Settings.MaxClimb;

    // Whether cell a is the top of a step to b that is too high to climb.
    private bool IsLedge(int a, int b) => _baked[b] && _height[a] - _height[b] > Settings.MaxClimb;

    // A* over the 8 neighbours; null when the node budget ran out. Unreachable goals end at the closest cell found.
    private List<int>? Search(int start, int goal, out bool complete)
    {
        complete = false;
        unchecked
        {
            _stamp++;
        }

        if (_stamp == int.MaxValue)
        {
            Array.Clear(_openStamp);
            Array.Clear(_closedStamp);
            _stamp = 1;
        }

        _heap.Clear();
        var gx = goal % Width;
        var gz = goal / Width;
        _g[start] = 0f;
        _parent[start] = -1;
        _openStamp[start] = _stamp;
        Push(Heuristic(start % Width, start / Width, gx, gz), start);
        var best = start;
        var bestH = Heuristic(start % Width, start / Width, gx, gz);
        var visited = 0;
        while (_heap.Count > 0)
        {
            var (_, node) = Pop();
            if (_closedStamp[node] == _stamp)
            {
                continue;
            }

            _closedStamp[node] = _stamp;
            if (node == goal)
            {
                complete = true;
                best = goal;
                break;
            }

            if (++visited > MaxSearchNodes)
            {
                return null;
            }

            var cx = node % Width;
            var cz = node / Width;
            var h = Heuristic(cx, cz, gx, gz);
            if (h < bestH)
            {
                bestH = h;
                best = node;
            }

            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    var nx = cx + dx;
                    var nz = cz + dz;
                    if (!Contains(nx, nz))
                    {
                        continue;
                    }

                    var next = nz * Width + nx;
                    if (!_walkable[next] || _closedStamp[next] == _stamp || !CanStep(node, next))
                    {
                        continue;
                    }

                    // Diagonals only where both straight neighbours are open: no cutting corners.
                    if (dx != 0 && dz != 0 && (!Walkable(cx + dx, cz, node) || !Walkable(cx, cz + dz, node)))
                    {
                        continue;
                    }

                    var cost = (dx != 0 && dz != 0 ? 1.41421356f : 1f) + MathF.Abs(_height[next] - _height[node]) * 0.5f;
                    var g = _g[node] + cost;
                    if (_openStamp[next] == _stamp && g >= _g[next])
                    {
                        continue;
                    }

                    _openStamp[next] = _stamp;
                    _g[next] = g;
                    _parent[next] = node;
                    Push(g + Heuristic(nx, nz, gx, gz), next);
                }
            }
        }

        var path = new List<int>();
        for (var node = best; node >= 0; node = _parent[node])
        {
            path.Add(node);
            if (path.Count > CellCount)
            {
                break;
            }
        }

        path.Reverse();
        return path;
    }

    // Octile distance in cells.
    private static float Heuristic(int x, int z, int gx, int gz)
    {
        var dx = global::System.Math.Abs(x - gx);
        var dz = global::System.Math.Abs(z - gz);
        return global::System.Math.Max(dx, dz) + 0.41421356f * global::System.Math.Min(dx, dz);
    }

    // String pulling: from each corner, the farthest cell still in a straight walkable line.
    private List<int> Smooth(List<int> cells)
    {
        var corners = new List<int> { cells[0] };
        var anchor = 0;
        while (anchor < cells.Count - 1)
        {
            var next = anchor + 1;
            for (var candidate = cells.Count - 1; candidate > anchor + 1; candidate--)
            {
                if (LineWalkable(PointOf(cells[anchor]), PointOf(cells[candidate])))
                {
                    next = candidate;
                    break;
                }
            }

            corners.Add(cells[next]);
            anchor = next;
        }

        return corners;
    }

    private void Push(float f, int node)
    {
        _heap.Add((f, node));
        var i = _heap.Count - 1;
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_heap[parent].F <= _heap[i].F)
            {
                break;
            }

            (_heap[parent], _heap[i]) = (_heap[i], _heap[parent]);
            i = parent;
        }
    }

    private (float F, int Node) Pop()
    {
        var top = _heap[0];
        var last = _heap[^1];
        _heap.RemoveAt(_heap.Count - 1);
        if (_heap.Count > 0)
        {
            _heap[0] = last;
            var i = 0;
            while (true)
            {
                var left = i * 2 + 1;
                var right = left + 1;
                var smallest = i;
                if (left < _heap.Count && _heap[left].F < _heap[smallest].F)
                {
                    smallest = left;
                }

                if (right < _heap.Count && _heap[right].F < _heap[smallest].F)
                {
                    smallest = right;
                }

                if (smallest == i)
                {
                    break;
                }

                (_heap[smallest], _heap[i]) = (_heap[i], _heap[smallest]);
                i = smallest;
            }
        }

        return top;
    }
}
