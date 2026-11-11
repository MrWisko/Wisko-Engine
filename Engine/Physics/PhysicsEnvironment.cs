using Wisko.Engine.Math;

namespace Wisko.Engine.Physics;

/// <summary>A fixed box that is part of the world but not a scene Part (e.g. the studio baseplate).</summary>
public readonly record struct PhysicsEnvironmentBox(string Name, Vector3 Center, Vector3 Size);

/// <summary>
/// The static colliders a physics world gets besides the scene's Parts: environment geometry the host draws itself,
/// such as the baseplate. The host describes it (it owns the baseplate's size and place); the Engine never assumes one.
/// </summary>
public sealed class PhysicsEnvironment
{
    private readonly List<PhysicsEnvironmentBox> _boxes = [];

    public IReadOnlyList<PhysicsEnvironmentBox> Boxes => _boxes;

    /// <summary>Adds an axis-aligned box; false (nothing added) when its centre or size is not finite or not positive.</summary>
    public bool AddBox(string name, Vector3 center, Vector3 size)
    {
        if (!PhysicsMath.IsFinite(center) || !PhysicsMath.IsFinite(size) || size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
        {
            Console.WriteLine($"[Physics] Environment box '{name}' ignored: invalid centre or size");
            return false;
        }

        _boxes.Add(new PhysicsEnvironmentBox(name, center, size));
        return true;
    }

    /// <summary>The same environment without the boxes a world deleted (its own copy; this one is unchanged).</summary>
    public PhysicsEnvironment Without(IReadOnlyCollection<string> removed)
    {
        var copy = new PhysicsEnvironment();
        foreach (var box in _boxes)
        {
            if (!removed.Contains(box.Name))
            {
                copy._boxes.Add(box);
            }
        }

        return copy;
    }
}
