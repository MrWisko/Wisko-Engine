namespace Wisko.Engine.World;

/// <summary>
/// Root container for all Parts in the world.
/// </summary>
public sealed class Workspace
{
    /// <summary>The ground the editor host draws for every world (an environment object, not a Part).</summary>
    public const string BaseplateName = "Baseplate";

    private readonly List<Part> _parts = new();

    // The host's environment objects (the baseplate) this world has deleted; saved with the scene. Empty: all present.
    private readonly SortedSet<string> _removedEnvironment = new(StringComparer.Ordinal);

    public IReadOnlyList<Part> Parts => _parts;

    /// <summary>Moves on whenever Parts are added or removed (not when a Part itself changes), or an environment object is.</summary>
    public long Version { get; private set; }

    /// <summary>The environment objects this world deleted, by name (sorted).</summary>
    public IReadOnlyCollection<string> RemovedEnvironment => _removedEnvironment;

    /// <summary>
    /// Whether the world has its baseplate: when deleted, nothing draws it, nothing collides with it in Play, the editor
    /// stops keeping Parts above it and drags no longer rest on it.
    /// </summary>
    public bool HasBaseplate => IsEnvironmentPresent(BaseplateName);

    public bool IsEnvironmentPresent(string name) => !_removedEnvironment.Contains(name);

    /// <summary>Deletes (false) or restores (true) one of the host's environment objects; true when that changed anything.</summary>
    public bool SetEnvironmentPresent(string name, bool present)
    {
        var changed = present ? _removedEnvironment.Remove(name) : _removedEnvironment.Add(name);
        if (changed)
        {
            Version++;
        }

        return changed;
    }

    /// <summary>Entering a world: exactly these environment objects are deleted (none: all present).</summary>
    public void SetRemovedEnvironment(IEnumerable<string> names)
    {
        _removedEnvironment.Clear();
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _removedEnvironment.Add(name);
            }
        }

        Version++;
    }

    public void AddPart(Part part)
    {
        _parts.Add(part);
        Version++;
    }

    public bool RemovePart(Part part)
    {
        if (!_parts.Remove(part))
        {
            return false;
        }

        Version++;
        return true;
    }

    public bool RemovePart(string name)
    {
        var removed = false;
        for (var i = _parts.Count - 1; i >= 0; i--)
        {
            if (_parts[i].Name == name)
            {
                _parts.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            Version++;
        }

        return removed;
    }

    public void Clear()
    {
        _parts.Clear();
        Version++;
    }
}
