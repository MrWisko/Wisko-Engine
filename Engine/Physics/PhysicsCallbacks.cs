using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Physics;

/// <summary>
/// What a collidable is made of, as the callbacks see it: its friction, whether it makes contacts, (for a dynamic body)
/// whether gravity pulls it, whether it is a trigger (overlaps only, never a contact response), whether it is the
/// environment (e.g. the baseplate) rather than a Part, whether a script listens to its contacts (only then are
/// they recorded), and its attachment group (Parts parented together; 0: none): two collidables of one group never touch.
/// Layer: its collision group (0: Default; <see cref="PhysicsMaterials.LayersCollide"/> says which pairs of groups touch).
/// Id: its Part's sequence in the world (0: the environment), for pairs told never to touch. GravityScale multiplies
/// the world's gravity for a dynamic body that has gravity; LinearDamping / AngularDamping (per second) slow it.
/// </summary>
internal readonly record struct PhysicsMaterial(float Friction, bool CanCollide, bool Gravity, bool IsTrigger = false, bool IsEnvironment = false, bool ReportsContacts = false,
    int Group = 0, int Layer = 0, int Id = 0, float GravityScale = 1f, float LinearDamping = 0f, float AngularDamping = 0f)
{
    /// <summary>Anything the integrator must do beyond full gravity (no gravity, another scale, damping).</summary>
    public bool CustomIntegration => !Gravity || GravityScale != 1f || LinearDamping > 0f || AngularDamping > 0f;
}

/// <summary>
/// One recorded pair of one step: the two collidables, the depth of its deepest contact (negative: still apart by that
/// much), whether it is a trigger pair, and that contact as the narrow phase found it before the step moved anything:
/// its world point, its normal (from B toward A, as BEPU gives it) and how fast A moves relative to B at that point.
/// </summary>
internal readonly record struct PhysicsContactRecord(CollidablePair Pair, float Depth, bool Trigger, NVector3 Point, NVector3 Normal, NVector3 RelativeVelocity);

/// <summary>
/// The contacts and overlaps one step found for the pairs scripts listen to (see <see cref="PhysicsContactRecord"/>).
/// Written by the narrow-phase callback during the step and read by the world right after it; nothing else happens in
/// the callback (no Part, no script, no allocation once the list has grown). The world steps on one thread (no thread
/// dispatcher), so one list serves.
/// </summary>
internal sealed class PhysicsContactRecorder
{
    public readonly List<PhysicsContactRecord> Records = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(in PhysicsContactRecord record) => Records.Add(record);
}

/// <summary>
/// Each collidable's <see cref="PhysicsMaterial"/>, by runtime handle (bodies and statics apart). The world fills it as
/// it adds collidables, never during a step, so the callbacks only read it: plain arrays, no allocation per contact.
/// </summary>
internal sealed class PhysicsMaterials
{
    private PhysicsMaterial[] _bodies = new PhysicsMaterial[64];
    private PhysicsMaterial[] _statics = new PhysicsMaterial[64];

    /// <summary>Collision groups (layers) there can be: Default and 31 more.</summary>
    public const int MaxLayers = 32;

    private readonly uint[] _layerMasks = Enumerable.Repeat(uint.MaxValue, MaxLayers).ToArray();
    private readonly HashSet<long> _ignoredPairs = [];

    /// <summary>The world's gravity (units/s²), read by the integrator at the start of every step (scripts may change it).</summary>
    public NVector3 Gravity;

    /// <summary>
    /// A dynamic body with custom integration exists (no gravity, another gravity scale, damping): until then
    /// integration skips the per-body lookup.
    /// </summary>
    public bool AnyCustomIntegration { get; private set; }

    /// <summary>Some pair of Parts was told never to touch (until then the pair test skips the lookup).</summary>
    public int IgnoredPairCount => _ignoredPairs.Count;

    public void Set(BodyHandle handle, PhysicsMaterial material)
    {
        Store(ref _bodies, handle.Value, material);
        AnyCustomIntegration |= material.CustomIntegration;
    }

    public void Set(StaticHandle handle, PhysicsMaterial material) => Store(ref _statics, handle.Value, material);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PhysicsMaterial Of(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static ? _statics[collidable.StaticHandle.Value] : _bodies[collidable.BodyHandle.Value];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly PhysicsMaterial OfBody(BodyHandle handle) => ref _bodies[handle.Value];

    /// <summary>Whether collision groups <paramref name="a"/> and <paramref name="b"/> touch (symmetric).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LayersCollide(int a, int b) => (_layerMasks[a] & (1u << b)) != 0;

    public void SetLayersCollide(int a, int b, bool collide)
    {
        if (collide)
        {
            _layerMasks[a] |= 1u << b;
            _layerMasks[b] |= 1u << a;
        }
        else
        {
            _layerMasks[a] &= ~(1u << b);
            _layerMasks[b] &= ~(1u << a);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsIgnored(int idA, int idB) => _ignoredPairs.Contains(PairKey(idA, idB));

    /// <summary>Adds or removes a pair of Parts (by their sequence) that never touch; true when it changed.</summary>
    public bool SetIgnored(int idA, int idB, bool ignored) =>
        ignored ? _ignoredPairs.Add(PairKey(idA, idB)) : _ignoredPairs.Remove(PairKey(idA, idB));

    /// <summary>Forgets every ignored pair a Part (by its sequence) was in: it left the world.</summary>
    public void ForgetIgnored(int id) => _ignoredPairs.RemoveWhere(key => (int)(key >> 32) == id || (int)key == id);

    private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static void Store(ref PhysicsMaterial[] table, int index, PhysicsMaterial material)
    {
        if (index >= table.Length)
        {
            Array.Resize(ref table, global::System.Math.Max(table.Length * 2, index + 1));
        }

        table[index] = material;
    }
}

/// <summary>
/// Contact rules for every pair: no contacts between two collidables that can never move (static/kinematic pairs) or
/// with a collidable that cannot collide; friction combined from both sides' materials; the world's contact softness.
/// A pair with a trigger in it is tested for overlap only (no contact constraint, so nothing pushes): between Parts
/// only (never the environment), whatever the trigger's Can Collide (a Part that is neither a trigger nor collides takes
/// part in nothing), and only when a script listens to one side. Pairs scripts listen to are recorded for the world
/// (<see cref="PhysicsContactRecorder"/>); materials are only read.
/// </summary>
internal struct PhysicsNarrowPhaseCallbacks(SpringSettings contactSpring, float maximumRecoveryVelocity, PhysicsMaterials materials, PhysicsContactRecorder contacts) : INarrowPhaseCallbacks
{
    private Simulation _simulation = null!; // set by Initialize, before any step

    public void Initialize(Simulation simulation)
    {
        _simulation = simulation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        var materialA = materials.Of(a);
        var materialB = materials.Of(b);

        // Parts parented together (object:SetParent) never touch each other: a held tool does not push its holder.
        if (materialA.Group != 0 && materialA.Group == materialB.Group)
        {
            return false;
        }

        // Collision groups that do not touch, and pairs told never to touch (triggers included: no overlap either).
        if (!materials.LayersCollide(materialA.Layer, materialB.Layer)
            || (materials.IgnoredPairCount > 0 && materialA.Id != 0 && materialB.Id != 0 && materials.IsIgnored(materialA.Id, materialB.Id)))
        {
            return false;
        }

        if (materialA.IsTrigger || materialB.IsTrigger)
        {
            return !materialA.IsEnvironment && !materialB.IsEnvironment
                && (materialA.IsTrigger || materialA.CanCollide) && (materialB.IsTrigger || materialB.CanCollide)
                && (materialA.ReportsContacts || materialB.ReportsContacts);
        }

        return (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic) && materialA.CanCollide && materialB.CanCollide;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var materialA = materials.Of(pair.A);
        var materialB = materials.Of(pair.B);
        var trigger = materialA.IsTrigger || materialB.IsTrigger;
        if ((materialA.ReportsContacts || materialB.ReportsContacts) && manifold.Count > 0)
        {
            // The deepest contact (the first of equally deep ones) stands for the pair.
            var deepest = 0;
            var depth = manifold.GetDepth(0);
            for (var i = 1; i < manifold.Count; i++)
            {
                var contactDepth = manifold.GetDepth(i);
                if (contactDepth > depth)
                {
                    depth = contactDepth;
                    deepest = i;
                }
            }

            manifold.GetContact(deepest, out var offset, out var normal, out _, out _);
            var (positionA, linearA, angularA) = MotionOf(pair.A);
            var (positionB, linearB, angularB) = MotionOf(pair.B);
            var point = positionA + offset;
            var relative = linearA + NVector3.Cross(angularA, offset) - (linearB + NVector3.Cross(angularB, point - positionB));
            contacts.Record(new PhysicsContactRecord(pair, depth, trigger, point, normal, relative));
        }

        if (trigger)
        {
            // Overlap only: no constraint, so a trigger never pushes or stops anything.
            pairMaterial = default;
            return false;
        }

        pairMaterial.FrictionCoefficient = PhysicsWorld.CombineFriction(materialA.Friction, materialB.Friction);
        pairMaterial.MaximumRecoveryVelocity = maximumRecoveryVelocity;
        pairMaterial.SpringSettings = contactSpring;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    // Where a collidable is and how it moves as the narrow phase sees it (before this step's solve): statics stand still.
    private readonly (NVector3 Position, NVector3 Linear, NVector3 Angular) MotionOf(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Static)
        {
            return (_simulation.Statics[collidable.StaticHandle].Pose.Position, NVector3.Zero, NVector3.Zero);
        }

        var body = _simulation.Bodies[collidable.BodyHandle];
        return (body.Pose.Position, body.Velocity.Linear, body.Velocity.Angular);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Velocity integration for dynamic bodies: the world's gravity (<see cref="PhysicsMaterials.Gravity"/>, read at the
/// start of every step) times each body's gravity scale, for those whose material has gravity, then each body's damping
/// (velocity x e^(-damping x dt)). A body without gravity or damping keeps its velocity as it is.
/// </summary>
internal struct PhysicsPoseIntegratorCallbacks(PhysicsMaterials materials) : IPoseIntegratorCallbacks
{
    private Vector3Wide _gravity;
    private Bodies _bodies = null!; // set by Initialize, before any step

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

    public readonly bool AllowSubstepsForUnconstrainedBodies => false;

    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation)
    {
        _gravity = Vector3Wide.Broadcast(materials.Gravity);
        _bodies = simulation.Bodies;
    }

    public void PrepareForIntegration(float dt)
    {
        _gravity = Vector3Wide.Broadcast(materials.Gravity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void IntegrateVelocity(
        System.Numerics.Vector<int> bodyIndices,
        Vector3Wide position,
        QuaternionWide orientation,
        BodyInertiaWide localInertia,
        System.Numerics.Vector<int> integrationMask,
        int workerIndex,
        System.Numerics.Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        // dt is this (sub)step's duration for each body in the bundle.
        if (!materials.AnyCustomIntegration)
        {
            velocity.Linear += _gravity * dt;
            return;
        }

        // Per lane (unused lanes have index -1): the body's gravity factor and its damping factors for this dt.
        var count = System.Numerics.Vector<float>.Count;
        Span<float> gravity = stackalloc float[count];
        Span<float> linear = stackalloc float[count];
        Span<float> angular = stackalloc float[count];
        var activeSet = _bodies.ActiveSet;
        for (var lane = 0; lane < count; lane++)
        {
            var index = bodyIndices[lane];
            if (index < 0 || index >= activeSet.Count)
            {
                gravity[lane] = 0f;
                linear[lane] = angular[lane] = 1f;
                continue;
            }

            ref readonly var material = ref materials.OfBody(activeSet.IndexToHandle[index]);
            var step = dt[lane];
            gravity[lane] = material.Gravity ? material.GravityScale : 0f;
            linear[lane] = material.LinearDamping > 0f ? MathF.Exp(-material.LinearDamping * step) : 1f;
            angular[lane] = material.AngularDamping > 0f ? MathF.Exp(-material.AngularDamping * step) : 1f;
        }

        var linearFactor = new System.Numerics.Vector<float>(linear);
        var angularFactor = new System.Numerics.Vector<float>(angular);
        velocity.Linear = (velocity.Linear + _gravity * (dt * new System.Numerics.Vector<float>(gravity))) * linearFactor;
        velocity.Angular *= angularFactor;
    }
}
