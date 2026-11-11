using Wisko.Engine.Animation;
using Wisko.Engine.Gameplay;
using Wisko.Engine.Gameplay.Navigation;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.World;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Scripting;

// Gameplay systems, the scene side: the session's gameplay services each frame, navigation (grid and agents),
// teleporting, bones (poses for attachments), and trigger connections.
public sealed partial class SceneScriptRuntime : IScriptGameplayScene, INavObstacleProbe
{
    /// <summary>Most navigation agents at once.</summary>
    public const int MaxAgents = 2000;

    private readonly Dictionary<int, NavAgent> _agents = [];
    private readonly List<(Vector3 Position, float Radius)> _neighbours = [];
    private int _nextAgent;
    private NavGrid? _navGrid;
    private double _frameSeconds;
    private double _frameRealSeconds;

    /// <summary>The session's gameplay services (the Play session's, or the runtime's own when it was given none).</summary>
    public GameplaySession Gameplay => _gameplay;

    /// <summary>The navigation grid, once baked (diagnostics).</summary>
    public NavGrid? NavigationGrid => _navGrid;

    /// <summary>Navigation agents alive (diagnostics).</summary>
    public int AgentCount => _agents.Count;

    /// <summary>
    /// <see cref="Update(double, ScriptInputState?)"/> when the simulation runs at a time scale: <paramref name="deltaTime"/>
    /// is the scaled frame time, <paramref name="unscaledDeltaTime"/> the real one.
    /// </summary>
    public void Update(double deltaTime, double unscaledDeltaTime, ScriptInputState? input) => UpdateFrame(deltaTime, unscaledDeltaTime, input);

    // The start of a frame for the gameplay services (before any script runs).
    private void BeginGameplayFrame(double seconds, double realSeconds)
    {
        _frameSeconds = seconds;
        _frameRealSeconds = realSeconds;
        _gameplay.BeginFrame(seconds, realSeconds, _camera);
    }

    // The end of a frame (after LateUpdate): particles and the shown camera.
    private void EndGameplayFrame()
    {
        var gravity = _physics is { IsDisposed: false } physics ? physics.Gravity : PhysicsSettings.Default.Gravity;
        _gameplay.EndFrame(_frameSeconds, _frameRealSeconds, _camera, gravity, PoseForCamera);
    }

    private (Vector3 Position, NQuaternion Rotation)? PoseForCamera(Part part) =>
        _sceneObjects.TryGetValue(part, out var sceneObject) && IsPresent(part) ? PoseOf(sceneObject) : null;

    // ---- Navigation -------------------------------------------------------------------------------------------------

    string? IScriptGameplayScene.BuildNavigation(ScriptNavBuild request, out NavGrid? grid, out double milliseconds)
    {
        grid = null;
        milliseconds = 0d;
        if (_physics is not { IsDisposed: false } physics)
        {
            return "navigation needs physics (it is only available while the game plays)";
        }

        ReportTransformWrites();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var settings = SettingsFor(request);
        grid = new NavGrid(settings);
        grid.Bake(physics);
        _navGrid = grid;
        milliseconds = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[Navigation] Baked {grid.Width} x {grid.Depth} cells of {grid.CellSize:0.##} studs: {grid.WalkableCount} walkable, {grid.RegionCount} areas, {milliseconds:0.0} ms");
        return null;
    }

    NavGrid? IScriptGameplayScene.Navigation(out string? error)
    {
        error = null;
        if (_navGrid is not null)
        {
            return _navGrid;
        }

        var defaults = new ScriptNavBuild(null, null, 1f, 0.8f, 4f, 1.1f, 46f, false, false, []);
        error = ((IScriptGameplayScene)this).BuildNavigation(defaults, out var grid, out _);
        return grid;
    }

    string? IScriptGameplayScene.RebakeNavigation(Vector3 center, Vector3 size)
    {
        if (((IScriptGameplayScene)this).Navigation(out var error) is not { } grid)
        {
            return error;
        }

        ReportTransformWrites();
        grid.Rebake(_physics!, center, size);
        return null;
    }

    // The grid's settings: the area asked for, or one around every anchored Part (and the ground under them).
    private NavGridSettings SettingsFor(ScriptNavBuild request)
    {
        var ignoreTags = request.IgnoreTags;
        bool Ignored(Part part) =>
            _parents.ContainsKey(part) || ignoreTags.Count > 0 && _sceneObjects.TryGetValue(part, out var sceneObject) && ignoreTags.Any(sceneObject.Data.Tags.Contains);
        Vector3 min, max;
        if (request.Min is { } low && request.Max is { } high)
        {
            (min, max) = (low, high);
        }
        else
        {
            var any = false;
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            foreach (var (part, sceneObject) in _sceneObjects)
            {
                if (!IsPresent(part) || !part.Anchored || !part.CanCollide || part.IsTrigger || Ignored(part))
                {
                    continue;
                }

                var (a, b) = sceneObject.Bounds;
                x0 = MathF.Min(x0, a.X);
                y0 = MathF.Min(y0, a.Y);
                z0 = MathF.Min(z0, a.Z);
                x1 = MathF.Max(x1, b.X);
                y1 = MathF.Max(y1, b.Y);
                z1 = MathF.Max(z1, b.Z);
                any = true;
            }

            if (!any)
            {
                (x0, y0, z0, x1, y1, z1) = (-64f, -2f, -64f, 64f, 2f, 64f);
            }

            // Room around them, and from below the lowest surface to well above the highest.
            const float margin = 16f;
            min = new Vector3(x0 - margin, MathF.Min(y0, 0f) - 4f, z0 - margin);
            max = new Vector3(x1 + margin, y1 + request.AgentHeight + 4f, z1 + margin);
        }

        // A coarser grid when the area would need too many cells.
        var cell = global::System.Math.Clamp(request.CellSize, 0.25f, 8f);
        while ((max.X - min.X) / cell * ((max.Z - min.Z) / cell) > NavGridSettings.MaxCells && cell < 64f)
        {
            cell *= 1.25f;
        }

        if (cell != request.CellSize)
        {
            Console.WriteLine($"[Navigation] The area is large: cells of {cell:0.##} studs instead of {request.CellSize:0.##}");
        }

        return new NavGridSettings
        {
            Min = min,
            Max = max,
            CellSize = cell,
            AgentRadius = request.AgentRadius,
            AgentHeight = request.AgentHeight,
            MaxClimb = request.MaxClimb,
            MaxSlope = request.MaxSlope,
            UseHighestSurface = request.UseHighestSurface,
            IncludeUnanchored = request.IncludeUnanchored,
            Ignore = Ignored
        };
    }

    Vector3? IScriptGameplayScene.FeetOf(IScriptObject target) =>
        target is PartScriptObject sceneObject && _sceneObjects.ContainsKey(sceneObject.Part) ? Feet(sceneObject) : null;

    private static Vector3 Feet(PartScriptObject sceneObject)
    {
        var (min, max) = sceneObject.Bounds;
        return new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);
    }

    string? IScriptGameplayScene.CreateAgent(IScriptObject target, out NavAgent? agent)
    {
        agent = null;
        if (target is not PartScriptObject sceneObject || !_sceneObjects.ContainsKey(sceneObject.Part))
        {
            return "Navigation.CreateAgent expects a scene Part";
        }

        if (_agents.Count >= MaxAgents)
        {
            return $"there can be at most {MaxAgents} navigation agents at once";
        }

        agent = new NavAgent(++_nextAgent, sceneObject.Part);
        agent.HeightOffset = sceneObject.ReadTransform().Position.Y - Feet(sceneObject).Y;
        _agents[agent.Id] = agent;
        return null;
    }

    NavAgent? IScriptGameplayScene.FindAgent(int id) => _agents.TryGetValue(id, out var agent) ? agent : null;

    void IScriptGameplayScene.DestroyAgent(NavAgent agent)
    {
        agent.Destroyed = true;
        _agents.Remove(agent.Id);
    }

    string? IScriptGameplayScene.SetAgentDestination(NavAgent agent, Vector3 destination)
    {
        if (!_sceneObjects.TryGetValue(agent.Part, out var sceneObject))
        {
            return "the agent's object no longer exists";
        }

        if (((IScriptGameplayScene)this).Navigation(out var error) is not { } grid)
        {
            return error;
        }

        return agent.SetDestination(grid, Feet(sceneObject), destination, out var reason) ? null : reason;
    }

    string? IScriptGameplayScene.FollowWithAgent(NavAgent agent, IScriptObject target, float distance)
    {
        if (!_sceneObjects.TryGetValue(agent.Part, out var sceneObject))
        {
            return "the agent's object no longer exists";
        }

        if (target is not PartScriptObject followed || !_sceneObjects.ContainsKey(followed.Part))
        {
            return "Follow: that object no longer exists";
        }

        if (ReferenceEquals(followed.Part, agent.Part))
        {
            return "an agent cannot follow its own object";
        }

        if (((IScriptGameplayScene)this).Navigation(out var error) is not { } grid)
        {
            return error;
        }

        return agent.Follow(grid, Feet(sceneObject), followed.Part, Feet(followed), distance, out var reason) ? null : reason;
    }

    NavAgentStep? IScriptGameplayScene.StepAgent(NavAgent agent, double seconds, out string? error)
    {
        error = null;
        if (!_sceneObjects.TryGetValue(agent.Part, out var sceneObject) || !IsPresent(agent.Part))
        {
            ((IScriptGameplayScene)this).DestroyAgent(agent);
            error = "the agent's object no longer exists";
            return null;
        }

        if (((IScriptGameplayScene)this).Navigation(out error) is not { } grid)
        {
            return null;
        }

        ReportTransformWrites();
        var feet = Feet(sceneObject);
        Vector3? target = agent.Target is { } followed && _sceneObjects.TryGetValue(followed, out var followedObject) && IsPresent(followed)
            ? Feet(followedObject)
            : null;

        // Other agents nearby (for keeping apart): a reused list, no allocation per frame.
        _neighbours.Clear();
        if (agent.Avoidance)
        {
            foreach (var other in _agents.Values)
            {
                if (ReferenceEquals(other, agent) || !_sceneObjects.TryGetValue(other.Part, out var otherObject))
                {
                    continue;
                }

                var position = otherObject.Part.Model is null ? otherObject.Part.Position : otherObject.ReadTransform().Position;
                var dx = position.X - feet.X;
                var dz = position.Z - feet.Z;
                var reach = (agent.Radius + other.Radius) * 1.25f;
                if (dx * dx + dz * dz < reach * reach)
                {
                    _neighbours.Add((new Vector3(position.X, feet.Y, position.Z), other.Radius));
                }
            }
        }

        var step = agent.Step(grid, feet, target, seconds, _neighbours, this);
        Apply(agent, sceneObject, step, seconds);
        return step;
    }

    // One query filter for every agent's look ahead (made once: steering allocates nothing per frame).
    private PhysicsQueryFilter? _probeFilter;
    private Part? _probeSelf;
    private Part? _probeFollowed;

    private bool AcceptObstacle(Part part) =>
        !part.Anchored && !ReferenceEquals(part, _probeSelf) && !ReferenceEquals(part, _probeFollowed) && !_parents.ContainsKey(part);

    // Something unanchored (a crate, another body) right in front: a sideways direction around it, else null.
    Vector3? INavObstacleProbe.BlockedAhead(NavAgent agent, Vector3 direction)
    {
        if (_physics is not { IsDisposed: false } physics || !_sceneObjects.TryGetValue(agent.Part, out var sceneObject))
        {
            return null;
        }

        var (min, max) = sceneObject.Bounds;
        var center = (min + max) * 0.5f;
        var radius = MathF.Max(0.2f, MathF.Min(max.X - min.X, max.Z - min.Z) * 0.4f);
        _probeSelf = sceneObject.Part;
        _probeFollowed = agent.Target;
        _probeFilter ??= new PhysicsQueryFilter { IgnoreTriggers = true, IgnoreEnvironment = true, AcceptPart = AcceptObstacle };
        var found = physics.SphereCast(center, direction, radius, agent.Radius + 1.5f, _probeFilter, out var hit);
        _probeSelf = _probeFollowed = null;
        if (!found || hit.Part is null)
        {
            return null;
        }

        // Pass on the side the obstacle's surface faces away from.
        var right = new Vector3(-direction.Z, 0f, direction.X);
        var side = Vector3.Dot(hit.Normal, right) >= 0f ? right : right * -1f;
        return side;
    }

    private void Apply(NavAgent agent, PartScriptObject sceneObject, NavAgentStep step, double seconds)
    {
        var velocity = step.Velocity;
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        switch (agent.Move)
        {
            case NavAgentMove.Velocity:
                if (_physics is { IsDisposed: false } physics && physics.KindOf(sceneObject.Part) == PhysicsBodyKind.Dynamic)
                {
                    var current = physics.LinearVelocityOf(sceneObject.Part);
                    physics.TrySetLinearVelocity(sceneObject.Part, new Vector3(velocity.X, current.Y, velocity.Z));
                    if (speed > 0.05f)
                    {
                        physics.TryWake(sceneObject.Part);
                    }
                }

                break;
            case NavAgentMove.Transform:
                if (speed > 0f || step.Status == NavAgentStatus.Moving)
                {
                    var transform = sceneObject.ReadTransform();
                    var moved = transform.Position + new Vector3(velocity.X, 0f, velocity.Z) * (float)seconds;
                    var ground = agent.PathHeightAt(moved);
                    sceneObject.WriteTransform(transform with { Position = new Vector3(moved.X, ground + agent.HeightOffset, moved.Z) });
                }

                break;
        }

        if (agent.FaceMovement && agent.Move != NavAgentMove.None && speed > 0.25f)
        {
            var transform = sceneObject.ReadTransform();
            var yaw = MathF.Atan2(-velocity.X, -velocity.Z) * 180f / MathF.PI + agent.FaceYawOffset;
            sceneObject.WriteTransform(transform with { Rotation = new Vector3(0f, yaw, 0f) });
        }
    }

    // A destroyed Part's agents go with it.
    private void RemoveAgentsOf(Part part)
    {
        if (_agents.Count == 0)
        {
            return;
        }

        foreach (var agent in _agents.Values.Where(agent => ReferenceEquals(agent.Part, part)).ToList())
        {
            agent.Destroyed = true;
            _agents.Remove(agent.Id);
        }
    }

    // ---- Teleporting ------------------------------------------------------------------------------------------------

    string? IScriptGameplayScene.Teleport(IScriptObject target, Vector3 position, Vector3? rotation, bool keepVelocity, bool rotateVelocity)
    {
        if (target is not PartScriptObject sceneObject || !_sceneObjects.ContainsKey(sceneObject.Part))
        {
            return "only scene objects can be teleported";
        }

        var part = sceneObject.Part;
        if (_parents.ContainsKey(part))
        {
            return $"'{part.Name}' follows a parent (SetParent): teleport its parent, or let it go first (SetParent(nil))";
        }

        // What the running callback moved counts; then the pose, the body and the velocity change together.
        ReportTransformWrites();
        var before = sceneObject.ReadTransform();
        var newRotation = rotation ?? before.Rotation;
        var physics = _physics is { IsDisposed: false } world ? world : null;
        var dynamic = physics?.KindOf(part) == PhysicsBodyKind.Dynamic;
        Vector3 linear = Vector3.Zero, angular = Vector3.Zero;
        if (dynamic && keepVelocity)
        {
            linear = physics!.LinearVelocityOf(part);
            angular = physics.AngularVelocityOf(part);
            if (rotateVelocity && rotation is not null)
            {
                var turn = NQuaternion.Normalize(PhysicsMath.EulerDegreesToQuaternion(newRotation) * NQuaternion.Inverse(PhysicsMath.EulerDegreesToQuaternion(before.Rotation)));
                linear = PhysicsMath.ToWisko(NVector3.Transform(PhysicsMath.ToNumerics(linear), turn));
                angular = PhysicsMath.ToWisko(NVector3.Transform(PhysicsMath.ToNumerics(angular), turn));
            }
        }

        sceneObject.WriteTransform(new ScriptTransform(position, newRotation, before.Scale));
        ReportTransformWrites();
        if (dynamic)
        {
            physics!.TrySetLinearVelocity(part, linear);
            physics.TrySetAngularVelocity(part, angular);
            physics.TryWake(part);
        }

        // What follows it arrives with it.
        FollowParents();
        return null;
    }

    // ---- Bones ------------------------------------------------------------------------------------------------------

    IReadOnlyList<string>? IScriptGameplayScene.BonesOf(IScriptObject target, out string? error)
    {
        error = null;
        if (target is not PartScriptObject { Part.Model.Asset: { } asset })
        {
            error = $"'{target.Name}' is not an imported model (only models have bones)";
            return null;
        }

        var skeleton = asset.Animation.Skeleton;
        var names = new List<string>(skeleton.BoneCount);
        for (var node = 0; node < skeleton.NodeCount; node++)
        {
            if (skeleton.IsBone(node))
            {
                names.Add(skeleton.NameOf(node));
            }
        }

        return names;
    }

    string? IScriptGameplayScene.BoneTransform(IScriptObject target, string bone, out Vector3 position, out Vector3 rotation)
    {
        position = rotation = Vector3.Zero;
        if (target is not PartScriptObject { Part: { Model.Asset: not null } part })
        {
            return $"'{target.Name}' is not an imported model (only models have bones)";
        }

        if (BonePose(part, NodeOf(part, bone)) is not { } pose)
        {
            return NoSuchBone(part, bone);
        }

        position = pose.Position;
        rotation = PhysicsMath.QuaternionToEulerDegrees(pose.Rotation);
        return null;
    }

    private static int NodeOf(Part part, string bone) => part.Model?.Asset?.Animation.Skeleton.IndexOf(bone) ?? -1;

    private static string NoSuchBone(Part part, string bone)
    {
        var shown = bone.Length > 40 ? bone[..40] + "..." : bone;
        return $"'{part.Name}' has no bone called \"{shown}\" (GetBones() lists them)";
    }

    // A model node's world pose in the pose shown now (its animator's, or the rest pose): position and rotation, scale
    // left out (offsets stay in studs whatever the model's scale).
    private static (Vector3 Position, NQuaternion Rotation)? BonePose(Part part, int node)
    {
        if (node < 0 || part.Model?.Asset is not { } asset || node >= asset.Animation.Skeleton.NodeCount)
        {
            return null;
        }

        var pose = part.Animator?.Pose ?? asset.Animation.RestPose;
        var world = Matrix4.Multiply(ModelTransform.GetInstanceMatrix(part), pose.GlobalOf(node));
        // A mirrored frame decomposes with a negative X scale and a proper rotation.
        var transform = AnimationMath.Decompose(world);
        return (transform.Translation, NQuaternion.Normalize(transform.Rotation));
    }

    // ---- Triggers ---------------------------------------------------------------------------------------------------

    void IScriptGameplayScene.WatchTriggers(IScriptObject target)
    {
        if (target is PartScriptObject sceneObject && _physics is { IsDisposed: false } physics && _sceneObjects.ContainsKey(sceneObject.Part))
        {
            physics.SetReportsContacts(sceneObject.Part, true);
        }
    }

    // A trigger overlap began or ended: object:OnTriggerEntered / OnTriggerExited connections (any script's) hear it.
    private void DispatchTriggerConnections(Part trigger, Part? other, bool entered)
    {
        if (!_sceneObjects.TryGetValue(trigger, out var triggerObject))
        {
            return;
        }

        PartScriptObject? otherObject = null;
        if (other is not null)
        {
            _sceneObjects.TryGetValue(other, out otherObject);
        }

        _lua.DispatchTriggerConnections(triggerObject, otherObject, entered);
    }
}
