using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Wisko.Engine.World;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Physics;

/// <summary>How a Part takes part in the simulation (see <see cref="PhysicsWorld.KindFor"/>).</summary>
public enum PhysicsBodyKind
{
    /// <summary>Anchored, no scripts: never moves.</summary>
    Static,

    /// <summary>Anchored with scripts: moved only by its scripts (its Part's transform); pushes dynamic bodies it meets.</summary>
    Kinematic,

    /// <summary>Not anchored: moved by physics.</summary>
    Dynamic
}

/// <summary>What a Part collides as, for display and checks (never a runtime handle).</summary>
public readonly record struct PhysicsColliderInfo(PhysicsBodyKind Kind, PhysicsColliderShape Shape, uint ShapeId, Vector3 CenterOffset, float Mass);

/// <summary>Where a pair of touching (or, with a trigger, overlapping) colliders is since the last collection.</summary>
public enum PhysicsContactPhase
{
    /// <summary>Started touching.</summary>
    Enter,

    /// <summary>Still touching.</summary>
    Stay,

    /// <summary>Stopped touching (or one of them left the world).</summary>
    Exit
}

/// <summary>
/// Where a pair touches, as the physics step that found it saw it (before that step moved anything): the world point of
/// its deepest contact, the surface normal there pointing toward the receiving side (null for a trigger pair: an overlap
/// has no contact surface), and the receiving side's velocity relative to the other side at that point (units/s,
/// angular motion included).
/// </summary>
public readonly record struct PhysicsContactPoint(Vector3 Point, Vector3? Normal, Vector3 RelativeVelocity)
{
    /// <summary>The same contact as the other side receives it (normal and relative velocity reversed).</summary>
    public PhysicsContactPoint Flipped => new(Point, Normal is { } normal ? Vector3.Zero - normal : null, Vector3.Zero - RelativeVelocity);
}

/// <summary>
/// One pair's change since the last <see cref="PhysicsWorld.CollectContactEvents"/>: <see cref="Part"/> is the one
/// added to the world first, <see cref="Other"/> the other Part or null for the environment (e.g. the baseplate; never
/// for a trigger pair). A trigger pair overlaps (no contact response); any other pair touches. <see cref="Contact"/>,
/// as <see cref="Part"/> receives it: for Enter the contact of the step it began in, for Stay the latest step's, and
/// none for Exit (the pair no longer touches).
/// </summary>
public readonly record struct PhysicsContactEvent(PhysicsContactPhase Phase, bool IsTrigger, Part Part, Part? Other, PhysicsContactPoint? Contact = null);

/// <summary>The nearest collider a ray hit: its Part (null: the environment), how far along the ray, where and the surface normal there.</summary>
public readonly record struct PhysicsRaycastHit(Part? Part, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>A query shape's Parts (no duplicates, in the order the world added them) and whether it reaches the environment.</summary>
public readonly record struct PhysicsOverlapResult(IReadOnlyList<Part> Parts, bool Environment);

/// <summary>
/// What a query (raycast, sphere cast, overlap) skips besides what it can never hit: the Part <see cref="Ignore"/>,
/// Parts <see cref="AcceptPart"/> turns down, triggers (<see cref="IgnoreTriggers"/>) and the environment
/// (<see cref="IgnoreEnvironment"/>).
/// </summary>
public sealed class PhysicsQueryFilter
{
    public Part? Ignore { get; init; }

    /// <summary>Null: every Part (that the other rules let through).</summary>
    public Func<Part, bool>? AcceptPart { get; init; }

    public bool IgnoreTriggers { get; init; }

    public bool IgnoreEnvironment { get; init; }

    /// <summary>Query as a body of this collision group (index): only what that group collides with is found.</summary>
    public int? Layer { get; init; }
}

/// <summary>World-wide physics settings, in Wisko units (Parts' sizes and positions) and seconds.</summary>
public sealed record PhysicsSettings
{
    public static PhysicsSettings Default { get; } = new();

    /// <summary>35 units/sÂ² downward (about real gravity when 1 unit is about 0.28 m).</summary>
    public Vector3 Gravity { get; init; } = new(0f, -35f, 0f);

    /// <summary>Duration of one <see cref="PhysicsWorld.Step"/>; always the same (the runtime is tuned for a fixed step).</summary>
    public float FixedTimestep { get; init; } = 1f / 60f;

    /// <summary>Longest frame <see cref="PhysicsWorld.Advance"/> accepts; a longer one counts as this long.</summary>
    public float MaxFrameTime { get; init; } = 0.25f;

    /// <summary>Most fixed steps one <see cref="PhysicsWorld.Advance"/> takes; time beyond that is dropped.</summary>
    public int MaxStepsPerFrame { get; init; } = 4;

    /// <summary>
    /// Solver substeps per step and velocity iterations per substep. With the 60 Hz contacts below, 2 x 4 keeps a
    /// 10-box tower standing with the least sag; 1 x 4 cannot hold contacts that stiff (PhysicsProbe stacking sweep).
    /// </summary>
    public int Substeps { get; init; } = 4;

    public int VelocityIterations { get; init; } = 2;

    /// <summary>
    /// Friction of the environment's colliders (the baseplate); each Part has its own <see cref="Part.Friction"/>. Two
    /// surfaces in contact use <see cref="PhysicsWorld.CombineFriction"/> of theirs.
    /// </summary>
    public float Friction { get; init; } = Part.DefaultFriction;

    /// <summary>
    /// Contact softness: spring frequency (Hz; at most half the solver rate, which is 240 Hz with 4 substeps at 60 Hz)
    /// and damping ratio. Softer contacts sink further under load (30 Hz: a 10-box tower sags 4 times more).
    /// </summary>
    public float ContactFrequency { get; init; } = 60f;

    public float ContactDampingRatio { get; init; } = 1f;

    /// <summary>Fastest speed (units/s) at which overlapping bodies are pushed apart.</summary>
    public float MaximumRecoveryVelocity { get; init; } = 7f;

    /// <summary>Mass per unit volume: a dynamic Part's Auto mass is density x the volume of its shape.</summary>
    public float Density { get; init; } = 1f;

    /// <summary>A body sleeps after resting below this squared speed for a while (sleeping costs almost nothing).</summary>
    public float SleepThreshold { get; init; } = 0.1f;

    /// <summary>
    /// A kinematic Part whose script moves it faster than this (units/s) jumps there instead of sweeping across, so a
    /// script that relocates a platform does not fling whatever stands on it.
    /// </summary>
    public float MaxKinematicSpeed { get; init; } = 1000f;
}

/// <summary>
/// One physics simulation of a set of Parts (plus environment boxes such as the baseplate). It owns all runtime state:
/// the simulation, its memory and its shapes, keyed by the Part objects themselves; nothing runtime-specific is stored
/// on a Part, in a scene file or anywhere else. Anchored Parts are static, or kinematic when they have scripts; the
/// others are dynamic bodies whose mass is the Part's manual mass or, on Auto, density x volume. Each Part's Gravity,
/// Can Collide, Is Trigger and Friction become its runtime material (see <see cref="PhysicsMaterials"/>), read by the
/// contact and integration callbacks. A trigger Part never makes a contact response (whatever its Can Collide): it only
/// overlaps other Parts, never the environment.
/// <para>
/// Contacts: for the Parts scripts listen to (<see cref="SetReportsContacts"/>), each fixed step notes which of their
/// pairs touch (a trigger pair: overlap), per pair of Parts or Part and environment, and <see cref="CollectContactEvents"/>
/// hands out what changed since it was last called (Enter, Stay, Exit; one of each at most per pair, so a touch that
/// starts and ends in between gives Enter then Exit). The physics callbacks only record; nothing else runs during a step.
/// A pair whose bodies are all asleep keeps its state (the simulation does not test it again until something wakes it).
/// </para>
/// <para>
/// Parts can be added (<see cref="TryAddPart"/>) and removed (<see cref="RemovePart"/>) while the world runs; a removed
/// Part's contacts end at once. A Part's body kind follows its Anchored setting while playing
/// (<see cref="ApplyKindChange"/>), keeping its contacts. <see cref="Raycast"/> / <see cref="RaycastAll"/> /
/// <see cref="SphereCast"/> look along a line, <see cref="OverlapSphere"/> / <see cref="OverlapBox"/> in a region.
/// </para>
/// <para>
/// Forces: <see cref="TryApplyForce"/> / <see cref="TryApplyTorque"/> push a dynamic body during the frame they are
/// applied in. Each <see cref="Advance"/> turns the frame's summed forces into impulses (force x the frame's time) and
/// shares them over the fixed steps that time pays for, so a force applied every frame pushes the same whatever the
/// frame rate; time physics drops (a capped slow frame) drops the matching part of the push, and time not yet worth a
/// step carries its part to the next frame. Nothing is left once the body leaves the world or stops being dynamic.
/// </para>
/// <para>
/// Who owns a transform: a static or kinematic Part's own transform is authoritative (a kinematic body is driven to it
/// every <see cref="Advance"/>); a dynamic body owns its Part's Position and Rotation, written back after stepping. Size
/// always belongs to the Part. A script writing a dynamic Part's Position or Rotation teleports the body
/// (<see cref="ApplyScriptWrite"/>), and a Size change rebuilds that Part's collider.
/// </para>
/// Runs single-threaded. Dispose releases everything; a world is not reusable afterwards.
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
    private const float DegreesPerRadian = 180f / MathF.PI;

    // A pair starts touching when its deepest contact is at most this far apart (units), and keeps touching until it is
    // more than ContactEndGap apart: a resting or sliding contact does not flicker between Enter and Exit.
    private const float ContactBeginGap = 0.01f;
    private const float ContactEndGap = 0.05f;

    private readonly BufferPool _pool;
    private readonly Simulation _simulation;
    private readonly PhysicsColliders _colliders;
    private readonly PhysicsMaterials _materials = new();
    private readonly PhysicsContactRecorder _contactRecorder = new();
    private readonly Dictionary<Part, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _kinematics = [];
    private readonly List<PhysicsEnvironmentBox> _environmentBoxes = [];

    // Collision groups by index (0: Default) and the joints alive, in the order made.
    private readonly List<string> _layerNames = ["Default"];
    private readonly List<PhysicsConstraint> _constraints = [];
    private readonly HashSet<long> _scriptIgnoredPairs = [];
    private int _nextConstraintId;

    // Dynamic bodies with forces this frame or impulses from earlier forces still to hand out.
    private readonly List<Entry> _forced = [];

    // Pairs touching (or with events not yet collected), by ContactKey; pairs ended for good before collection (a Part
    // became or stopped being a trigger: its next pair is a new one); reused buffer for collecting them in order.
    private readonly Dictionary<long, ContactPair> _contacts = [];
    private readonly List<ContactPair> _endedContacts = [];
    private readonly List<ContactPair> _contactOrder = [];

    // Listened pairs about to touch (a speculative contact, not yet within the gap): their relative velocity in the
    // last step and the step before. The solver already slows a body in the step before it touches, so the speed it
    // hit with is the one seen while it was approaching.
    private Dictionary<long, NVector3> _approaching = [];
    private Dictionary<long, NVector3> _approachingBefore = [];

    // Runtime handle -> its Part's entry (null: the environment, or free).
    private Entry?[] _bodyEntries = new Entry?[64];
    private Entry?[] _staticEntries = new Entry?[64];
    private double _accumulator;
    private int _environmentCount;
    private int _nextSequence;
    private long _contactStep;
    private bool _disposed;

    public PhysicsWorld(PhysicsSettings? settings = null)
    {
        Settings = settings ?? PhysicsSettings.Default;
        Validate(Settings);

        _pool = new BufferPool();
        var narrowPhase = new PhysicsNarrowPhaseCallbacks(
            new SpringSettings(Settings.ContactFrequency, Settings.ContactDampingRatio),
            Settings.MaximumRecoveryVelocity,
            _materials,
            _contactRecorder);
        _materials.Gravity = PhysicsMath.ToNumerics(Settings.Gravity);
        var integrator = new PhysicsPoseIntegratorCallbacks(_materials);
        _simulation = Simulation.Create(_pool, narrowPhase, integrator, new SolveDescription(Settings.VelocityIterations, Settings.Substeps));
        _colliders = new PhysicsColliders(_simulation.Shapes, _pool);
    }

    public PhysicsSettings Settings { get; }

    /// <summary>Most gravity a script may set, per axis (units/sÂ²): well beyond any game, short of breaking the solver.</summary>
    public const float MaxGravity = 1000f;

    /// <summary>
    /// The world's gravity now (units/sÂ²; starts as <see cref="PhysicsSettings.Gravity"/>). Setting it applies from the
    /// next step and wakes every dynamic body, so resting ones follow the new direction. Non-finite values are ignored;
    /// each axis is kept within Â±<see cref="MaxGravity"/>.
    /// </summary>
    public Vector3 Gravity
    {
        get => PhysicsMath.ToWisko(_materials.Gravity);
        set
        {
            ThrowIfDisposed();
            if (!PhysicsMath.IsFinite(value))
            {
                return;
            }

            var clamped = new Vector3(
                global::System.Math.Clamp(value.X, -MaxGravity, MaxGravity),
                global::System.Math.Clamp(value.Y, -MaxGravity, MaxGravity),
                global::System.Math.Clamp(value.Z, -MaxGravity, MaxGravity));
            _materials.Gravity = PhysicsMath.ToNumerics(clamped);
            foreach (var entry in _entries.Values)
            {
                if (entry.Kind == PhysicsBodyKind.Dynamic && !entry.Removed)
                {
                    var awake = _simulation.Bodies.GetBodyReference(entry.Body);
                    awake.Awake = true;
                }
            }
        }
    }

    public int StaticCount { get; private set; }

    public int KinematicCount { get; private set; }

    public int DynamicCount { get; private set; }

    /// <summary>Environment boxes (not Parts) in the world.</summary>
    public int EnvironmentCount => _environmentCount;

    /// <summary>Distinct collider shapes registered (Parts with the same type and exact Size share one).</summary>
    public int ShapeCount => _colliders.Count;

    /// <summary>Fixed steps taken so far.</summary>
    public long StepCount { get; private set; }

    /// <summary>Pairs whose contact state is tracked right now (diagnostics).</summary>
    public int ContactPairCount => _contacts.Count + _endedContacts.Count;

    /// <summary>Dynamic bodies with forces not yet handed out (diagnostics).</summary>
    public int ForcedBodyCount => _forced.Count;

    public bool IsDisposed => _disposed;

    /// <summary>The collider a primitive of this type and Size gets, without building a world.</summary>
    public static PhysicsColliderShape ColliderShapeOf(ShapeType type, Vector3 size) => PhysicsColliders.ShapeOf(type, size);

    /// <summary>
    /// The friction two touching surfaces get: the geometric mean of theirs (0.6 and 0.6 give 0.6, 0 and 1 give 0, 1 and
    /// 1 give 1). Symmetric: the order of the two never matters.
    /// </summary>
    public static float CombineFriction(float a, float b) => a == b ? a : MathF.Sqrt(a * b);

    /// <summary>
    /// The mass a primitive Part gets on Auto: density x the volume of its collider (the same calculation a dynamic body
    /// uses, at its current Size). Null for Parts physics does not simulate (imported models, or a Size that is not
    /// finite and positive).
    /// </summary>
    public static float? AutoMassOf(Part part, PhysicsSettings? settings = null) =>
        part.Model is null && PhysicsMath.IsFinite(part.Size) && part.Size.X > 0f && part.Size.Y > 0f && part.Size.Z > 0f
            ? (settings ?? PhysicsSettings.Default).Density * PhysicsColliders.VolumeOf(part.ShapeType, part.Size)
            : null;

    /// <summary>Anchored without scripts: static. Anchored with scripts (they may move it): kinematic. Otherwise dynamic.</summary>
    public static PhysicsBodyKind KindFor(Part part) => KindFor(part.Anchored, part.Scripts.Count > 0);

    /// <summary><see cref="KindFor(Part)"/> from its two inputs (scripted: scripts run on the Part).</summary>
    public static PhysicsBodyKind KindFor(bool anchored, bool scripted) =>
        !anchored ? PhysicsBodyKind.Dynamic
        : scripted ? PhysicsBodyKind.Kinematic
        : PhysicsBodyKind.Static;

    /// <summary>Adds the environment's boxes as fixed colliders.</summary>
    public void AddEnvironment(PhysicsEnvironment environment)
    {
        ThrowIfDisposed();
        foreach (var box in environment.Boxes)
        {
            var collider = _colliders.Get(ShapeType.Block, box.Size);
            var handle = _simulation.Statics.Add(new StaticDescription(PhysicsMath.ToNumerics(box.Center), NQuaternion.Identity, collider.Index));
            _materials.Set(handle, new PhysicsMaterial(Settings.Friction, CanCollide: true, Gravity: false, IsEnvironment: true));
            _environmentBoxes.Add(box);
            _environmentCount++;
        }
    }

    /// <summary>Adds every Part it can; returns how many were added (each skipped Part is logged with the reason).</summary>
    public int AddParts(IEnumerable<Part> parts)
    {
        var added = 0;
        foreach (var part in parts)
        {
            if (TryAddPart(part, out var reason))
            {
                added++;
            }
            else
            {
                Console.WriteLine($"[Physics] '{part.Name}' not simulated: {reason}");
            }
        }

        return added;
    }

    /// <summary>
    /// Adds one Part at its current transform, as <see cref="KindFor"/> says. False with a reason when it cannot be
    /// simulated (already added, an imported model, or a transform that is not finite / a size that is not positive).
    /// </summary>
    public bool TryAddPart(Part part, out string? reason)
    {
        ThrowIfDisposed();
        reason = null;
        if (_entries.ContainsKey(part))
        {
            reason = "already in this world";
            return false;
        }

        if (part.Model is not null)
        {
            reason = "imported model colliders are not supported yet";
            return false;
        }

        if (!HasValidTransform(part))
        {
            reason = "its position, rotation or size is not valid";
            return false;
        }

        var collider = _colliders.Get(part.ShapeType, part.Size);
        var scripted = part.Scripts.Count > 0;
        var entry = new Entry(part, KindFor(part.Anchored, scripted), collider, ++_nextSequence) { Scripted = scripted };
        AddCollidable(entry);
        _entries.Add(part, entry);
        return true;
    }

    /// <summary>
    /// The Part's Anchored setting changed while playing: its body becomes what <see cref="KindFor(Part)"/> says now
    /// (anchored: static, or kinematic when scripts run on it; unanchored: dynamic, starting at rest where the Part is).
    /// It keeps its place in the world, so its contacts go on (a pair that can no longer touch ends: two static sides
    /// are never tested) and its partners are woken. Forces not yet handed out are dropped when it stops being dynamic.
    /// False when the world does not simulate it; true (nothing to do) when its kind is already right.
    /// </summary>
    public bool ApplyKindChange(Part part)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        var kind = KindFor(part.Anchored, entry.Scripted);
        if (kind == entry.Kind || !HasValidTransform(part))
        {
            return true;
        }

        WakeContactPartners(entry);
        RemoveCollidable(entry);
        entry.Kind = kind;
        entry.SyncedAsleep = false;
        if (kind != PhysicsBodyKind.Dynamic)
        {
            ClearForces(entry);
        }

        AddCollidable(entry);
        if (kind == PhysicsBodyKind.Static)
        {
            // Two static sides (or a static side and the environment) are never tested again: that touch is over.
            foreach (var contact in _contacts.Values)
            {
                if ((contact.Part == entry || contact.Other == entry)
                    && (contact.Part == entry ? contact.Other : contact.Part) is not { Kind: not PhysicsBodyKind.Static })
                {
                    contact.Touching = false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// A script now runs on the Part (attached while playing): an anchored Part it may move becomes kinematic, as if the
    /// script had been there when Play started. Does nothing for a Part the world does not simulate.
    /// </summary>
    public void MarkScripted(Part part)
    {
        ThrowIfDisposed();
        if (_entries.TryGetValue(part, out var entry) && !entry.Scripted)
        {
            entry.Scripted = true;
            ApplyKindChange(part);
        }
    }

    public bool Contains(Part part) => _entries.ContainsKey(part);

    /// <summary>
    /// Takes a Part out of the world (its body or static collider goes). Its contacts end at once (each partner gets an
    /// Exit at the next <see cref="CollectContactEvents"/>) and what touched it is woken, so nothing keeps resting on
    /// it. False when the world does not simulate it.
    /// </summary>
    public bool RemovePart(Part part)
    {
        ThrowIfDisposed();
        if (!_entries.Remove(part, out var entry))
        {
            return false;
        }

        foreach (var contact in _contacts.Values)
        {
            if (contact.Part == entry || contact.Other == entry)
            {
                if (contact.Touching && (contact.Part == entry ? contact.Other : contact.Part) is { } partner)
                {
                    Wake(partner);
                }

                contact.Touching = false;
            }
        }

        // Its joints end for good (their other Parts move freely), then its body goes.
        foreach (var constraint in entry.Constraints.ToArray())
        {
            DestroyConstraint(constraint);
        }

        RemoveCollidable(entry);
        ClearForces(entry);
        _materials.ForgetIgnored(entry.Sequence);
        _scriptIgnoredPairs.RemoveWhere(key => (int)(key >> 32) == entry.Sequence || (int)key == entry.Sequence);
        entry.Removed = true;
        return true;
    }

    /// <summary>
    /// Whether the Part's contacts are tracked for <see cref="CollectContactEvents"/> (a script listens to them). Pairs
    /// with a trigger are only tested while one side is listened to. Does nothing for a Part the world does not simulate.
    /// </summary>
    public void SetReportsContacts(Part part, bool reports)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry) || entry.ReportsContacts == reports)
        {
            return;
        }

        entry.ReportsContacts = reports;
        SetMaterial(entry);
        Wake(entry);
    }

    /// <summary>
    /// Adds what changed for each tracked pair since the last call, in a fixed order (by the order the Parts were added,
    /// the environment before other Parts): Enter when it started touching, Stay while it still does, Exit when it
    /// stopped (or a Part left), Enter then Exit for a touch that started and ended in between. One of each at most per
    /// pair and call.
    /// </summary>
    public void CollectContactEvents(List<PhysicsContactEvent> events)
    {
        ThrowIfDisposed();
        if (_contacts.Count == 0 && _endedContacts.Count == 0)
        {
            return;
        }

        // A pair ended for good comes before a new pair of the same two Parts (its Exit before the new Enter).
        _contactOrder.Clear();
        _contactOrder.AddRange(_endedContacts);
        _contactOrder.AddRange(_contacts.Values);
        _endedContacts.Clear();
        _contactOrder.Sort(static (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : b.Ended.CompareTo(a.Ended));
        foreach (var contact in _contactOrder)
        {
            var part = contact.Part.Part;
            var other = contact.Other?.Part;
            if (contact.Touching)
            {
                events.Add(contact.WasTouching
                    ? new PhysicsContactEvent(PhysicsContactPhase.Stay, contact.IsTrigger, part, other, contact.Latest)
                    : new PhysicsContactEvent(PhysicsContactPhase.Enter, contact.IsTrigger, part, other, contact.EnterContact));
            }
            else if (contact.WasTouching)
            {
                events.Add(new PhysicsContactEvent(PhysicsContactPhase.Exit, contact.IsTrigger, part, other));
            }
            else if (contact.Began)
            {
                events.Add(new PhysicsContactEvent(PhysicsContactPhase.Enter, contact.IsTrigger, part, other, contact.EnterContact));
                events.Add(new PhysicsContactEvent(PhysicsContactPhase.Exit, contact.IsTrigger, part, other));
            }

            contact.WasTouching = contact.Touching;
            contact.Began = false;
            if (!contact.Touching && !contact.Ended)
            {
                _contacts.Remove(contact.Key);
            }
        }

        _contactOrder.Clear();
    }

    /// <summary>
    /// The nearest collider along a ray from <paramref name="origin"/> in <paramref name="direction"/> (any non-zero
    /// length) within <paramref name="maxDistance"/>: Parts (triggers included; a Part that neither collides nor is a
    /// trigger is passed through) and the environment, as they are now (a kinematic Part where its Part is, even before
    /// its body catches up). <paramref name="ignore"/> is skipped. False when nothing is hit or the ray is not usable.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, Part? ignore, out PhysicsRaycastHit hit) =>
        Raycast(origin, direction, maxDistance, FilterOf(ignore), out hit);

    /// <summary><see cref="Raycast(Vector3, Vector3, float, Part?, out PhysicsRaycastHit)"/> skipping what <paramref name="filter"/> skips.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, PhysicsQueryFilter? filter, out PhysicsRaycastHit hit)
    {
        ThrowIfDisposed();
        hit = default;
        var start = PhysicsMath.ToNumerics(origin);
        var ray = PhysicsMath.ToNumerics(direction);
        var length = ray.Length();
        if (!PhysicsMath.IsFinite(start) || !PhysicsMath.IsFinite(ray) || !(length > 0f) || !float.IsFinite(length)
            || !(maxDistance > 0f) || !float.IsFinite(maxDistance))
        {
            return false;
        }

        ray /= length;
        var handler = new RayHitHandler(this, filter, maxDistance);
        _simulation.RayCast(start, ray, maxDistance, _pool, ref handler);

        // Kinematic Parts are tested where their Parts are now: their bodies follow only at the next Advance.
        var data = new RayData { Origin = start, Direction = ray };
        foreach (var entry in _kinematics)
        {
            if (!Accepts(filter, entry, _materials.Of(new CollidableReference(CollidableMobility.Kinematic, entry.Body))) || !TryTargetPose(entry, out var pose))
            {
                continue;
            }

            var shapeHit = new ShapeRayHitHandler(handler.Distance);
            var limit = handler.Distance;
            _simulation.Shapes[entry.Collider.Index.Type].RayTest(entry.Collider.Index.Index, pose, data, ref limit, _pool, ref shapeHit);
            if (shapeHit.Found && shapeHit.Distance < handler.Distance)
            {
                handler.Distance = shapeHit.Distance;
                handler.Normal = shapeHit.Normal;
                handler.Entry = entry;
                handler.Environment = false;
                handler.Found = true;
            }
        }

        if (!handler.Found)
        {
            return false;
        }

        var normal = handler.Normal;
        var normalLength = normal.Length();
        normal = normalLength > 1e-12f && float.IsFinite(normalLength) ? normal / normalLength : -ray;
        hit = new PhysicsRaycastHit(handler.Environment ? null : handler.Entry?.Part, handler.Distance, PhysicsMath.ToWisko(start + ray * handler.Distance), PhysicsMath.ToWisko(normal));
        return true;
    }

    /// <summary>
    /// Every collider along the ray within <paramref name="maxDistance"/> (what <see cref="Raycast"/> could hit, each
    /// once, where the ray enters it), nearest first (equal distances: the environment, then Parts in the order the
    /// world added them), at most <paramref name="maxHits"/>. Empty when the ray is not usable.
    /// </summary>
    public List<PhysicsRaycastHit> RaycastAll(Vector3 origin, Vector3 direction, float maxDistance, Part? ignore, int maxHits) =>
        RaycastAll(origin, direction, maxDistance, FilterOf(ignore), maxHits);

    /// <summary><see cref="RaycastAll(Vector3, Vector3, float, Part?, int)"/> skipping what <paramref name="filter"/> skips.</summary>
    public List<PhysicsRaycastHit> RaycastAll(Vector3 origin, Vector3 direction, float maxDistance, PhysicsQueryFilter? filter, int maxHits)
    {
        ThrowIfDisposed();
        var results = new List<PhysicsRaycastHit>();
        var start = PhysicsMath.ToNumerics(origin);
        var ray = PhysicsMath.ToNumerics(direction);
        var length = ray.Length();
        if (!PhysicsMath.IsFinite(start) || !PhysicsMath.IsFinite(ray) || !(length > 0f) || !float.IsFinite(length)
            || !(maxDistance > 0f) || !float.IsFinite(maxDistance) || maxHits <= 0)
        {
            return results;
        }

        ray /= length;
        var hits = new List<(float Distance, int Order, Entry? Entry, NVector3 Normal)>();
        var handler = new RayAllHitHandler(this, filter, hits);
        _simulation.RayCast(start, ray, maxDistance, _pool, ref handler);

        var data = new RayData { Origin = start, Direction = ray };
        foreach (var entry in _kinematics)
        {
            if (!Accepts(filter, entry, _materials.Of(new CollidableReference(CollidableMobility.Kinematic, entry.Body))) || !TryTargetPose(entry, out var pose))
            {
                continue;
            }

            var shapeHit = new ShapeRayHitHandler(maxDistance);
            var limit = maxDistance;
            _simulation.Shapes[entry.Collider.Index.Type].RayTest(entry.Collider.Index.Index, pose, data, ref limit, _pool, ref shapeHit);
            if (shapeHit.Found)
            {
                hits.Add((shapeHit.Distance, entry.Sequence, entry, shapeHit.Normal));
            }
        }

        hits.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Order.CompareTo(b.Order));
        foreach (var (distance, _, entry, rawNormal) in hits)
        {
            if (results.Count == maxHits)
            {
                break;
            }

            var normalLength = rawNormal.Length();
            var normal = normalLength > 1e-12f && float.IsFinite(normalLength) ? rawNormal / normalLength : -ray;
            results.Add(new PhysicsRaycastHit(entry?.Part, distance, PhysicsMath.ToWisko(start + ray * distance), PhysicsMath.ToWisko(normal)));
        }

        return results;
    }

    /// <summary>
    /// The first collider a sphere of <paramref name="radius"/> meets moving from <paramref name="origin"/> along
    /// <paramref name="direction"/> (any non-zero length) within <paramref name="maxDistance"/>: a true swept-shape test
    /// against the physics colliders (what <see cref="Raycast"/> can hit; kinematic Parts where their Parts are now).
    /// Distance is how far the sphere's centre travelled, Point the touching point on the collider, Normal its surface
    /// normal there (pointing back toward the sphere). A collider the sphere already overlaps where it starts is not
    /// reported (<see cref="OverlapSphere"/> finds those). False when nothing is met or the query is not usable.
    /// </summary>
    public bool SphereCast(Vector3 origin, Vector3 direction, float radius, float maxDistance, Part? ignore, out PhysicsRaycastHit hit) =>
        SphereCast(origin, direction, radius, maxDistance, FilterOf(ignore), out hit);

    /// <summary><see cref="SphereCast(Vector3, Vector3, float, float, Part?, out PhysicsRaycastHit)"/> skipping what <paramref name="filter"/> skips.</summary>
    public unsafe bool SphereCast(Vector3 origin, Vector3 direction, float radius, float maxDistance, PhysicsQueryFilter? filter, out PhysicsRaycastHit hit)
    {
        ThrowIfDisposed();
        hit = default;
        var start = PhysicsMath.ToNumerics(origin);
        var ray = PhysicsMath.ToNumerics(direction);
        var length = ray.Length();
        if (!PhysicsMath.IsFinite(start) || !PhysicsMath.IsFinite(ray) || !(length > 0f) || !float.IsFinite(length)
            || !(maxDistance > 0f) || !float.IsFinite(maxDistance) || !(radius > 0f) || !float.IsFinite(radius))
        {
            return false;
        }

        ray /= length;
        var sphere = new Sphere(radius);
        var pose = new RigidPose(start);
        var velocity = new BodyVelocity(ray);

        // BEPU's own termination settings for a shape this size (see Simulation.Sweep).
        var minimumProgression = 1e-3f * radius;
        var convergenceThreshold = 1e-5f * radius;
        const int maximumIterations = 25;
        var handler = new SweepHitHandler(this, filter, maxDistance);
        _simulation.Sweep(sphere, pose, velocity, maxDistance, _pool, ref handler, minimumProgression, convergenceThreshold, maximumIterations);

        // Kinematic Parts are tested where their Parts are now (their bodies follow only at the next Advance).
        var children = new AllChildren();
        foreach (var entry in _kinematics)
        {
            if (!Accepts(filter, entry, _materials.Of(new CollidableReference(CollidableMobility.Kinematic, entry.Body))) || !TryTargetPose(entry, out var target))
            {
                continue;
            }

            var type = entry.Collider.Index.Type;
            var task = _simulation.NarrowPhase.SweepTaskRegistry.GetTask(Sphere.Id, type);
            if (task is null)
            {
                continue;
            }

            _simulation.Shapes[type].GetShapeData(entry.Collider.Index.Index, out var shapeData, out _);
            var stillB = default(BodyVelocity);
            if (task.Sweep(&sphere, Sphere.Id, NQuaternion.Identity, velocity, shapeData, type, target.Position - start, target.Orientation, stillB,
                    handler.Distance, minimumProgression, convergenceThreshold, maximumIterations, ref children, _simulation.Shapes,
                    _simulation.NarrowPhase.SweepTaskRegistry, _pool, out _, out var t1, out var location, out var normal)
                && t1 > 0f && (t1 < handler.Distance || !handler.Found))
            {
                handler.Distance = t1;
                handler.Point = location + start;
                handler.Normal = normal;
                handler.Entry = entry;
                handler.Environment = false;
                handler.Found = true;
            }
        }

        if (!handler.Found)
        {
            return false;
        }

        // The normal points from the collider toward the sphere's centre where they meet.
        var centre = start + ray * handler.Distance;
        var surfaceNormal = handler.Normal;
        var normalLength = surfaceNormal.Length();
        surfaceNormal = normalLength > 1e-12f && float.IsFinite(normalLength) ? surfaceNormal / normalLength : -ray;
        if (NVector3.Dot(surfaceNormal, centre - handler.Point) < 0f)
        {
            surfaceNormal = -surfaceNormal;
        }

        hit = new PhysicsRaycastHit(handler.Environment ? null : handler.Entry?.Part, handler.Distance, PhysicsMath.ToWisko(handler.Point), PhysicsMath.ToWisko(surfaceNormal));
        return true;
    }

    /// <summary>
    /// The Parts a sphere overlaps (more than touching), tested against each Part's exact shape where its Part is now,
    /// among what <see cref="Raycast"/> can hit (Parts that collide or are triggers), skipping <paramref name="ignore"/>;
    /// and whether it reaches the environment. At most <paramref name="maxParts"/> Parts.
    /// </summary>
    public PhysicsOverlapResult OverlapSphere(Vector3 center, float radius, Part? ignore, int maxParts) =>
        OverlapSphere(center, radius, FilterOf(ignore), maxParts);

    /// <summary><see cref="OverlapSphere(Vector3, float, Part?, int)"/> skipping what <paramref name="filter"/> skips.</summary>
    public PhysicsOverlapResult OverlapSphere(Vector3 center, float radius, PhysicsQueryFilter? filter, int maxParts)
    {
        ThrowIfDisposed();
        return !PhysicsMath.IsFinite(center) || !(radius > 0f) || !float.IsFinite(radius)
            ? new PhysicsOverlapResult([], false)
            : Overlap(PartShape.Sphere(center, radius), filter, maxParts);
    }

    /// <summary>
    /// <see cref="OverlapSphere"/> for a box of <paramref name="size"/> centred on <paramref name="center"/>, turned by
    /// <paramref name="rotationDegrees"/> (Euler degrees, as a Part's Rotation).
    /// </summary>
    public PhysicsOverlapResult OverlapBox(Vector3 center, Vector3 size, Vector3 rotationDegrees, Part? ignore, int maxParts) =>
        OverlapBox(center, size, rotationDegrees, FilterOf(ignore), maxParts);

    /// <summary><see cref="OverlapBox(Vector3, Vector3, Vector3, Part?, int)"/> skipping what <paramref name="filter"/> skips.</summary>
    public PhysicsOverlapResult OverlapBox(Vector3 center, Vector3 size, Vector3 rotationDegrees, PhysicsQueryFilter? filter, int maxParts)
    {
        ThrowIfDisposed();
        return !PhysicsMath.IsFinite(center) || !PhysicsMath.IsFinite(size) || !PhysicsMath.IsFinite(rotationDegrees)
            || !(size.X > 0f) || !(size.Y > 0f) || !(size.Z > 0f)
            ? new PhysicsOverlapResult([], false)
            : Overlap(PartShape.Box(center, size, rotationDegrees), filter, maxParts);
    }

    /// <summary>Advances the simulation by exactly one <see cref="PhysicsSettings.FixedTimestep"/> (single-threaded).</summary>
    public void Step()
    {
        ThrowIfDisposed();
        _simulation.Timestep(Settings.FixedTimestep, null);
        StepCount++;
        UpdateContacts();
    }

    /// <summary>
    /// One rendered frame of <paramref name="frameSeconds"/>: takes the fixed steps that time adds up to, at most
    /// <see cref="PhysicsSettings.MaxStepsPerFrame"/> (frames longer than <see cref="PhysicsSettings.MaxFrameTime"/>
    /// count as that long, and whole steps beyond the cap are dropped, so a slow frame slows physics down for a moment
    /// instead of making it catch up). Kinematic bodies are carried to their Parts' transforms across those steps, then
    /// the dynamic Parts get their new positions and rotations. Returns the number of steps taken (0 when the time
    /// does not add up to one yet).
    /// </summary>
    public int Advance(double frameSeconds)
    {
        ThrowIfDisposed();
        var frame = double.IsFinite(frameSeconds) && frameSeconds > 0d ? System.Math.Min(frameSeconds, Settings.MaxFrameTime) : 0d;
        _accumulator += frame;
        if (_forced.Count > 0)
        {
            CollectForces((float)frame);
        }

        // The step is a float (1/60f) and frame times are doubles: a hair of slack keeps 60 frames of 1/60 s at 60 steps.
        var step = (double)Settings.FixedTimestep;
        var steps = (int)System.Math.Min(System.Math.Floor(_accumulator / step + 1e-4), Settings.MaxStepsPerFrame);
        if (steps == 0)
        {
            return 0;
        }

        var pending = _accumulator;
        _accumulator = System.Math.Max(0d, _accumulator - steps * step);
        if (_accumulator >= step)
        {
            _accumulator = 0d; // capped: the rest of this long frame is dropped, never caught up later
        }

        var duration = steps * Settings.FixedTimestep;
        var surfaces = false;
        foreach (var entry in _kinematics)
        {
            // An anchored Part with its own velocity: its Part moves on by the time these steps cover (physics time,
            // so the same whatever the frame rate), then its body is driven there like any scripted one.
            if (entry.KinematicLinear != NVector3.Zero || entry.KinematicAngular != NVector3.Zero)
            {
                MoveKinematicPart(entry, duration);
            }

            DriveKinematic(entry, duration);
            entry.DrivenVelocity = _simulation.Bodies.GetBodyReference(entry.Body).Velocity;
            surfaces |= entry.SurfaceVelocity != NVector3.Zero;
        }

        // The pending pushes belong to the pending time: these steps hand out their share, the rest waits (or is
        // dropped with the time physics dropped).
        var share = _forced.Count > 0 && pending > 0d ? (float)System.Math.Min(1d, steps * step / pending) : 0f;
        for (var i = 0; i < steps; i++)
        {
            if (share > 0f)
            {
                ApplyPendingImpulses(share / steps);
            }

            if (surfaces)
            {
                PrepareSurfaces(i, steps);
            }

            Step();
        }

        if (_forced.Count > 0)
        {
            KeepPendingImpulses(share, _accumulator > 0d);
        }

        // Kinematic bodies end exactly where their Parts are (no drift from integrating the velocities).
        foreach (var entry in _kinematics)
        {
            var body = _simulation.Bodies.GetBodyReference(entry.Body);
            if (TryTargetPose(entry, out var target) && (body.Pose.Position != target.Position || body.Pose.Orientation != target.Orientation))
            {
                body.Pose = target;
                _simulation.Bodies.UpdateBounds(entry.Body);
            }
        }

        SyncToParts();
        return steps;
    }

    /// <summary>
    /// A script wrote <paramref name="part"/>'s transform (only Parts in this world are affected). A Size change
    /// rebuilds its collider; a Position or Rotation change on a dynamic body teleports it there (velocity kept, woken)
    /// and moves a static one. Kinematic bodies need nothing here: <see cref="Advance"/> follows their Parts.
    /// </summary>
    public void ApplyScriptWrite(Part part, bool positionOrRotationChanged, bool sizeChanged)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry) || !HasValidTransform(part))
        {
            return;
        }

        if (sizeChanged)
        {
            RebuildCollider(entry);
            WakeContactPartners(entry);
            return;
        }

        if (!positionOrRotationChanged)
        {
            return;
        }

        if (entry.Kind == PhysicsBodyKind.Dynamic)
        {
            var body = _simulation.Bodies.GetBodyReference(entry.Body);
            body.Awake = true;
            body.Pose = PoseOf(part, entry.Collider);
            _simulation.Bodies.UpdateBounds(entry.Body);
        }
        else if (entry.Kind == PhysicsBodyKind.Static)
        {
            _simulation.Statics.ApplyDescription(entry.Static, new StaticDescription(PoseOf(part, entry.Collider), entry.Collider.Index));
            WakeContactPartners(entry); // a sleeping partner is tested again (ApplyDescription wakes dynamic ones only)
        }
    }

    /// <summary>Puts every kinematic body exactly at its Part's transform, at rest (after scripts' Start set things up).</summary>
    public void SnapKinematicsToParts()
    {
        ThrowIfDisposed();
        foreach (var entry in _kinematics)
        {
            if (TryTargetPose(entry, out var target))
            {
                var body = _simulation.Bodies.GetBodyReference(entry.Body);
                body.Awake = true;
                body.Pose = target;
                body.Velocity = default;
                _simulation.Bodies.UpdateBounds(entry.Body);
            }
        }
    }

    /// <summary>
    /// Writes each dynamic body's position and rotation back to its Part (Position = the Part's origin, Rotation =
    /// Euler degrees) when they changed; Size and everything else are never touched, and static and kinematic Parts
    /// are never written. A body already asleep at the previous sync has not moved and is skipped. A body whose pose is
    /// not finite is left out (logged).
    /// </summary>
    public void SyncToParts()
    {
        ThrowIfDisposed();
        foreach (var (part, entry) in _entries)
        {
            if (entry.Kind != PhysicsBodyKind.Dynamic)
            {
                continue;
            }

            var awake = _simulation.Bodies.GetBodyReference(entry.Body).Awake;
            if (!awake && entry.SyncedAsleep)
            {
                continue;
            }

            entry.SyncedAsleep = !awake;

            if (!TryPartPose(entry, out var position, out var rotation))
            {
                Console.WriteLine($"[Physics] '{part.Name}' has an invalid physics pose; its transform was not updated");
                continue;
            }

            if (!part.Position.Equals(position))
            {
                part.Position = position;
            }

            if (!part.Rotation.Equals(rotation))
            {
                part.Rotation = rotation;
            }
        }
    }

    /// <summary>The Part's transform as the simulation has it now (Position = the Part's origin, Rotation = Euler degrees).</summary>
    public bool TryGetPose(Part part, out Vector3 position, out Vector3 rotationDegrees)
    {
        position = default;
        rotationDegrees = default;
        return !_disposed && _entries.TryGetValue(part, out var entry) && TryPartPose(entry, out position, out rotationDegrees);
    }

    public bool TryGetCollider(Part part, out PhysicsColliderInfo info)
    {
        info = default;
        if (_disposed || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        info = new PhysicsColliderInfo(entry.Kind, entry.Collider.Shape, entry.Collider.Index.Packed, PhysicsMath.ToWisko(entry.Collider.Center), entry.Mass);
        return true;
    }

    /// <summary>False for static Parts and for bodies that have gone to sleep.</summary>
    public bool IsAwake(Part part) =>
        !_disposed && _entries.TryGetValue(part, out var entry) && entry.Kind != PhysicsBodyKind.Static
        && _simulation.Bodies.GetBodyReference(entry.Body).Awake;

    /// <summary>
    /// A moving body's current linear velocity (units/s); zero for static Parts. An anchored Part moving on its own
    /// (<see cref="SetKinematicVelocity"/>) reports that velocity; a surface reports how its Part moves, not its surface.
    /// </summary>
    public Vector3 LinearVelocityOf(Part part)
    {
        if (_disposed || !_entries.TryGetValue(part, out var entry) || entry.Kind == PhysicsBodyKind.Static)
        {
            return Vector3.Zero;
        }

        if (entry.Kind == PhysicsBodyKind.Kinematic)
        {
            return PhysicsMath.ToWisko(entry.KinematicLinear != NVector3.Zero || entry.KinematicAngular != NVector3.Zero
                ? entry.KinematicLinear
                : entry.SurfaceVelocity != NVector3.Zero ? entry.DrivenVelocity.Linear : _simulation.Bodies.GetBodyReference(entry.Body).Velocity.Linear);
        }

        return PhysicsMath.ToWisko(_simulation.Bodies.GetBodyReference(entry.Body).Velocity.Linear);
    }

    /// <summary>How this world simulates the Part, or null when it does not (never added: an imported model, an invalid transform).</summary>
    public PhysicsBodyKind? KindOf(Part part) => !_disposed && _entries.TryGetValue(part, out var entry) ? entry.Kind : null;

    /// <summary>A moving body's current angular velocity in degrees per second around the world axes; zero for static Parts.</summary>
    public Vector3 AngularVelocityOf(Part part)
    {
        if (_disposed || !_entries.TryGetValue(part, out var entry) || entry.Kind == PhysicsBodyKind.Static)
        {
            return Vector3.Zero;
        }

        var angular = entry.Kind == PhysicsBodyKind.Kinematic && (entry.KinematicLinear != NVector3.Zero || entry.KinematicAngular != NVector3.Zero)
            ? entry.KinematicAngular
            : _simulation.Bodies.GetBodyReference(entry.Body).Velocity.Angular;
        return PhysicsMath.ToWisko(angular * DegreesPerRadian);
    }

    /// <summary>
    /// Sets a dynamic body's linear velocity (units per second) and wakes it. False, changing nothing, for any other Part:
    /// static and kinematic bodies follow their Parts' transforms, and a Part this world does not simulate has no body.
    /// </summary>
    public bool TrySetLinearVelocity(Part part, Vector3 velocity)
    {
        if (!TryDynamicBody(part, velocity, out var body))
        {
            return false;
        }

        body.Velocity.Linear = PhysicsMath.ToNumerics(velocity);
        return true;
    }

    /// <summary>Sets a dynamic body's angular velocity (degrees per second around the world axes) and wakes it (see <see cref="TrySetLinearVelocity"/>).</summary>
    public bool TrySetAngularVelocity(Part part, Vector3 degreesPerSecond)
    {
        if (!TryDynamicBody(part, degreesPerSecond, out var body))
        {
            return false;
        }

        body.Velocity.Angular = PhysicsMath.ToNumerics(degreesPerSecond) / DegreesPerRadian;
        return true;
    }

    /// <summary>
    /// Adds an instant change of momentum (mass x velocity) at a dynamic body's centre of mass and wakes it; the body's
    /// velocity changes by impulse / mass. False for any other Part (see <see cref="TrySetLinearVelocity"/>).
    /// </summary>
    public bool TryApplyImpulse(Part part, Vector3 impulse)
    {
        if (!TryDynamicBody(part, impulse, out var body))
        {
            return false;
        }

        body.ApplyLinearImpulse(PhysicsMath.ToNumerics(impulse));
        return true;
    }

    /// <summary>
    /// Adds a force (mass x units/sÂ²) at a dynamic body's centre of mass for this frame: forces applied in one frame add
    /// up and push for that frame's time (see the class remarks), then they are gone (apply one every frame to keep
    /// pushing). Wakes the body. False, changing nothing, for any other Part or a force that is not finite.
    /// </summary>
    public bool TryApplyForce(Part part, Vector3 force) => AddForce(part, force, torque: false);

    /// <summary>A torque (mass x unitsÂ²/sÂ², around the world axes) for this frame, like <see cref="TryApplyForce"/>.</summary>
    public bool TryApplyTorque(Part part, Vector3 torque) => AddForce(part, torque, torque: true);

    /// <summary>
    /// Adds an instant impulse at a world point of a dynamic body: its velocity changes by impulse / mass, and an
    /// impulse off its centre of mass also turns it. Wakes it. False for any other Part or values that are not finite.
    /// </summary>
    public bool TryApplyImpulseAtPoint(Part part, Vector3 impulse, Vector3 point)
    {
        if (!PhysicsMath.IsFinite(point) || !TryDynamicBody(part, impulse, out var body))
        {
            return false;
        }

        body.ApplyImpulse(PhysicsMath.ToNumerics(impulse), PhysicsMath.ToNumerics(point) - body.Pose.Position);
        return true;
    }

    /// <summary>An instant angular impulse (around the world axes) on a dynamic body; wakes it. False for any other Part.</summary>
    public bool TryApplyAngularImpulse(Part part, Vector3 impulse)
    {
        if (!TryDynamicBody(part, impulse, out var body))
        {
            return false;
        }

        body.ApplyAngularImpulse(PhysicsMath.ToNumerics(impulse));
        return true;
    }

    /// <summary>Wakes a moving body (dynamic or kinematic) so the next step simulates it. False for a Part the world does not simulate.</summary>
    public bool TryWake(Part part)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        Wake(entry);
        return true;
    }

    /// <summary>
    /// The Part's Gravity, Can Collide, Is Trigger, Friction or Mass changed while playing: its runtime material follows
    /// at once (a dynamic body's mass and inertia too), and the change is made visible: a body is woken, and a static
    /// Part wakes the sleeping bodies around it (one resting on it falls through once it no longer collides). When it
    /// became or stopped being a trigger, its pairs end now (Exit at the next collection) and later touches are new pairs.
    /// </summary>
    public void ApplySettingsChange(Part part)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry))
        {
            return;
        }

        if (MaterialOf(entry).IsTrigger != CurrentMaterial(entry).IsTrigger)
        {
            // Partners are woken while the pairs still name them (what rested on it is tested again).
            WakeContactPartners(entry);
            EndContactsForGood(entry);
        }

        SetMaterial(entry);
        WakeContactPartners(entry);
        if (entry.Kind == PhysicsBodyKind.Static)
        {
            _simulation.Statics.ApplyDescription(entry.Static, new StaticDescription(_simulation.Statics.GetStaticReference(entry.Static).Pose, entry.Collider.Index));
            return;
        }

        var body = _simulation.Bodies.GetBodyReference(entry.Body);
        body.Awake = true;
        if (entry.Kind == PhysicsBodyKind.Dynamic)
        {
            var mass = MassOf(part, entry.Collider);
            if (mass != entry.Mass)
            {
                entry.Mass = mass;
                _simulation.Bodies.SetLocalInertia(entry.Body, InertiaOf(entry, entry.Collider, mass));
            }
        }
    }

    /// <summary>
    /// While <paramref name="locked"/>, physics never turns the Part's body (infinite inertia: contacts, forces and
    /// torques leave its rotation alone; a script can still set its Rotation), e.g. an upright character. Locking stops
    /// its spin. Kept until changed, across Anchored changes. False when the world does not simulate the Part.
    /// </summary>
    public bool SetRotationLocked(Part part, bool locked)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        if (entry.RotationLocked == locked)
        {
            return true;
        }

        entry.RotationLocked = locked;
        if (entry.Kind == PhysicsBodyKind.Dynamic)
        {
            _simulation.Bodies.SetLocalInertia(entry.Body, InertiaOf(entry, entry.Collider, entry.Mass));
            var body = _simulation.Bodies.GetBodyReference(entry.Body);
            if (locked)
            {
                body.Velocity.Angular = NVector3.Zero;
            }

            body.Awake = true;
        }

        return true;
    }

    public bool IsRotationLocked(Part part) => !_disposed && _entries.TryGetValue(part, out var entry) && entry.RotationLocked;

    /// <summary>
    /// Puts the Part in an attachment group (0: none): Parts of one group never touch each other (a held tool does not
    /// push its holder); touches between them end at the next step.
    /// </summary>
    public void SetCollisionGroup(Part part, int group)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(part, out var entry) || entry.CollisionGroup == group)
        {
            return;
        }

        entry.CollisionGroup = group;
        SetMaterial(entry);
        WakeContactPartners(entry);
    }

    /// <summary>
    /// How strongly the world's gravity pulls the Part while its Gravity is on (1: normal, 0: floats, negative: falls
    /// up), from the next step; wakes it. Kept across Anchored changes (only dynamic bodies feel gravity). False when the
    /// world does not simulate the Part or the scale is not finite.
    /// </summary>
    public bool SetGravityScale(Part part, float scale)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(scale) || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        entry.GravityScale = scale;
        SetMaterial(entry);
        Wake(entry);
        return true;
    }

    public float GravityScaleOf(Part part) => !_disposed && _entries.TryGetValue(part, out var entry) ? entry.GravityScale : 1f;

    /// <summary>
    /// Drag on the Part's motion while it is dynamic: each second its velocity keeps e^(-damping) of itself (0: none;
    /// 1: about a third left after a second). Values are clamped to 0..100. False when the world does not simulate it.
    /// </summary>
    public bool SetDamping(Part part, float linear, float angular)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(linear) || !float.IsFinite(angular) || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        entry.LinearDamping = global::System.Math.Clamp(linear, 0f, 100f);
        entry.AngularDamping = global::System.Math.Clamp(angular, 0f, 100f);
        SetMaterial(entry);
        return true;
    }

    public (float Linear, float Angular) DampingOf(Part part) =>
        !_disposed && _entries.TryGetValue(part, out var entry) ? (entry.LinearDamping, entry.AngularDamping) : (0f, 0f);

    /// <summary>
    /// The collision group with this name (case-sensitive), made when <paramref name="create"/> and it does not exist
    /// yet (it then collides with every group); -1 when there is none, or no room for another (at most
    /// <see cref="PhysicsMaterials.MaxLayers"/>, Default included). "Default" is group 0.
    /// </summary>
    public int CollisionLayerIndex(string name, bool create)
    {
        ThrowIfDisposed();
        var index = _layerNames.IndexOf(name);
        if (index >= 0 || !create || _layerNames.Count >= PhysicsMaterials.MaxLayers)
        {
            return index;
        }

        _layerNames.Add(name);
        return _layerNames.Count - 1;
    }

    public string CollisionLayerName(int index) => index >= 0 && index < _layerNames.Count ? _layerNames[index] : "Default";

    public IReadOnlyList<string> CollisionLayerNames => _layerNames;

    /// <summary>Puts the Part in a collision group (index from <see cref="CollisionLayerIndex"/>); wakes it and what it touches.</summary>
    public bool SetCollisionLayer(Part part, int layer)
    {
        ThrowIfDisposed();
        if (layer < 0 || layer >= _layerNames.Count || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        if (entry.Layer != layer)
        {
            entry.Layer = layer;
            SetMaterial(entry);
            Wake(entry);
            WakeBodiesNear(entry);
        }

        return true;
    }

    public int CollisionLayerOf(Part part) => !_disposed && _entries.TryGetValue(part, out var entry) ? entry.Layer : 0;

    /// <summary>Whether two collision groups touch (and trigger each other). Applies from the next step to every body (all are woken).</summary>
    public void SetLayersCollide(int a, int b, bool collide)
    {
        ThrowIfDisposed();
        if (a < 0 || b < 0 || a >= _layerNames.Count || b >= _layerNames.Count || _materials.LayersCollide(a, b) == collide)
        {
            return;
        }

        _materials.SetLayersCollide(a, b, collide);
        WakeAll();
    }

    public bool LayersCollide(int a, int b) =>
        a < 0 || b < 0 || a >= PhysicsMaterials.MaxLayers || b >= PhysicsMaterials.MaxLayers || _materials.LayersCollide(a, b);

    /// <summary>Two Parts never touch (nor trigger each other) while <paramref name="ignored"/>; false when either is not simulated.</summary>
    public bool SetCollisionIgnored(Part a, Part b, bool ignored)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(a, out var entryA) || !_entries.TryGetValue(b, out var entryB) || entryA == entryB)
        {
            return false;
        }

        var key = PairKey(entryA, entryB);
        if (ignored ? _scriptIgnoredPairs.Add(key) : _scriptIgnoredPairs.Remove(key))
        {
            ApplyIgnoredPair(entryA, entryB);
        }

        return true;
    }

    /// <summary>Whether a script set the pair to never touch (<see cref="SetCollisionIgnored"/>); joints do not count here.</summary>
    public bool IsCollisionIgnored(Part a, Part b) =>
        !_disposed && _entries.TryGetValue(a, out var entryA) && _entries.TryGetValue(b, out var entryB)
        && _scriptIgnoredPairs.Contains(PairKey(entryA, entryB));

    private static long PairKey(Entry a, Entry b) =>
        a.Sequence < b.Sequence ? ((long)a.Sequence << 32) | (uint)b.Sequence : ((long)b.Sequence << 32) | (uint)a.Sequence;

    // The pair never touches while a script says so or a joint (without CollideConnected) holds them together.
    private void ApplyIgnoredPair(Entry a, Entry b)
    {
        var joined = _constraints.Any(other => !other.IsDestroyed && !other.CollideConnected
            && ((other.PartA == a.Part && other.PartB == b.Part) || (other.PartA == b.Part && other.PartB == a.Part)));
        if (_materials.SetIgnored(a.Sequence, b.Sequence, joined || _scriptIgnoredPairs.Contains(PairKey(a, b))))
        {
            Wake(a);
            Wake(b);
        }
    }

    /// <summary>
    /// An anchored Part that moves on its own: its Position and Rotation advance by these velocities (units/s; degrees/s
    /// around the world axes) every physics step, and its body pushes and carries what it touches (a moving platform,
    /// a spinning hazard, a turning door). Zero stops it. An anchored Part without scripts becomes kinematic for this.
    /// False for a Part that is not anchored or not simulated, or values that are not finite.
    /// </summary>
    public bool SetKinematicVelocity(Part part, Vector3 linear, Vector3 angularDegrees)
    {
        ThrowIfDisposed();
        if (!PhysicsMath.IsFinite(linear) || !PhysicsMath.IsFinite(angularDegrees) || !part.Anchored || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        MarkScripted(part);
        entry.KinematicLinear = PhysicsMath.ToNumerics(linear);
        entry.KinematicAngular = PhysicsMath.ToNumerics(angularDegrees) / DegreesPerRadian;
        Wake(entry);
        WakeBodiesNear(entry);
        return true;
    }

    /// <summary>
    /// A conveyor surface: what rests on the anchored Part is carried at this velocity (units/s, world space) by friction,
    /// while the Part itself stays where it is. Zero stops it. An anchored Part without scripts becomes kinematic for
    /// this. False for a Part that is not anchored or not simulated, or a velocity that is not finite.
    /// </summary>
    public bool SetSurfaceVelocity(Part part, Vector3 velocity)
    {
        ThrowIfDisposed();
        if (!PhysicsMath.IsFinite(velocity) || !part.Anchored || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        MarkScripted(part);
        entry.SurfaceVelocity = PhysicsMath.ToNumerics(velocity);
        Wake(entry);
        WakeBodiesNear(entry);
        return true;
    }

    /// <summary>The velocities an anchored Part moves on its own with (<see cref="SetKinematicVelocity"/>); zero when it does not.</summary>
    public (Vector3 Linear, Vector3 Angular) KinematicVelocityOf(Part part) =>
        !_disposed && _entries.TryGetValue(part, out var entry)
            ? (PhysicsMath.ToWisko(entry.KinematicLinear), PhysicsMath.ToWisko(entry.KinematicAngular * DegreesPerRadian))
            : (Vector3.Zero, Vector3.Zero);

    public Vector3 SurfaceVelocityOf(Part part) =>
        !_disposed && _entries.TryGetValue(part, out var entry) ? PhysicsMath.ToWisko(entry.SurfaceVelocity) : Vector3.Zero;

    /// <summary>
    /// A force at a world point of a dynamic body for this frame (like <see cref="TryApplyForce"/>): off its centre of mass
    /// it also turns it (the torque is figured where the body is now). False for any other Part or values not finite.
    /// </summary>
    public bool TryApplyForceAtPoint(Part part, Vector3 force, Vector3 point)
    {
        if (!PhysicsMath.IsFinite(point) || !TryDynamicBody(part, force, out var body))
        {
            return false;
        }

        var arm = PhysicsMath.ToNumerics(point) - body.Pose.Position;
        var torque = NVector3.Cross(arm, PhysicsMath.ToNumerics(force));
        return AddForce(part, force, torque: false) && AddForce(part, PhysicsMath.ToWisko(torque), torque: true);
    }

    /// <summary>
    /// How fast a world point fixed to the Part moves now (its linear velocity plus its spin around its centre of mass):
    /// for a wheel's contact, a turret's tip. Zero for a static or unsimulated Part.
    /// </summary>
    public Vector3 VelocityAtPoint(Part part, Vector3 point)
    {
        if (_disposed || !_entries.TryGetValue(part, out var entry) || entry.Kind == PhysicsBodyKind.Static || !PhysicsMath.IsFinite(point))
        {
            return Vector3.Zero;
        }

        var body = _simulation.Bodies.GetBodyReference(entry.Body);
        var velocity = body.Velocity;
        if (entry.Kind == PhysicsBodyKind.Kinematic && (entry.KinematicLinear != NVector3.Zero || entry.KinematicAngular != NVector3.Zero))
        {
            velocity = new BodyVelocity(entry.KinematicLinear, entry.KinematicAngular);
        }

        var arm = PhysicsMath.ToNumerics(point) - body.Pose.Position;
        return PhysicsMath.ToWisko(velocity.Linear + NVector3.Cross(velocity.Angular, arm));
    }

    /// <summary>The mass physics uses for the Part now (its manual mass, or density x volume); null when not simulated.</summary>
    public float? EffectiveMassOf(Part part) =>
        _disposed || !_entries.TryGetValue(part, out var entry) ? null : entry.Kind == PhysicsBodyKind.Dynamic ? entry.Mass : MassOf(part, entry.Collider);

    /// <summary>
    /// Joins <paramref name="a"/> to <paramref name="b"/> (or to a fixed point in the world when b is null) with a joint
    /// of this kind at a world point (Weld: their current relative pose; Hinge: around <paramref name="worldAxis"/>).
    /// Anchored Parts in it become kinematic; at least one side must be unanchored. Null with a reason when it cannot.
    /// </summary>
    public PhysicsConstraint? CreateConstraint(PhysicsConstraintKind kind, Part a, Part? b, Vector3 worldPoint, Vector3 worldAxis, out string? error)
    {
        ThrowIfDisposed();
        error = null;
        if (!_entries.TryGetValue(a, out var entryA))
        {
            error = $"'{a.Name}' is not simulated by physics";
            return null;
        }

        Entry? entryB = null;
        if (b is not null && !_entries.TryGetValue(b, out entryB))
        {
            error = $"'{b.Name}' is not simulated by physics";
            return null;
        }

        if (entryA == entryB)
        {
            error = "a joint needs two different objects";
            return null;
        }

        if (a.Anchored && (b is null || b.Anchored))
        {
            error = "a joint needs at least one unanchored object (anchored ones do not move by physics)";
            return null;
        }

        if (!PhysicsMath.IsFinite(worldPoint) || !PhysicsMath.IsFinite(worldAxis) || (kind == PhysicsConstraintKind.Hinge && worldAxis.Length < 1e-6f))
        {
            error = "a joint needs a finite point (and, for a hinge, an axis that is not zero)";
            return null;
        }

        // Anchored sides become kinematic bodies (a joint needs a body on each side).
        if (a.Anchored)
        {
            MarkScripted(a);
        }

        if (b is { Anchored: true })
        {
            MarkScripted(b);
        }

        var poseA = _simulation.Bodies.GetBodyReference(entryA.Body).Pose;
        var point = PhysicsMath.ToNumerics(worldPoint);
        var poseB = entryB is not null
            ? _simulation.Bodies.GetBodyReference(entryB.Body).Pose
            : kind == PhysicsConstraintKind.Weld ? poseA : new RigidPose(point, NQuaternion.Identity);
        var constraint = new PhysicsConstraint(this, kind, a, b, ++_nextConstraintId, poseA, poseB, point,
            kind == PhysicsConstraintKind.Hinge ? PhysicsMath.ToNumerics(worldAxis) : NVector3.UnitY);
        _constraints.Add(constraint);
        entryA.Constraints.Add(constraint);
        entryB?.Constraints.Add(constraint);
        UpdateConstraintCollision(constraint);
        constraint.Attach(_simulation, entryA.Body, entryB?.Body);
        return constraint;
    }

    /// <summary>The world's joints still alive, in the order they were made.</summary>
    public IReadOnlyList<PhysicsConstraint> Constraints => _constraints;

    /// <summary>A hinge's angle now in degrees (0 for other kinds or one not on the solver).</summary>
    public float ConstraintAngle(PhysicsConstraint constraint)
    {
        if (_disposed || constraint.Kind != PhysicsConstraintKind.Hinge || !_entries.TryGetValue(constraint.PartA, out var entryA)
            || entryA.Kind == PhysicsBodyKind.Static)
        {
            return 0f;
        }

        var poseA = _simulation.Bodies.GetBodyReference(entryA.Body).Pose;
        RigidPose poseB;
        if (constraint.PartB is null)
        {
            poseB = constraint.AnchorPose;
        }
        else if (_entries.TryGetValue(constraint.PartB, out var entryB) && entryB.Kind != PhysicsBodyKind.Static)
        {
            poseB = _simulation.Bodies.GetBodyReference(entryB.Body).Pose;
        }
        else
        {
            return 0f;
        }

        return constraint.AngleBetween(poseA, poseB);
    }

    internal void DestroyConstraint(PhysicsConstraint constraint)
    {
        if (_disposed || constraint.IsDestroyed)
        {
            return;
        }

        constraint.Detach(_simulation);
        constraint.MarkDestroyed();
        _constraints.Remove(constraint);
        foreach (var part in new[] { constraint.PartA, constraint.PartB })
        {
            if (part is not null && _entries.TryGetValue(part, out var entry))
            {
                entry.Constraints.Remove(constraint);
                Wake(entry);
            }
        }

        UpdateConstraintCollision(constraint);
    }

    // A setting changed (enabled, motor, limits): taken off the solver and put back as it is now.
    internal void RefreshConstraint(PhysicsConstraint constraint)
    {
        if (_disposed || constraint.IsDestroyed)
        {
            return;
        }

        constraint.Detach(_simulation);
        TryAttach(constraint);
    }

    // Parts in a joint do not collide with each other (unless CollideConnected): kept as an ignored pair while it lasts.
    internal void UpdateConstraintCollision(PhysicsConstraint constraint)
    {
        if (_disposed || constraint.PartB is not { } b || !_entries.TryGetValue(constraint.PartA, out var entryA) || !_entries.TryGetValue(b, out var entryB))
        {
            return;
        }

        ApplyIgnoredPair(entryA, entryB);
    }

    // On the solver when both sides have bodies (a static side cannot hold a joint).
    private void TryAttach(PhysicsConstraint constraint)
    {
        if (!_entries.TryGetValue(constraint.PartA, out var entryA) || entryA.Removed || entryA.Kind == PhysicsBodyKind.Static)
        {
            return;
        }

        BodyHandle? bodyB = null;
        if (constraint.PartB is { } b)
        {
            if (!_entries.TryGetValue(b, out var entryB) || entryB.Removed || entryB.Kind == PhysicsBodyKind.Static)
            {
                return;
            }

            bodyB = entryB.Body;
        }

        constraint.Attach(_simulation, entryA.Body, bodyB);
    }

    // Wakes the dynamic bodies whose bounds come near the entry's (what rests on it starts moving with it).
    private void WakeBodiesNear(Entry entry)
    {
        if (!TryTargetPose(entry, out _) || !HasValidTransform(entry.Part))
        {
            return;
        }

        var bounds = PartShape.Of(entry.Part).Bounds;
        var margin = new Vector3(0.5f, 0.5f, 0.5f);
        var candidates = new List<Entry>();
        var collector = new OverlapCandidates(this, candidates);
        _simulation.BroadPhase.GetOverlaps(PhysicsMath.ToNumerics(bounds.Min - margin), PhysicsMath.ToNumerics(bounds.Max + margin), _pool, ref collector);
        foreach (var candidate in candidates)
        {
            if (candidate.Kind == PhysicsBodyKind.Dynamic)
            {
                Wake(candidate);
            }
        }
    }

    private void WakeAll()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Kind == PhysicsBodyKind.Dynamic && !entry.Removed)
            {
                Wake(entry);
            }
        }
    }

    /// <summary>Releases the simulation and all its memory. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entries.Clear();
        _kinematics.Clear();
        _forced.Clear();
        _environmentBoxes.Clear();
        _contacts.Clear();
        _endedContacts.Clear();
        _contactOrder.Clear();
        _approaching.Clear();
        _approachingBefore.Clear();
        _contactRecorder.Records.Clear();
        Array.Clear(_bodyEntries);
        Array.Clear(_staticEntries);
        _simulation.Dispose();
        _pool.Clear();
    }

    // The runtime pose of the collider: its centre (shape centre) and orientation. For checks of the transform boundary.
    internal bool TryGetColliderPose(Part part, out NVector3 position, out NQuaternion orientation)
    {
        position = default;
        orientation = default;
        if (_disposed || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        var pose = ColliderPose(entry);
        position = pose.Position;
        orientation = pose.Orientation;
        return true;
    }

    // The runtime material of a Part (friction, contacts, gravity) as the callbacks read it. For checks.
    internal bool TryGetMaterial(Part part, out float friction, out bool canCollide, out bool gravity)
    {
        friction = 0f;
        canCollide = false;
        gravity = false;
        if (_disposed || !_entries.TryGetValue(part, out var entry))
        {
            return false;
        }

        var material = entry.Kind == PhysicsBodyKind.Static
            ? _materials.Of(new CollidableReference(entry.Static))
            : _materials.Of(new CollidableReference(entry.Kind == PhysicsBodyKind.Dynamic ? CollidableMobility.Dynamic : CollidableMobility.Kinematic, entry.Body));
        (friction, canCollide, gravity) = (material.Friction, material.CanCollide, material.Gravity);
        return true;
    }

    // A dynamic body's mass and inverse inertia as the solver has them (not as the Part asks). For checks.
    internal bool TryGetBodyInertia(Part part, out float mass, out NVector3 inverseInertiaDiagonal)
    {
        mass = 0f;
        inverseInertiaDiagonal = default;
        if (_disposed || !_entries.TryGetValue(part, out var entry) || entry.Kind != PhysicsBodyKind.Dynamic)
        {
            return false;
        }

        var inertia = _simulation.Bodies.GetBodyReference(entry.Body).LocalInertia;
        mass = 1f / inertia.InverseMass;
        inverseInertiaDiagonal = new NVector3(inertia.InverseInertiaTensor.XX, inertia.InverseInertiaTensor.YY, inertia.InverseInertiaTensor.ZZ);
        return true;
    }

    // The Part's collider in the simulation as its entry's kind says (static, kinematic or dynamic body) at the Part's
    // transform; a new body starts at rest.
    private void AddCollidable(Entry entry)
    {
        var part = entry.Part;
        var collider = entry.Collider;
        var pose = PoseOf(part, collider);
        var material = MaterialOf(entry);
        switch (entry.Kind)
        {
            case PhysicsBodyKind.Static:
                entry.Static = _simulation.Statics.Add(new StaticDescription(pose, collider.Index));
                _materials.Set(entry.Static, material);
                Store(ref _staticEntries, entry.Static.Value, entry);
                StaticCount++;
                break;
            case PhysicsBodyKind.Kinematic:
                entry.Body = _simulation.Bodies.Add(BodyDescription.CreateKinematic(
                    pose,
                    new CollidableDescription(collider.Index, ContinuousDetection.Passive),
                    new BodyActivityDescription(Settings.SleepThreshold)));
                _materials.Set(entry.Body, material);
                Store(ref _bodyEntries, entry.Body.Value, entry);
                _kinematics.Add(entry);
                KinematicCount++;
                break;
            default:
                entry.Mass = MassOf(part, collider);
                entry.Body = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    pose,
                    InertiaOf(entry, collider, entry.Mass),
                    new CollidableDescription(collider.Index, ContinuousDetection.Passive),
                    new BodyActivityDescription(Settings.SleepThreshold)));
                _materials.Set(entry.Body, material);
                Store(ref _bodyEntries, entry.Body.Value, entry);
                DynamicCount++;
                break;
        }

        // Its joints (after a kind change) carry on with the new body, in the frames they were made in.
        foreach (var constraint in entry.Constraints)
        {
            if (!constraint.IsActive)
            {
                TryAttach(constraint);
            }
        }
    }

    // Takes the entry's collider out of the simulation (the entry itself, and its pairs, stay). Its joints come off the
    // solver first (a body must never leave with constraints on it); AddCollidable puts them back on the new body.
    private void RemoveCollidable(Entry entry)
    {
        foreach (var constraint in entry.Constraints)
        {
            constraint.Detach(_simulation);
        }

        switch (entry.Kind)
        {
            case PhysicsBodyKind.Static:
                _staticEntries[entry.Static.Value] = null;
                _simulation.Statics.Remove(entry.Static); // wakes the sleeping bodies around it
                StaticCount--;
                break;
            case PhysicsBodyKind.Kinematic:
                _bodyEntries[entry.Body.Value] = null;
                _kinematics.Remove(entry);
                _simulation.Bodies.Remove(entry.Body);
                KinematicCount--;
                break;
            default:
                _bodyEntries[entry.Body.Value] = null;
                _simulation.Bodies.Remove(entry.Body); // a sleeping island is woken first
                DynamicCount--;
                break;
        }
    }

    // The material the callbacks have for the entry now.
    private PhysicsMaterial CurrentMaterial(Entry entry) =>
        entry.Kind == PhysicsBodyKind.Static
            ? _materials.Of(new CollidableReference(entry.Static))
            : _materials.Of(new CollidableReference(entry.Kind == PhysicsBodyKind.Dynamic ? CollidableMobility.Dynamic : CollidableMobility.Kinematic, entry.Body));

    // The entry's pairs are over and are not picked up again: each gives its Exit (or Enter then Exit) at the next
    // collection, and a later touch of the same two Parts is a new pair.
    private void EndContactsForGood(Entry entry)
    {
        List<long>? ended = null;
        foreach (var (key, contact) in _contacts)
        {
            if (contact.Part == entry || contact.Other == entry)
            {
                contact.Touching = false;
                contact.Ended = true;
                _endedContacts.Add(contact);
                (ended ??= []).Add(key);
            }
        }

        if (ended is not null)
        {
            foreach (var key in ended)
            {
                _contacts.Remove(key);
            }
        }
    }

    private bool AddForce(Part part, Vector3 value, bool torque)
    {
        if (!TryDynamicBody(part, value, out _))
        {
            return false;
        }

        var entry = _entries[part];
        if (!entry.Forced)
        {
            entry.Forced = true;
            _forced.Add(entry);
        }

        if (torque)
        {
            entry.Torque += PhysicsMath.ToNumerics(value);
        }
        else
        {
            entry.Force += PhysicsMath.ToNumerics(value);
        }

        return true;
    }

    // This frame's forces become pending impulses: force x the frame's time (as physics counts it).
    private void CollectForces(float frameSeconds)
    {
        foreach (var entry in _forced)
        {
            entry.PendingLinear += entry.Force * frameSeconds;
            entry.PendingAngular += entry.Torque * frameSeconds;
            entry.Force = NVector3.Zero;
            entry.Torque = NVector3.Zero;
        }
    }

    // One step's part of each body's pending impulses (a fraction of what was pending when Advance began).
    private void ApplyPendingImpulses(float fraction)
    {
        foreach (var entry in _forced)
        {
            if (entry.Removed || entry.Kind != PhysicsBodyKind.Dynamic)
            {
                continue;
            }

            var body = _simulation.Bodies.GetBodyReference(entry.Body);
            if (entry.PendingLinear != NVector3.Zero || entry.PendingAngular != NVector3.Zero)
            {
                body.Awake = true;
                body.ApplyLinearImpulse(entry.PendingLinear * fraction);
                body.ApplyAngularImpulse(entry.PendingAngular * fraction);
            }
        }
    }

    // After the steps: what was handed out is gone; with time still waiting for a step, the rest of the push waits
    // with it, otherwise (no time left, or the time was dropped) it is gone too.
    private void KeepPendingImpulses(float handedOut, bool timeLeft)
    {
        for (var i = _forced.Count - 1; i >= 0; i--)
        {
            var entry = _forced[i];
            if (timeLeft && handedOut < 1f && !entry.Removed && entry.Kind == PhysicsBodyKind.Dynamic)
            {
                entry.PendingLinear *= 1f - handedOut;
                entry.PendingAngular *= 1f - handedOut;
                continue;
            }

            ClearForces(entry);
        }
    }

    private void ClearForces(Entry entry)
    {
        entry.Force = entry.Torque = entry.PendingLinear = entry.PendingAngular = NVector3.Zero;
        if (entry.Forced)
        {
            entry.Forced = false;
            _forced.Remove(entry);
        }
    }

    // The Parts (in the order added) whose exact shape overlaps the query shape, and whether it reaches the environment.
    private PhysicsOverlapResult Overlap(PartShape query, PhysicsQueryFilter? filter, int maxParts)
    {
        var bounds = query.Bounds;
        var environment = false;
        foreach (var box in filter is { IgnoreEnvironment: true } ? Array.Empty<PhysicsEnvironmentBox>() : (IReadOnlyList<PhysicsEnvironmentBox>)_environmentBoxes)
        {
            var boxShape = PartShape.Box(box.Center, box.Size, Vector3.Zero);
            if (bounds.Overlaps(boxShape.Bounds) && PartShape.Overlap(query, boxShape))
            {
                environment = true;
                break;
            }
        }

        // Candidates from the broad phase (static and dynamic bodies are where their Parts are), kinematic ones where
        // their Parts are now (their bodies follow only at the next Advance); then each one's exact shape.
        var candidates = new List<Entry>();
        var collector = new OverlapCandidates(this, candidates);
        _simulation.BroadPhase.GetOverlaps(PhysicsMath.ToNumerics(bounds.Min), PhysicsMath.ToNumerics(bounds.Max), _pool, ref collector);
        candidates.AddRange(_kinematics);
        var found = new List<(int Sequence, Part Part)>();
        foreach (var entry in candidates)
        {
            var part = entry.Part;
            if (entry.Removed || !Accepts(filter, entry, CurrentMaterial(entry)) || !HasValidTransform(part))
            {
                continue;
            }

            var shape = PartShape.Of(part);
            if (bounds.Overlaps(shape.Bounds) && PartShape.Overlap(query, shape))
            {
                found.Add((entry.Sequence, part));
            }
        }

        found.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        var parts = new List<Part>(global::System.Math.Min(found.Count, global::System.Math.Max(maxParts, 0)));
        for (var i = 0; i < found.Count && parts.Count < maxParts; i++)
        {
            parts.Add(found[i].Part);
        }

        return new PhysicsOverlapResult(parts, environment);
    }

    // Gravity and damping only matter for dynamic bodies (static and kinematic ones are never integrated).
    private static PhysicsMaterial MaterialOf(Entry entry)
    {
        var dynamic = entry.Kind == PhysicsBodyKind.Dynamic;
        return new(entry.Part.Friction, entry.Part.CanCollide, Gravity: !dynamic || entry.Part.GravityEnabled,
            entry.Part.IsTrigger, IsEnvironment: false, entry.ReportsContacts, entry.CollisionGroup, entry.Layer, entry.Sequence,
            dynamic ? entry.GravityScale : 1f, dynamic ? entry.LinearDamping : 0f, dynamic ? entry.AngularDamping : 0f);
    }

    // A dynamic body's inertia for its mass; a rotation-locked one has none to turn with (infinite inertia).
    private static BodyInertia InertiaOf(Entry entry, PhysicsCollider collider, float mass)
    {
        var inertia = collider.InertiaFor(mass);
        if (entry.RotationLocked)
        {
            inertia.InverseInertiaTensor = default;
        }

        return inertia;
    }

    private void SetMaterial(Entry entry)
    {
        if (entry.Kind == PhysicsBodyKind.Static)
        {
            _materials.Set(entry.Static, MaterialOf(entry));
        }
        else
        {
            _materials.Set(entry.Body, MaterialOf(entry));
        }
    }

    // The Part's entry behind a runtime collidable; null for the environment.
    private Entry? EntryOf(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static ? At(_staticEntries, collidable.StaticHandle.Value) : At(_bodyEntries, collidable.BodyHandle.Value);

    private static Entry? At(Entry?[] table, int index) => index >= 0 && index < table.Length ? table[index] : null;

    private static void Store(ref Entry?[] table, int index, Entry entry)
    {
        if (index >= table.Length)
        {
            Array.Resize(ref table, global::System.Math.Max(table.Length * 2, index + 1));
        }

        table[index] = entry;
    }

    // What a ray can hit: the environment, triggers and Parts that collide (a Part that does neither is passed through).
    private static bool RayHits(PhysicsMaterial material) => material.IsEnvironment || material.IsTrigger || material.CanCollide;

    // Whether a query takes this collidable (entry null: the environment): what a ray can hit at all, then the filter.
    private bool Accepts(PhysicsQueryFilter? filter, Entry? entry, PhysicsMaterial material)
    {
        if (!RayHits(material))
        {
            return false;
        }

        if (filter is null)
        {
            return true;
        }

        // As a body of that collision group would: only what its group touches.
        if (filter.Layer is { } layer && !_materials.LayersCollide(layer, material.Layer))
        {
            return false;
        }

        if (entry is null)
        {
            return !filter.IgnoreEnvironment;
        }

        return !ReferenceEquals(entry.Part, filter.Ignore)
            && !(filter.IgnoreTriggers && material.IsTrigger)
            && (filter.AcceptPart?.Invoke(entry.Part) ?? true);
    }

    private static PhysicsQueryFilter? FilterOf(Part? ignore) => ignore is null ? null : new PhysicsQueryFilter { Ignore = ignore };

    // After a step: the pairs the step found touching (within the gap) are touching; a pair it did not find has stopped,
    // unless every body in it is asleep (the simulation does not test sleeping pairs, so their contact stands).
    private void UpdateContacts()
    {
        _contactStep++;
        (_approachingBefore, _approaching) = (_approaching, _approachingBefore);
        _approaching.Clear();
        var records = _contactRecorder.Records;
        foreach (var record in records)
        {
            var (pair, depth, trigger) = (record.Pair, record.Depth, record.Trigger);
            var a = EntryOf(pair.A);
            var b = EntryOf(pair.B);
            if (a is null && b is null)
            {
                continue;
            }

            // The Part added first leads the pair; the environment is only ever the other side.
            var (first, other) = a is null ? (b!, (Entry?)null)
                : b is null ? (a, null)
                : a.Sequence < b.Sequence ? (a, b) : (b, a);
            var key = ContactKey(first, other);
            if (!_contacts.TryGetValue(key, out var contact))
            {
                if (depth < -ContactBeginGap)
                {
                    _approaching[key] = first == a ? record.RelativeVelocity : -record.RelativeVelocity;
                    continue;
                }

                contact = new ContactPair(key, first, other, trigger);
                _contacts.Add(key, contact);
            }

            if (depth >= -(contact.Touching ? ContactEndGap : ContactBeginGap))
            {
                contact.SeenStep = _contactStep;

                // BEPU's normal points from B to A and the velocity is A's relative to B: as the leading Part receives
                // them, both turn round when the leading Part is B. A trigger overlap has no contact surface.
                var point = new PhysicsContactPoint(PhysicsMath.ToWisko(record.Point), trigger ? null : PhysicsMath.ToWisko(record.Normal),
                    PhysicsMath.ToWisko(record.RelativeVelocity));
                contact.Latest = first == a ? point : point.Flipped;
            }
        }

        records.Clear();
        foreach (var contact in _contacts.Values)
        {
            if (contact.SeenStep == _contactStep)
            {
                if (!contact.Touching)
                {
                    contact.Touching = true;
                    contact.Began = true;
                    contact.EnterContact = contact.Latest;

                    // It touched at the speed it approached with, before the solver began slowing it (the faster of the
                    // last approaching step and this one).
                    if (contact.Latest is { } latest && _approachingBefore.TryGetValue(contact.Key, out var approach)
                        && approach.LengthSquared() > PhysicsMath.ToNumerics(latest.RelativeVelocity).LengthSquared())
                    {
                        contact.EnterContact = latest with { RelativeVelocity = PhysicsMath.ToWisko(approach) };
                    }
                }
            }
            else if (contact.Touching && !(IsResting(contact.Part) && (contact.Other is null || IsResting(contact.Other))))
            {
                contact.Touching = false;
            }
        }
    }

    // Pairs order by the Parts' order of addition (environment = 0, before any Part).
    private static long ContactKey(Entry first, Entry? other) => ((long)first.Sequence << 32) | (uint)(other?.Sequence ?? 0);

    private bool IsResting(Entry entry) =>
        !entry.Removed && (entry.Kind == PhysicsBodyKind.Static || !_simulation.Bodies.GetBodyReference(entry.Body).Awake);

    private void Wake(Entry entry)
    {
        if (!entry.Removed && entry.Kind != PhysicsBodyKind.Static)
        {
            var body = _simulation.Bodies.GetBodyReference(entry.Body);
            body.Awake = true;
        }
    }

    // The Part changed (moved, resized, settings): whatever touches it is tested again at the next step.
    private void WakeContactPartners(Entry entry)
    {
        if (_contacts.Count == 0)
        {
            return;
        }

        foreach (var contact in _contacts.Values)
        {
            if (contact.Touching && (contact.Part == entry || contact.Other == entry))
            {
                Wake(contact.Part);
                if (contact.Other is { } other)
                {
                    Wake(other);
                }
            }
        }
    }

    // Moves an anchored Part by its own velocity over `duration` seconds (position, and rotation around the world axes).
    private static void MoveKinematicPart(Entry entry, float duration)
    {
        var part = entry.Part;
        if (!HasValidTransform(part))
        {
            return;
        }

        if (entry.KinematicLinear != NVector3.Zero)
        {
            part.Position = PhysicsMath.ToWisko(PhysicsMath.ToNumerics(part.Position) + entry.KinematicLinear * duration);
        }

        var angular = entry.KinematicAngular;
        var angle = angular.Length() * duration;
        if (angle > 0f)
        {
            var turn = NQuaternion.CreateFromAxisAngle(NVector3.Normalize(angular), angle);
            var orientation = NQuaternion.Normalize(turn * PhysicsMath.EulerDegreesToQuaternion(part.Rotation));
            part.Rotation = PhysicsMath.QuaternionToEulerDegrees(orientation);
        }
    }

    // Before each step: a surface Part stays where it is (put back where its Part is, at the step's share of its
    // movement) while its body has its surface velocity added, so what rests on it is carried by friction exactly as
    // by a moving surface, without the Part going anywhere.
    private void PrepareSurfaces(int stepIndex, int steps)
    {
        foreach (var entry in _kinematics)
        {
            if (entry.SurfaceVelocity == NVector3.Zero || !TryTargetPose(entry, out var target))
            {
                continue;
            }

            var body = _simulation.Bodies.GetBodyReference(entry.Body);
            var driven = entry.DrivenVelocity;
            if (driven.Linear == NVector3.Zero && driven.Angular == NVector3.Zero)
            {
                body.Pose = target;
            }
            else
            {
                // It also moves this frame: where it is due at the start of this step.
                var remaining = (steps - stepIndex) * Settings.FixedTimestep;
                body.Pose = new RigidPose(target.Position - driven.Linear * remaining, body.Pose.Orientation);
            }

            body.Awake = true;
            body.Velocity = new BodyVelocity(driven.Linear + entry.SurfaceVelocity, driven.Angular);
            _simulation.Bodies.UpdateBounds(entry.Body);
        }
    }

    // Sets the kinematic body's velocities so it reaches its Part's transform after `duration` seconds of steps.
    private void DriveKinematic(Entry entry, float duration)
    {
        if (!TryTargetPose(entry, out var target))
        {
            return;
        }

        var body = _simulation.Bodies.GetBodyReference(entry.Body);
        var current = body.Pose;
        if (current.Position == target.Position && current.Orientation == target.Orientation)
        {
            if (body.Velocity.Linear != NVector3.Zero || body.Velocity.Angular != NVector3.Zero)
            {
                body.Velocity = default; // stopped moving: at rest (and free to sleep)
            }

            return;
        }

        var linear = (target.Position - current.Position) / duration;

        // The shortest turn from the current orientation to the target, as an angular velocity.
        var turn = target.Orientation * NQuaternion.Conjugate(current.Orientation);
        if (turn.W < 0f)
        {
            turn = NQuaternion.Negate(turn);
        }

        var axisLength = MathF.Sqrt(turn.X * turn.X + turn.Y * turn.Y + turn.Z * turn.Z);
        var angle = 2f * MathF.Atan2(axisLength, turn.W);
        var angular = axisLength > 1e-9f ? new NVector3(turn.X, turn.Y, turn.Z) / axisLength * (angle / duration) : NVector3.Zero;

        body.Awake = true;
        if (linear.Length() > Settings.MaxKinematicSpeed)
        {
            // Relocated rather than moved: jump there at rest.
            body.Pose = target;
            body.Velocity = default;
            _simulation.Bodies.UpdateBounds(entry.Body);
            return;
        }

        body.Velocity = new BodyVelocity(linear, angular);
    }

    // The woken body of a dynamic Part, for a finite value to be written to it.
    private bool TryDynamicBody(Part part, Vector3 value, out BodyReference body)
    {
        ThrowIfDisposed();
        body = default;
        if (!_entries.TryGetValue(part, out var entry) || entry.Kind != PhysicsBodyKind.Dynamic || !PhysicsMath.IsFinite(value))
        {
            return false;
        }

        body = _simulation.Bodies.GetBodyReference(entry.Body);
        body.Awake = true;
        return true;
    }

    // A new Size: the collider is rebuilt for this Part only (shape, centre offset, and for a dynamic body its mass and
    // inertia: an Auto mass follows the new volume, a manual mass stays and the inertia is recomputed for the new
    // shape), keeping its kind, its material, its Part's current pose and, for a moving body, its velocity.
    private void RebuildCollider(Entry entry)
    {
        var part = entry.Part;
        var collider = _colliders.Get(part.ShapeType, part.Size);
        entry.Collider = collider;
        var pose = PoseOf(part, collider);
        switch (entry.Kind)
        {
            case PhysicsBodyKind.Static:
                _simulation.Statics.ApplyDescription(entry.Static, new StaticDescription(pose, collider.Index));
                break;
            case PhysicsBodyKind.Kinematic:
                _simulation.Bodies.SetShape(entry.Body, collider.Index);
                _simulation.Bodies.GetBodyReference(entry.Body).Pose = pose;
                _simulation.Bodies.UpdateBounds(entry.Body);
                break;
            default:
                entry.Mass = MassOf(entry.Part, collider);
                _simulation.Bodies.SetShape(entry.Body, collider.Index);
                _simulation.Bodies.SetLocalInertia(entry.Body, InertiaOf(entry, collider, entry.Mass));
                _simulation.Bodies.GetBodyReference(entry.Body).Pose = pose;
                _simulation.Bodies.UpdateBounds(entry.Body);
                break;
        }
    }

    // The Part's manual mass, or on Auto density x its collider's volume.
    private float MassOf(Part part, PhysicsCollider collider) => part.MassOverride ?? Settings.Density * collider.Volume;

    private bool TryTargetPose(Entry entry, out RigidPose target)
    {
        target = default;
        if (!HasValidTransform(entry.Part))
        {
            return false;
        }

        target = PoseOf(entry.Part, entry.Collider);
        return true;
    }

    // The collider's pose for a Part's transform: its orientation, and its centre (the Part's origin plus the shape's
    // centre offset turned with it).
    private static RigidPose PoseOf(Part part, PhysicsCollider collider)
    {
        var orientation = PhysicsMath.EulerDegreesToQuaternion(part.Rotation);
        return new RigidPose(PhysicsMath.ToNumerics(part.Position) + NVector3.Transform(collider.Center, orientation), orientation);
    }

    private static bool HasValidTransform(Part part) =>
        PhysicsMath.IsFinite(part.Position) && PhysicsMath.IsFinite(part.Rotation) && PhysicsMath.IsFinite(part.Size)
        && part.Size.X > 0f && part.Size.Y > 0f && part.Size.Z > 0f;

    private RigidPose ColliderPose(Entry entry) =>
        entry.Kind == PhysicsBodyKind.Static
            ? _simulation.Statics.GetStaticReference(entry.Static).Pose
            : _simulation.Bodies.GetBodyReference(entry.Body).Pose;

    private bool TryPartPose(Entry entry, out Vector3 position, out Vector3 rotationDegrees)
    {
        var pose = ColliderPose(entry);
        var origin = pose.Position - NVector3.Transform(entry.Collider.Center, pose.Orientation);
        position = PhysicsMath.ToWisko(origin);
        rotationDegrees = default;
        if (!PhysicsMath.IsFinite(origin) || !PhysicsMath.IsFinite(pose.Orientation))
        {
            return false;
        }

        rotationDegrees = PhysicsMath.QuaternionToEulerDegrees(pose.Orientation);
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Validate(PhysicsSettings settings)
    {
        if (!PhysicsMath.IsFinite(settings.Gravity) || !(settings.FixedTimestep > 0f) || !float.IsFinite(settings.FixedTimestep)
            || !(settings.MaxFrameTime > 0f) || !float.IsFinite(settings.MaxFrameTime) || settings.MaxStepsPerFrame < 1
            || settings.Substeps < 1 || settings.VelocityIterations < 1 || !(settings.Density > 0f) || !float.IsFinite(settings.Density)
            || !(settings.Friction >= 0f) || !(settings.ContactFrequency > 0f) || !(settings.ContactDampingRatio >= 0f)
            || !(settings.MaximumRecoveryVelocity >= 0f) || !(settings.SleepThreshold >= 0f) || !(settings.MaxKinematicSpeed > 0f))
        {
            throw new ArgumentException("Physics settings must be finite and positive.", nameof(settings));
        }
    }

    private sealed class Entry(Part part, PhysicsBodyKind kind, PhysicsCollider collider, int sequence)
    {
        public Part Part { get; } = part;

        // Changes with the Part's Anchored setting while playing (see ApplyKindChange).
        public PhysicsBodyKind Kind { get; set; } = kind;

        // Scripts run on the Part (from Play's start, or attached while playing): anchored, it is kinematic.
        public bool Scripted { get; set; }

        // Order of addition (from 1, never reused): orders contact pairs.
        public int Sequence { get; } = sequence;

        // A script listens to this Part's contacts (see SetReportsContacts).
        public bool ReportsContacts { get; set; }

        // Physics never turns it (infinite inertia; see SetRotationLocked). Kept across kind changes.
        public bool RotationLocked { get; set; }

        // Its attachment group (Parts parented together never touch; see SetCollisionGroup); 0: none.
        public int CollisionGroup { get; set; }

        // Its collision group (layer) index (0: Default; see SetCollisionLayer).
        public int Layer { get; set; }

        // Multiplies the world's gravity while it has gravity (see SetGravityScale); damping per second.
        public float GravityScale { get; set; } = 1f;

        public float LinearDamping { get; set; }

        public float AngularDamping { get; set; }

        // An anchored Part moving on its own (see SetKinematicVelocity): units/s and radians/s around the world axes.
        public NVector3 KinematicLinear { get; set; }

        public NVector3 KinematicAngular { get; set; }

        // A surface that carries what rests on it without moving itself (see SetSurfaceVelocity), units/s.
        public NVector3 SurfaceVelocity { get; set; }

        // The velocity DriveKinematic gave it for this frame's steps (a surface's is added to it each step).
        public BodyVelocity DrivenVelocity { get; set; }

        // Joints it is in (see CreateConstraint).
        public List<PhysicsConstraint> Constraints { get; } = [];

        // Taken out of the world (RemovePart); its handles may already belong to another Part.
        public bool Removed { get; set; }

        public PhysicsCollider Collider { get; set; } = collider;

        public float Mass { get; set; }

        public BodyHandle Body { get; set; }

        public StaticHandle Static { get; set; }

        // The last SyncToParts already saw this body asleep (its pose is unchanged until something wakes it).
        public bool SyncedAsleep { get; set; }

        // In the world's forced list; this frame's summed force and torque; impulses from earlier forces not yet handed out.
        public bool Forced { get; set; }

        public NVector3 Force { get; set; }

        public NVector3 Torque { get; set; }

        public NVector3 PendingLinear { get; set; }

        public NVector3 PendingAngular { get; set; }
    }

    // One tracked pair: a Part and another Part or the environment (null).
    private sealed class ContactPair(long key, Entry part, Entry? other, bool isTrigger)
    {
        public long Key { get; } = key;

        public Entry Part { get; } = part;

        public Entry? Other { get; } = other;

        public bool IsTrigger { get; } = isTrigger;

        // Touching after the last step; touching when events were last collected; started touching since then.
        public bool Touching { get; set; }

        public bool WasTouching { get; set; }

        public bool Began { get; set; }

        // The last step that found it touching.
        public long SeenStep { get; set; }

        // Over for good (EndContactsForGood): reports what is left at the next collection, then goes.
        public bool Ended { get; set; }

        // The latest step's contact and the contact of the step it began touching in, as Part receives them.
        public PhysicsContactPoint? Latest { get; set; }

        public PhysicsContactPoint? EnterContact { get; set; }
    }

    // The nearest hit among what a ray may hit (kinematic Parts are tested separately, where their Parts are).
    private struct RayHitHandler(PhysicsWorld world, PhysicsQueryFilter? filter, float maxDistance) : IRayHitHandler
    {
        public float Distance = maxDistance;
        public NVector3 Normal;
        public Entry? Entry;
        public bool Environment;
        public bool Found;

        public readonly bool AllowTest(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Kinematic)
            {
                return false;
            }

            var entry = world.EntryOf(collidable);
            return world.Accepts(filter, entry, world._materials.Of(collidable));
        }

        public readonly bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, NVector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t >= Distance && Found)
            {
                return;
            }

            Distance = t;
            maximumT = t;
            Normal = normal;
            Entry = world.EntryOf(collidable);
            Environment = Entry is null;
            Found = true;
        }
    }

    private struct ShapeRayHitHandler(float maxDistance) : IShapeRayHitHandler
    {
        public float Distance = maxDistance;
        public NVector3 Normal;
        public bool Found;

        public readonly bool AllowTest(int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, NVector3 normal, int childIndex)
        {
            if (t < Distance || !Found)
            {
                Distance = t;
                maximumT = t;
                Normal = normal;
                Found = true;
            }
        }
    }

    // Every hit along a ray (kinematic Parts are tested separately, where their Parts are): each collidable once.
    private readonly struct RayAllHitHandler(PhysicsWorld world, PhysicsQueryFilter? filter, List<(float Distance, int Order, Entry? Entry, NVector3 Normal)> hits) : IRayHitHandler
    {
        public bool AllowTest(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Kinematic)
            {
                return false;
            }

            var entry = world.EntryOf(collidable);
            return world.Accepts(filter, entry, world._materials.Of(collidable));
        }

        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, NVector3 normal, CollidableReference collidable, int childIndex)
        {
            var entry = world.EntryOf(collidable);
            hits.Add((t, entry?.Sequence ?? 0, entry, normal));
        }
    }

    // The first collider a swept sphere meets (kinematic Parts are tested separately, where their Parts are). One it
    // already overlaps at the start is not a hit.
    private struct SweepHitHandler(PhysicsWorld world, PhysicsQueryFilter? filter, float maxDistance) : ISweepHitHandler
    {
        public float Distance = maxDistance;
        public NVector3 Point;
        public NVector3 Normal;
        public Entry? Entry;
        public bool Environment;
        public bool Found;

        public readonly bool AllowTest(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Kinematic)
            {
                return false;
            }

            var entry = world.EntryOf(collidable);
            return world.Accepts(filter, entry, world._materials.Of(collidable));
        }

        public readonly bool AllowTest(CollidableReference collidable, int child) => true;

        public void OnHit(ref float maximumT, float t, NVector3 hitLocation, NVector3 hitNormal, CollidableReference collidable)
        {
            if (Found && t >= Distance)
            {
                return;
            }

            Distance = t;
            maximumT = t;
            Point = hitLocation;
            Normal = hitNormal;
            Entry = world.EntryOf(collidable);
            Environment = Entry is null;
            Found = true;
        }

        public readonly void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
        }
    }

    // The broad phase's Parts whose bounds meet a query box (kinematic ones are added separately; the environment is
    // tested on its own).
    private readonly struct OverlapCandidates(PhysicsWorld world, List<Entry> found) : BepuUtilities.IBreakableForEach<CollidableReference>
    {
        public bool LoopBody(CollidableReference collidable)
        {
            if (collidable.Mobility != CollidableMobility.Kinematic && world.EntryOf(collidable) is { } entry)
            {
                found.Add(entry);
            }

            return true;
        }
    }

    // Convex colliders only: every child pair may be tested.
    private readonly struct AllChildren : ISweepFilter
    {
        public bool AllowTest(int childA, int childB) => true;
    }
}
