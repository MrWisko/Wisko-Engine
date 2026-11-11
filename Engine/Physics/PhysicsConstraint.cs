using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Wisko.Engine.World;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Physics;

/// <summary>What a <see cref="PhysicsConstraint"/> holds together.</summary>
public enum PhysicsConstraintKind
{
    /// <summary>Two bodies keep their relative position and orientation (a physical, breakable-by-script glue).</summary>
    Weld,

    /// <summary>Two bodies share a point and turn only around one axis (doors, wheels, windmills), with an optional motor and limits.</summary>
    Hinge,

    /// <summary>Two bodies share a point and turn freely around it (chains, pendulums, trailers).</summary>
    BallSocket
}

/// <summary>
/// A physics joint between two Parts, or between a Part and a fixed point in the world (<see cref="PartB"/> null): the
/// solver keeps it, so forces, collisions and motors act through it, unlike <c>SetParent</c>, which only moves a Part
/// along. Made by <see cref="PhysicsWorld.CreateConstraint"/>; it lives as long as both Parts stay in the world (or until
/// <see cref="Destroy"/>), and goes with the world. An anchored Part in a joint becomes kinematic (it may be moved by a
/// script and the joint follows); at least one side must be unanchored for the joint to do anything.
/// <para>
/// A Part's body is rebuilt when its kind changes (Anchored toggled while playing): the joint is taken off the old body
/// first and put back on the new one, in the same local frames, so it carries on. While <see cref="Enabled"/> is false
/// it is off the solver entirely (the Parts move freely) but keeps its frames. Parts in a joint do not collide with each
/// other unless <see cref="CollideConnected"/> is set.
/// </para>
/// </summary>
public sealed class PhysicsConstraint
{
    private static readonly SpringSettings Stiff = new(30f, 1f);

    private readonly PhysicsWorld _world;

    // Body-local frames (relative to each body's centre of mass and orientation) captured when it was made.
    private readonly NVector3 _localOffsetA;
    private readonly NVector3 _localOffsetB;
    private readonly NVector3 _localAxisA;
    private readonly NVector3 _localAxisB;
    private readonly NVector3 _localReferenceA;
    private readonly NVector3 _localReferenceB;
    private readonly NQuaternion _localOrientation;

    // A fixed point in the world for a joint without PartB: a shapeless kinematic body where the joint was made.
    private readonly RigidPose _anchorPose;
    private BodyHandle? _anchor;

    private ConstraintHandle? _joint;
    private ConstraintHandle? _motor;
    private ConstraintHandle? _limit;
    private bool _enabled = true;
    private bool _collideConnected;
    private bool _motorEnabled;
    private float _motorSpeed;
    private float _motorMaxTorque = 1000f;
    private bool _limitsEnabled;
    private float _lowerAngle = -45f;
    private float _upperAngle = 45f;

    internal PhysicsConstraint(PhysicsWorld world, PhysicsConstraintKind kind, Part partA, Part? partB, int id,
        RigidPose poseA, RigidPose poseB, NVector3 worldPoint, NVector3 worldAxis)
    {
        _world = world;
        Kind = kind;
        PartA = partA;
        PartB = partB;
        Id = id;
        _anchorPose = poseB;

        var inverseA = NQuaternion.Conjugate(poseA.Orientation);
        var inverseB = NQuaternion.Conjugate(poseB.Orientation);
        _localOffsetA = NVector3.Transform(worldPoint - poseA.Position, inverseA);
        _localOffsetB = NVector3.Transform(worldPoint - poseB.Position, inverseB);
        var axis = NVector3.Normalize(worldAxis);
        _localAxisA = NVector3.Normalize(NVector3.Transform(axis, inverseA));
        _localAxisB = NVector3.Normalize(NVector3.Transform(axis, inverseB));
        var reference = Perpendicular(axis);
        _localReferenceA = NVector3.Normalize(NVector3.Transform(reference, inverseA));
        _localReferenceB = NVector3.Normalize(NVector3.Transform(reference, inverseB));

        // Weld: B's pose in A's frame (offset from A's centre to B's, and B's orientation relative to A's).
        _localOrientation = NQuaternion.Normalize(inverseA * poseB.Orientation);
        if (kind == PhysicsConstraintKind.Weld)
        {
            _localOffsetA = NVector3.Transform(poseB.Position - poseA.Position, inverseA);
        }
    }

    public PhysicsConstraintKind Kind { get; }

    public Part PartA { get; }

    /// <summary>The other Part, or null when the joint holds <see cref="PartA"/> to a fixed point in the world.</summary>
    public Part? PartB { get; }

    /// <summary>Unique within its world (from 1).</summary>
    public int Id { get; }

    /// <summary>Destroyed (by a script, or because one of its Parts left the world): it never comes back.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>On the solver now: enabled, not destroyed, and both sides have bodies that can hold a joint.</summary>
    public bool IsActive => _joint is not null;

    /// <summary>
    /// False takes the joint off the solver (the Parts move freely; it keeps its frames); true puts it back. Ignored once
    /// destroyed.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (IsDestroyed || _enabled == value)
            {
                return;
            }

            _enabled = value;
            _world.RefreshConstraint(this);
        }
    }

    /// <summary>Whether the two Parts may still collide with each other (default false: a wheel does not rub its car).</summary>
    public bool CollideConnected
    {
        get => _collideConnected;
        set
        {
            if (IsDestroyed || _collideConnected == value)
            {
                return;
            }

            _collideConnected = value;
            _world.UpdateConstraintCollision(this);
        }
    }

    /// <summary>Hinge only: drives the hinge at <see cref="MotorSpeed"/> with at most <see cref="MotorMaxTorque"/>.</summary>
    public bool MotorEnabled
    {
        get => _motorEnabled;
        set => Change(ref _motorEnabled, value);
    }

    /// <summary>Hinge motor target speed in degrees per second (positive turns PartB counter-clockwise around the axis seen from its tip).</summary>
    public float MotorSpeed
    {
        get => _motorSpeed;
        set => Change(ref _motorSpeed, value);
    }

    /// <summary>The most torque the motor may use (mass x units²/s²); a weak motor can be held back by a load.</summary>
    public float MotorMaxTorque
    {
        get => _motorMaxTorque;
        set => Change(ref _motorMaxTorque, value);
    }

    /// <summary>Hinge only: keeps <see cref="Angle"/> within <see cref="LowerAngle"/>..<see cref="UpperAngle"/>.</summary>
    public bool LimitsEnabled
    {
        get => _limitsEnabled;
        set => Change(ref _limitsEnabled, value);
    }

    /// <summary>Degrees, from the pose the hinge was made in (-180 to 180).</summary>
    public float LowerAngle
    {
        get => _lowerAngle;
        set => Change(ref _lowerAngle, value);
    }

    public float UpperAngle
    {
        get => _upperAngle;
        set => Change(ref _upperAngle, value);
    }

    /// <summary>Destroys the joint for good (the Parts stay where they are and move freely). Safe to call twice.</summary>
    public void Destroy() => _world.DestroyConstraint(this);

    internal bool UsesAnchor => PartB is null;

    internal void MarkDestroyed() => IsDestroyed = true;

    // Off the solver: every handle this joint added goes (before either body may be removed).
    internal void Detach(Simulation simulation)
    {
        foreach (var handle in new[] { _limit, _motor, _joint })
        {
            if (handle is { } h && simulation.Solver.ConstraintExists(h))
            {
                simulation.Solver.Remove(h);
            }
        }

        _joint = _motor = _limit = null;
        if (_anchor is { } anchor)
        {
            simulation.Bodies.Remove(anchor);
            _anchor = null;
        }
    }

    // On the solver between these bodies (bodyB null: a fixed anchor point is made for it). False when it cannot be.
    internal bool Attach(Simulation simulation, BodyHandle bodyA, BodyHandle? bodyB)
    {
        if (IsDestroyed || !_enabled)
        {
            return false;
        }

        BodyHandle b;
        if (bodyB is { } other)
        {
            b = other;
        }
        else
        {
            _anchor = simulation.Bodies.Add(BodyDescription.CreateKinematic(_anchorPose, new CollidableDescription(default(TypedIndex)),
                new BodyActivityDescription(-1f)));
            b = _anchor.Value;
        }

        var a = simulation.Bodies.GetBodyReference(bodyA);
        var bReference = simulation.Bodies.GetBodyReference(b);
        a.Awake = true;
        bReference.Awake = true;
        switch (Kind)
        {
            case PhysicsConstraintKind.Weld:
                _joint = simulation.Solver.Add(bodyA, b, new Weld { LocalOffset = _localOffsetA, LocalOrientation = _localOrientation, SpringSettings = Stiff });
                break;
            case PhysicsConstraintKind.BallSocket:
                _joint = simulation.Solver.Add(bodyA, b, new BallSocket { LocalOffsetA = _localOffsetA, LocalOffsetB = _localOffsetB, SpringSettings = Stiff });
                break;
            default:
                _joint = simulation.Solver.Add(bodyA, b, new Hinge
                {
                    LocalOffsetA = _localOffsetA,
                    LocalHingeAxisA = _localAxisA,
                    LocalOffsetB = _localOffsetB,
                    LocalHingeAxisB = _localAxisB,
                    SpringSettings = Stiff
                });
                if (_motorEnabled)
                {
                    _motor = simulation.Solver.Add(bodyA, b, Motor());
                }

                if (_limitsEnabled)
                {
                    _limit = simulation.Solver.Add(bodyA, b, Limit());
                }

                break;
        }

        return true;
    }

    // The hinge's angle now: PartB's reference direction against PartA's, around PartA's axis (degrees, -180..180).
    internal float AngleBetween(RigidPose poseA, RigidPose poseB)
    {
        var axis = NVector3.Transform(_localAxisA, poseA.Orientation);
        var referenceA = NVector3.Transform(_localReferenceA, poseA.Orientation);
        var referenceB = NVector3.Transform(_localReferenceB, poseB.Orientation);
        referenceB -= axis * NVector3.Dot(referenceB, axis);
        if (referenceB.LengthSquared() < 1e-12f)
        {
            return 0f;
        }

        var sine = NVector3.Dot(NVector3.Cross(referenceA, referenceB), axis);
        var cosine = NVector3.Dot(referenceA, NVector3.Normalize(referenceB));
        return MathF.Atan2(sine, cosine) * 180f / MathF.PI;
    }

    internal RigidPose AnchorPose => _anchorPose;

    private AngularAxisMotor Motor() => new()
    {
        LocalAxisA = _localAxisA,
        TargetVelocity = _motorSpeed * MathF.PI / 180f,
        Settings = new MotorSettings(MathF.Max(0f, _motorMaxTorque), 1e-4f)
    };

    // A twist limit around the hinge axis: each body's basis has Z along the axis and X along its reference direction.
    private TwistLimit Limit() => new()
    {
        LocalBasisA = Basis(_localAxisA, _localReferenceA),
        LocalBasisB = Basis(_localAxisB, _localReferenceB),
        MinimumAngle = MathF.Min(_lowerAngle, _upperAngle) * MathF.PI / 180f,
        MaximumAngle = MathF.Max(_lowerAngle, _upperAngle) * MathF.PI / 180f,
        SpringSettings = Stiff
    };

    private void Change<T>(ref T field, T value)
    {
        if (IsDestroyed || EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        if (Kind == PhysicsConstraintKind.Hinge)
        {
            _world.RefreshConstraint(this);
        }
    }

    private static NQuaternion Basis(NVector3 z, NVector3 x)
    {
        var y = NVector3.Normalize(NVector3.Cross(z, x));
        x = NVector3.Cross(y, z);
        var matrix = new System.Numerics.Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
        return NQuaternion.Normalize(NQuaternion.CreateFromRotationMatrix(matrix));
    }

    private static NVector3 Perpendicular(NVector3 axis)
    {
        var helper = MathF.Abs(axis.Y) < 0.9f ? NVector3.UnitY : NVector3.UnitX;
        return NVector3.Normalize(NVector3.Cross(axis, helper));
    }
}
