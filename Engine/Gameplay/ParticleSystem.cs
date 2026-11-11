using Wisko.Engine.Math;

namespace Wisko.Engine.Gameplay;

/// <summary>
/// One particle emitter scripts made (Lua: Particles.CreateEmitter): it spawns <see cref="Rate"/> particles per second
/// (and <see cref="Burst"/> ones on demand) at random points of a box (<see cref="Position"/>, <see cref="Area"/>; with
/// <see cref="FollowCamera"/> the box is centred on the camera, offset by Position), each moving with
/// <see cref="Velocity"/> plus up to <see cref="Spread"/> of that speed in a random direction, pulled by
/// <see cref="Gravity"/> times the world's gravity, for <see cref="Lifetime"/> seconds (±10 %). Particles are drawn as
/// flat camera-facing quads <see cref="Size"/> wide; with a <see cref="Length"/> they are streaks that long along
/// their motion (rain). Particles never collide; they keep their world position once spawned. The random sequence is
/// fixed per emitter (deterministic for a given frame sequence).
/// </summary>
public sealed class ParticleEmitter
{
    public const int MaxParticlesPerEmitter = 20_000;
    public const float MaxRate = 100_000f;
    public const float MaxLifetime = 60f;

    private Vector3[] _positions = [];
    private Vector3[] _velocities = [];
    private float[] _ages = [];
    private float[] _lifetimes = [];
    private int _count;
    private double _carry;
    private uint _random;
    private int _maxParticles = 2000;

    internal ParticleEmitter(int id)
    {
        Id = id;
        _random = 0x9E3779B9u ^ (uint)id * 0x85EBCA6Bu;
    }

    public int Id { get; }

    public Vector3 Position { get; set; }

    public Vector3 Area { get; set; }

    public float Rate { get; set; } = 50f;

    public float Lifetime { get; set; } = 2f;

    public Vector3 Velocity { get; set; } = new(0f, 2f, 0f);

    public float Spread { get; set; } = 0.2f;

    public float Gravity { get; set; }

    /// <summary>Colour, 0..1 per channel.</summary>
    public Vector3 Color { get; set; } = new(1f, 1f, 1f);

    public float Size { get; set; } = 0.2f;

    public float Length { get; set; }

    public bool FollowCamera { get; set; }

    public bool Enabled { get; set; } = true;

    public bool Destroyed { get; internal set; }

    public int MaxParticles
    {
        get => _maxParticles;
        set => _maxParticles = global::System.Math.Clamp(value, 1, MaxParticlesPerEmitter);
    }

    /// <summary>Particles alive now.</summary>
    public int Count => _count;

    internal ReadOnlySpan<Vector3> Positions => _positions.AsSpan(0, _count);

    internal ReadOnlySpan<Vector3> Velocities => _velocities.AsSpan(0, _count);

    /// <summary>Spawns this many particles now (within <see cref="MaxParticles"/>); returns how many were made.</summary>
    public int Burst(int count, Vector3 camera, int budget = int.MaxValue)
    {
        var made = 0;
        var room = global::System.Math.Min(global::System.Math.Min(count, MaxParticles - _count), budget);
        for (var i = 0; i < room; i++)
        {
            Spawn(camera);
            made++;
        }

        return made;
    }

    public void Clear()
    {
        _count = 0;
        _carry = 0d;
    }

    /// <summary>One simulation step: ageing (expired particles go), motion, then new particles at <see cref="Rate"/>.</summary>
    internal void Step(float seconds, Vector3 gravity, Vector3 camera, int budget)
    {
        if (seconds <= 0f)
        {
            return;
        }

        var pull = gravity * Gravity * seconds;
        var i = 0;
        while (i < _count)
        {
            _ages[i] += seconds;
            if (_ages[i] >= _lifetimes[i])
            {
                // The last particle takes its place (order does not matter).
                _count--;
                _positions[i] = _positions[_count];
                _velocities[i] = _velocities[_count];
                _ages[i] = _ages[_count];
                _lifetimes[i] = _lifetimes[_count];
                continue;
            }

            _velocities[i] += pull;
            _positions[i] += _velocities[i] * seconds;
            i++;
        }

        if (!Enabled || Rate <= 0f)
        {
            _carry = 0d;
            return;
        }

        _carry += Rate * (double)seconds;
        var due = (int)global::System.Math.Floor(_carry);
        _carry -= due;
        Burst(due, camera, budget);
    }

    private void Spawn(Vector3 camera)
    {
        if (_count == _positions.Length)
        {
            var size = global::System.Math.Min(global::System.Math.Max(64, _positions.Length * 2), MaxParticlesPerEmitter);
            Array.Resize(ref _positions, size);
            Array.Resize(ref _velocities, size);
            Array.Resize(ref _ages, size);
            Array.Resize(ref _lifetimes, size);
        }

        var center = FollowCamera ? camera + Position : Position;
        var offset = new Vector3((Next() - 0.5f) * Area.X, (Next() - 0.5f) * Area.Y, (Next() - 0.5f) * Area.Z);
        var speed = Velocity.Length;
        var jitter = RandomDirection() * (speed * Spread * Next());
        _positions[_count] = center + offset;
        _velocities[_count] = Velocity + jitter;
        _ages[_count] = 0f;
        _lifetimes[_count] = Lifetime * (0.9f + 0.2f * Next());
        _count++;
    }

    private Vector3 RandomDirection()
    {
        // Uniform on the sphere: z uniform in -1..1, angle uniform.
        var z = Next() * 2f - 1f;
        var angle = Next() * MathF.Tau;
        var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        var (s, c) = MathF.SinCos(angle);
        return new Vector3(r * c, r * s, z);
    }

    // xorshift32: a fixed sequence per emitter, 0 <= value < 1.
    private float Next()
    {
        var x = _random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _random = x;
        return (x >> 8) * (1f / 16777216f);
    }
}

/// <summary>
/// The Play session's particle emitters (Lua: Particles): stepped once per frame after physics by the simulation's
/// (scaled) time and drawn by the renderer while playing. At most <see cref="MaxEmitters"/> emitters and
/// <see cref="MaxTotalParticles"/> particles in all (new particles wait while the total is at the cap).
/// </summary>
public sealed class ParticleSystem
{
    public const int MaxEmitters = 64;
    public const int MaxTotalParticles = 60_000;

    private readonly List<ParticleEmitter> _emitters = [];
    private int _nextId;

    public IReadOnlyList<ParticleEmitter> Emitters => _emitters;

    public int TotalCount
    {
        get
        {
            var total = 0;
            foreach (var emitter in _emitters)
            {
                total += emitter.Count;
            }

            return total;
        }
    }

    public ParticleEmitter? Create()
    {
        if (_emitters.Count >= MaxEmitters)
        {
            return null;
        }

        var emitter = new ParticleEmitter(++_nextId);
        _emitters.Add(emitter);
        return emitter;
    }

    public ParticleEmitter? Find(int id)
    {
        foreach (var emitter in _emitters)
        {
            if (emitter.Id == id)
            {
                return emitter;
            }
        }

        return null;
    }

    public void Destroy(ParticleEmitter emitter)
    {
        emitter.Destroyed = true;
        emitter.Clear();
        _emitters.Remove(emitter);
    }

    /// <summary>Particles one more <see cref="ParticleEmitter.Burst"/> may add before the session's cap.</summary>
    public int Room => global::System.Math.Max(0, MaxTotalParticles - TotalCount);

    internal void Step(float seconds, Vector3 gravity, Vector3 camera)
    {
        foreach (var emitter in _emitters)
        {
            emitter.Step(seconds, gravity, camera, Room);
        }
    }

    internal void Clear()
    {
        foreach (var emitter in _emitters)
        {
            emitter.Destroyed = true;
            emitter.Clear();
        }

        _emitters.Clear();
    }
}
